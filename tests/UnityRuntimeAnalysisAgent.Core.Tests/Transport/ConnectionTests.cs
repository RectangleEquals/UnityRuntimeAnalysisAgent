using System.Text;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Envelopes;
using UnityLudometry.Protocol.Framing;
using UnityRuntimeAnalysisAgent.Core.Hosting;
using UnityRuntimeAnalysisAgent.Core.Tests.Support;
using UnityRuntimeAnalysisAgent.Core.Transport;

namespace UnityRuntimeAnalysisAgent.Core.Tests.Transport;

public sealed class ConnectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (Connection Connection, WirePeer Peer, List<RequestEnvelope> Received, TaskCompletionSource<ConnectionClosedEventArgs> Closed) Open(int maxFrameBytes = 1 << 20)
    {
        var (agentSide, clientSide) = MemoryDuplex.CreatePair();
        var received = new List<RequestEnvelope>();
        var closed = new TaskCompletionSource<ConnectionClosedEventArgs>();
        var connection = new Connection(agentSide, "memory", maxFrameBytes, new TestLogger(), (c, r) =>
        {
            lock (received)
            {
                received.Add(r);
            }

            c.Send(ResponseEnvelope.Success(r.Id, null, r.Context));
        });
        connection.Closed += (_, e) => closed.TrySetResult(e);
        connection.Start();
        return (connection, new WirePeer(clientSide), received, closed);
    }

    [Fact]
    public void Requests_are_handled_and_answered_even_when_frames_arrive_in_pieces()
    {
        var (connection, peer, received, _) = Open();
        var request = new RequestEnvelope { Id = "a", Method = "ping" }.ToUtf8Bytes();
        var frame = new byte[4 + request.Length];
        BitConverter.GetBytes(request.Length).CopyTo(frame, 0);
        request.CopyTo(frame, 4);
        foreach (var b in frame)
        {
            peer.Stream.Write(new[] { b }, 0, 1); // byte by byte
        }

        var response = Assert.IsType<ResponseEnvelope>(peer.Receive());
        Assert.Equal("a", response.Id);
        Assert.Single(received);
        connection.Dispose();
    }

    [Fact]
    public async Task An_oversized_frame_closes_the_connection()
    {
        var (connection, peer, _, closed) = Open(maxFrameBytes: 1024);
        new FrameWriter(peer.Stream).WriteFrame(new byte[2048]);
        var e = await closed.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(ErrorCodes.FrameTooLarge, e.Reason);
        Assert.True(connection.IsClosed);
    }

    [Fact]
    public async Task A_truncated_frame_closes_the_connection()
    {
        var (_, peer, _, closed) = Open();
        peer.Stream.Write(new byte[] { 10, 0, 0, 0, (byte)'{' }, 0, 5);
        peer.Dispose();
        var e = await closed.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(ErrorCodes.InvalidFrame, e.Reason);
    }

    [Fact]
    public void Malformed_json_is_dropped_and_the_connection_stays_usable()
    {
        var (connection, peer, received, _) = Open();
        peer.WriteRaw(Encoding.UTF8.GetBytes("{not json"));
        var response = peer.Call("ping");
        Assert.False(response.IsError);
        Assert.Single(received);
        Assert.False(connection.IsClosed);
        connection.Dispose();
    }

    [Fact]
    public void An_invalid_envelope_with_an_id_is_answered_with_INVALID_FRAME()
    {
        var (connection, peer, received, _) = Open();
        peer.WriteRaw(Encoding.UTF8.GetBytes("{\"v\":0,\"id\":\"x\",\"kind\":\"request\"}")); // no method
        var response = Assert.IsType<ResponseEnvelope>(peer.Receive());
        Assert.Equal("x", response.Id);
        Assert.Equal(ErrorCodes.InvalidFrame, response.Error!.Code);
        Assert.Empty(received);
        connection.Dispose();
    }

    [Fact]
    public async Task SendAndClose_delivers_the_response_before_closing()
    {
        var (connection, peer, _, closed) = Open();
        connection.SendAndClose(ResponseEnvelope.Failure("z", new ProtocolError { Code = ErrorCodes.BadToken, Message = "no" }));
        var response = Assert.IsType<ResponseEnvelope>(peer.Receive());
        Assert.Equal(ErrorCodes.BadToken, response.Error!.Code);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Null(peer.Receive());
    }

    [Fact]
    public void Oversized_responses_become_errors_and_event_sequence_numbers_increase()
    {
        var (connection, peer, _, _) = Open(maxFrameBytes: 1024);
        var big = new UnityLudometry.Protocol.Json.JsonString(new string('x', 4096));
        connection.Send(ResponseEnvelope.Success("big", big));
        var response = Assert.IsType<ResponseEnvelope>(peer.Receive());
        Assert.Equal(ErrorCodes.Internal, response.Error!.Code);
        Assert.Equal(1, connection.NextEventSeq());
        Assert.Equal(2, connection.NextEventSeq());
        connection.Dispose();
    }

    [Fact]
    public void Subscriptions_are_tracked_per_connection()
    {
        var (connection, _, _, _) = Open();
        connection.Subscribe(["log", "agent.warning"]);
        Assert.True(connection.IsSubscribed("log"));
        Assert.Equal(["log"], connection.Unsubscribe(["log", "exception"]));
        Assert.Equal(["agent.warning"], connection.Unsubscribe(null));
        Assert.False(connection.IsSubscribed("agent.warning"));
        connection.Dispose();
    }
}
