using System.Collections;
using UnityLudometry.Protocol;
using UnityRuntimeAnalysisAgent.Core.Runtime;
using UnityRuntimeAnalysisAgent.Core.Tests.Support;

namespace UnityRuntimeAnalysisAgent.Core.Tests.Runtime;

public sealed class MainThreadPumpTests
{
    private readonly FakeUnityApi _unity = new();
    private readonly TestLogger _log = new();

    private MainThreadPump Pump(double budgetMs = 4)
    {
        var pump = new MainThreadPump(_unity, budgetMs, _log, _unity.NowMs);
        pump.Start();
        return pump;
    }

    private sealed class Outcomes
    {
        public List<object?> Results { get; } = new();

        public List<Exception> Errors { get; } = new();

        public PumpWork Work(Func<object?> body) => new(body, r => Results.Add(r), e => Errors.Add(e));

        public PumpWork Cancellable(Func<object?> body, CancellationToken cancellation) => new(body, r => Results.Add(r), e => Errors.Add(e), cancellation);
    }

    [Fact]
    public void Work_runs_in_the_next_frame_within_the_budget_and_the_rest_spills_over()
    {
        var pump = Pump(budgetMs: 4);
        var outcomes = new Outcomes();
        var frames = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            pump.Enqueue(outcomes.Work(() =>
            {
                frames.Add(_unity.Frame);
                _unity.RealtimeMs += 3; // each item "takes" 3 ms
                return "ok";
            }));
        }

        Assert.Empty(outcomes.Results);
        _unity.StepFrames();
        Assert.Equal(2, outcomes.Results.Count); // 3 ms + 3 ms crosses the 4 ms budget after the second item
        _unity.StepFrames(2);
        Assert.Equal(5, outcomes.Results.Count);
        Assert.Equal(new long[] { 1, 1, 2, 2, 3 }, frames);
        Assert.Equal(0, pump.QueueLength);
    }

    [Fact]
    public void At_least_one_item_runs_even_when_it_blows_the_budget()
    {
        var pump = Pump(budgetMs: 1);
        var outcomes = new Outcomes();
        pump.Enqueue(outcomes.Work(() =>
        {
            _unity.RealtimeMs += 50;
            return 1;
        }));
        pump.Enqueue(outcomes.Work(() => 2));
        _unity.StepFrames();
        Assert.Equal(new object?[] { 1 }, outcomes.Results);
        _unity.StepFrames();
        Assert.Equal(new object?[] { 1, 2 }, outcomes.Results);
    }

    private IEnumerable Routine(List<string> log)
    {
        log.Add($"start@{_unity.Frame}");
        yield return PumpWait.NextFrame;
        log.Add($"next@{_unity.Frame}");
        yield return PumpWait.Frames(3);
        log.Add($"frames@{_unity.Frame}");
        yield return PumpWait.Realtime(100);
        log.Add($"realtime@{_unity.Frame}");
        var ready = _unity.Frame + 2;
        yield return PumpWait.Until(() => _unity.Frame >= ready, 10_000);
        log.Add($"until@{_unity.Frame}");
        yield return PumpWait.EndOfFrame;
        log.Add($"eof@{_unity.Frame}");
        yield return "result";
    }

    [Fact]
    public void Routines_resume_across_frames_with_every_wait_primitive()
    {
        var pump = Pump();
        var outcomes = new Outcomes();
        var log = new List<string>();
        pump.Enqueue(outcomes.Work(() => Routine(log)));
        _unity.StepFrames(20);
        Assert.Equal(new object?[] { "result" }, outcomes.Results);
        // 16 ms per frame: 100 ms of real time is 7 frames after frame 5.
        Assert.Equal(new[] { "start@1", "next@2", "frames@5", "realtime@12", "until@14", "eof@14" }, log);
        Assert.Empty(outcomes.Errors);
    }

    [Fact]
    public void Cancellation_is_observed_at_every_yield()
    {
        var pump = Pump();
        var outcomes = new Outcomes();
        using var cts = new CancellationTokenSource();
        var steps = 0;

        IEnumerable Forever()
        {
            while (true)
            {
                steps++;
                yield return PumpWait.NextFrame;
            }
        }

        pump.Enqueue(outcomes.Cancellable(Forever, cts.Token));
        _unity.StepFrames(3);
        cts.Cancel();
        _unity.StepFrames(3);
        Assert.Equal(3, steps);
        Assert.IsType<OperationCanceledException>(Assert.Single(outcomes.Errors));
        Assert.Equal(0, pump.QueueLength);

        using var before = new CancellationTokenSource();
        before.Cancel();
        pump.Enqueue(outcomes.Cancellable(() => throw new InvalidOperationException("must not run"), before.Token));
        _unity.StepFrames();
        Assert.Equal(2, outcomes.Errors.Count);
        Assert.IsType<OperationCanceledException>(outcomes.Errors[1]);
    }

    [Fact]
    public void A_failing_item_or_routine_fails_only_itself()
    {
        var pump = Pump();
        var outcomes = new Outcomes();

        IEnumerable Throws()
        {
            yield return PumpWait.NextFrame;
            throw new InvalidOperationException("routine bug");
        }

        pump.Enqueue(outcomes.Work(() => throw new InvalidOperationException("body bug")));
        pump.Enqueue(outcomes.Work(Throws));
        pump.Enqueue(outcomes.Work(() => "fine"));
        pump.Enqueue(new PumpWork(() => "callback", _ => throw new InvalidOperationException("callback bug"), _ => { }));
        _unity.StepFrames(3);
        Assert.Equal(new object?[] { "fine" }, outcomes.Results);
        Assert.Equal(new[] { "body bug", "routine bug" }, outcomes.Errors.Select(e => e.Message));
        Assert.True(_log.Contains(Core.Hosting.AgentLogLevel.Error, "completion callback failed"));
    }

    [Fact]
    public void Until_times_out_with_TIMEOUT()
    {
        var pump = Pump();
        var outcomes = new Outcomes();

        IEnumerable Never()
        {
            yield return PumpWait.Until(() => false, 50);
            yield return "unreachable";
        }

        pump.Enqueue(outcomes.Work(Never));
        _unity.StepFrames(10);
        Assert.Equal(ErrorCodes.Timeout, Assert.IsType<ProtocolException>(Assert.Single(outcomes.Errors)).Code);
    }

    [Fact]
    public void Same_frame_work_can_run_inline_but_not_routines()
    {
        var pump = Pump();
        Assert.Equal(42, pump.RunInline(() => 42));
        Assert.Equal(ErrorCodes.InvalidParams, Assert.Throws<ProtocolException>(() => pump.RunInline(() => Routine(new List<string>()))).Code);
    }

    [Fact]
    public void A_stalled_main_thread_is_detected_the_host_recreated_and_recovery_clears_it()
    {
        var pump = Pump();
        using var watchdog = new PumpWatchdog(pump, _unity, stallMs: 500, _log);
        _unity.StepFrames();
        watchdog.Check();
        Assert.False(pump.IsStalled);

        _unity.RealtimeMs += 800; // no frames for 800 ms
        watchdog.Check();
        Assert.True(pump.IsStalled);
        Assert.InRange(watchdog.StalledMs, 800, 900);

        _unity.KillPumpHost(); // the game destroyed our host
        watchdog.Check();
        Assert.Equal(1, _unity.RecreatedCount);
        Assert.Equal(1, pump.RecreatedCount);

        _unity.StepFrames();
        Assert.False(pump.IsStalled);
        Assert.Equal(0, watchdog.StalledMs);
    }

    [Fact]
    public void Stopping_fails_waiting_work_and_destroys_the_host()
    {
        var pump = Pump();
        var outcomes = new Outcomes();
        pump.Enqueue(outcomes.Work(() => Routine(new List<string>())));
        _unity.StepFrames();
        pump.Enqueue(outcomes.Work(() => "queued"));
        pump.Stop();
        Assert.All(outcomes.Errors, e => Assert.Equal(ErrorCodes.MainThreadUnavailable, Assert.IsType<ProtocolException>(e).Code));
        Assert.Equal(2, outcomes.Errors.Count);
        Assert.Equal(1, _unity.DestroyedCount);
        pump.Enqueue(outcomes.Work(() => "after stop"));
        Assert.Equal(3, outcomes.Errors.Count);
    }
}
