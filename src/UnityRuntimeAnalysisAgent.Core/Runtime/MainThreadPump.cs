using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using UnityLudometry.Protocol;
using UnityRuntimeAnalysisAgent.Core.Abstractions;
using UnityRuntimeAnalysisAgent.Core.Hosting;

namespace UnityRuntimeAnalysisAgent.Core.Runtime;

/// <summary>A piece of main-thread work: a body that returns a result, or a routine (an iterator, see <see cref="PumpWait"/>).</summary>
public sealed class PumpWork
{
    /// <summary>Creates the work item.</summary>
    /// <param name="body">Runs on the main thread. Returns the result, or an <see cref="IEnumerable"/>/<see cref="IEnumerator"/> routine.</param>
    /// <param name="completed">Called (on the main thread) with the result.</param>
    /// <param name="failed">Called (on the main thread) with the failure; cancellation arrives as <see cref="OperationCanceledException"/>.</param>
    /// <param name="cancellation">Checked before the body starts and at every yield.</param>
    public PumpWork(Func<object?> body, Action<object?> completed, Action<Exception> failed, CancellationToken cancellation = default)
    {
        Body = body;
        Completed = completed;
        Failed = failed;
        Cancellation = cancellation;
    }

    internal Func<object?> Body { get; }

    internal Action<object?> Completed { get; }

    internal Action<Exception> Failed { get; }

    internal CancellationToken Cancellation { get; }
}

/// <summary>
/// Runs main-thread work in the game's frames. Every frame (<see cref="Tick"/>) it publishes the <see cref="Clock"/>,
/// resumes routines whose wait is over, and starts queued work, within <c>Pump.FrameBudgetMs</c>: what doesn't fit runs in
/// the next frame (at least one step always runs, so work never starves). Every step is isolated: a failing item fails
/// only itself, and nothing is ever thrown into the game.
/// </summary>
public sealed class MainThreadPump
{
    private readonly IUnityApi _unity;
    private readonly IAgentLogger _log;
    private readonly Func<double> _nowMs;
    private readonly object _gate = new();
    private readonly Queue<PumpWork> _queue = new();
    private readonly List<Routine> _routines = new();
    private readonly List<Routine> _endOfFrame = new();
    private FrameTime _clock;
    private long _ticks;
    private double _lastTickAtMs;
    private int _started;

    /// <summary>Creates the pump. <paramref name="nowMs"/> is a monotonic clock in milliseconds (default: a stopwatch).</summary>
    public MainThreadPump(IUnityApi unity, double frameBudgetMs, IAgentLogger log, Func<double>? nowMs = null)
    {
        _unity = unity;
        FrameBudgetMs = frameBudgetMs;
        _log = log;
        var stopwatch = Stopwatch.StartNew();
        _nowMs = nowMs ?? (() => stopwatch.Elapsed.TotalMilliseconds);
    }

    /// <summary>The main-thread budget per frame.</summary>
    public double FrameBudgetMs { get; }

    /// <summary>The latest frame timing (safe to read from any thread).</summary>
    public FrameTime Clock
    {
        get
        {
            lock (_gate)
            {
                return _clock;
            }
        }
    }

    /// <summary>Whether a frame has ticked since the pump started.</summary>
    public bool HasTicked => Interlocked.Read(ref _ticks) > 0;

    /// <summary>Whether the pump host is running.</summary>
    public bool IsStarted => Volatile.Read(ref _started) != 0;

    /// <summary>Whether the watchdog found the main thread not ticking.</summary>
    public bool IsStalled { get; internal set; }

    /// <summary>When the last tick happened (monotonic milliseconds).</summary>
    public double LastTickAtMs
    {
        get
        {
            lock (_gate)
            {
                return _lastTickAtMs;
            }
        }
    }

    /// <summary>How many times the pump host was recreated.</summary>
    public long RecreatedCount { get; internal set; }

    /// <summary>Queued work items plus running routines.</summary>
    public int QueueLength
    {
        get
        {
            lock (_gate)
            {
                return _queue.Count + _routines.Count + _endOfFrame.Count;
            }
        }
    }

    internal double NowMs => _nowMs();

    /// <summary>Creates the pump host (the game then calls <see cref="Tick"/> every frame).</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            lock (_gate)
            {
                _lastTickAtMs = _nowMs();
            }

            _unity.CreatePumpHost(Tick, EndOfFrame);
        }
    }

    /// <summary>Destroys the pump host and fails everything still waiting with <c>MAIN_THREAD_UNAVAILABLE</c>.</summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
        {
            return;
        }

        try
        {
            _unity.DestroyPumpHost();
        }
        catch (Exception e)
        {
            _log.Warning("Destroying the pump host failed.", e);
        }

        FailAll(new ProtocolException(ErrorCodes.MainThreadUnavailable, "The agent is shutting down."));
    }

    /// <summary>Queues main-thread work. It runs in the next frame (FIFO), within the budget.</summary>
    public void Enqueue(PumpWork work)
    {
        if (!IsStarted)
        {
            SafeInvoke(() => work.Failed(new ProtocolException(ErrorCodes.MainThreadUnavailable, "The main-thread pump isn't running (no Unity host).")));
            return;
        }

        lock (_gate)
        {
            _queue.Enqueue(work);
        }
    }

    /// <summary>One frame: publish the clock, resume due routines, then start queued work, within the budget.</summary>
    public void Tick()
    {
        try
        {
            var clock = _unity.ReadFrameTime();
            var start = _nowMs();
            lock (_gate)
            {
                _clock = clock;
                _lastTickAtMs = start;
            }

            Interlocked.Increment(ref _ticks);
            IsStalled = false;
            var ranAny = false;
            bool BudgetLeft() => !ranAny || _nowMs() - start < FrameBudgetMs;

            foreach (var routine in SnapshotRoutines())
            {
                if (!BudgetLeft())
                {
                    break;
                }

                if (routine.IsDue(clock, _nowMs()))
                {
                    Step(routine, clock);
                    ranAny = true;
                }
            }

            while (BudgetLeft())
            {
                PumpWork? work;
                lock (_gate)
                {
                    if (_queue.Count == 0)
                    {
                        break;
                    }

                    work = _queue.Dequeue();
                }

                Begin(work, clock);
                ranAny = true;
            }
        }
        catch (Exception e)
        {
            // Never let the agent break the game's frame.
            _log.Error("The main-thread pump failed in a frame.", e);
        }
    }

    /// <summary>End of the frame: resume routines that yielded <see cref="PumpWait.EndOfFrame"/>.</summary>
    public void EndOfFrame()
    {
        try
        {
            List<Routine> due;
            lock (_gate)
            {
                due = new List<Routine>(_endOfFrame);
                _endOfFrame.Clear();
            }

            var clock = Clock;
            foreach (var routine in due)
            {
                Step(routine, clock);
            }
        }
        catch (Exception e)
        {
            _log.Error("The main-thread pump failed at the end of a frame.", e);
        }
    }

    /// <summary>Runs work right now on the calling thread, which must be the main thread (used by same-frame batches).
    /// Routines aren't allowed here: they would span frames.</summary>
    public object? RunInline(Func<object?> body)
    {
        var result = body();
        if (result is IEnumerable and not string || result is IEnumerator)
        {
            throw ProtocolException.InvalidParams("params.requests", "A multi-frame method can't run in a same-frame batch.");
        }

        return result;
    }

    internal void FailAll(Exception error)
    {
        List<PumpWork> queued;
        List<Routine> routines;
        lock (_gate)
        {
            queued = new List<PumpWork>(_queue);
            _queue.Clear();
            routines = new List<Routine>(_routines);
            routines.AddRange(_endOfFrame);
            _routines.Clear();
            _endOfFrame.Clear();
        }

        foreach (var work in queued)
        {
            SafeInvoke(() => work.Failed(error));
        }

        foreach (var routine in routines)
        {
            routine.Dispose();
            SafeInvoke(() => routine.Work.Failed(error));
        }
    }

    private List<Routine> SnapshotRoutines()
    {
        lock (_gate)
        {
            return new List<Routine>(_routines);
        }
    }

    private void Begin(PumpWork work, FrameTime clock)
    {
        if (work.Cancellation.IsCancellationRequested)
        {
            SafeInvoke(() => work.Failed(new OperationCanceledException(work.Cancellation)));
            return;
        }

        object? result;
        try
        {
            result = work.Body();
        }
        catch (Exception e)
        {
            SafeInvoke(() => work.Failed(e));
            return;
        }

        var iterator = result switch
        {
            // An iterator method's object is both: it must be started through GetEnumerator().
            IEnumerable e and not string => e.GetEnumerator(),
            IEnumerator e => e,
            _ => null,
        };
        if (iterator is null)
        {
            SafeInvoke(() => work.Completed(result));
            return;
        }

        var routine = new Routine(work, iterator);
        lock (_gate)
        {
            _routines.Add(routine);
        }

        Step(routine, clock);
    }

    private void Step(Routine routine, FrameTime clock)
    {
        var work = routine.Work;
        lock (_gate)
        {
            _routines.Remove(routine);
        }

        if (work.Cancellation.IsCancellationRequested)
        {
            routine.Dispose();
            SafeInvoke(() => work.Failed(new OperationCanceledException(work.Cancellation)));
            return;
        }

        try
        {
            if (routine.Wait is PumpWait.UntilWait until && !until.Predicate())
            {
                // Timed out (IsDue said it was due): not a result.
                routine.Dispose();
                SafeInvoke(() => work.Failed(new ProtocolException(ErrorCodes.Timeout, $"The condition didn't become true within {until.TimeoutMs:0} ms.")));
                return;
            }

            while (true)
            {
                if (!routine.Iterator.MoveNext())
                {
                    routine.Dispose();
                    SafeInvoke(() => work.Completed(null));
                    return;
                }

                var current = routine.Iterator.Current;
                if (current is PumpWait wait)
                {
                    if (wait is PumpWait.UntilWait u && u.Predicate())
                    {
                        continue; // already true: no need to wait a frame
                    }

                    routine.Arm(wait, clock, _nowMs());
                    lock (_gate)
                    {
                        (wait is PumpWait.EndOfFrameWait ? _endOfFrame : _routines).Add(routine);
                    }

                    return;
                }

                routine.Dispose();
                SafeInvoke(() => work.Completed(current));
                return;
            }
        }
        catch (Exception e)
        {
            routine.Dispose();
            SafeInvoke(() => work.Failed(e));
        }
    }

    private void SafeInvoke(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            _log.Error("A main-thread completion callback failed.", e);
        }
    }

    private sealed class Routine
    {
        private long _resumeFrame;
        private double _resumeAtMs;
        private double _deadlineMs;

        public Routine(PumpWork work, IEnumerator iterator)
        {
            Work = work;
            Iterator = iterator;
        }

        public PumpWork Work { get; }

        public IEnumerator Iterator { get; }

        public PumpWait? Wait { get; private set; }

        public void Arm(PumpWait wait, FrameTime clock, double nowMs)
        {
            Wait = wait;
            switch (wait)
            {
                case PumpWait.FramesWait frames:
                    _resumeFrame = clock.FrameCount + frames.Count;
                    break;
                case PumpWait.RealtimeWait realtime:
                    _resumeAtMs = nowMs + realtime.Milliseconds;
                    break;
                case PumpWait.UntilWait until:
                    _deadlineMs = nowMs + until.TimeoutMs;
                    break;
            }
        }

        public bool IsDue(FrameTime clock, double nowMs)
        {
            if (Work.Cancellation.IsCancellationRequested)
            {
                return true;
            }

            return Wait switch
            {
                PumpWait.FramesWait => clock.FrameCount >= _resumeFrame,
                PumpWait.RealtimeWait => nowMs >= _resumeAtMs,
                PumpWait.UntilWait until => nowMs >= _deadlineMs || SafePredicate(until.Predicate),
                _ => true,
            };
        }

        public void Dispose() => (Iterator as IDisposable)?.Dispose();

        private static bool SafePredicate(Func<bool> predicate)
        {
            try
            {
                return predicate();
            }
            catch
            {
                return true; // let Step run it again and report the exception
            }
        }
    }
}
