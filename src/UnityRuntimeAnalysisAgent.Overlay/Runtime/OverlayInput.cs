using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityRuntimeAnalysisAgent.Core.Hosting;
using UnityRuntimeAnalysisAgent.Core.Overlay;
using UnityRuntimeAnalysisAgent.Overlay.Input;

namespace UnityRuntimeAnalysisAgent.Overlay.Runtime;

/// <summary>
/// The overlay's own input, read without touching the game's: where the pointer is (Input Manager or Input System, by
/// reflection), the gamepad chord, and <c>Overlay.BlockWorldInput</c> — an opt-in Harmony patch that makes the Input
/// Manager's mouse-button queries answer "not pressed" to the game's code while the pointer is over the overlay (UI
/// event systems still see them, so the overlay and its blocker keep working). Games that only use the Input System
/// package aren't covered by that patch.
/// </summary>
public sealed class OverlayInput : IDisposable
{
    /// <summary>The Harmony id of the world-input patch.</summary>
    public const string HarmonyId = "com.github.rectangleequals.unityruntimeanalysisagent.overlay.input";

    // The assemblies whose input queries are never blocked: UI event handling (uGUI, UI Toolkit, the Input System's UI
    // module) and the agent.
    private static readonly string[] ExemptAssemblies = { "UnityEngine.UI", "UnityEngine.UIElementsModule", "Unity.InputSystem", "UnityRuntimeAnalysisAgent." };

    private static volatile bool s_blocking;

    private static readonly Type? LegacyInput = Type.GetType("UnityEngine.Input, UnityEngine.InputLegacyModule")
        ?? Type.GetType("UnityEngine.Input, UnityEngine.CoreModule") ?? Type.GetType("UnityEngine.Input, UnityEngine");

    private readonly OverlayController _controller;
    private readonly IAgentLogger _log;
    private readonly GamepadReader _gamepad = new();
    private readonly ChordDetector _chord;
    private Harmony? _harmony;
    private bool _legacyPointer = LegacyInput is not null;

    /// <summary>Creates it (and applies the world-input patch when it's switched on).</summary>
    public OverlayInput(OverlayController controller, IAgentLogger log)
    {
        _controller = controller;
        _log = log;
        _chord = new ChordDetector(controller.Settings.GamepadToggle);
        if (controller.Settings.BlockWorldInput)
        {
            PatchWorldInput();
        }
    }

    /// <summary>Whether the pointer was over the overlay this frame.</summary>
    public bool PointerOver { get; private set; }

    /// <summary>Where the pointer was this frame, in screen pixels from the top-left, or null.</summary>
    public Vector2? PointerPosition => _hasPointer ? new Vector2(_pointerX, _pointerY) : null;

    // Plain fields (no engine types), so this class loads outside the engine too.
    private bool _hasPointer;
    private float _pointerX;
    private float _pointerY;

    /// <summary>The gamepad buttons held this frame (names as in <c>Overlay.GamepadToggle</c>).</summary>
    public IReadOnlyCollection<string> HeldButtons { get; private set; } = Array.Empty<string>();

    /// <summary>Called every frame: hover state for the patch, and the gamepad chord (which toggles the overlay).</summary>
    public void Frame(IReadOnlyList<Rect> occupied)
    {
        var pointer = Pointer();
        _hasPointer = pointer.HasValue;
        _pointerX = pointer?.x ?? 0;
        _pointerY = pointer?.y ?? 0;
        PointerOver = pointer is { } p && occupied.Any(r => r.Contains(p));
        s_blocking = _harmony is not null && PointerOver;
        if (_controller.Settings.Gamepad == "off")
        {
            return;
        }

        HeldButtons = _gamepad.Held();
        if (_chord.Update(HeldButtons))
        {
            _controller.Model.Toggle();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        s_blocking = false;
        _harmony?.UnpatchSelf();
        _harmony = null;
    }

    /// <summary>Whether an input query from this call stack is exempt from blocking (UI event handling, the agent).</summary>
    public static bool Exempt(IEnumerable<string?> callerAssemblies) =>
        callerAssemblies.Any(name => name is not null && ExemptAssemblies.Any(e => e.EndsWith(".", StringComparison.Ordinal) ? name.StartsWith(e, StringComparison.Ordinal) : name == e));

    // The pointer in screen pixels from the top-left, or null when there's no pointer to read.
    private Vector2? Pointer()
    {
        if (_legacyPointer)
        {
            try
            {
                if (LegacyInput!.GetProperty("mousePosition", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null) is Vector3 position)
                {
                    return new Vector2(position.x, Screen.height - position.y);
                }
            }
            catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException)
            {
                _legacyPointer = false; // the project switched the Input Manager off
            }
        }

        return GamepadReader.InputSystemPointer() is { } p ? new Vector2(p.x, Screen.height - p.y) : null;
    }

    private void PatchWorldInput()
    {
        if (LegacyInput is null)
        {
            return;
        }

        try
        {
            _harmony = new Harmony(HarmonyId);
            var postfix = new HarmonyMethod(typeof(OverlayInput).GetMethod(nameof(MouseButtonPostfix), BindingFlags.NonPublic | BindingFlags.Static));
            foreach (var name in new[] { "GetMouseButton", "GetMouseButtonDown", "GetMouseButtonUp" })
            {
                if (LegacyInput.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int) }, null) is { } method)
                {
                    _harmony.Patch(method, postfix: postfix);
                }
            }
        }
        catch (Exception e)
        {
            _log.Warning($"Overlay.BlockWorldInput couldn't patch the mouse-button queries: {e.Message}");
            _harmony?.UnpatchSelf();
            _harmony = null;
        }
    }

    private static void MouseButtonPostfix(ref bool __result)
    {
        if (__result && s_blocking && !Exempt(new StackTrace(1, false).GetFrames()?.Select(f => f.GetMethod()?.DeclaringType?.Assembly.GetName().Name) ?? Enumerable.Empty<string?>()))
        {
            __result = false;
        }
    }
}

/// <summary>
/// Reads the first gamepad's buttons by reflection: the Input System package when it's loaded, otherwise the Input
/// Manager's joystick buttons (standard XInput numbering; no triggers or d-pad there, they're axes).
/// </summary>
public sealed class GamepadReader
{
    // Input System control names for our button names.
    private static readonly (string Name, string Control)[] InputSystemButtons =
    {
        ("A", "buttonSouth"), ("B", "buttonEast"), ("X", "buttonWest"), ("Y", "buttonNorth"),
        ("LB", "leftShoulder"), ("RB", "rightShoulder"), ("LT", "leftTrigger"), ("RT", "rightTrigger"),
        ("Select", "selectButton"), ("Start", "startButton"), ("LS", "leftStickButton"), ("RS", "rightStickButton"),
    };

    // Input Manager joystick buttons (XInput layout on Windows).
    private static readonly (string Name, KeyCode Key)[] LegacyButtons =
    {
        ("A", KeyCode.JoystickButton0), ("B", KeyCode.JoystickButton1), ("X", KeyCode.JoystickButton2), ("Y", KeyCode.JoystickButton3),
        ("LB", KeyCode.JoystickButton4), ("RB", KeyCode.JoystickButton5), ("Select", KeyCode.JoystickButton6), ("Start", KeyCode.JoystickButton7),
        ("LS", KeyCode.JoystickButton8), ("RS", KeyCode.JoystickButton9),
    };

    private static readonly Type? LegacyInput = Type.GetType("UnityEngine.Input, UnityEngine.InputLegacyModule")
        ?? Type.GetType("UnityEngine.Input, UnityEngine.CoreModule") ?? Type.GetType("UnityEngine.Input, UnityEngine");

    private readonly List<string> _held = new();
    private bool _legacy = LegacyInput is not null;

    /// <summary>The buttons held now (empty without a gamepad).</summary>
    public IReadOnlyCollection<string> Held()
    {
        _held.Clear();
        if (InputSystemType("UnityEngine.InputSystem.Gamepad") is { } gamepadType
            && gamepadType.GetProperty("current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null) is { } gamepad)
        {
            foreach (var (name, control) in InputSystemButtons)
            {
                var button = gamepadType.GetProperty(control, BindingFlags.Public | BindingFlags.Instance)?.GetValue(gamepad, null);
                if (button?.GetType().GetProperty("isPressed", BindingFlags.Public | BindingFlags.Instance)?.GetValue(button, null) is true)
                {
                    _held.Add(name);
                }
            }

            var dpad = gamepadType.GetProperty("dpad", BindingFlags.Public | BindingFlags.Instance)?.GetValue(gamepad, null);
            foreach (var direction in new[] { "up", "down", "left", "right" })
            {
                var button = dpad?.GetType().GetProperty(direction, BindingFlags.Public | BindingFlags.Instance)?.GetValue(dpad, null);
                if (button?.GetType().GetProperty("isPressed", BindingFlags.Public | BindingFlags.Instance)?.GetValue(button, null) is true)
                {
                    _held.Add(char.ToUpperInvariant(direction[0]) + direction.Substring(1));
                }
            }

            return _held;
        }

        if (_legacy && LegacyInput!.GetMethod("GetKey", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(KeyCode) }, null) is { } getKey)
        {
            try
            {
                foreach (var (name, key) in LegacyButtons)
                {
                    if (getKey.Invoke(null, new object[] { key }) is true)
                    {
                        _held.Add(name);
                    }
                }
            }
            catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException)
            {
                _legacy = false; // the project switched the Input Manager off
            }
        }

        return _held;
    }

    /// <summary>The Input System mouse position (bottom-left origin), or null without the package or a mouse.</summary>
    public static Vector2? InputSystemPointer()
    {
        if (InputSystemType("UnityEngine.InputSystem.Mouse") is not { } mouseType
            || mouseType.GetProperty("current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null) is not { } mouse)
        {
            return null;
        }

        var position = mouseType.GetProperty("position", BindingFlags.Public | BindingFlags.Instance)?.GetValue(mouse, null);
        return position?.GetType().GetMethod("ReadValue", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)?.Invoke(position, null) as Vector2?;
    }

    // The Input System assembly, looked up again at most every few seconds until it's found (it can load late).
    private static Assembly? s_inputSystem;
    private static float s_nextLookup;

    private static Type? InputSystemType(string name)
    {
        if (s_inputSystem is null && Time.realtimeSinceStartup >= s_nextLookup)
        {
            s_nextLookup = Time.realtimeSinceStartup + 5;
            s_inputSystem = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Unity.InputSystem");
        }

        return s_inputSystem?.GetType(name, throwOnError: false);
    }
}
