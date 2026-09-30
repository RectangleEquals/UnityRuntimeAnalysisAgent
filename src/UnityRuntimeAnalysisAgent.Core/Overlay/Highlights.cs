using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace UnityRuntimeAnalysisAgent.Core.Overlay;

/// <summary>Things outlined on screen: the renderer projects each target's bounds every frame.</summary>
public sealed class Highlight
{
    internal Highlight(string id, IReadOnlyList<object> targets, string? label, uint color, double shownAt, double? durationSeconds)
    {
        Id = id;
        Targets = targets;
        Label = label;
        Color = color;
        ShownAt = shownAt;
        DurationSeconds = durationSeconds;
    }

    /// <summary>Its id (<c>hl-N</c>).</summary>
    public string Id { get; }

    /// <summary>The objects outlined (UI elements, GameObjects, components).</summary>
    public IReadOnlyList<object> Targets { get; }

    /// <summary>The label drawn with it.</summary>
    public string? Label { get; }

    /// <summary>The colour, 0xRRGGBBAA.</summary>
    public uint Color { get; }

    /// <summary>When it was shown (unscaled realtime, seconds).</summary>
    public double ShownAt { get; }

    /// <summary>How long it stays, or null until cleared.</summary>
    public double? DurationSeconds { get; }
}

/// <summary>The active highlights, their colours and expiry.</summary>
public sealed class HighlightSet
{
    /// <summary>The default colour (amber).</summary>
    public const uint DefaultColor = 0xFFB020FF;

    /// <summary>How long a highlight stays when the client gives no duration.</summary>
    public const double DefaultSeconds = 5;

    /// <summary>How long the flash on an element the client clicked stays.</summary>
    public const double FlashSeconds = 0.8;

    private readonly List<Highlight> _active = new();
    private int _next = 1;

    /// <summary>The highlights on screen.</summary>
    public IReadOnlyList<Highlight> Active => _active;

    /// <summary>Adds a highlight. The colour is <c>#RRGGBB</c> or <c>#RRGGBBAA</c> (or null for the default).</summary>
    /// <exception cref="ArgumentException">The colour can't be read.</exception>
    public Highlight Add(IReadOnlyList<object> targets, string? label, string? color, double now, int? durationMs)
    {
        var highlight = new Highlight($"hl-{_next++}", targets.ToArray(), label, color is null ? DefaultColor : ParseColor(color), now,
            durationMs is { } ms ? ms / 1000.0 : DefaultSeconds);
        _active.Add(highlight);
        return highlight;
    }

    /// <summary>A short flash on something the client acted on.</summary>
    public Highlight Flash(object target, string label, double now) =>
        Track(new Highlight($"hl-{_next++}", new[] { target }, label, 0x40C0FFFF, now, FlashSeconds));

    /// <summary>Removes one highlight (by id) or all of them. Returns how many were removed.</summary>
    public int Clear(string? id = null) => id is null ? RemoveAll() : _active.RemoveAll(h => h.Id == id);

    /// <summary>Removes expired highlights.</summary>
    public void Tick(double now) => _active.RemoveAll(h => h.DurationSeconds is { } d && now - h.ShownAt >= d);

    /// <summary>Reads <c>#RRGGBB</c> or <c>#RRGGBBAA</c>.</summary>
    /// <exception cref="ArgumentException">Not a colour in that notation.</exception>
    public static uint ParseColor(string text)
    {
        var hex = text.Trim().TrimStart('#');
        if ((hex.Length == 6 || hex.Length == 8) && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            return hex.Length == 6 ? (value << 8) | 0xFF : value;
        }

        throw new ArgumentException($"'{text}' is not a colour (#RRGGBB or #RRGGBBAA).");
    }

    private Highlight Track(Highlight highlight)
    {
        _active.Add(highlight);
        return highlight;
    }

    private int RemoveAll()
    {
        var count = _active.Count;
        _active.Clear();
        return count;
    }
}
