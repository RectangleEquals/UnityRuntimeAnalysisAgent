using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace UnityRuntimeAnalysisAgent.Core.Overlay;

/// <summary>A rectangle in screen pixels, origin at the top left.</summary>
public readonly struct OverlayRect : IEquatable<OverlayRect>
{
    /// <summary>Creates the rectangle.</summary>
    public OverlayRect(double x, double y, double width, double height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    /// <summary>Left.</summary>
    public double X { get; }

    /// <summary>Top.</summary>
    public double Y { get; }

    /// <summary>Width.</summary>
    public double Width { get; }

    /// <summary>Height.</summary>
    public double Height { get; }

    /// <summary>Whether the point is inside.</summary>
    public bool Contains(double x, double y) => x >= X && y >= Y && x < X + Width && y < Y + Height;

    /// <inheritdoc/>
    public bool Equals(OverlayRect other) => X.Equals(other.X) && Y.Equals(other.Y) && Width.Equals(other.Width) && Height.Equals(other.Height);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is OverlayRect other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => (X, Y, Width, Height).GetHashCode();

    /// <inheritdoc/>
    public override string ToString() => string.Format(CultureInfo.InvariantCulture, "{0},{1} {2}x{3}", X, Y, Width, Height);
}

/// <summary>The arrow's colour: what the agent is doing.</summary>
public enum ArrowStatus
{
    /// <summary>No client connected.</summary>
    Idle,

    /// <summary>A client is connected and nothing is happening.</summary>
    Connected,

    /// <summary>Recent activity, or a prompt is waiting for the user.</summary>
    Busy,

    /// <summary>E-STOP engaged, or an error needs attention.</summary>
    Alert,
}

/// <summary>Edge docking geometry, shared by every renderer.</summary>
public static class EdgeDock
{
    /// <summary>The arrow's rotation in degrees, clockwise, from a dart pointing right: it points into the screen.</summary>
    public static double ArrowAngle(OverlayEdge edge) => edge switch
    {
        OverlayEdge.Left => 0,
        OverlayEdge.Top => 90,
        OverlayEdge.Right => 180,
        _ => 270,
    };

    /// <summary>The arrow's rectangle: against its edge, centred at the offset along it, kept on screen.</summary>
    public static OverlayRect Arrow(OverlayEdge edge, double offset, double size, double screenWidth, double screenHeight)
    {
        var horizontal = edge is OverlayEdge.Top or OverlayEdge.Bottom;
        var length = horizontal ? screenWidth : screenHeight;
        var along = Clamp(offset * length - size / 2, 0, Math.Max(0, length - size));
        return edge switch
        {
            OverlayEdge.Left => new OverlayRect(0, along, size, size),
            OverlayEdge.Right => new OverlayRect(screenWidth - size, along, size, size),
            OverlayEdge.Top => new OverlayRect(along, 0, size, size),
            _ => new OverlayRect(along, screenHeight - size, size, size),
        };
    }

    /// <summary>
    /// The docked panel's rectangle: <paramref name="inset"/> in from its edge (beside the arrow, which stays visible),
    /// centred at the offset along it, the size capped to the screen and kept on screen.
    /// </summary>
    public static OverlayRect Panel(OverlayEdge edge, double offset, double width, double height, double screenWidth, double screenHeight, double inset = 0)
    {
        var sideEdge = edge is OverlayEdge.Left or OverlayEdge.Right;
        inset = Clamp(inset, 0, (sideEdge ? screenWidth : screenHeight) / 2);
        width = Math.Min(width, screenWidth - (sideEdge ? inset : 0));
        height = Math.Min(height, screenHeight - (sideEdge ? 0 : inset));
        var horizontal = edge is OverlayEdge.Top or OverlayEdge.Bottom;
        var along = horizontal
            ? Clamp(offset * screenWidth - width / 2, 0, screenWidth - width)
            : Clamp(offset * screenHeight - height / 2, 0, screenHeight - height);
        return edge switch
        {
            OverlayEdge.Left => new OverlayRect(inset, along, width, height),
            OverlayEdge.Right => new OverlayRect(screenWidth - inset - width, along, width, height),
            OverlayEdge.Top => new OverlayRect(along, inset, width, height),
            _ => new OverlayRect(along, screenHeight - inset - height, width, height),
        };
    }

    /// <summary>The nearest edge to a point and the offset along it (where a dropped arrow docks).</summary>
    public static (OverlayEdge Edge, double Offset) Snap(double x, double y, double screenWidth, double screenHeight)
    {
        x = Clamp(x, 0, screenWidth);
        y = Clamp(y, 0, screenHeight);
        var distances = new[]
        {
            (Edge: OverlayEdge.Left, Distance: x),
            (Edge: OverlayEdge.Right, Distance: screenWidth - x),
            (Edge: OverlayEdge.Top, Distance: y),
            (Edge: OverlayEdge.Bottom, Distance: screenHeight - y),
        };
        var edge = distances.OrderBy(d => d.Distance).First().Edge;
        return (edge, Along(edge, x, y, screenWidth, screenHeight));
    }

    /// <summary>The offset along an edge for a point (dragging along the current edge).</summary>
    public static double Along(OverlayEdge edge, double x, double y, double screenWidth, double screenHeight) =>
        edge is OverlayEdge.Top or OverlayEdge.Bottom
            ? Clamp(screenWidth <= 0 ? 0.5 : x / screenWidth, 0, 1)
            : Clamp(screenHeight <= 0 ? 0.5 : y / screenHeight, 0, 1);

    private static double Clamp(double value, double min, double max) => value < min ? min : value > max ? max : value;
}

/// <summary>
/// The overlay's state: Hidden / Collapsed / Expanded, the docking edge and offset, docked or floating, the
/// selected tab and pick mode. Renderer-independent; every change raises <see cref="Changed"/>, and what the user
/// arranges (edge, offset, docking, tab) is written back through the config writer.
/// </summary>
public sealed class OverlayModel
{
    private readonly OverlaySettings _settings;
    private readonly IConfigWriter? _writer;
    private OverlayVisibility _beforeHide;
    private bool _dragging;

    /// <summary>Creates the model in the configured start state.</summary>
    public OverlayModel(OverlaySettings settings, IConfigWriter? writer = null)
    {
        _settings = settings;
        _writer = writer;
        State = settings.StartState;
        _beforeHide = settings.StartState == OverlayVisibility.Hidden ? OverlayVisibility.Collapsed : settings.StartState;
        Edge = settings.Edge;
        Offset = settings.EdgeOffset;
        Docked = settings.Docked;
        Tab = settings.VisibleTabs.FirstOrDefault() ?? "status";
    }

    /// <summary>Raised after any change (renderers redraw; nothing heavy runs here).</summary>
    public event Action<OverlayModel>? Changed;

    /// <summary>The settings in effect.</summary>
    public OverlaySettings Settings => _settings;

    /// <summary>The current state.</summary>
    public OverlayVisibility State { get; private set; }

    /// <summary>The docking edge.</summary>
    public OverlayEdge Edge { get; private set; }

    /// <summary>Position along the edge, 0–1.</summary>
    public double Offset { get; private set; }

    /// <summary>Docked (false: floating at <see cref="FloatingPosition"/>).</summary>
    public bool Docked { get; private set; }

    /// <summary>The floating panel's top-left corner, when not docked.</summary>
    public (double X, double Y) FloatingPosition { get; private set; } = (80, 80);

    /// <summary>The selected tab.</summary>
    public string Tab { get; private set; }

    /// <summary>Pick mode (a crosshair picks an object).</summary>
    public bool PickMode { get; private set; }

    /// <summary>Whether the arrow is being dragged.</summary>
    public bool Dragging => _dragging;

    /// <summary>The toggle hotkey or the arrow: Expanded ↔ Collapsed (from Hidden, straight to Expanded).</summary>
    public void Toggle() => Set(State == OverlayVisibility.Expanded ? OverlayVisibility.Collapsed : OverlayVisibility.Expanded);

    /// <summary>The hide hotkey: Hidden ↔ the state before.</summary>
    public void Hide()
    {
        if (State == OverlayVisibility.Hidden)
        {
            Set(_beforeHide);
            return;
        }

        _beforeHide = State;
        Set(OverlayVisibility.Hidden);
    }

    /// <summary>Sets the state (and the tab when given; unknown or hidden tabs are refused).</summary>
    public void SetState(OverlayVisibility state, string? tab = null)
    {
        if (tab is not null)
        {
            SelectTab(tab);
        }

        if (state == OverlayVisibility.Hidden && State != OverlayVisibility.Hidden)
        {
            _beforeHide = State;
        }

        Set(state);
    }

    /// <summary>Selects a visible tab.</summary>
    /// <exception cref="ArgumentException">The tab doesn't exist or isn't visible.</exception>
    public void SelectTab(string tab)
    {
        var name = tab.Trim().ToLowerInvariant();
        if (!_settings.VisibleTabs.Contains(name))
        {
            throw new ArgumentException($"There is no visible tab '{tab}' (visible: {string.Join(", ", _settings.VisibleTabs)}).", nameof(tab));
        }

        if (Tab == name)
        {
            return;
        }

        Tab = name;
        Raise();
    }

    /// <summary>Starts or ends pick mode.</summary>
    public void SetPickMode(bool on)
    {
        if (PickMode == on)
        {
            return;
        }

        PickMode = on;
        Raise();
    }

    /// <summary>Docks the panel to its edge, or lets it float at a position.</summary>
    public void SetDocked(bool docked, (double X, double Y)? floatingAt = null)
    {
        Docked = docked;
        if (floatingAt is { } at)
        {
            FloatingPosition = at;
        }

        Persist("Docked", docked ? "true" : "false");
        Raise();
    }

    /// <summary>The arrow is pressed and moved: it follows the pointer along its edge.</summary>
    public void Drag(double x, double y, double screenWidth, double screenHeight)
    {
        _dragging = true;
        Offset = EdgeDock.Along(Edge, x, y, screenWidth, screenHeight);
        Raise();
    }

    /// <summary>The arrow is dropped: it docks to the nearest edge; the position is saved.</summary>
    public void Drop(double x, double y, double screenWidth, double screenHeight)
    {
        _dragging = false;
        (Edge, Offset) = EdgeDock.Snap(x, y, screenWidth, screenHeight);
        Persist("Edge", Edge.ToString());
        Persist("EdgeOffset", Offset.ToString("0.###", CultureInfo.InvariantCulture));
        Raise();
    }

    /// <summary>The arrow's colour from what's going on.</summary>
    public static ArrowStatus Status(int clients, bool recentActivity, int pendingPrompts, bool estopEngaged, bool error) =>
        estopEngaged || error ? ArrowStatus.Alert
        : pendingPrompts > 0 || (clients > 0 && recentActivity) ? ArrowStatus.Busy
        : clients > 0 ? ArrowStatus.Connected
        : ArrowStatus.Idle;

    /// <summary>The protocol's name for a state.</summary>
    public static string Name(OverlayVisibility state) => state.ToString().ToLowerInvariant();

    /// <summary>The protocol's name for an edge.</summary>
    public static string Name(OverlayEdge edge) => edge.ToString().ToLowerInvariant();

    private void Set(OverlayVisibility state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        Raise();
    }

    private void Persist(string name, string value)
    {
        _writer?.Set("Overlay." + name, value);
    }

    private void Raise() => Changed?.Invoke(this);
}
