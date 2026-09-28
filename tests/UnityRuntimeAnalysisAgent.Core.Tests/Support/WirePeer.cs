using UnityLudometry.Protocol.Envelopes;
using UnityLudometry.Protocol.Framing;
using UnityLudometry.Protocol.Json;

namespace UnityRuntimeAnalysisAgent.Core.Tests.Support;

/// <summary>The client side of an in-memory connection: writes frames and reads envelopes (with a timeout).</summary>
public sealed class WirePeer(Stream stream) : IDisposable
{
    private readonly FrameReader _reader = new(stream);
    private readonly FrameWriter _writer = new(stream);
    private int _id;

    public Stream Stream => stream;

    public void WriteRaw(byte[] payload) => _writer.WriteFrame(payload);

    public string Send(string method, string? paramsJson = null, string? contextJson = null)
    {
        var id = "t-" + Interlocked.Increment(ref _id);
        var request = new RequestEnvelope
        {
            Id = id,
            Method = method,
            Params = paramsJson is null ? null : (JsonObject)JsonValue.Parse(paramsJson),
            Context = contextJson is null ? null : (JsonObject)JsonValue.Parse(contextJson),
        };
        _writer.WriteFrame(request.ToUtf8Bytes());
        return id;
    }

    /// <summary>The next envelope, or null when the agent closed the connection.</summary>
    public Envelope? Receive(int timeoutMs = 5000)
    {
        var task = Task.Run(() => _reader.ReadFrame());
        if (!task.Wait(timeoutMs))
        {
            throw new TimeoutException("No frame from the agent.");
        }

        return task.Result is { } frame ? Envelope.Parse(frame) : null;
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

    public void Dispose() => stream.Dispose();
}
