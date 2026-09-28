using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Envelopes;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Session;
using UnityRuntimeAnalysisAgent.Core.Tests.Support;
using UnityRuntimeAnalysisAgent.Core.Transport;

namespace UnityRuntimeAnalysisAgent.Core.Tests.Session;

public sealed class SessionHandlerTests
{
    private readonly SessionToken _token = SessionToken.Generate();

    private (SessionHandler Handler, Connection Connection, WirePeer Peer) Open()
    {
        var handler = new SessionHandler(_token, () => new AgentInfo { AgentVersion = "0.1.0", Mode = AgentMode.ReadOnly, ProcessName = "Test" },
            c =>
            {
                c.AgentVersion = "0.1.0";
                c.ApiVersion = "0.1";
                c.Protocol = ProtocolVersionInfo.Current;
                return c;
            });
        var (agentSide, clientSide) = MemoryDuplex.CreatePair();
        var connection = new Connection(agentSide, "memory", 1 << 20, new TestLogger(), handler.Handle);
        connection.Start();
        return (handler, connection, new WirePeer(clientSide));
    }

    private string Hello(string? token = null, int major = ProtocolVersion.Major, int minor = ProtocolVersion.Minor) =>
        $"{{\"token\":\"{token ?? _token.Value}\",\"client\":{{\"name\":\"test\",\"version\":\"1\"}},\"protocol\":{{\"major\":{major},\"minor\":{minor}}}}}";

    [Fact]
    public void A_valid_hello_returns_agent_info_and_authenticates()
    {
        var (_, connection, peer) = Open();
        var response = peer.Call(Methods.Hello, Hello(), "{\"task\":\"t\"}");
        Assert.False(response.IsError);
        Assert.Equal("0.1.0", AgentInfo.Read(response.Result, "result").AgentVersion);
        Assert.Equal("{\"task\":\"t\"}", response.Context!.ToString());
        Assert.True(connection.Authenticated);
        Assert.Equal("test 1", connection.ClientName);
        connection.Dispose();
    }

    [Theory]
    [InlineData("bad-token")]
    [InlineData("wrong-minor")]
    [InlineData("wrong-major")]
    [InlineData("not-hello")]
    [InlineData("invalid-hello")]
    public void Handshake_failures_are_answered_and_the_connection_closes(string scenario)
    {
        var (_, connection, peer) = Open();
        var (method, parameters, code) = scenario switch
        {
            "bad-token" => (Methods.Hello, Hello(token: new string('0', 64)), ErrorCodes.BadToken),
            "wrong-minor" => (Methods.Hello, Hello(minor: ProtocolVersion.Minor + 1), ErrorCodes.ProtocolMismatch),
            "wrong-major" => (Methods.Hello, Hello(major: ProtocolVersion.Major + 1), ErrorCodes.ProtocolMismatch),
            "not-hello" => (Methods.Ping, "{}", ErrorCodes.HandshakeRequired),
            _ => (Methods.Hello, "{\"token\":\"x\"}", ErrorCodes.InvalidParams),
        };
        var response = peer.Call(method, parameters);
        Assert.Equal(code, response.Error!.Code);
        Assert.Null(peer.Receive()); // closed
        Assert.False(connection.Authenticated);
    }

    [Fact]
    public void Session_methods_after_the_handshake()
    {
        var (handler, connection, peer) = Open();
        peer.Call(Methods.Hello, Hello());

        var ping = PingResult.Read(peer.Call(Methods.Ping, "{\"echo\":\"hi\"}").Result, "result");
        Assert.Equal("hi", ping.Echo);
        Assert.Null(ping.Frame);

        var caps = AgentCapabilities.Read(peer.Call(Methods.AgentCapabilities).Result, "result");
        Assert.Equal(
            new[] { "agent.capabilities", "agent.info", "cancel", "events.subscribe", "events.unsubscribe", "hello", "ping" },
            caps.Methods.Select(m => m.Name));
        Assert.All(caps.Methods, m => Assert.Equal(AgentMode.ReadOnly, m.MinMode));

        Assert.False(CancelResult.Read(peer.Call(Methods.Cancel, "{\"id\":\"t-9\"}").Result, "result").Cancelled);

        var sub = EventsSubscribeResult.Read(peer.Call(Methods.EventsSubscribe, "{\"kinds\":[\"log\",\"future.kind\",\"log\"]}").Result, "result");
        Assert.Equal(["log"], sub.Subscribed);
        Assert.Equal(["future.kind"], sub.Unknown);
        Assert.True(connection.IsSubscribed("log"));
        Assert.Equal(["log"], EventsUnsubscribeResult.Read(peer.Call(Methods.EventsUnsubscribe, "{}").Result, "result").Unsubscribed);

        Assert.False(peer.Call(Methods.Hello, Hello()).IsError); // a repeated hello just returns agent.info
        Assert.NotEmpty(handler.ImplementedMethods());
        connection.Dispose();
    }

    [Fact]
    public void Errors_after_the_handshake_keep_the_connection_open()
    {
        var (handler, connection, peer) = Open();
        peer.Call(Methods.Hello, Hello());
        Assert.Equal(ErrorCodes.MethodNotFound, peer.Call("no.suchMethod").Error!.Code);
        var notYet = peer.Call(Methods.ObjGet, "{}").Error!;
        Assert.Equal(ErrorCodes.MethodNotFound, notYet.Code);
        Assert.Contains("isn't implemented", notYet.Message);
        var invalid = peer.Call(Methods.EventsSubscribe, "{\"kinds\":[]}").Error!;
        Assert.Equal(ErrorCodes.InvalidParams, invalid.Code);
        Assert.Equal(ErrorCodes.InvalidParams, peer.Call(Methods.Cancel, "{}").Error!.Code);

        handler.Register(Methods.TimeInfo, _ => throw new ProtocolException(ErrorCodes.Unsupported, "no time here"));
        Assert.Equal(ErrorCodes.Unsupported, peer.Call(Methods.TimeInfo).Error!.Code);
        Assert.False(peer.Call(Methods.Ping).IsError);
        Assert.Throws<ArgumentException>(() => handler.Register("not.aMethod", _ => new PingResult()));
        connection.Dispose();
    }

    [Fact]
    public void The_token_comparison_rejects_near_misses()
    {
        Assert.True(_token.Matches(_token.Value));
        Assert.False(_token.Matches(_token.Value[..63] + (_token.Value[63] == 'a' ? 'b' : 'a')));
        Assert.False(_token.Matches(_token.Value + "0"));
        Assert.False(_token.Matches(null));
        Assert.Matches("^[0-9a-f]{64}$", SessionToken.Generate().Value);
        Assert.NotEqual(SessionToken.Generate().Value, SessionToken.Generate().Value);
        Assert.DoesNotContain(_token.Value, _token.ToString(), StringComparison.Ordinal);
    }
}
