using System;
using System.Collections.Generic;
using System.Linq;
using UnityLudometry.Protocol;
using UnityRuntimeAnalysisAgent.Core.Dispatch;

namespace UnityRuntimeAnalysisAgent.Core.Overlay;

/// <summary>What the Inspector shows: one selected object.</summary>
public sealed class SelectionEntry
{
    /// <summary>Creates an entry.</summary>
    public SelectionEntry(object target, string label, string? locator, string source)
    {
        Target = target;
        Label = label;
        Locator = locator;
        Source = source;
    }

    /// <summary>The object.</summary>
    public object Target { get; }

    /// <summary>A short label (name and type).</summary>
    public string Label { get; }

    /// <summary>Its locator, when it has one.</summary>
    public string? Locator { get; }

    /// <summary>How it was selected: <c>pick</c>, <c>tree</c>, <c>pin</c>, <c>link</c> or <c>client</c>.</summary>
    public string Source { get; }

    /// <summary>The object was destroyed: the Inspector keeps its last data, greyed out.</summary>
    public bool Destroyed { get; internal set; }
}

/// <summary>
/// The current selection with back/forward history (the last <see cref="HistorySize"/>). Destroyed objects stay
/// selected, marked, until something else is selected.
/// </summary>
public sealed class SelectionModel
{
    /// <summary>How many selections the history keeps.</summary>
    public const int HistorySize = 50;

    private readonly List<SelectionEntry> _history = new();
    private int _index = -1;

    /// <summary>Raised when the current selection changes (or is marked destroyed).</summary>
    public event Action<SelectionEntry?>? Changed;

    /// <summary>The current selection, if any.</summary>
    public SelectionEntry? Current => _index >= 0 ? _history[_index] : null;

    /// <summary>Whether <see cref="Back"/> can go further.</summary>
    public bool CanGoBack => _index > 0;

    /// <summary>Whether <see cref="Forward"/> can go further.</summary>
    public bool CanGoForward => _index >= 0 && _index < _history.Count - 1;

    /// <summary>The history, oldest first.</summary>
    public IReadOnlyList<SelectionEntry> History => _history;

    /// <summary>Selects an object; anything after the current point in the history is dropped.</summary>
    public void Select(SelectionEntry entry)
    {
        if (Current is { } current && ReferenceEquals(current.Target, entry.Target))
        {
            return;
        }

        if (_index < _history.Count - 1)
        {
            _history.RemoveRange(_index + 1, _history.Count - _index - 1);
        }

        _history.Add(entry);
        if (_history.Count > HistorySize)
        {
            _history.RemoveAt(0);
        }

        _index = _history.Count - 1;
        Changed?.Invoke(Current);
    }

    /// <summary>Goes back one selection.</summary>
    public bool Back() => Move(-1);

    /// <summary>Goes forward one selection.</summary>
    public bool Forward() => Move(1);

    /// <summary>Marks destroyed objects (checked on refresh and after scene changes).</summary>
    public void Refresh(Func<object, bool> alive)
    {
        foreach (var entry in _history.Where(e => !e.Destroyed && !alive(e.Target)))
        {
            entry.Destroyed = true;
            if (ReferenceEquals(entry, Current))
            {
                Changed?.Invoke(entry);
            }
        }
    }

    private bool Move(int step)
    {
        var next = _index + step;
        if (next < 0 || next >= _history.Count)
        {
            return false;
        }

        _index = next;
        Changed?.Invoke(Current);
        return true;
    }
}

/// <summary>Names for picked objects: <c>pick1</c>, <c>pick2</c>… skipping names already taken.</summary>
public sealed class PickNames
{
    private int _next = 1;

    /// <summary>The next free default name.</summary>
    public string Next(Func<string, bool> taken)
    {
        string name;
        do
        {
            name = "pick" + _next++;
        }
        while (taken(name));

        return name;
    }
}

/// <summary>
/// The Inspector lock: locked by default and after every start; it opens only in Full mode, and lowering the
/// mode (E-STOP) locks it again.
/// </summary>
public sealed class InspectorLock
{
    private readonly ModeController _modes;

    /// <summary>Creates the lock.</summary>
    public InspectorLock(ModeController modes, bool startsLocked)
    {
        _modes = modes;
        Locked = startsLocked || modes.Current != AgentMode.Full;
    }

    /// <summary>Whether edits and actions are hidden.</summary>
    public bool Locked { get; private set; }

    /// <summary>Whether the Inspector may edit now (unlocked and still in Full mode).</summary>
    public bool AllowsEdits => !Locked && _modes.Current == AgentMode.Full;

    /// <summary>Why the lock can't open, or null when it can.</summary>
    public string? WhyLocked => _modes.Current == AgentMode.Full ? null : $"Editing needs Full mode (the agent is in {AgentModes.ToWire(_modes.Current)}).";

    /// <summary>Opens the lock. Returns false (and stays locked) outside Full mode.</summary>
    public bool Unlock()
    {
        if (_modes.Current != AgentMode.Full)
        {
            return false;
        }

        Locked = false;
        return true;
    }

    /// <summary>Closes the lock.</summary>
    public void Lock() => Locked = true;
}
