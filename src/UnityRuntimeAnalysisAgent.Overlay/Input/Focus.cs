using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityRuntimeAnalysisAgent.Overlay.Input;

/// <summary>A direction for keyboard and gamepad navigation.</summary>
public enum NavDirection
{
    /// <summary>Up.</summary>
    Up,

    /// <summary>Down.</summary>
    Down,

    /// <summary>Left.</summary>
    Left,

    /// <summary>Right.</summary>
    Right,
}

/// <summary>Something that can take focus, with its rectangle on screen (top-left origin).</summary>
public sealed class Focusable
{
    /// <summary>Creates an entry.</summary>
    public Focusable(string id, double x, double y, double width, double height)
    {
        Id = id;
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    /// <summary>Its id (the view node's id or path).</summary>
    public string Id { get; }

    /// <summary>Left.</summary>
    public double X { get; }

    /// <summary>Top.</summary>
    public double Y { get; }

    /// <summary>Width.</summary>
    public double Width { get; }

    /// <summary>Height.</summary>
    public double Height { get; }

    internal double CenterX => X + Width / 2;

    internal double CenterY => Y + Height / 2;
}

/// <summary>
/// The overlay's own focus model (renderer-independent, so gamepad navigation doesn't depend on the game's EventSystem):
/// directional moves pick the nearest focusable in that direction (distance along it, with sideways offset weighing
/// more), Tab order is the order given (the view's document order), and the focus survives rebuilds by id.
/// </summary>
public sealed class FocusModel
{
    private List<Focusable> _items = new();

    /// <summary>Raised when the focus moves (with the new id, or null).</summary>
    public event Action<string?>? Changed;

    /// <summary>The focused id, if any.</summary>
    public string? Current { get; private set; }

    /// <summary>The focusables, in Tab order.</summary>
    public IReadOnlyList<Focusable> Items => _items;

    /// <summary>Replaces the focusables (after a layout); the focus stays on the same id when it still exists.</summary>
    public void Update(IEnumerable<Focusable> items)
    {
        _items = items.ToList();
        if (Current is not null && _items.All(i => i.Id != Current))
        {
            Set(_items.FirstOrDefault()?.Id);
        }
    }

    /// <summary>Focuses an id (null clears the focus). Returns false for an unknown id.</summary>
    public bool Focus(string? id)
    {
        if (id is not null && _items.All(i => i.Id != id))
        {
            return false;
        }

        Set(id);
        return true;
    }

    /// <summary>Moves in a direction. Without a focus, focuses the first item. Returns whether the focus moved.</summary>
    public bool Move(NavDirection direction)
    {
        var from = _items.FirstOrDefault(i => i.Id == Current);
        if (from is null)
        {
            return _items.Count > 0 && Focus(_items[0].Id);
        }

        Focusable? best = null;
        var bestScore = double.MaxValue;
        foreach (var candidate in _items.Where(i => i != from))
        {
            var dx = candidate.CenterX - from.CenterX;
            var dy = candidate.CenterY - from.CenterY;
            var (along, across) = direction switch
            {
                NavDirection.Up => (-dy, Math.Abs(dx)),
                NavDirection.Down => (dy, Math.Abs(dx)),
                NavDirection.Left => (-dx, Math.Abs(dy)),
                _ => (dx, Math.Abs(dy)),
            };
            if (along <= 0.5)
            {
                continue;
            }

            // Overlapping in the other axis counts as straight ahead.
            var overlaps = direction is NavDirection.Up or NavDirection.Down
                ? candidate.X < from.X + from.Width && from.X < candidate.X + candidate.Width
                : candidate.Y < from.Y + from.Height && from.Y < candidate.Y + candidate.Height;
            var score = along + (overlaps ? 0 : across * 2);
            if (score < bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        if (best is null)
        {
            return false;
        }

        Set(best.Id);
        return true;
    }

    /// <summary>The next (or previous) item in Tab order, wrapping around.</summary>
    public bool Next(bool backwards = false)
    {
        if (_items.Count == 0)
        {
            return false;
        }

        var index = _items.FindIndex(i => i.Id == Current);
        var next = index < 0 ? (backwards ? _items.Count - 1 : 0) : (index + (backwards ? -1 : 1) + _items.Count) % _items.Count;
        Set(_items[next].Id);
        return true;
    }

    private void Set(string? id)
    {
        if (Current == id)
        {
            return;
        }

        Current = id;
        Changed?.Invoke(id);
    }
}

/// <summary>A button chord (e.g. Select + Start): fires once on the frame all its buttons are down together.</summary>
public sealed class ChordDetector
{
    private readonly HashSet<string> _buttons;
    private bool _wasDown;

    /// <summary>Creates the detector for these buttons (names as the gamepad reader reports them, case-insensitive).</summary>
    public ChordDetector(IEnumerable<string> buttons)
    {
        _buttons = new HashSet<string>(buttons, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Feeds the buttons held this frame; true on the frame the chord completes.</summary>
    public bool Update(IEnumerable<string> held)
    {
        var down = _buttons.Count > 0 && _buttons.IsSubsetOf(new HashSet<string>(held, StringComparer.OrdinalIgnoreCase));
        var fired = down && !_wasDown;
        _wasDown = down;
        return fired;
    }
}
