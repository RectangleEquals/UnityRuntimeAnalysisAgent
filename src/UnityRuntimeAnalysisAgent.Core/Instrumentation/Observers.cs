using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Threading;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Abstractions;
using UnityRuntimeAnalysisAgent.Core.Code;
using UnityRuntimeAnalysisAgent.Core.Data;
using UnityRuntimeAnalysisAgent.Core.Runtime;

namespace UnityRuntimeAnalysisAgent.Core.Instrumentation;

/// <summary>
/// Watches: a value read again at a cadence on the main thread (from the pump's frame event), with a change recorded
/// whenever its encoding differs from the last read.
/// </summary>
public sealed class WatchManager
{
    private readonly ConcurrentDictionary<string, Watch> _watches = new(StringComparer.Ordinal);
    private readonly DataModel _data;
    private readonly EventHub _events;
    private readonly MainThreadPump _pump;
    private int _next;

    /// <summary>Creates the manager (it polls from the pump's frame event).</summary>
    public WatchManager(DataModel data, EventHub events, MainThreadPump pump)
    {
        _data = data;
        _events = events;
        _pump = pump;
        pump.Ticked += Poll;
    }

    /// <summary>Active watches.</summary>
    public int Count => _watches.Count;

    /// <summary><c>watch.add</c> (main thread): the first read is the initial value.</summary>
    public WatchAddResult Add(WatchAddParams p, string owner)
    {
        var view = ViewOptions.From(p.View);
        if (p.View?.Depth is null)
        {
            view.Depth = 1;
        }

        _data.Targets.Resolve(p.Target, "params.target"); // HANDLE_EXPIRED / NOT_FOUND now, not as the first value
        var id = "w-" + Interlocked.Increment(ref _next);
        var watch = new Watch(id, p, view, owner);
        var clock = _pump.Clock;
        watch.Last = Read(watch, clock.FrameCount);
        watch.NextFrame = clock.FrameCount + watch.EveryFrames;
        watch.NextMs = _pump.NowMs + watch.EveryMs;
        _watches[id] = watch;
        return new WatchAddResult { WatchId = id, Initial = watch.Last };
    }

    /// <summary><c>watch.remove</c>.</summary>
    public WatchRemoveResult Remove(string id) =>
        _watches.TryRemove(id, out var watch) ? new WatchRemoveResult { Changes = watch.Changes } : throw DataErrors.NotFound("params.watchId", $"No watch {id}.");

    /// <summary><c>watch.list</c>.</summary>
    public List<WatchInfo> List() => _watches.Values.OrderBy(w => w.Number).Select(w => new WatchInfo
    {
        WatchId = w.Id, Target = w.Params.Target, Path = w.Params.Path, Changes = w.Changes, Owner = w.Owner, Persistent = w.Params.Persistent ?? false,
    }).ToList();

    /// <summary><c>watch.changes</c>.</summary>
    public WatchChangesResult Changes(string id, long sinceFrame, int limit)
    {
        var watch = _watches.TryGetValue(id, out var w) ? w : throw DataErrors.NotFound("params.watchId", $"No watch {id}.");
        lock (watch.Recorded)
        {
            return new WatchChangesResult { Items = watch.Recorded.Where(c => c.Frame >= sinceFrame).Take(limit).ToList(), Dropped = watch.Dropped };
        }
    }

    /// <summary>Removes watches: all, or one owner's (optionally keeping persistent ones).</summary>
    public int Clear(string? owner, bool keepPersistent = false)
    {
        var removed = 0;
        foreach (var watch in _watches.Values.ToList())
        {
            if ((owner is null || watch.Owner == owner) && !(keepPersistent && (watch.Params.Persistent ?? false)) && _watches.TryRemove(watch.Id, out _))
            {
                removed++;
            }
        }

        return removed;
    }

    private void Poll(FrameTime clock)
    {
        if (_watches.IsEmpty)
        {
            return;
        }

        var now = _pump.NowMs;
        foreach (var watch in _watches.Values)
        {
            if (clock.FrameCount < watch.NextFrame || now < watch.NextMs)
            {
                continue;
            }

            watch.NextFrame = clock.FrameCount + watch.EveryFrames;
            watch.NextMs = now + watch.EveryMs;
            var current = Read(watch, clock.FrameCount);
            var changed = current.ToString() != watch.Last.ToString();
            if (!changed && watch.OnChangeOnly)
            {
                continue;
            }

            var change = new WatchChange { WatchId = watch.Id, Previous = watch.Last, Current = current, Frame = clock.FrameCount, RealtimeMs = (long)(clock.Realtime * 1000) };
            watch.Last = current;
            lock (watch.Recorded)
            {
                if (watch.Recorded.Count >= watch.MaxEvents)
                {
                    watch.Dropped++;
                    continue;
                }

                watch.Recorded.Add(change);
            }

            watch.Changes++;
            if (_events.HasSubscribers(EventKinds.WatchChanges))
            {
                _events.PublishItem(EventKinds.WatchChanges, change.ToJson());
            }
        }
    }

    // A value that can't be read (a destroyed target, a throwing getter) is recorded as its error.
    private JsonValue Read(Watch watch, long frame)
    {
        try
        {
            var resolved = _data.Targets.Resolve(watch.Params.Target, watch.Params.Path, "params.target", "params.path");
            return _data.Writer(watch.View, frame).Write(resolved.Value, resolved.Place);
        }
        catch (Exception e) when (e is ProtocolException or Dispatch.GameCodeException)
        {
            var error = e is ProtocolException p ? p.ToError() : new UnityLudometry.Protocol.Envelopes.ProtocolError { Code = ErrorCodes.GameException, Message = e.InnerException?.Message ?? e.Message };
            return new JsonObject { { "error", Dispatch.AgentErrors.ToJson(error) } };
        }
    }

    private sealed class Watch
    {
        public Watch(string id, WatchAddParams p, ViewOptions view, string owner)
        {
            Id = id;
            Number = int.Parse(id.Substring(2), System.Globalization.CultureInfo.InvariantCulture);
            Params = p;
            View = view;
            Owner = owner;
            EveryFrames = p.EveryMs is null ? Math.Max(1, p.EveryFrames ?? 10) : 0;
            EveryMs = Math.Max(0, p.EveryMs ?? 0);
            OnChangeOnly = p.OnChangeOnly ?? true;
            MaxEvents = (int)Math.Max(1, Math.Min(100_000, p.MaxEvents ?? 1000));
        }

        public string Id { get; }

        public int Number { get; }

        public WatchAddParams Params { get; }

        public ViewOptions View { get; }

        public string Owner { get; }

        public long EveryFrames { get; }

        public double EveryMs { get; }

        public bool OnChangeOnly { get; }

        public int MaxEvents { get; }

        public JsonValue Last { get; set; } = JsonNull.Instance;

        public long NextFrame { get; set; }

        public double NextMs { get; set; }

        public long Changes { get; set; }

        public long Dropped { get; set; }

        public List<WatchChange> Recorded { get; } = new();
    }
}

/// <summary>
/// Event subscriptions: a handler of the event's exact delegate type (built with expression trees: parameters →
/// <c>object[]</c> → the agent's callback), attached through the add accessor, or a runtime listener on a UnityEvent.
/// Each raise is captured shallowly and delivered as <c>event.raised</c>.
/// </summary>
public sealed class SubscriptionManager
{
    private readonly ConcurrentDictionary<string, Subscription> _subscriptions = new(StringComparer.Ordinal);
    private readonly DataModel _data;
    private readonly EventHub _events;
    private readonly MainThreadPump _pump;
    private int _next;

    /// <summary>Creates the manager.</summary>
    public SubscriptionManager(DataModel data, EventHub events, MainThreadPump pump)
    {
        _data = data;
        _events = events;
        _pump = pump;
    }

    /// <summary>Active subscriptions.</summary>
    public int Count => _subscriptions.Count;

    /// <summary><c>event.subscribe</c> (main thread).</summary>
    public EventSubscribeResult Subscribe(EventSubscribeParams p, string owner)
    {
        var id = "s-" + Interlocked.Increment(ref _next);
        var subscription = new Subscription(id, owner, p.Persistent ?? false, (int)Math.Max(1, Math.Min(100_000, p.MaxEvents ?? 1000)),
            p.Capture?.Args ?? true, (int)Math.Max(0, Math.Min(2, p.Capture?.Depth ?? 1)));
        Action<object?[]> callback = args => Raised(subscription, args);

        if (p.Event is not null)
        {
            var member = _data.Anchors.ResolveMember(p.Event, "params.event") as EventInfo ?? throw ProtocolException.InvalidParams("params.event", "params.event must be an event.");
            object? instance = null;
            if (p.Target is not null)
            {
                var resolved = _data.Targets.Resolve(p.Target, "params.target");
                if (!resolved.IsStatic)
                {
                    member = AnchorResolver.BindTo(member, resolved.Type) as EventInfo ?? throw ProtocolException.InvalidParams("params.event", $"{AnchorWriter.TypeName(resolved.Type)} has no event {member.Name}.");
                    instance = resolved.Value;
                }
            }
            else if (!(member.GetAddMethod(true)?.IsStatic ?? false))
            {
                throw ProtocolException.InvalidParams("params.target", "An instance event needs params.target.");
            }

            var handler = Handler(member.EventHandlerType!, callback);
            var add = member.GetAddMethod(true)!;
            var remove = member.GetRemoveMethod(true)!;
            add.Invoke(instance, new object[] { handler });
            subscription.Detach = () => remove.Invoke(instance, new object[] { handler });
        }
        else if (p.Target is not null && p.Path is not null)
        {
            var resolved = _data.Targets.Resolve(p.Target, p.Path, "params.target", "params.path");
            var unityEvent = resolved.Value ?? throw ProtocolException.InvalidParams("params.path", "The UnityEvent is null.");
            var addListener = unityEvent.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(m => m.Name == "AddListener" && m.GetParameters().Length == 1 && typeof(Delegate).IsAssignableFrom(m.GetParameters()[0].ParameterType))
                ?? throw ProtocolException.InvalidParams("params.path", $"{unityEvent.GetType().FullName} isn't a UnityEvent (no AddListener).");
            var removeListener = unityEvent.GetType().GetMethod("RemoveListener", new[] { addListener.GetParameters()[0].ParameterType });
            var handler = Handler(addListener.GetParameters()[0].ParameterType, callback);
            addListener.Invoke(unityEvent, new object[] { handler });
            subscription.Detach = () => removeListener?.Invoke(unityEvent, new object[] { handler });
        }
        else
        {
            throw ProtocolException.InvalidParams("params", "Give an event (C# events) or a target and a path (UnityEvents).");
        }

        _subscriptions[id] = subscription;
        return new EventSubscribeResult { SubscriptionId = id };
    }

    /// <summary><c>event.unsubscribe</c>.</summary>
    public EventUnsubscribeResult Unsubscribe(string id)
    {
        var subscription = Take(id) ?? throw DataErrors.NotFound("params.subscriptionId", $"No subscription {id}.");
        return new EventUnsubscribeResult { Raised = subscription.RaisedCount };
    }

    /// <summary>Removes subscriptions: all, or one owner's (optionally keeping persistent ones).</summary>
    public int Clear(string? owner, bool keepPersistent = false)
    {
        var removed = 0;
        foreach (var subscription in _subscriptions.Values.ToList())
        {
            if ((owner is null || subscription.Owner == owner) && !(keepPersistent && subscription.Persistent) && Take(subscription.Id) is not null)
            {
                removed++;
            }
        }

        return removed;
    }

    /// <summary>A delegate of <paramref name="delegateType"/> that passes its arguments to <paramref name="callback"/>
    /// (and returns the default value when the delegate returns one).</summary>
    public static Delegate Handler(Type delegateType, Action<object?[]> callback)
    {
        var invoke = delegateType.GetMethod("Invoke") ?? throw new ArgumentException($"{delegateType} isn't a delegate type.", nameof(delegateType));
        var parameters = invoke.GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
        var array = Expression.NewArrayInit(typeof(object), parameters.Select(p => Expression.Convert(p, typeof(object))));
        Expression body = Expression.Invoke(Expression.Constant(callback), array);
        if (invoke.ReturnType != typeof(void))
        {
            body = Expression.Block(body, Expression.Default(invoke.ReturnType));
        }

        return Expression.Lambda(delegateType, body, parameters).Compile();
    }

    private void Raised(Subscription subscription, object?[] args)
    {
        if (subscription.Removed)
        {
            return;
        }

        PatchDispatch.Quietly(() =>
        {
            var clock = _pump.Clock;
            var raised = new RaisedEvent { SubscriptionId = subscription.Id, Frame = clock.FrameCount, RealtimeMs = (long)(clock.Realtime * 1000) };
            if (subscription.CaptureArgs)
            {
                var writer = _data.Writer(new ViewOptions { Safe = true, Capture = true, Depth = subscription.Depth, MaxString = 256, MaxItems = 16, MaxMembers = 32 }, clock.FrameCount);
                raised.Args = args.Select(a => writer.Write(a, new Place())).ToList();
            }

            var count = Interlocked.Increment(ref subscription.RaisedCount);
            if (count > subscription.MaxEvents)
            {
                return 0; // over the cap: counted, not delivered
            }

            if (_events.HasSubscribers(EventKinds.EventRaised))
            {
                _events.PublishItem(EventKinds.EventRaised, raised.ToJson());
            }

            return 0;
        });
    }

    private Subscription? Take(string id)
    {
        if (!_subscriptions.TryRemove(id, out var subscription))
        {
            return null;
        }

        subscription.Removed = true;
        try
        {
            subscription.Detach?.Invoke();
        }
        catch (TargetInvocationException)
        {
            // The event's owner is gone (destroyed): nothing to detach from.
        }

        return subscription;
    }

    private sealed class Subscription
    {
        public Subscription(string id, string owner, bool persistent, int maxEvents, bool captureArgs, int depth)
        {
            Id = id;
            Owner = owner;
            Persistent = persistent;
            MaxEvents = maxEvents;
            CaptureArgs = captureArgs;
            Depth = depth;
        }

        public string Id { get; }

        public string Owner { get; }

        public bool Persistent { get; }

        public int MaxEvents { get; }

        public bool CaptureArgs { get; }

        public int Depth { get; }

        public long RaisedCount;

        public volatile bool Removed;

        public Action? Detach { get; set; }
    }
}

/// <summary>
/// Exception monitoring: exceptions Unity logs (unhandled ones and those the game logs), and optionally every thrown
/// exception (first chance: very noisy, so filtered and rate-limited). Delivered as <c>exception</c> events.
/// </summary>
public sealed class ExceptionMonitor : IDisposable
{
    private const int MaxPerSecond = 100;
    private static readonly Regex LoggedException = new(@"^(?<type>[\w.+`]+(?:Exception|Error))\s*:\s*(?<message>.*)$", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    [ThreadStatic]
    private static bool t_inHandler;

    private readonly Diagnostics.LogBuffer _logs;
    private readonly EventHub _events;
    private readonly MainThreadPump _pump;
    private readonly object _gate = new();
    private Regex? _typeRegex;
    private AssemblyFilter? _assemblies;
    private bool _enabled;
    private bool _firstChance;
    private long _windowStart;
    private int _inWindow;

    /// <summary>Creates the monitor (off until enabled); logged exceptions come from the unified log.</summary>
    public ExceptionMonitor(Diagnostics.LogBuffer logs, EventHub events, MainThreadPump pump)
    {
        _logs = logs;
        _events = events;
        _pump = pump;
    }

    /// <summary>Whether the runtime reports first-chance exceptions.</summary>
    public static bool FirstChanceSupported => typeof(AppDomain).GetEvent("FirstChanceException") is not null;

    /// <summary>Exceptions left out by the rate limit so far.</summary>
    public long Dropped { get; private set; }

    /// <summary><c>exceptions.monitor</c>.</summary>
    public ExceptionsMonitorResult Configure(ExceptionsMonitorParams p)
    {
        lock (_gate)
        {
            _typeRegex = p.Filter?.TypeRegex is { } pattern ? new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) : null;
            _assemblies = p.Filter?.AssemblyGlobs is { Count: > 0 } globs ? new AssemblyFilter(globs, null, builtInExclusions: false) : null;
            SetLog(p.Enabled);
            SetFirstChance(p.Enabled && (p.FirstChance ?? false) && FirstChanceSupported);
            return new ExceptionsMonitorResult { Enabled = _enabled, FirstChance = _firstChance };
        }
    }

    /// <summary>Stops monitoring.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            SetLog(false);
            SetFirstChance(false);
        }
    }

    private void SetLog(bool on)
    {
        if (on && !_enabled)
        {
            _logs.Added += OnLog;
        }
        else if (!on && _enabled)
        {
            _logs.Added -= OnLog;
        }

        _enabled = on;
    }

    private void SetFirstChance(bool on)
    {
        if (on && !_firstChance)
        {
            AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
        }
        else if (!on && _firstChance)
        {
            AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
        }

        _firstChance = on;
    }

    private void OnLog(LogEntry entry)
    {
        if (entry.Source != "unity" || entry.Level != "exception")
        {
            return;
        }

        var match = LoggedException.Match(entry.Message);
        Report(match.Success ? match.Groups["type"].Value : "Exception", match.Success ? match.Groups["message"].Value : entry.Message, entry.Stack ?? string.Empty, null, "log");
    }

    private void OnFirstChance(object? sender, FirstChanceExceptionEventArgs e)
    {
        if (t_inHandler || PatchDispatch.Busy)
        {
            return; // the agent's own exceptions, or one thrown while reporting
        }

        t_inHandler = true;
        try
        {
            var exception = e.Exception;
            var assembly = exception.TargetSite?.DeclaringType?.Assembly.GetName().Name;
            if (_assemblies is not null && (assembly is null || !_assemblies.Includes(assembly)))
            {
                return;
            }

            Report(exception.GetType().FullName ?? exception.GetType().Name, exception.Message, exception.StackTrace ?? string.Empty, exception, "firstChance");
        }
        catch (Exception)
        {
            // Never throw from a first-chance handler.
        }
        finally
        {
            t_inHandler = false;
        }
    }

    private void Report(string type, string message, string stack, Exception? exception, string source)
    {
        if (_typeRegex is { } regex && !regex.IsMatch(type))
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (now - _windowStart > Stopwatch.Frequency)
            {
                _windowStart = now;
                _inWindow = 0;
            }

            if (++_inWindow > MaxPerSecond)
            {
                Dropped++;
                return;
            }
        }

        if (!_events.HasSubscribers(EventKinds.Exception))
        {
            return;
        }

        var clock = _pump.Clock;
        var observed = new ObservedException
        {
            Type = type,
            Message = message,
            Stack = stack,
            Source = source,
            Frame = clock.FrameCount,
            RealtimeMs = (long)(clock.Realtime * 1000),
        };
        if (exception is not null)
        {
            observed.StackAnchors = new StackTrace(exception, false).GetFrames()?.Select(f => f.GetMethod()).Where(m => m is not null).Take(16)
                .Select(m => AnchorWriter.ForMember(m!)).ToList();
        }

        _events.PublishItem(EventKinds.Exception, observed.ToJson());
    }
}
