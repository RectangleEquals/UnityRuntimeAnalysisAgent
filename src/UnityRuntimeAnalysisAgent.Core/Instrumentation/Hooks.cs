using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Data;
using UnityRuntimeAnalysisAgent.Core.Live;
using UnityRuntimeAnalysisAgent.Core.Runtime;

namespace UnityRuntimeAnalysisAgent.Core.Instrumentation;

/// <summary>
/// Hooks: observe calls of a method (enter/exit/throw) with captured values, sampling, conditions and a ring buffer,
/// delivered by pull (<c>hook.hits</c>) and as batched <c>hook.hits</c> events. Hooks belong to the connection that added
/// them. A cap on buffered records (all hooks together) keeps memory bounded.
/// </summary>
public sealed class HookManager
{
    /// <summary>Records buffered across all hooks at most.</summary>
    public const int MaxBufferedRecords = 200_000;

    private readonly ConcurrentDictionary<string, Hook> _hooks = new(StringComparer.Ordinal);
    private readonly Instrumenter _instrumenter;
    private readonly RecordBuilder _records;
    private readonly ConditionEvaluator _conditions;
    private readonly EventHub _events;
    private int _next;
    private long _buffered;

    /// <summary>Creates the manager.</summary>
    public HookManager(Instrumenter instrumenter, RecordBuilder records, ConditionEvaluator conditions, EventHub events)
    {
        _instrumenter = instrumenter;
        _records = records;
        _conditions = conditions;
        _events = events;
    }

    /// <summary>Active hooks.</summary>
    public int Count => _hooks.Count;

    /// <summary>Records buffered now.</summary>
    public long Buffered => Interlocked.Read(ref _buffered);

    /// <summary><c>hook.add</c>.</summary>
    public HookAddResult Add(MethodBase method, HookAddParams p, string owner)
    {
        var phases = p.Phases is { Count: > 0 } ? new HashSet<string>(p.Phases, StringComparer.Ordinal) : new HashSet<string>(new[] { "enter", "exit", "throw" }, StringComparer.Ordinal);
        var capture = new CaptureOptions
        {
            Instance = p.Capture?.Instance ?? false,
            Args = p.Capture?.Args ?? true,
            Result = p.Capture?.Result ?? true,
            Stack = (int)Math.Max(0, Math.Min(32, p.Capture?.Stack ?? 0)),
            Depth = (int)Math.Max(0, Math.Min(2, p.Capture?.Depth ?? 1)),
            Retain = p.Capture?.Retain ?? false,
        };
        var id = "h-" + Interlocked.Increment(ref _next);
        var hook = new Hook(this, id, method, phases, capture, (int)Math.Max(1, p.Sample ?? 1), Math.Max(1, p.MaxHits ?? 1000), (int)Math.Max(1, Math.Min(100_000, p.Ring ?? 1000)),
            p.Persistent ?? false, owner, p.Condition);
        _instrumenter.Attach(method, hook, p.Force ?? false);
        _hooks[id] = hook;
        return new HookAddResult { HookId = id, Patched = new PatchedParts { Prefix = true, Postfix = true, Finalizer = true } };
    }

    /// <summary><c>hook.remove</c>.</summary>
    public HookRemoveResult Remove(string id)
    {
        var hook = Take(id) ?? throw DataErrors.NotFound("params.hookId", $"No hook {id}.");
        return new HookRemoveResult { Hits = hook.Hits, Dropped = hook.Dropped };
    }

    /// <summary><c>hook.list</c>.</summary>
    public List<HookInfo> List() => _hooks.Values.OrderBy(h => h.Number).Select(h => new HookInfo
    {
        HookId = h.Id, Method = AnchorWriter.ForMember(h.Method), Hits = h.Hits, Dropped = h.Dropped, Sample = h.Sample, Owner = h.Owner, Persistent = h.Persistent,
    }).ToList();

    /// <summary><c>hook.hits</c>: buffered records after <paramref name="sinceSeq"/>.</summary>
    public HookHitsResult Hits(string id, long sinceSeq, int limit)
    {
        var hook = _hooks.TryGetValue(id, out var h) ? h : throw DataErrors.NotFound("params.hookId", $"No hook {id}.");
        var items = hook.Snapshot().Where(r => r.Seq > sinceSeq).Take(limit).ToList();
        return new HookHitsResult { Items = items, NextSeq = items.Count > 0 ? items[items.Count - 1].Seq : sinceSeq, Dropped = hook.Dropped };
    }

    /// <summary>Removes hooks: all, or those of one owner (optionally keeping persistent ones). Returns how many.</summary>
    public int Clear(string? owner, bool keepPersistent = false)
    {
        var removed = 0;
        foreach (var hook in _hooks.Values.ToList())
        {
            if ((owner is null || hook.Owner == owner) && !(keepPersistent && hook.Persistent) && Take(hook.Id) is not null)
            {
                removed++;
            }
        }

        return removed;
    }

    internal RecordBuilder Records => _records;

    internal Instrumenter Instrumenter => _instrumenter;

    internal bool Reserve()
    {
        if (Interlocked.Increment(ref _buffered) > MaxBufferedRecords)
        {
            Interlocked.Decrement(ref _buffered);
            return false;
        }

        return true;
    }

    internal void Release(long count) => Interlocked.Add(ref _buffered, -count);

    internal void Publish(InstrumentationRecord record)
    {
        if (_events.HasSubscribers(EventKinds.HookHits))
        {
            _events.PublishItem(EventKinds.HookHits, record.ToJson());
        }
    }

    // A hook's conditions on the captured argument or instance (quietly false when they can't be evaluated).
    internal bool Matches(IReadOnlyList<Condition> conditions, CallContext call)
    {
        foreach (var condition in conditions)
        {
            object? subject = condition.Instance == true ? call.Instance
                : condition.Arg is { } arg && call.Args is { } args && arg >= 0 && arg < args.Length ? args[arg]
                : null;
            try
            {
                if (subject is null)
                {
                    if (!(condition.Op == "isNull" || (condition.Op == "eq" && condition.Value is null or JsonNull)))
                    {
                        return false;
                    }
                }
                else if (!_conditions.Matches(subject, new[] { condition }, "params.condition"))
                {
                    return false;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        return true;
    }

    // maxHits reached: removed off the calling thread (never unpatch inside the patched call).
    internal void AutoDisable(Hook hook) => ThreadPool.QueueUserWorkItem(_ => Take(hook.Id));

    private Hook? Take(string id)
    {
        if (!_hooks.TryRemove(id, out var hook))
        {
            return null;
        }

        hook.Disabled = true;
        _instrumenter.Detach(hook.Method, hook);
        Release(hook.ClearBuffer());
        return hook;
    }
}

/// <summary>One hook: a sink on one method.</summary>
internal sealed class Hook : IMethodSink, IFaultAware
{
    private static readonly object Token = new();
    private readonly HookManager _manager;
    private readonly HashSet<string> _phases;
    private readonly CaptureOptions _capture;
    private readonly long _maxHits;
    private readonly int _ring;
    private readonly IReadOnlyList<Condition>? _conditions;
    private readonly Queue<InstrumentationRecord> _buffer = new();
    private long _calls;
    private long _hits;
    private long _dropped;

    public Hook(HookManager manager, string id, MethodBase method, HashSet<string> phases, CaptureOptions capture, int sample, long maxHits, int ring, bool persistent, string owner,
        IReadOnlyList<Condition>? conditions)
    {
        _manager = manager;
        Id = id;
        Number = int.Parse(id.Substring(2), System.Globalization.CultureInfo.InvariantCulture);
        Method = method;
        _phases = phases;
        _capture = capture;
        Sample = sample;
        _maxHits = maxHits;
        _ring = ring;
        Persistent = persistent;
        Owner = owner;
        _conditions = conditions is { Count: > 0 } ? conditions : null;
    }

    public string Id { get; }

    public int Number { get; }

    public MethodBase Method { get; }

    public int Sample { get; }

    public bool Persistent { get; }

    public string Owner { get; }

    public volatile bool Disabled;

    public long Hits => Interlocked.Read(ref _hits);

    public long Dropped => Interlocked.Read(ref _dropped);

    public object? Enter(CallContext call)
    {
        if (Disabled || (Interlocked.Increment(ref _calls) - 1) % Sample != 0 || (_conditions is not null && !_manager.Matches(_conditions, call)))
        {
            return null;
        }

        if (_phases.Contains("enter"))
        {
            Add(_manager.Records.Build(_manager.Instrumenter, call, "enter", _capture));
        }

        return Token;
    }

    public void Exit(CallContext call, object token, object? result, bool hasResult, long elapsedTicks)
    {
        if (!Disabled && _phases.Contains("exit"))
        {
            Add(_manager.Records.Build(_manager.Instrumenter, call, "exit", _capture, result, hasResult, elapsedTicks: elapsedTicks));
        }
    }

    public void Throw(CallContext call, object token, Exception exception, long elapsedTicks)
    {
        if (!Disabled && _phases.Contains("throw"))
        {
            Add(_manager.Records.Build(_manager.Instrumenter, call, "throw", _capture, exception: exception, elapsedTicks: elapsedTicks));
        }
    }

    public void Faulted(Exception error) => Disabled = true;

    public List<InstrumentationRecord> Snapshot()
    {
        lock (_buffer)
        {
            return _buffer.ToList();
        }
    }

    public long ClearBuffer()
    {
        lock (_buffer)
        {
            var count = _buffer.Count;
            _buffer.Clear();
            return count;
        }
    }

    private void Add(InstrumentationRecord record)
    {
        record.HookId = Id;
        var hits = Interlocked.Increment(ref _hits);
        lock (_buffer)
        {
            if (_buffer.Count >= _ring)
            {
                _buffer.Dequeue();
                _manager.Release(1);
                Interlocked.Increment(ref _dropped);
            }

            if (_manager.Reserve())
            {
                _buffer.Enqueue(record);
            }
            else
            {
                Interlocked.Increment(ref _dropped);
            }
        }

        _manager.Publish(record);
        if (hits >= _maxHits)
        {
            Disabled = true;
            _manager.AutoDisable(this);
        }
    }
}
