using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Abstractions;
using UnityRuntimeAnalysisAgent.Core.Data;
using UnityRuntimeAnalysisAgent.Core.Diagnostics;
using UnityRuntimeAnalysisAgent.Core.Execution;
using UnityRuntimeAnalysisAgent.Core.Instrumentation;
using UnityRuntimeAnalysisAgent.Core.Live;

namespace UnityRuntimeAnalysisAgent.Core.Rules;

/// <summary>"Now", as conditions measure it: the frame, real time and game (scaled) time.</summary>
internal readonly struct RuleClock
{
    public RuleClock(long frame, double realtimeMs, double gameTimeMs)
    {
        Frame = frame;
        RealtimeMs = realtimeMs;
        GameTimeMs = gameTimeMs;
    }

    public long Frame { get; }

    public double RealtimeMs { get; }

    public double GameTimeMs { get; }
}

/// <summary>What a rule's conditions report to (the armed rule).</summary>
internal interface IConditionHost
{
    /// <summary>The lock every condition change happens under.</summary>
    object Gate { get; }

    /// <summary>False once the rule stopped listening (ended, or its actions are running).</summary>
    bool Listening { get; }

    RuleClock Now { get; }

    /// <summary>A sub-condition was satisfied (or, for a restarted sequence, no longer is).</summary>
    void Progress(ConditionNode node, bool satisfied);

    /// <summary>The whole condition was satisfied; <paramref name="trigger"/> is the leaf that completed it.</summary>
    void Completed(ConditionNode trigger, bool fromHook);

    /// <summary>A condition can't be evaluated any more (the rule is suspended).</summary>
    void Fault(ConditionNode node, string message);

    /// <summary>A call seen by one of the rule's hook conditions (for the <c>hits</c> action).</summary>
    void RecordHit(InstrumentationRecord record);
}

/// <summary>The agent's parts conditions observe.</summary>
internal sealed class RuleSources
{
    public RuleSources(DataModel data, IUnityApi unity, LogBuffer logs, HookManager hooks, SnippetRunner snippets, Func<long, IReadOnlyList<UiElementFacts>> uiSnapshot)
    {
        Data = data;
        Unity = unity;
        Logs = logs;
        Hooks = hooks;
        Snippets = snippets;
        UiSnapshot = uiSnapshot;
        Evaluator = new ConditionEvaluator(data);
    }

    public DataModel Data { get; }

    public IUnityApi Unity { get; }

    public LogBuffer Logs { get; }

    public HookManager Hooks { get; }

    public SnippetRunner Snippets { get; }

    public ConditionEvaluator Evaluator { get; }

    /// <summary>The visible uGUI elements, read at most once per frame for all rules.</summary>
    public Func<long, IReadOnlyList<UiElementFacts>> UiSnapshot { get; }
}

/// <summary>
/// One node of a rule's condition tree. A node is started (measuring from then), may be satisfied (latched until it is
/// started again), and is stopped when its result no longer matters. Leaves observe the game; combinators combine
/// their children. Everything runs under the host's lock; per-frame work happens in <see cref="Poll"/>.
/// </summary>
internal abstract class ConditionNode
{
    protected ConditionNode(string path) => Path = path;

    /// <summary>Where the node is in the rule (<c>when.seq[1].hook</c>).</summary>
    public string Path { get; }

    public ConditionNode? Parent { get; private set; }

    public IConditionHost Host { get; private set; } = null!;

    public bool Active { get; private set; }

    public bool Satisfied { get; private set; }

    /// <summary>The frame of the latest satisfaction (-1 before).</summary>
    public long SatisfiedFrame { get; private set; } = -1;

    /// <summary>What the latest occurrence was (leaves).</summary>
    public JsonValue? Detail { get; protected set; }

    public virtual IReadOnlyList<ConditionNode> Children => Array.Empty<ConditionNode>();

    /// <summary>Hooks the tree to its host (after building).</summary>
    public void Bind(IConditionHost host, ConditionNode? parent)
    {
        Host = host;
        Parent = parent;
        foreach (var child in Children)
        {
            child.Bind(host, this);
        }
    }

    /// <summary>Starts measuring from <paramref name="now"/> (clears the latch).</summary>
    public virtual void Start(RuleClock now)
    {
        Active = true;
        Satisfied = false;
        SatisfiedFrame = -1;
    }

    /// <summary>Starts again after an occurrence (<c>count</c>, <c>not</c>): like <see cref="Start"/>, except that a state
    /// keeps what it last saw, so it counts again only when it becomes true again.</summary>
    public virtual void Rearm(RuleClock now) => Start(now);

    /// <summary>Stops measuring (and every child).</summary>
    public virtual void Stop()
    {
        Active = false;
        foreach (var child in Children)
        {
            child.Stop();
        }
    }

    /// <summary>Per-frame work while active (polled leaves, time windows).</summary>
    public virtual void Poll(RuleClock now)
    {
    }

    /// <summary>Whether the node holds in this frame (for <c>all</c> with <c>simultaneous</c>): a state that is true now,
    /// or an occurrence in this frame.</summary>
    public virtual bool Holds(RuleClock now) => Satisfied && SatisfiedFrame == now.Frame;

    /// <summary>Starts observing the game (main thread). Throws the reason when it can't.</summary>
    public virtual void Attach()
    {
        foreach (var child in Children)
        {
            child.Attach();
        }
    }

    /// <summary>Stops observing the game (main thread, or shutdown).</summary>
    public virtual void Detach()
    {
        foreach (var child in Children)
        {
            child.Detach();
        }
    }

    /// <summary>This node's progress for <c>rule.list</c>.</summary>
    public virtual JsonObject Progress()
    {
        var progress = new JsonObject { { "satisfied", JsonValue.From(Satisfied) } };
        if (SatisfiedFrame >= 0)
        {
            progress.Set("frame", new JsonNumber(SatisfiedFrame));
        }

        return progress;
    }

    /// <summary>Every node, depth first.</summary>
    public IEnumerable<ConditionNode> All()
    {
        yield return this;
        foreach (var node in Children.SelectMany(c => c.All()))
        {
            yield return node;
        }
    }

    /// <summary>Latches the node and tells its parent (or the host, at the root). Under the host's lock.</summary>
    protected void Satisfy(RuleClock now, ConditionNode trigger, bool fromHook)
    {
        Satisfied = true;
        SatisfiedFrame = now.Frame;
        if (Parent is null)
        {
            Host.Completed(trigger, fromHook);
            return;
        }

        Host.Progress(this, true);
        Parent.ChildSatisfied(this, trigger, now, fromHook);
    }

    /// <summary>A child was satisfied.</summary>
    protected virtual void ChildSatisfied(ConditionNode child, ConditionNode trigger, RuleClock now, bool fromHook)
    {
    }

    /// <summary>Clears the latch without stopping (a sequence restarting).</summary>
    protected void Reset() => Satisfied = false;
}

/// <summary>A leaf: something observed in the game.</summary>
internal abstract class LeafNode : ConditionNode
{
    protected LeafNode(string path) : base(path)
    {
    }

    /// <summary>The leaf occurred (from any thread): latched and reported while the rule listens.</summary>
    protected void Occur(JsonValue? detail, bool fromHook = false)
    {
        lock (Host.Gate)
        {
            if (!Active || !Host.Listening)
            {
                return;
            }

            Detail = detail;
            Satisfy(Host.Now, this, fromHook);
        }
    }
}

/// <summary>A leaf that is a state (true or false when polled): occurs when it becomes true, holds while it is.</summary>
internal abstract class LevelNode : LeafNode
{
    private readonly int _everyFrames;
    private long _startFrame;
    private bool _truth;

    protected LevelNode(string path, int everyFrames) : base(path) => _everyFrames = Math.Max(1, everyFrames);

    public override void Start(RuleClock now)
    {
        base.Start(now);
        _startFrame = now.Frame;
        _truth = false; // a state already true when measuring starts counts
    }

    public override void Rearm(RuleClock now)
    {
        var truth = _truth;
        Start(now);
        _truth = truth;
    }

    public override void Poll(RuleClock now)
    {
        if ((now.Frame - _startFrame) % _everyFrames != 0)
        {
            return;
        }

        var (truth, detail) = Evaluate(now);
        var rising = truth && !_truth;
        _truth = truth;
        if (rising && Host.Listening)
        {
            Detail = detail;
            Satisfy(now, this, false);
        }
    }

    public override bool Holds(RuleClock now) => _truth;

    /// <summary>The state now, and what to report when it becomes true.</summary>
    protected abstract (bool Truth, JsonValue? Detail) Evaluate(RuleClock now);
}

// ---- leaves ------------------------------------------------------------------------------------------------------------

/// <summary><c>scene</c>: a scene loaded or unloaded (events), or is the active one (a state).</summary>
internal sealed class SceneNode : LevelNode
{
    private readonly RuleSources _sources;
    private readonly string _change;
    private readonly string _name;
    private readonly Regex _pattern;

    public SceneNode(string path, SceneCondition c, RuleSources sources) : base(path, 1)
    {
        _sources = sources;
        (_change, _name) = c.Loaded is { } loaded ? ("loaded", loaded) : c.Unloaded is { } unloaded ? ("unloaded", unloaded) : ("active", c.Active!);
        _pattern = new Regex("^(?:" + _name + ")$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    public override void Attach() => _sources.Unity.SceneChanged += OnSceneChanged;

    public override void Detach() => _sources.Unity.SceneChanged -= OnSceneChanged;

    public override void Poll(RuleClock now)
    {
        if (_change == "active")
        {
            base.Poll(now);
        }
    }

    public override bool Holds(RuleClock now) => _change == "active" ? base.Holds(now) : Satisfied && SatisfiedFrame == now.Frame;

    protected override (bool Truth, JsonValue? Detail) Evaluate(RuleClock now)
    {
        var active = _sources.Unity.Scenes().FirstOrDefault(s => s.IsActive);
        return active is not null && Matches(active) ? (true, Describe(active, "active")) : (false, null);
    }

    private void OnSceneChanged(SceneChange change)
    {
        if (change.Change == _change && Matches(change.Scene))
        {
            Occur(Describe(change.Scene, change.Change));
        }
    }

    private bool Matches(SceneFacts scene)
    {
        try
        {
            return scene.Name == _name || scene.Path == _name || _pattern.IsMatch(scene.Name);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static JsonObject Describe(SceneFacts scene, string change) => new()
    {
        { "kind", new JsonString("scene") },
        { "change", new JsonString(change) },
        { "scene", new JsonString(scene.Name) },
        { "buildIndex", new JsonNumber(scene.BuildIndex) },
    };
}

/// <summary><c>delay</c>: real time, game time or frames since the node started.</summary>
internal sealed class DelayNode : LeafNode
{
    private readonly DelayCondition _c;
    private RuleClock _start;
    private bool _done;

    public DelayNode(string path, DelayCondition c) : base(path) => _c = c;

    public override void Start(RuleClock now)
    {
        base.Start(now);
        _start = now;
        _done = false;
    }

    public override void Poll(RuleClock now)
    {
        if (_done)
        {
            return;
        }

        var (elapsed, needed, unit) = _c.RealtimeMs is { } ms ? (now.RealtimeMs - _start.RealtimeMs, (double)ms, "realtimeMs")
            : _c.GameTimeMs is { } game ? (now.GameTimeMs - _start.GameTimeMs, (double)game, "gameTimeMs")
            : ((double)(now.Frame - _start.Frame), (double)_c.Frames!.Value, "frames");
        if (elapsed >= needed && Host.Listening)
        {
            _done = true;
            Detail = new JsonObject { { "kind", new JsonString("delay") }, { unit, new JsonNumber(Math.Round(elapsed)) } };
            Satisfy(now, this, false);
        }
    }
}

/// <summary><c>value</c>: a member path on a target, compared with an operator or watched for changes (polled).</summary>
internal sealed class ValueNode : LevelNode
{
    private readonly ValueCondition _c;
    private readonly RuleSources _sources;
    private string? _last;

    public ValueNode(string path, ValueCondition c, RuleSources sources) : base(path, (int)Math.Min(c.EveryFrames ?? 5, 100_000))
    {
        _c = c;
        _sources = sources;
    }

    public override void Start(RuleClock now)
    {
        base.Start(now);
        _last = null;
    }

    protected override (bool Truth, JsonValue? Detail) Evaluate(RuleClock now)
    {
        object? value;
        try
        {
            value = _sources.Data.Targets.Resolve(_c.Target, _c.Path, Path + ".target", Path + ".path").Value;
            if (value is not null && _sources.Unity.IsDestroyed(value))
            {
                value = null;
            }
        }
        catch (Exception e) when (e is ProtocolException or Dispatch.GameCodeException)
        {
            return (false, null); // the target isn't there (yet): not true
        }

        var encoded = _sources.Data.Writer(new ViewOptions { Safe = true, Depth = 0, MaxString = 256, MaxItems = 16, MaxMembers = 16 }, now.Frame).Write(value, new Place());
        var detail = new JsonObject { { "kind", new JsonString("value") }, { "value", encoded } };
        if (_c.Changed == true)
        {
            var text = encoded.ToString();
            var changed = _last is not null && !string.Equals(_last, text, StringComparison.Ordinal);
            _last = text;
            return (changed, detail);
        }

        try
        {
            return (_sources.Evaluator.Test(value, _c.Op!, _c.Value, Path + ".value"), detail);
        }
        catch (Exception e) when (e is ProtocolException or InvalidCastException or ArgumentException or FormatException)
        {
            return (false, null);
        }
    }
}

/// <summary><c>hook</c>: calls of a method (enter, exit or throw), counted, optionally only those meeting conditions on
/// their arguments or instance. The method is instrumented while the rule is armed.</summary>
internal sealed class HookNode : LeafNode, IMethodSink, IFaultAware
{
    private static readonly object Token = new();
    private readonly MethodBase _method;
    private readonly string _phase;
    private readonly long _count;
    private readonly IReadOnlyList<Condition>? _where;
    private readonly RuleSources _sources;
    private readonly Instrumentation.CaptureOptions _capture = new() { Args = true, Result = true, Depth = 1 };
    private long _hits;
    private bool _attached;

    public HookNode(string path, HookCondition c, MethodBase method, RuleSources sources) : base(path)
    {
        _method = method;
        _phase = c.Phase ?? "enter";
        _count = Math.Max(1, c.Count ?? 1);
        _where = c.Where is { Count: > 0 } where ? where : null;
        _sources = sources;
    }

    public override void Start(RuleClock now)
    {
        base.Start(now);
        Interlocked.Exchange(ref _hits, 0);
    }

    public override void Attach()
    {
        _sources.Hooks.Instrumenter.Attach(_method, this, force: false);
        _attached = true;
    }

    public override void Detach()
    {
        if (_attached)
        {
            _attached = false;
            _sources.Hooks.Instrumenter.Detach(_method, this);
        }
    }

    public object? Enter(CallContext call)
    {
        if (!Active || !Host.Listening || (_where is not null && !_sources.Hooks.Matches(_where, call)))
        {
            return null;
        }

        if (_phase == "enter")
        {
            Hit(_sources.Hooks.Records.Build(_sources.Hooks.Instrumenter, call, "enter", _capture));
        }

        return Token;
    }

    public void Exit(CallContext call, object token, object? result, bool hasResult, long elapsedTicks)
    {
        if (_phase == "exit")
        {
            Hit(_sources.Hooks.Records.Build(_sources.Hooks.Instrumenter, call, "exit", _capture, result, hasResult, elapsedTicks: elapsedTicks));
        }
    }

    public void Throw(CallContext call, object token, Exception exception, long elapsedTicks)
    {
        if (_phase == "throw")
        {
            Hit(_sources.Hooks.Records.Build(_sources.Hooks.Instrumenter, call, "throw", _capture, exception: exception, elapsedTicks: elapsedTicks));
        }
    }

    public void Faulted(Exception error)
    {
        _attached = false;
        lock (Host.Gate)
        {
            Host.Fault(this, $"its hook on {AnchorWriter.MemberName(_method)} failed: {error.GetType().Name}: {error.Message}");
        }
    }

    public override JsonObject Progress()
    {
        var progress = base.Progress();
        progress.Set("hits", new JsonNumber(Interlocked.Read(ref _hits)));
        progress.Set("count", new JsonNumber(_count));
        return progress;
    }

    private void Hit(InstrumentationRecord record)
    {
        Host.RecordHit(record);
        var hits = Interlocked.Increment(ref _hits);
        if (hits == _count)
        {
            Occur(new JsonObject
            {
                { "kind", new JsonString("hook") },
                { "method", new JsonString(AnchorWriter.MemberName(_method)) },
                { "phase", new JsonString(_phase) },
                { "seq", new JsonNumber(record.Seq) },
                { "threadId", new JsonNumber(record.ThreadId) },
            }, fromHook: true);
        }
    }
}

/// <summary><c>event</c>: a C# event raised, or a UnityEvent invoked (an anchor to a UnityEvent field or property).</summary>
internal sealed class EventNode : LeafNode
{
    private readonly EventCondition _c;
    private readonly RuleSources _sources;
    private Action? _detach;

    public EventNode(string path, EventCondition c, RuleSources sources) : base(path)
    {
        _c = c;
        _sources = sources;
    }

    public override void Attach()
    {
        var data = _sources.Data;
        var member = data.Anchors.ResolveMember(_c.Event, Path + ".event");
        object? instance = null;
        if (_c.Target is not null)
        {
            var resolved = data.Targets.Resolve(_c.Target, Path + ".target");
            if (!resolved.IsStatic)
            {
                member = AnchorResolver.BindTo(member, resolved.Type) ?? throw ProtocolException.InvalidParams(Path + ".event", $"{AnchorWriter.TypeName(resolved.Type)} has no {member.Name}.");
                instance = resolved.Value;
            }
        }

        Action<object?[]> callback = args => Occur(new JsonObject
        {
            { "kind", new JsonString("event") },
            { "event", new JsonString(member.Name) },
            { "args", new JsonNumber(args.Length) },
        });

        if (member is EventInfo info)
        {
            if (instance is null && !(info.GetAddMethod(true)?.IsStatic ?? false))
            {
                throw ProtocolException.InvalidParams(Path + ".target", "An instance event needs a target.");
            }

            var handler = SubscriptionManager.Handler(info.EventHandlerType!, callback);
            info.GetAddMethod(true)!.Invoke(instance, new object[] { handler });
            var remove = info.GetRemoveMethod(true)!;
            _detach = () => remove.Invoke(instance, new object[] { handler });
            return;
        }

        var unityEvent = member switch
        {
            FieldInfo field => field.GetValue(instance),
            PropertyInfo property => property.GetValue(instance, null),
            _ => throw ProtocolException.InvalidParams(Path + ".event", "The event must be a C# event, or a UnityEvent field or property."),
        } ?? throw ProtocolException.InvalidParams(Path + ".event", "The UnityEvent is null.");
        var addListener = unityEvent.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(m => m.Name == "AddListener" && m.GetParameters().Length == 1 && typeof(Delegate).IsAssignableFrom(m.GetParameters()[0].ParameterType))
            ?? throw ProtocolException.InvalidParams(Path + ".event", $"{unityEvent.GetType().FullName} isn't a UnityEvent (no AddListener).");
        var listenerType = addListener.GetParameters()[0].ParameterType;
        var removeListener = unityEvent.GetType().GetMethod("RemoveListener", new[] { listenerType });
        var listener = SubscriptionManager.Handler(listenerType, callback);
        addListener.Invoke(unityEvent, new object[] { listener });
        _detach = () => removeListener?.Invoke(unityEvent, new object[] { listener });
    }

    public override void Detach()
    {
        try
        {
            _detach?.Invoke();
        }
        catch (TargetInvocationException)
        {
            // The event's owner is gone (destroyed): nothing to detach from.
        }

        _detach = null;
    }
}

/// <summary><c>log</c>: an entry in the unified log (pattern, minimum level, source).</summary>
internal sealed class LogNode : LeafNode
{
    private readonly RuleSources _sources;
    private readonly Regex _regex;
    private readonly int _minRank;
    private readonly string[]? _source;

    public LogNode(string path, LogCondition c, RuleSources sources) : base(path)
    {
        _sources = sources;
        _regex = RuleCheck.Pattern(c.Regex, path + ".regex");
        _minRank = c.MinLevel is null ? 0 : Math.Max(0, LogBuffer.Rank(c.MinLevel));
        _source = c.Source is null ? null : new[] { c.Source };
    }

    public override void Attach() => _sources.Logs.Added += OnLog;

    public override void Detach() => _sources.Logs.Added -= OnLog;

    private void OnLog(LogEntry entry)
    {
        bool matches;
        try
        {
            matches = Active && LogBuffer.Matches(entry, _minRank, _source, null, _regex);
        }
        catch (RegexMatchTimeoutException)
        {
            return;
        }

        if (matches)
        {
            Occur(new JsonObject
            {
                { "kind", new JsonString("log") },
                { "seq", new JsonNumber(entry.Seq) },
                { "level", new JsonString(entry.Level) },
                { "message", new JsonString(entry.Message.Length > 512 ? entry.Message.Substring(0, 512) : entry.Message) },
            });
        }
    }
}

/// <summary><c>exception</c>: an exception Unity logs (unhandled ones, and those the game logs).</summary>
internal sealed class ExceptionNode : LeafNode
{
    private readonly RuleSources _sources;
    private readonly Regex? _type;
    private readonly Regex? _message;

    public ExceptionNode(string path, ExceptionCondition c, RuleSources sources) : base(path)
    {
        _sources = sources;
        _type = c.TypeRegex is null ? null : RuleCheck.Pattern(c.TypeRegex, path + ".typeRegex");
        _message = c.MessageRegex is null ? null : RuleCheck.Pattern(c.MessageRegex, path + ".messageRegex");
    }

    public override void Attach() => _sources.Logs.Added += OnLog;

    public override void Detach() => _sources.Logs.Added -= OnLog;

    private void OnLog(LogEntry entry)
    {
        if (!Active || entry.Source != "unity" || entry.Level != "exception")
        {
            return;
        }

        var (type, message) = ExceptionMonitor.ParseLogged(entry.Message);
        try
        {
            if ((_type is not null && !_type.IsMatch(type)) || (_message is not null && !_message.IsMatch(message)))
            {
                return;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return;
        }

        Occur(new JsonObject
        {
            { "kind", new JsonString("exception") },
            { "type", new JsonString(type) },
            { "message", new JsonString(message.Length > 512 ? message.Substring(0, 512) : message) },
            { "seq", new JsonNumber(entry.Seq) },
        });
    }
}

/// <summary><c>ui</c>: a visible uGUI element (by text pattern, path, kind) appears, disappears or becomes interactable
/// (polled every 5 frames).</summary>
internal sealed class UiNode : LevelNode
{
    private readonly UiCondition _c;
    private readonly RuleSources _sources;
    private readonly Regex? _text;
    private bool _seen;

    public UiNode(string path, UiCondition c, RuleSources sources) : base(path, 5)
    {
        _c = c;
        _sources = sources;
        _text = c.Text is null ? null : RuleCheck.Pattern(c.Text, path + ".text");
    }

    public override void Start(RuleClock now)
    {
        base.Start(now);
        _seen = false;
    }

    protected override (bool Truth, JsonValue? Detail) Evaluate(RuleClock now)
    {
        var matches = _sources.UiSnapshot(now.Frame).Where(Matches).ToList();
        switch (_c.State)
        {
            case "appears":
                return matches.Count > 0 ? (true, Describe(matches[0])) : (false, null);
            case "interactable":
                var ready = matches.FirstOrDefault(e => e.Interactable);
                return ready is not null ? (true, Describe(ready)) : (false, null);
            default: // disappears: gone after having been seen since the node started
                _seen |= matches.Count > 0;
                return _seen && matches.Count == 0 ? (true, new JsonObject { { "kind", new JsonString("ui") }, { "state", new JsonString("disappears") } }) : (false, null);
        }
    }

    private bool Matches(UiElementFacts e)
    {
        try
        {
            if (_text is not null && (e.Text is null || !_text.IsMatch(e.Text)))
            {
                return false;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }

        if (_c.Kind is not null && e.Kind != _c.Kind)
        {
            return false;
        }

        if (_c.Path is not null)
        {
            var path = _sources.Unity.Locate(e.GameObject)?.Path ?? string.Empty;
            return path == _c.Path || path.EndsWith("/" + _c.Path, StringComparison.Ordinal);
        }

        return true;
    }

    private JsonObject Describe(UiElementFacts e)
    {
        var detail = new JsonObject
        {
            { "kind", new JsonString("ui") },
            { "state", new JsonString(_c.State) },
            { "h", new JsonNumber(_sources.Data.Handles.Mint(e.GameObject)) },
            { "elementKind", new JsonString(e.Kind) },
            { "path", new JsonString(_sources.Unity.Locate(e.GameObject)?.Path ?? string.Empty) },
        };
        if (e.Text is not null)
        {
            detail.Set("text", new JsonString(e.Text));
        }

        return detail;
    }
}

/// <summary><c>predicate</c>: a compiled <c>bool Check(IAgentContext)</c>, called every N frames (default 5).</summary>
internal sealed class PredicateNode : LevelNode
{
    private readonly SnippetEntry _entry;
    private readonly RuleSources _sources;

    public PredicateNode(string path, PredicateCondition c, SnippetEntry entry, RuleSources sources) : base(path, (int)Math.Min(c.EveryFrames ?? 5, 100_000))
    {
        _entry = entry;
        _sources = sources;
    }

    protected override (bool Truth, JsonValue? Detail) Evaluate(RuleClock now)
    {
        try
        {
            return _sources.Snippets.Check(_entry) ? (true, new JsonObject { { "kind", new JsonString("predicate") } }) : (false, null);
        }
        catch (ProtocolException e)
        {
            Host.Fault(this, $"its predicate failed: {e.Message}");
            return (false, null);
        }
    }
}

/// <summary><c>prompt</c> and <c>pick</c>: answers from the in-game overlay. Accepted, but they never occur while the agent
/// has no overlay.</summary>
internal sealed class OverlayNode : LeafNode
{
    public OverlayNode(string path) : base(path)
    {
    }
}

// ---- combinators -------------------------------------------------------------------------------------------------------

/// <summary><c>all</c>: every child satisfied at some point since starting (latched), or all holding in one frame.</summary>
internal sealed class AllNode : ConditionNode
{
    private readonly ConditionNode[] _children;
    private readonly bool _simultaneous;

    public AllNode(string path, IEnumerable<ConditionNode> children, bool simultaneous) : base(path)
    {
        _children = children.ToArray();
        _simultaneous = simultaneous;
    }

    public override IReadOnlyList<ConditionNode> Children => _children;

    public override void Start(RuleClock now)
    {
        base.Start(now);
        foreach (var child in _children)
        {
            child.Start(now);
        }
    }

    public override void Poll(RuleClock now)
    {
        foreach (var child in _children)
        {
            if (child.Active)
            {
                child.Poll(now);
            }
        }
    }

    public override bool Holds(RuleClock now) => _simultaneous ? _children.All(c => c.Holds(now)) : Satisfied;

    protected override void ChildSatisfied(ConditionNode child, ConditionNode trigger, RuleClock now, bool fromHook)
    {
        if (_simultaneous ? _children.All(c => c.Holds(now)) : _children.All(c => c.Satisfied))
        {
            Satisfy(now, trigger, fromHook);
        }
    }
}

/// <summary><c>any</c>: the first child satisfied.</summary>
internal sealed class AnyNode : ConditionNode
{
    private readonly ConditionNode[] _children;

    public AnyNode(string path, IEnumerable<ConditionNode> children) : base(path) => _children = children.ToArray();

    public override IReadOnlyList<ConditionNode> Children => _children;

    public override void Start(RuleClock now)
    {
        base.Start(now);
        foreach (var child in _children)
        {
            child.Start(now);
        }
    }

    public override void Poll(RuleClock now)
    {
        foreach (var child in _children)
        {
            if (child.Active)
            {
                child.Poll(now);
            }
        }
    }

    public override bool Holds(RuleClock now) => _children.Any(c => c.Holds(now));

    protected override void ChildSatisfied(ConditionNode child, ConditionNode trigger, RuleClock now, bool fromHook) => Satisfy(now, trigger, fromHook);
}

/// <summary><c>seq</c>: the children in order, each measured from the previous one; with <c>withinMs</c>, a step that takes
/// longer than that after the previous one restarts the sequence.</summary>
internal sealed class SeqNode : ConditionNode
{
    private readonly ConditionNode[] _children;
    private readonly long? _withinMs;
    private int _step;
    private double _lastAtMs;

    public SeqNode(string path, IEnumerable<ConditionNode> children, long? withinMs) : base(path)
    {
        _children = children.ToArray();
        _withinMs = withinMs;
    }

    public override IReadOnlyList<ConditionNode> Children => _children;

    public override void Start(RuleClock now)
    {
        base.Start(now);
        foreach (var child in _children)
        {
            child.Stop();
        }

        _step = 0;
        _children[0].Start(now);
    }

    public override void Poll(RuleClock now)
    {
        if (_step >= _children.Length)
        {
            return;
        }

        if (_withinMs is { } within && _step > 0 && now.RealtimeMs - _lastAtMs > within)
        {
            for (var i = 0; i < _step; i++)
            {
                Host.Progress(_children[i], false);
            }

            Reset();
            Start(now);
            return;
        }

        _children[_step].Poll(now);
    }

    public override JsonObject Progress()
    {
        var progress = base.Progress();
        progress.Set("step", new JsonNumber(Math.Min(_step, _children.Length)));
        progress.Set("steps", new JsonNumber(_children.Length));
        return progress;
    }

    protected override void ChildSatisfied(ConditionNode child, ConditionNode trigger, RuleClock now, bool fromHook)
    {
        if (_step >= _children.Length || !ReferenceEquals(child, _children[_step]))
        {
            return;
        }

        child.Stop();
        _lastAtMs = now.RealtimeMs;
        _step++;
        if (_step == _children.Length)
        {
            Satisfy(now, trigger, fromHook);
        }
        else
        {
            _children[_step].Start(now);
        }
    }
}

/// <summary><c>not</c>: the child doesn't occur for <c>forMs</c> (each occurrence restarts the window).</summary>
internal sealed class NotNode : ConditionNode
{
    private readonly ConditionNode _child;
    private readonly long _forMs;
    private double _windowStart;

    public NotNode(string path, ConditionNode child, long forMs) : base(path)
    {
        _child = child;
        _forMs = forMs;
    }

    public override IReadOnlyList<ConditionNode> Children => new[] { _child };

    public override void Start(RuleClock now)
    {
        base.Start(now);
        _windowStart = now.RealtimeMs;
        _child.Start(now);
    }

    public override void Poll(RuleClock now)
    {
        _child.Poll(now);
        if (!Satisfied && now.RealtimeMs - _windowStart >= _forMs && Host.Listening)
        {
            Detail = new JsonObject { { "kind", new JsonString("not") }, { "forMs", new JsonNumber(_forMs) } };
            Satisfy(now, this, false);
        }
    }

    public override bool Holds(RuleClock now) => now.RealtimeMs - _windowStart >= _forMs;

    protected override void ChildSatisfied(ConditionNode child, ConditionNode trigger, RuleClock now, bool fromHook)
    {
        _windowStart = now.RealtimeMs;
        child.Rearm(now);
    }
}

/// <summary><c>count</c>: the child occurs n times (it is restarted after each occurrence).</summary>
internal sealed class CountNode : ConditionNode
{
    private readonly ConditionNode _child;
    private readonly long _n;
    private long _count;

    public CountNode(string path, ConditionNode child, long n) : base(path)
    {
        _child = child;
        _n = Math.Max(1, n);
    }

    public override IReadOnlyList<ConditionNode> Children => new[] { _child };

    public override void Start(RuleClock now)
    {
        base.Start(now);
        _count = 0;
        _child.Start(now);
    }

    public override void Poll(RuleClock now) => _child.Poll(now);

    public override JsonObject Progress()
    {
        var progress = base.Progress();
        progress.Set("count", new JsonNumber(_count));
        progress.Set("n", new JsonNumber(_n));
        return progress;
    }

    protected override void ChildSatisfied(ConditionNode child, ConditionNode trigger, RuleClock now, bool fromHook)
    {
        _count++;
        if (_count >= _n)
        {
            Satisfy(now, trigger, fromHook);
        }
        else
        {
            child.Rearm(now);
        }
    }
}

/// <summary>Builds a rule's condition tree, resolving what can be resolved off the main thread (methods, snippets).</summary>
internal static class ConditionBuilder
{
    public static ConditionNode Build(RuleCondition c, string path, RuleSources sources)
    {
        var kind = RuleCheck.KindOf(c);
        switch (kind)
        {
            case "all":
                return new AllNode(path + ".all", c.All!.Select((child, i) => Build(child, $"{path}.all[{i}]", sources)), c.Simultaneous ?? false);
            case "any":
                return new AnyNode(path + ".any", c.Any!.Select((child, i) => Build(child, $"{path}.any[{i}]", sources)));
            case "seq":
                return new SeqNode(path + ".seq", c.Seq!.Select((child, i) => Build(child, $"{path}.seq[{i}]", sources)), c.WithinMs);
            case "not":
                return new NotNode(path + ".not", Build(c.Not!, path + ".not", sources), c.ForMs!.Value);
            case "count":
                return new CountNode(path + ".count", Build(c.Count!, path + ".count", sources), c.N!.Value);
        }

        var leaf = path + "." + kind;
        return kind switch
        {
            "scene" => new SceneNode(leaf, c.Scene!, sources),
            "delay" => new DelayNode(leaf, c.Delay!),
            "value" => new ValueNode(leaf, c.Value!, sources),
            "hook" => new HookNode(leaf, c.Hook!, Method(c.Hook!.Method, leaf + ".method", sources), sources),
            "event" => new EventNode(leaf, c.Event!, sources),
            "log" => new LogNode(leaf, c.Log!, sources),
            "exception" => new ExceptionNode(leaf, c.Exception!, sources),
            "ui" => new UiNode(leaf, c.Ui!, sources),
            "predicate" => new PredicateNode(leaf, c.Predicate!,
                sources.Snippets.Bind(AssemblyLoader.Decode(c.Predicate!.Assembly, leaf + ".assembly"), c.Predicate.EntryType, c.Predicate.Method), sources),
            _ => new OverlayNode(leaf),
        };
    }

    private static MethodBase Method(Anchor anchor, string param, RuleSources sources)
    {
        var method = sources.Data.Anchors.ResolveMember(anchor, param) as MethodBase ?? throw ProtocolException.InvalidParams(param, $"{param} must be a method or constructor.");
        if (Instrumenter.Refusal(method, force: false) is { } reason)
        {
            throw new ProtocolException(ErrorCodes.Unsupported, $"{AnchorWriter.MemberName(method)} can't be instrumented: {reason}");
        }

        return method;
    }
}
