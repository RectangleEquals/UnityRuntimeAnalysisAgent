using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Abstractions;
using UnityRuntimeAnalysisAgent.Core.Code;
using UnityRuntimeAnalysisAgent.Core.Data;
using UnityRuntimeAnalysisAgent.Core.Dispatch;
using UnityRuntimeAnalysisAgent.Core.Instrumentation;
using UnityRuntimeAnalysisAgent.Core.Jobs;
using UnityRuntimeAnalysisAgent.Core.Live;
using UnityRuntimeAnalysisAgent.Core.Runtime;

namespace UnityRuntimeAnalysisAgent.Core.Session;

/// <summary>
/// Instrumentation over time: hooks, traces, profiles, patch-point verification, watches, event subscriptions and
/// exception monitoring. Everything is owned by the connection that created it (removed when it disconnects unless
/// persistent), removed by <c>instrumentation.clear</c>, and removed at shutdown; the agent's patches never outlive it.
/// </summary>
internal sealed class InstrumentationServices : IDisposable
{
    private readonly DataModel _data;
    private readonly CodeModel _code;
    private readonly MainThreadPump _pump;
    private readonly JobManager _jobs;
    private readonly bool _removeOnDisconnect;
    private readonly ModeController _modes;
    private readonly Dictionary<string, ITrigger> _triggers = new(StringComparer.Ordinal);
    private readonly Stopwatch _rateClock = Stopwatch.StartNew();
    private readonly object _rateGate = new();
    private double _rateAt;
    private long _rateRecords;

    public InstrumentationServices(DataModel data, CodeModel code, MainThreadPump pump, JobManager jobs, EventHub events, IUnityApi unity, ModeController modes,
        int maxMethods, bool removeOnDisconnect, string agentVersion, Action<string, string, JsonObject> warning)
    {
        _modes = modes;
        _data = data;
        _code = code;
        _pump = pump;
        _jobs = jobs;
        _removeOnDisconnect = removeOnDisconnect;
        Instrumenter = new Instrumenter(pump, maxMethods, warning) { TypeSeen = data.StaticInit.Observe };
        var records = new RecordBuilder(data);
        Hooks = new HookManager(Instrumenter, records, new ConditionEvaluator(data), events);
        Traces = new TraceManager(Instrumenter, records, pump, events, agentVersion);
        Profiles = new ProfileManager(Instrumenter, records, pump, agentVersion);
        Watches = new WatchManager(data, events, pump);
        Subscriptions = new SubscriptionManager(data, events, pump);
        Exceptions = new ExceptionMonitor(unity, events, pump);
        Records = records;
        RegisterTrigger(new InvokeTrigger(data));
    }

    public Instrumenter Instrumenter { get; }

    public HookManager Hooks { get; }

    public TraceManager Traces { get; }

    public ProfileManager Profiles { get; }

    public WatchManager Watches { get; }

    public SubscriptionManager Subscriptions { get; }

    public ExceptionMonitor Exceptions { get; }

    private RecordBuilder Records { get; }

    /// <summary>Adds a trigger kind for <c>hook.verify</c> (snippets and UI clicks register theirs later).</summary>
    public void RegisterTrigger(ITrigger trigger) => _triggers[trigger.Kind] = trigger;

    // ---- hooks ---------------------------------------------------------------------------------------------------------

    [RpcMethod(Methods.HookAdd)]
    public ProtocolMessage HookAdd(RequestContext context, HookAddParams p) => Hooks.Add(Method(p.Method, "params.method"), p, Owner(context));

    [RpcMethod(Methods.HookRemove)]
    public ProtocolMessage HookRemove(RequestContext context, HookRemoveParams p) => Hooks.Remove(p.HookId);

    [RpcMethod(Methods.HookList)]
    public ProtocolMessage HookList(RequestContext context) => new HookListResult { Items = Hooks.List() };

    [RpcMethod(Methods.HookHits)]
    public ProtocolMessage HookHits(RequestContext context, HookHitsParams p) => Hooks.Hits(p.HookId, Math.Max(0, p.SinceSeq ?? 0), (int)Math.Max(1, Math.Min(10_000, p.Limit ?? 100)));

    [RpcMethod(Methods.HooksClear)]
    public ProtocolMessage HooksClear(RequestContext context, HooksClearParams p) =>
        new HooksClearResult { Removed = Hooks.Clear(p.Owner == "all" ? null : Owner(context)) };

    // ---- verification ----------------------------------------------------------------------------------------------------

    [RpcMethod(Methods.HookVerify, DefaultTimeoutMs = 30_000, MaxTimeoutMs = 600_000)]
    public Deferred HookVerify(RequestContext context, HookVerifyParams p)
    {
        var method = Method(p.Method, "params.method");
        var trigger = p.Trigger;
        // An active trigger runs game code (a runtime-checked escalation: the method itself is ReadOnly).
        if (trigger is not null && trigger.Kind != "wait" && _modes.Current != AgentMode.Full)
        {
            throw AgentErrors.ModeForbidden(Methods.HookVerify + $" with a {trigger.Kind} trigger", AgentMode.Full, _modes.Current);
        }

        if (trigger is not null && trigger.Kind != "wait" && !_triggers.ContainsKey(trigger.Kind))
        {
            throw DataErrors.Unsupported($"The trigger kind '{trigger.Kind}' isn't available in this agent (only invoke and wait).");
        }

        var sink = new VerifySink(Records, Instrumenter);
        Instrumenter.Attach(method, sink, force: false);
        var deferred = new Deferred();
        _pump.Enqueue(new PumpWork(
            () => Verify(method, sink, p),
            result => deferred.Complete((ProtocolMessage)result!),
            error =>
            {
                Instrumenter.Detach(method, sink);
                deferred.Fail(error);
            },
            context.Cancellation));
        return deferred;
    }

    // ---- traces and profiles ---------------------------------------------------------------------------------------------

    [RpcMethod(Methods.TraceStart)]
    public ProtocolMessage TraceStart(RequestContext context, TraceStartParams p)
    {
        if (!Path.IsPathRooted(p.OutFile))
        {
            throw ProtocolException.InvalidParams("params.outFile", "outFile must be an absolute path.");
        }

        var methods = TraceManager.Expand((p.Methods ?? new List<Anchor>()).Select((a, i) => Method(a, $"params.methods[{i}]")), p.Include, _code, Instrumenter.MaxMethods);
        var stopOn = p.Stop?.OnMethod is { } anchor ? Method(anchor, "params.stop.onMethod") : null;
        return _jobs.Start("trace", job => Traces.Run(job, methods, p, stopOn), context.Context);
    }

    [RpcMethod(Methods.TraceStop)]
    public ProtocolMessage TraceStop(RequestContext context, TraceStopParams p)
    {
        _jobs.Get(p.JobId); // NOT_FOUND for an unknown job
        return new TraceStopResult { Stopped = Traces.Stop(p.JobId) };
    }

    [RpcMethod(Methods.ProfileStart)]
    public ProtocolMessage ProfileStart(RequestContext context, ProfileStartParams p)
    {
        if (p.OutFile is not null && !Path.IsPathRooted(p.OutFile))
        {
            throw ProtocolException.InvalidParams("params.outFile", "outFile must be an absolute path.");
        }

        var methods = p.Methods.Select((a, i) => Method(a, $"params.methods[{i}]")).Distinct().ToList();
        foreach (var method in methods)
        {
            if (Instrumenter.Refusal(method, force: false) is { } reason)
            {
                throw new ProtocolException(ErrorCodes.Unsupported, $"{AnchorWriter.MemberName(method)} can't be instrumented: {reason}");
            }
        }

        return _jobs.Start("profile", job => Profiles.Run(job, methods, p), context.Context);
    }

    // ---- watches and event subscriptions ----------------------------------------------------------------------------------

    [RpcMethod(Methods.WatchAdd)]
    public ProtocolMessage WatchAdd(RequestContext context, WatchAddParams p) => Watches.Add(p, Owner(context));

    [RpcMethod(Methods.WatchRemove)]
    public ProtocolMessage WatchRemove(RequestContext context, WatchRemoveParams p) => Watches.Remove(p.WatchId);

    [RpcMethod(Methods.WatchList)]
    public ProtocolMessage WatchList(RequestContext context) => new WatchListResult { Items = Watches.List() };

    [RpcMethod(Methods.WatchChanges)]
    public ProtocolMessage WatchChanges(RequestContext context, WatchChangesParams p) => Watches.Changes(p.WatchId, Math.Max(0, p.SinceFrame ?? 0), (int)Math.Max(1, Math.Min(10_000, p.Limit ?? 100)));

    [RpcMethod(Methods.EventSubscribe)]
    public ProtocolMessage EventSubscribe(RequestContext context, EventSubscribeParams p) => Subscriptions.Subscribe(p, Owner(context));

    [RpcMethod(Methods.EventUnsubscribe)]
    public ProtocolMessage EventUnsubscribe(RequestContext context, EventUnsubscribeParams p) => Subscriptions.Unsubscribe(p.SubscriptionId);

    // ---- exceptions, status, clear ------------------------------------------------------------------------------------------

    [RpcMethod(Methods.ExceptionsMonitor)]
    public ProtocolMessage ExceptionsMonitor(RequestContext context, ExceptionsMonitorParams p)
    {
        try
        {
            return Exceptions.Configure(p);
        }
        catch (ArgumentException e)
        {
            throw ProtocolException.InvalidParams("params.filter.typeRegex", $"params.filter.typeRegex isn't a valid regular expression: {e.Message}");
        }
    }

    [RpcMethod(Methods.InstrumentationStatus)]
    public ProtocolMessage Status(RequestContext context) => new InstrumentationStatusResult
    {
        Hooks = Hooks.Count,
        Traces = Traces.Count,
        Profiles = Profiles.Count,
        Watches = Watches.Count,
        Subscriptions = Subscriptions.Count,
        PatchedMethods = Instrumenter.PatchedMethods,
        HitsPerSecond = HitsPerSecond(),
        BufferedRecords = Hooks.Buffered,
    };

    [RpcMethod(Methods.InstrumentationClear)]
    public ProtocolMessage Clear(RequestContext context) => ClearAll();

    /// <summary>A connection closed: its non-persistent instrumentation goes (when configured).</summary>
    public void OnDisconnect(string owner)
    {
        if (_removeOnDisconnect)
        {
            Hooks.Clear(owner, keepPersistent: true);
            Watches.Clear(owner, keepPersistent: true);
            Subscriptions.Clear(owner, keepPersistent: true);
        }
    }

    /// <summary>Removes everything (shutdown): no agent patch survives the agent.</summary>
    public void Dispose()
    {
        ClearAll();
        Exceptions.Dispose();
        Instrumenter.Dispose();
    }

    private InstrumentationClearResult ClearAll() => new()
    {
        Hooks = Hooks.Clear(null),
        Traces = Traces.StopAll(),
        Profiles = Profiles.StopAll(),
        Watches = Watches.Clear(null),
        Subscriptions = Subscriptions.Clear(null),
    };

    private IEnumerable Verify(MethodBase method, VerifySink sink, HookVerifyParams p)
    {
        var reasonsFromTrigger = new List<string>();
        try
        {
            var trigger = p.Trigger;
            if (trigger is not null && trigger.Kind != "wait" && _triggers[trigger.Kind].Fire(trigger, "params.trigger") is { } note)
            {
                reasonsFromTrigger.Add(note);
            }

            var waitFrames = Math.Max(1, p.WaitFrames ?? 120);
            var timeoutMs = Math.Max(1, p.TimeoutMs ?? 10_000);
            var clock = Stopwatch.StartNew();
            for (var frame = 0; sink.Hits == 0 && frame < waitFrames && clock.ElapsedMilliseconds < timeoutMs; frame++)
            {
                yield return PumpWait.NextFrame;
            }
        }
        finally
        {
            Instrumenter.Detach(method, sink);
        }

        var (ilSize, risk, reasons) = InliningHeuristic.Assess(method);
        reasons.AddRange(reasonsFromTrigger);
        var fired = sink.Hits > 0;
        var result = new HookVerifyResult { Fired = fired, Hits = sink.Hits, FirstHit = sink.First, IlSize = ilSize, InliningRisk = risk, Reasons = reasons };
        if (!fired)
        {
            result.Recommendation = risk == "high"
                ? "The method didn't fire and is likely inlined: patch a caller instead (see callerCandidates)."
                : "The method didn't fire: check that the trigger reaches it (or wait longer), then patch a caller if it's inlined.";
            result.CallerCandidates = _code.Xrefs.Referencing(method, XrefKind.Call, default).Select(x => x.Method).Distinct().Take(10).Select(m => AnchorWriter.ForMember(m)).ToList();
        }

        yield return result;
    }

    private double HitsPerSecond()
    {
        lock (_rateGate)
        {
            var now = _rateClock.Elapsed.TotalSeconds;
            var records = Instrumenter.Records;
            var rate = now - _rateAt > 0 ? (records - _rateRecords) / (now - _rateAt) : 0;
            (_rateAt, _rateRecords) = (now, records);
            return Math.Round(rate, 1);
        }
    }

    private MethodBase Method(Anchor anchor, string param) =>
        _data.Anchors.ResolveMember(anchor, param) as MethodBase ?? throw ProtocolException.InvalidParams(param, $"{param} must be a method or constructor.");

    private static string Owner(RequestContext context) => context.Connection?.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "local";

    // hook.verify's built-in trigger: call a method (the target's, or a static one) with decoded arguments.
    private sealed class InvokeTrigger : ITrigger
    {
        private readonly DataModel _data;

        public InvokeTrigger(DataModel data) => _data = data;

        public string Kind => "invoke";

        public string? Fire(Trigger trigger, string param)
        {
            var method = trigger.Method is null ? throw ProtocolException.InvalidParams(param + ".method", "An invoke trigger needs a method.")
                : _data.Anchors.ResolveMember(trigger.Method, param + ".method") as MethodInfo ?? throw ProtocolException.InvalidParams(param + ".method", "The trigger's method must be a method.");
            object? instance = null;
            if (trigger.Target is not null)
            {
                var resolved = _data.Targets.Resolve(trigger.Target, param + ".target");
                if (!resolved.IsStatic)
                {
                    instance = resolved.Value;
                    method = AnchorResolver.BindTo(method, resolved.Type) as MethodInfo ?? method;
                }
            }

            var parameters = SafeReflection.Parameters(method) ?? throw DataErrors.Unsupported("The trigger's method is an engine internal call.");
            var args = trigger.Args ?? new List<JsonValue>();
            var live = parameters.Select((p, i) => i < args.Count ? _data.Reader.Read(args[i], p.ParameterType, $"{param}.args[{i}]") : p.HasDefaultValue ? p.DefaultValue : null).ToArray();
            try
            {
                method.Invoke(instance, live);
                return null;
            }
            catch (TargetInvocationException e)
            {
                var inner = e.InnerException ?? e;
                return $"the trigger threw {inner.GetType().Name}: {inner.Message}";
            }
        }
    }
}
