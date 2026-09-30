using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Abstractions;
using UnityRuntimeAnalysisAgent.Core.Data;
using UnityRuntimeAnalysisAgent.Core.Diagnostics;
using UnityRuntimeAnalysisAgent.Core.Dispatch;
using UnityRuntimeAnalysisAgent.Core.Execution;
using UnityRuntimeAnalysisAgent.Core.Rules;
using UnityRuntimeAnalysisAgent.Core.Runtime;

namespace UnityRuntimeAnalysisAgent.Core.Session;

/// <summary>The caps and timings rules run under.</summary>
internal sealed class RuleLimits
{
    public int MaxActive { get; set; } = 32;

    public int MaxFiresPerMinute { get; set; } = 60;

    public int MaxCapturesPerMinute { get; set; } = 30;

    public int MaxPauseMs { get; set; } = 30_000;

    public bool RemoveOnDisconnect { get; set; } = true;
}

/// <summary>The agent's parts rules act through.</summary>
internal sealed class RuleActors
{
    public RuleActors(ControlServices control, ScreenshotServices screenshots, LiveServices live, SnippetRunner snippets, ActivityFeed activity)
    {
        Control = control;
        Screenshots = screenshots;
        Live = live;
        Snippets = snippets;
        Activity = activity;
    }

    public ControlServices Control { get; }

    public ScreenshotServices Screenshots { get; }

    public LiveServices Live { get; }

    public SnippetRunner Snippets { get; }

    public ActivityFeed Activity { get; }
}

/// <summary>
/// Rules: "when these things happen, do this". Conditions are evaluated at the start of each frame (polled ones) or when
/// they occur (hooks, events, logs, scenes); a satisfied rule runs its actions in order on the main thread — starting at
/// the end of the same frame when the trigger happened on the main thread. Pauses a rule holds are always bounded by
/// <c>Rules.MaxPauseMs</c> unless kept alive with <c>rule.hold</c>. Rules belong to the connection that added them, like
/// instrumentation, and all of them end with the agent.
/// </summary>
internal sealed class RuleServices : IDisposable
{
    /// <summary>Firings kept per rule for <c>rule.get</c>.</summary>
    public const int MaxFiringsKept = 100;

    /// <summary>Hook records kept per rule for the <c>hits</c> action.</summary>
    public const int MaxHitsKept = 1000;

    /// <summary>Log entries per <c>logs</c> action at most.</summary>
    public const int MaxLogsPerAction = 1000;

    /// <summary>Ended rules kept for <c>rule.get</c> (the oldest go first).</summary>
    public const int MaxEndedKept = 100;

    private const long MaxWaitMs = 3_600_000;

    private readonly object _gate = new();
    private readonly Dictionary<string, ArmedRule> _rules = new(StringComparer.Ordinal);
    private readonly Queue<double> _fires = new();
    private readonly Queue<double> _captures = new();
    private readonly MainThreadPump _pump;
    private readonly IUnityApi _unity;
    private readonly LogBuffer _logs;
    private readonly EventHub _events;
    private readonly ModeController _modes;
    private readonly RuleLimits _limits;
    private readonly RuleSources _sources;
    private readonly RuleActors _actors;
    private readonly Action<string, string, JsonObject> _warning;
    private IReadOnlyList<UiElementFacts> _ui = Array.Empty<UiElementFacts>();
    private long _uiFrame = -1;
    private int _next;
    private int _order;
    private bool _stopped;

    public RuleServices(DataModel data, MainThreadPump pump, IUnityApi unity, LogBuffer logs, EventHub events, ModeController modes,
        Instrumentation.HookManager hooks, RuleActors actors, RuleLimits limits, Action<string, string, JsonObject> warning)
    {
        _pump = pump;
        _unity = unity;
        _logs = logs;
        _events = events;
        _modes = modes;
        _limits = limits;
        _actors = actors;
        _warning = warning;
        _sources = new RuleSources(data, unity, logs, hooks, actors.Snippets, UiSnapshot);
        pump.Ticked += OnTick;
    }

    /// <summary>Rules armed now.</summary>
    public int Active
    {
        get
        {
            lock (_gate)
            {
                return _rules.Values.Count(r => r.State == "armed");
            }
        }
    }

    // ---- methods ----------------------------------------------------------------------------------------------------------

    [RpcMethod(Methods.RuleAdd, DefaultTimeoutMs = 60_000, MaxTimeoutMs = 600_000)]
    public Deferred RuleAdd(RequestContext context, Rule p)
    {
        RuleCheck.Validate(p);
        var required = RuleCheck.RequiredMode(p);
        if (!_modes.Allows(required))
        {
            throw AgentErrors.ModeForbidden(Methods.RuleAdd + " with this rule's conditions or actions", required, _modes.Current);
        }

        lock (_gate)
        {
            CheckRoom(p.Id);
        }

        var root = ConditionBuilder.Build(p.When, "when", _sources);
        var snippets = new Dictionary<int, SnippetEntry>();
        for (var i = 0; i < p.Then.Count; i++)
        {
            if (p.Then[i].Exec is { } exec)
            {
                snippets[i] = _actors.Snippets.Bind(AssemblyLoader.Decode(exec.Assembly, $"params.then[{i}].exec.assembly"), exec.EntryType, exec.EntryMethod);
                context.Assembly ??= snippets[i].Assembly.Audit;
            }
        }

        var owner = context.Connection?.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? context.Source;
        var rule = new ArmedRule(this, p, owner, required, root, snippets, context.Context);
        root.Bind(rule, null);
        var deferred = new Deferred();
        _pump.Enqueue(new PumpWork(() => Arm(rule), result => deferred.Complete((ProtocolMessage)result!), error => deferred.Fail(error), context.Cancellation));
        return deferred;
    }

    [RpcMethod(Methods.RuleList)]
    public ProtocolMessage RuleList(RequestContext context)
    {
        lock (_gate)
        {
            return new RuleListResult { Items = _rules.Values.OrderBy(r => r.Order).Select(Summary).ToList() };
        }
    }

    [RpcMethod(Methods.RuleGet)]
    public ProtocolMessage RuleGet(RequestContext context, RuleGetParams p)
    {
        lock (_gate)
        {
            var rule = Find(p.RuleId);
            return new RuleGetResult { Rule = rule.Definition, Summary = Summary(rule), Firings = rule.Firings.ToList() };
        }
    }

    /// <summary>Waits for the rule's next completed firing; a rule that can't fire again answers at once with its last one.</summary>
    [RpcMethod(Methods.RuleWait, DefaultTimeoutMs = (int)MaxWaitMs + 60_000, MaxTimeoutMs = (int)MaxWaitMs + 60_000)]
    public Deferred RuleWait(RequestContext context, RuleWaitParams p)
    {
        var deferred = new Deferred();
        lock (_gate)
        {
            var rule = Find(p.RuleId);
            if (rule.State != "armed" && !rule.Firing)
            {
                deferred.Complete(new RuleWaitResult { Firing = rule.Firings.LastOrDefault() });
                return deferred;
            }

            var waiter = new Waiter(deferred);
            rule.Waiters.Add(waiter);
            var timeout = Math.Max(1, Math.Min(p.TimeoutMs ?? 60_000, MaxWaitMs));
            waiter.Timer = new Timer(_ => Answer(rule, waiter, null), null, timeout, Timeout.Infinite);
            waiter.Cancellation = context.Cancellation.Register(() => Answer(rule, waiter, null));
        }

        return deferred;
    }

    [RpcMethod(Methods.RuleHold)]
    public ProtocolMessage RuleHold(RequestContext context, RuleHoldParams p)
    {
        lock (_gate)
        {
            var rule = Find(p.RuleId);
            var extend = Math.Max(1, Math.Min(p.ExtendMs ?? _limits.MaxPauseMs, MaxWaitMs));
            rule.HeldUntilMs = Math.Max(rule.HeldUntilMs, Now().RealtimeMs + extend);
            return new RuleHoldResult { HeldUntilRealtimeMs = (long)rule.HeldUntilMs };
        }
    }

    [RpcMethod(Methods.RuleCancel)]
    public ProtocolMessage RuleCancel(RequestContext context, RuleCancelParams p)
    {
        lock (_gate)
        {
            var rule = Find(p.RuleId);
            var cancelled = rule.State == "armed";
            var holding = rule.HoldsPause;
            End(rule, "cancelled");
            ReleaseSoon(rule);
            return new RuleCancelResult { Cancelled = cancelled, Resumed = holding };
        }
    }

    [RpcMethod(Methods.RulesClear)]
    public ProtocolMessage RulesClear(RequestContext context, RulesClearParams p)
    {
        var owner = p.Owner == "all" ? null : context.Connection?.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? context.Source;
        return new RulesClearResult { Cancelled = CancelWhere(r => owner is null || r.Owner == owner) };
    }

    /// <summary>A connection closed: its non-persistent rules end (when configured).</summary>
    public void OnDisconnect(string owner)
    {
        if (_limits.RemoveOnDisconnect)
        {
            CancelWhere(r => r.Owner == owner && !r.Persistent);
        }
    }

    /// <summary>
    /// Ends every armed rule and lets go of every pause a rule holds (E-STOP). The engine keeps running: new rules can
    /// still be added. Returns how many rules were cancelled.
    /// </summary>
    public int CancelAll()
    {
        lock (_gate)
        {
            var count = CancelWhere(_ => true);
            foreach (var rule in _rules.Values.Where(r => r.HoldsPause || r.PausedAudio).ToList())
            {
                ReleaseSoon(rule);
            }

            return count;
        }
    }

    /// <summary>Ends every rule and releases every pause a rule holds (shutdown).</summary>
    public void Dispose()
    {
        _pump.Ticked -= OnTick;
        List<ArmedRule> rules;
        lock (_gate)
        {
            _stopped = true;
            rules = _rules.Values.ToList();
            foreach (var rule in rules)
            {
                End(rule, "cancelled");
            }
        }

        foreach (var rule in rules)
        {
            try
            {
                ReleasePause(rule, null);
            }
            catch (Exception)
            {
                // Shutting down: nothing more to do.
            }
        }
    }

    // ---- arming, firing, ending -------------------------------------------------------------------------------------------

    private RuleAddResult Arm(ArmedRule rule)
    {
        lock (_gate)
        {
            CheckRoom(rule.Definition.Id);
            rule.Definition.Id ??= NextId();
            try
            {
                rule.Root.Attach();
            }
            catch (Exception)
            {
                rule.Root.Detach();
                throw;
            }

            var now = Now();
            rule.Order = ++_order;
            rule.ArmedAtFrame = now.Frame;
            rule.ExpiresAtMs = rule.Definition.ExpiresMs is { } expires ? now.RealtimeMs + expires : null;
            rule.LogSeqArmed = rule.LogSeqLastFire = _logs.NextSeq - 1;
            rule.State = "armed";
            _rules[rule.Id] = rule;
            Prune();
            rule.Root.Start(now);
            return new RuleAddResult { RuleId = rule.Id, RequiredMode = rule.RequiredMode, ArmedAtFrame = now.Frame };
        }
    }

    // The whole condition was satisfied (under the lock, on any thread).
    private void Fire(ArmedRule rule, ConditionNode trigger, bool fromHook)
    {
        if (rule.Firing || rule.State != "armed" || rule.CooldownUntilMs is not null)
        {
            return;
        }

        if (!_modes.Allows(rule.RequiredMode))
        {
            Suspend(rule, $"it needs {AgentModes.ToWire(rule.RequiredMode)} mode and the agent is now in {AgentModes.ToWire(_modes.Current)}");
            return;
        }

        var now = Now();
        if (!Take(_fires, now.RealtimeMs, _limits.MaxFiresPerMinute))
        {
            Suspend(rule, $"rules fired more than {_limits.MaxFiresPerMinute} times in a minute (Rules.MaxFiresPerMinute)");
            return;
        }

        rule.Firing = true;
        rule.Root.Stop();
        var firing = new RuleFiring
        {
            RuleId = rule.Id,
            Fire = rule.Fires + 1,
            Frame = now.Frame,
            RealtimeMs = (long)now.RealtimeMs,
            Trigger = new RuleTrigger { ConditionPath = trigger.Path, Detail = trigger.Detail },
            Results = new RuleResults(),
        };
        rule.Cancellation = new CancellationTokenSource();
        rule.StayPaused = false;
        var onMainThread = Environment.CurrentManagedThreadId == _pump.MainThreadId;
        if (rule.Definition.PauseImmediately == true && fromHook && onMainThread)
        {
            try
            {
                Pause(rule, firing, rule.Definition.Then.First(a => a.Pause is not null).Pause!);
                rule.PausedImmediately = true;
            }
            catch (Exception e)
            {
                ActionFailed(rule, 0, "pause", e);
            }
        }

        var work = new PumpWork(() => Actions(rule, firing), _ => Finish(rule, firing, null), error => Finish(rule, firing, error), rule.Cancellation.Token);
        if (onMainThread)
        {
            _pump.EnqueueEndOfFrame(work);
        }
        else
        {
            _pump.Enqueue(work);
        }
    }

    // The actions are done (or failed, or were cancelled): main thread.
    private void Finish(ArmedRule rule, RuleFiring firing, Exception? error)
    {
        var cancelled = error is OperationCanceledException;
        if (error is not null && !cancelled)
        {
            Warn("RULE_ACTION_FAILED", $"Rule {rule.Id}'s actions stopped: {error.Message}", rule);
        }

        // A pause ends with the firing unless the rule said stayPaused (then the watchdog bounds it).
        if (!rule.StayPaused || error is not null)
        {
            TryRelease(rule, firing);
        }

        List<Waiter> waiters;
        lock (_gate)
        {
            rule.Firing = false;
            rule.PausedImmediately = false;
            if (cancelled)
            {
                // The rule was cancelled while acting: this firing never completed.
                waiters = rule.Waiters.ToList();
                rule.Waiters.Clear();
                foreach (var waiter in waiters)
                {
                    waiter.Answer(null);
                }

                return;
            }

            rule.Fires++;
            rule.LastFireFrame = firing.Frame;
            rule.Firings.Add(firing);
            if (rule.Firings.Count > MaxFiringsKept)
            {
                rule.Firings.RemoveAt(0);
            }

            rule.LogSeqLastFire = _logs.NextSeq - 1;
            rule.HitSeqLastFire = rule.LastHitSeq;
            waiters = rule.Waiters.ToList();
            rule.Waiters.Clear();
            if (rule.State == "armed")
            {
                var fire = rule.Definition.Fire;
                if (rule.SuspendReason is { } reason)
                {
                    Suspend(rule, reason);
                }
                else if (fire?.Mode != "repeat" || (fire.MaxFires is { } max && rule.Fires >= max))
                {
                    End(rule, "fired");
                }
                else
                {
                    var now = Now();
                    var cooldown = Math.Max(0, fire.CooldownMs ?? 0);
                    if (cooldown == 0)
                    {
                        rule.Root.Start(now);
                    }
                    else
                    {
                        rule.CooldownUntilMs = now.RealtimeMs + cooldown;
                    }
                }
            }
        }

        foreach (var waiter in waiters)
        {
            waiter.Answer(firing);
        }

        if (_events.HasSubscribers(EventKinds.RuleFired))
        {
            _events.Publish(EventKinds.RuleFired, firing, rule.Context);
        }
    }

    // Under the lock: the rule stops listening for good, and lets go of what it observes (on the main thread).
    private void End(ArmedRule rule, string state)
    {
        if (rule.State != "armed")
        {
            return;
        }

        rule.State = state;
        rule.Root.Stop();
        if (state != "fired")
        {
            rule.Cancellation?.Cancel();
        }

        if (!rule.Firing)
        {
            foreach (var waiter in rule.Waiters)
            {
                waiter.Answer(rule.Firings.LastOrDefault());
            }

            rule.Waiters.Clear();
        }

        if (_stopped)
        {
            Detach(rule);
        }
        else
        {
            _pump.Enqueue(new PumpWork(() => Detach(rule), _ => { }, _ => { }));
        }
    }

    private void Suspend(ArmedRule rule, string reason)
    {
        End(rule, "suspended");
        Warn("RULE_SUSPENDED", $"Rule {rule.Id} was suspended: {reason}.", rule);
    }

    private object? Detach(ArmedRule rule)
    {
        try
        {
            rule.Root.Detach();
        }
        catch (Exception e)
        {
            Warn("RULE_ACTION_FAILED", $"Rule {rule.Id} couldn't stop observing: {e.Message}", rule);
        }

        return null;
    }

    private int CancelWhere(Func<ArmedRule, bool> which)
    {
        lock (_gate)
        {
            var count = 0;
            foreach (var rule in _rules.Values.Where(r => r.State == "armed" && which(r)).ToList())
            {
                End(rule, "cancelled");
                ReleaseSoon(rule);
                count++;
            }

            return count;
        }
    }

    // ---- per frame --------------------------------------------------------------------------------------------------------

    private void OnTick(FrameTime clock)
    {
        lock (_gate)
        {
            var now = Now();
            foreach (var rule in _rules.Values.ToList())
            {
                if (rule.State == "armed" && !rule.Firing)
                {
                    if (rule.ExpiresAtMs is { } expires && now.RealtimeMs >= expires)
                    {
                        End(rule, "expired");
                    }
                    else if (rule.CooldownUntilMs is { } until)
                    {
                        if (now.RealtimeMs >= until)
                        {
                            rule.CooldownUntilMs = null;
                            rule.Root.Start(now);
                        }
                    }
                    else
                    {
                        try
                        {
                            rule.Root.Poll(now);
                        }
                        catch (Exception e)
                        {
                            Suspend(rule, $"evaluating its conditions failed: {e.GetType().Name}: {e.Message}");
                        }
                    }
                }

                // The pause watchdog.
                if (rule.HoldsPause && now.RealtimeMs - rule.PausedAtMs > _limits.MaxPauseMs && now.RealtimeMs > rule.HeldUntilMs)
                {
                    if (TryRelease(rule, rule.Firing ? null : rule.Firings.LastOrDefault()))
                    {
                        Warn("RULE_PAUSE_RELEASED", $"Rule {rule.Id} kept the game paused for {_limits.MaxPauseMs} ms (Rules.MaxPauseMs); the game was resumed. Keep a pause with rule.hold.", rule);
                    }
                }
            }
        }
    }

    private IReadOnlyList<UiElementFacts> UiSnapshot(long frame)
    {
        if (_uiFrame != frame)
        {
            _uiFrame = frame;
            _ui = _unity.Ui is { UguiStatus.Available: true } ui ? ui.Snapshot(onlyInteractable: false, onlyVisible: true, includeText: true, 5000) : Array.Empty<UiElementFacts>();
        }

        return _ui;
    }

    // ---- actions ----------------------------------------------------------------------------------------------------------

    // The rule's actions in order (main thread). A failing action is reported and the next one runs.
    private IEnumerable<object?> Actions(ArmedRule rule, RuleFiring firing)
    {
        var actions = rule.Definition.Then;
        for (var i = 0; i < actions.Count; i++)
        {
            var kind = RuleCheck.KindOf(actions[i]);
            using var steps = Action(rule, firing, actions[i], i).GetEnumerator();
            while (true)
            {
                object? current;
                try
                {
                    if (!steps.MoveNext())
                    {
                        break;
                    }

                    current = steps.Current;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    ActionFailed(rule, i, kind, e);
                    break;
                }

                yield return current;
            }
        }
    }

    private IEnumerable<object?> Action(ArmedRule rule, RuleFiring firing, RuleAction action, int index)
    {
        var results = firing.Results;
        switch (RuleCheck.KindOf(action))
        {
            case "pause":
                if (rule.PausedImmediately)
                {
                    rule.PausedImmediately = false; // done inside the hook callback already
                }
                else
                {
                    Pause(rule, firing, action.Pause!);
                }

                break;
            case "waitFrames":
                yield return PumpWait.Frames((int)Math.Min(action.WaitFrames!.Value, int.MaxValue));
                break;
            case "waitMs":
                yield return PumpWait.Realtime(action.WaitMs!.Value);
                break;
            case "screenshot":
                foreach (var step in Screenshot(rule, results, action.Screenshot!))
                {
                    yield return step;
                }

                break;
            case "snapshot":
                var snapshot = _actors.Live.ObjSnapshot(Context(rule, Methods.ObjSnapshot),
                    new ObjSnapshotParams { Targets = action.Snapshot!.Targets, Paths = action.Snapshot.Paths, View = action.Snapshot.View });
                results.Snapshot = Append(results.Snapshot, snapshot.ToJson());
                break;
            case "hits":
                var since = action.Hits!.Since == "armed" ? 0 : rule.HitSeqLastFire;
                (results.Hits ??= new List<InstrumentationRecord>()).AddRange(rule.HitsSince(since));
                break;
            case "logs":
                var from = action.Logs!.Since == "armed" ? rule.LogSeqArmed : rule.LogSeqLastFire;
                var minRank = action.Logs.MinLevel is null ? 0 : Math.Max(0, LogBuffer.Rank(action.Logs.MinLevel));
                (results.Logs ??= new List<LogEntry>()).AddRange(_logs.Tail(from, MaxLogsPerAction, minRank, null, null, null).Items);
                break;
            case "mark":
                _logs.Mark(action.Mark!.Label);
                break;
            case "notify" or "highlight":
                break; // shown by the in-game overlay; without one there's nothing to show
            case "exec":
                ExecRunResult? run = null;
                var entry = rule.Snippets[index];
                Audit(rule, Methods.ExecRun, null, entry.Assembly.Audit);
                foreach (var step in _actors.Snippets.Steps(entry, action.Exec!.Args, rule.Definition.OutDir, rule.Cancellation!.Token, r => run = r))
                {
                    yield return step;
                }

                results.Exec = Append(results.Exec, run is null ? JsonNull.Instance : run.ToJson());
                break;
            case "uiClick":
                var click = Context(rule, Methods.UiClick);
                _actors.Control.UiClick(click, new UiClickParams { Target = action.UiClick!.Target });
                Audit(rule, Methods.UiClick, click.Target, null);
                break;
            case "invoke":
                var invoke = Context(rule, Methods.ObjInvoke);
                _actors.Live.ObjInvoke(invoke, new ObjInvokeParams { Target = action.Invoke!.Target, Method = action.Invoke.Method, Args = action.Invoke.Args });
                Audit(rule, Methods.ObjInvoke, invoke.Target, null);
                break;
            case "resume":
                if (action.Resume!.AfterMs is > 0)
                {
                    yield return PumpWait.Realtime(action.Resume.AfterMs.Value);
                }

                TryRelease(rule, firing);
                break;
            case "stayPaused":
                rule.StayPaused = true;
                break;
            case "emit":
                var emitted = new JsonObject { { "kind", new JsonString(action.Emit!.Kind) }, { "payload", action.Emit.Payload ?? JsonNull.Instance } };
                (results.Emitted ??= new List<JsonValue>()).Add(emitted);
                _events.Publish(EventKinds.ExecEmit, new ExecEmitEventParams { Kind = action.Emit.Kind, Payload = action.Emit.Payload, Session = "rule:" + rule.Id }, rule.Context);
                break;
        }
    }

    private IEnumerable<object?> Screenshot(ArmedRule rule, RuleResults results, UnityLudometry.Protocol.Messages.CaptureOptions options)
    {
        lock (_gate)
        {
            if (!Take(_captures, Now().RealtimeMs, _limits.MaxCapturesPerMinute))
            {
                rule.SuspendReason = $"rules took more than {_limits.MaxCapturesPerMinute} screenshots in a minute (Rules.MaxCapturesPerMinute)";
                throw new ProtocolException(ErrorCodes.Busy, "Rules.MaxCapturesPerMinute was reached: the screenshot was skipped.");
            }
        }

        var copy = UnityLudometry.Protocol.Messages.CaptureOptions.Read(options.ToJson(), "screenshot");
        if (copy.Path is null && copy.OutDir is null)
        {
            copy.OutDir = rule.Definition.OutDir;
        }

        foreach (var step in _actors.Screenshots.CaptureScreen(Context(rule, Methods.ScreenshotCapture), copy))
        {
            if (step is ScreenshotCaptureResult shot)
            {
                (results.Screenshots ??= new List<CaptureResult>()).AddRange(shot.Items);
                (results.Files ??= new List<OutputFile>()).AddRange(shot.Items.Select(i => new OutputFile { Path = i.Path, Bytes = i.Bytes, Sha256 = i.Sha256 }));
                yield break;
            }

            yield return step;
        }
    }

    // Pauses for the rule (main thread). If the game is already paused, the rule doesn't hold that pause.
    private void Pause(ArmedRule rule, RuleFiring firing, PauseAction pause)
    {
        var control = _unity.Control ?? throw new ProtocolException(ErrorCodes.MainThreadUnavailable, "There is no Unity main thread (no game).");
        firing.Paused = true;
        if (control.TimeScale != 0 && !rule.HoldsPause)
        {
            _actors.Control.Pause();
            rule.HoldsPause = true;
            rule.PausedAtMs = Now().RealtimeMs;
            Audit(rule, Methods.TimePause, null, null);
        }

        if (pause.Audio == true && !control.AudioPaused)
        {
            control.AudioPaused = true;
            rule.PausedAudio = true;
        }
    }

    // Lets go of the rule's pause (main thread): resumes the game unless something else resumed it already.
    private bool ReleasePause(ArmedRule rule, RuleFiring? firing)
    {
        if (!rule.HoldsPause && !rule.PausedAudio)
        {
            return false;
        }

        var control = _unity.Control;
        var held = rule.HoldsPause;
        rule.HoldsPause = false;
        rule.HeldUntilMs = 0;
        if (held && control is not null && control.TimeScale == 0)
        {
            _actors.Control.Resume();
            Audit(rule, Methods.TimeResume, null, null);
        }

        if (rule.PausedAudio && control is not null)
        {
            control.AudioPaused = false;
        }

        rule.PausedAudio = false;
        if (held && firing is not null)
        {
            firing.ResumedAtFrame = _pump.Clock.FrameCount;
        }

        return held;
    }

    private bool TryRelease(ArmedRule rule, RuleFiring? firing)
    {
        try
        {
            return ReleasePause(rule, firing);
        }
        catch (Exception e)
        {
            Warn("RULE_ACTION_FAILED", $"Rule {rule.Id} couldn't resume the game: {e.Message}", rule);
            return false;
        }
    }

    // Releases a cancelled rule's pause on the main thread (its running actions, if any, release it themselves).
    private void ReleaseSoon(ArmedRule rule)
    {
        if (!rule.Firing && (rule.HoldsPause || rule.PausedAudio) && !_stopped)
        {
            _pump.Enqueue(new PumpWork(() => TryRelease(rule, null), _ => { }, _ => { }));
        }
    }

    private void ActionFailed(ArmedRule rule, int index, string kind, Exception error)
    {
        var inner = error is System.Reflection.TargetInvocationException { InnerException: { } e } ? e : error;
        var code = inner is ProtocolException p ? p.Code : ErrorCodes.GameException;
        var data = new JsonObject { { "ruleId", new JsonString(rule.Id) }, { "action", new JsonNumber(index) }, { "kind", new JsonString(kind) }, { "code", new JsonString(code) } };
        _warning("RULE_ACTION_FAILED", $"Rule {rule.Id}, action {index} ({kind}) failed: {inner.Message}", data);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------------

    private void CheckRoom(string? id)
    {
        if (id is not null && _rules.TryGetValue(id, out var existing) && existing.State == "armed")
        {
            throw ProtocolException.InvalidParams("params.id", $"A rule {id} is armed already.");
        }

        if (_rules.Values.Count(r => r.State == "armed") >= _limits.MaxActive)
        {
            throw new ProtocolException(ErrorCodes.Busy, $"{_limits.MaxActive} rules are armed already (Rules.MaxActive).");
        }
    }

    private string NextId()
    {
        string id;
        do
        {
            id = "r-" + ++_next;
        }
        while (_rules.ContainsKey(id));

        return id;
    }

    private void Prune()
    {
        var ended = _rules.Values.Where(r => r.State != "armed" && !r.Firing && !r.HoldsPause).OrderBy(r => r.Order).ToList();
        foreach (var rule in ended.Take(Math.Max(0, ended.Count - MaxEndedKept)))
        {
            _rules.Remove(rule.Id);
        }
    }

    private ArmedRule Find(string id) => _rules.TryGetValue(id, out var rule) ? rule : throw DataErrors.NotFound("params.ruleId", $"No rule {id}.");

    private RuleSummary Summary(ArmedRule rule)
    {
        var progress = new JsonObject();
        foreach (var node in rule.Root.All())
        {
            progress.Set(node.Path, node.Progress());
        }

        return new RuleSummary
        {
            Id = rule.Id,
            State = rule.State,
            Fires = rule.Fires,
            LastFire = rule.LastFireFrame,
            ConditionsProgress = progress,
            RequiredMode = rule.RequiredMode,
        };
    }

    private RuleClock Now()
    {
        var clock = _pump.Clock;
        return new RuleClock(clock.FrameCount, clock.Realtime * 1000, clock.Time * 1000);
    }

    // A sliding one-minute window: false (and nothing taken) when it is full.
    private static bool Take(Queue<double> window, double nowMs, int max)
    {
        while (window.Count > 0 && nowMs - window.Peek() >= 60_000)
        {
            window.Dequeue();
        }

        if (window.Count >= max)
        {
            return false;
        }

        window.Enqueue(nowMs);
        return true;
    }

    // One result, or several (in order) once an action kind ran more than once.
    private static JsonValue Append(JsonValue? existing, JsonValue value)
    {
        switch (existing)
        {
            case null:
                return value;
            case JsonArray many when many.Count > 0 && many[0] is JsonObject:
                many.Add(value);
                return many;
            default:
                return new JsonArray { existing, value };
        }
    }

    private RequestContext Context(ArmedRule rule, string method) =>
        new($"rule:{rule.Id}", method, null, rule.Context, "rule", "rule " + rule.Id, null, rule.Cancellation?.Token ?? CancellationToken.None);

    private void Audit(ArmedRule rule, string method, string? target, AuditedAssembly? assembly) =>
        _actors.Activity.Record(method, "rule", "rule " + rule.Id, mutating: true, DateTime.UtcNow, 0, new JsonObject { { "ruleId", new JsonString(rule.Id) } }, new JsonObject(), null, target, assembly);

    private void Warn(string code, string message, ArmedRule rule) => _warning(code, message, new JsonObject { { "ruleId", new JsonString(rule.Id) } });

    private void Answer(ArmedRule rule, Waiter waiter, RuleFiring? firing)
    {
        lock (_gate)
        {
            rule.Waiters.Remove(waiter);
        }

        waiter.Answer(firing);
    }

    private void Progress(ArmedRule rule, ConditionNode node, bool satisfied)
    {
        if (!_events.HasSubscribers(EventKinds.RuleProgress))
        {
            return;
        }

        var now = Now();
        _events.Publish(EventKinds.RuleProgress, new RuleProgressEventParams
        {
            RuleId = rule.Id,
            ConditionPath = node.Path,
            Satisfied = satisfied,
            Detail = satisfied ? node.Detail : null,
            Frame = now.Frame,
            RealtimeMs = (long)now.RealtimeMs,
        }, rule.Context);
    }

    /// <summary>A <c>rule.wait</c> in progress.</summary>
    private sealed class Waiter
    {
        private readonly Deferred _deferred;

        public Waiter(Deferred deferred) => _deferred = deferred;

        public Timer? Timer { get; set; }

        public CancellationTokenRegistration Cancellation { get; set; }

        public void Answer(RuleFiring? firing)
        {
            Timer?.Dispose();
            Cancellation.Dispose();
            _deferred.Complete(new RuleWaitResult { Firing = firing });
        }
    }

    /// <summary>One rule and everything it tracks.</summary>
    private sealed class ArmedRule : IConditionHost
    {
        private readonly RuleServices _owner;
        private readonly List<InstrumentationRecord> _hits = new();

        public ArmedRule(RuleServices owner, Rule definition, string ownerId, AgentMode requiredMode, ConditionNode root, Dictionary<int, SnippetEntry> snippets, JsonObject? context)
        {
            _owner = owner;
            Definition = definition;
            Owner = ownerId;
            RequiredMode = requiredMode;
            Root = root;
            Snippets = snippets;
            Context = context;
        }

        public string Id => Definition.Id ?? string.Empty;

        public Rule Definition { get; }

        public string Owner { get; }

        public bool Persistent => Definition.Persistent ?? false;

        public AgentMode RequiredMode { get; }

        public ConditionNode Root { get; }

        public Dictionary<int, SnippetEntry> Snippets { get; }

        public JsonObject? Context { get; }

        public int Order { get; set; }

        /// <summary><c>pending</c> until armed, then <c>armed</c>, <c>fired</c>, <c>expired</c>, <c>cancelled</c> or <c>suspended</c>.</summary>
        public string State { get; set; } = "pending";

        public bool Firing { get; set; }

        public long Fires { get; set; }

        public long? LastFireFrame { get; set; }

        public long ArmedAtFrame { get; set; }

        public double? ExpiresAtMs { get; set; }

        public double? CooldownUntilMs { get; set; }

        public long LogSeqArmed { get; set; }

        public long LogSeqLastFire { get; set; }

        public long HitSeqLastFire { get; set; }

        public long LastHitSeq { get; private set; }

        public string? SuspendReason { get; set; }

        public CancellationTokenSource? Cancellation { get; set; }

        public bool HoldsPause { get; set; }

        public bool PausedAudio { get; set; }

        public bool PausedImmediately { get; set; }

        public bool StayPaused { get; set; }

        public double PausedAtMs { get; set; }

        public double HeldUntilMs { get; set; }

        public List<RuleFiring> Firings { get; } = new();

        public List<Waiter> Waiters { get; } = new();

        public object Gate => _owner._gate;

        public bool Listening => State == "armed" && !Firing && CooldownUntilMs is null;

        public RuleClock Now => _owner.Now();

        public void Progress(ConditionNode node, bool satisfied) => _owner.Progress(this, node, satisfied);

        public void Completed(ConditionNode trigger, bool fromHook) => _owner.Fire(this, trigger, fromHook);

        public void Fault(ConditionNode node, string message) => _owner.Suspend(this, $"{node.Path}: {message}");

        public void RecordHit(InstrumentationRecord record)
        {
            lock (_hits)
            {
                _hits.Add(record);
                if (_hits.Count > MaxHitsKept)
                {
                    _hits.RemoveAt(0);
                }

                LastHitSeq = record.Seq;
            }
        }

        public List<InstrumentationRecord> HitsSince(long seq)
        {
            lock (_hits)
            {
                return _hits.Where(h => h.Seq > seq).ToList();
            }
        }
    }
}
