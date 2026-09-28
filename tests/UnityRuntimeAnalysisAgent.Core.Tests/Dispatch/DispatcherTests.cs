using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Dispatch;
using UnityRuntimeAnalysisAgent.Core.Hosting;
using UnityRuntimeAnalysisAgent.Core.Tests.Support;

namespace UnityRuntimeAnalysisAgent.Core.Tests.Dispatch;

public sealed class DispatcherTests : IDisposable
{
    private readonly TestHost _test;
    private readonly TestService _service = new();

    public DispatcherTests()
    {
        _test = new TestHost(mode: "ReadOnly");
        _service.Unity = _test.Unity;
        _service.Jobs = _test.Host.Jobs;
        _test.Host.Dispatcher.Register(_service);
    }

    [Fact]
    public void A_main_thread_method_answers_only_in_a_frame()
    {
        var peer = _test.Connect();
        var id = peer.Send(Methods.AppInfo);
        Thread.Sleep(100);
        Assert.Empty(_service.TickFrames); // nothing ran: no frame yet
        _test.Unity.StepFrames();
        var response = peer.AwaitResponse(id);
        Assert.Equal(_test.Unity.Frame, PingResult.Read(response.Result, "result").Frame);
    }

    [Fact]
    public void A_multi_frame_method_resumes_across_frames()
    {
        var peer = _test.Connect();
        var result = PingResult.Read(_test.CallStepping(peer, Methods.TimeInfo).Result, "result");
        Assert.Equal("waited", result.Echo);
        Assert.Equal(3, result.Frame);
    }

    [Fact]
    public void Each_pipeline_stage_answers_with_its_error()
    {
        var peer = _test.Connect();
        Assert.Equal(ErrorCodes.MethodNotFound, peer.Call("no.such").Error!.Code);

        var forbidden = peer.Call(Methods.AppQuit, "{}").Error!;
        Assert.Equal(ErrorCodes.ModeForbidden, forbidden.Code);
        Assert.Equal("Full", ((JsonString)forbidden.Data!["requiredMode"]!).Value);
        Assert.Equal(0, _service.QuitCalls);

        var unsupported = peer.Call(Methods.AddressablesInfo).Error!;
        Assert.Equal(ErrorCodes.Unsupported, unsupported.Code);
        Assert.Equal("module:addressables", ((JsonString)((JsonArray)unsupported.Data!["requires"]!)[0]).Value);
        _test.Host.Capabilities.SetModule("addressables", true, "1.19.19");
        Assert.False(_test.CallStepping(peer, Methods.AddressablesInfo).IsError);

        // The mode is checked before the params: a forbidden call with bad params is still MODE_FORBIDDEN.
        Assert.Equal(ErrorCodes.ModeForbidden, peer.Call(Methods.AppQuit, "{\"exitCode\":\"not a number\"}").Error!.Code);
        Assert.Equal(ErrorCodes.InvalidParams, peer.Call(Methods.JobGet, "{}").Error!.Code);
    }

    [Fact]
    public void Full_mode_runs_mutating_methods_and_records_them_in_the_audit_log()
    {
        using var full = new TestHost(mode: "Full");
        var service = new TestService { Unity = full.Unity };
        full.Host.Dispatcher.Register(service);
        var peer = full.Connect();
        Assert.True(AppQuitResult.Read(full.CallStepping(peer, Methods.AppQuit, "{}").Result, "result").Quitting);
        Assert.Equal(1, service.QuitCalls);
        Assert.True(full.Log.Contains(AgentLogLevel.Info, "[audit]"));
        Assert.True(full.Log.Contains(AgentLogLevel.Info, "app.quit by test 1: ok"));
    }

    [Fact]
    public void Errors_are_mapped_and_agent_defects_logged()
    {
        var peer = _test.Connect();
        var game = peer.Call(Methods.LogsTail, "{\"how\":\"game\"}").Error!;
        Assert.Equal(ErrorCodes.GameException, game.Code);
        Assert.Equal("System.InvalidOperationException", ((JsonString)game.Data!["exceptionType"]!).Value);
        Assert.Equal(ErrorCodes.NotFound, peer.Call(Methods.LogsTail, "{\"how\":\"protocol\"}").Error!.Code);

        var bug = peer.Call(Methods.LogsTail, "{\"how\":\"bug\"}").Error!;
        Assert.Equal(ErrorCodes.Internal, bug.Code);
        var stack = ((JsonString)bug.Data!["stack"]!).Value;
        Assert.True(stack.Split('\n').Length <= AgentErrors.StackLines + 1);
        Assert.True(_test.Log.Contains(AgentLogLevel.Error, "logs.tail failed"));
    }

    [Fact]
    public void Timeouts_and_cancellation()
    {
        var peer = _test.Connect();
        var hang = peer.Call(Methods.LogsTail, "{\"how\":\"hang\"}").Error!; // DefaultTimeoutMs = 300
        Assert.Equal(ErrorCodes.Timeout, hang.Code);

        var id = peer.Send(Methods.LogsTail, "{\"how\":\"hang\"}");
        Thread.Sleep(50);
        var cancel = CancelResult.Read(peer.Call(Methods.Cancel, $"{{\"id\":\"{id}\"}}").Result, "result");
        Assert.True(cancel.Cancelled);
        Assert.Equal(ErrorCodes.Cancelled, peer.AwaitResponse(id).Error!.Code);
        Assert.False(CancelResult.Read(peer.Call(Methods.Cancel, "{\"id\":\"nothing-in-flight\"}").Result, "result").Cancelled);
    }

    [Fact]
    public void A_stalled_main_thread_fails_waiting_requests_with_MAIN_THREAD_UNAVAILABLE()
    {
        var peer = _test.Connect();
        _test.Host.Pump.IsStalled = true; // as the watchdog marks it when no frame ticks
        var id = peer.Send(Methods.TimeInfo); // DefaultTimeoutMs = 2000
        var response = peer.AwaitResponse(id, 5000);
        Assert.Equal(ErrorCodes.MainThreadUnavailable, response.Error!.Code);
    }

    [Fact]
    public void Without_Unity_main_thread_methods_fail_at_once()
    {
        using var headless = new TestHost(withUnity: false);
        headless.Host.Dispatcher.Register(new TestService());
        var peer = headless.Connect();
        Assert.Equal(ErrorCodes.MainThreadUnavailable, peer.Call(Methods.AppInfo).Error!.Code);
        Assert.False(AgentInfo.Read(peer.Call(Methods.AgentInfo).Result, "result").Health.Pump.Alive);
    }

    [Fact]
    public void Context_is_echoed_and_every_call_is_in_the_activity_feed()
    {
        var peer = _test.Connect();
        var response = peer.Call(Methods.Ping, "{}", "{\"finding\":\"F-17\"}");
        Assert.Equal("{\"finding\":\"F-17\"}", response.Context!.ToString());
        _test.CallStepping(peer, Methods.AppInfo);
        peer.Call("no.such");

        var list = ActivityListResult.Read(peer.Call(Methods.ActivityList, "{}").Result, "result");
        var methods = list.Items.Select(i => i.Method).ToList();
        Assert.Equal(new[] { Methods.Ping, Methods.AppInfo, "no.such" }, methods.Where(m => m != Methods.ActivityList));
        var appInfo = list.Items.Single(i => i.Method == Methods.AppInfo);
        Assert.Equal("live://test/app", appInfo.Target);
        Assert.Equal("client", appInfo.Source);
        Assert.Equal("test 1", appInfo.Client);
        Assert.Equal(ErrorCodes.MethodNotFound, list.Items.Single(i => i.Method == "no.such").ErrorCode);

        var detail = ActivityGetResult.Read(peer.Call(Methods.ActivityGet, $"{{\"id\":{appInfo.Id}}}").Result, "result");
        Assert.NotNull(detail.Response["result"]);
        Assert.Equal(ErrorCodes.NotFound, peer.Call(Methods.ActivityGet, "{\"id\":99999}").Error!.Code);
        var since = ActivityListResult.Read(peer.Call(Methods.ActivityList, $"{{\"sinceId\":{appInfo.Id},\"limit\":1}}").Result, "result");
        Assert.Single(since.Items);
        Assert.True(since.Total >= 2);
    }

    [Fact]
    public void Only_protocol_methods_can_be_registered()
    {
        Assert.Throws<ArgumentException>(() => _test.Host.Dispatcher.Register(new NotAProtocolMethod()));
    }

    private sealed class NotAProtocolMethod
    {
        [RpcMethod("made.up")]
        public ProtocolMessage MadeUp(RequestContext context) => new PingResult();
    }

    public void Dispose() => _test.Dispose();
}
