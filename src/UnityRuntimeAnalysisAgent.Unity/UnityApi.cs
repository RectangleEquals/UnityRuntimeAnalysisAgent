using System;
using System.Collections;
using System.Linq;
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
        SceneManager.sceneLoaded += (scene, mode) => Guard(() => OnSceneLoaded(scene, mode));
        SceneManager.sceneUnloaded += scene => Guard(() => OnSceneUnloaded(scene));
        SceneManager.activeSceneChanged += (previous, next) => Guard(() => OnActiveSceneChanged(previous, next));
    }

    /// <inheritdoc />
    public bool IsPumpHostAlive => _alive;

    // Unity callbacks must never throw into the game.
    private void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            _log.Error("A scene callback failed.", e);
        }
    }

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
        var steps = path.Split('/');
        var level = Roots(scene).ToList();
        GameObject? found = null;
        foreach (var step in steps)
        {
            found = Pick(level, step);
            if (found == null)
            {
                return null;
            }

            level = Children(found);
        }

        return found;
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
        var go = GameObjectOf(gameObjectOrComponent);
        foreach (var step in path.Split('/'))
        {
            go = go == null ? null : Pick(Children(go), step);
        }

        return go == null ? null : go;
    }

    /// <inheritdoc />
    public SceneAddress? Locate(object unityObject)
    {
        var go = GameObjectOf(unityObject);
        return go == null || !go.scene.IsValid() ? null : new SceneAddress(SceneName(go.scene), PathOf(go));
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

    private static System.Collections.Generic.List<GameObject> Children(GameObject go)
    {
        var children = new System.Collections.Generic.List<GameObject>(go.transform.childCount);
        foreach (Transform child in go.transform)
        {
            children.Add(child.gameObject);
        }

        return children;
    }

    // "Name" is the first object with that name among its siblings, "Name[2]" the second, and so on.
    private static GameObject? Pick(System.Collections.Generic.List<GameObject> level, string step)
    {
        var name = step;
        var nth = 1;
        var open = step.LastIndexOf('[');
        if (open > 0 && step.EndsWith("]", StringComparison.Ordinal) && int.TryParse(step.Substring(open + 1, step.Length - open - 2), out var n) && n >= 1)
        {
            name = step.Substring(0, open);
            nth = n;
        }

        foreach (var candidate in level)
        {
            if (candidate.name == name && --nth == 0)
            {
                return candidate;
            }
        }

        return null;
    }

    private static string PathOf(GameObject go)
    {
        var steps = new System.Collections.Generic.List<string>();
        for (var t = go.transform; t != null; t = t.parent)
        {
            var siblings = t.parent != null ? Children(t.parent.gameObject) : new System.Collections.Generic.List<GameObject>(t.gameObject.scene.GetRootGameObjects());
            var nth = 0;
            foreach (var sibling in siblings)
            {
                if (sibling.name == t.name)
                {
                    nth++;
                }

                if (sibling == t.gameObject)
                {
                    break;
                }
            }

            steps.Insert(0, nth > 1 ? $"{t.name}[{nth}]" : t.name);
        }

        return string.Join("/", steps.ToArray());
    }

    // Scene.handle is public in some Unity versions only; GetHashCode returns the same handle in all of them.
    private static int Handle(Scene scene) => scene.GetHashCode();

    private static string SceneName(Scene scene) => scene.buildIndex == -1 && scene.name == DontDestroyOnLoadScene ? "ddol" : scene.name;

    // Scene roots to search: one scene by name (or DontDestroyOnLoad), or all of them.
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

        if ((scene is null || scene == "ddol") && DdolScene() is { } ddol)
        {
            foreach (var root in ddol.GetRootGameObjects())
            {
                yield return root;
            }
        }
    }

    // The DontDestroyOnLoad pseudo-scene: the agent's own host lives there; without it, a hidden object is parked there
    // for a moment to read its scene.
    private Scene? DdolScene()
    {
        if (_host != null)
        {
            return _host.scene;
        }

        var probe = new GameObject("UnityRuntimeAnalysisAgent.DdolProbe") { hideFlags = HideFlags.HideAndDontSave };
        UnityEngine.Object.DontDestroyOnLoad(probe);
        var scene = probe.scene;
        UnityEngine.Object.DestroyImmediate(probe);
        return scene;
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

    /// <inheritdoc />
    public System.Collections.Generic.IReadOnlyList<SceneFacts> Scenes()
    {
        var active = Handle(SceneManager.GetActiveScene());
        var scenes = new System.Collections.Generic.List<SceneFacts>();
        for (var i = 0; i < SceneManager.sceneCount; i++)
        {
            scenes.Add(Facts(SceneManager.GetSceneAt(i), active));
        }

        if (DdolScene() is { } ddol)
        {
            scenes.Add(Facts(ddol, active));
        }

        return scenes;
    }

    /// <inheritdoc />
    public int SceneCountInBuildSettings => SceneManager.sceneCountInBuildSettings;

    /// <inheritdoc />
    public System.Collections.Generic.IReadOnlyList<object> SceneRoots(int sceneHandle)
    {
        for (var i = 0; i < SceneManager.sceneCount; i++)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (Handle(scene) == sceneHandle)
            {
                return scene.isLoaded ? scene.GetRootGameObjects() : Array.Empty<object>();
            }
        }

        return DdolScene() is { } ddol && Handle(ddol) == sceneHandle ? ddol.GetRootGameObjects() : Array.Empty<object>();
    }

    /// <inheritdoc />
    public GameObjectFacts? DescribeGameObject(object gameObjectOrComponent)
    {
        var go = GameObjectOf(gameObjectOrComponent);
        if (go == null)
        {
            return null;
        }

        var components = new System.Collections.Generic.List<(object, bool?)>();
        foreach (var component in go.GetComponents<Component>())
        {
            if (component == null)
            {
                continue; // a missing script
            }

            bool? enabled = component switch
            {
                Behaviour behaviour => behaviour.enabled,
                Renderer renderer => renderer.enabled,
                Collider collider => collider.enabled,
                _ => null,
            };
            components.Add((component, enabled));
        }

        var transform = go.transform;
        return new GameObjectFacts
        {
            GameObject = go,
            Name = go.name,
            ActiveSelf = go.activeSelf,
            ActiveInHierarchy = go.activeInHierarchy,
            Tag = go.tag,
            Layer = go.layer,
            HideFlags = go.hideFlags.ToString(),
            Scene = go.scene.IsValid() ? SceneName(go.scene) : string.Empty,
            Path = go.scene.IsValid() ? PathOf(go) : go.name,
            Parent = transform.parent == null ? null : transform.parent.gameObject,
            SiblingIndex = transform.GetSiblingIndex(),
            Children = Children(go).ToArray(),
            Components = components,
            LocalPosition = transform.localPosition,
            LocalRotation = transform.localRotation,
            LocalScale = transform.localScale,
            Position = transform.position,
            Rotation = transform.rotation,
            LossyScale = transform.lossyScale,
        };
    }

    /// <inheritdoc />
    public System.Collections.Generic.IReadOnlyList<object> FindObjectsOfTypeAll(Type type) => Resources.FindObjectsOfTypeAll(type);

    /// <inheritdoc />
    public object CreateGameObject(string name, object? parent, string? scene)
    {
        var go = new GameObject(name);
        if (parent != null && GameObjectOf(parent) is { } parentObject)
        {
            go.transform.SetParent(parentObject.transform, false);
        }
        else if (scene == "ddol")
        {
            UnityEngine.Object.DontDestroyOnLoad(go);
        }
        else if (scene != null)
        {
            var target = SceneManager.GetSceneByName(scene);
            if (!target.IsValid() || !target.isLoaded)
            {
                UnityEngine.Object.DestroyImmediate(go);
                throw new ArgumentException($"No loaded scene '{scene}'.");
            }

            SceneManager.MoveGameObjectToScene(go, target);
        }

        return go;
    }

    /// <inheritdoc />
    public object Instantiate(object original, object? parent, object? position, object? rotation)
    {
        var clone = UnityEngine.Object.Instantiate((UnityEngine.Object)original);
        if (GameObjectOf(clone) is { } go)
        {
            if (parent != null && GameObjectOf(parent) is { } parentObject)
            {
                go.transform.SetParent(parentObject.transform, false);
            }

            if (position is Vector3 p)
            {
                go.transform.position = p;
            }

            if (rotation is Quaternion r)
            {
                go.transform.rotation = r;
            }
        }

        return clone;
    }

    /// <inheritdoc />
    public void SetActive(object gameObject, bool active) =>
        (GameObjectOf(gameObject) ?? throw new ArgumentException("Not a GameObject.")).SetActive(active);

    /// <inheritdoc />
    public void Destroy(object unityObject, bool immediate)
    {
        if (immediate)
        {
            UnityEngine.Object.DestroyImmediate((UnityEngine.Object)unityObject);
        }
        else
        {
            UnityEngine.Object.Destroy((UnityEngine.Object)unityObject);
        }
    }

    /// <inheritdoc />
    public object AddComponent(object gameObjectOrComponent, Type componentType) =>
        (GameObjectOf(gameObjectOrComponent) ?? throw new ArgumentException("Not a GameObject or component.")).AddComponent(componentType);

    /// <inheritdoc />
    public object CreateScriptableObject(Type type) => ScriptableObject.CreateInstance(type);

    /// <inheritdoc />
    public event Action<SceneChange>? SceneChanged;

    private SceneFacts Facts(Scene scene, int activeHandle) => new()
    {
        Handle = Handle(scene),
        Name = scene.name,
        Path = scene.path,
        BuildIndex = scene.buildIndex,
        IsLoaded = scene.isLoaded,
        IsActive = Handle(scene) == activeHandle,
        RootCount = scene.isLoaded ? scene.rootCount : 0,
        IsDontDestroyOnLoad = scene.buildIndex == -1 && scene.name == DontDestroyOnLoadScene,
    };

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode) =>
        SceneChanged?.Invoke(new SceneChange { Change = "loaded", Scene = Facts(scene, Handle(SceneManager.GetActiveScene())), Mode = mode == LoadSceneMode.Additive ? "additive" : "single" });

    private void OnSceneUnloaded(Scene scene) =>
        SceneChanged?.Invoke(new SceneChange { Change = "unloaded", Scene = Facts(scene, Handle(SceneManager.GetActiveScene())) });

    private void OnActiveSceneChanged(Scene previous, Scene next) =>
        SceneChanged?.Invoke(new SceneChange { Change = "activeChanged", Scene = Facts(next, Handle(next)), PreviousActive = previous.name });

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
