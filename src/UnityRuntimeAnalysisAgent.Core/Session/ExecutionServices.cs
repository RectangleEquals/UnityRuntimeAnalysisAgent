using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Api;
using UnityRuntimeAnalysisAgent.Core.Abstractions;
using UnityRuntimeAnalysisAgent.Core.Data;
using UnityRuntimeAnalysisAgent.Core.Dispatch;
using UnityRuntimeAnalysisAgent.Core.Execution;
using UnityRuntimeAnalysisAgent.Core.Instrumentation;

namespace UnityRuntimeAnalysisAgent.Core.Session;

/// <summary>
/// Code the client compiled, run in the game: snippets (<c>exec.*</c>), live Harmony patch sets (<c>patch.*</c>) and mod
/// hot-reload (<c>mod.*</c>), plus the process's Harmony landscape (<c>patch.inspect</c>, <c>patches.all</c>). Everything that
/// runs code needs Full mode, and every assembly is audited with its name and SHA-256. The agent never compiles.
/// </summary>
internal sealed class ExecutionServices : IDisposable
{
    private readonly DataModel _data;

    public ExecutionServices(ContextServices context, ILoaderApi? loader, Action<string, string, JsonObject> warning)
    {
        _data = context.Data;
        Assemblies = new AssemblyLoader(warning);
        Snippets = new SnippetRunner(context, Assemblies);
        Patches = new LivePatchManager(Assemblies);
        Mods = new ModManager(loader, context.Data, Assemblies);
        Global = new AgentContext(context, null, null, new Dictionary<string, object?>(StringComparer.Ordinal), default, null, collect: false);
        AgentApi.Current = Global;
    }

    public AssemblyLoader Assemblies { get; }

    public SnippetRunner Snippets { get; }

    public LivePatchManager Patches { get; }

    public ModManager Mods { get; }

    /// <summary>The context live patches see (<see cref="AgentApi.Current"/>).</summary>
    public AgentContext Global { get; }

    /// <summary>hook.verify's <c>exec</c> trigger: runs a snippet (without waiting for an iterator one to finish).</summary>
    public ITrigger Trigger => new ExecTrigger(Snippets);

    // ---- snippets ---------------------------------------------------------------------------------------------------------

    [RpcMethod(Methods.ExecRun, DefaultTimeoutMs = 60_000, MaxTimeoutMs = 3_600_000)]
    public Deferred ExecRun(RequestContext context, ExecRunParams p)
    {
        if (p.OutDir is { } outDir && !Path.IsPathRooted(outDir))
        {
            throw ProtocolException.InvalidParams("params.outDir", "outDir must be an absolute path.");
        }

        var entry = Snippets.Bind(AssemblyLoader.Decode(p.Assembly, "params.assembly"), p.EntryType, p.EntryMethod);
        context.Assembly = entry.Assembly.Audit;
        return Snippets.Run(entry, p, context.Cancellation);
    }

    [RpcMethod(Methods.ExecSessions)]
    public ProtocolMessage ExecSessions(RequestContext context) => new ExecSessionsResult { Items = Snippets.Sessions() };

    [RpcMethod(Methods.ExecSessionClose)]
    public ProtocolMessage ExecSessionClose(RequestContext context, ExecSessionCloseParams p) => new ExecSessionCloseResult { Closed = Snippets.Close(p.Session) };

    // ---- live patches -----------------------------------------------------------------------------------------------------

    [RpcMethod(Methods.PatchApply, DefaultTimeoutMs = 60_000, MaxTimeoutMs = 600_000)]
    public ProtocolMessage PatchApply(RequestContext context, PatchApplyParams p)
    {
        var (result, assembly) = Patches.Apply(p, Owner(context));
        context.Assembly = assembly.Audit;
        return result;
    }

    [RpcMethod(Methods.PatchRevert)]
    public ProtocolMessage PatchRevert(RequestContext context, PatchRevertParams p) => Patches.Revert(p.PatchSetId);

    [RpcMethod(Methods.PatchList)]
    public ProtocolMessage PatchList(RequestContext context) => Patches.List();

    [RpcMethod(Methods.PatchInspect)]
    public ProtocolMessage PatchInspect(RequestContext context, PatchInspectParams p)
    {
        var method = _data.Anchors.ResolveMember(p.Method, "params.method") as MethodBase
            ?? throw ProtocolException.InvalidParams("params.method", "params.method must be a method or constructor.");
        return new PatchInspectResult { Method = p.Method, Patches = LivePatchManager.Inspect(method) };
    }

    [RpcMethod(Methods.PatchesAll, DefaultTimeoutMs = 60_000, MaxTimeoutMs = 600_000)]
    public ProtocolMessage PatchesAll(RequestContext context, PatchesAllParams p) => new PatchesAllResult { Items = LivePatchManager.All(p.Owner) };

    // ---- mods -------------------------------------------------------------------------------------------------------------

    [RpcMethod(Methods.ModReload, DefaultTimeoutMs = 60_000, MaxTimeoutMs = 600_000)]
    public IEnumerable<object?> ModReload(RequestContext context, ModReloadParams p) => Mods.Reload(p, context);

    [RpcMethod(Methods.ModUnload)]
    public ProtocolMessage ModUnload(RequestContext context, ModUnloadParams p) => Mods.Unload(p.PluginGuid);

    [RpcMethod(Methods.ModList)]
    public ProtocolMessage ModList(RequestContext context) => Mods.List();

    /// <summary>Reverts every live patch set (E-STOP); snippet sessions and loaded mods stay. Returns how many sets were reverted.</summary>
    public int RevertPatches() => Patches.RevertAll();

    /// <summary>Reverts every live patch set and forgets the sessions (shutdown).</summary>
    public void Dispose()
    {
        Patches.RevertAll();
        Snippets.CloseAll();
        if (ReferenceEquals(AgentApi.Current, Global))
        {
            AgentApi.Current = null;
        }

        Global.End();
    }

    private static string Owner(RequestContext context) => context.Connection?.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? context.Source;

    private sealed class ExecTrigger : ITrigger
    {
        private readonly SnippetRunner _snippets;

        public ExecTrigger(SnippetRunner snippets) => _snippets = snippets;

        public string Kind => "exec";

        public string? Fire(Trigger trigger, string param)
        {
            var assembly = trigger.Assembly is { Length: > 0 } base64 ? base64 : throw ProtocolException.InvalidParams(param + ".assembly", "An exec trigger needs the snippet assembly.");
            var entry = _snippets.Bind(AssemblyLoader.Decode(assembly, param + ".assembly"), trigger.EntryType, trigger.EntryMethod);
            return _snippets.Start(entry);
        }
    }
}
