using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityRuntimeAnalysisAgent.Core.Input;

namespace UnityRuntimeAnalysisAgent.Unity.Input;

/// <summary>
/// The Input Manager layer (<c>UnityEngine.Input</c>, bound by reflection: <c>InputLegacyModule</c> since 2019.1).
/// Harmony postfixes on its queries merge the session's virtual input while attached: virtual OR real for keys and
/// buttons, the larger magnitude for axes. Mouse buttons and gamepad buttons are also the Input Manager's
/// <c>Mouse0..6</c> and <c>JoystickButton0..9</c> key codes (XInput numbering). Named buttons and axes use the action of
/// that name when one is set, else the project defaults' bindings (<c>Horizontal</c>, <c>Fire1</c>, <c>Mouse X</c>, …).
/// The patches stay in place once made; while detached they return at once.
/// </summary>
public sealed class InputManagerLayer : IInputLayer
{
    private const string HarmonyId = "com.github.rectangleequals.unityruntimeanalysisagent.input.inputmanager";

    private static readonly Type? LegacyInput = Type.GetType("UnityEngine.Input, UnityEngine.InputLegacyModule")
        ?? Type.GetType("UnityEngine.Input, UnityEngine.CoreModule") ?? Type.GetType("UnityEngine.Input, UnityEngine");

    // Mouse buttons and XInput pad buttons as Input Manager key codes.
    private static readonly string[] MouseNames = { "left", "right", "middle", "back", "forward" };
    private static readonly Dictionary<string, KeyCode> PadButtons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["south"] = KeyCode.JoystickButton0, ["east"] = KeyCode.JoystickButton1, ["west"] = KeyCode.JoystickButton2, ["north"] = KeyCode.JoystickButton3,
        ["leftShoulder"] = KeyCode.JoystickButton4, ["rightShoulder"] = KeyCode.JoystickButton5, ["select"] = KeyCode.JoystickButton6,
        ["start"] = KeyCode.JoystickButton7, ["leftStickPress"] = KeyCode.JoystickButton8, ["rightStickPress"] = KeyCode.JoystickButton9,
    };

    // The default Input Manager's named buttons (keys, mouse buttons, pad buttons that drive them).
    private static readonly Dictionary<string, KeyCode[]> DefaultButtons = new(StringComparer.Ordinal)
    {
        ["Fire1"] = new[] { KeyCode.LeftControl, KeyCode.Mouse0, KeyCode.JoystickButton0 },
        ["Fire2"] = new[] { KeyCode.LeftAlt, KeyCode.Mouse1, KeyCode.JoystickButton1 },
        ["Fire3"] = new[] { KeyCode.LeftShift, KeyCode.Mouse2, KeyCode.JoystickButton2 },
        ["Jump"] = new[] { KeyCode.Space, KeyCode.JoystickButton3 },
        ["Submit"] = new[] { KeyCode.Return, KeyCode.KeypadEnter, KeyCode.JoystickButton0 },
        ["Cancel"] = new[] { KeyCode.Escape, KeyCode.JoystickButton1 },
    };

    private static readonly string[] ProbeAxes =
    {
        "Horizontal", "Vertical", "Fire1", "Fire2", "Fire3", "Jump", "Mouse X", "Mouse Y", "Mouse ScrollWheel", "Submit", "Cancel",
    };

    // The Input Manager's queries as delegates: compiled against the 2018.1 API, a direct reference to UnityEngine.Input
    // would bind to the wrong module on 2019.1+ players.
    private static class Api
    {
        public static readonly Func<KeyCode, bool> GetKey = Bind<Func<KeyCode, bool>>("GetKey", typeof(KeyCode));
        public static readonly Func<string, float> GetAxisRaw = Bind<Func<string, float>>("GetAxisRaw", typeof(string));
        public static readonly Func<string[]> JoystickNames = Bind<Func<string[]>>("GetJoystickNames");
        public static readonly Func<bool> AnyKey = Getter<Func<bool>>("anyKey");
        public static readonly Func<Vector3> MousePosition = Getter<Func<Vector3>>("mousePosition");
        public static readonly Func<Vector2> ScrollDelta = Getter<Func<Vector2>>("mouseScrollDelta");

        private static T Bind<T>(string name, params Type[] args)
            where T : Delegate =>
            LegacyInput?.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, args, null) is { } method
                ? (T)Delegate.CreateDelegate(typeof(T), method)
                : throw new InvalidOperationException($"UnityEngine.Input.{name} isn't in this Unity build.");

        private static T Getter<T>(string name)
            where T : Delegate =>
            LegacyInput?.GetProperty(name, BindingFlags.Public | BindingFlags.Static)?.GetGetMethod() is { } method
                ? (T)Delegate.CreateDelegate(typeof(T), method)
                : throw new InvalidOperationException($"UnityEngine.Input.{name} isn't in this Unity build.");
    }

    private static volatile VirtualInput? s_input; // attached
    [ThreadStatic] private static bool t_raw; // reading real input (no merge)
    private static Harmony? s_harmony;
    private static string? s_patchError;
    private Vector3? _lastMouse;
    private IReadOnlyList<string>? _axes;

    /// <inheritdoc />
    public string Id => "inputManager";

    /// <inheritdoc />
    public InputLayerStatus Status
    {
        get
        {
            if (LegacyInput is null)
            {
                return new InputLayerStatus(false, null, Array.Empty<string>(), "UnityEngine.Input isn't in this Unity build.");
            }

            if (s_patchError is { } error)
            {
                return new InputLayerStatus(false, null, Array.Empty<string>(), "Its queries couldn't be patched: " + error);
            }

            try
            {
                Raw(() => Api.GetKey(KeyCode.None));
            }
            catch (InvalidOperationException)
            {
                return new InputLayerStatus(false, null, Array.Empty<string>(), "The game uses only the Input System package (the Input Manager is switched off in its player settings).");
            }

            return new InputLayerStatus(true, null, new[] { "keyboard", "mouse", "gamepad" }, "UnityEngine.Input is bound (" + LegacyInput.Assembly.GetName().Name + ").");
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<(string Kind, string Name)> Devices()
    {
        var devices = new List<(string, string)> { ("keyboard", "Keyboard"), ("mouse", "Mouse") };
        try
        {
            devices.AddRange(Api.JoystickNames().Where(n => !string.IsNullOrEmpty(n)).Select(n => ("gamepad", n)));
        }
        catch (InvalidOperationException)
        {
        }

        return devices;
    }

    /// <inheritdoc />
    public IReadOnlyList<(string Name, string Kind, int? Player)> Actions() => Array.Empty<(string, string, int?)>();

    /// <inheritdoc />
    public IReadOnlyList<string> Axes() => _axes ??= ProbeAxes.Where(Defined).ToList();

    /// <inheritdoc />
    public void Attach(VirtualInput input)
    {
        Patch();
        s_input = input;
        _lastMouse = null;
    }

    /// <inheritdoc />
    public void Detach() => s_input = null;

    /// <inheritdoc />
    public bool RealInput()
    {
        return Raw(() =>
        {
            if (Api.AnyKey())
            {
                return true; // keys, mouse buttons, pad buttons
            }

            var mouse = Api.MousePosition();
            var moved = _lastMouse is { } last && (mouse - last).sqrMagnitude > 16; // more than 4 pixels in a frame
            _lastMouse = mouse;
            if (moved || Api.ScrollDelta() != Vector2.zero)
            {
                return true;
            }

            return Axes().Where(a => a is "Horizontal" or "Vertical").Any(a => Math.Abs(Api.GetAxisRaw(a)) > 0.5f);
        });
    }

    /// <inheritdoc />
    public bool RealChord(IReadOnlyList<string> keys, IReadOnlyList<string> padButtons) => Raw(() =>
        (keys.Count > 0 && keys.All(k => Code(k) is { } code && Api.GetKey(code)))
        || (padButtons.Count > 0 && padButtons.All(b => PadButtons.TryGetValue(b, out var code) && Api.GetKey(code))));

    /// <summary>A key name (<c>UnityEngine.KeyCode</c>, or the Input Manager's string names such as <c>left shift</c>).</summary>
    public static KeyCode? Code(string name)
    {
        if (Enum.TryParse<KeyCode>(name, true, out var code) && Enum.IsDefined(typeof(KeyCode), code))
        {
            return code;
        }

        var n = name.Trim().ToLowerInvariant();
        if (n.Length == 1 && n[0] is >= '0' and <= '9')
        {
            return KeyCode.Alpha0 + (n[0] - '0');
        }

        if (n.StartsWith("[", StringComparison.Ordinal) && n.EndsWith("]", StringComparison.Ordinal) && n.Length == 3 && n[1] is >= '0' and <= '9')
        {
            return KeyCode.Keypad0 + (n[1] - '0');
        }

        if (n.StartsWith("mouse ", StringComparison.Ordinal) && int.TryParse(n.Substring(6), out var mouse) && mouse is >= 0 and <= 6)
        {
            return KeyCode.Mouse0 + mouse;
        }

        if (n.StartsWith("joystick button ", StringComparison.Ordinal) && int.TryParse(n.Substring(16), out var button) && button is >= 0 and <= 19)
        {
            return KeyCode.JoystickButton0 + button;
        }

        return n switch
        {
            "left shift" => KeyCode.LeftShift,
            "right shift" => KeyCode.RightShift,
            "left ctrl" => KeyCode.LeftControl,
            "right ctrl" => KeyCode.RightControl,
            "left alt" => KeyCode.LeftAlt,
            "right alt" => KeyCode.RightAlt,
            "left cmd" => KeyCode.LeftCommand,
            "right cmd" => KeyCode.RightCommand,
            "enter" => KeyCode.KeypadEnter,
            "page up" => KeyCode.PageUp,
            "page down" => KeyCode.PageDown,
            "caps lock" => KeyCode.CapsLock,
            _ => Enum.TryParse(n.Replace(" ", string.Empty), true, out code) ? code : null,
        };
    }

    private static T Raw<T>(Func<T> read)
    {
        var before = t_raw;
        t_raw = true;
        try
        {
            return read();
        }
        finally
        {
            t_raw = before;
        }
    }

    private static bool Defined(string axis)
    {
        try
        {
            Raw(() => Api.GetAxisRaw(axis));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    // The virtual input, advanced to this frame, when attached and not reading real input.
    private static VirtualInput? Live()
    {
        if (t_raw || s_input is not { } input)
        {
            return null;
        }

        input.Advance(Time.frameCount, Time.realtimeSinceStartup);
        return input.Active ? input : null;
    }

    // Whether a key code is held / went down / came up virtually (keys, mouse buttons, pad buttons).
    private static bool Virtual(VirtualInput input, KeyCode code, Func<VirtualInput, InputKind, string, bool> query)
    {
        if (code is >= KeyCode.Mouse0 and <= KeyCode.Mouse6)
        {
            var index = code - KeyCode.Mouse0;
            return index < MouseNames.Length && query(input, InputKind.MouseButton, MouseNames[index]);
        }

        if (code is >= KeyCode.JoystickButton0 and <= KeyCode.JoystickButton19 || code is >= KeyCode.Joystick1Button0 and <= KeyCode.Joystick1Button19)
        {
            var index = code >= KeyCode.Joystick1Button0 ? code - KeyCode.Joystick1Button0 : code - KeyCode.JoystickButton0;
            var pad = PadButtons.FirstOrDefault(p => p.Value - KeyCode.JoystickButton0 == index).Key;
            return pad is not null && query(input, InputKind.GamepadButton, pad);
        }

        return query(input, InputKind.Key, code.ToString());
    }

    private static bool Held(VirtualInput i, InputKind k, string n) => i.Held(k, n);

    private static bool Down(VirtualInput i, InputKind k, string n) => i.Down(k, n);

    private static bool Up(VirtualInput i, InputKind k, string n) => i.Up(k, n);

    private static void Patch()
    {
        if (s_harmony is not null || LegacyInput is null)
        {
            return;
        }

        try
        {
            var harmony = s_harmony = new Harmony(HarmonyId);
            var input = LegacyInput;
            void Postfix(string method, Type[] args, string postfix)
            {
                if (input.GetMethod(method, BindingFlags.Public | BindingFlags.Static, null, args, null) is { } target)
                {
                    harmony.Patch(target, postfix: new HarmonyMethod(typeof(InputManagerLayer).GetMethod(postfix, BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.Last });
                }
            }

            void Getter(string property, string postfix)
            {
                if (input.GetProperty(property, BindingFlags.Public | BindingFlags.Static)?.GetGetMethod() is { } target)
                {
                    harmony.Patch(target, postfix: new HarmonyMethod(typeof(InputManagerLayer).GetMethod(postfix, BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.Last });
                }
            }

            Postfix("GetKey", new[] { typeof(KeyCode) }, nameof(KeyHeld));
            Postfix("GetKeyDown", new[] { typeof(KeyCode) }, nameof(KeyDown));
            Postfix("GetKeyUp", new[] { typeof(KeyCode) }, nameof(KeyUp));
            Postfix("GetKey", new[] { typeof(string) }, nameof(NamedKeyHeld));
            Postfix("GetKeyDown", new[] { typeof(string) }, nameof(NamedKeyDown));
            Postfix("GetKeyUp", new[] { typeof(string) }, nameof(NamedKeyUp));
            Postfix("GetMouseButton", new[] { typeof(int) }, nameof(MouseHeld));
            Postfix("GetMouseButtonDown", new[] { typeof(int) }, nameof(MouseDown));
            Postfix("GetMouseButtonUp", new[] { typeof(int) }, nameof(MouseUp));
            Postfix("GetButton", new[] { typeof(string) }, nameof(ButtonHeld));
            Postfix("GetButtonDown", new[] { typeof(string) }, nameof(ButtonDown));
            Postfix("GetButtonUp", new[] { typeof(string) }, nameof(ButtonUp));
            Postfix("GetAxis", new[] { typeof(string) }, nameof(Axis));
            Postfix("GetAxisRaw", new[] { typeof(string) }, nameof(Axis));
            Getter("anyKey", nameof(AnyKey));
            Getter("anyKeyDown", nameof(AnyKeyDown));
            Getter("mousePosition", nameof(MousePosition));
            Getter("mouseScrollDelta", nameof(ScrollDelta));
        }
        catch (Exception e)
        {
            s_patchError = e.Message;
            s_harmony?.UnpatchSelf();
        }
    }

    private static void KeyHeld(KeyCode key, ref bool __result) => Merge(key, Held, ref __result);

    private static void KeyDown(KeyCode key, ref bool __result) => Merge(key, Down, ref __result);

    private static void KeyUp(KeyCode key, ref bool __result) => Merge(key, Up, ref __result);

    private static void NamedKeyHeld(string name, ref bool __result) => MergeNamed(name, Held, ref __result);

    private static void NamedKeyDown(string name, ref bool __result) => MergeNamed(name, Down, ref __result);

    private static void NamedKeyUp(string name, ref bool __result) => MergeNamed(name, Up, ref __result);

    private static void MouseHeld(int button, ref bool __result) => Merge(KeyCode.Mouse0 + button, Held, ref __result);

    private static void MouseDown(int button, ref bool __result) => Merge(KeyCode.Mouse0 + button, Down, ref __result);

    private static void MouseUp(int button, ref bool __result) => Merge(KeyCode.Mouse0 + button, Up, ref __result);

    private static void ButtonHeld(string buttonName, ref bool __result) => MergeButton(buttonName, Held, ref __result);

    private static void ButtonDown(string buttonName, ref bool __result) => MergeButton(buttonName, Down, ref __result);

    private static void ButtonUp(string buttonName, ref bool __result) => MergeButton(buttonName, Up, ref __result);

    private static void Merge(KeyCode key, Func<VirtualInput, InputKind, string, bool> query, ref bool result)
    {
        if (!result && Live() is { } input && Virtual(input, key, query))
        {
            result = true;
        }
    }

    private static void MergeNamed(string name, Func<VirtualInput, InputKind, string, bool> query, ref bool result)
    {
        if (!result && Live() is { } input && Code(name) is { } code && Virtual(input, code, query))
        {
            result = true;
        }
    }

    // A named button: the action of that name, else the keys the default Input Manager binds to it.
    private static void MergeButton(string name, Func<VirtualInput, InputKind, string, bool> query, ref bool result)
    {
        if (result || Live() is not { } input)
        {
            return;
        }

        if (query(input, InputKind.Action, name) || (DefaultButtons.TryGetValue(name, out var codes) && codes.Any(c => Virtual(input, c, query))))
        {
            result = true;
        }
    }

    // A named axis: the action of that name, else the default bindings (keys, sticks, mouse movement and wheel).
    private static void Axis(string axisName, ref float __result)
    {
        if (Live() is not { } input)
        {
            return;
        }

        var value = input.Action(axisName) is { } action ? action.Axis : DefaultAxis(input, axisName);
        if (Math.Abs(value) > Math.Abs(__result))
        {
            __result = (float)value;
        }
    }

    private static double DefaultAxis(VirtualInput input, string axis)
    {
        double Keys(KeyCode negative, KeyCode altNegative, KeyCode positive, KeyCode altPositive) =>
            (Virtual(input, positive, Held) || Virtual(input, altPositive, Held) ? 1 : 0) - (Virtual(input, negative, Held) || Virtual(input, altNegative, Held) ? 1 : 0);

        var pads = input.GamepadAxes;
        var (delta, wheel) = input.MouseMotion;
        return axis switch
        {
            "Horizontal" => Bigger(Keys(KeyCode.A, KeyCode.LeftArrow, KeyCode.D, KeyCode.RightArrow), pads.Left.X),
            "Vertical" => Bigger(Keys(KeyCode.S, KeyCode.DownArrow, KeyCode.W, KeyCode.UpArrow), pads.Left.Y),
            "Mouse X" => delta.X * 0.1, // the default sensitivity
            "Mouse Y" => -delta.Y * 0.1, // screen y grows down; the axis is positive up
            "Mouse ScrollWheel" => wheel.Y * 0.1,
            _ => DefaultButtons.TryGetValue(axis, out var codes) && codes.Any(c => Virtual(input, c, Held)) ? 1 : 0,
        };
    }

    private static double Bigger(double a, double b) => Math.Abs(a) >= Math.Abs(b) ? a : b;

    private static void AnyKey(ref bool __result)
    {
        if (!__result && Live() is { } input && (input.KeysHeld.Count > 0 || MouseNames.Any(m => input.Held(InputKind.MouseButton, m)) || PadButtons.Keys.Any(p => input.Held(InputKind.GamepadButton, p))))
        {
            __result = true;
        }
    }

    private static void AnyKeyDown(ref bool __result)
    {
        if (!__result && Live() is { } input
            && (input.KeysHeld.Any(k => input.Down(InputKind.Key, k)) || MouseNames.Any(m => input.Down(InputKind.MouseButton, m)) || PadButtons.Keys.Any(p => input.Down(InputKind.GamepadButton, p))))
        {
            __result = true;
        }
    }

    // The virtual pointer replaces the real one while set (screen pixels, origin top left → Unity's bottom left).
    private static void MousePosition(ref Vector3 __result)
    {
        if (Live() is { } input && input.MousePosition is { } p)
        {
            __result = new Vector3((float)p.X, Screen.height - (float)p.Y, 0);
        }
    }

    private static void ScrollDelta(ref Vector2 __result)
    {
        if (Live() is { } input && input.MouseMotion.Wheel is var (x, y) && (x != 0 || y != 0))
        {
            __result += new Vector2((float)x, (float)y);
        }
    }
}
