using System.Collections.Generic;

namespace UnityRuntimeAnalysisAgent.Core.Hosting;

/// <summary>How a loader should store a setting (the loader shim maps these to its own config types).</summary>
public enum ConfigKind
{
    /// <summary>Free text.</summary>
    Text,

    /// <summary><c>true</c> / <c>false</c>.</summary>
    Bool,

    /// <summary>A whole number.</summary>
    Int,

    /// <summary>A decimal number.</summary>
    Float,

    /// <summary>One of <see cref="ConfigKey.Choices"/>.</summary>
    Choice,

    /// <summary>A keyboard shortcut in the loader's notation (empty = none).</summary>
    Shortcut,
}

/// <summary>One setting of the agent: its section, name, default and meaning.</summary>
public sealed class ConfigKey
{
    internal ConfigKey(string section, string name, ConfigKind kind, string defaultValue, string description, params string[] choices)
    {
        Section = section;
        Name = name;
        Kind = kind;
        Default = defaultValue;
        Description = description;
        Choices = choices;
    }

    /// <summary>The section (e.g. <c>Security</c>).</summary>
    public string Section { get; }

    /// <summary>The name within the section (e.g. <c>Mode</c>).</summary>
    public string Name { get; }

    /// <summary><c>Section.Name</c>, the key <see cref="IConfigSource.Get"/> takes.</summary>
    public string Key => Section + "." + Name;

    /// <summary>How the value is stored.</summary>
    public ConfigKind Kind { get; }

    /// <summary>The default, as text (shortcuts in BepInEx notation, e.g. <c>F9 + LeftControl</c>).</summary>
    public string Default { get; }

    /// <summary>What the setting does.</summary>
    public string Description { get; }

    /// <summary>The allowed values of a <see cref="ConfigKind.Choice"/>.</summary>
    public IReadOnlyList<string> Choices { get; }
}

/// <summary>
/// Every setting of the agent, in one place: the loader shim binds them all (with these defaults and descriptions) when the
/// plugin loads, so the config file always lists every setting, including ones for features that arrive later.
/// </summary>
public static class ConfigKeys
{
    /// <summary>All settings, in file order.</summary>
    public static IReadOnlyList<ConfigKey> All { get; } = new[]
    {
        new ConfigKey("Discovery", "ProvidersDir", ConfigKind.Text, "", "Folder for the discovery file (agent-<pid>.json) that lets clients find and authenticate to the agent. Set by the orchestrator; without it no client can connect."),
        new ConfigKey("Security", "Mode", ConfigKind.Choice, "ReadOnly", "What clients may do: ReadOnly (observe), ReadOnly+Load (also load content) or Full (also change the game and run code). Clients can lower it at runtime, never raise it.", "ReadOnly", "ReadOnly+Load", "Full"),
        new ConfigKey("Transport", "Mode", ConfigKind.Choice, "auto", "auto: a named pipe, falling back to TCP on 127.0.0.1. pipe or tcp: only that.", "auto", "pipe", "tcp"),
        new ConfigKey("Transport", "MaxFrameBytes", ConfigKind.Int, "16777216", "Largest message accepted or sent, in bytes."),
        new ConfigKey("Pump", "FrameBudgetMs", ConfigKind.Int, "4", "Main-thread time per frame for the agent's work, in milliseconds."),
        new ConfigKey("Pump", "StallMs", ConfigKind.Int, "3000", "After this long without a frame, main-thread requests fail with MAIN_THREAD_UNAVAILABLE instead of waiting."),
        new ConfigKey("Jobs", "MaxConcurrent", ConfigKind.Int, "2", "Long-running jobs (surveys, indexes, exports) at once; more wait in a queue."),
        new ConfigKey("Handles", "Max", ConfigKind.Int, "20000", "Live object handles kept (least recently used ones are released)."),
        new ConfigKey("Instrumentation", "MaxMethods", ConfigKind.Int, "2000", "Methods instrumented at once."),
        new ConfigKey("Instrumentation", "RemoveOnDisconnect", ConfigKind.Bool, "true", "Remove a client's non-persistent instrumentation and rules when it disconnects."),
        new ConfigKey("Logs", "BufferSize", ConfigKind.Int, "10000", "Log lines kept in memory."),
        new ConfigKey("Events", "MaxQueueBytes", ConfigKind.Int, "8388608", "Unsent events per client, in bytes; over it the oldest events are dropped and counted (never replies)."),
        new ConfigKey("Agent", "LogLevel", ConfigKind.Choice, "Info", "The agent's own log verbosity.", "Debug", "Info", "Warning", "Error"),
        new ConfigKey("Rules", "MaxActive", ConfigKind.Int, "32", "Automation rules active at once."),
        new ConfigKey("Rules", "MaxFiresPerMinute", ConfigKind.Int, "60", "Rule firings per minute before rules are suspended."),
        new ConfigKey("Rules", "MaxCapturesPerMinute", ConfigKind.Int, "30", "Screenshots taken by rules per minute."),
        new ConfigKey("Rules", "MaxPauseMs", ConfigKind.Int, "30000", "Longest a rule may keep the game paused, in milliseconds."),
        new ConfigKey("Overlay", "Enabled", ConfigKind.Bool, "true", "Show the in-game overlay."),
        new ConfigKey("Overlay", "ToggleKey", ConfigKind.Shortcut, "F9 + LeftControl", "Expand or collapse the overlay."),
        new ConfigKey("Overlay", "HideKey", ConfigKind.Shortcut, "F9 + LeftControl + LeftShift", "Hide the overlay completely, or show it again."),
        new ConfigKey("Overlay", "PickKey", ConfigKind.Shortcut, "F8 + LeftControl", "Pick an object in the game."),
        new ConfigKey("Overlay", "EStopKey", ConfigKind.Shortcut, "", "E-STOP: stop all automated activity at once (no shortcut by default)."),
        new ConfigKey("Overlay", "StartState", ConfigKind.Choice, "Collapsed", "How the overlay starts.", "Hidden", "Collapsed", "Expanded"),
        new ConfigKey("Overlay", "Edge", ConfigKind.Choice, "Right", "The screen edge the overlay is docked to.", "Left", "Right", "Top", "Bottom"),
        new ConfigKey("Overlay", "EdgeOffset", ConfigKind.Float, "0.5", "Position along that edge, from 0 to 1."),
        new ConfigKey("Overlay", "Docked", ConfigKind.Bool, "true", "Keep the overlay docked to its edge."),
        new ConfigKey("Overlay", "PanelSize", ConfigKind.Text, "auto", "Panel size (auto or width x height)."),
        new ConfigKey("Overlay", "Scale", ConfigKind.Text, "auto", "UI scale (auto follows the screen height)."),
        new ConfigKey("Overlay", "Opacity", ConfigKind.Float, "0.92", "Panel opacity."),
        new ConfigKey("Overlay", "FontSize", ConfigKind.Int, "13", "Font size."),
        new ConfigKey("Overlay", "RefreshHz", ConfigKind.Int, "4", "How often the panel's contents refresh, per second."),
        new ConfigKey("Overlay", "Toasts", ConfigKind.Text, "Important", "Which notifications are shown next to the overlay."),
        new ConfigKey("Overlay", "BlockUiClicks", ConfigKind.Bool, "true", "Keep clicks on the overlay from reaching the game's UI."),
        new ConfigKey("Overlay", "BlockWorldInput", ConfigKind.Bool, "false", "Keep input over the overlay from reaching the game world."),
        new ConfigKey("Overlay", "ForceCursorWhenExpanded", ConfigKind.Bool, "true", "Show and free the mouse cursor while the overlay is expanded."),
        new ConfigKey("Overlay", "InspectorStartsLocked", ConfigKind.Bool, "true", "The inspector starts locked (no edits until unlocked)."),
        new ConfigKey("Overlay", "LocalTimeControl", ConfigKind.Bool, "true", "Allow pausing and stepping the game from the overlay."),
        new ConfigKey("Overlay", "EStopPauses", ConfigKind.Bool, "false", "E-STOP also pauses the game."),
        new ConfigKey("Overlay", "EStopDisconnects", ConfigKind.Bool, "true", "E-STOP also disconnects clients."),
        new ConfigKey("Overlay", "ExcludeFromScreenshots", ConfigKind.Bool, "true", "Hide the overlay in screenshots the agent takes."),
        new ConfigKey("Overlay", "VisibleTabs", ConfigKind.Text, "all", "The overlay tabs to show (all, or a comma-separated list)."),
        new ConfigKey("Overlay", "FollowStaticToolSelection", ConfigKind.Bool, "false", "Follow selections made in a static analysis tool, when the orchestrator relays them."),
        new ConfigKey("Overlay", "Renderer", ConfigKind.Choice, "auto", "How the overlay is drawn (auto picks the first that works).", "auto", "uitoolkit", "ugui", "imgui"),
        new ConfigKey("Overlay", "Theme", ConfigKind.Text, "default", "The overlay's theme (a file in the overlay's themes folder)."),
        new ConfigKey("Overlay", "RetroFonts", ConfigKind.Bool, "true", "Use the pixel font for all overlay text (off: smooth fonts)."),
        new ConfigKey("Overlay", "Effects", ConfigKind.Choice, "on", "Animated effects in the overlay.", "on", "reduced", "off"),
        new ConfigKey("Overlay", "Gamepad", ConfigKind.Choice, "auto", "Control the overlay with a gamepad.", "auto", "off"),
        new ConfigKey("Overlay", "GamepadToggle", ConfigKind.Text, "Select+Start", "The gamepad buttons pressed together to expand or collapse the overlay."),
        new ConfigKey("Overlay", "GamepadPausesGame", ConfigKind.Bool, "false", "Pause the game while the overlay has gamepad focus."),
    };
}
