using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Envelopes;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Dispatch;
using UnityRuntimeAnalysisAgent.Core.Hosting;
using UnityRuntimeAnalysisAgent.Core.Runtime;

namespace UnityRuntimeAnalysisAgent.Core.Jobs;

/// <summary>What a job body gets: cancellation, progress reporting, and a way to run chunks on the main thread.</summary>
public sealed class JobContext
{
    private readonly JobManager.Job _job;
    private readonly JobManager _manager;

    internal JobContext(JobManager manager, JobManager.Job job)
    {
        _manager = manager;
        _job = job;
    }

    /// <summary>The job id.</summary>
    public string JobId => _job.Id;

    /// <summary>Cancelled by <c>job.cancel</c> or shutdown. Check it often.</summary>
    public CancellationToken Cancellation => _job.Cancellation.Token;

    /// <summary>The starting request's context (echoed into the job's events).</summary>
    public JsonObject? Context => _job.Context;

    /// <summary>Reports progress (events are throttled; <c>job.get</c> always shows the latest).</summary>
    public void Progress(string phase, long done, long? total = null, string? message = null) => _manager.ReportProgress(_job, phase, done, total, message);

    /// <summary>Runs a chunk on the main thread (within the frame budget) and waits for it, observing cancellation.</summary>
    public T RunOnMain<T>(Func<T> chunk) => _manager.RunOnMain(chunk, Cancellation);
}

/// <summary>
/// Long-running operations: a method marked <c>job</c> starts one and returns <c>{jobId, kind}</c> at once. Jobs run on
/// worker threads (below-normal priority), at most <c>Jobs.MaxConcurrent</c> at a time; the rest queue. State moves
/// queued → running → succeeded | failed | cancelled. <c>job.progress</c> (throttled) and <c>job.finished</c> events go to
/// subscribers, with the starting request's context.
/// </summary>
public sealed class JobManager : IDisposable
{
    /// <summary>Finished jobs kept for <c>job.get</c>.</summary>
    public const int KeepFinished = 200;

    /// <summary>Least time between two <c>job.progress</c> events of one job.</summary>
    public const int ProgressEventIntervalMs = 250;

    private readonly int _maxConcurrent;
    private readonly EventHub _events;
    private readonly MainThreadPump _pump;
    private readonly IAgentLogger _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly LinkedList<Job> _queue = new();
    private long _nextId;
    private int _running;
    private bool _stopped;

    /// <summary>Creates the manager.</summary>
    public JobManager(int maxConcurrent, EventHub events, MainThreadPump pump, IAgentLogger log)
    {
        _maxConcurrent = Math.Max(1, maxConcurrent);
        _events = events;
        _pump = pump;
        _log = log;
    }

    /// <summary>Jobs running now.</summary>
    public int Running
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    /// <summary>Starts (or queues) a job. The body returns the job's result (or null) and should observe cancellation.</summary>
    public JobRef Start(string kind, Func<JobContext, ProtocolMessage?> body, JsonObject? context = null)
    {
        Job job;
        lock (_gate)
        {
            if (_stopped)
            {
                throw new ProtocolException(ErrorCodes.Cancelled, "The agent is shutting down.");
            }

            job = new Job($"j-{++_nextId}", kind, body, context);
            _jobs[job.Id] = job;
            _queue.AddLast(job);
            Prune();
        }

        Pump();
        return new JobRef { JobId = job.Id, Kind = kind };
    }

    /// <summary>A job's state (<c>NOT_FOUND</c> if unknown or pruned).</summary>
    public JobInfo Get(string jobId)
    {
        lock (_gate)
        {
            return Find(jobId).Info();
        }
    }

    /// <summary>Waits for a job to finish, up to <paramref name="timeoutMs"/>; returns its state either way. It blocks the
    /// calling thread: request handlers use <see cref="WaitDeferred"/>.</summary>
    public JobInfo Wait(string jobId, int timeoutMs, CancellationToken cancellation)
    {
        Job job;
        lock (_gate)
        {
            job = Find(jobId);
        }

        WaitHandle.WaitAny(new[] { job.Finished.WaitHandle, cancellation.WaitHandle }, Math.Max(0, timeoutMs));
        cancellation.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return job.Info();
        }
    }

    /// <summary>Like <see cref="Wait"/>, without holding a thread: the result completes when the job finishes, when
    /// <paramref name="timeoutMs"/> has passed (with the job's current state), or fails when cancelled.</summary>
    public Deferred WaitDeferred(string jobId, int timeoutMs, CancellationToken cancellation)
    {
        var deferred = new Deferred();
        Timer? timer = null;
        CancellationTokenRegistration registration = default;
        Job job = null!;
        Action<JobInfo> onFinished = null!;

        // Whichever comes first (finish, timer, cancellation) completes the result and releases the other two.
        void Settle(bool completed)
        {
            if (!completed)
            {
                return;
            }

            timer?.Dispose();
            registration.Dispose();
            lock (_gate)
            {
                job.Waiters.Remove(onFinished);
            }
        }

        onFinished = info => Settle(deferred.Complete(info));
        lock (_gate)
        {
            job = Find(jobId);
            if (job.State is "succeeded" or "failed" or "cancelled")
            {
                deferred.Complete(job.Info());
                return deferred;
            }

            timer = new Timer(_ =>
            {
                JobInfo info;
                lock (_gate)
                {
                    info = job.Info();
                }

                Settle(deferred.Complete(info));
            }, null, Timeout.Infinite, Timeout.Infinite);
            job.Waiters.Add(onFinished);
        }

        registration = cancellation.Register(() => Settle(deferred.Fail(new OperationCanceledException(cancellation))));
        if (deferred.IsCompleted)
        {
            registration.Dispose(); // settled before the registration existed
            return deferred;
        }

        try
        {
            timer.Change(Math.Max(0, timeoutMs), Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // settled meanwhile
        }

        return deferred;
    }

    /// <summary>Cancels a job: a queued one at once, a running one cooperatively.</summary>
    public JobCancelResult Cancel(string jobId)
    {
        Job job;
        bool cancelled;
        lock (_gate)
        {
            job = Find(jobId);
            cancelled = job.State is "queued" or "running";
            if (job.State == "queued")
            {
                _queue.Remove(job);
                job.Cancellation.Cancel();
            }
            else if (job.State == "running")
            {
                job.Cancellation.Cancel();
            }
        }

        if (cancelled && job.State == "queued")
        {
            Finish(job, "cancelled", null, new ProtocolError { Code = ErrorCodes.Cancelled, Message = "The job was cancelled before it started." });
        }

        lock (_gate)
        {
            return new JobCancelResult { Cancelled = cancelled, State = job.State };
        }
    }

    /// <summary>Jobs, optionally in one state, oldest first.</summary>
    public List<JobInfo> List(string? state)
    {
        lock (_gate)
        {
            return _jobs.Values.OrderBy(j => j.Number).Where(j => state is null || j.State == state).Select(j => j.Info()).ToList();
        }
    }

    /// <summary>Cancels every job and waits (briefly) for running ones to stop. No new jobs start afterwards.</summary>
    public void CancelAll(int waitMs = 2000)
    {
        List<Job> active;
        lock (_gate)
        {
            _stopped = true;
            active = _jobs.Values.Where(j => j.State is "queued" or "running").ToList();
        }

        foreach (var job in active)
        {
            Cancel(job.Id);
        }

        var deadline = Stopwatch.StartNew();
        foreach (var job in active)
        {
            job.Finished.Wait(Math.Max(0, waitMs - (int)deadline.ElapsedMilliseconds));
        }
    }

    /// <inheritdoc />
    public void Dispose() => CancelAll();

    internal void ReportProgress(Job job, string phase, long done, long? total, string? message)
    {
        JobProgress progress;
        bool send;
        lock (_gate)
        {
            progress = new JobProgress { Phase = phase, Done = done, Total = total, Message = message };
            job.Progress = progress;
            send = job.ProgressClock.ElapsedMilliseconds >= ProgressEventIntervalMs || job.ProgressEvents == 0;
            if (send)
            {
                job.ProgressEvents++;
                job.ProgressClock.Restart();
            }
        }

        if (send)
        {
            SafePublish(EventKinds.JobProgress, new JobProgressEventParams { JobId = job.Id, Kind = job.Kind, Progress = progress }, job.Context);
        }
    }

    private sealed class Holder<T>
    {
        public Holder(T value) => Value = value;

        public T Value { get; }
    }

    internal T RunOnMain<T>(Func<T> chunk, CancellationToken cancellation)
    {
        T result = default!;
        Exception? error = null;
        using var done = new ManualResetEventSlim(false);
        // The result travels in a holder: a chunk that returns a collection must not be taken for a multi-frame routine.
        _pump.Enqueue(new PumpWork(() => new Holder<T>(chunk()), r =>
        {
            result = ((Holder<T>)r!).Value;
            done.Set();
        }, e =>
        {
            error = e;
            done.Set();
        }, cancellation));
        done.Wait(cancellation);
        if (error is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }

        return result;
    }

    private Job Find(string jobId) => _jobs.TryGetValue(jobId, out var job) ? job : throw AgentErrors.NotFound($"No job {jobId} (unknown, or finished long ago).");

    private void Pump()
    {
        while (true)
        {
            Job job;
            lock (_gate)
            {
                if (_running >= _maxConcurrent || _queue.Count == 0)
                {
                    return;
                }

                job = _queue.First!.Value;
                _queue.RemoveFirst();
                job.State = "running";
                job.StartedAt = Now();
                _running++;
            }

            var thread = new Thread(() => Run(job)) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = $"URAA job {job.Id} ({job.Kind})" };
            thread.Start();
        }
    }

    private void Run(Job job)
    {
        try
        {
            var result = job.Body(new JobContext(this, job));
            job.Cancellation.Token.ThrowIfCancellationRequested();
            Finish(job, "succeeded", result?.ToJson(), null);
        }
        catch (Exception e)
        {
            var error = AgentErrors.FromException(e, _log, $"Job {job.Id} ({job.Kind})");
            Finish(job, error.Code == ErrorCodes.Cancelled ? "cancelled" : "failed", null, error);
        }
        finally
        {
            lock (_gate)
            {
                _running--;
            }

            Pump();
        }
    }

    private void Finish(Job job, string state, JsonValue? result, ProtocolError? error)
    {
        JobInfo info;
        lock (_gate)
        {
            if (job.State is "succeeded" or "failed" or "cancelled")
            {
                return;
            }

            job.State = state;
            job.Result = result;
            job.Error = error;
            job.FinishedAt = Now();
            info = job.Info();
        }

        job.Finished.Set();
        List<Action<JobInfo>> waiters;
        lock (_gate)
        {
            waiters = job.Waiters.ToList();
        }

        foreach (var waiter in waiters)
        {
            waiter(info);
        }

        SafePublish(EventKinds.JobFinished, new JobFinishedEventParams { Job = info }, job.Context);
    }

    private void SafePublish(string kind, ProtocolMessage payload, JsonObject? context)
    {
        try
        {
            _events.Publish(kind, payload, context);
        }
        catch (Exception e)
        {
            _log.Error($"Publishing {kind} failed.", e);
        }
    }

    private void Prune()
    {
        var finished = _jobs.Values.Where(j => j.State is "succeeded" or "failed" or "cancelled").OrderBy(j => j.Number).ToList();
        foreach (var job in finished.Take(Math.Max(0, finished.Count - KeepFinished)))
        {
            _jobs.Remove(job.Id);
        }
    }

    private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    internal sealed class Job
    {
        private static long s_number;

        public Job(string id, string kind, Func<JobContext, ProtocolMessage?> body, JsonObject? context)
        {
            Id = id;
            Kind = kind;
            Body = body;
            Context = context;
            Number = Interlocked.Increment(ref s_number);
        }

        public string Id { get; }

        public string Kind { get; }

        public long Number { get; }

        public Func<JobContext, ProtocolMessage?> Body { get; }

        public JsonObject? Context { get; }

        public CancellationTokenSource Cancellation { get; } = new();

        public ManualResetEventSlim Finished { get; } = new(false);

        public List<Action<JobInfo>> Waiters { get; } = new();

        public Stopwatch ProgressClock { get; } = Stopwatch.StartNew();

        public int ProgressEvents { get; set; }

        public string State { get; set; } = "queued";

        public JobProgress? Progress { get; set; }

        public JsonValue? Result { get; set; }

        public ProtocolError? Error { get; set; }

        public string? StartedAt { get; set; }

        public string? FinishedAt { get; set; }

        public JobInfo Info() => new()
        {
            JobId = Id,
            Kind = Kind,
            State = State,
            Progress = Progress,
            Result = Result,
            Error = Error,
            StartedAt = StartedAt,
            FinishedAt = FinishedAt,
        };
    }
}
