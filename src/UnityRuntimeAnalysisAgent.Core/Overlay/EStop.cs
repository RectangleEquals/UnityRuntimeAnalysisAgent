using System;
using System.Collections.Generic;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Hosting;

namespace UnityRuntimeAnalysisAgent.Core.Overlay;

/// <summary>What E-STOP does, step by step (wired to the agent's services by the host; fakes in tests).</summary>
public sealed class EStopSteps
{
    /// <summary>Lowers the mode to ReadOnly; returns the mode now in effect.</summary>
    public Func<AgentMode> LowerMode { get; set; } = () => AgentMode.ReadOnly;

    /// <summary>Reverts every live patch set; returns how many.</summary>
    public Func<long> RevertPatches { get; set; } = () => 0;

    /// <summary>Removes all instrumentation; returns how many items.</summary>
    public Func<long> RemoveInstrumentation { get; set; } = () => 0;

    /// <summary>Cancels running and queued jobs; returns how many.</summary>
    public Func<long> CancelJobs { get; set; } = () => 0;

    /// <summary>Cancels every rule and releases rule-held pauses; returns how many rules.</summary>
    public Func<long> CancelRules { get; set; } = () => 0;

    /// <summary>Pauses the game (only when <c>Overlay.EStopPauses</c> is on).</summary>
    public Action Pause { get; set; } = () => { };

    /// <summary>Sends <c>overlay.estop</c> to every client.</summary>
    public Action<EstopEventParams> Emit { get; set; } = _ => { };

    /// <summary>Disconnects every client (only when <c>Overlay.EStopDisconnects</c> is on), after the event.</summary>
    public Action Disconnect { get; set; } = () => { };

    /// <summary>The frame and realtime for the event.</summary>
    public Func<(long Frame, long RealtimeMs)> Clock { get; set; } = () => (0, 0);
}

/// <summary>
/// E-STOP: one action that lowers the mode to ReadOnly, reverts live patches, removes instrumentation, cancels
/// jobs and rules (releasing rule-held pauses), optionally pauses, tells every client (<c>overlay.estop</c>) and then
/// optionally disconnects them. Every step runs even if an earlier one fails, and each is logged.
/// </summary>
public sealed class EStop
{
    private readonly EStopSteps _steps;
    private readonly IAgentLogger _log;
    private readonly Func<bool> _pauses;
    private readonly Func<bool> _disconnects;

    /// <summary>Creates the sequence; the two settings are read when it's engaged.</summary>
    public EStop(EStopSteps steps, IAgentLogger log, Func<bool> pauses, Func<bool> disconnects)
    {
        _steps = steps;
        _log = log;
        _pauses = pauses;
        _disconnects = disconnects;
    }

    /// <summary>Whether E-STOP was engaged in this run of the game (the arrow stays red).</summary>
    public bool Engaged { get; private set; }

    /// <summary>The steps that failed the last time, with why.</summary>
    public IReadOnlyList<string> Failures { get; private set; } = Array.Empty<string>();

    /// <summary>Runs the whole sequence and returns the event it sent.</summary>
    public EstopEventParams Engage()
    {
        Engaged = true;
        var failures = new List<string>();
        _log.Warning("E-STOP engaged by the user.");
        var mode = Step("lower the mode to ReadOnly", _steps.LowerMode, AgentMode.ReadOnly, failures);
        var patches = Step("revert live patches", _steps.RevertPatches, 0, failures);
        var instrumentation = Step("remove instrumentation", _steps.RemoveInstrumentation, 0, failures);
        var jobs = Step("cancel jobs", _steps.CancelJobs, 0, failures);
        var rules = Step("cancel rules and release their pauses", _steps.CancelRules, 0, failures);
        var paused = _pauses() && Step("pause the game", () => { _steps.Pause(); return true; }, false, failures);
        var disconnecting = _disconnects();
        var (frame, realtimeMs) = _steps.Clock();
        var report = new EstopEventParams
        {
            Mode = mode,
            PatchesReverted = patches,
            InstrumentationRemoved = instrumentation,
            JobsCancelled = jobs,
            RulesCancelled = rules,
            Paused = paused,
            Disconnecting = disconnecting,
            Frame = frame,
            RealtimeMs = realtimeMs,
        };
        Step("tell the clients", () => { _steps.Emit(report); return true; }, false, failures);
        if (disconnecting)
        {
            Step("disconnect the clients", () => { _steps.Disconnect(); return true; }, false, failures);
        }

        Failures = failures;
        _log.Warning($"E-STOP done: mode {AgentModes.ToWire(mode)}, {patches} patch set(s) reverted, {instrumentation} instrumentation item(s) removed, "
            + $"{jobs} job(s) and {rules} rule(s) cancelled{(paused ? ", game paused" : "")}{(disconnecting ? ", clients disconnected" : "")}"
            + (failures.Count == 0 ? "." : $"; {failures.Count} step(s) failed."));
        return report;
    }

    private T Step<T>(string name, Func<T> step, T fallback, List<string> failures)
    {
        try
        {
            var result = step();
            _log.Info($"E-STOP: {name}: done.");
            return result;
        }
        catch (Exception e)
        {
            failures.Add($"{name}: {e.Message}");
            _log.Error($"E-STOP: {name} failed.", e);
            return fallback;
        }
    }
}
