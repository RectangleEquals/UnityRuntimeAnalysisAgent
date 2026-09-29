using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Abstractions;
using UnityRuntimeAnalysisAgent.Core.Data;
using UnityRuntimeAnalysisAgent.Core.Dispatch;
using UnityRuntimeAnalysisAgent.Core.Runtime;

namespace UnityRuntimeAnalysisAgent.Core.Execution;

/// <summary>
/// Mod hot-reload. The mod's contract: a stable plugin GUID, a Harmony instance whose id is that GUID, unpatching itself
/// in <c>OnDestroy</c>, (re)initialising static state in <c>Awake</c>, and a unique assembly name per build. A reload
/// unpatches the GUID's Harmony id, destroys the current instance (the loader's or a hot-loaded one), waits a frame for
/// Unity to finish destroying it, loads the new assemblies from bytes and instantiates the plugin type with the GUID on a
/// hidden host. Old assemblies stay loaded (the runtime can't unload them).
/// </summary>
internal sealed class ModManager
{
    private readonly ILoaderApi? _loaderApi;
    private readonly DataModel _data;
    private readonly AssemblyLoader _assemblies;
    private readonly Dictionary<string, HotPlugin> _hot = new(StringComparer.Ordinal);

    public ModManager(ILoaderApi? loader, DataModel data, AssemblyLoader assemblies)
    {
        _loaderApi = loader;
        _data = data;
        _assemblies = assemblies;
    }

    private ILoaderApi Loader => _loaderApi ?? throw DataErrors.Unsupported("There is no mod loader: plugins can only be managed inside a game with a loader.");

    /// <summary>The reload, as a main-thread routine: it yields a frame wait, then the <see cref="ModReloadResult"/>.</summary>
    public IEnumerable<object?> Reload(ModReloadParams p, RequestContext context)
    {
        var guid = p.PluginGuid;
        var builds = p.Assemblies.Select((a, i) => (a.Name, Bytes: AssemblyLoader.Decode(a.Base64, $"params.assemblies[{i}].base64"))).ToList();
        PreviousPlugin? previous = null;
        var current = Current(guid);
        if (current is { } found)
        {
            var unpatched = Unpatch(guid);
            Loader.DestroyPlugin(found.Instance);
            lock (_hot)
            {
                _hot.Remove(guid);
            }

            previous = new PreviousPlugin { Assembly = found.Assembly, Source = found.Source, Destroyed = true, Unpatched = unpatched };

            // Unity destroys components at the end of the frame: the old OnDestroy (which unpatches the GUID) must run
            // before the new instance's Awake patches it again.
            yield return PumpWait.NextFrame;
        }

        LoadedAssembly? main = null;
        Type? pluginType = null;
        object instance;
        try
        {
            foreach (var (name, bytes) in builds)
            {
                main = _assemblies.Load(bytes);
                context.Assembly = main.Audit;
            }

            pluginType = LoadableTypes(main!).FirstOrDefault(t => Loader.PluginMetadata(t)?.Guid == guid)
                ?? throw AgentErrors.ExecFailed("bind", $"{main!.Name} has no plugin type with the GUID {guid}.");
            try
            {
                instance = Loader.InstantiatePlugin(pluginType);
            }
            catch (Exception e)
            {
                throw AgentErrors.ExecFailed("run", $"Instantiating {pluginType.FullName} failed: {e.Message}", e);
            }
        }
        catch (ProtocolException e) when (previous is not null)
        {
            // The previous instance is gone either way: say so with the error.
            var data = e.ErrorData ?? new JsonObject();
            data.Add("previous", previous.ToJson());
            throw new ProtocolException(e.Code, e.Message, data, e.InnerException);
        }

        var metadata = Loader.PluginMetadata(pluginType)!;
        lock (_hot)
        {
            _hot[guid] = new HotPlugin(instance, main!.Name, metadata.Name, metadata.Version);
        }

        yield return new ModReloadResult
        {
            Previous = previous,
            Loaded = new LoadedPlugin { Assembly = main.Name, PluginType = pluginType.FullName ?? pluginType.Name, Instance = _data.Handles.DescribeValue(instance) },
        };
    }

    /// <summary>Destroys a plugin's instance and unpatches its Harmony id (main thread).</summary>
    public ModUnloadResult Unload(string guid)
    {
        var current = Current(guid) ?? throw AgentErrors.NotFound($"No live plugin with the GUID {guid}.");
        var unpatched = Unpatch(guid);
        Loader.DestroyPlugin(current.Instance);
        lock (_hot)
        {
            _hot.Remove(guid);
        }

        return new ModUnloadResult { Destroyed = true, Unpatched = unpatched };
    }

    /// <summary>The loader's plugins and the hot-loaded ones (main thread).</summary>
    public ModListResult List()
    {
        var items = new List<PluginInfo>();
        foreach (var plugin in Loader.Plugins)
        {
            var instance = Loader.FindPluginInstance(plugin.Guid);
            items.Add(new PluginInfo
            {
                Guid = plugin.Guid,
                Name = plugin.Name,
                Version = plugin.Version,
                Assembly = instance?.GetType().Assembly.GetName().Name ?? System.IO.Path.GetFileNameWithoutExtension(plugin.AssemblyPath ?? string.Empty),
                Source = "loader",
                Alive = instance is not null,
            });
        }

        lock (_hot)
        {
            items.AddRange(_hot.Select(h => new PluginInfo
            {
                Guid = h.Key,
                Name = h.Value.Name,
                Version = h.Value.Version,
                Assembly = h.Value.Assembly,
                Source = "hotload",
                Alive = !_data.Unity.IsDestroyed(h.Value.Instance),
            }));
        }

        return new ModListResult { Items = items };
    }

    private (object Instance, string Assembly, string Source)? Current(string guid)
    {
        lock (_hot)
        {
            if (_hot.TryGetValue(guid, out var hot) && !_data.Unity.IsDestroyed(hot.Instance))
            {
                return (hot.Instance, hot.Assembly, "hotload");
            }
        }

        var loaded = Loader.FindPluginInstance(guid);
        return loaded is null ? null : (loaded, loaded.GetType().Assembly.GetName().Name ?? "?", "loader");
    }

    // The mod's own Harmony id is its GUID.
    private static long Unpatch(string guid)
    {
        var patched = Harmony.GetAllPatchedMethods().Count(m => Harmony.GetPatchInfo(m)?.Owners.Contains(guid) == true);
        if (patched > 0)
        {
            Harmony.UnpatchID(guid);
        }

        return patched;
    }

    private static IEnumerable<Type> LoadableTypes(LoadedAssembly assembly)
    {
        try
        {
            return assembly.Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.Where(t => t is not null)!;
        }
    }

    private sealed class HotPlugin
    {
        public HotPlugin(object instance, string assembly, string name, string version)
        {
            Instance = instance;
            Assembly = assembly;
            Name = name;
            Version = version;
        }

        public object Instance { get; }

        public string Assembly { get; }

        public string Name { get; }

        public string Version { get; }
    }
}
