using System;

namespace UnityRuntimeAnalysisAgent.Core.Abstractions;

/// <summary>A snapshot of Unity's frame timing, published by the main-thread pump every frame.</summary>
public readonly struct FrameTime
{
    /// <summary>Creates a snapshot.</summary>
    public FrameTime(long frameCount, double time, double unscaledTime, double realtime, double timeScale, double deltaTime)
    {
        FrameCount = frameCount;
        Time = time;
        UnscaledTime = unscaledTime;
        Realtime = realtime;
        TimeScale = timeScale;
        DeltaTime = deltaTime;
    }

    /// <summary><c>Time.frameCount</c>.</summary>
    public long FrameCount { get; }

    /// <summary><c>Time.time</c> (seconds, scaled).</summary>
    public double Time { get; }

    /// <summary><c>Time.unscaledTime</c> (seconds).</summary>
    public double UnscaledTime { get; }

    /// <summary><c>Time.realtimeSinceStartup</c> (seconds).</summary>
    public double Realtime { get; }

    /// <summary><c>Time.timeScale</c>.</summary>
    public double TimeScale { get; }

    /// <summary><c>Time.deltaTime</c> (seconds).</summary>
    public double DeltaTime { get; }
}

/// <summary>What Core keeps about a Unity object: read once on the main thread, so it can be reported from any thread.</summary>
public readonly struct UnityObjectFacts
{
    /// <summary>Creates the facts.</summary>
    public UnityObjectFacts(long instanceId, string? name)
    {
        InstanceId = instanceId;
        Name = name;
    }

    /// <summary><c>Object.GetInstanceID()</c>.</summary>
    public long InstanceId { get; }

    /// <summary><c>Object.name</c>.</summary>
    public string? Name { get; }
}

/// <summary>Where a scene object is: its scene (<c>ddol</c> for DontDestroyOnLoad) and its GameObject's path from the root.</summary>
public readonly struct SceneAddress
{
    /// <summary>Creates the address.</summary>
    public SceneAddress(string scene, string path)
    {
        Scene = scene;
        Path = path;
    }

    /// <summary>The scene's name, or <c>ddol</c>.</summary>
    public string Scene { get; }

    /// <summary>The GameObject's path, root first, separated by <c>/</c>.</summary>
    public string Path { get; }
}

/// <summary>
/// What Core needs from Unity. Core never references UnityEngine: the Unity bindings implement this (and later slices of
/// it), and tests use a fake. This slice covers the main-thread pump, object liveness and facts, and finding objects in
/// the scene hierarchy.
/// </summary>
public interface IUnityApi
{
    /// <summary>Creates the hidden pump host (a <c>DontDestroyOnLoad</c> object) that calls <paramref name="tick"/> every
    /// frame and <paramref name="endOfFrame"/> at the end of every frame, on the main thread.</summary>
    void CreatePumpHost(Action tick, Action endOfFrame);

    /// <summary>Whether the pump host still exists (a game may destroy it).</summary>
    bool IsPumpHostAlive { get; }

    /// <summary>Recreates the pump host after it was destroyed. May be called from any thread.</summary>
    void RecreatePumpHost();

    /// <summary>Destroys the pump host (at shutdown).</summary>
    void DestroyPumpHost();

    /// <summary>Reads the frame timing (main thread only).</summary>
    FrameTime ReadFrameTime();

    /// <summary>Whether a Unity object has been destroyed (<c>UnityEngine.Object == null</c> semantics). May be called from
    /// any thread.</summary>
    bool IsDestroyed(object unityObject);

    /// <summary>A Unity object's instance id and name, or null when it isn't a Unity object (main thread only).</summary>
    UnityObjectFacts? Describe(object unityObject);

    /// <summary>The GameObject at <paramref name="path"/> (root first, <c>/</c>-separated) in <paramref name="scene"/>
    /// (<c>ddol</c> for DontDestroyOnLoad; null searches every loaded scene, then DontDestroyOnLoad), or null (main thread
    /// only). The first match wins when names repeat.</summary>
    object? FindGameObject(string path, string? scene);

    /// <summary>The component of <paramref name="componentType"/> on a GameObject (or on a component's GameObject), or null
    /// (main thread only).</summary>
    object? GetComponent(object gameObjectOrComponent, Type componentType);

    /// <summary><c>Transform.Find(path)</c> on a GameObject (or on a component's GameObject): the child's GameObject, or null
    /// (main thread only).</summary>
    object? FindChild(object gameObjectOrComponent, string path);

    /// <summary>Where a GameObject or component is in the loaded scenes, or null for anything else (assets, destroyed
    /// objects) (main thread only).</summary>
    SceneAddress? Locate(object unityObject);
}
