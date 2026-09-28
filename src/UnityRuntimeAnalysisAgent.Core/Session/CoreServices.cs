using System;
using System.Collections.Generic;
using System.Linq;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Envelopes;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Dispatch;
using UnityRuntimeAnalysisAgent.Core.Hosting;
using UnityRuntimeAnalysisAgent.Core.Jobs;
using UnityRuntimeAnalysisAgent.Core.Runtime;

namespace UnityRuntimeAnalysisAgent.Core.Session;

/// <summary>Session methods: <c>ping</c>, <c>agent.info</c>, <c>agent.capabilities</c>, <c>cancel</c>, event subscriptions,
/// <c>agent.setMode</c> and <c>agent.logLevel</c>.</summary>
internal sealed class SessionService
{
    private readonly AgentHost _host;

    public SessionService(AgentHost host) => _host = host;

    [RpcMethod(Methods.Ping)]
    public ProtocolMessage Ping(RequestContext context, PingParams p) => new PingResult
    {
        Echo = p.Echo,
        UptimeMs = (long)(DateTime.UtcNow - _host.Session.StartedUtc).TotalMilliseconds,
        Frame = _host.Pump.HasTicked ? _host.Pump.Clock.FrameCount : null,
    };

    [RpcMethod(Methods.AgentInfo)]
    public ProtocolMessage Info(RequestContext context) => _host.BuildAgentInfo();

    [RpcMethod(Methods.AgentCapabilities)]
    public ProtocolMessage Capabilities(RequestContext context) => _host.BuildCapabilities();

    [RpcMethod(Methods.Cancel)]
    public ProtocolMessage Cancel(RequestContext context, CancelParams p) => new CancelResult
    {
        Cancelled = context.Connection is not null && p.Id != context.Id && _host.Session.Cancel(context.Connection, p.Id),
    };

    [RpcMethod(Methods.EventsSubscribe)]
    public ProtocolMessage Subscribe(RequestContext context, EventsSubscribeParams p)
    {
        if (p.Kinds.Count == 0)
        {
            throw ProtocolException.InvalidParams("params.kinds", "kinds must contain at least one event kind.");
        }

        return _host.Events.Subscribe(RequireConnection(context), p);
    }

    [RpcMethod(Methods.EventsUnsubscribe)]
    public ProtocolMessage Unsubscribe(RequestContext context, EventsUnsubscribeParams p) =>
        new EventsUnsubscribeResult { Unsubscribed = _host.Events.Unsubscribe(RequireConnection(context), p.Kinds) };

    [RpcMethod(Methods.AgentSetMode)]
    public ProtocolMessage SetMode(RequestContext context, AgentSetModeParams p)
    {
        var previous = _host.Modes.Lower(p.Mode);
        return new AgentSetModeResult { Previous = previous, Mode = _host.Modes.Current };
    }

    [RpcMethod(Methods.AgentLogLevel)]
    public ProtocolMessage LogLevel(RequestContext context, AgentLogLevelParams p)
    {
        if (!Enum.TryParse<AgentLogLevel>(p.Level, ignoreCase: false, out var level) || !Enum.IsDefined(typeof(AgentLogLevel), level))
        {
            throw ProtocolException.InvalidParams("params.level", "level must be Debug, Info, Warning or Error.");
        }

        var previous = _host.Log.MinLevel;
        _host.Log.MinLevel = level;
        return new AgentLogLevelResult { Previous = previous.ToString(), Level = level.ToString() };
    }

    private static Transport.Connection RequireConnection(RequestContext context) =>
        context.Connection ?? throw ProtocolException.InvalidParams("params", "Event subscriptions belong to a client connection.");
}

/// <summary>Job methods: <c>job.get</c>, <c>job.wait</c>, <c>job.cancel</c>, <c>job.list</c>.</summary>
internal sealed class JobService
{
    /// <summary>How long <c>job.wait</c> waits when the request doesn't say.</summary>
    public const int DefaultWaitMs = 30_000;

    private readonly JobManager _jobs;

    public JobService(JobManager jobs) => _jobs = jobs;

    [RpcMethod(Methods.JobGet)]
    public ProtocolMessage Get(RequestContext context, JobGetParams p) => _jobs.Get(p.JobId);

    [RpcMethod(Methods.JobWait, DefaultTimeoutMs = 120_000, MaxTimeoutMs = 600_000)]
    public ProtocolMessage Wait(RequestContext context, JobWaitParams p)
    {
        // Answer just before the request's own timeout, so a long wait returns the state instead of TIMEOUT.
        var requestTimeout = context.TimeoutMs ?? 120_000;
        var waitMs = (int)Math.Max(0, Math.Min(p.TimeoutMs ?? DefaultWaitMs, Math.Min(requestTimeout, 600_000) - 1_000));
        return _jobs.Wait(p.JobId, waitMs, context.Cancellation);
    }

    [RpcMethod(Methods.JobCancel)]
    public ProtocolMessage Cancel(RequestContext context, JobCancelParams p) => _jobs.Cancel(p.JobId);

    [RpcMethod(Methods.JobList)]
    public ProtocolMessage List(RequestContext context, JobListParams p) => new JobListResult { Items = _jobs.List(p.State) };
}

/// <summary>The activity feed: <c>activity.list</c>, <c>activity.get</c>.</summary>
internal sealed class ActivityService
{
    private readonly ActivityFeed _feed;

    public ActivityService(ActivityFeed feed) => _feed = feed;

    [RpcMethod(Methods.ActivityList)]
    public ProtocolMessage List(RequestContext context, ActivityListParams p)
    {
        var (items, total) = _feed.List(p.MutatingOnly ?? false, p.SinceId ?? 0, (int)Math.Min(p.Limit ?? 100, 1000));
        return new ActivityListResult { Items = items.ToList(), Total = total };
    }

    [RpcMethod(Methods.ActivityGet)]
    public ProtocolMessage Get(RequestContext context, ActivityGetParams p)
    {
        var found = _feed.Get(p.Id) ?? throw AgentErrors.NotFound($"No activity entry {p.Id} (unknown, or evicted from the ring).");
        return new ActivityGetResult { Entry = found.Entry, Request = found.Request, Response = found.Response };
    }
}

/// <summary><c>batch</c>: several requests in one; with <c>sameFrame</c>, all of them within one main-thread frame.</summary>
internal sealed class BatchService
{
    private readonly Dispatcher _dispatcher;
    private readonly MainThreadPump _pump;

    public BatchService(Dispatcher dispatcher, MainThreadPump pump)
    {
        _dispatcher = dispatcher;
        _pump = pump;
    }

    [RpcMethod(Methods.Batch, DefaultTimeoutMs = 60_000, MaxTimeoutMs = 600_000)]
    public ProtocolMessage Batch(RequestContext context, BatchParams p)
    {
        if (p.Requests.Any(r => r.Method == Methods.Batch))
        {
            throw ProtocolException.InvalidParams("params.requests", "A batch can't contain another batch.");
        }

        if (p.SameFrame == true)
        {
            // One main-thread work item runs every request back to back: they all see the same frame.
            BatchResult? result = null;
            Exception? failure = null;
            using var done = new System.Threading.ManualResetEventSlim(false);
            _pump.Enqueue(new PumpWork(() => Run(context, p, inline: true), r =>
            {
                result = (BatchResult)r!;
                done.Set();
            }, e =>
            {
                failure = e;
                done.Set();
            }, context.Cancellation));
            done.Wait(context.Cancellation);
            if (failure is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }

            return result!;
        }

        return Run(context, p, inline: false);
    }

    private BatchResult Run(RequestContext context, BatchParams p, bool inline)
    {
        var results = new List<BatchItemResult>();
        var stopped = false;
        for (var i = 0; i < p.Requests.Count; i++)
        {
            var item = p.Requests[i];
            if (stopped || context.Cancellation.IsCancellationRequested)
            {
                results.Add(new BatchItemResult { Error = new ProtocolError { Code = ErrorCodes.Cancelled, Message = stopped ? "Skipped after an earlier error (stopOnError)." : "The batch was cancelled." } });
                continue;
            }

            var itemContext = new RequestContext($"{context.Id}#{i}", item.Method, item.Params, context.Context, context.Source, context.Client, context.Connection, context.Cancellation);
            var outcome = inline ? _dispatcher.InvokeInline(itemContext) : _dispatcher.Invoke(itemContext);
            results.Add(outcome.Error is { } error ? new BatchItemResult { Error = error } : new BatchItemResult { Result = outcome.Result });
            stopped = outcome.Error is not null && p.StopOnError == true;
        }

        var clock = _pump.Clock;
        return new BatchResult { Results = results, Frame = clock.FrameCount, RealtimeMs = (long)(clock.Realtime * 1000) };
    }
}
