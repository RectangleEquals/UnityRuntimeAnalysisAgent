using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Tests.Support;
using UnityRuntimeAnalysisAgent.Core.Transport;

namespace UnityRuntimeAnalysisAgent.Core.Tests.Session;

/// <summary>The handshake rules and the session methods, against a real host over in-memory connections.</summary>
public sealed class SessionHandlerTests : IDisposable
{
    private readonly TestHost _test = new();

    private string Hello(string? token = null, int major = ProtocolVersion.Major, int minor = ProtocolVersion.Minor) =>
        $"{{\"token\":\"{token ?? _test.Host.Token.Value}\",\"client\":{{\"name\":\"test\",\"version\":\"1\"}},\"protocol\":{{\"major\":{major},\"minor\":{minor}}}}}";

    [Fact]
    public void A_valid_hello_returns_agent_info()
    {
        var peer = _test.Connect(hello: false);
        var response = peer.Call(Methods.Hello, Hello(), "{\"task\":\"t\"}");
        Assert.False(response.IsError);
        var info = AgentInfo.Read(response.Result, "result");
        Assert.Equal(AgentMode.ReadOnly, info.Mode);
        Assert.Equal("{\"task\":\"t\"}", response.Context!.ToString());
        Assert.False(peer.Call(Methods.Hello, Hello()).IsError); // a repeated hello just returns agent.info
    }

    [Theory]
    [InlineData("bad-token")]
    [InlineData("wrong-minor")]
    [InlineData("wrong-major")]
    [InlineData("not-hello")]
    [InlineData("invalid-hello")]
    public void Handshake_failures_are_answered_and_the_connection_closes(string scenario)
    {
        var peer = _test.Connect(hello: false);
        var (method, parameters, code) = scenario switch
        {
            "bad-token" => (Methods.Hello, Hello(token: new string('0', 64)), ErrorCodes.BadToken),
            "wrong-minor" => (Methods.Hello, Hello(minor: ProtocolVersion.Minor + 1), ErrorCodes.ProtocolMismatch),
            "wrong-major" => (Methods.Hello, Hello(major: ProtocolVersion.Major + 1), ErrorCodes.ProtocolMismatch),
            "not-hello" => (Methods.Ping, "{}", ErrorCodes.HandshakeRequired),
            _ => (Methods.Hello, "{\"token\":\"x\"}", ErrorCodes.InvalidParams),
        };
        Assert.Equal(code, peer.Call(method, parameters).Error!.Code);
        Assert.Null(peer.Receive()); // closed
    }

    [Fact]
    public void Session_methods()
    {
        var peer = _test.Connect();
        var ping = PingResult.Read(peer.Call(Methods.Ping, "{\"echo\":\"hi\"}").Result, "result");
        Assert.Equal("hi", ping.Echo);
        Assert.Null(ping.Frame); // no frame has ticked yet

        var caps = AgentCapabilities.Read(peer.Call(Methods.AgentCapabilities).Result, "result");
        Assert.Equal(
            new[]
            {
                "activity.get", "activity.list", "agent.capabilities", "agent.info", "agent.logLevel", "agent.selfTest", "agent.setMode", "batch", "cancel",
                "code.resolve", "events.subscribe", "events.unsubscribe", "handles.list", "handles.release", "handles.releaseAll", "hello",
                "job.cancel", "job.get", "job.list", "job.wait", "locator.resolve", "ping", "value.expand", "vars.delete", "vars.get", "vars.list", "vars.set",
            },
            caps.Methods.Select(m => m.Name));
        Assert.Equal(120_000, caps.Methods.Single(m => m.Name == "job.wait").DefaultTimeoutMs);
        Assert.Contains(EventKinds.JobFinished, caps.EventKinds);
        Assert.Equal("16777216", ((UnityLudometry.Protocol.Json.JsonNumber)caps.Limits["Transport.MaxFrameBytes"]!).RawText);

        var sub = EventsSubscribeResult.Read(peer.Call(Methods.EventsSubscribe, "{\"kinds\":[\"log\",\"future.kind\",\"log\"]}").Result, "result");
        Assert.Equal(["log"], sub.Subscribed);
        Assert.Equal(["future.kind"], sub.Unknown);
        Assert.Equal(["log"], EventsUnsubscribeResult.Read(peer.Call(Methods.EventsUnsubscribe, "{}").Result, "result").Unsubscribed);
    }

    [Fact]
    public void Errors_after_the_handshake_keep_the_connection_open()
    {
        var peer = _test.Connect();
        Assert.Equal(ErrorCodes.MethodNotFound, peer.Call("no.suchMethod").Error!.Code);
        var notYet = peer.Call(Methods.ObjGet, "{}").Error!;
        Assert.Equal(ErrorCodes.MethodNotFound, notYet.Code);
        Assert.Contains("isn't implemented", notYet.Message);
        Assert.Equal(ErrorCodes.InvalidParams, peer.Call(Methods.EventsSubscribe, "{\"kinds\":[]}").Error!.Code);
        Assert.Equal(ErrorCodes.InvalidParams, peer.Call(Methods.Cancel, "{}").Error!.Code);
        Assert.False(peer.Call(Methods.Ping).IsError);
    }

    [Fact]
    public void The_token_comparison_rejects_near_misses()
    {
        var token = SessionToken.Generate();
        Assert.True(token.Matches(token.Value));
        Assert.False(token.Matches(token.Value[..63] + (token.Value[63] == 'a' ? 'b' : 'a')));
        Assert.False(token.Matches(token.Value + "0"));
        Assert.False(token.Matches(null));
        Assert.Matches("^[0-9a-f]{64}$", SessionToken.Generate().Value);
        Assert.NotEqual(SessionToken.Generate().Value, SessionToken.Generate().Value);
        Assert.DoesNotContain(token.Value, token.ToString(), StringComparison.Ordinal);
    }

    public void Dispose() => _test.Dispose();
}
