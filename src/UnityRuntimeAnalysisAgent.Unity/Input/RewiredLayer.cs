using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityRuntimeAnalysisAgent.Core.Input;

namespace UnityRuntimeAnalysisAgent.Unity.Input;

/// <summary>
/// The Rewired layer (Guavaman's Rewired, bound by reflection when the game ships it). Harmony postfixes on
/// <c>Rewired.Player</c>'s action queries (by name and by id) merge the session's virtual input: action values set by
/// name, and virtual keys, mouse buttons, mouse movement and wheel, and pad buttons and sticks wherever the player's own
/// control maps bind them to the queried action. So the game's actions, bindings and glyphs keep working; nothing is
/// guessed. Real input is read with the merge off, for takeover.
/// </summary>
public sealed class RewiredLayer : IInputLayer
{
    private const string HarmonyId = "com.github.rectangleequals.unityruntimeanalysisagent.input.rewired";

    // Rewired's controller types.
    private const int Keyboard = 0;
    private const int Mouse = 1;
    private const int Joystick = 2;

    // Mouse element names → virtual mouse inputs.
    private static readonly Dictionary<string, string> MouseButtons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Left Mouse Button"] = "left", ["Right Mouse Button"] = "right", ["Mouse Button 3"] = "middle",
        ["Mouse Button 4"] = "back", ["Mouse Button 5"] = "forward",
    };

    // Pad element names (Xbox and PlayStation layouts, Rewired's names) → virtual pad buttons.
    private static readonly Dictionary<string, string> PadButtons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = "south", ["Cross"] = "south", ["B"] = "east", ["Circle"] = "east", ["X"] = "west", ["Square"] = "west",
        ["Y"] = "north", ["Triangle"] = "north", ["Left Shoulder"] = "leftShoulder", ["L1"] = "leftShoulder",
        ["Right Shoulder"] = "rightShoulder", ["R1"] = "rightShoulder", ["Back"] = "select", ["Share"] = "select",
        ["Create"] = "select", ["Start"] = "start", ["Options"] = "start", ["Left Stick Button"] = "leftStickPress",
        ["L3"] = "leftStickPress", ["Right Stick Button"] = "rightStickPress", ["R3"] = "rightStickPress",
        ["D-Pad Up"] = "dpadUp", ["D-Pad Down"] = "dpadDown", ["D-Pad Left"] = "dpadLeft", ["D-Pad Right"] = "dpadRight",
    };

    private static volatile VirtualInput? s_input;
    [ThreadStatic] private static bool t_raw;
    private static Harmony? s_harmony;
    private static string? s_patchError;
    private static readonly Dictionary<(int Player, int Action), (long Frame, Value Value)> s_cache = new();
    private static readonly Dictionary<string, int> s_actionIds = new(StringComparer.Ordinal);
    private static readonly Dictionary<int, string> s_actionNames = new();

    /// <inheritdoc />
    public string Id => "rewired";

    /// <inheritdoc />
    public InputLayerStatus Status
    {
        get
        {
            if (Api.ReInput is null)
            {
                return new InputLayerStatus(false, null, Array.Empty<string>(), "Rewired isn't in this game.");
            }

            if (s_patchError is { } error)
            {
                return new InputLayerStatus(false, Api.Version(), Array.Empty<string>(), "Its action queries couldn't be patched: " + error);
            }

            return Api.Ready()
                ? new InputLayerStatus(true, Api.Version(), new[] { "actions", "keyboard", "mouse", "gamepad" }, $"Rewired is ready ({Api.Players().Count} players, {Api.Actions().Count} actions).")
                : new InputLayerStatus(false, Api.Version(), Array.Empty<string>(), "Rewired is loaded but not ready (ReInput.isReady is false).");
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<(string Kind, string Name)> Devices()
    {
        var devices = new List<(string, string)>();
        foreach (var player in Api.Players())
        {
            foreach (var joystick in Api.Joysticks(player))
            {
                devices.Add(("gamepad", Api.Name(joystick)));
            }
        }

        devices.Insert(0, ("keyboard", "Keyboard"));
        devices.Insert(1, ("mouse", "Mouse"));
        return devices.Distinct().ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<(string Name, string Kind, int? Player)> Actions() =>
        Api.Actions().Select(a => (Api.Name(a), Api.ActionType(a) == 1 ? "button" : "axis", (int?)null)).ToList();

    /// <inheritdoc />
    public IReadOnlyList<string> Axes() => Array.Empty<string>();

    /// <inheritdoc />
    public void Attach(VirtualInput input)
    {
        Patch();
        lock (s_cache)
        {
            s_cache.Clear();
        }

        s_input = input;
    }

    /// <inheritdoc />
    public void Detach() => s_input = null;

    /// <inheritdoc />
    public bool RealInput() => Raw(() => Api.Players().Any(p => Api.AnyButton(p)));

    /// <inheritdoc />
    public bool RealChord(IReadOnlyList<string> keys, IReadOnlyList<string> padButtons) => false; // keyboard: the Input Manager layer reads it; pads: Rewired's joysticks below

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

    // What the virtual input gives an action for a player this frame (cached per frame: queries repeat a lot).
    private static Value? Virtual(object player, int actionId)
    {
        if (t_raw || s_input is not { } input)
        {
            return null;
        }

        input.Advance(Time.frameCount, Time.realtimeSinceStartup);
        if (!input.Active)
        {
            return null;
        }

        var playerId = Api.PlayerId(player);
        lock (s_cache)
        {
            if (s_cache.TryGetValue((playerId, actionId), out var cached) && cached.Frame == Time.frameCount)
            {
                return cached.Value;
            }
        }

        var value = Compute(input, player, playerId, actionId);
        lock (s_cache)
        {
            s_cache[(playerId, actionId)] = (Time.frameCount, value);
        }

        return value;
    }

    // An action's virtual value: set by name, else from the elements the player's maps bind to it.
    private static Value Compute(VirtualInput input, object player, int playerId, int actionId)
    {
        var name = ActionName(actionId);
        var result = default(Value);
        if (name is not null && input.Action(name, playerId) is { } set)
        {
            result.Add(set.Axis, set.IsPressed, input.Down(InputKind.Action, name, playerId), input.Up(InputKind.Action, name, playerId));
        }

        foreach (var (type, key, element, isAxis, negative, invert) in Maps(player, playerId, actionId))
        {
            var sign = (negative ? -1 : 1) * (invert ? -1 : 1);
            switch (type)
            {
                case Keyboard when key != KeyCode.None:
                    Button(ref result, input, InputKind.Key, key.ToString(), sign);
                    break;
                case Mouse when MouseButtons.TryGetValue(element, out var button):
                    Button(ref result, input, InputKind.MouseButton, button, sign);
                    break;
                case Mouse when isAxis:
                    var (delta, wheel) = input.MouseMotion;
                    var amount = element.IndexOf("Wheel", StringComparison.OrdinalIgnoreCase) >= 0
                        ? (element.IndexOf("Horizontal", StringComparison.OrdinalIgnoreCase) >= 0 ? wheel.X : wheel.Y)
                        : element.IndexOf("Horizontal", StringComparison.OrdinalIgnoreCase) >= 0 ? delta.X * 0.1 : -delta.Y * 0.1;
                    result.Add(amount * (invert ? -1 : 1), false, false, false);
                    break;
                case Joystick when PadButtons.TryGetValue(element, out var pad):
                    Button(ref result, input, InputKind.GamepadButton, pad, sign);
                    break;
                case Joystick when isAxis:
                    var axes = input.GamepadAxes;
                    var stick = element.IndexOf("Right", StringComparison.OrdinalIgnoreCase) >= 0 ? axes.Right : axes.Left;
                    var v = element.IndexOf("Trigger", StringComparison.OrdinalIgnoreCase) >= 0
                        ? (element.IndexOf("Right", StringComparison.OrdinalIgnoreCase) >= 0 ? axes.RightTrigger : axes.LeftTrigger)
                        : element.EndsWith("X", StringComparison.OrdinalIgnoreCase) ? stick.X : stick.Y;
                    result.Add(v * (invert ? -1 : 1), false, false, false);
                    break;
            }
        }

        return result;
    }

    private static void Button(ref Value result, VirtualInput input, InputKind kind, string name, int sign)
    {
        if (input.Held(kind, name) || input.Up(kind, name))
        {
            result.Add(input.Held(kind, name) ? sign : 0, input.Held(kind, name), input.Down(kind, name), input.Up(kind, name));
        }
    }

    // The element maps that bind an action for a player, described (refreshed every couple of seconds: maps rarely change).
    private static readonly Dictionary<(int Player, int Action), (long Frame, List<(int, KeyCode, string, bool, bool, bool)> Maps)> s_maps = new();

    private static List<(int Type, KeyCode Key, string Element, bool Axis, bool Negative, bool Invert)> Maps(object player, int playerId, int actionId)
    {
        lock (s_maps)
        {
            if (s_maps.TryGetValue((playerId, actionId), out var cached) && Time.frameCount - cached.Frame < 120)
            {
                return cached.Maps;
            }
        }

        var maps = Api.ElementMaps(player, actionId).Select(Api.Describe).ToList();
        lock (s_maps)
        {
            s_maps[(playerId, actionId)] = (Time.frameCount, maps);
        }

        return maps;
    }

    private static string? ActionName(int id)
    {
        lock (s_actionNames)
        {
            if (!s_actionNames.TryGetValue(id, out var name))
            {
                name = Api.Action(id) is { } action ? Api.Name(action) : null;
                s_actionNames[id] = name!;
            }

            return name;
        }
    }

    private static int? ActionId(string name)
    {
        lock (s_actionIds)
        {
            if (!s_actionIds.TryGetValue(name, out var id))
            {
                id = Api.Action(name) is { } action ? Api.ActionId(action) : -1;
                s_actionIds[name] = id;
            }

            return id >= 0 ? id : null;
        }
    }

    private static void Patch()
    {
        if (s_harmony is not null || Api.Player is null)
        {
            return;
        }

        try
        {
            var harmony = s_harmony = new Harmony(HarmonyId);
            var player = Api.Player;
            void Postfix(string method, Type arg, string postfix)
            {
                if (player.GetMethod(method, BindingFlags.Public | BindingFlags.Instance, null, new[] { arg }, null) is { } target)
                {
                    harmony.Patch(target, postfix: new HarmonyMethod(typeof(RewiredLayer).GetMethod(postfix, BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.Last });
                }
            }

            foreach (var (method, postfix) in new[] { ("GetButton", "Held"), ("GetButtonDown", "Down"), ("GetButtonUp", "Up"), ("GetButtonRepeating", "Down"), ("GetAxis", "Axis"), ("GetAxisRaw", "Axis"), ("GetNegativeButton", "Negative") })
            {
                Postfix(method, typeof(int), postfix + "ById");
                Postfix(method, typeof(string), postfix + "ByName");
            }

            if (player.GetMethod("GetAnyButton", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null) is { } any)
            {
                harmony.Patch(any, postfix: new HarmonyMethod(typeof(RewiredLayer).GetMethod(nameof(AnyButton), BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.Last });
            }
        }
        catch (Exception e)
        {
            s_patchError = e.Message;
            s_harmony?.UnpatchSelf();
        }
    }

    private static void HeldById(object __instance, int actionId, ref bool __result) => Merge(__instance, actionId, v => v.Held, ref __result);

    private static void DownById(object __instance, int actionId, ref bool __result) => Merge(__instance, actionId, v => v.Down, ref __result);

    private static void UpById(object __instance, int actionId, ref bool __result) => Merge(__instance, actionId, v => v.Up, ref __result);

    private static void NegativeById(object __instance, int actionId, ref bool __result) => Merge(__instance, actionId, v => v.Axis < -0.5, ref __result);

    private static void AxisById(object __instance, int actionId, ref float __result) => MergeAxis(__instance, actionId, ref __result);

    private static void HeldByName(object __instance, string actionName, ref bool __result) => Merge(__instance, ActionId(actionName), v => v.Held, ref __result);

    private static void DownByName(object __instance, string actionName, ref bool __result) => Merge(__instance, ActionId(actionName), v => v.Down, ref __result);

    private static void UpByName(object __instance, string actionName, ref bool __result) => Merge(__instance, ActionId(actionName), v => v.Up, ref __result);

    private static void NegativeByName(object __instance, string actionName, ref bool __result) => Merge(__instance, ActionId(actionName), v => v.Axis < -0.5, ref __result);

    private static void AxisByName(object __instance, string actionName, ref float __result)
    {
        if (ActionId(actionName) is { } id)
        {
            MergeAxis(__instance, id, ref __result);
        }
    }

    private static void Merge(object player, int? actionId, Func<Value, bool> test, ref bool result)
    {
        if (!result && actionId is { } id && Virtual(player, id) is { } value && test(value))
        {
            result = true;
        }
    }

    private static void MergeAxis(object player, int actionId, ref float result)
    {
        if (Virtual(player, actionId) is { } value && Math.Abs(value.Axis) > Math.Abs(result))
        {
            result = (float)value.Axis;
        }
    }

    private static void AnyButton(object __instance, ref bool __result)
    {
        if (!__result && !t_raw && s_input is { } input && input.Active)
        {
            __result = Api.Actions().Any(a => Virtual(__instance, Api.ActionId(a)) is { Held: true });
        }
    }

    // A virtual contribution to an action: the strongest axis value, and whether any bound button is held, went down or came up.
    private struct Value
    {
        public double Axis;
        public bool Held;
        public bool Down;
        public bool Up;

        public void Add(double axis, bool held, bool down, bool up)
        {
            if (Math.Abs(axis) > Math.Abs(Axis))
            {
                Axis = axis;
            }

            Held |= held;
            Down |= down;
            Up |= up;
        }
    }

    // Rewired by reflection (it's the game's own assembly; the agent doesn't ship it).
    private static class Api
    {
        private static Type? s_reInput;
        private static Type? s_player;

        // Found once the game has loaded Rewired (the agent may start before it does).
        public static Type? ReInput => s_reInput ??= Find("Rewired.ReInput");

        public static Type? Player => s_player ??= Find("Rewired.Player");

        private const BindingFlags Static = BindingFlags.Public | BindingFlags.Static;
        private const BindingFlags Instance = BindingFlags.Public | BindingFlags.Instance;

        public static bool Ready() => ReInput?.GetProperty("isReady", Static)?.GetValue(null, null) is true;

        public static string? Version() => ReInput?.GetProperty("programVersion", Static)?.GetValue(null, null) as string;

        public static IList<object> Players() =>
            Ready() && Get(ReInput!.GetProperty("players", Static)?.GetValue(null, null), "Players") is IEnumerable players ? players.Cast<object>().ToList() : new List<object>();

        public static IList<object> Actions() =>
            Ready() && Get(Mapping(), "Actions") is IEnumerable actions ? actions.Cast<object>().ToList() : new List<object>();

        public static object? Action(int id) => Ready() ? Mapping()?.GetType().GetMethod("GetAction", Instance, null, new[] { typeof(int) }, null)?.Invoke(Mapping(), new object[] { id }) : null;

        public static object? Action(string name) => Ready() ? Mapping()?.GetType().GetMethod("GetAction", Instance, null, new[] { typeof(string) }, null)?.Invoke(Mapping(), new object[] { name }) : null;

        public static int ActionId(object action) => Get(action, "id") is int id ? id : -1;

        public static int ActionType(object action) => Convert.ToInt32(Get(action, "type"));

        public static string Name(object o) => Get(o, "name") as string ?? Get(o, "hardwareName") as string ?? "?";

        public static int PlayerId(object player) => Get(player, "id") is int id ? id : 0;

        public static IEnumerable<object> Joysticks(object player) => Get(Get(player, "controllers"), "Joysticks") is IEnumerable joysticks ? joysticks.Cast<object>() : Enumerable.Empty<object>();

        public static bool AnyButton(object player) => player.GetType().GetMethod("GetAnyButton", Instance, null, Type.EmptyTypes, null)?.Invoke(player, null) is true;

        public static IEnumerable<object> ElementMaps(object player, int actionId)
        {
            var maps = Get(Get(player, "controllers"), "maps");
            var method = maps?.GetType().GetMethod("ElementMapsWithAction", Instance, null, new[] { typeof(int), typeof(bool) }, null);
            return method?.Invoke(maps, new object[] { actionId, true }) is IEnumerable found ? found.Cast<object>().ToList() : Enumerable.Empty<object>();
        }

        // An element map: controller type, key code (keyboard), element name, axis or button, negative pole, inverted.
        public static (int Type, KeyCode Key, string Element, bool Axis, bool Negative, bool Invert) Describe(object map) => (
            Convert.ToInt32(Get(Get(map, "controllerMap"), "controllerType")),
            Get(map, "keyCode") is KeyCode key ? key : KeyCode.None,
            Get(map, "elementIdentifierName") as string ?? string.Empty,
            Convert.ToInt32(Get(map, "elementType")) == 0,
            Convert.ToInt32(Get(map, "axisContribution")) == 1,
            Get(map, "invert") is true);

        private static object? Mapping() => ReInput?.GetProperty("mapping", Static)?.GetValue(null, null);

        private static object? Get(object? o, string member) =>
            o is null ? null : o.GetType().GetProperty(member, Instance)?.GetValue(o, null) ?? o.GetType().GetField(member, Instance)?.GetValue(o);

        private static Type? Find(string name) =>
            AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name?.StartsWith("Rewired", StringComparison.Ordinal) == true)
                .Select(a => a.GetType(name, false)).FirstOrDefault(t => t is not null);
    }
}
