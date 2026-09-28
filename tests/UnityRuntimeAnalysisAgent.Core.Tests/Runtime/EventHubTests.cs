using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Envelopes;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Hosting;
using UnityRuntimeAnalysisAgent.Core.Tests.Support;

namespace UnityRuntimeAnalysisAgent.Core.Tests.Runtime;

public sealed class EventHubTests : IDisposable
{
    private readonly TestHost _test = new(configure: c => c.Set(AgentConfig.MaxEventQueueBytesKey, "65536"));

    private static JsonObject LogItem(int seq, string level = "info", string message = "m") =>
        (JsonObject)JsonValue.Parse($"{{\"seq\":{seq},\"realtimeMs\":0,\"frame\":null,\"source\":\"unity\",\"channel\":\"game\",\"level\":\"{level}\",\"message\":\"{message}\"}}");

    private static LogEventParams Batch(EventEnvelope e) => LogEventParams.Read(e.Params, "params");

    [Fact]
    public void Batched_kinds_go_out_by_size_or_after_the_throttle_interval()
    {
        var peer = _test.Connect();
        peer.Call(Methods.EventsSubscribe, "{\"kinds\":[\"log\"],\"throttleMs\":60000,\"maxBatch\":3}");
        for (var i = 1; i <= 7; i++)
        {
            _test.Host.Events.PublishItem(EventKinds.Log, LogItem(i));
        }

        Assert.Equal(new long[] { 1, 2, 3 }, Batch(peer.AwaitEvent(EventKinds.Log)).Items.Select(x => x.Seq));
        Assert.Equal(new long[] { 4, 5, 6 }, Batch(peer.AwaitEvent(EventKinds.Log)).Items.Select(x => x.Seq));
        _test.Host.Events.Flush(); // the 60 s throttle hasn't passed
        _test.Host.Events.Flush(all: true); // unsubscribe/shutdown flushes everything
        var last = Batch(peer.AwaitEvent(EventKinds.Log));
        Assert.Equal(new long[] { 7 }, last.Items.Select(x => x.Seq));
        Assert.Equal(0, last.Dropped);
    }

    [Fact]
    public void Subscribers_filters_apply_per_kind()
    {
        _test.Host.Events.RegisterFilter(EventKinds.Log, (filter, item) =>
            filter["minLevel"] is not JsonString min || (((JsonObject)item)["level"] as JsonString)?.Value == min.Value);
        var peer = _test.Connect();
        peer.Call(Methods.EventsSubscribe, "{\"kinds\":[\"log\"],\"filter\":{\"log\":{\"minLevel\":\"warning\"}}}");
        _test.Host.Events.PublishItem(EventKinds.Log, LogItem(1, "info"));
        _test.Host.Events.PublishItem(EventKinds.Log, LogItem(2, "warning"));
        _test.Host.Events.Flush(all: true);
        Assert.Equal(new long[] { 2 }, Batch(peer.AwaitEvent(EventKinds.Log)).Items.Select(x => x.Seq));
    }

    [Fact]
    public void A_slow_client_loses_the_oldest_events_never_responses_and_can_tell()
    {
        var (agentSide, clientSide) = MemoryDuplex.CreatePair();
        var gated = new GatedStream(agentSide);
        var agentConnection = _test.Host.AcceptConnection(gated, "tcp")!;
        using var peer = new WirePeer(clientSide);
        peer.Call(Methods.Hello, $"{{\"token\":\"{_test.Host.Token.Value}\",\"client\":{{\"name\":\"slow\",\"version\":\"1\"}},\"protocol\":{{\"major\":{ProtocolVersion.Major},\"minor\":{ProtocolVersion.Minor}}}}}");
        peer.Call(Methods.EventsSubscribe, "{\"kinds\":[\"agent.warning\",\"log\"],\"throttleMs\":0}");

        gated.Block(); // the client stops reading
        var big = new string('x', 8000);
        for (var i = 0; i < 10; i++)
        {
            _test.Host.Publish(EventKinds.AgentWarning, new AgentWarningParams { Code = $"W{i}", Message = big });
        }

        // The response is queued among the events; the events after it push the queue past its cap many times over.
        var pingId = peer.Send(Methods.Ping, "{\"echo\":\"still answered\"}");
        Thread.Sleep(100);
        for (var i = 10; i < 30; i++)
        {
            _test.Host.Publish(EventKinds.AgentWarning, new AgentWarningParams { Code = $"W{i}", Message = big });
        }

        gated.Unblock();

        Assert.Equal("still answered", PingResult.Read(peer.AwaitResponse(pingId).Result, "result").Echo);
        var warnings = new List<EventEnvelope>();
        while (warnings.Count == 0 || Code(warnings[^1]) != "W29")
        {
            warnings.Add(peer.AwaitEvent(EventKinds.AgentWarning));
        }

        Assert.True(warnings.Count < 30, "some warnings were dropped");
        var seqs = warnings.Select(w => w.Seq).ToList();
        Assert.Equal(seqs.OrderBy(s => s), seqs);
        var gaps = seqs.Zip(seqs.Skip(1), (a, b) => b - a - 1).Sum();
        Assert.True(gaps > 0, "dropped events leave a gap in seq");

        // A batched kind also reports what it lost, in its next batch.
        gated.Block();
        for (var i = 0; i < 20; i++)
        {
            _test.Host.Events.PublishItem(EventKinds.Log, LogItem(i, message: big));
            _test.Host.Events.Flush(all: true); // one batch per item, queued while the client isn't reading
        }

        var queued = agentConnection.QueuedEventBytes;
        Assert.True(queued <= 65536 + 16384, $"the queue stays near its cap ({queued} bytes queued)");
        gated.Unblock();
        _test.Host.Events.PublishItem(EventKinds.Log, LogItem(99));
        _test.Host.Events.Flush(all: true);
        var batches = new List<LogEventParams>();
        while (batches.Count == 0 || batches[^1].Items.All(item => item.Seq != 99))
        {
            batches.Add(Batch(peer.AwaitEvent(EventKinds.Log)));
        }

        // Queuing the last batch may itself evict an older one: that drop is reported by the batch after it.
        _test.Host.Events.Flush(all: true);
        try
        {
            while (true)
            {
                batches.Add(Batch(peer.AwaitEvent(EventKinds.Log, timeoutMs: 300)));
            }
        }
        catch (TimeoutException)
        {
        }

        Assert.True(batches.Count(b => b.Items.Count > 0) < 21, "some batches were dropped");
        var delivered = batches.Sum(b => b.Items.Count);
        var dropped = batches.Sum(b => b.Dropped);
        Assert.Equal(21, delivered + dropped); // every item is either delivered or counted
    }

    private static string Code(EventEnvelope e)
    {
        try
        {
            return AgentWarningParams.Read(e.Params, "params").Code;
        }
        catch (ProtocolException ex)
        {
            throw new Xunit.Sdk.XunitException($"{ex.Message} in seq {e.Seq}: {e.Params.ToString()[..Math.Min(300, e.Params.ToString().Length)]}");
        }
    }

    [Fact]
    public void Kinds_are_checked()
    {
        Assert.Throws<ArgumentException>(() => _test.Host.Publish("not.a.kind", new AgentWarningParams()));
        Assert.Throws<ArgumentException>(() => _test.Host.Publish(EventKinds.Log, new AgentWarningParams()));
        Assert.Throws<ArgumentException>(() => _test.Host.Events.PublishItem(EventKinds.AgentWarning, JsonNull.Instance));
    }

    public void Dispose() => _test.Dispose();

    /// <summary>A stream whose writes block while the gate is closed (a client that stopped reading).</summary>
    private sealed class GatedStream(Stream inner) : Stream
    {
        private readonly ManualResetEventSlim _open = new(true);

        public void Block() => _open.Reset();

        public void Unblock() => _open.Set();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override void Write(byte[] buffer, int offset, int count)
        {
            _open.Wait();
            inner.Write(buffer, offset, count);
        }

        public override void Flush() => inner.Flush();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _open.Set();
            inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
