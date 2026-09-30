using System;
using System.Collections.Generic;

namespace UnityRuntimeAnalysisAgent.Core.Abstractions;

/// <summary>Unity's time settings and clocks, read on the main thread.</summary>
public sealed class TimeFacts
{
    /// <summary><c>Time.frameCount</c>.</summary>
    public long FrameCount { get; set; }

    /// <summary><c>Time.time</c> (seconds, scaled).</summary>
    public double Time { get; set; }

    /// <summary><c>Time.unscaledTime</c> (seconds).</summary>
    public double UnscaledTime { get; set; }

    /// <summary><c>Time.realtimeSinceStartup</c> (seconds).</summary>
    public double Realtime { get; set; }

    /// <summary><c>Time.timeScale</c>.</summary>
    public double TimeScale { get; set; }

    /// <summary><c>Time.deltaTime</c> (seconds).</summary>
    public double DeltaTime { get; set; }

    /// <summary><c>Time.unscaledDeltaTime</c> (seconds).</summary>
    public double UnscaledDeltaTime { get; set; }

    /// <summary><c>Time.fixedDeltaTime</c> (seconds).</summary>
    public double FixedDeltaTime { get; set; }

    /// <summary><c>Application.targetFrameRate</c>.</summary>
    public int TargetFrameRate { get; set; }

    /// <summary><c>QualitySettings.vSyncCount</c>.</summary>
    public int VSyncCount { get; set; }
}

/// <summary>The application: identity, paths, focus, screen and graphics.</summary>
public sealed class AppFacts
{
    /// <summary><c>Application.productName</c>.</summary>
    public string ProductName { get; set; } = string.Empty;

    /// <summary><c>Application.companyName</c>.</summary>
    public string CompanyName { get; set; } = string.Empty;

    /// <summary><c>Application.version</c>.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary><c>Application.unityVersion</c>.</summary>
    public string UnityVersion { get; set; } = string.Empty;

    /// <summary><c>Application.platform</c>.</summary>
    public string Platform { get; set; } = string.Empty;

    /// <summary><c>Application.dataPath</c>.</summary>
    public string DataPath { get; set; } = string.Empty;

    /// <summary><c>Application.persistentDataPath</c> (where saves usually live).</summary>
    public string PersistentDataPath { get; set; } = string.Empty;

    /// <summary>The player log file, when Unity reports it.</summary>
    public string? ConsoleLogPath { get; set; }

    /// <summary><c>Application.isFocused</c>.</summary>
    public bool IsFocused { get; set; }

    /// <summary><c>Application.runInBackground</c>.</summary>
    public bool RunInBackground { get; set; }

    /// <summary><c>Screen.width</c>.</summary>
    public int ScreenWidth { get; set; }

    /// <summary><c>Screen.height</c>.</summary>
    public int ScreenHeight { get; set; }

    /// <summary><c>Screen.fullScreen</c>.</summary>
    public bool FullScreen { get; set; }

    /// <summary><c>QualitySettings.GetQualityLevel()</c>.</summary>
    public int QualityLevel { get; set; }

    /// <summary><c>SystemInfo.graphicsDeviceName</c>.</summary>
    public string GraphicsDevice { get; set; } = string.Empty;
}

/// <summary>A scene load or unload in progress.</summary>
public interface ISceneOperation
{
    /// <summary>Whether it has finished (main thread only).</summary>
    bool IsDone { get; }

    /// <summary>The scene it loaded, once done (main thread only).</summary>
    SceneFacts? Result { get; }

    /// <summary>Whether Unity refused to start it (e.g. unloading the only loaded scene).</summary>
    bool Refused { get; }
}

/// <summary>Time, scenes and the application (main thread only).</summary>
public interface IGameControl
{
    /// <summary>Reads the clocks.</summary>
    TimeFacts ReadTime();

    /// <summary><c>Time.timeScale</c>.</summary>
    double TimeScale { get; set; }

    /// <summary>Reads the application facts.</summary>
    AppFacts ReadApp();

    /// <summary><c>Application.runInBackground</c>.</summary>
    bool RunInBackground { get; set; }

    /// <summary><c>AudioListener.pause</c>: pauses the game's audio (sources set to ignore it keep playing).</summary>
    bool AudioPaused { get; set; }

    /// <summary><c>Application.Quit(exitCode)</c>.</summary>
    void Quit(int exitCode);

    /// <summary>Whether a scene with this name or path is in the build and can be loaded.</summary>
    bool CanLoadScene(string name);

    /// <summary>Starts loading a scene by name or build index (one of them).</summary>
    ISceneOperation LoadScene(string? name, int? buildIndex, bool additive, bool async);

    /// <summary>Starts unloading a loaded scene (by handle).</summary>
    ISceneOperation UnloadScene(int handle);

    /// <summary>Makes a loaded scene the active one; false if Unity refused.</summary>
    bool SetActiveScene(int handle);
}

/// <summary>A uGUI element (a GameObject with a Selectable, ScrollRect, text or image component), read on the main thread.</summary>
public sealed class UiElementFacts
{
    /// <summary>Creates it.</summary>
    public UiElementFacts(object gameObject) => GameObject = gameObject;

    /// <summary>The element's GameObject.</summary>
    public object GameObject { get; }

    /// <summary><c>button</c>, <c>toggle</c>, <c>slider</c>, <c>dropdown</c>, <c>inputField</c>, <c>scrollRect</c>, <c>text</c>,
    /// <c>image</c> or <c>other</c>.</summary>
    public string Kind { get; set; } = "other";

    /// <summary>Its text (its own, or its first child text for controls), rich-text markup removed.</summary>
    public string? Text { get; set; }

    /// <summary>The text with its rich-text markup, when it had any (else null).</summary>
    public string? RawText { get; set; }

    /// <summary>Sprite and texture names of the element's images (its own and its children's).</summary>
    public List<(string? Sprite, string? Texture)> Images { get; } = new();

    /// <summary>Whether it can be used now: an interactable Selectable, or another element whose pointer handlers can be
    /// reached (<see cref="Interaction"/> is <c>clickable</c>).</summary>
    public bool Interactable { get; set; }

    /// <summary><c>clickable</c>, <c>disabled</c>, <c>hover</c> (only pointer enter/exit handlers) or <c>display</c>.</summary>
    public string Interaction { get; set; } = "display";

    /// <summary><c>visible</c>, <c>partial</c>, <c>clipped</c> (hidden by a mask or scroll view), <c>offscreen</c> or
    /// <c>hidden</c> (inactive, faded out by a CanvasGroup, or without size).</summary>
    public string Visibility { get; set; } = "visible";

    /// <summary>The enclosing ScrollRect's GameObject, or null.</summary>
    public object? ScrollContainer { get; set; }

    /// <summary>Whether it's the EventSystem's selected object.</summary>
    public bool Selected { get; set; }

    /// <summary>Keyboard/gamepad navigation of a Selectable (null for other elements).</summary>
    public UiNavigationFacts? Navigation { get; set; }

    /// <summary>A toggle's state.</summary>
    public bool? IsOn { get; set; }

    /// <summary>Slider/scrollbar value (double), dropdown index (int) or input text (string).</summary>
    public object? Value { get; set; }

    /// <summary>A dropdown's option texts.</summary>
    public List<string>? Options { get; set; }

    /// <summary>Its rectangle on screen in pixels, origin top left.</summary>
    public (double X, double Y, double W, double H) ScreenRect { get; set; }

    /// <summary>Whether any of it can be seen (<see cref="Visibility"/> is <c>visible</c> or <c>partial</c>). Setting it
    /// sets <see cref="Visibility"/> to <c>visible</c> or <c>offscreen</c>.</summary>
    public bool Visible
    {
        get => Visibility is "visible" or "partial";
        set => Visibility = value ? "visible" : "offscreen";
    }

    /// <summary>The part that can be seen (clipped by the screen, masks and scroll views); null when nothing can be seen,
    /// or when it wasn't measured (then the whole <see cref="ScreenRect"/> counts).</summary>
    public (double X, double Y, double W, double H)? VisibleRect { get; set; }

    /// <summary>Its canvas is drawn into a render texture (shown on an in-world screen or a processed surface):
    /// <see cref="ScreenRect"/> and <see cref="VisibleRect"/> are in that texture's pixels, not the screen's.</summary>
    public bool DrawnToTexture { get; set; }

    /// <summary>Its root canvas's name.</summary>
    public string Canvas { get; set; } = string.Empty;

    /// <summary>Its root canvas's sorting order.</summary>
    public int SortingOrder { get; set; }

    /// <summary>Whether something else is hit first at its centre (it can't be clicked).</summary>
    public bool RaycastBlocked { get; set; }
}

/// <summary>A Selectable's navigation: its mode and the GameObjects it moves to (null where it goes nowhere).</summary>
public sealed class UiNavigationFacts
{
    /// <summary><c>none</c>, <c>horizontal</c>, <c>vertical</c>, <c>automatic</c> or <c>explicit</c>.</summary>
    public string Mode { get; set; } = "none";

    public object? Up { get; set; }

    public object? Down { get; set; }

    public object? Left { get; set; }

    public object? Right { get; set; }
}

/// <summary>What <c>ui.scrollTo</c> did.</summary>
public readonly struct UiScrollOutcome
{
    /// <summary>Creates it.</summary>
    public UiScrollOutcome(bool scrolled, object? container)
    {
        Scrolled = scrolled;
        Container = container;
    }

    /// <summary>Whether any scroll view moved.</summary>
    public bool Scrolled { get; }

    /// <summary>The innermost scroll view's GameObject, or null when the element isn't in one.</summary>
    public object? Container { get; }
}

/// <summary>What <c>ui.navigate</c> did.</summary>
public readonly struct UiNavigateOutcome
{
    /// <summary>Creates it.</summary>
    public UiNavigateOutcome(bool moved, object? selected)
    {
        Moved = moved;
        Selected = selected;
    }

    /// <summary>Whether the selection changed.</summary>
    public bool Moved { get; }

    /// <summary>The selected GameObject afterwards, or null.</summary>
    public object? Selected { get; }
}

/// <summary>The UI frameworks and input handling of the running game (<c>ui.frameworks</c>).</summary>
public sealed class UiFrameworksFacts
{
    public ModuleStatus Ugui { get; set; } = new(false, null, null);

    /// <summary>Active root canvases by render mode.</summary>
    public (int Overlay, int Camera, int World) Canvases { get; set; }

    public bool EventSystemPresent { get; set; }

    /// <summary>The active input module's type name.</summary>
    public string? InputModule { get; set; }

    public ModuleStatus UiToolkit { get; set; } = new(false, null, null);

    /// <summary>Runtime UI Toolkit (UIDocument) exists in this Unity version.</summary>
    public bool UiToolkitRuntime { get; set; }

    public int UiDocuments { get; set; }

    /// <summary>Panels in use: the PanelSettings asset's name, its sort order, and the active documents on it.</summary>
    public List<(string Name, double SortOrder, int Documents)> Panels { get; } = new();

    public ModuleStatus Tmp { get; set; } = new(false, null, null);

    public bool ImguiAvailable { get; set; }

    /// <summary>Active behaviours of the game with an <c>OnGUI</c> method (the agent's own excluded).</summary>
    public int ImguiBehaviours { get; set; }

    /// <summary><c>inputManager</c>, <c>inputSystem</c>, <c>both</c> or <c>unknown</c>.</summary>
    public string InputHandling { get; set; } = "unknown";

    public string? InputSystemVersion { get; set; }

    public int Gamepads { get; set; }
}

/// <summary>What a UI action did: whether something handled it, and how (<c>eventSystem</c>, <c>onClick</c>, <c>setter</c>, …).</summary>
public readonly struct UiActionOutcome
{
    /// <summary>Creates it.</summary>
    public UiActionOutcome(bool handled, string via)
    {
        Handled = handled;
        Via = via;
    }

    /// <summary>Whether something handled the action.</summary>
    public bool Handled { get; }

    /// <summary>How it was done.</summary>
    public string Via { get; }

    /// <summary>Deconstructs it.</summary>
    public void Deconstruct(out bool handled, out string via)
    {
        handled = Handled;
        via = Via;
    }
}

/// <summary>
/// uGUI and TextMeshPro, bound by reflection (main thread only). Objects under an <c>AgentOwned</c> marker (the agent's
/// own UI) are never listed or driven.
/// </summary>
public interface IUiApi
{
    /// <summary>Whether uGUI (<c>UnityEngine.UI</c>) is loaded and bound (<c>module:ugui</c>).</summary>
    ModuleStatus UguiStatus { get; }

    /// <summary>Whether TextMeshPro is loaded and bound (<c>module:tmp</c>).</summary>
    ModuleStatus TmpStatus { get; }

    /// <summary>The elements on active canvases, in hierarchy order.</summary>
    IReadOnlyList<UiElementFacts> Snapshot(bool onlyInteractable, bool onlyVisible, bool includeText, int limit);

    /// <summary>Whether a GameObject or component belongs to the agent's own UI.</summary>
    bool IsAgentOwned(object gameObjectOrComponent);

    /// <summary>A pointer click (enter, down, up, click) through the EventSystem; <c>onClick</c> without one.</summary>
    UiActionOutcome Click(object target);

    /// <summary>Sets an input field's text (its change event fires); <paramref name="submit"/> also ends the edit and
    /// submits.</summary>
    UiActionOutcome SetText(object target, string text, bool submit);

    /// <summary>Sets a toggle (bool), slider or scrollbar (number), dropdown (index) or input field (text; null clears it)
    /// value; its change event fires.</summary>
    UiActionOutcome SetValue(object target, object? value);

    /// <summary>Sends Submit to the selected object.</summary>
    UiActionOutcome Submit();

    /// <summary>Sends Cancel to the selected object.</summary>
    UiActionOutcome Cancel();

    /// <summary>Selects an object in the EventSystem.</summary>
    UiActionOutcome Select(object target);

    /// <summary>One element, described as <see cref="Snapshot"/> would (null if it isn't a UI element).</summary>
    UiElementFacts? Describe(object target);

    /// <summary>Pointer enter (or, with <paramref name="leave"/>, exit) through the EventSystem.</summary>
    UiActionOutcome Hover(object target, bool leave);

    /// <summary>Scrolls the target's scroll views (innermost first) until it is visible.</summary>
    UiScrollOutcome ScrollTo(object target);

    /// <summary>A navigation move (<c>up</c>, <c>down</c>, <c>left</c>, <c>right</c>) from the selected object.</summary>
    UiNavigateOutcome Navigate(string direction);

    /// <summary>The UI frameworks the game can use and uses, and its input handling. Works without uGUI.</summary>
    UiFrameworksFacts Frameworks();
}
