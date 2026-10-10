using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityRuntimeAnalysisAgent.Core.Hosting;

namespace UnityRuntimeAnalysisAgent.Overlay.Runtime;

/// <summary>
/// Keeps the cursor shown and free while the overlay's panel is open, and gives the game back the cursor it wants when
/// the panel closes. While held, the game's own writes to <c>Cursor.visible</c> and <c>Cursor.lockState</c> are recorded
/// instead of applied (a game that locks the cursor when its menu closes would otherwise take it back mid-panel, or be
/// overwritten); on release the game's latest request wins, else the state from before the panel opened. Without the
/// patches (they failed), the snapshot alone is restored.
/// </summary>
internal sealed class CursorGuard : IDisposable
{
    private const string HarmonyId = "com.github.rectangleequals.unityruntimeanalysisagent.overlay.cursor";

    private static bool s_holding;
    private static bool? s_wantVisible;
    private static CursorLockMode? s_wantLock;
    [ThreadStatic] private static bool t_own;

    private readonly Harmony? _harmony;
    private (bool Visible, CursorLockMode Lock)? _saved;

    public CursorGuard(IAgentLogger log)
    {
        try
        {
            _harmony = new Harmony(HarmonyId);
            var type = typeof(UnityEngine.Cursor);
            _harmony.Patch(type.GetProperty(nameof(UnityEngine.Cursor.visible))!.GetSetMethod(), prefix: new HarmonyMethod(typeof(CursorGuard).GetMethod(nameof(VisiblePrefix), BindingFlags.NonPublic | BindingFlags.Static)));
            _harmony.Patch(type.GetProperty(nameof(UnityEngine.Cursor.lockState))!.GetSetMethod(), prefix: new HarmonyMethod(typeof(CursorGuard).GetMethod(nameof(LockPrefix), BindingFlags.NonPublic | BindingFlags.Static)));
        }
        catch (Exception e)
        {
            log.Warning($"The overlay couldn't watch the game's cursor changes; closing the panel restores the cursor from when it opened: {e.Message}");
            _harmony?.UnpatchSelf();
            _harmony = null;
        }
    }

    /// <summary>Shows and frees the cursor (each frame while the panel is open).</summary>
    public void Hold()
    {
        if (!s_holding)
        {
            _saved = (UnityEngine.Cursor.visible, UnityEngine.Cursor.lockState);
            s_wantVisible = null;
            s_wantLock = null;
            s_holding = true;
        }

        Write(true, CursorLockMode.None);
    }

    /// <summary>Gives the cursor back: the game's latest request while held, else its state from before.</summary>
    public void Release()
    {
        if (!s_holding)
        {
            return;
        }

        s_holding = false;
        if (_saved is { } saved)
        {
            Write(s_wantVisible ?? saved.Visible, s_wantLock ?? saved.Lock);
        }

        _saved = null;
    }

    public void Dispose()
    {
        Release();
        _harmony?.UnpatchSelf();
    }

    private static void Write(bool visible, CursorLockMode mode)
    {
        t_own = true;
        try
        {
            UnityEngine.Cursor.visible = visible;
            UnityEngine.Cursor.lockState = mode;
        }
        finally
        {
            t_own = false;
        }
    }

    private static bool VisiblePrefix(bool value)
    {
        if (!s_holding || t_own)
        {
            return true;
        }

        s_wantVisible = value;
        return false;
    }

    private static bool LockPrefix(CursorLockMode value)
    {
        if (!s_holding || t_own)
        {
            return true;
        }

        s_wantLock = value;
        return false;
    }
}
