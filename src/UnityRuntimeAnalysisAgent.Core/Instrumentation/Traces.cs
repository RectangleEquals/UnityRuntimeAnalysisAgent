using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Code;
using UnityRuntimeAnalysisAgent.Core.Data;
using UnityRuntimeAnalysisAgent.Core.Jobs;
using UnityRuntimeAnalysisAgent.Core.Runtime;

namespace UnityRuntimeAnalysisAgent.Core.Instrumentation;

/// <summary>
/// Traces: ordered enter/exit records of a set of methods, written as NDJSON while they happen (a job), with a summary
/// (top-level sequence, the call tree of each root's first occurrence, per-method counts and durations). Methods that
/// can't be patched are listed and the trace goes on for the rest.
/// </summary>
public sealed class TraceManager
{
    private const int MaxSequence = 1000;
    private const int MaxTreeNodes = 300;
    private readonly ConcurrentDictionary<string, TraceSession> _sessions = new(StringComparer.Ordinal);
    private readonly Instrumenter _instrumenter;
    private readonly RecordBuilder _records;
    private readonly MainThreadPump _pump;
    private readonly EventHub _events;
    private readonly string _agentVersion;

    /// <summary>Creates the manager.</summary>
    public TraceManager(Instrumenter instrumenter, RecordBuilder records, MainThreadPump pump, EventHub events, string agentVersion)
    {
        _instrumenter = instrumenter;
        _records = records;
        _pump = pump;
        _events = events;
        _agentVersion = agentVersion;
    }

    /// <summary>Traces running.</summary>
    public int Count => _sessions.Count;

    /// <summary>The methods a trace covers: explicit anchors plus the methods of matching types (capped).</summary>
    public static List<MethodBase> Expand(IEnumerable<MethodBase> methods, TraceInclude? include, CodeModel code, int cap)
    {
        var result = new List<MethodBase>(methods);
        if (include is not null && (include.AssemblyGlobs is { Count: > 0 } || include.TypeRegex is not null))
        {
            var typeRegex = include.TypeRegex is null ? null : new Regex(include.TypeRegex, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            var filter = new AssemblyFilter(include.AssemblyGlobs, null, builtInExclusions: true);
            foreach (var type in code.Catalog.Assemblies(filter).SelectMany(a => a.GetModules()).SelectMany(AnchorResolver.LoadableTypes))
            {
                if (result.Count >= cap)
                {
                    break;
                }

                if (typeRegex is not null && !typeRegex.IsMatch(type.FullName ?? type.Name))
                {
                    continue;
                }

                IEnumerable<MethodBase> declared;
                try
                {
                    declared = type.GetMethods(Describe.Declared).Cast<MethodBase>().Concat(type.GetConstructors(Describe.Declared).Where(c => !c.IsStatic)).ToList();
                }
                catch (Exception)
                {
                    continue;
                }

                result.AddRange(declared.Where(m => Instrumenter.Refusal(m, force: false) is null));
            }
        }

        return result.Distinct().Take(cap).ToList();
    }

    /// <summary>Runs a trace (the body of a <c>trace.start</c> job).</summary>
    public TraceStartJobResult Run(JobContext job, IReadOnlyList<MethodBase> methods, TraceStartParams p, MethodBase? stopOn)
    {
        var clock = Stopwatch.StartNew();
        var session = new TraceSession(this, job.JobId, p, stopOn);
        _sessions[job.JobId] = session;
        var failures = new List<MethodFailure>();
        var patched = new List<MethodBase>();
        var source = new JsonObject { { "methods", JsonValue.From(methods.Count) } };
        using var writer = new NdjsonFileWriter(p.OutFile, "trace", "1", _agentVersion, source);
        var kept = new List<TraceEntry>();
        try
        {
            foreach (var method in methods)
            {
                try
                {
                    _instrumenter.Attach(method, session, force: false);
                    patched.Add(method);
                }
                catch (ProtocolException e)
                {
                    failures.Add(new MethodFailure { Method = AnchorWriter.ForMember(method), Reason = e.Message });
                }
            }

            if (patched.Count == 0)
            {
                session.RequestStop("noMethods");
            }

            var durationMs = p.Stop?.DurationMs;
            while (session.StopReason is null)
            {
                if (job.Cancellation.WaitHandle.WaitOne(25))
                {
                    session.RequestStop("cancelled");
                }
                else if (durationMs is { } limit && clock.ElapsedMilliseconds >= limit)
                {
                    session.RequestStop("duration");
                }

                Drain(session, writer, kept, job);
            }
        }
        finally
        {
            foreach (var method in patched)
            {
                _instrumenter.Detach(method, session);
            }

            _sessions.TryRemove(job.JobId, out _);
        }

        Drain(session, writer, kept, job);
        var file = writer.Complete();
        return new TraceStartJobResult
        {
            File = file,
            Records = kept.Count,
            Truncated = session.Truncated,
            StopReason = session.StopReason!,
            DroppedAfterSeq = session.DroppedAfterSeq,
            MethodsPatched = patched.Count,
            MethodsFailed = failures,
            Summary = Summarize(kept),
        };
    }

    /// <summary><c>trace.stop</c>.</summary>
    public bool Stop(string jobId)
    {
        if (!_sessions.TryGetValue(jobId, out var session))
        {
            return false;
        }

        session.RequestStop("stopped");
        return true;
    }

    /// <summary>Stops every trace (instrumentation.clear, shutdown).</summary>
    public int StopAll()
    {
        var sessions = _sessions.Values.ToList();
        foreach (var session in sessions)
        {
            session.RequestStop("cleared");
        }

        return sessions.Count;
    }

    internal RecordBuilder Records => _records;

    internal Instrumenter Instrumenter => _instrumenter;

    internal int MainThreadId => _pump.MainThreadId;

    // Builds the summary from the records kept (in sequence order).
    internal static TraceSummary Summarize(IReadOnlyList<TraceEntry> entries)
    {
        var methods = entries.Where(e => e.Phase != "enter" && e.DurationUs is not null).GroupBy(e => e.Key)
            .Select(g => new MethodTiming
            {
                Method = g.First().Anchor,
                Calls = g.Count(),
                TotalMs = g.Sum(e => e.DurationUs!.Value) / 1000.0,
                MeanUs = g.Average(e => (double)e.DurationUs!.Value),
            })
            .OrderByDescending(m => m.TotalMs).ToList();

        var sequence = new List<SequenceEntry>();
        foreach (var entry in entries.Where(e => e.Phase == "enter" && e.Depth == 0))
        {
            if (sequence.Count > 0 && AnchorWriter.ToJson(sequence[sequence.Count - 1].Method).ToString() == entry.Key)
            {
                sequence[sequence.Count - 1].Count++;
            }
            else if (sequence.Count < MaxSequence)
            {
                sequence.Add(new SequenceEntry { Method = entry.Anchor, Count = 1 });
            }
        }

        // The call tree of the first occurrence of each root method.
        var trees = new JsonArray();
        var seenRoots = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < entries.Count; i++)
        {
            var root = entries[i];
            if (root.Phase != "enter" || root.Depth != 0 || !seenRoots.Add(root.Key))
            {
                continue;
            }

            var nodes = 0;
            var index = i;
            trees.Add(Node(entries, ref index, root.ThreadId, ref nodes));
        }

        return new TraceSummary { Sequence = sequence, Tree = trees, Methods = methods };
    }

    // One node: the call at entries[index] (an enter) and its nested calls until its exit on the same thread.
    private static JsonObject Node(IReadOnlyList<TraceEntry> entries, ref int index, int thread, ref int nodes)
    {
        var enter = entries[index];
        nodes++;
        var children = new JsonArray();
        JsonObject? previous = null;
        for (index++; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (entry.ThreadId != thread)
            {
                continue;
            }

            if (entry.Depth == enter.Depth && entry.Phase != "enter")
            {
                break; // this call's exit
            }

            if (entry.Phase == "enter" && entry.Depth == enter.Depth + 1)
            {
                if (nodes >= MaxTreeNodes)
                {
                    continue;
                }

                var child = Node(entries, ref index, thread, ref nodes);
                if (previous is not null && previous["method"]!.ToString() == child["method"]!.ToString() && !child.ContainsKey("children") && !previous.ContainsKey("children"))
                {
                    previous.Set("count", JsonValue.From(((JsonNumber)previous["count"]!).TryGetInt64(out var n) ? n + 1 : 2));
                }
                else
                {
                    children.Add(child);
                    previous = child;
                }
            }
        }

        var node = new JsonObject { { "method", AnchorWriter.ToJson(enter.Anchor) }, { "count", JsonValue.From(1L) } };
        if (children.Count > 0)
        {
            node.Add("children", children);
        }

        return node;
    }

    private void Drain(TraceSession session, NdjsonFileWriter writer, List<TraceEntry> kept, JobContext job)
    {
        var batch = new List<InstrumentationRecord>();
        while (session.Pending.TryDequeue(out var record))
        {
            writer.Write(record.ToJson());
            kept.Add(new TraceEntry(record));
            batch.Add(record);
        }

        if (batch.Count > 0)
        {
            job.Progress("trace", kept.Count, null);
            if (session.Stream && _events.HasSubscribers(EventKinds.TraceRecords))
            {
                _events.Publish(EventKinds.TraceRecords, new TraceRecordsEventParams { JobId = session.JobId, Items = batch, Dropped = 0 });
            }
        }
    }
}

/// <summary>What the summary needs from a record.</summary>
internal sealed class TraceEntry
{
    public TraceEntry(InstrumentationRecord record)
    {
        Anchor = record.Method;
        Key = AnchorWriter.ToJson(record.Method).ToString();
        Phase = record.Phase;
        Depth = (int)record.Depth;
        ThreadId = (int)record.ThreadId;
        DurationUs = record.DurationUs;
    }

    public Anchor Anchor { get; }

    public string Key { get; }

    public string Phase { get; }

    public int Depth { get; }

    public int ThreadId { get; }

    public long? DurationUs { get; }
}

/// <summary>One trace's sink: records every call of its methods with a per-thread depth of its own.</summary>
internal sealed class TraceSession : IMethodSink
{
    private readonly TraceManager _manager;
    private readonly CaptureOptions _capture;
    private readonly bool _mainOnly;
    private readonly long _maxRecords;
    private readonly MethodBase? _stopOn;
    private readonly string _stopPhase;
    private readonly ThreadLocal<int> _depth = new(() => 0);
    private long _count;
    private string? _stopReason;

    public TraceSession(TraceManager manager, string jobId, TraceStartParams p, MethodBase? stopOn)
    {
        _manager = manager;
        JobId = jobId;
        _capture = new CaptureOptions { Args = p.Capture?.Args ?? false, Result = p.Capture?.Result ?? false, Depth = 1 };
        _mainOnly = (p.Threads ?? "main") == "main";
        _maxRecords = Math.Max(1, p.Stop?.MaxRecords ?? 200_000);
        _stopOn = stopOn;
        _stopPhase = p.Stop?.OnMethodPhase ?? "exit";
        Stream = p.Stream ?? false;
    }

    public string JobId { get; }

    public bool Stream { get; }

    public ConcurrentQueue<InstrumentationRecord> Pending { get; } = new();

    public string? StopReason => Volatile.Read(ref _stopReason);

    public bool Truncated { get; private set; }

    public long? DroppedAfterSeq { get; private set; }

    public void RequestStop(string reason) => Interlocked.CompareExchange(ref _stopReason, reason, null);

    public object? Enter(CallContext call)
    {
        if (StopReason is not null || (_mainOnly && call.ThreadId != _manager.MainThreadId))
        {
            return null;
        }

        var depth = _depth.Value++;
        Add(call, "enter", depth, null, false, null, null);
        if (_stopOn is not null && _stopPhase == "enter" && call.Method.MetadataToken == _stopOn.MetadataToken && call.Method.Module == _stopOn.Module)
        {
            RequestStop("onMethod");
        }

        return depth;
    }

    public void Exit(CallContext call, object token, object? result, bool hasResult, long elapsedTicks)
    {
        var depth = (int)token;
        _depth.Value = depth;
        Add(call, "exit", depth, result, hasResult, null, elapsedTicks);
        if (_stopOn is not null && _stopPhase == "exit" && call.Method.MetadataToken == _stopOn.MetadataToken && call.Method.Module == _stopOn.Module)
        {
            RequestStop("onMethod");
        }
    }

    public void Throw(CallContext call, object token, Exception exception, long elapsedTicks)
    {
        var depth = (int)token;
        _depth.Value = depth;
        Add(call, "throw", depth, null, false, exception, elapsedTicks);
    }

    private void Add(CallContext call, string phase, int depth, object? result, bool hasResult, Exception? exception, long? elapsedTicks)
    {
        if (Interlocked.Increment(ref _count) > _maxRecords)
        {
            if (!Truncated)
            {
                Truncated = true;
                RequestStop("maxRecords");
            }

            return;
        }

        var record = _manager.Records.Build(_manager.Instrumenter, call, phase, _capture, result, hasResult, exception, elapsedTicks);
        record.Depth = depth;
        record.JobId = JobId;
        record.Rec = "call";
        if (Truncated)
        {
            return;
        }

        DroppedAfterSeq = record.Seq;
        Pending.Enqueue(record);
    }
}

/// <summary>
/// Profiles: per-method call counts and durations (mean, p50, p95 from streaming P² estimators, max, by thread) and
/// the frame-time distribution over a window.
/// </summary>
public sealed class ProfileManager
{
    private readonly ConcurrentDictionary<string, ProfileSession> _sessions = new(StringComparer.Ordinal);
    private readonly Instrumenter _instrumenter;
    private readonly RecordBuilder _records;
    private readonly MainThreadPump _pump;
    private readonly string _agentVersion;

    /// <summary>Creates the manager.</summary>
    public ProfileManager(Instrumenter instrumenter, RecordBuilder records, MainThreadPump pump, string agentVersion)
    {
        _instrumenter = instrumenter;
        _records = records;
        _pump = pump;
        _agentVersion = agentVersion;
    }

    /// <summary>Profiles running.</summary>
    public int Count => _sessions.Count;

    /// <summary>Runs a profile (the body of a <c>profile.start</c> job).</summary>
    public ProfileStartJobResult Run(JobContext job, IReadOnlyList<MethodBase> methods, ProfileStartParams p)
    {
        var session = new ProfileSession(this, p.OutFile is not null);
        _sessions[job.JobId] = session;
        var patched = new List<MethodBase>();
        var frames = new List<double>();
        void OnFrame(Abstractions.FrameTime clock)
        {
            lock (frames)
            {
                frames.Add(clock.DeltaTime * 1000);
            }
        }

        _pump.Ticked += OnFrame;
        try
        {
            foreach (var method in methods)
            {
                _instrumenter.Attach(method, session, force: false);
                patched.Add(method);
            }

            var end = Stopwatch.StartNew();
            while (end.ElapsedMilliseconds < p.DurationMs && !session.Stopped)
            {
                if (job.Cancellation.WaitHandle.WaitOne(50))
                {
                    job.Cancellation.ThrowIfCancellationRequested();
                }

                job.Progress("profile", end.ElapsedMilliseconds, p.DurationMs);
            }
        }
        finally
        {
            _pump.Ticked -= OnFrame;
            foreach (var method in patched)
            {
                _instrumenter.Detach(method, session);
            }

            _sessions.TryRemove(job.JobId, out _);
        }

        OutputFile? file = null;
        if (p.OutFile is not null)
        {
            using var writer = new NdjsonFileWriter(p.OutFile, "trace", "1", _agentVersion, new JsonObject { { "profile", JsonValue.From(true) } });
            foreach (var record in session.Calls)
            {
                writer.Write(record.ToJson());
            }

            file = writer.Complete();
        }

        double[] frameTimes;
        lock (frames)
        {
            frameTimes = frames.OrderBy(f => f).ToArray();
        }

        return new ProfileStartJobResult
        {
            Methods = methods.Select(m => session.Result(m)).ToList(),
            Frames = new FrameTimeSummary
            {
                Count = frameTimes.Length,
                MeanMs = frameTimes.Length == 0 ? 0 : frameTimes.Average(),
                P95Ms = frameTimes.Length == 0 ? 0 : frameTimes[(int)Math.Min(frameTimes.Length - 1, Math.Ceiling(frameTimes.Length * 0.95) - 1)],
                MaxMs = frameTimes.Length == 0 ? 0 : frameTimes[frameTimes.Length - 1],
            },
            File = file,
        };
    }

    /// <summary>Stops every profile early (instrumentation.clear, shutdown).</summary>
    public int StopAll()
    {
        var sessions = _sessions.Values.ToList();
        foreach (var session in sessions)
        {
            session.Stopped = true;
        }

        return sessions.Count;
    }

    internal RecordBuilder Records => _records;

    internal Instrumenter Instrumenter => _instrumenter;
}

/// <summary>One profile's sink: timing per method.</summary>
internal sealed class ProfileSession : IMethodSink
{
    private const int MaxCallRecords = 200_000;
    private static readonly object Token = new();
    private readonly ProfileManager _manager;
    private readonly bool _keepCalls;
    private readonly ConcurrentDictionary<MethodBase, MethodStats> _stats = new(PatchDispatch.Comparer);
    private readonly ConcurrentQueue<InstrumentationRecord> _calls = new();

    public ProfileSession(ProfileManager manager, bool keepCalls)
    {
        _manager = manager;
        _keepCalls = keepCalls;
    }

    public volatile bool Stopped;

    public IEnumerable<InstrumentationRecord> Calls => _calls;

    public object? Enter(CallContext call) => Stopped ? null : Token;

    public void Exit(CallContext call, object token, object? result, bool hasResult, long elapsedTicks) => Add(call, elapsedTicks, "exit");

    public void Throw(CallContext call, object token, Exception exception, long elapsedTicks) => Add(call, elapsedTicks, "throw");

    public MethodProfile Result(MethodBase method)
    {
        var stats = _stats.TryGetValue(method, out var s) ? s : new MethodStats();
        lock (stats)
        {
            var byThread = new JsonObject();
            foreach (var pair in stats.ByThread.OrderBy(p => p.Key))
            {
                byThread.Add(pair.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), JsonValue.From(pair.Value));
            }

            return new MethodProfile
            {
                Method = AnchorWriter.ForMember(method),
                Calls = stats.Calls,
                TotalMs = stats.TotalUs / 1000.0,
                MeanUs = stats.Calls == 0 ? 0 : stats.TotalUs / stats.Calls,
                P50Us = stats.P50.Value,
                P95Us = stats.P95.Value,
                MaxUs = stats.MaxUs,
                ByThread = byThread,
            };
        }
    }

    private void Add(CallContext call, long elapsedTicks, string phase)
    {
        var us = elapsedTicks * 1_000_000.0 / Stopwatch.Frequency;
        var stats = _stats.GetOrAdd(call.Method, _ => new MethodStats());
        lock (stats)
        {
            stats.Calls++;
            stats.TotalUs += us;
            stats.MaxUs = Math.Max(stats.MaxUs, us);
            stats.P50.Add(us);
            stats.P95.Add(us);
            stats.ByThread[call.ThreadId] = (stats.ByThread.TryGetValue(call.ThreadId, out var n) ? n : 0) + 1;
        }

        if (_keepCalls && _calls.Count < MaxCallRecords)
        {
            var record = _manager.Records.Build(_manager.Instrumenter, call, phase, CaptureOptions.None, elapsedTicks: elapsedTicks);
            record.Rec = "call";
            _calls.Enqueue(record);
        }
    }

    private sealed class MethodStats
    {
        public long Calls;
        public double TotalUs;
        public double MaxUs;
        public readonly P2Quantile P50 = new(0.5);
        public readonly P2Quantile P95 = new(0.95);
        public readonly Dictionary<int, long> ByThread = new();
    }
}

/// <summary>A streaming quantile estimate in constant memory (the P² algorithm, Jain and Chlamtac 1985).</summary>
public sealed class P2Quantile
{
    private readonly double _p;
    private readonly double[] _heights = new double[5];
    private readonly double[] _positions = new double[5];
    private readonly double[] _desired = new double[5];
    private readonly double[] _increments = new double[5];
    private int _count;

    /// <summary>Creates an estimator of the <paramref name="p"/> quantile (0–1).</summary>
    public P2Quantile(double p)
    {
        _p = p;
        _increments[0] = 0;
        _increments[1] = p / 2;
        _increments[2] = p;
        _increments[3] = (1 + p) / 2;
        _increments[4] = 1;
    }

    /// <summary>The current estimate (exact for fewer than five samples).</summary>
    public double Value
    {
        get
        {
            if (_count == 0)
            {
                return 0;
            }

            if (_count < 5)
            {
                var sorted = _heights.Take(_count).OrderBy(h => h).ToArray();
                return sorted[(int)Math.Min(sorted.Length - 1, Math.Round(_p * (sorted.Length - 1)))];
            }

            return _heights[2];
        }
    }

    /// <summary>Adds a sample.</summary>
    public void Add(double x)
    {
        if (_count < 5)
        {
            _heights[_count++] = x;
            if (_count == 5)
            {
                Array.Sort(_heights);
                for (var i = 0; i < 5; i++)
                {
                    _positions[i] = i + 1;
                }

                _desired[0] = 1;
                _desired[1] = 1 + (2 * _p);
                _desired[2] = 1 + (4 * _p);
                _desired[3] = 3 + (2 * _p);
                _desired[4] = 5;
            }

            return;
        }

        _count++;
        int k;
        if (x < _heights[0])
        {
            _heights[0] = x;
            k = 0;
        }
        else if (x >= _heights[4])
        {
            _heights[4] = x;
            k = 3;
        }
        else
        {
            k = 0;
            while (k < 3 && x >= _heights[k + 1])
            {
                k++;
            }
        }

        for (var i = k + 1; i < 5; i++)
        {
            _positions[i]++;
        }

        for (var i = 0; i < 5; i++)
        {
            _desired[i] += _increments[i];
        }

        for (var i = 1; i <= 3; i++)
        {
            var d = _desired[i] - _positions[i];
            if ((d >= 1 && _positions[i + 1] - _positions[i] > 1) || (d <= -1 && _positions[i - 1] - _positions[i] < -1))
            {
                var sign = Math.Sign(d);
                var parabolic = _heights[i] + (sign / (_positions[i + 1] - _positions[i - 1]) * (((_positions[i] - _positions[i - 1] + sign) * (_heights[i + 1] - _heights[i]) / (_positions[i + 1] - _positions[i]))
                    + ((_positions[i + 1] - _positions[i] - sign) * (_heights[i] - _heights[i - 1]) / (_positions[i] - _positions[i - 1]))));
                _heights[i] = _heights[i - 1] < parabolic && parabolic < _heights[i + 1] ? parabolic
                    : _heights[i] + (sign * (_heights[i + sign] - _heights[i]) / (_positions[i + sign] - _positions[i]));
                _positions[i] += sign;
            }
        }
    }
}
