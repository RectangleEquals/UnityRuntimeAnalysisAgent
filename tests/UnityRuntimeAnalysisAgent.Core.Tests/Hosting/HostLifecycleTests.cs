using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Client;
using UnityRuntimeAnalysisAgent.Core.Hosting;
using UnityRuntimeAnalysisAgent.Core.Tests.Support;

namespace UnityRuntimeAnalysisAgent.Core.Tests.Hosting;

public sealed class HostLifecycleTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestHost _test = new();
    private readonly TestService _service = new();

    public HostLifecycleTests()
    {
        _service.Unity = _test.Unity;
        _service.Jobs = _test.Host.Jobs;
        _test.Host.Dispatcher.Register(_service);
    }

    [Fact]
    public async Task The_client_library_talks_to_a_host_with_fakes()
    {
        await using var client = await AgentClient.ConnectTcpAsync(_test.Host.Transport!.Port!.Value, _test.Host.Token.Value, "test", Ct);
        Assert.Equal(AgentMode.ReadOnly, client.Info.Mode);
        Assert.True(client.Info.Health.Pump.Alive);
        Assert.Equal("16777216", ((JsonNumber)client.Info.Limits["Transport.MaxFrameBytes"]!).RawText);

        var caps = AgentCapabilities.Read(await client.CallAsync(Methods.AgentCapabilities, cancellationToken: Ct), "result");
        Assert.Contains(caps.Methods, m => m.Name == Methods.AppInfo && m.Thread == "main");

        var pending = client.CallAsync(Methods.AppInfo, cancellationToken: Ct);
        await Task.Delay(100, Ct);
        Assert.False(pending.IsCompleted); // a main-thread method waits for a frame
        _test.Unity.StepFrames();
        Assert.Equal("app", PingResult.Read(await pending.WaitAsync(TimeSpan.FromSeconds(5), Ct), "result").Echo);

        var info = AgentInfo.Read(await client.CallAsync(Methods.AgentInfo, cancellationToken: Ct), "result");
        Assert.Equal(_test.Unity.Frame, info.Health.Pump.LastTickFrame);
        Assert.Equal(0, info.Health.JobsRunning);
    }

    [Fact]
    public void The_mode_can_only_be_lowered_remotely()
    {
        using var full = new TestHost(mode: "Full");
        var peer = full.Connect();
        var lowered = AgentSetModeResult.Read(peer.Call(Methods.AgentSetMode, "{\"mode\":\"ReadOnly+Load\"}").Result, "result");
        Assert.Equal((AgentMode.Full, AgentMode.ReadOnlyLoad), (lowered.Previous, lowered.Mode));
        var raise = peer.Call(Methods.AgentSetMode, "{\"mode\":\"Full\"}").Error!;
        Assert.Equal(ErrorCodes.ModeForbidden, raise.Code);
        Assert.Equal(AgentMode.ReadOnlyLoad, full.Host.Modes.Current);
        Assert.Equal(AgentMode.ReadOnlyLoad, AgentInfo.Read(peer.Call(Methods.AgentInfo).Result, "result").Mode);
        Assert.True(full.Log.Contains(AgentLogLevel.Warning, "Mode lowered from Full to ReadOnly+Load"));
    }

    [Fact]
    public void The_log_level_can_be_changed()
    {
        var peer = _test.Connect();
        var result = AgentLogLevelResult.Read(peer.Call(Methods.AgentLogLevel, "{\"level\":\"Error\"}").Result, "result");
        Assert.Equal(("Info", "Error"), (result.Previous, result.Level));
        _test.Host.Log.Warning("hidden now");
        Assert.False(_test.Log.Contains(AgentLogLevel.Warning, "hidden now"));
        Assert.Equal(ErrorCodes.InvalidParams, peer.Call(Methods.AgentLogLevel, "{\"level\":\"Loud\"}").Error!.Code);
    }

    [Fact]
    public void A_same_frame_batch_runs_every_main_thread_request_in_one_tick()
    {
        var peer = _test.Connect();
        var requests = string.Join(",", Enumerable.Repeat("{\"method\":\"app.info\"}", 5));
        var batch = BatchResult.Read(_test.CallStepping(peer, Methods.Batch, $"{{\"requests\":[{requests},{{\"method\":\"ping\"}}],\"sameFrame\":true}}").Result, "result");
        Assert.Equal(6, batch.Results.Count);
        Assert.All(batch.Results, r => Assert.Null(r.Error));
        Assert.Single(_service.TickFrames.Distinct()); // all five in the same frame
        Assert.Equal(_service.TickFrames[0], batch.Frame);

        // Without sameFrame, main-thread requests take their turn in the pump (here with a 1-frame budget each).
        _service.TickFrames.Clear();
        _test.CallStepping(peer, Methods.Batch, $"{{\"requests\":[{requests}]}}");
        Assert.Equal(5, _service.TickFrames.Count);
    }

    [Fact]
    public void Batch_errors_stopOnError_and_what_a_batch_cant_hold()
    {
        var peer = _test.Connect();
        var batch = BatchResult.Read(peer.Call(Methods.Batch,
            "{\"requests\":[{\"method\":\"ping\"},{\"method\":\"no.such\"},{\"method\":\"ping\"}],\"stopOnError\":true}").Result, "result");
        Assert.Null(batch.Results[0].Error);
        Assert.Equal(ErrorCodes.MethodNotFound, batch.Results[1].Error!.Code);
        Assert.Equal(ErrorCodes.Cancelled, batch.Results[2].Error!.Code);

        var carryOn = BatchResult.Read(peer.Call(Methods.Batch, "{\"requests\":[{\"method\":\"no.such\"},{\"method\":\"ping\"}]}").Result, "result");
        Assert.Null(carryOn.Results[1].Error);

        Assert.Equal(ErrorCodes.InvalidParams, peer.Call(Methods.Batch, "{\"requests\":[{\"method\":\"batch\",\"params\":{\"requests\":[]}}]}").Error!.Code);
        var multiFrame = BatchResult.Read(_test.CallStepping(peer, Methods.Batch, "{\"requests\":[{\"method\":\"time.info\"}],\"sameFrame\":true}").Result, "result");
        Assert.Equal(ErrorCodes.InvalidParams, multiFrame.Results[0].Error!.Code); // a multi-frame method can't share one frame

        var job = _test.Host.Jobs.Start("slow", j =>
        {
            j.Cancellation.WaitHandle.WaitOne(5000);
            return null;
        });
        var waiting = BatchResult.Read(_test.CallStepping(peer, Methods.Batch, $"{{\"requests\":[{{\"method\":\"job.wait\",\"params\":{{\"jobId\":\"{job.JobId}\"}}}}],\"sameFrame\":true}}").Result, "result");
        Assert.Equal(ErrorCodes.InvalidParams, waiting.Results[0].Error!.Code); // nor can a method that waits
        _test.Host.Jobs.Cancel(job.JobId);
    }

    [Fact]
    public void Shutdown_cleans_up_in_order_and_is_idempotent()
    {
        var order = new List<string>();
        _test.Host.RegisterCleanup("patches", () => order.Add("patches"));
        _test.Host.RegisterCleanup("hooks", () => order.Add("hooks"));
        _test.Host.RegisterCleanup("broken", () => throw new InvalidOperationException("cleanup bug"));
        _service.JobGate.Reset();
        var peer = _test.Connect();
        var job = JobRef.Read(peer.Call(Methods.SurveyStart, "{\"outFile\":\"X:/Example/s.ndjson\"}").Result, "result");

        _test.Host.Shutdown();
        _test.Host.Shutdown();

        Assert.Equal(new[] { "hooks", "patches" }, order); // reverse order of registration; a failing cleanup doesn't stop the rest
        Assert.True(_test.Log.Contains(AgentLogLevel.Error, "Shutdown step 'broken' failed"));
        Assert.Equal("cancelled", _test.Host.Jobs.Get(job.JobId).State);
        Assert.Equal(1, _test.Unity.DestroyedCount);
        Assert.Equal("destroy", _test.Unity.Calls[^1]);
        Assert.Null(peer.Receive()); // connections closed
        Assert.Equal(0, _test.Host.ConnectionCount);
    }

    public void Dispose() => _test.Dispose();
}
