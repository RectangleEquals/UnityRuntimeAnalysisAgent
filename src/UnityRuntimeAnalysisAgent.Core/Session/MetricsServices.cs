using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Abstractions;
using UnityRuntimeAnalysisAgent.Core.Dispatch;
using UnityRuntimeAnalysisAgent.Core.Jobs;
using UnityRuntimeAnalysisAgent.Core.Runtime;

namespace UnityRuntimeAnalysisAgent.Core.Session;

/// <summary>
/// Performance and memory: frame times over the last frames (from the pump's clock), managed memory and collections,
/// Unity's profiler memory counters, optional object counts, and the process. <c>metrics.get</c> reads once;
/// <c>metrics.sample.start</c> writes a time series (NDJSON) as a job.
/// </summary>
internal sealed class MetricsServices
{
    public const int FrameWindow = 300;

    // Counted with Resources.FindObjectsOfTypeAll when object counts are asked for.
    private static readonly string[] CountedTypes =
    {
        "UnityEngine.GameObject", "UnityEngine.Component", "UnityEngine.MonoBehaviour", "UnityEngine.Texture2D", "UnityEngine.Sprite", "UnityEngine.Mesh",
        "UnityEngine.Material", "UnityEngine.AudioClip", "UnityEngine.ScriptableObject",
    };

    private readonly MainThreadPump _pump;
    private readonly IUnityApi _unity;
    private readonly JobManager _jobs;
    private readonly string _agentVersion;
    private readonly double[] _frameMs = new double[FrameWindow];
    private readonly object _gate = new();
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private int _frames;
    private int _next;
    private double _lastRealtime = -1;

    public MetricsServices(MainThreadPump pump, IUnityApi unity, JobManager jobs, string agentVersion)
    {
        _pump = pump;
        _unity = unity;
        _jobs = jobs;
        _agentVersion = agentVersion;
        pump.Ticked += OnTick;
    }

    [RpcMethod(Methods.MetricsGet)]
    public Deferred Get(RequestContext context, MetricsGetParams p)
    {
        var deferred = new Deferred();
        var includeCounts = p.IncludeObjectCounts ?? false;
        _pump.Enqueue(new PumpWork(
            () => MainThreadPart(includeCounts),
            main =>
            {
                try
                {
                    deferred.Complete(Complete((Metrics)main!));
                }
                catch (Exception e)
                {
                    deferred.Fail(e);
                }
            },
            e => deferred.Fail(e),
            context.Cancellation));
        return deferred;
    }

    [RpcMethod(Methods.MetricsSampleStart)]
    public ProtocolMessage SampleStart(RequestContext context, MetricsSampleStartParams p)
    {
        if (!System.IO.Path.IsPathRooted(p.OutFile))
        {
            throw ProtocolException.InvalidParams("params.outFile", "outFile must be an absolute path.");
        }

        var everyFrames = (int)Math.Max(1, Math.Min(p.EveryFrames, 100_000));
        var durationMs = Math.Max(1, p.DurationMs);
        return _jobs.Start("metrics.sample", job =>
        {
            var samples = new ConcurrentQueue<Metrics>();
            var counter = 0;
            void Sample(FrameTime clock)
            {
                if (++counter % everyFrames == 0)
                {
                    samples.Enqueue(MainThreadPart(includeCounts: false));
                }
            }

            using var writer = new NdjsonFileWriter(p.OutFile, "metrics", "1", _agentVersion);
            var written = 0L;
            var clock = Stopwatch.StartNew();
            _pump.Ticked += Sample;
            try
            {
                while (clock.ElapsedMilliseconds < durationMs && !job.Cancellation.IsCancellationRequested)
                {
                    written += Drain(samples, writer);
                    job.Progress("sample", clock.ElapsedMilliseconds, durationMs, $"{written} samples");
                    job.Cancellation.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(Math.Min(100, Math.Max(1, durationMs - clock.ElapsedMilliseconds))));
                }
            }
            finally
            {
                _pump.Ticked -= Sample;
            }

            written += Drain(samples, writer);
            job.Cancellation.ThrowIfCancellationRequested();
            return new MetricsSampleStartJobResult { File = writer.Complete(), Samples = written };
        });
    }

    private long Drain(ConcurrentQueue<Metrics> samples, NdjsonFileWriter writer)
    {
        var count = 0L;
        while (samples.TryDequeue(out var sample))
        {
            var complete = Complete(sample);
            complete.Rec = "sample";
            writer.Write(complete.ToJson());
            count++;
        }

        return count;
    }

    // What has to be read on the main thread: frame stats, the profiler, object counts.
    private Metrics MainThreadPart(bool includeCounts)
    {
        var clock = _pump.Clock;
        var memory = _unity.ReadProfilerMemory();
        var metrics = new Metrics
        {
            FrameTimeMs = FrameStats(out var fps),
            Fps = fps,
            MonoUsed = memory.MonoUsed,
            MonoHeap = memory.MonoHeap,
            TotalAllocated = memory.TotalAllocated,
            TotalReserved = memory.TotalReserved,
            Frame = clock.FrameCount,
            RealtimeMs = (long)(clock.Realtime * 1000),
        };
        if (includeCounts)
        {
            var counts = new JsonObject();
            foreach (var name in CountedTypes)
            {
                if (FindType(name) is { } type)
                {
                    counts.Add(name.Substring("UnityEngine.".Length), new JsonNumber((long)_unity.FindObjectsOfTypeAll(type).Count));
                }
            }

            metrics.ObjectCounts = counts;
        }

        return metrics;
    }

    // Anything thread-safe: managed memory, collections, the process.
    private Metrics Complete(Metrics metrics)
    {
        metrics.GcTotalMemory = GC.GetTotalMemory(false);
        metrics.GcCollections = Enumerable.Range(0, GC.MaxGeneration + 1).Select(g => (long)GC.CollectionCount(g)).ToList();
        var (workingSet, privateBytes, threads) = Diagnostics.ProcessStats.Read();
        metrics.ProcessWorkingSet = workingSet;
        metrics.ProcessPrivateBytes = privateBytes;
        metrics.ThreadCount = threads;
        try
        {
            using var process = Process.GetCurrentProcess();
            metrics.UptimeMs = Safe(() => (long)(DateTime.Now - process.StartTime).TotalMilliseconds, (long)_uptime.Elapsed.TotalMilliseconds);
        }
        catch (Exception)
        {
            metrics.UptimeMs = (long)_uptime.Elapsed.TotalMilliseconds;
        }

        return metrics;
    }

    private FrameTimeStats FrameStats(out double fps)
    {
        double[] window;
        lock (_gate)
        {
            window = _frames < FrameWindow ? _frameMs.Take(_frames).ToArray() : (double[])_frameMs.Clone();
        }

        if (window.Length == 0)
        {
            fps = 0;
            return new FrameTimeStats();
        }

        Array.Sort(window);
        var mean = window.Average();
        fps = mean > 0 ? Math.Round(1000.0 / mean, 1) : 0;
        return new FrameTimeStats
        {
            Mean = Math.Round(mean, 3),
            P95 = Math.Round(window[Math.Min(window.Length - 1, (int)Math.Ceiling(window.Length * 0.95) - 1)], 3),
            Max = Math.Round(window[window.Length - 1], 3),
        };
    }

    private void OnTick(FrameTime clock)
    {
        var last = _lastRealtime;
        _lastRealtime = clock.Realtime;
        if (last < 0 || clock.Realtime <= last)
        {
            return;
        }

        lock (_gate)
        {
            _frameMs[_next] = (clock.Realtime - last) * 1000;
            _next = (_next + 1) % FrameWindow;
            _frames = Math.Min(FrameWindow, _frames + 1);
        }
    }

    private static Type? FindType(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                if (assembly.GetType(fullName, throwOnError: false) is { } type)
                {
                    return type;
                }
            }
            catch (Exception)
            {
                // A broken assembly: skip it.
            }
        }

        return null;
    }

    private static long Safe(Func<long> read, long fallback = 0)
    {
        try
        {
            return Math.Max(0, read());
        }
        catch (Exception)
        {
            return fallback;
        }
    }
}
