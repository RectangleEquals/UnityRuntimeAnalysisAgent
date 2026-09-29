using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Dispatch;
using UnityRuntimeAnalysisAgent.Core.Hosting;
using UnityRuntimeAnalysisAgent.Core.Runtime;

namespace UnityRuntimeAnalysisAgent.Core.Session;

/// <summary><c>agent.healthCheck</c>: checks the agent's own plumbing in the running game: the main-thread pump, the
/// transport and discovery file, settings, anchor resolution, a hook round trip on an agent-owned method, and the log.</summary>
internal sealed class DiagnosticsService
{
    private readonly AgentHost _host;

    public DiagnosticsService(AgentHost host) => _host = host;

    [RpcMethod(Methods.AgentHealthCheck, DefaultTimeoutMs = 15_000)]
    public IEnumerable HealthCheck(RequestContext context)
    {
        var checks = new List<HealthCheckItem>();

        // The main thread: this runs in a frame; wait for the next one and check the frame counter moved.
        var clock = Stopwatch.StartNew();
        var startFrame = _host.Pump.Clock.FrameCount;
        yield return PumpWait.NextFrame;
        var nextFrame = _host.Pump.Clock.FrameCount;
        checks.Add(Check("pump.roundTrip", nextFrame > startFrame, clock.ElapsedMilliseconds, $"frame {startFrame} → {nextFrame}"));

        clock.Restart();
        var transport = _host.Transport;
        checks.Add(Check("transport.listening", transport is not null, clock.ElapsedMilliseconds,
            transport is null ? "no transport" : transport.Kind == "pipe" ? $"pipe {transport.PipeName}" : $"tcp 127.0.0.1:{transport.Port}"));

        clock.Restart();
        var discovery = _host.DiscoveryPath;
        checks.Add(_host.Config.ProvidersDir is null
            ? Check("discovery.published", true, clock.ElapsedMilliseconds, "Discovery.ProvidersDir isn't set: nothing to publish")
            : Check("discovery.published", discovery is not null && File.Exists(discovery), clock.ElapsedMilliseconds, discovery ?? "not written"));

        clock.Restart();
        checks.Add(Try("anchor.resolve", clock, () =>
        {
            var anchor = Data.AnchorWriter.ForType(typeof(AgentHost));
            var type = _host.Data.Anchors.ResolveType(anchor, "anchor");
            return (type == typeof(AgentHost), $"{anchor.Name} → {type.FullName}");
        }));

        clock.Restart();
        checks.Add(Try("hook.roundTrip", clock, () =>
        {
            var method = typeof(Diagnostics.HealthProbe).GetMethod(nameof(Diagnostics.HealthProbe.Ping))!;
            var sink = new Diagnostics.HealthProbe.CountingSink();
            _host.Instrumentation.Instrumenter.Attach(method, sink, force: false);
            try
            {
                Diagnostics.HealthProbe.Ping();
            }
            finally
            {
                _host.Instrumentation.Instrumenter.Detach(method, sink);
            }

            var remaining = Instrumentation.Instrumenter.AgentPatchedMethods().Any(m => m.DeclaringType == typeof(Diagnostics.HealthProbe));
            return (sink.Hits == 1 && !remaining, $"{sink.Hits} hit(s), {(remaining ? "patch left behind" : "patch removed")}");
        }));

        clock.Restart();
        checks.Add(Try("log.roundTrip", clock, () =>
        {
            var label = "agent.healthCheck " + System.Guid.NewGuid().ToString("N").Substring(0, 8);
            var seq = _host.Logs.Mark(label);
            var found = _host.Logs.Tail(seq - 1, 1, 0, null, null, null).Items.FirstOrDefault();
            return (found?.Marker == label, found is null ? "the marker isn't in the log" : $"marker at seq {seq}");
        }));

        clock.Restart();
        var warnings = _host.Config.Warnings;
        checks.Add(Check("config.valid", warnings.Count == 0, clock.ElapsedMilliseconds, warnings.Count == 0 ? "all settings valid" : string.Join(" ", warnings)));

        yield return new AgentHealthCheckResult { Passed = checks.All(c => c.Passed), Checks = checks };
    }

    private static HealthCheckItem Try(string name, Stopwatch clock, System.Func<(bool Passed, string Message)> check)
    {
        try
        {
            var (passed, message) = check();
            return Check(name, passed, clock.ElapsedMilliseconds, message);
        }
        catch (System.Exception e)
        {
            return Check(name, false, clock.ElapsedMilliseconds, $"{e.GetType().Name}: {e.Message}");
        }
    }

    private static HealthCheckItem Check(string name, bool passed, long durationMs, string message) =>
        new() { Name = name, Passed = passed, DurationMs = durationMs, Message = message };
}
