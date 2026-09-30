using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Abstractions;
using UnityRuntimeAnalysisAgent.Core.Data;
using UnityRuntimeAnalysisAgent.Core.Dispatch;
using UnityRuntimeAnalysisAgent.Core.Execution;
using UnityRuntimeAnalysisAgent.Core.Instrumentation;
using UnityRuntimeAnalysisAgent.Core.Runtime;

namespace UnityRuntimeAnalysisAgent.Core.Session;

/// <summary>
/// Driving the game: time (scale, pause, frame-exact stepping, waits), scenes, uGUI interaction through the game's own
/// handlers, and the application. Changes need Full mode and are audited with what they acted on; reads work in ReadOnly.
/// </summary>
internal sealed class ControlServices
{
    public const int DefaultUiLimit = 500;

    private readonly DataModel _data;
    private readonly MainThreadPump _pump;
    private readonly IUnityApi _unity;
    private readonly object _gate = new();
    private double? _pausedFrom;
    private double _lastRealtime = -1;
    private double _fps;

    public ControlServices(DataModel data, MainThreadPump pump, IUnityApi unity)
    {
        _data = data;
        _pump = pump;
        _unity = unity;
        pump.Ticked += OnTick;
    }

    /// <summary>hook.verify's <c>ui.click</c> trigger: clicks the trigger's target.</summary>
    public ITrigger Trigger => new UiClickTrigger(this);

    private IGameControl Control => _unity.Control ?? throw new ProtocolException(ErrorCodes.MainThreadUnavailable, "There is no Unity main thread (no game).");

    private IUiApi Ui => _unity.Ui ?? throw DataErrors.Unsupported("There is no Unity UI (no game).");

    private long Frame => _pump.Clock.FrameCount;

    private long RealtimeMs => (long)(_pump.Clock.Realtime * 1000);

    // ---- time -------------------------------------------------------------------------------------------------------------

    [RpcMethod(Methods.TimeInfo)]
    public ProtocolMessage TimeInfo(RequestContext context)
    {
        var t = Control.ReadTime();
        return new TimeInfoResult
        {
            FrameCount = t.FrameCount, Time = t.Time, UnscaledTime = t.UnscaledTime, RealtimeSinceStartup = t.Realtime, TimeScale = t.TimeScale,
            DeltaTime = t.DeltaTime, UnscaledDeltaTime = t.UnscaledDeltaTime, FixedDeltaTime = t.FixedDeltaTime, TargetFrameRate = t.TargetFrameRate,
            VSyncCount = t.VSyncCount, Fps = Math.Round(Volatile.Read(ref _fps), 1), Paused = t.TimeScale == 0,
        };
    }

    [RpcMethod(Methods.TimeScale)]
    public ProtocolMessage TimeScale(RequestContext context, TimeScaleParams p)
    {
        var previous = Control.TimeScale;
        Control.TimeScale = p.Value;
        lock (_gate)
        {
            _pausedFrom = p.Value == 0 ? (previous == 0 ? _pausedFrom : previous) : null;
        }

        return new TimeScaleResult { Previous = previous, Current = Control.TimeScale };
    }

    [RpcMethod(Methods.TimePause)]
    public ProtocolMessage TimePause(RequestContext context) => new TimePauseResult { PreviousTimeScale = Pause(), Paused = true };

    [RpcMethod(Methods.TimeResume)]
    public ProtocolMessage TimeResume(RequestContext context)
    {
        Resume();
        return new TimeResumeResult { TimeScale = Control.TimeScale, Paused = Control.TimeScale == 0 };
    }

    /// <summary>Pauses the game (time scale 0), remembering the scale to go back to; returns that scale. Main thread.</summary>
    public double Pause()
    {
        var current = Control.TimeScale;
        double previous;
        lock (_gate)
        {
            if (current != 0)
            {
                _pausedFrom = current;
            }

            previous = _pausedFrom ?? 1;
        }

        Control.TimeScale = 0;
        return previous;
    }

    /// <summary>Resumes at the scale the game had before it was paused (1 if unknown). Main thread.</summary>
    public void Resume()
    {
        Control.TimeScale = ResumeScale();
        lock (_gate)
        {
            _pausedFrom = null;
        }
    }

    /// <summary>Runs the game at its previous time scale for exactly N frames, then pauses it again: the scale is restored
    /// in frame F, so frames F+1 … F+N move, and set back to 0 in frame F+N.</summary>
    [RpcMethod(Methods.TimeStep, DefaultTimeoutMs = 30_000, MaxTimeoutMs = 600_000)]
    public IEnumerable<object?> TimeStep(RequestContext context, TimeStepParams p)
    {
        var count = (int)Math.Max(1, Math.Min(p.Frames ?? 1, 100_000));
        var scale = ResumeScale();
        lock (_gate)
        {
            _pausedFrom = scale;
        }

        var frames = new List<long>();
        Control.TimeScale = scale;
        try
        {
            for (var i = 0; i < count; i++)
            {
                yield return PumpWait.Frames(1);
                frames.Add(Frame);
            }
        }
        finally
        {
            Control.TimeScale = 0;
        }

        yield return new TimeStepResult { Frames = frames };
    }

    [RpcMethod(Methods.TimeWaitFrames, DefaultTimeoutMs = 60_000, MaxTimeoutMs = 600_000)]
    public IEnumerable<object?> TimeWaitFrames(RequestContext context, TimeWaitFramesParams p)
    {
        yield return PumpWait.Frames((int)Math.Min(p.Frames, int.MaxValue));
        yield return new TimeWaitFramesResult { Frame = Frame, RealtimeMs = RealtimeMs };
    }

    [RpcMethod(Methods.TimeWaitSeconds, DefaultTimeoutMs = 60_000, MaxTimeoutMs = 600_000)]
    public IEnumerable<object?> TimeWaitSeconds(RequestContext context, TimeWaitSecondsParams p)
    {
        yield return PumpWait.Realtime(p.Seconds * 1000);
        yield return new TimeWaitSecondsResult { Frame = Frame, RealtimeMs = RealtimeMs };
    }

    // ---- scenes ------------------------------------------------------------------------------------------------------------

    /// <summary>Loads a scene by name or build index; null reloads the active scene.</summary>
    [RpcMethod(Methods.SceneLoad, DefaultTimeoutMs = 120_000, MaxTimeoutMs = 600_000)]
    public IEnumerable<object?> SceneLoad(RequestContext context, SceneLoadParams p)
    {
        string? name = null;
        int? index = null;
        switch (p.Scene)
        {
            case JsonNull:
                var active = _unity.Scenes().FirstOrDefault(s => s.IsActive) ?? throw DataErrors.NotFound("params.scene", "No scene is active.");
                name = active.Path.Length > 0 ? active.Path : active.Name;
                break;
            case JsonString s:
                name = Control.CanLoadScene(s.Value) ? s.Value : throw DataErrors.NotFound("params.scene", $"No scene '{s.Value}' in the build.");
                break;
            case JsonNumber n when n.TryGetInt32(out var i):
                index = i >= 0 && i < _unity.SceneCountInBuildSettings ? i : throw DataErrors.NotFound("params.scene", $"No scene with build index {i} (the build has {_unity.SceneCountInBuildSettings}).");
                break;
            default:
                throw ProtocolException.InvalidParams("params.scene", "params.scene must be a scene name or a build index.");
        }

        context.Target = "scene://" + (name ?? index!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var operation = Control.LoadScene(name, index, p.Mode == "additive", p.Async ?? true);
        while (!operation.IsDone)
        {
            yield return PumpWait.NextFrame;
        }

        var scene = operation.Result ?? throw DataErrors.NotFound("params.scene", "The scene loaded but can't be found among the loaded scenes.");
        yield return new SceneLoadResult { Scene = LiveServices.SceneInfo(scene), Frame = Frame, RealtimeMs = RealtimeMs };
    }

    [RpcMethod(Methods.SceneUnload, DefaultTimeoutMs = 120_000, MaxTimeoutMs = 600_000)]
    public IEnumerable<object?> SceneUnload(RequestContext context, SceneUnloadParams p)
    {
        var scene = LoadedScene(p.Scene, "params.scene");
        context.Target = "scene://" + scene.Name;
        var operation = Control.UnloadScene(scene.Handle);
        while (!operation.IsDone)
        {
            yield return PumpWait.NextFrame;
        }

        yield return new SceneUnloadResult { Unloaded = !operation.Refused, Frame = Frame, RealtimeMs = RealtimeMs };
    }

    [RpcMethod(Methods.SceneSetActive)]
    public ProtocolMessage SceneSetActive(RequestContext context, SceneSetActiveParams p)
    {
        var scene = LoadedScene(p.Scene, "params.scene");
        context.Target = "scene://" + scene.Name;
        var previous = _unity.Scenes().FirstOrDefault(s => s.IsActive)?.Name ?? string.Empty;
        Control.SetActiveScene(scene.Handle);
        return new SceneSetActiveResult { Previous = previous, Active = _unity.Scenes().FirstOrDefault(s => s.IsActive)?.Name ?? string.Empty };
    }

    // ---- UI ----------------------------------------------------------------------------------------------------------------

    [RpcMethod(Methods.UiSnapshot)]
    public ProtocolMessage UiSnapshot(RequestContext context, UiSnapshotParams p) => new UiSnapshotResult
    {
        Items = Snapshot(p.OnlyInteractable ?? false, p.OnlyVisible ?? true, p.IncludeText ?? true, (int)Math.Max(1, Math.Min(p.Limit ?? DefaultUiLimit, 10_000))),
        Frame = Frame,
        RealtimeMs = RealtimeMs,
    };

    [RpcMethod(Methods.UiFind)]
    public ProtocolMessage UiFind(RequestContext context, UiFindParams p)
    {
        Regex? text = null;
        if (p.Text is { } pattern)
        {
            try
            {
                text = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException e)
            {
                throw ProtocolException.InvalidParams("params.text", $"params.text isn't a valid regular expression: {e.Message}");
            }
        }

        var items = Snapshot(false, true, true, 10_000).Where(e =>
            (text is null || (e.Text is not null && text.IsMatch(e.Text)))
            && (p.Path is null || e.Path == p.Path || e.Path.EndsWith("/" + p.Path, StringComparison.Ordinal))
            && (p.Kind is null || e.Kind == p.Kind)).ToList();
        return new UiFindResult { Items = items };
    }

    [RpcMethod(Methods.UiClick)]
    public ProtocolMessage UiClick(RequestContext context, UiClickParams p)
    {
        var (handled, via) = Act(context, p.Target, target => Ui.Click(target));
        return new UiClickResult { Handled = handled, Via = via, Frame = Frame, RealtimeMs = RealtimeMs };
    }

    [RpcMethod(Methods.UiSetText)]
    public ProtocolMessage UiSetText(RequestContext context, UiSetTextParams p)
    {
        var (handled, via) = Act(context, p.Target, target => Ui.SetText(target, p.Text, p.Submit ?? false));
        return new UiSetTextResult { Handled = handled, Via = via, Frame = Frame, RealtimeMs = RealtimeMs };
    }

    [RpcMethod(Methods.UiSetValue)]
    public ProtocolMessage UiSetValue(RequestContext context, UiSetValueParams p)
    {
        var value = PlainJson.ToObject(p.Value);
        var (handled, via) = Act(context, p.Target, target => Ui.SetValue(target, value));
        return new UiSetValueResult { Handled = handled, Via = via, Frame = Frame, RealtimeMs = RealtimeMs };
    }

    [RpcMethod(Methods.UiSubmit)]
    public ProtocolMessage UiSubmit(RequestContext context)
    {
        var outcome = Game(() => Ui.Submit());
        return new UiSubmitResult { Handled = outcome.Handled, Via = outcome.Via, Frame = Frame, RealtimeMs = RealtimeMs };
    }

    [RpcMethod(Methods.UiCancel)]
    public ProtocolMessage UiCancel(RequestContext context)
    {
        var outcome = Game(() => Ui.Cancel());
        return new UiCancelResult { Handled = outcome.Handled, Via = outcome.Via, Frame = Frame, RealtimeMs = RealtimeMs };
    }

    [RpcMethod(Methods.UiSelect)]
    public ProtocolMessage UiSelect(RequestContext context, UiSelectParams p)
    {
        var (handled, via) = Act(context, p.Target, target => Ui.Select(target));
        return new UiSelectResult { Handled = handled, Via = via, Frame = Frame, RealtimeMs = RealtimeMs };
    }

    // ---- application -------------------------------------------------------------------------------------------------------

    [RpcMethod(Methods.AppInfo)]
    public ProtocolMessage AppInfo(RequestContext context)
    {
        var a = Control.ReadApp();
        return new AppInfoResult
        {
            ProductName = a.ProductName, CompanyName = a.CompanyName, Version = a.Version, UnityVersion = a.UnityVersion, Platform = a.Platform,
            DataPath = a.DataPath, PersistentDataPath = a.PersistentDataPath, ConsoleLogPath = a.ConsoleLogPath, IsFocused = a.IsFocused,
            RunInBackground = a.RunInBackground, Screen = new ScreenInfo { Width = a.ScreenWidth, Height = a.ScreenHeight, FullScreen = a.FullScreen },
            QualityLevel = a.QualityLevel, GraphicsDevice = a.GraphicsDevice,
        };
    }

    [RpcMethod(Methods.AppRunInBackground)]
    public ProtocolMessage AppRunInBackground(RequestContext context, AppRunInBackgroundParams p)
    {
        var previous = Control.RunInBackground;
        Control.RunInBackground = p.Value;
        return new AppRunInBackgroundResult { Previous = previous, Current = Control.RunInBackground };
    }

    /// <summary>Answers first, then quits: the quit happens a moment later on the main thread, so the response is sent.</summary>
    [RpcMethod(Methods.AppQuit)]
    public ProtocolMessage AppQuit(RequestContext context, AppQuitParams p)
    {
        var control = Control;
        var exitCode = (int)(p.ExitCode ?? 0);
        _pump.Enqueue(new PumpWork(() => QuitSoon(control, exitCode), _ => { }, _ => { }));
        return new AppQuitResult { Quitting = true };
    }

    private static IEnumerable<object?> QuitSoon(IGameControl control, int exitCode)
    {
        yield return PumpWait.Realtime(250);
        control.Quit(exitCode);
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------------

    private List<UiElement> Snapshot(bool onlyInteractable, bool onlyVisible, bool includeText, int limit) =>
        Ui.Snapshot(onlyInteractable, onlyVisible, includeText, limit).Select(e => new UiElement
        {
            H = _data.Handles.Mint(e.GameObject),
            Path = _unity.Locate(e.GameObject) is { } at ? at.Path : string.Empty,
            Kind = e.Kind,
            Text = e.Text,
            Images = e.Images.Count > 0 ? e.Images.Select(i => new UiImage { Sprite = i.Sprite, Texture = i.Texture }).ToList() : null,
            Interactable = e.Interactable,
            IsOn = e.IsOn,
            Value = e.Value switch
            {
                null => null,
                string s => new JsonString(s),
                int i => new JsonNumber((long)i),
                double d => new JsonNumber(d),
                _ => null,
            },
            Options = e.Options,
            ScreenRect = new ScreenRect { X = Math.Round(e.ScreenRect.X, 1), Y = Math.Round(e.ScreenRect.Y, 1), W = Math.Round(e.ScreenRect.W, 1), H = Math.Round(e.ScreenRect.H, 1) },
            Canvas = e.Canvas,
            SortingOrder = e.SortingOrder,
            RaycastBlocked = e.RaycastBlocked,
            Locator = _data.Targets.LocatorBaseOf(e.GameObject),
        }).ToList();

    // Resolves the target (a GameObject or component, never the agent's own UI), audits it, and runs the action.
    private UiActionOutcome Act(RequestContext context, Target target, Func<object, UiActionOutcome> action)
    {
        var resolved = _data.Targets.Resolve(target, "params.target");
        var value = resolved.IsStatic ? null : resolved.Value;
        if (value is null || _unity.Describe(value) is null || _unity.Locate(value) is null)
        {
            throw ProtocolException.InvalidParams("params.target", "The target must be a GameObject or a component in a loaded scene.");
        }

        if (Ui.IsAgentOwned(value))
        {
            throw ProtocolException.InvalidParams("params.target", "The target is part of the agent's own UI.");
        }

        context.Target = _data.Targets.LocatorBaseOf(value);
        return Game(() => action(value));
    }

    private static UiActionOutcome Game(Func<UiActionOutcome> action)
    {
        try
        {
            return action();
        }
        catch (TargetInvocationException e)
        {
            throw AgentErrors.Game(e);
        }
        catch (ArgumentException e)
        {
            throw ProtocolException.InvalidParams("params", e.Message);
        }
        catch (NotSupportedException e)
        {
            throw DataErrors.Unsupported(e.Message);
        }
    }

    private SceneFacts LoadedScene(JsonValue scene, string param)
    {
        var scenes = _unity.Scenes().Where(s => !s.IsDontDestroyOnLoad).ToList();
        return scene switch
        {
            JsonNull => scenes.FirstOrDefault(s => s.IsActive) ?? throw DataErrors.NotFound(param, "No scene is active."),
            JsonString name => scenes.FirstOrDefault(s => s.Name == name.Value || s.Path == name.Value) ?? throw DataErrors.NotFound(param, $"No loaded scene '{name.Value}'."),
            JsonNumber n when n.TryGetInt32(out var number) =>
                scenes.FirstOrDefault(s => s.Handle == number) ?? scenes.FirstOrDefault(s => s.BuildIndex == number)
                ?? throw DataErrors.NotFound(param, $"No loaded scene with handle or build index {number}."),
            _ => throw ProtocolException.InvalidParams(param, $"{param} must be a scene name, a build index, a handle, or null for the active scene."),
        };
    }

    private double ResumeScale()
    {
        lock (_gate)
        {
            var current = Control.TimeScale;
            return current != 0 ? current : _pausedFrom ?? 1;
        }
    }

    // A smoothed frame rate from the real time between frames.
    private void OnTick(FrameTime clock)
    {
        var last = _lastRealtime;
        _lastRealtime = clock.Realtime;
        var delta = clock.Realtime - last;
        if (last < 0 || delta <= 0)
        {
            return;
        }

        var fps = 1.0 / delta;
        Volatile.Write(ref _fps, _fps == 0 ? fps : (_fps * 0.9) + (fps * 0.1));
    }

    private sealed class UiClickTrigger : ITrigger
    {
        private readonly ControlServices _owner;

        public UiClickTrigger(ControlServices owner) => _owner = owner;

        public string Kind => "ui.click";

        public string? Fire(Trigger trigger, string param)
        {
            var target = trigger.Target ?? throw ProtocolException.InvalidParams(param + ".target", "A ui.click trigger needs a target.");
            var resolved = _owner._data.Targets.Resolve(target, param + ".target");
            if (resolved.IsStatic || resolved.Value is null)
            {
                throw ProtocolException.InvalidParams(param + ".target", "The target must be a GameObject or a component.");
            }

            try
            {
                var outcome = _owner.Ui.Click(resolved.Value);
                return outcome.Handled ? null : "the click wasn't handled by anything";
            }
            catch (TargetInvocationException e)
            {
                var inner = e.InnerException ?? e;
                return $"the click threw {inner.GetType().Name}: {inner.Message}";
            }
        }
    }
}
