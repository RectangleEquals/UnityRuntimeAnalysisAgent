using System;
using System.Linq;
using System.Reflection;

namespace UnityRuntimeAnalysisAgent.Overlay.Runtime;

/// <summary>
/// Whether a mouse button is held now, read from the mouse itself (the Input Manager, or the Input System package), by
/// reflection: renderers use it to keep the overlay steady while a click is in progress.
/// </summary>
public static class PointerButtons
{
    private static readonly MethodInfo? LegacyGetMouseButton =
        (Type.GetType("UnityEngine.Input, UnityEngine.InputLegacyModule") ?? Type.GetType("UnityEngine.Input, UnityEngine.CoreModule") ?? Type.GetType("UnityEngine.Input, UnityEngine"))
        ?.GetMethod("GetMouseButton", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int) }, null);

    private static bool s_legacy = LegacyGetMouseButton is not null;

    private static readonly PropertyInfo? LegacyScroll = LegacyGetMouseButton?.DeclaringType?.GetProperty("mouseScrollDelta", BindingFlags.Public | BindingFlags.Static);

    /// <summary>The mouse wheel's movement this frame (positive = up), from the Input Manager or the Input System.</summary>
    public static float Wheel()
    {
        if (s_legacy)
        {
            try
            {
                return LegacyScroll?.GetValue(null, null) is UnityEngine.Vector2 delta ? delta.y : 0;
            }
            catch (TargetInvocationException)
            {
                s_legacy = false;
            }
        }

        var mouseType = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Unity.InputSystem")?.GetType("UnityEngine.InputSystem.Mouse");
        var mouse = mouseType?.GetProperty("current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null);
        var scroll = mouse?.GetType().GetProperty("scroll")?.GetValue(mouse, null);
        var value = scroll?.GetType().GetMethod("ReadValue", Type.EmptyTypes)?.Invoke(scroll, null);
        return value is UnityEngine.Vector2 v ? v.y : 0;
    }

    /// <summary>Whether the left or right mouse button is held.</summary>
    public static bool Held()
    {
        if (s_legacy)
        {
            try
            {
                return (bool)LegacyGetMouseButton!.Invoke(null, new object[] { 0 }) || (bool)LegacyGetMouseButton.Invoke(null, new object[] { 1 });
            }
            catch (TargetInvocationException)
            {
                s_legacy = false; // the project switched the Input Manager off
            }
        }

        var mouseType = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Unity.InputSystem")?.GetType("UnityEngine.InputSystem.Mouse");
        var mouse = mouseType?.GetProperty("current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null);
        foreach (var name in new[] { "leftButton", "rightButton" })
        {
            var button = mouse?.GetType().GetProperty(name)?.GetValue(mouse, null);
            if (button?.GetType().GetProperty("isPressed")?.GetValue(button, null) is true)
            {
                return true;
            }
        }

        return false;
    }
}
