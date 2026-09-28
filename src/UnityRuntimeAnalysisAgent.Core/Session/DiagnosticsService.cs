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

/// <summary><c>agent.selfTest</c>: checks the agent's own plumbing in the running game. More checks join as features land.</summary>
internal sealed class DiagnosticsService
{
    private readonly AgentHost _host;

    public DiagnosticsService(AgentHost host) => _host = host;

    [RpcMethod(Methods.AgentSelfTest, DefaultTimeoutMs = 15_000)]
    public IEnumerable SelfTest(RequestContext context)
    {
        var checks = new List<SelfTestCheck>();

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
        var warnings = _host.Config.Warnings;
        checks.Add(Check("config.valid", warnings.Count == 0, clock.ElapsedMilliseconds, warnings.Count == 0 ? "all settings valid" : string.Join(" ", warnings)));

        yield return new AgentSelfTestResult { Passed = checks.All(c => c.Passed), Checks = checks };
    }

    private static SelfTestCheck Check(string name, bool passed, long durationMs, string message) =>
        new() { Name = name, Passed = passed, DurationMs = durationMs, Message = message };
}
