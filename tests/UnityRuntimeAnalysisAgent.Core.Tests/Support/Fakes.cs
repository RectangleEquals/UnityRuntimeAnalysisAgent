using UnityRuntimeAnalysisAgent.Core.Abstractions;
using UnityRuntimeAnalysisAgent.Core.Hosting;

namespace UnityRuntimeAnalysisAgent.Core.Tests.Support;

/// <summary>A controllable Unity: frames advance only in <see cref="StepFrames"/>, and time is a settable clock.</summary>
public sealed class FakeUnityApi : IUnityApi
{
    private readonly object _gate = new();
    private readonly HashSet<object> _destroyed = new(ReferenceEqualityComparer.Instance);
    private Action? _tick;
    private Action? _endOfFrame;
    private long _frame;

    /// <summary>Real time since start in milliseconds (the pump's clock in tests that pass <see cref="NowMs"/>).</summary>
    public double RealtimeMs { get; set; }

    /// <summary>Milliseconds each frame advances the clock.</summary>
    public double FrameMs { get; set; } = 16;

    public bool IsPumpHostAlive { get; private set; }

    public int CreatedCount { get; private set; }

    public int RecreatedCount { get; private set; }

    public int DestroyedCount { get; private set; }

    public List<string> Calls { get; } = new();

    public long Frame
    {
        get
        {
            lock (_gate)
            {
                return _frame;
            }
        }
    }

    public double NowMs()
    {
        lock (_gate)
        {
            return RealtimeMs;
        }
    }

    public void CreatePumpHost(Action tick, Action endOfFrame)
    {
        lock (_gate)
        {
            _tick = tick;
            _endOfFrame = endOfFrame;
            IsPumpHostAlive = true;
            CreatedCount++;
            Calls.Add("create");
        }
    }

    public void RecreatePumpHost()
    {
        lock (_gate)
        {
            IsPumpHostAlive = true;
            RecreatedCount++;
            Calls.Add("recreate");
        }
    }

    public void DestroyPumpHost()
    {
        lock (_gate)
        {
            IsPumpHostAlive = false;
            DestroyedCount++;
            Calls.Add("destroy");
        }
    }

    /// <summary>Simulates the game destroying the pump host.</summary>
    public void KillPumpHost()
    {
        lock (_gate)
        {
            IsPumpHostAlive = false;
        }
    }

    public FrameTime ReadFrameTime()
    {
        lock (_gate)
        {
            var seconds = RealtimeMs / 1000.0;
            return new FrameTime(_frame, seconds, seconds, seconds, 1, FrameMs / 1000.0);
        }
    }

    public bool IsDestroyed(object unityObject)
    {
        lock (_gate)
        {
            return _destroyed.Contains(unityObject);
        }
    }

    public void Destroy(object unityObject)
    {
        lock (_gate)
        {
            _destroyed.Add(unityObject);
        }
    }

    /// <summary>Runs <paramref name="count"/> frames (tick, then end of frame), advancing the clock by <see cref="FrameMs"/> each.</summary>
    public void StepFrames(int count = 1)
    {
        for (var i = 0; i < count; i++)
        {
            Action? tick, endOfFrame;
            lock (_gate)
            {
                _frame++;
                RealtimeMs += FrameMs;
                tick = IsPumpHostAlive ? _tick : null;
                endOfFrame = IsPumpHostAlive ? _endOfFrame : null;
            }

            tick?.Invoke();
            endOfFrame?.Invoke();
        }
    }

    /// <summary>Steps frames until <paramref name="done"/> is true (or fails the test after <paramref name="maxFrames"/>).</summary>
    public void StepUntil(Func<bool> done, int maxFrames = 1000)
    {
        for (var i = 0; i < maxFrames && !done(); i++)
        {
            StepFrames();
            Thread.Sleep(1);
        }

        Assert.True(done(), "the condition didn't become true while stepping frames");
    }
}

/// <summary>A loader with a dictionary config and a recording log.</summary>
public sealed class FakeLoaderApi : ILoaderApi
{
    public FakeLoaderApi(DictionaryConfigSource? config = null) => Config = config ?? new DictionaryConfigSource();

    public string LoaderName => "FakeLoader";

    public string LoaderVersion => "1.0";

    public IConfigSource Config { get; }

    public IReadOnlyList<LoaderPluginInfo> Plugins { get; } = new List<LoaderPluginInfo> { new("com.example.plugin", "Example", "1.0", null) };

    public HashSet<string> PressedShortcuts { get; } = new(StringComparer.Ordinal);

    public TestLogger Log { get; } = new();

    public event Action<LoaderLogEntry>? LoaderLog;

    public bool IsShortcutPressed(string configKey) => PressedShortcuts.Contains(configKey);

    public IAgentLogger CreateLog(string source) => Log;

    public void RaiseLog(LoaderLogEntry entry) => LoaderLog?.Invoke(entry);

    public List<object> PluginInstances { get; } = new();

    public object InstantiatePlugin(Type pluginType)
    {
        var instance = Activator.CreateInstance(pluginType)!;
        PluginInstances.Add(instance);
        return instance;
    }

    public void DestroyPlugin(object instance) => PluginInstances.Remove(instance);
}
