using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityRuntimeAnalysisAgent.Core.Abstractions;
using UnityRuntimeAnalysisAgent.Core.Hosting;
using UnityRuntimeAnalysisAgent.Core.Overlay;
using UnityRuntimeAnalysisAgent.Overlay.Assets;
using UnityRuntimeAnalysisAgent.Overlay.Ugui;
using UnityRuntimeAnalysisAgent.Overlay.Views;

namespace UnityRuntimeAnalysisAgent.Overlay.Runtime;

/// <summary>
/// Runs the overlay in the game: loads its files and the bundle for this Unity version, picks a renderer (UI Toolkit,
/// then styled uGUI, then the IMGUI emergency view; <c>Overlay.Renderer</c> can force one), and every frame on the
/// main thread polls the hotkeys, keeps the cursor usable while expanded, restarts a renderer the game destroyed, and lets
/// the renderer follow the model. Failures never reach the game: the overlay falls back or stays off, and says why.
/// </summary>
public sealed class OverlayRuntime : IDisposable
{
    /// <summary>The UI Toolkit renderer's type, in its own assembly (compiled against 2021.3; loaded only when chosen).</summary>
    public const string UiToolkitRendererType = "UnityRuntimeAnalysisAgent.Overlay.UIToolkit.UiToolkitRenderer, UnityRuntimeAnalysisAgent.Overlay.UIToolkit";

    private readonly AgentHost _host;
    private readonly OverlayController _controller;
    private readonly ILoaderApi? _loader;
    private readonly IAgentLogger _log;
    private readonly OverlayContext _context;
    private readonly List<(string Name, Func<IOverlayRenderer?> Create, Func<string?> Why)> _candidates;
    private readonly Action<FrameTime> _tick;
    private readonly OverlayInput _input;
    private readonly KeyboardCapture _keyboard;
    private UguiClickBlocker? _blocker;
    private bool _noUgui;
    private IOverlayRenderer? _renderer;
    private int _rendererIndex;
    private List<string> _rendererReasons = new();
    private CursorGuard? _cursor;
    private int _restarts;
    private bool _disposed;

    private OverlayRuntime(AgentHost host, OverlayController controller, ILoaderApi? loader, IAgentLogger log, string overlayDir, LoadedBundle? bundle, string? bundleProblem)
    {
        _host = host;
        _controller = controller;
        _loader = loader;
        _log = log;
        _cursor = new CursorGuard(log);
        Bundle = bundle;
        _context = new OverlayContext(controller, OverlayFiles.LoadTheme(overlayDir, controller.Settings.Theme, log), bundle, overlayDir, log, () => Time.realtimeSinceStartup);
        _candidates = Candidates(controller.Settings.Renderer, bundle, bundleProblem);
        _tick = _ => OnFrame();
        _input = new OverlayInput(controller, log);
        _keyboard = new KeyboardCapture(controller.Settings.KeyboardCapture, log);
    }

    /// <summary>The loaded bundle family, if any.</summary>
    public LoadedBundle? Bundle { get; }

    /// <summary>The renderer drawing the overlay.</summary>
    public IOverlayRenderer? Renderer => _renderer;

    /// <summary>
    /// Starts the overlay for a running agent (nothing when <c>Overlay.Enabled</c> is off). Never throws: a failure is
    /// logged and the agent runs without its overlay.
    /// </summary>
    public static OverlayRuntime? Start(AgentHost host, ILoaderApi? loader, IAgentLogger log)
    {
        if (host.Overlay is not { } controller)
        {
            return null;
        }

        try
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            var overlayDir = OverlayFiles.Directory;
            LoadedBundle? bundle = null;
            string? bundleProblem = null;
            var bundleMs = 0L;
            try
            {
                bundle = LoadedBundle.Load(OverlayAssets.Load(overlayDir), Application.unityVersion);
                bundleMs = started.ElapsedMilliseconds;
            }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                bundleProblem = e.Message;
                log.Warning($"Overlay: {e.Message} Fonts and effects fall back to built-in ones.");
            }

            var runtime = new OverlayRuntime(host, controller, loader, log, overlayDir, bundle, bundleProblem);
            runtime.Choose();
            log.Info($"Overlay started in {started.ElapsedMilliseconds} ms (bundle {bundleMs} ms).");
            host.Pump.Ticked += runtime._tick;
            host.RegisterCleanup("stop the overlay", runtime.Dispose);
            return runtime;
        }
        catch (Exception e)
        {
            log.Error("The overlay failed to start; the agent runs without it.", e);
            controller.SetRenderer("none", $"The overlay failed to start: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// The renderers to try, in order, with why each can't run here (null when it can). <c>auto</c> tries all; a forced
    /// renderer is tried first and the ones after it are the fallbacks.
    /// </summary>
    public static IReadOnlyList<string> Order(string setting) => setting switch
    {
        "ugui" => new[] { "ugui", "imgui" },
        "imgui" => new[] { "imgui" },
        _ => new[] { "uitoolkit", "ugui", "imgui" },
    };

    /// <summary>Why UI Toolkit can't draw the overlay in a Unity version with a given bundle family, or null when it can.</summary>
    public static string? UiToolkitBlocked(string unityVersion, string? family)
    {
        var parts = unityVersion.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(new string(parts[1].TakeWhile(char.IsDigit).ToArray()), NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
        {
            return $"Unity version '{unityVersion}' can't be read.";
        }

        if (major < 2021 || (major == 2021 && minor < 3))
        {
            return $"UI Toolkit's runtime needs Unity 2021.3 or newer (this game uses {unityVersion}).";
        }

        if (major == 2023 && minor == 2)
        {
            return "UI Toolkit's runtime text rendering is broken in Unity 2023.2.";
        }

        return family is null or "legacy" ? "No UI Toolkit bundle for this Unity version." : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _host.Pump.Ticked -= _tick;
        _cursor?.Dispose();
        _renderer?.Stop();
        _renderer = null;
        _blocker?.Dispose();
        _input.Dispose();
        _keyboard.Dispose();
        Bundle?.Dispose();
    }

    private List<(string, Func<IOverlayRenderer?>, Func<string?>)> Candidates(string setting, LoadedBundle? bundle, string? bundleProblem)
    {
        var all = new Dictionary<string, (Func<IOverlayRenderer?>, Func<string?>)>
        {
            ["uitoolkit"] = (() => (IOverlayRenderer?)Activator.CreateInstance(Type.GetType(UiToolkitRendererType, throwOnError: true)!),
                () => UiToolkitBlocked(Application.unityVersion, bundle?.Family.Name) is { } why ? why + (bundleProblem is null ? "" : $" ({bundleProblem})")
                    : Type.GetType("UnityEngine.UIElements.PanelSettings, UnityEngine.UIElementsModule") is null ? "UI Toolkit's types aren't in this game."
                    : null),
            ["ugui"] = (() => new UguiRenderer(),
                () => Type.GetType("UnityEngine.UI.Graphic, UnityEngine.UI") is null ? "uGUI (UnityEngine.UI) isn't in this game." : null),
            ["imgui"] = (() => new ImguiEmergencyRenderer(), () => null),
        };
        return Order(setting).Select(name => (name, all[name].Item1, all[name].Item2)).ToList();
    }

    // Tries the candidates in order (from a given one) and records which one runs and why the ones before it didn't.
    private void Choose(int from = 0, List<string>? earlier = null)
    {
        var reasons = earlier ?? new List<string>();
        for (var index = from; index < _candidates.Count; index++)
        {
            var (name, create, why) = _candidates[index];
            var blocked = why();
            if (blocked is not null)
            {
                reasons.Add($"{name}: {blocked}");
                continue;
            }

            IOverlayRenderer? renderer = null;
            string? failed = null;
            try
            {
                renderer = create();
                if (renderer is ImguiEmergencyRenderer emergency)
                {
                    emergency.Reason = reasons.Count == 0 ? "The emergency view was chosen." : string.Join(" ", reasons);
                }

                if (renderer is not null && renderer.TryStart(_context, out failed))
                {
                    _renderer = renderer;
                    _rendererIndex = index;
                    _rendererReasons = reasons;
                    var reason = reasons.Count == 0 ? (_controller.Settings.Renderer == "auto" ? "The first renderer that works here." : "Chosen in Overlay.Renderer.") : string.Join("; ", reasons);
                    _controller.SetRenderer(name, reason);
                    _controller.Automation = new OverlayAutomation(renderer, _controller); // clients can drive what it draws
                    _log.Info($"Overlay drawn with {name}{(reasons.Count == 0 ? "" : $" ({reason})")}.");
                    return;
                }

                renderer?.Stop();
                reasons.Add($"{name}: {failed ?? "didn't start"}");
            }
            catch (Exception e)
            {
                renderer?.Stop();
                reasons.Add($"{name}: {e.GetType().Name}: {e.Message}");
            }
        }

        _controller.Automation = null;
        _controller.SetRenderer("none", string.Join("; ", reasons));
        _log.Warning($"The overlay can't be drawn in this game: {string.Join("; ", reasons)}");
    }

    // The text box with the keyboard focus (KeyboardFocus) takes the keyboard: typed text, the caret keys, Enter,
    // Backspace, Escape and Ctrl+V/C/X with the system clipboard; the game sees none of it (KeyboardCapture). A click
    // outside every text box blurs it so the game gets the keyboard back.
    private void Typing()
    {
        var box = _controller.Keyboard.Focused;
        _keyboard.Active = box is not null;
        if (box is null)
        {
            if (_renderer is { TextFields.Count: > 0 })
            {
                ScrollField(PointerButtons.Wheel()); // a box without the keyboard scrolls too
            }

            return;
        }

        var typed = _keyboard.Take();
        ScrollField(typed.Wheel);
        // A mouse press anywhere but a text box blurs it: the overlay's buttons and arrow then work at once, and a press
        // in the game gives it the keyboard back. A press in a box is the renderer's (it moves the caret or the focus).
        if (typed.MousePressed && FieldUnderPointer() is null)
        {
            _controller.Keyboard.Blur();
            return;
        }

        if (typed.Cancel)
        {
            box.Cancel();
            return;
        }

        if (typed.Copy && box.SelectedText is { } selected)
        {
            GUIUtility.systemCopyBuffer = selected;
        }

        if (typed.Cut && box.Cut() is { Length: > 0 } cut)
        {
            GUIUtility.systemCopyBuffer = cut;
        }

        if (typed.Paste)
        {
            box.Paste(GUIUtility.systemCopyBuffer);
        }

        box.Type(typed.Text);
    }

    // The mouse wheel over a text box scrolls its lines (three per notch), leaving the caret where it is (renderers that
    // see wheel events themselves decide there instead). The wheel latch decides whether the box gets it (WheelLatch); a
    // box with nothing to scroll counts as outside it.
    private void ScrollField(float wheel)
    {
        if (wheel == 0 || _renderer is null || _renderer.HandlesWheel)
        {
            return;
        }

        var box = FieldUnderPointer() is { CanScroll: true } over ? over : null;
        if (_controller.Wheel.Claim(box is not null, Time.realtimeSinceStartup))
        {
            box!.ScrollLines(wheel > 0 ? -3 : 3);
        }
    }

    // The text box under the pointer, or null.
    private TextBox? FieldUnderPointer()
    {
        if (_renderer is null || _input.PointerPosition is not { } pointer)
        {
            return null;
        }

        foreach (var (rect, box) in _renderer.TextFields)
        {
            if (rect.Contains(pointer))
            {
                return box;
            }
        }

        return null;
    }

    private void OnFrame()
    {
        try
        {
            Hotkeys();
            if (_renderer?.Failure is { } failure)
            {
                // It started but can't draw correctly here: the next renderer takes over.
                _log.Warning($"Overlay renderer {_renderer.Name}: {failure}");
                _renderer.Stop();
                _renderer = null;
                var reasons = new List<string>(_rendererReasons) { $"{_candidates[_rendererIndex].Name}: {failure}" };
                Choose(_rendererIndex + 1, reasons);
                return;
            }

            if (_renderer is { Alive: false })
            {
                // The game destroyed the overlay's objects (e.g. a scene load that destroys everything): build them again.
                _restarts++;
                _renderer.Stop();
                if (_restarts > 20 || !_renderer.TryStart(_context, out _))
                {
                    _log.Warning("The overlay's objects keep being destroyed; the overlay stops.");
                    _controller.Automation = null;
                    _controller.SetRenderer("none", "The game keeps destroying the overlay's objects.");
                    _renderer = null;
                    return;
                }
            }

            Cursor();
            Typing();

            _renderer?.Update();
            var occupied = _renderer?.Occupied ?? Array.Empty<Rect>();
            _input.Frame(occupied);
            Block(occupied);
        }
        catch (Exception e)
        {
            _log.Error("The overlay failed during a frame; it stops (the agent keeps running).", e);
            _controller.Automation = null;
            _controller.SetRenderer("none", $"The overlay stopped after an error: {e.Message}");
            _renderer?.Stop();
            _renderer = null;
        }
    }

    // Overlay.BlockUiClicks for UI Toolkit (uGUI's own canvas already takes the clicks; IMGUI's box is tiny).
    private void Block(IReadOnlyList<Rect> occupied)
    {
        var wanted = _controller.Settings.BlockUiClicks && _renderer?.Name == "uitoolkit";
        if (!wanted || _noUgui)
        {
            _blocker?.Dispose();
            _blocker = null;
            return;
        }

        if (_blocker is not { Alive: true })
        {
            _blocker?.Dispose();
            _blocker = new UguiClickBlocker();
            if (!_blocker.Start())
            {
                _noUgui = true; // no uGUI in this game: nothing of the game's to block
                _blocker = null;
                return;
            }
        }

        _blocker.Cover(occupied);
    }

    private void Hotkeys()
    {
        if (_loader is null)
        {
            return;
        }

        var model = _controller.Model;
        if (_loader.IsShortcutPressed("Overlay.HideKey"))
        {
            model.Hide();
        }
        else if (_loader.IsShortcutPressed("Overlay.ToggleKey"))
        {
            model.Toggle();
        }

        if (_loader.IsShortcutPressed("Overlay.PickKey"))
        {
            model.SetPickMode(!model.PickMode);
        }

        if (_loader.IsShortcutPressed("Overlay.EStopKey") && !_controller.EStop.Engaged)
        {
            _controller.EngageEStop();
        }
    }

    // While expanded (when configured), the cursor is shown and free; the game's own settings come back on collapse.
    private void Cursor()
    {
        var expanded = _controller.Model.State == OverlayVisibility.Expanded && _controller.Settings.ForceCursorWhenExpanded;
        if (expanded)
        {
            _cursor?.Hold();
        }
        else
        {
            _cursor?.Release();
        }
    }

}
