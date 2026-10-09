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

    /// <summary>The smallest value of a number setting with a range (the overlay shows it as a slider), or null.</summary>
    public double? Min { get; private set; }

    /// <summary>The largest value of a number setting with a range, or null.</summary>
    public double? Max { get; private set; }

    /// <summary>How much a number setting changes per step (its slider's steps and the Settings tab's − and +).</summary>
    public double Step { get; private set; } = 0.05;

    /// <summary>The section of the overlay's Settings tab it's listed in, or null.</summary>
    public string? Group { get; private set; }

    // The Settings tab's section for it.
    internal ConfigKey In(string group)
    {
        Group = group;
        return this;
    }

    /// <summary>Its name in the overlay's Settings tab (human-readable), or null for <see cref="Name"/>.</summary>
    public string? Title { get; private set; }

    /// <summary>What it does in more detail, for the Settings tab's tooltip, or null for <see cref="Description"/>.</summary>
    public string? Help { get; private set; }

    // How the overlay's Settings tab shows it.
    internal ConfigKey Shown(string title, string help)
    {
        Title = title;
        Help = help;
        return this;
    }

    // A number setting's range.
    internal ConfigKey Ranged(double min, double max, double step = 0.05)
    {
        Min = min;
        Max = max;
        Step = step;
        return this;
    }
}

/// <summary>
/// Every setting of the agent, in one place: the loader shim binds them all (with these defaults and descriptions) when the
/// plugin loads, so the config file always lists every setting, including ones for features that arrive later.
/// </summary>
public static class ConfigKeys
{
    /// <summary>The overlay Settings tab's sections, in the order it lists them (see <see cref="ConfigKey.Group"/>).</summary>
    public static IReadOnlyList<string> OverlayGroups { get; } = new[] { "General", "Shortcuts", "Position and Size", "Appearance", "Mouse and Keyboard", "Text Boxes", "Inspector and Time", "E-STOP", "Gamepad" };

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
        new ConfigKey("Overlay", "Enabled", ConfigKind.Bool, "true", "Show the in-game overlay.").Shown("Show Overlay", "Whether the in-game overlay exists at all. Off: no arrow, no panel, no prompts or notifications in the game (clients still work). Takes effect after the game restarts.").In("General"),
        new ConfigKey("Overlay", "ToggleKey", ConfigKind.Shortcut, "F9 + LeftControl", "Expand or collapse the overlay.").Shown("Expand/Collapse Shortcut", "The key combination that opens the overlay panel, or closes it back to the arrow at the screen edge.").In("Shortcuts"),
        new ConfigKey("Overlay", "HideKey", ConfigKind.Shortcut, "F9 + LeftControl + LeftShift", "Hide the overlay completely, or show it again.").Shown("Hide Shortcut", "The key combination that hides the overlay completely (arrow included), or brings it back. Handy for clean screenshots or recordings.").In("Shortcuts"),
        new ConfigKey("Overlay", "PickKey", ConfigKind.Shortcut, "F8 + LeftControl", "Pick an object in the game.").Shown("Pick Object Shortcut", "The key combination that starts picking: click something in the game to select it and inspect it in the Inspector tab.").In("Shortcuts"),
        new ConfigKey("Overlay", "EStopKey", ConfigKind.Shortcut, "", "E-STOP: stop all automated activity at once (no shortcut by default).").Shown("E-STOP Shortcut", "A key combination for the emergency stop, which halts everything automated at once (rules, instrumentation, scripted input). Empty means no shortcut; the E-STOP button in the panel always works.").In("Shortcuts"),
        new ConfigKey("Overlay", "StartState", ConfigKind.Choice, "Collapsed", "How the overlay starts.", "Hidden", "Collapsed", "Expanded").Shown("Start As", "How the overlay looks when the game starts: Hidden (nothing shown), Collapsed (just the arrow at the screen edge) or Expanded (the panel open).").In("General"),
        new ConfigKey("Overlay", "Edge", ConfigKind.Choice, "Right", "The screen edge the overlay is docked to.", "Left", "Right", "Top", "Bottom").Shown("Screen Edge", "The edge of the screen the overlay's arrow and panel sit on: Left, Right, Top or Bottom.").In("Position and Size"),
        new ConfigKey("Overlay", "EdgeOffset", ConfigKind.Float, "0.5", "Position along that edge, from 0 to 1.").Shown("Position Along Edge", "Where along its screen edge the overlay sits, from 0 (the start: top or left) to 1 (the end: bottom or right). 0.5 is the middle.").In("Position and Size"),
        new ConfigKey("Overlay", "Docked", ConfigKind.Bool, "true", "Keep the overlay docked to its edge.").Shown("Docked", "On: the overlay stays attached to its screen edge. Off: it can be moved freely.").In("Position and Size"),
        new ConfigKey("Overlay", "PanelSize", ConfigKind.Text, "auto", "Panel size (auto or width x height).").Shown("Panel Size", "The size of the open panel, in pixels before scaling: auto picks one from the screen size, or type it as width x height (for example 900x600).").In("Position and Size"),
        new ConfigKey("Overlay", "Scale", ConfigKind.Text, "auto", "UI scale (auto follows the screen height).").Shown("UI Scale", "How large the overlay is drawn. auto follows the screen height (1.0 at 1080 pixels); or type a number from 0.5 to 4. With pixel fonts on, it rounds to whole steps so text stays crisp.").In("Position and Size"),
        new ConfigKey("Overlay", "Opacity", ConfigKind.Float, "0.92", "Panel opacity.").Shown("Panel Opacity", "How solid the panel's background is, from 0.2 (mostly see-through) to 1 (opaque).").In("Appearance"),
        new ConfigKey("Overlay", "FontSize", ConfigKind.Int, "13", "Font size.").Shown("Font Size", "The size of the overlay's smooth text, from 8 to 48.").In("Appearance"),
        new ConfigKey("Overlay", "RefreshHz", ConfigKind.Int, "4", "How often the panel's contents refresh, per second.").Shown("Refresh Rate", "How many times per second the open tab updates its contents, from 1 to 30. Higher feels livelier but costs a little more of each frame.").In("Appearance"),
        new ConfigKey("Overlay", "Toasts", ConfigKind.Text, "Important", "Which notifications are shown next to the overlay.").Shown("Notifications", "Which notifications pop up next to the arrow: Important (warnings, errors and messages sent by a client), All, or Off. Notifications filtered out here are not kept.").In("Appearance"),
        new ConfigKey("Overlay", "BlockUiClicks", ConfigKind.Bool, "true", "Keep clicks on the overlay from reaching the game's UI.").Shown("Block Clicks to Game UI", "On: clicks on the overlay never reach the game's menus and buttons underneath it.").In("Mouse and Keyboard"),
        new ConfigKey("Overlay", "BlockWorldInput", ConfigKind.Bool, "false", "Keep input over the overlay from reaching the game world.").Shown("Block Input to Game World", "On: mouse input over the overlay is also kept from the game world (for example so a click on the panel doesn't make your character attack or move).").In("Mouse and Keyboard"),
        new ConfigKey("Overlay", "KeyboardCapture", ConfigKind.Choice, "auto", "Keep the keyboard from the game while you type into the overlay (auto adds the Windows keyboard hook to Unity's input).", "auto", "unity", "off").Shown("Keyboard Capture", "While you type into an overlay text box, keep the keystrokes from the game. unity: through Unity's input systems. auto: the same, plus on Windows a keyboard hook for games that read the keyboard their own way. off: the game sees every key you type.").In("Mouse and Keyboard"),
        new ConfigKey("Overlay", "WheelLatch", ConfigKind.Float, "0.5", "After the mouse wheel scrolls something other than a text box, how long it must rest (in seconds) before a text box under the pointer takes the wheel.").Ranged(0.1, 1).Shown("Mouse Input Latch Delay", "When you scroll the panel and a text box slides under the mouse pointer, the text box doesn't take over the mouse wheel until the wheel has rested for this long (in seconds). Shorter lets text boxes take the wheel sooner; longer keeps a scroll gesture on the panel more reliably. From 0.1 to 1 second; applies at once.").In("Text Boxes"),
        new ConfigKey("Overlay", "DragScrollStartSpeed", ConfigKind.Float, "2", "How fast a text box scrolls, in lines per second, while a drag-selection is held just past its top or bottom.").Ranged(0.25, 10, 0.25).Shown("Drag Scroll Start Speed", "While you drag-select in a text box and hold the pointer just past its top or bottom edge, the box scrolls toward the pointer at this many lines per second, selecting as it goes. Further out it speeds up toward Drag Scroll Top Speed. Applies at once.").In("Text Boxes"),
        new ConfigKey("Overlay", "DragScrollTopSpeed", ConfigKind.Float, "80", "The fastest a text box scrolls, in lines per second, while a drag-selection is held far past its top or bottom.").Ranged(30, 100, 1).Shown("Drag Scroll Top Speed", "The fastest a text box scrolls, in lines per second, while you drag-select with the pointer far past its top or bottom edge (at Drag Scroll Ramp Distance or further). Applies at once.").In("Text Boxes"),
        new ConfigKey("Overlay", "DragScrollRampDistance", ConfigKind.Float, "5", "How far past a text box's top or bottom, in line heights, a drag-selection reaches the top scrolling speed.").Ranged(0.5, 15, 0.5).Shown("Drag Scroll Ramp Distance", "How far past a text box's top or bottom edge, in line heights, the pointer must be for drag-scrolling to reach Drag Scroll Top Speed. In between, the speed rises quickly at first and then levels off (an ease-out curve). Applies at once.").In("Text Boxes"),
        new ConfigKey("Overlay", "ForceCursorWhenExpanded", ConfigKind.Bool, "true", "Show and free the mouse cursor while the overlay is expanded.").Shown("Show Cursor When Open", "On: while the panel is open, the mouse cursor is shown and free to move, even in games that hide or lock it.").In("Mouse and Keyboard"),
        new ConfigKey("Overlay", "InspectorStartsLocked", ConfigKind.Bool, "true", "The inspector starts locked (no edits until unlocked).").Shown("Inspector Starts Locked", "On: the Inspector starts locked, so values can be looked at but not changed until you unlock it.").In("Inspector and Time"),
        new ConfigKey("Overlay", "LocalTimeControl", ConfigKind.Bool, "true", "Allow pausing and stepping the game from the overlay.").Shown("Pause and Step Controls", "On: the overlay offers buttons to pause the game and advance it frame by frame.").In("Inspector and Time"),
        new ConfigKey("Overlay", "EStopPauses", ConfigKind.Bool, "false", "E-STOP also pauses the game.").Shown("E-STOP Pauses Game", "On: the emergency stop also pauses the game, besides stopping everything automated.").In("E-STOP"),
        new ConfigKey("Overlay", "EStopDisconnects", ConfigKind.Bool, "true", "E-STOP also disconnects clients.").Shown("E-STOP Disconnects Clients", "On: the emergency stop also disconnects every connected client (tools and assistants talking to the agent).").In("E-STOP"),
        new ConfigKey("Overlay", "ExcludeFromScreenshots", ConfigKind.Bool, "true", "Hide the overlay in screenshots the agent takes.").Shown("Hide in Screenshots", "On: the overlay is left out of screenshots the agent takes, so they show only the game.").In("General"),
        new ConfigKey("Overlay", "VisibleTabs", ConfigKind.Text, "all", "The overlay tabs to show (all, or a comma-separated list).").Shown("Visible Tabs", "Which tabs the panel shows: all, or a comma-separated list of status, activity, inspector, pinned, instrumentation, mods, logs, control and settings. Status, Activity and Control always show (the health view, questions and notifications, and E-STOP).").In("General"),
        new ConfigKey("Overlay", "FollowStaticToolSelection", ConfigKind.Bool, "false", "Follow selections made in a static analysis tool, when the orchestrator relays them.").Shown("Follow Static Tool Selection", "On: when a static analysis tool selects something (and the connected client passes it along), the overlay selects the same thing in the game.").In("General"),
        new ConfigKey("Overlay", "Renderer", ConfigKind.Choice, "auto", "How the overlay is drawn (auto picks the first that works).", "auto", "uitoolkit", "ugui", "imgui").Shown("Renderer", "How the overlay is drawn: auto uses the first that works in this game (UI Toolkit, then uGUI, then the plain emergency view), or choose uitoolkit, ugui or imgui.").In("General"),
        new ConfigKey("Overlay", "Theme", ConfigKind.Text, "default", "The overlay's theme (a file in the overlay's themes folder).").Shown("Theme", "The overlay's color theme: the name of a file in the overlay's themes folder (default is the built-in one).").In("Appearance"),
        new ConfigKey("Overlay", "RetroFonts", ConfigKind.Bool, "true", "Use the pixel font for all overlay text (off: smooth fonts).").Shown("Pixel Font", "On: all overlay text uses the pixel font. Off: smooth fonts.").In("Appearance"),
        new ConfigKey("Overlay", "Effects", ConfigKind.Choice, "on", "Animated effects in the overlay.", "on", "reduced", "off").Shown("Animated Effects", "The overlay's animated backgrounds and transitions: on, reduced (calmer), or off.").In("Appearance"),
        new ConfigKey("Overlay", "Gamepad", ConfigKind.Choice, "auto", "Control the overlay with a gamepad.", "auto", "off").Shown("Gamepad Control", "auto: the overlay can be used with a gamepad when one is connected. off: mouse and keyboard only.").In("Gamepad"),
        new ConfigKey("Overlay", "GamepadToggle", ConfigKind.Text, "Select+Start", "The gamepad buttons pressed together to expand or collapse the overlay.").Shown("Gamepad Shortcut", "The gamepad buttons pressed together to open or close the overlay, joined with + (for example Select+Start).").In("Gamepad"),
        new ConfigKey("Overlay", "GamepadPausesGame", ConfigKind.Bool, "false", "Pause the game while the overlay has gamepad focus.").Shown("Gamepad Pauses Game", "On: the game pauses while you are using the overlay with a gamepad.").In("Gamepad"),
    };
}
