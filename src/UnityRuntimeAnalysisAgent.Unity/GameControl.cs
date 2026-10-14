using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityRuntimeAnalysisAgent.Core.Abstractions;

namespace UnityRuntimeAnalysisAgent.Unity;

/// <summary>Time, scenes and the application, against the 2018.1 API (newer members by reflection).</summary>
internal sealed class GameControl : IGameControl
{
    // Application.consoleLogPath arrived after 2018.1.
    private static readonly PropertyInfo? ConsoleLogPath = typeof(Application).GetProperty("consoleLogPath", BindingFlags.Public | BindingFlags.Static);

    private static readonly MethodInfo? QuitWithCode = typeof(Application).GetMethod("Quit", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int) }, null);

    private const string HarmonyId = "com.github.rectangleequals.unityruntimeanalysisagent.runinbackground";

    // Keeping the game running in the background: the game's own value (at start, then its writes), whether its writes
    // are held back, and whether the patch on the setter is in place (the agent's own writes pass through it).
    private static bool? s_gameRunInBackground;
    private static bool s_keep;
    private static Harmony? s_harmony;
    [ThreadStatic] private static bool t_own;

    private readonly Func<Scene, SceneFacts> _facts;

    public GameControl(Func<Scene, SceneFacts> facts)
    {
        _facts = facts;
        s_gameRunInBackground ??= Application.runInBackground;
    }

    public TimeFacts ReadTime() => new()
    {
        FrameCount = Time.frameCount,
        Time = Time.time,
        UnscaledTime = Time.unscaledTime,
        Realtime = Time.realtimeSinceStartup,
        TimeScale = Time.timeScale,
        DeltaTime = Time.deltaTime,
        UnscaledDeltaTime = Time.unscaledDeltaTime,
        FixedDeltaTime = Time.fixedDeltaTime,
        TargetFrameRate = Application.targetFrameRate,
        VSyncCount = QualitySettings.vSyncCount,
    };

    public double TimeScale
    {
        get => Time.timeScale;
        set => Time.timeScale = (float)value;
    }

    public AppFacts ReadApp() => new()
    {
        ProductName = Application.productName,
        CompanyName = Application.companyName,
        Version = Application.version,
        UnityVersion = Application.unityVersion,
        Platform = Application.platform.ToString(),
        DataPath = Application.dataPath,
        PersistentDataPath = Application.persistentDataPath,
        ConsoleLogPath = ConsoleLogPath?.GetValue(null, null) as string,
        IsFocused = Application.isFocused,
        RunInBackground = Application.runInBackground,
        GameRunInBackground = s_gameRunInBackground,
        KeptRunning = s_keep,
        ScreenWidth = Screen.width,
        ScreenHeight = Screen.height,
        FullScreen = Screen.fullScreen,
        QualityLevel = QualitySettings.GetQualityLevel(),
        GraphicsDevice = SystemInfo.graphicsDeviceName ?? string.Empty,
    };

    public bool AudioPaused
    {
        get => AudioListener.pause;
        set => AudioListener.pause = value;
    }

    public bool RunInBackground
    {
        get => Application.runInBackground;
        set => Write(value);
    }

    public bool KeepRunning
    {
        get => s_keep;
        set
        {
            if (value)
            {
                Patch();
                s_keep = true;
                Write(true);
            }
            else if (s_keep)
            {
                s_keep = false;
                Write(s_gameRunInBackground ?? Application.runInBackground); // the game's own choice again
            }
        }
    }

    // The game's writes to Application.runInBackground: recorded, and held back while the agent keeps it running (a game
    // that switches it off as its window loses focus would otherwise stop before the agent's next frame could undo it).
    private static bool RunInBackgroundPrefix(bool value)
    {
        if (t_own)
        {
            return true;
        }

        s_gameRunInBackground = value;
        return !s_keep;
    }

    private static void Patch()
    {
        if (s_harmony is not null)
        {
            return;
        }

        try
        {
            s_harmony = new Harmony(HarmonyId);
            var setter = typeof(Application).GetProperty(nameof(Application.runInBackground))!.GetSetMethod();
            s_harmony.Patch(setter, prefix: new HarmonyMethod(typeof(GameControl).GetMethod(nameof(RunInBackgroundPrefix), BindingFlags.NonPublic | BindingFlags.Static)));
        }
        catch (Exception)
        {
            // Without the patch the host's per-frame check still turns it back on (from the next frame).
            s_harmony?.UnpatchSelf();
        }
    }

    private static void Write(bool value)
    {
        t_own = true;
        try
        {
            Application.runInBackground = value;
        }
        finally
        {
            t_own = false;
        }
    }

    // Application.Quit(int) arrived after 2018.1; without it the exit code is Unity's own.
    public void Quit(int exitCode)
    {
        if (QuitWithCode is not null)
        {
            QuitWithCode.Invoke(null, new object[] { exitCode });
        }
        else
        {
            Application.Quit();
        }
    }

    public bool CanLoadScene(string name) => Application.CanStreamedLevelBeLoaded(name);

    public ISceneOperation LoadScene(string? name, int? buildIndex, bool additive, bool async)
    {
        var mode = additive ? LoadSceneMode.Additive : LoadSceneMode.Single;
        Scene Find()
        {
            if (name is null)
            {
                return SceneManager.GetSceneByBuildIndex(buildIndex!.Value);
            }

            var byName = SceneManager.GetSceneByName(name);
            return byName.IsValid() ? byName : SceneManager.GetSceneByPath(name);
        }

        if (async)
        {
            var operation = name is not null ? SceneManager.LoadSceneAsync(name, mode) : SceneManager.LoadSceneAsync(buildIndex!.Value, mode);
            return new Operation(() => operation is null || operation.isDone, Find, _facts);
        }

        var started = Time.frameCount;
        if (name is not null)
        {
            SceneManager.LoadScene(name, mode);
        }
        else
        {
            SceneManager.LoadScene(buildIndex!.Value, mode);
        }

        // A synchronous load completes at the start of the next frame.
        return new Operation(() => Time.frameCount > started && Find().isLoaded, Find, _facts);
    }

    public ISceneOperation UnloadScene(int handle)
    {
        var scene = SceneByHandle(handle) ?? throw new ArgumentException($"No loaded scene with handle {handle}.", nameof(handle));
        var operation = SceneManager.UnloadSceneAsync(scene);
        if (operation is null)
        {
            // Unity refuses (e.g. the last loaded scene).
            return new Operation(() => true, () => scene, _ => null!, refused: true);
        }

        return new Operation(() => operation.isDone, () => scene, _ => null!);
    }

    public bool SetActiveScene(int handle) => SceneByHandle(handle) is { } scene && SceneManager.SetActiveScene(scene);

    private static Scene? SceneByHandle(int handle)
    {
        for (var i = 0; i < SceneManager.sceneCount; i++)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (scene.GetHashCode() == handle)
            {
                return scene;
            }
        }

        return null;
    }

    private sealed class Operation : ISceneOperation
    {
        private readonly Func<bool> _done;
        private readonly Func<Scene> _scene;
        private readonly Func<Scene, SceneFacts> _facts;
        private readonly bool _refused;

        public Operation(Func<bool> done, Func<Scene> scene, Func<Scene, SceneFacts> facts, bool refused = false)
        {
            _done = done;
            _scene = scene;
            _facts = facts;
            _refused = refused;
        }

        public bool IsDone => _done();

        public bool Refused => _refused;

        public SceneFacts? Result
        {
            get
            {
                if (_refused || !IsDone)
                {
                    return null;
                }

                var scene = _scene();
                return scene.IsValid() ? _facts(scene) : null;
            }
        }
    }
}
