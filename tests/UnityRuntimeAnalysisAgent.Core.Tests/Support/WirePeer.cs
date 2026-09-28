using UnityLudometry.Protocol.Envelopes;
using UnityLudometry.Protocol.Framing;
using UnityLudometry.Protocol.Json;

namespace UnityRuntimeAnalysisAgent.Core.Tests.Support;

/// <summary>The client side of an in-memory connection: writes frames and reads envelopes (with a timeout). Frames are read
/// on the peer's own thread, so waiting for one never holds a thread-pool thread the agent needs.</summary>
public sealed class WirePeer : IDisposable
{
    private readonly Stream _stream;
    private readonly FrameWriter _writer;
    private readonly System.Collections.Concurrent.BlockingCollection<byte[]?> _frames = new();
    private int _id;

    public WirePeer(Stream stream)
    {
        _stream = stream;
        _writer = new FrameWriter(stream);
        var reader = new FrameReader(stream);
        new Thread(() =>
        {
            try
            {
                while (reader.ReadFrame() is { } frame)
                {
                    _frames.Add(frame);
                }
            }
            catch (Exception)
            {
                // the connection closed or broke; readers see it as closed
            }

            _frames.Add(null); // the agent closed the connection
        })
        { IsBackground = true, Name = "WirePeer reader" }.Start();
    }

    public Stream Stream => _stream;

    public void WriteRaw(byte[] payload) => _writer.WriteFrame(payload);

    public string Send(string method, string? paramsJson = null, string? contextJson = null, long? timeoutMs = null)
    {
        var id = "t-" + Interlocked.Increment(ref _id);
        var request = new RequestEnvelope
        {
            Id = id,
            Method = method,
            Params = paramsJson is null ? null : (JsonObject)JsonValue.Parse(paramsJson),
            Context = contextJson is null ? null : (JsonObject)JsonValue.Parse(contextJson),
            TimeoutMs = timeoutMs,
        };
        _writer.WriteFrame(request.ToUtf8Bytes());
        return id;
    }

    /// <summary>The next envelope, or null when the agent closed the connection.</summary>
    public Envelope? Receive(int timeoutMs = 5000)
    {
        if (!_frames.TryTake(out var frame, timeoutMs))
        {
            throw new TimeoutException("No frame from the agent.");
        }

        if (frame is null)
        {
            _frames.Add(null); // stays closed for later reads
            return null;
        }

        return Envelope.Parse(frame);
    }

    /// <summary>Events received while waiting for responses (in arrival order).</summary>
    public List<EventEnvelope> Events { get; } = new();

    /// <summary>Sends a request and returns its response; events that arrive first are kept in <see cref="Events"/>.</summary>
    public ResponseEnvelope Call(string method, string? paramsJson = null, string? contextJson = null, int timeoutMs = 5000)
    {
        var id = Send(method, paramsJson, contextJson);
        return AwaitResponse(id, timeoutMs);
    }

    private readonly Dictionary<string, ResponseEnvelope> _early = new(StringComparer.Ordinal);

    /// <summary>The response to request <paramref name="id"/>. Responses may arrive in any order (they're matched by id):
    /// others that arrive first are kept for their own callers, and events in <see cref="Events"/>.</summary>
    public ResponseEnvelope AwaitResponse(string id, int timeoutMs = 5000)
    {
        if (_early.Remove(id, out var early))
        {
            return early;
        }

        while (true)
        {
            var envelope = Receive(timeoutMs) ?? throw new InvalidOperationException("The agent closed the connection.");
            if (envelope is EventEnvelope ev)
            {
                Events.Add(ev);
                continue;
            }

            var response = Assert.IsType<ResponseEnvelope>(envelope);
            if (response.Id == id)
            {
                return response;
            }

            _early[response.Id] = response;
        }
    }

    /// <summary>Waits for an event of a kind (checking those already received first).</summary>
    public EventEnvelope AwaitEvent(string kind, int timeoutMs = 5000)
    {
        var found = Events.FirstOrDefault(e => e.Method == kind);
        if (found is not null)
        {
            Events.Remove(found);
            return found;
        }

        while (true)
        {
            var envelope = Receive(timeoutMs) ?? throw new InvalidOperationException("The agent closed the connection.");
            if (envelope is EventEnvelope ev && ev.Method == kind)
            {
                return ev;
            }

            if (envelope is EventEnvelope other)
            {
                Events.Add(other);
            }
        }
    }

    public void Dispose() => _stream.Dispose();
}
