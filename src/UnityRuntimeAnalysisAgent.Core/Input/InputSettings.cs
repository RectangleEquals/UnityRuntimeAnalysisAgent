using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityRuntimeAnalysisAgent.Core.Hosting;

namespace UnityRuntimeAnalysisAgent.Core.Input;

/// <summary>The <c>[Input]</c> settings: whether input driving is allowed, its warnings and limits, and the takeover chords.</summary>
public sealed class InputSettings
{
    private readonly List<string> _warnings = new();

    /// <summary>Whether clients may drive the game's input at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The warning before input starts (and after a resume), in milliseconds.</summary>
    public int CountdownMs { get; set; } = 3000;

    /// <summary>The longest a session may run, in milliseconds.</summary>
    public long MaxSessionMs { get; set; } = 600000;

    /// <summary>The keyboard chord that takes over (KeyCode names, all held).</summary>
    public IReadOnlyList<string> TakeoverKey { get; set; } = new[] { "Backspace", "LeftControl", "LeftAlt" };

    /// <summary>The gamepad chord that takes over (buttons by position, all held).</summary>
    public IReadOnlyList<string> TakeoverPad { get; set; } = new[] { "LeftShoulder", "RightShoulder" };

    /// <summary>Problems found while reading (each falls back to its default).</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Reads the settings, falling back to defaults (with a warning) for invalid values.</summary>
    public static InputSettings Read(IConfigSource source)
    {
        var s = new InputSettings();
        s.Enabled = s.Bool(source, "Enabled", s.Enabled);
        s.CountdownMs = (int)Math.Round(s.Number(source, "Countdown", s.CountdownMs / 1000.0, 0, 30) * 1000);
        s.MaxSessionMs = (long)s.Number(source, "MaxSessionMs", s.MaxSessionMs, 30000, 14400000);
        s.TakeoverKey = s.Chord(source, "TakeoverKey", s.TakeoverKey);
        s.TakeoverPad = s.Chord(source, "TakeoverPad", s.TakeoverPad);
        return s;
    }

    /// <summary>Changes one setting while the game runs (the value already checked against its key).</summary>
    public void Apply(string name, string value)
    {
        var source = new DictionaryConfigSource(new Dictionary<string, string> { [Key(name)] = value });
        switch (name)
        {
            case "Enabled":
                Enabled = Bool(source, name, Enabled);
                break;
            case "Countdown":
                CountdownMs = (int)Math.Round(Number(source, name, CountdownMs / 1000.0, 0, 30) * 1000);
                break;
            case "MaxSessionMs":
                MaxSessionMs = (long)Number(source, name, MaxSessionMs, 30000, 14400000);
                break;
            case "TakeoverKey":
                TakeoverKey = Chord(source, name, TakeoverKey);
                break;
            case "TakeoverPad":
                TakeoverPad = Chord(source, name, TakeoverPad);
                break;
        }
    }

    /// <summary>A chord as the settings write it (<c>Backspace + LeftControl + LeftAlt</c>).</summary>
    public static string Format(IReadOnlyList<string> chord) => string.Join(" + ", chord);

    private static string Key(string name) => "Input." + name;

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

    private IReadOnlyList<string> Chord(IConfigSource source, string name, IReadOnlyList<string> fallback)
    {
        var text = Text(source, name);
        if (text is null)
        {
            return fallback;
        }

        var parts = text.Split('+').Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
        if (parts.Length is >= 1 and <= 4)
        {
            return parts;
        }

        _warnings.Add($"{Key(name)} '{text}' is not 1 to 4 names joined by '+'; using {Format(fallback)}.");
        return fallback;
    }
}
