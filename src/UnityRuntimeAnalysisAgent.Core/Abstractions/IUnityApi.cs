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

/// <summary>
/// What Core needs from Unity. Core never references UnityEngine: the Unity bindings implement this (and later slices of
/// it), and tests use a fake. This slice covers the main-thread pump and object liveness.
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

    /// <summary>Whether a Unity object has been destroyed (<c>UnityEngine.Object == null</c> semantics).</summary>
    bool IsDestroyed(object unityObject);
}
