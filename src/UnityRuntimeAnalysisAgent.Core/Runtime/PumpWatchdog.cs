using System;
using System.Threading;
using UnityRuntimeAnalysisAgent.Core.Abstractions;
using UnityRuntimeAnalysisAgent.Core.Hosting;

namespace UnityRuntimeAnalysisAgent.Core.Runtime;

/// <summary>
/// Watches the pump from a timer thread. If the game destroyed the pump host, it's recreated. If no frame ticked for
/// <c>Pump.StallMs</c> (a blocking load, a hang), the pump is marked stalled: main-thread requests that time out meanwhile
/// fail with <c>MAIN_THREAD_UNAVAILABLE</c> instead of <c>TIMEOUT</c>, and the pump host is recreated in case it's gone.
/// </summary>
public sealed class PumpWatchdog : IDisposable
{
    private readonly MainThreadPump _pump;
    private readonly IUnityApi _unity;
    private readonly double _stallMs;
    private readonly IAgentLogger _log;
    private Timer? _timer;

    /// <summary>Creates the watchdog (call <see cref="Start"/> to run it on a timer, or <see cref="Check"/> directly).</summary>
    public PumpWatchdog(MainThreadPump pump, IUnityApi unity, double stallMs, IAgentLogger log)
    {
        _pump = pump;
        _unity = unity;
        _stallMs = stallMs;
        _log = log;
    }

    /// <summary>How long the main thread hasn't ticked, when stalled (else 0).</summary>
    public long StalledMs => _pump.IsStalled ? (long)Math.Max(0, _pump.NowMs - _pump.LastTickAtMs) : 0;

    /// <summary>Checks every <paramref name="periodMs"/>.</summary>
    public void Start(int periodMs = 250)
    {
        _timer ??= new Timer(_ => Check(), null, periodMs, periodMs);
    }

    /// <summary>One check (thread-safe).</summary>
    public void Check()
    {
        try
        {
            if (!_pump.IsStarted)
            {
                return;
            }

            var sinceTick = _pump.NowMs - _pump.LastTickAtMs;
            if (sinceTick > _stallMs && !_pump.IsStalled)
            {
                _pump.IsStalled = true;
                _log.Warning($"The main thread hasn't ticked for {sinceTick:0} ms; main-thread requests will fail with MAIN_THREAD_UNAVAILABLE until it does.");
            }

            if (!_unity.IsPumpHostAlive)
            {
                _log.Warning("The pump host was destroyed; recreating it.");
                _unity.RecreatePumpHost();
                _pump.RecreatedCount++;
            }
        }
        catch (Exception e)
        {
            _log.Error("The pump watchdog failed.", e);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
