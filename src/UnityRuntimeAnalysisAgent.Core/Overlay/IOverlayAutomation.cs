using System.Collections.Generic;
using UnityLudometry.Protocol.Json;

namespace UnityRuntimeAnalysisAgent.Core.Overlay;

/// <summary>
/// What the drawn overlay offers clients that drive it (<c>overlay.snapshot</c>, <c>overlay.reveal</c>,
/// <c>overlay.invoke</c>): its elements as the user sees them, scrolling one into view, outlining it, and operating it
/// as a click would. The renderer in use provides it; without one there's nothing to drive.
/// </summary>
public interface IOverlayAutomation
{
    /// <summary>The overlay's elements now: the open tab's content, the header, the cards next to the arrow, the arrow.</summary>
    IReadOnlyList<OverlayElementState> Elements();

    /// <summary>Scrolls an element's list or scroll view until it's in view. False when it isn't in the overlay now.</summary>
    bool ScrollIntoView(string id);

    /// <summary>Outlines an element for a while (follows it if it moves).</summary>
    void Outline(string id, double seconds);

    /// <summary>
    /// Operates an element as a click would: its command runs (for a toggle, slider or dropdown, with
    /// <paramref name="value"/>). Returns the command that ran.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No element has that id.</exception>
    /// <exception cref="System.InvalidOperationException">It does nothing when clicked.</exception>
    string Invoke(string id, JsonValue? value);
}

/// <summary>One overlay element as <c>overlay.snapshot</c> reports it.</summary>
public sealed class OverlayElementState
{
    /// <summary>Its id: its place in its view, list rows named by their item's key or id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The containing element's id, or null at the top.</summary>
    public string? Parent { get; set; }

    /// <summary><c>panel</c>, <c>header</c>, <c>cards</c> or <c>arrow</c>.</summary>
    public string Area { get; set; } = "panel";

    /// <summary>The view node type (<c>button</c>, <c>text</c>, <c>textBox</c>, …).</summary>
    public string Type { get; set; } = "stack";

    /// <summary>The text it shows.</summary>
    public string? Text { get; set; }

    /// <summary>Its bound value, or null.</summary>
    public JsonValue? Value { get; set; }

    /// <summary>The command a press runs, or null.</summary>
    public string? Command { get; set; }

    /// <summary>Its tooltip, or null.</summary>
    public string? Tooltip { get; set; }

    /// <summary><c>clickable</c>, <c>editable</c>, <c>scrollable</c> or <c>display</c>.</summary>
    public string Interaction { get; set; } = "display";

    /// <summary>Whether it can be used now.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>A text box with the keyboard.</summary>
    public bool Focused { get; set; }

    /// <summary><c>visible</c>, <c>partial</c>, <c>clipped</c>, <c>offscreen</c> or <c>hidden</c>.</summary>
    public string Visibility { get; set; } = "hidden";

    /// <summary>Where it is drawn, in screen pixels from the top-left, or null when it isn't.</summary>
    public (double X, double Y, double W, double H)? Rect { get; set; }

    /// <summary>The text box it is, for typing into (null for other elements).</summary>
    public TextBox? Box { get; set; }
}
