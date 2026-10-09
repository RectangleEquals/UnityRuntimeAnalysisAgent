using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityRuntimeAnalysisAgent.Core.Hosting;

namespace UnityRuntimeAnalysisAgent.Core.Overlay;

/// <summary>The overlay's states.</summary>
public enum OverlayVisibility
{
    /// <summary>Nothing drawn; only the hotkeys are checked.</summary>
    Hidden,

    /// <summary>The arrow on its edge (and toasts).</summary>
    Collapsed,

    /// <summary>The tabbed panel.</summary>
    Expanded,
}

/// <summary>A screen edge the overlay docks to.</summary>
public enum OverlayEdge
{
    /// <summary>The left edge (the arrow points right).</summary>
    Left,

    /// <summary>The right edge (the arrow points left).</summary>
    Right,

    /// <summary>The top edge (the arrow points down).</summary>
    Top,

    /// <summary>The bottom edge (the arrow points up).</summary>
    Bottom,
}

/// <summary>Which notifications are shown (<c>Overlay.Toasts</c>).</summary>
public enum ToastFilter
{
    /// <summary>None.</summary>
    Off,

    /// <summary>Warnings, errors and those sent by the client.</summary>
    Important,

    /// <summary>Every notification.</summary>
    All,
}

/// <summary>Writes a setting back to the loader's config file (the overlay persists what the user changes).</summary>
public interface IConfigWriter
{
    /// <summary>Stores <paramref name="value"/> for <paramref name="key"/> (<c>Section.Name</c>), in the loader's notation.</summary>
    void Set(string key, string value);
}

/// <summary>
/// The <c>Overlay.*</c> settings, validated: a value that can't be used falls back to its default with a warning.
/// </summary>
public sealed class OverlaySettings
{
    /// <summary>The tabs, in display order.</summary>
    public static readonly IReadOnlyList<string> AllTabs = new[] { "status", "activity", "inspector", "pinned", "instrumentation", "mods", "logs", "control", "settings" };

    private readonly List<string> _warnings = new();

    /// <summary>Show the overlay at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How the overlay starts.</summary>
    public OverlayVisibility StartState { get; set; } = OverlayVisibility.Collapsed;

    /// <summary>The edge it's docked to.</summary>
    public OverlayEdge Edge { get; set; } = OverlayEdge.Right;

    /// <summary>Position along the edge, 0–1.</summary>
    public double EdgeOffset { get; set; } = 0.5;

    /// <summary>Docked to its edge (false: floating).</summary>
    public bool Docked { get; set; } = true;

    /// <summary>The panel size in reference pixels, or null for automatic.</summary>
    public (int Width, int Height)? PanelSize { get; set; }

    /// <summary>The UI scale, or null for automatic (screen height / 1080, clamped 0.75–2.5).</summary>
    public double? Scale { get; set; }

    /// <summary>Panel opacity, 0.2–1.</summary>
    public double Opacity { get; set; } = 0.92;

    /// <summary>Base font size in reference pixels.</summary>
    public int FontSize { get; set; } = 13;

    /// <summary>View-model refreshes per second (1–30).</summary>
    public int RefreshHz { get; set; } = 4;

    /// <summary>Which notifications are shown.</summary>
    public ToastFilter Toasts { get; set; } = ToastFilter.Important;

    /// <summary>Keep clicks on the overlay from reaching the game's UI.</summary>
    public bool BlockUiClicks { get; set; } = true;

    /// <summary>Keep mouse input over the overlay from reaching the game world (a narrow patch; off by default).</summary>
    public bool BlockWorldInput { get; set; }

    /// <summary>
    /// Keeping the keyboard from the game while typing into the overlay: auto (Unity's input systems, plus a Windows
    /// keyboard hook for input read outside Unity), unity (Unity's input systems only) or off.
    /// </summary>
    public string KeyboardCapture { get; set; } = "auto";

    /// <summary>Show and free the cursor while expanded.</summary>
    public bool ForceCursorWhenExpanded { get; set; } = true;

    /// <summary>The Inspector starts locked.</summary>
    public bool InspectorStartsLocked { get; set; } = true;

    /// <summary>The local user may pause and step the game even in ReadOnly.</summary>
    public bool LocalTimeControl { get; set; } = true;

    /// <summary>E-STOP also pauses the game.</summary>
    public bool EStopPauses { get; set; }

    /// <summary>E-STOP also disconnects clients.</summary>
    public bool EStopDisconnects { get; set; } = true;

    /// <summary>Hide the overlay in the agent's screenshots.</summary>
    public bool ExcludeFromScreenshots { get; set; } = true;

    /// <summary>The visible tabs, in display order.</summary>
    public IReadOnlyList<string> VisibleTabs { get; set; } = AllTabs;

    /// <summary>The renderer: auto, uitoolkit, ugui or imgui.</summary>
    public string Renderer { get; set; } = "auto";

    /// <summary>The theme's name (a file in the overlay's themes folder).</summary>
    public string Theme { get; set; } = "default";

    /// <summary>The pixel font for all text (false: the smooth set).</summary>
    public bool RetroFonts { get; set; } = true;

    /// <summary>Effects: on, reduced or off.</summary>
    public string Effects { get; set; } = "on";

    /// <summary>Gamepad control: auto or off.</summary>
    public string Gamepad { get; set; } = "auto";

    /// <summary>The gamepad chord that toggles the overlay (button names joined by '+').</summary>
    public IReadOnlyList<string> GamepadToggle { get; set; } = new[] { "Select", "Start" };

    /// <summary>Pause the game while the overlay has gamepad focus.</summary>
    public bool GamepadPausesGame { get; set; }

    /// <summary>Follow selections relayed from a static tool (only offered when the client declares it).</summary>
    public bool FollowStaticToolSelection { get; set; }

    /// <summary>Problems found while reading (each already resolved to a default).</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Reads the settings.</summary>
    public static OverlaySettings Read(IConfigSource source)
    {
        var s = new OverlaySettings();
        s.Enabled = s.Bool(source, "Enabled", s.Enabled);
        s.StartState = s.Choice(source, "StartState", s.StartState);
        s.Edge = s.Choice(source, "Edge", s.Edge);
        s.EdgeOffset = s.Number(source, "EdgeOffset", s.EdgeOffset, 0, 1);
        s.Docked = s.Bool(source, "Docked", s.Docked);
        s.PanelSize = s.Size(source, "PanelSize");
        s.Scale = s.Auto(source, "Scale", 0.5, 4);
        s.Opacity = s.Number(source, "Opacity", s.Opacity, 0.2, 1);
        s.FontSize = (int)s.Number(source, "FontSize", s.FontSize, 8, 48);
        s.RefreshHz = (int)s.Number(source, "RefreshHz", s.RefreshHz, 1, 30);
        s.Toasts = s.Choice(source, "Toasts", s.Toasts);
        s.BlockUiClicks = s.Bool(source, "BlockUiClicks", s.BlockUiClicks);
        s.BlockWorldInput = s.Bool(source, "BlockWorldInput", s.BlockWorldInput);
        s.KeyboardCapture = s.Word(source, "KeyboardCapture", s.KeyboardCapture, "auto", "unity", "off");
        s.ForceCursorWhenExpanded = s.Bool(source, "ForceCursorWhenExpanded", s.ForceCursorWhenExpanded);
        s.InspectorStartsLocked = s.Bool(source, "InspectorStartsLocked", s.InspectorStartsLocked);
        s.LocalTimeControl = s.Bool(source, "LocalTimeControl", s.LocalTimeControl);
        s.EStopPauses = s.Bool(source, "EStopPauses", s.EStopPauses);
        s.EStopDisconnects = s.Bool(source, "EStopDisconnects", s.EStopDisconnects);
        s.ExcludeFromScreenshots = s.Bool(source, "ExcludeFromScreenshots", s.ExcludeFromScreenshots);
        s.VisibleTabs = s.Tabs(source);
        s.Renderer = s.Word(source, "Renderer", s.Renderer, "auto", "uitoolkit", "ugui", "imgui");
        s.Theme = s.Text(source, "Theme") ?? s.Theme;
        s.RetroFonts = s.Bool(source, "RetroFonts", s.RetroFonts);
        s.Effects = s.Word(source, "Effects", s.Effects, "on", "reduced", "off");
        s.Gamepad = s.Word(source, "Gamepad", s.Gamepad, "auto", "off");
        s.GamepadToggle = s.Text(source, "GamepadToggle") is { } chord
            ? chord.Split('+').Select(b => b.Trim()).Where(b => b.Length > 0).ToArray()
            : s.GamepadToggle;
        s.GamepadPausesGame = s.Bool(source, "GamepadPausesGame", s.GamepadPausesGame);
        s.FollowStaticToolSelection = s.Bool(source, "FollowStaticToolSelection", s.FollowStaticToolSelection);
        return s;
    }

    /// <summary>The effective UI scale for a screen height: the setting, or height / 1080 clamped to 0.75–2.5.</summary>
    public double EffectiveScale(int screenHeight) => Scale ?? Math.Max(0.75, Math.Min(2.5, screenHeight / 1080.0));

    private static string Key(string name) => "Overlay." + name;

    private string? Text(IConfigSource source, string name) => source.Get(Key(name)) is { } text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    private bool Bool(IConfigSource source, string name, bool fallback)
    {
        var text = Text(source, name);
        if (text is null)
        {
            return fallback;
        }

        if (bool.TryParse(text, out var value))
        {
            return value;
        }

        _warnings.Add($"{Key(name)} '{text}' is not true or false; using {fallback.ToString().ToLowerInvariant()}.");
        return fallback;
    }

    private double Number(IConfigSource source, string name, double fallback, double min, double max)
    {
        var text = Text(source, name);
        if (text is null)
        {
            return fallback;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max)
        {
            return value;
        }

        _warnings.Add($"{Key(name)} '{text}' is not a number from {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}; using {fallback.ToString(CultureInfo.InvariantCulture)}.");
        return fallback;
    }

    private double? Auto(IConfigSource source, string name, double min, double max)
    {
        var text = Text(source, name);
        if (text is null || text.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max)
        {
            return value;
        }

        _warnings.Add($"{Key(name)} '{text}' is not auto or a number from {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}; using auto.");
        return null;
    }

    private (int, int)? Size(IConfigSource source, string name)
    {
        var text = Text(source, name);
        if (text is null || text.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = text.Split('x', 'X');
        if (parts.Length == 2 && int.TryParse(parts[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var w)
            && int.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var h) && w >= 200 && h >= 150 && w <= 8192 && h <= 8192)
        {
            return (w, h);
        }

        _warnings.Add($"{Key(name)} '{text}' is not auto or <width>x<height> (at least 200x150); using auto.");
        return null;
    }

    private T Choice<T>(IConfigSource source, string name, T fallback)
        where T : struct, Enum
    {
        var text = Text(source, name);
        if (text is null)
        {
            return fallback;
        }

        if (Enum.TryParse<T>(text, ignoreCase: true, out var value) && Enum.IsDefined(typeof(T), value))
        {
            return value;
        }

        _warnings.Add($"{Key(name)} '{text}' is not one of {string.Join(", ", Enum.GetNames(typeof(T)))}; using {fallback}.");
        return fallback;
    }

    private string Word(IConfigSource source, string name, string fallback, params string[] choices)
    {
        var text = Text(source, name);
        if (text is null)
        {
            return fallback;
        }

        if (choices.FirstOrDefault(c => c.Equals(text, StringComparison.OrdinalIgnoreCase)) is { } choice)
        {
            return choice;
        }

        _warnings.Add($"{Key(name)} '{text}' is not one of {string.Join(", ", choices)}; using {fallback}.");
        return fallback;
    }

    private IReadOnlyList<string> Tabs(IConfigSource source)
    {
        var text = Text(source, "VisibleTabs");
        if (text is null || text.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return AllTabs;
        }

        var wanted = text.Split(',').Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).ToList();
        foreach (var unknown in wanted.Where(t => !AllTabs.Contains(t)))
        {
            _warnings.Add($"Overlay.VisibleTabs: '{unknown}' isn't a tab ({string.Join(", ", AllTabs)}); ignored.");
        }

        // Status and Control always stay: they hold the health view and E-STOP.
        var tabs = AllTabs.Where(t => wanted.Contains(t) || t is "status" or "control").ToList();
        return tabs;
    }
}
