using System;
using System.Collections;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityRuntimeAnalysisAgent.Core.Abstractions;
using UnityRuntimeAnalysisAgent.Core.Hosting;

namespace UnityRuntimeAnalysisAgent.Unity;

/// <summary>
/// Core's view of Unity. The pump host is a hidden <c>DontDestroyOnLoad</c> object whose behaviour calls the pump every
/// frame and at the end of every frame. Unity objects can only be created on the main thread, so a recreation requested
/// from another thread (the watchdog) happens at the next main-thread opportunity: the loader's own update
/// (<see cref="OnLoaderUpdate"/>) or the next scene load.
/// </summary>
public sealed class UnityApi : IUnityApi
{
    private readonly IAgentLogger _log;
    private GameObject? _host;
    private Action? _tick;
    private Action? _endOfFrame;
    private int _mainThreadId;
    private volatile bool _alive;
    private volatile bool _recreateRequested;
    private volatile bool _destroyRequested;

    /// <summary>Creates the bindings (call from the main thread, e.g. the plugin's <c>Awake</c>).</summary>
    public UnityApi(IAgentLogger log)
    {
        _log = log;
        _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        SceneManager.sceneLoaded += (_, _) => OnLoaderUpdate();
    }

    /// <inheritdoc />
    public bool IsPumpHostAlive => _alive;

    /// <summary>How many times the host was created (the first time included).</summary>
    public int HostsCreated { get; private set; }

    private bool OnMainThread => Thread.CurrentThread.ManagedThreadId == _mainThreadId;

    /// <inheritdoc />
    public void CreatePumpHost(Action tick, Action endOfFrame)
    {
        _tick = tick;
        _endOfFrame = endOfFrame;
        if (OnMainThread)
        {
            CreateHost();
        }
        else
        {
            _recreateRequested = true;
        }
    }

    /// <inheritdoc />
    public void RecreatePumpHost()
    {
        if (OnMainThread)
        {
            CreateHost();
        }
        else
        {
            _recreateRequested = true;
        }
    }

    /// <inheritdoc />
    public void DestroyPumpHost()
    {
        _tick = null;
        _endOfFrame = null;
        if (OnMainThread)
        {
            DestroyHost();
        }
        else
        {
            _destroyRequested = true;
        }
    }

    /// <summary>Called by the loader every frame (main thread): carries out requests made from other threads.</summary>
    public void OnLoaderUpdate()
    {
        try
        {
            if (_destroyRequested)
            {
                _destroyRequested = false;
                DestroyHost();
            }
            else if (_recreateRequested || (!_alive && _tick is not null))
            {
                _recreateRequested = false;
                CreateHost();
            }
        }
        catch (Exception e)
        {
            _log.Error("Creating the pump host failed.", e);
        }
    }

    /// <inheritdoc />
    public FrameTime ReadFrameTime() => new(Time.frameCount, Time.time, Time.unscaledTime, Time.realtimeSinceStartup, Time.timeScale, Time.deltaTime);

    /// <inheritdoc />
    public bool IsDestroyed(object unityObject)
    {
        if (unityObject is not UnityEngine.Object o)
        {
            return unityObject is null;
        }

        // Unity's == may call into the engine, which is only safe on the main thread. Elsewhere, the native pointer Unity
        // caches on every live object (cleared when it's destroyed) tells the same without touching the engine.
        if (OnMainThread || CachedPtr is null)
        {
            return o == null;
        }

        return (IntPtr)CachedPtr.GetValue(o) == IntPtr.Zero;
    }

    /// <inheritdoc />
    public UnityObjectFacts? Describe(object unityObject) =>
        unityObject is UnityEngine.Object o && o != null ? new UnityObjectFacts(o.GetInstanceID(), o.name) : null;

    /// <inheritdoc />
    public object? FindGameObject(string path, string? scene)
    {
        var names = path.Split('/');
        foreach (var root in Roots(scene))
        {
            if (root.name != names[0])
            {
                continue;
            }

            var found = names.Length == 1 ? root.transform : root.transform.Find(string.Join("/", names, 1, names.Length - 1));
            if (found != null)
            {
                return found.gameObject;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public object? GetComponent(object gameObjectOrComponent, Type componentType)
    {
        var component = GameObjectOf(gameObjectOrComponent)?.GetComponent(componentType);
        return component == null ? null : component;
    }

    /// <inheritdoc />
    public object? FindChild(object gameObjectOrComponent, string path)
    {
        var child = GameObjectOf(gameObjectOrComponent)?.transform.Find(path);
        return child == null ? null : child.gameObject;
    }

    /// <inheritdoc />
    public SceneAddress? Locate(object unityObject)
    {
        var go = GameObjectOf(unityObject);
        if (go == null || !go.scene.IsValid())
        {
            return null;
        }

        var names = new System.Collections.Generic.List<string>();
        for (var t = go.transform; t != null; t = t.parent)
        {
            names.Insert(0, t.name);
        }

        return new SceneAddress(go.scene.buildIndex == -1 && go.scene.name == DontDestroyOnLoadScene ? "ddol" : go.scene.name, string.Join("/", names.ToArray()));
    }

    private const string DontDestroyOnLoadScene = "DontDestroyOnLoad";

    private static readonly System.Reflection.FieldInfo? CachedPtr =
        typeof(UnityEngine.Object).GetField("m_CachedPtr", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    private static GameObject? GameObjectOf(object value) => value switch
    {
        GameObject go when go != null => go,
        Component c when c != null => c.gameObject,
        _ => null,
    };

    // Scene roots to search: one scene by name (or DontDestroyOnLoad, reached through the agent's own host), or all of them.
    private System.Collections.Generic.IEnumerable<GameObject> Roots(string? scene)
    {
        if (scene is null || scene != "ddol")
        {
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var loaded = SceneManager.GetSceneAt(i);
                if (loaded.isLoaded && (scene is null || loaded.name == scene))
                {
                    foreach (var root in loaded.GetRootGameObjects())
                    {
                        yield return root;
                    }
                }
            }
        }

        if ((scene is null || scene == "ddol") && _host != null)
        {
            foreach (var root in _host.scene.GetRootGameObjects())
            {
                yield return root;
            }
        }
    }

    /// <inheritdoc />
    public System.Collections.Generic.IReadOnlyDictionary<Type, int> CountObjectsByType(Type baseType)
    {
        var counts = new System.Collections.Generic.Dictionary<Type, int>();
        foreach (var found in Resources.FindObjectsOfTypeAll(baseType))
        {
            var type = found.GetType();
            counts[type] = counts.TryGetValue(type, out var n) ? n + 1 : 1;
        }

        return counts;
    }

    /// <summary>Destroys the pump host now, like a game destroying stray objects would (for the watchdog self-test).</summary>
    public void DestroyHostForTest() => DestroyHost();

    private void CreateHost()
    {
        if (_alive || _tick is null || _endOfFrame is null)
        {
            return;
        }

        var host = new GameObject("UnityRuntimeAnalysisAgent") { hideFlags = HideFlags.HideAndDontSave };
        UnityEngine.Object.DontDestroyOnLoad(host);
        var behaviour = host.AddComponent<PumpBehaviour>();
        behaviour.Attach(this, _tick, _endOfFrame);
        _host = host;
        _alive = true;
        HostsCreated++;
        if (HostsCreated > 1)
        {
            _log.Warning("The pump host was recreated.");
        }
    }

    private void DestroyHost()
    {
        if (_host != null)
        {
            UnityEngine.Object.Destroy(_host);
        }

        _host = null;
        _alive = false;
    }

    internal void OnHostDestroyed(PumpBehaviour behaviour)
    {
        if (_host == null || ReferenceEquals(_host, behaviour.gameObject))
        {
            _alive = false;
        }
    }

    /// <summary>The pump host's behaviour: every frame, and at the end of it, call into the agent's pump.</summary>
    internal sealed class PumpBehaviour : MonoBehaviour
    {
        private UnityApi? _owner;
        private Action? _tick;
        private Action? _endOfFrame;

        public void Attach(UnityApi owner, Action tick, Action endOfFrame)
        {
            _owner = owner;
            _tick = tick;
            _endOfFrame = endOfFrame;
            StartCoroutine(EndOfFrameLoop());
        }

        private void Update() => _tick?.Invoke();

        private IEnumerator EndOfFrameLoop()
        {
            var wait = new WaitForEndOfFrame();
            while (true)
            {
                yield return wait;
                _endOfFrame?.Invoke();
            }
        }

        private void OnDestroy() => _owner?.OnHostDestroyed(this);
    }
}
