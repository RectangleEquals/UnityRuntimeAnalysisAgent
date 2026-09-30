using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Envelopes;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Dispatch;
using UnityRuntimeAnalysisAgent.Core.Jobs;
using UnityRuntimeAnalysisAgent.Core.Probes;
using UnityRuntimeAnalysisAgent.Core.Runtime;

namespace UnityRuntimeAnalysisAgent.Core.Session;

/// <summary><c>probe.run</c>, <c>probe.runBatch</c> (a job, with <c>probe.progress</c> events), <c>probe.result</c> and
/// <c>probe.cancel</c> over the <see cref="ProbeExecutor"/>.</summary>
internal sealed class ProbeServices : IDisposable
{
    private readonly ProbeExecutor _executor;
    private readonly JobManager _jobs;
    private readonly EventHub _events;
    private readonly ModeController _modes;

    public ProbeServices(ProbeExecutor executor, JobManager jobs, EventHub events, ModeController modes)
    {
        _executor = executor;
        _jobs = jobs;
        _events = events;
        _modes = modes;
    }

    [RpcMethod(Methods.ProbeRun, DefaultTimeoutMs = ProbeExecutor.MaxTimeoutMs + 60_000, MaxTimeoutMs = ProbeExecutor.MaxTimeoutMs + 60_000)]
    public Deferred ProbeRun(RequestContext context, ProbeRunParams p)
    {
        // A single call probe is refused up front, like any Full-mode method (in a batch it becomes that probe's error).
        if (p.Probe.Kind == "call" && !_modes.Allows(AgentMode.Full))
        {
            throw AgentErrors.ModeForbidden(Methods.ProbeRun + " with a call probe", AgentMode.Full, _modes.Current);
        }

        if (p.Probe.Kind == "call" && p.Probe.Consented != true)
        {
            throw ProtocolException.InvalidParams("params.probe.consented", "A call probe runs game code: it needs consented: true (the user's approval).");
        }

        var deferred = new Deferred();
        Complete(_executor.RunAsync(p.Probe, context.Context, context.Cancellation), deferred);
        return deferred;
    }

    [RpcMethod(Methods.ProbeRunBatch)]
    public ProtocolMessage ProbeRunBatch(RequestContext context, ProbeRunBatchParams p)
    {
        var duplicate = p.Probes.GroupBy(x => x.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw ProtocolException.InvalidParams("params.probes", $"Probe id {duplicate.Key} is used more than once.");
        }

        var budgetMs = p.BudgetMs;
        var stopOnError = p.StopOnError ?? false;
        return _jobs.Start("probe.batch", job =>
        {
            var clock = Stopwatch.StartNew();
            var results = new List<ProbeResult>();
            string? notRun = null;
            for (var i = 0; i < p.Probes.Count; i++)
            {
                var probe = p.Probes[i];
                if (notRun is null && job.Cancellation.IsCancellationRequested)
                {
                    notRun = "the batch was cancelled";
                }

                if (notRun is null && budgetMs is { } budget && clock.ElapsedMilliseconds >= budget)
                {
                    notRun = $"the batch's budget ({budget} ms) was spent";
                }

                var result = notRun is null
                    ? _executor.RunAsync(probe, context.Context, job.Cancellation).GetAwaiter().GetResult()
                    : new ProbeResult
                    {
                        Id = probe.Id,
                        Status = "failed",
                        Error = new ProtocolError { Code = ErrorCodes.Cancelled, Message = $"Not run: {notRun}." },
                        Metrics = new ProbeMetrics(),
                    };
                results.Add(result);
                job.Progress("probe", i + 1, p.Probes.Count, probe.Id);
                if (_events.HasSubscribers(EventKinds.ProbeProgress))
                {
                    _events.Publish(EventKinds.ProbeProgress, new ProbeProgressEventParams { JobId = job.JobId, ProbeId = probe.Id, Index = i, Total = p.Probes.Count, Status = result.Status }, context.Context);
                }

                if (notRun is null && stopOnError && result.Status is "failed" or "stale")
                {
                    notRun = $"probe {probe.Id} {result.Status} (stopOnError)";
                }
            }

            return new ProbeRunBatchJobResult { Results = results };
        }, context.Context);
    }

    [RpcMethod(Methods.ProbeResult)]
    public Deferred ProbeResult(RequestContext context, ProbeResultParams p)
    {
        var deferred = new Deferred();
        Complete(_executor.CollectAsync(p.ProbeId), deferred);
        return deferred;
    }

    [RpcMethod(Methods.ProbeCancel)]
    public Deferred ProbeCancel(RequestContext context, ProbeCancelParams p)
    {
        var deferred = new Deferred();
        _executor.CancelAsync(p.ProbeId).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                deferred.Fail(t.Exception!.GetBaseException());
            }
            else
            {
                deferred.Complete(new ProbeCancelResult { Cancelled = t.Result });
            }
        }, TaskScheduler.Default);
        return deferred;
    }

    /// <summary>Disarms every waiting probe (shutdown).</summary>
    public void Dispose() => _executor.Dispose();

    private static void Complete(Task<ProbeResult> task, Deferred deferred) => task.ContinueWith(t =>
    {
        if (t.IsFaulted)
        {
            deferred.Fail(t.Exception!.GetBaseException());
        }
        else if (t.IsCanceled)
        {
            deferred.Fail(new OperationCanceledException());
        }
        else
        {
            deferred.Complete(t.Result);
        }
    }, TaskScheduler.Default);
}
