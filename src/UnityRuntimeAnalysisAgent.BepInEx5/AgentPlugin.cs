using System;
using BepInEx;
using UnityEngine;
using UnityRuntimeAnalysisAgent.Core;
using UnityRuntimeAnalysisAgent.Core.Hosting;
using UnityRuntimeAnalysisAgent.Overlay.Runtime;
using UnityRuntimeAnalysisAgent.Unity;

namespace UnityRuntimeAnalysisAgent.BepInEx5;

/// <summary>
/// The BepInEx 5 plugin: binds the agent's settings, builds the loader and Unity bindings, and runs the agent host for the
/// life of the game. Nothing it does can break the game: a failure to start is logged and the game carries on.
/// Some games destroy every object when they load a scene, BepInEx's manager (this plugin's object) included; the agent
/// then keeps running on its own hidden host and only stops when the application quits.
/// </summary>
[BepInPlugin(AgentIdentity.PluginGuid, AgentIdentity.PluginName, AgentBuild.PluginVersion)]
public sealed class AgentPlugin : BaseUnityPlugin
{
    private BepInEx5LoaderApi? _loader;
    private UnityApi? _unity;
    private AgentHost? _host;
    private bool _quitting;

    /// <summary>The running host (null if it failed to start).</summary>
    public AgentHost? Host => _host;

    private void Awake()
    {
        try
        {
            _loader = new BepInEx5LoaderApi(Config);
            var log = _loader.CreateLog("UnityRuntimeAnalysisAgent");
            _unity = new UnityApi(log);
            var environment = AgentEnvironment.ForCurrentProcess();
            environment.UnityVersion = Application.unityVersion;
            environment.Platform = Application.platform.ToString();
            environment.ScriptingBackend = Type.GetType("Mono.Runtime") is not null ? "mono" : "il2cpp";
            environment.LoaderName = _loader.LoaderName;
            environment.LoaderVersion = _loader.LoaderVersion;
            _host = new AgentHost(_loader.Config, environment, log, _unity, _loader);
            _host.Start();
            OverlayRuntime.Start(_host, _loader, log);
            Application.quitting += OnQuitting;
        }
        catch (Exception e)
        {
            Logger.LogError($"UnityRuntimeAnalysisAgent failed to start; the game is unaffected.{Environment.NewLine}{e}");
            Stop();
        }
    }

    private void Update() => _unity?.OnLoaderUpdate();

    private void OnApplicationQuit() => OnQuitting();

    // Destroyed by the game while it keeps running (not quitting): the agent's host outlives this object.
    private void OnDestroy()
    {
        if (!_quitting && _host is not null)
        {
            Logger.LogInfo("The game destroyed the plugin's object; the agent keeps running on its own host.");
        }
    }

    private void OnQuitting()
    {
        if (_quitting)
        {
            return;
        }

        _quitting = true;
        Application.quitting -= OnQuitting;
        Stop();
    }

    private void Stop()
    {
        try
        {
            _host?.Shutdown();
        }
        catch (Exception e)
        {
            Logger.LogError($"UnityRuntimeAnalysisAgent shutdown failed.{Environment.NewLine}{e}");
        }
        finally
        {
            _loader?.Dispose();
            _host = null;
        }
    }
}
