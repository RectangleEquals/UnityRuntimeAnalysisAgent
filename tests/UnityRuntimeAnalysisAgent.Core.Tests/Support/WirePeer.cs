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

    public ResponseEnvelope Call(string method, string? paramsJson = null, string? contextJson = null)
    {
        var id = Send(method, paramsJson, contextJson);
        var envelope = Receive();
        var response = Assert.IsType<ResponseEnvelope>(envelope);
        Assert.Equal(id, response.Id);
        return response;
    }

    public void Dispose() => stream.Dispose();
}
