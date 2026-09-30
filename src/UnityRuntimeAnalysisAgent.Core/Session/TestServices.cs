using System;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Abstractions;
using UnityRuntimeAnalysisAgent.Core.Data;
using UnityRuntimeAnalysisAgent.Core.Diagnostics;
using UnityRuntimeAnalysisAgent.Core.Dispatch;
using UnityRuntimeAnalysisAgent.Core.Execution;
using UnityRuntimeAnalysisAgent.Core.Jobs;
using UnityRuntimeAnalysisAgent.Core.Runtime;
using UnityRuntimeAnalysisAgent.Core.Testing;

namespace UnityRuntimeAnalysisAgent.Core.Session;

/// <summary>
/// In-game tests (<c>test.list</c>, <c>test.run</c>): test assemblies written against the Api, loaded from bytes (or
/// already loaded) and run sequentially on the main thread as a job, with a result per test (status, message, stack,
/// attachments, the log lines it produced) and <c>test.*</c> events.
/// </summary>
internal sealed class TestServices
{
    public const int DefaultTimeoutMs = 30_000;

    private readonly ContextServices _services;
    private readonly AssemblyLoader _loader;
    private readonly JobManager _jobs;
    private readonly LogBuffer _logs;
    private readonly IUnityApi _unity;
    private readonly string _agentVersion;
    private readonly string _unityVersion;

    public TestServices(ContextServices services, AssemblyLoader loader, JobManager jobs, LogBuffer logs, IUnityApi unity, string agentVersion, string unityVersion)
    {
        _services = services;
        _loader = loader;
        _jobs = jobs;
        _logs = logs;
        _unity = unity;
        _agentVersion = agentVersion;
        _unityVersion = unityVersion;
    }

    [RpcMethod(Methods.TestList)]
    public ProtocolMessage TestList(RequestContext context, TestListParams p)
    {
        var assembly = Assembly(context, p.Assembly);
        return new TestListResult
        {
            Items = GameTestDiscovery.Discover(assembly).SelectMany(f => f.Tests).Select(t => new TestInfo
            {
                Test = t.Id,
                TimeoutMs = t.Attribute.TimeoutMs > 0 ? t.Attribute.TimeoutMs : null,
                Order = t.Attribute.Order,
                Skip = t.Attribute.Skip,
                RequiresScene = t.Attribute.RequiresScene,
            }).ToList(),
        };
    }

    [RpcMethod(Methods.TestRun)]
    public ProtocolMessage TestRun(RequestContext context, TestRunParams p)
    {
        var assembly = Assembly(context, p.Assembly);
        var fixtures = GameTestDiscovery.Discover(assembly);
        if (fixtures.Count == 0)
        {
            throw DataErrors.NotFound("params.assembly", $"{assembly.GetName().Name} has no [GameTestFixture] classes (built against this agent's UnityRuntimeAnalysisAgent.Api?).");
        }

        Regex? name = null;
        if (p.Filter?.Name is { } pattern)
        {
            try
            {
                name = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException e)
            {
                throw ProtocolException.InvalidParams("params.filter.name", $"params.filter.name isn't a valid regular expression: {e.Message}");
            }
        }

        bool Selected(GameTestCase test) =>
            (p.Filter?.Fixture is not { } fixture || test.Fixture!.Name == fixture || test.Fixture.Type.FullName == fixture)
            && (name is null || name.IsMatch(test.Name) || name.IsMatch(test.Fixture!.Name + "." + test.Name))
            && (p.Filter?.Category is not { } category || test.Attribute.Category == category);

        var timeoutMs = (int)Math.Max(1, Math.Min(p.DefaultTimeoutMs ?? DefaultTimeoutMs, int.MaxValue));
        return _jobs.Start("test.run", job =>
        {
            var runner = new GameTestRunner(_services, _logs, _unity, (kind, payload) => Publish(kind, payload, context));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Exception? error = null;
            using (var done = new ManualResetEventSlim(false))
            {
                // Not cancelled through the pump: the runner stops at the next yield itself, so teardowns still run.
                _services.Pump.Enqueue(new PumpWork(
                    () => runner.Run(job.JobId, fixtures, Selected, p.StopOnFail ?? false, timeoutMs, p.TimeScale, job.Cancellation, (d, t, test) => job.Progress("test", d, t, test)),
                    _ => done.Set(),
                    e =>
                    {
                        error = e;
                        done.Set();
                    }));
                done.Wait();
            }

            if (error is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            }

            var totals = runner.Totals();
            Publish(EventKinds.TestFinished, new TestFinishedEventParams { JobId = job.JobId, Totals = totals }, context);
            job.Cancellation.ThrowIfCancellationRequested();
            return new TestRunJobResult
            {
                Totals = totals,
                DurationMs = clock.ElapsedMilliseconds,
                Tests = runner.Results,
                AgentVersion = _agentVersion,
                UnityVersion = _unityVersion,
                FrameRange = new FrameRange { First = runner.FirstFrame, Last = runner.LastFrame },
            };
        }, context.Context);
    }

    private void Publish(string kind, ProtocolMessage payload, RequestContext context)
    {
        if (_services.Events.HasSubscribers(kind))
        {
            _services.Events.Publish(kind, payload, context.Context);
        }
    }

    // A test assembly from bytes (loaded now, with a unique name) or one loaded already.
    private Assembly Assembly(RequestContext context, TestAssembly source)
    {
        if ((source.Base64 is null) == (source.LoadedName is null))
        {
            throw ProtocolException.InvalidParams("params.assembly", "Give exactly one of params.assembly.base64 and params.assembly.loadedName.");
        }

        if (source.Base64 is { } base64)
        {
            var loaded = _loader.Load(AssemblyLoader.Decode(base64, "params.assembly.base64"));
            context.Assembly = loaded.Audit;
            return loaded.Assembly;
        }

        return AppDomain.CurrentDomain.GetAssemblies().LastOrDefault(a => a.GetName().Name == source.LoadedName)
            ?? throw DataErrors.NotFound("params.assembly.loadedName", $"No loaded assembly {source.LoadedName}.");
    }
}
