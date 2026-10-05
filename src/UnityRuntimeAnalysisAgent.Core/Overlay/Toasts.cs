using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityRuntimeAnalysisAgent.Core.Overlay;

/// <summary>A notification's level (the protocol's <c>overlay.notify</c> levels).</summary>
public enum ToastLevel
{
    /// <summary>Information.</summary>
    Info,

    /// <summary>Something finished well.</summary>
    Success,

    /// <summary>Something needs attention.</summary>
    Warning,

    /// <summary>Something failed.</summary>
    Error,
}

/// <summary>One notification shown next to the arrow.</summary>
public sealed class Toast
{
    internal Toast(long id, string text, ToastLevel level, string source, double shownAt, double durationSeconds)
    {
        Id = id;
        Text = text;
        Level = level;
        Source = source;
        ShownAt = shownAt;
        DurationSeconds = durationSeconds;
    }

    /// <summary>Its id.</summary>
    public long Id { get; }

    /// <summary>The text.</summary>
    public string Text { get; }

    /// <summary>The level.</summary>
    public ToastLevel Level { get; }

    /// <summary>Who raised it (<c>client</c>, <c>agent</c>, <c>rule</c>, <c>test</c>, <c>job</c>).</summary>
    public string Source { get; }

    /// <summary>When it was shown (unscaled realtime, seconds).</summary>
    public double ShownAt { get; private set; }

    /// <summary>How long it stays.</summary>
    public double DurationSeconds { get; }

    /// <summary>How many identical notifications it stands for.</summary>
    public int Count { get; private set; } = 1;

    internal void Repeat(double now)
    {
        Count++;
        ShownAt = now;
    }
}

/// <summary>
/// The toast queue: filtered by <c>Overlay.Toasts</c> (off / important / all; important = warnings, errors
/// and the client's own notifications), identical toasts coalesced, and throttled: at most
/// <see cref="MaxPerWindow"/> ordinary toasts per <see cref="WindowSeconds"/>, the rest counted and summarised.
/// Timing is unscaled realtime, so toasts behave the same while the game is paused.
/// </summary>
public sealed class ToastQueue
{
    /// <summary>The most toasts shown at once.</summary>
    public const int MaxVisible = 5;

    /// <summary>How many notifications the history keeps (the expanded overlay lists them).</summary>
    public const int HistorySize = 200;

    /// <summary>The throttle window.</summary>
    public const double WindowSeconds = 10;

    /// <summary>Ordinary toasts allowed per window.</summary>
    public const int MaxPerWindow = 8;

    /// <summary>Reading speed for a toast's minimum time on screen (slow on purpose: streamed or remote screens).</summary>
    public const double ReadingCharsPerSecond = 12;

    /// <summary>Time added to every toast before its reading time (to notice it and look over).</summary>
    public const double NoticeSeconds = 2;

    /// <summary>The least time a toast stays up: long enough to notice and read it whole.</summary>
    public static double ReadingSeconds(string text) => NoticeSeconds + text.Length / ReadingCharsPerSecond;

    private readonly Func<ToastFilter> _filter;
    private readonly List<Toast> _visible = new();
    private readonly List<Toast> _history = new();
    private readonly Queue<double> _recent = new();
    private long _nextId = 1;

    /// <summary>Creates the queue; the filter is read on every add, so the setting can change at runtime.</summary>
    public ToastQueue(Func<ToastFilter> filter)
    {
        _filter = filter;
    }

    /// <summary>The toasts on screen, oldest first.</summary>
    public IReadOnlyList<Toast> Visible => _visible;

    /// <summary>Every notification shown (newest last, at most <see cref="HistorySize"/>), so a missed one can be read later.</summary>
    public IReadOnlyList<Toast> History => _history;

    /// <summary>Ordinary toasts not shown because of the throttle, since the last summary.</summary>
    public int Suppressed { get; private set; }

    /// <summary>
    /// Adds a toast. It stays the given time, but never less than its <see cref="ReadingSeconds"/>. Returns whether it's
    /// shown (false: filtered out or throttled).
    /// </summary>
    public bool Add(string text, ToastLevel level, string source, double now, double durationSeconds = 4)
    {
        durationSeconds = Math.Max(durationSeconds, ReadingSeconds(text));
        var important = level is ToastLevel.Warning or ToastLevel.Error || source == "client";
        var filter = _filter();
        if (filter == ToastFilter.Off || (filter == ToastFilter.Important && !important))
        {
            return false;
        }

        if (_visible.FirstOrDefault(t => t.Text == text && t.Level == level) is { } same)
        {
            same.Repeat(now);
            return true;
        }

        if (!important)
        {
            while (_recent.Count > 0 && now - _recent.Peek() > WindowSeconds)
            {
                _recent.Dequeue();
            }

            if (_recent.Count >= MaxPerWindow)
            {
                Suppressed++;
                return false;
            }

            _recent.Enqueue(now);
        }

        var toast = new Toast(_nextId++, text, level, source, now, durationSeconds);
        _visible.Add(toast);
        _history.Add(toast);
        if (_history.Count > HistorySize)
        {
            _history.RemoveAt(0);
        }

        while (_visible.Count > MaxVisible)
        {
            _visible.RemoveAt(_visible.FindIndex(t => t.Level is ToastLevel.Info or ToastLevel.Success) is var i and >= 0 ? i : 0);
        }

        return true;
    }

    /// <summary>Removes expired toasts and, once the throttle clears, shows one summary of what it held back.</summary>
    public void Tick(double now)
    {
        _visible.RemoveAll(t => now - t.ShownAt >= t.DurationSeconds);
        while (_recent.Count > 0 && now - _recent.Peek() > WindowSeconds)
        {
            _recent.Dequeue();
        }

        if (Suppressed > 0 && _recent.Count == 0)
        {
            var count = Suppressed;
            Suppressed = 0;
            _visible.Add(new Toast(_nextId++, $"{count} more notification{(count == 1 ? "" : "s")} (see the Activity tab)", ToastLevel.Info, "agent", now, 4));
        }
    }

    /// <summary>Removes one toast (the user dismissed it).</summary>
    public bool Dismiss(long id) => _visible.RemoveAll(t => t.Id == id) > 0;
}
