using System.Collections;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Envelopes;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Dispatch;
using UnityRuntimeAnalysisAgent.Core.Hosting;
using UnityRuntimeAnalysisAgent.Core.Jobs;
using UnityRuntimeAnalysisAgent.Core.Runtime;

namespace UnityRuntimeAnalysisAgent.Core.Tests.Support;

/// <summary>A started agent host over a <see cref="FakeUnityApi"/>, with in-memory client connections.</summary>
public sealed class TestHost : IDisposable
{
    private readonly List<WirePeer> _peers = new();

    public TestHost(string mode = "ReadOnly", Action<DictionaryConfigSource>? configure = null, bool withUnity = true)
    {
        var config = new DictionaryConfigSource().Set(AgentConfig.TransportModeKey, "tcp").Set(AgentConfig.ModeKey, mode);
        configure?.Invoke(config);
        var environment = AgentEnvironment.ForCurrentProcess();
        environment.UnityVersion = "2021.3.45f1";
        Host = new AgentHost(config, environment, Log, withUnity ? Unity : null);
        Host.Start();
    }

    public FakeUnityApi Unity { get; } = new();

    public TestLogger Log { get; } = new();

    public AgentHost Host { get; }

    /// <summary>Connects an in-memory client (authenticated unless <paramref name="hello"/> is false).</summary>
    public WirePeer Connect(bool hello = true)
    {
        var (agentSide, clientSide) = MemoryDuplex.CreatePair();
        Host.AcceptConnection(agentSide, "tcp");
        var peer = new WirePeer(clientSide);
        _peers.Add(peer);
        if (hello)
        {
            var response = peer.Call(Methods.Hello, $"{{\"token\":\"{Host.Token.Value}\",\"client\":{{\"name\":\"test\",\"version\":\"1\"}},\"protocol\":{{\"major\":{ProtocolVersion.Major},\"minor\":{ProtocolVersion.Minor}}}}}");
            Assert.False(response.IsError);
        }

        return peer;
    }

    /// <summary>Sends a request and steps frames until its response arrives (for main-thread methods).</summary>
    public ResponseEnvelope CallStepping(WirePeer peer, string method, string? paramsJson = null, int maxFrames = 200)
    {
        var id = peer.Send(method, paramsJson);
        var response = Task.Run(() => peer.AwaitResponse(id, 10_000));
        for (var i = 0; i < maxFrames && !response.IsCompleted; i++)
        {
            Unity.StepFrames();
            Thread.Sleep(2);
        }

        return response.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Host.Shutdown();
        foreach (var peer in _peers)
        {
            peer.Dispose();
        }
    }
}

/// <summary>Test implementations of a few protocol methods, one of each kind the dispatcher distinguishes.</summary>
public sealed class TestService
{
    public List<long> TickFrames { get; } = new();

    public int QuitCalls;

    public FakeUnityApi? Unity { get; set; }

    public JobManager? Jobs { get; set; }

    public ManualResetEventSlim JobGate { get; } = new(true);

    /// <summary>Main thread, one frame: records the frame it ran in.</summary>
    [RpcMethod(Methods.AppInfo)]
    public ProtocolMessage AppInfo(RequestContext context)
    {
        var frame = Unity?.Frame ?? -1;
        lock (TickFrames)
        {
            TickFrames.Add(frame);
        }

        context.Target = "live://test/app";
        return new PingResult { Echo = "app", UptimeMs = 0, Frame = frame };
    }

    /// <summary>Main thread, multi-frame: waits 3 frames, then answers.</summary>
    [RpcMethod(Methods.TimeInfo, DefaultTimeoutMs = 2000)]
    public IEnumerable TimeInfo(RequestContext context)
    {
        var start = Unity?.Frame ?? 0;
        yield return PumpWait.Frames(3);
        yield return new PingResult { Echo = "waited", UptimeMs = 0, Frame = (Unity?.Frame ?? 0) - start };
    }

    /// <summary>Full mode, mutating.</summary>
    [RpcMethod(Methods.AppQuit)]
    public ProtocolMessage Quit(RequestContext context, AppQuitParams p)
    {
        Interlocked.Increment(ref QuitCalls);
        return new AppQuitResult { Quitting = true };
    }

    /// <summary>Needs module:addressables.</summary>
    [RpcMethod(Methods.AddressablesInfo)]
    public ProtocolMessage Addressables(RequestContext context) => new PingResult { Echo = "addressables", UptimeMs = 0 };

    /// <summary>Any thread; fails as <c>params.how</c> says: <c>game</c>, <c>protocol</c>, <c>hang</c> (until cancelled), else an agent bug.</summary>
    [RpcMethod(Methods.LogsTail, DefaultTimeoutMs = 300)]
    public ProtocolMessage Fails(RequestContext context)
    {
        var how = context.Params["how"] is UnityLudometry.Protocol.Json.JsonString s ? s.Value : "internal";
        switch (how)
        {
            case "game":
                throw AgentErrors.Game(new InvalidOperationException("the game's own bug"));
            case "protocol":
                throw AgentErrors.NotFound("no such thing");
            case "hang":
                context.Cancellation.WaitHandle.WaitOne(5000);
                context.Cancellation.ThrowIfCancellationRequested();
                return new PingResult { Echo = "not cancelled", UptimeMs = 0 };
            default:
                throw new InvalidOperationException("an agent bug");
        }
    }

    /// <summary>A job (mixed thread): runs until the gate opens, reporting progress.</summary>
    [RpcMethod(Methods.SurveyStart)]
    public ProtocolMessage Survey(RequestContext context, SurveyStartParams p) => Jobs!.Start("survey", job =>
    {
        job.Progress("waiting", 0, 1);
        while (!JobGate.Wait(10))
        {
            job.Cancellation.ThrowIfCancellationRequested();
        }

        job.Progress("done", 1, 1);
        return new PingResult { Echo = p.OutFile, UptimeMs = 0 };
    }, context.Context);
}
