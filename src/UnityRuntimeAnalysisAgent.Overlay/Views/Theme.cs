using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityLudometry.Protocol.Json;
using UnityRuntimeAnalysisAgent.Overlay.Layout;

namespace UnityRuntimeAnalysisAgent.Overlay.Views;

/// <summary>An interaction state a class can style (<c>button:hover</c>).</summary>
[Flags]
public enum NodeState
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>The pointer is over it.</summary>
    Hover = 1,

    /// <summary>It has keyboard or gamepad focus.</summary>
    Focus = 2,

    /// <summary>It's being pressed.</summary>
    Active = 4,

    /// <summary>It can't be used now.</summary>
    Disabled = 8,

    /// <summary>A toggle that's on, or the selected tab or row.</summary>
    Checked = 16,
}

/// <summary>Which font family a text uses.</summary>
public enum FontRole
{
    /// <summary>UI text: the pixel font (Retro Fonts) or Rubik.</summary>
    Pixel,

    /// <summary>Always smooth (Rubik).</summary>
    Smooth,

    /// <summary>Values, code, IL, logs: pixel monospaced or JetBrains Mono.</summary>
    Mono,

    /// <summary>Headings: the pixel font at 2× or Rubik SemiBold.</summary>
    Heading,
}

/// <summary>An animated effect's settings (a theme preset).</summary>
public sealed class EffectPreset
{
    /// <summary>The effect: plasma, fire or water (the effects shader's passes).</summary>
    public string Kind { get; set; } = "plasma";

    /// <summary>The two colours, 0xRRGGBBAA.</summary>
    public uint ColorA { get; set; } = 0x0D1A33FF;

    /// <inheritdoc cref="ColorA"/>
    public uint ColorB { get; set; } = 0x4DCCFFFF;

    /// <summary>The pixel grid (cells across; 0 = smooth).</summary>
    public double Pixels { get; set; } = 64;

    /// <summary>Speed.</summary>
    public double Speed { get; set; } = 1;
}

/// <summary>A node's resolved look: layout plus the visual properties renderers apply.</summary>
public sealed class ResolvedStyle
{
    /// <summary>Layout.</summary>
    public LayoutStyle Layout { get; } = new();

    /// <summary>Text (and icon tint) colour, 0xRRGGBBAA.</summary>
    public uint Color { get; set; } = 0xE6E9F0FF;

    /// <summary>Background, 0xRRGGBBAA (0 = none).</summary>
    public uint BackgroundColor { get; set; }

    /// <summary>Border colour.</summary>
    public uint BorderColor { get; set; }

    /// <summary>Corner radius in pixels.</summary>
    public double BorderRadius { get; set; }

    /// <summary>Opacity 0–1.</summary>
    public double Opacity { get; set; } = 1;

    /// <summary>The font family.</summary>
    public FontRole Font { get; set; } = FontRole.Pixel;

    /// <summary>The requested font size in reference pixels.</summary>
    public double FontSize { get; set; } = 12;

    /// <summary>Bold and/or italic.</summary>
    public string FontStyle { get; set; } = "normal";

    /// <summary>left, center or right.</summary>
    public string TextAlign { get; set; } = "left";

    /// <summary>No wrapping.</summary>
    public bool NoWrap { get; set; }

    /// <summary>Clip long text with an ellipsis.</summary>
    public bool Ellipsis { get; set; }
}

/// <summary>
/// A theme (<c>overlay/themes/&lt;name&gt;.json</c>): tokens (colours, spacing, radii, sizes, motion) and style classes
/// (with <c>:hover</c>, <c>:focus</c>, <c>:active</c>, <c>:disabled</c>, <c>:checked</c> variants) that resolve to the
/// same styles on every renderer. Every node gets its type as its first class (<c>button</c>, <c>panel</c>…), then its
/// own classes in order, then its inline style; values may be tokens (<c>$color.accent</c>).
/// </summary>
public sealed class Theme
{
    private readonly Dictionary<string, Dictionary<string, string>> _tokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>> _classes = new(StringComparer.Ordinal);
    private readonly List<string> _warnings = new();

    private Theme(string name)
    {
        Name = name;
    }

    /// <summary>The theme's name.</summary>
    public string Name { get; }

    /// <summary>Problems found while reading or resolving (each skipped).</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Effect presets by name.</summary>
    public Dictionary<string, EffectPreset> Effects { get; } = new(StringComparer.Ordinal);

    /// <summary>Motion durations in milliseconds by name (from the <c>motion</c> tokens).</summary>
    public double Motion(string name, double fallback) =>
        _tokens.TryGetValue("motion", out var motion) && motion.TryGetValue(name, out var text) && StyleValues.TryNumber(text, out var ms) ? ms : fallback;

    /// <summary>Reads a theme from its JSON text.</summary>
    /// <exception cref="FormatException">Not a JSON object.</exception>
    public static Theme Load(string name, string json)
    {
        if (JsonValue.Parse(json) is not JsonObject document)
        {
            throw new FormatException($"Theme '{name}' isn't a JSON object.");
        }

        var theme = new Theme(document["name"] is JsonString n ? n.Value : name);
        if (document["tokens"] is JsonObject tokens)
        {
            foreach (var group in tokens)
            {
                if (group.Value is not JsonObject values)
                {
                    theme._warnings.Add($"tokens.{group.Key} must be an object; ignored.");
                    continue;
                }

                theme._tokens[group.Key] = values.ToDictionary(v => v.Key, v => v.Value is JsonString s ? s.Value : v.Value.ToString(), StringComparer.Ordinal);
            }
        }

        if (document["classes"] is JsonObject classes)
        {
            foreach (var cls in classes)
            {
                if (cls.Value is not JsonObject properties)
                {
                    theme._warnings.Add($"classes.{cls.Key} must be an object; ignored.");
                    continue;
                }

                var style = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var p in properties)
                {
                    var value = p.Value is JsonString s ? s.Value : p.Value.ToString();
                    var resolved = theme.Token(value, $"classes.{cls.Key}.{p.Key}");
                    if (resolved is null)
                    {
                        continue;
                    }

                    if (ViewLoader.CheckStyle(p.Key, resolved) is { } problem)
                    {
                        theme._warnings.Add($"classes.{cls.Key}: '{p.Key}': {problem} Ignored.");
                        continue;
                    }

                    style[p.Key] = value;
                }

                theme._classes[cls.Key] = style;
            }
        }

        if (document["effects"] is JsonObject effects)
        {
            foreach (var effect in effects)
            {
                if (effect.Value is JsonObject e)
                {
                    theme.Effects[effect.Key] = theme.ReadEffect(effect.Key, e);
                }
            }
        }

        return theme;
    }

    /// <summary>
    /// A node's resolved style: its type's class, its classes, their state variants, then its inline style. Token or
    /// value problems are skipped (and reported once in <see cref="Warnings"/>).
    /// </summary>
    public ResolvedStyle Resolve(ViewNode node, NodeState state = NodeState.None)
    {
        var resolved = new ResolvedStyle();
        var typeClass = char.ToLowerInvariant(node.Type.ToString()[0]) + node.Type.ToString().Substring(1);
        foreach (var cls in new[] { typeClass }.Concat(node.Classes))
        {
            Apply(resolved, cls, state);
        }

        foreach (var pair in node.Style)
        {
            Set(resolved, pair.Key, pair.Value, $"inline style of {node.Id ?? typeClass}");
        }

        return resolved;
    }

    /// <summary>
    /// A value with its tokens (<c>$group.name</c>) replaced, also inside shorthands (<c>0 $space.3</c>); null (with a
    /// warning) when a token is unknown.
    /// </summary>
    public string? Token(string value, string where)
    {
        if (value.IndexOf('$') < 0)
        {
            return value;
        }

        var parts = value.Split(' ');
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (!part.StartsWith("$", StringComparison.Ordinal))
            {
                continue;
            }

            var dot = part.IndexOf('.');
            if (dot > 1 && _tokens.TryGetValue(part.Substring(1, dot - 1), out var group) && group.TryGetValue(part.Substring(dot + 1), out var tokenValue))
            {
                parts[i] = tokenValue;
                continue;
            }

            Warn($"{where}: unknown token '{part}'; ignored.");
            return null;
        }

        return string.Join(" ", parts);
    }

    private void Apply(ResolvedStyle resolved, string cls, NodeState state)
    {
        if (_classes.TryGetValue(cls, out var style))
        {
            foreach (var pair in style)
            {
                Set(resolved, pair.Key, pair.Value, $"class {cls}");
            }
        }

        foreach (var flag in new[] { NodeState.Hover, NodeState.Focus, NodeState.Active, NodeState.Disabled, NodeState.Checked })
        {
            if ((state & flag) != 0 && _classes.TryGetValue(cls + ":" + flag.ToString().ToLowerInvariant(), out var variant))
            {
                foreach (var pair in variant)
                {
                    Set(resolved, pair.Key, pair.Value, $"class {cls}:{flag.ToString().ToLowerInvariant()}");
                }
            }
        }
    }

    private void Set(ResolvedStyle resolved, string property, string raw, string where)
    {
        var value = Token(raw, where);
        if (value is null)
        {
            return;
        }

        if (LayoutStyleParser.IsLayoutProperty(property))
        {
            if (LayoutStyleParser.Apply(resolved.Layout, property, value) is { } problem)
            {
                Warn($"{where}: '{property}': {problem}");
            }

            return;
        }

        switch (property)
        {
            case "color" when StyleValues.TryColor(value, out var c):
                resolved.Color = c;
                break;
            case "background-color" when StyleValues.TryColor(value, out var bg):
                resolved.BackgroundColor = bg;
                break;
            case "border-color" when StyleValues.TryColor(value, out var bc):
                resolved.BorderColor = bc;
                break;
            case "border-radius" when StyleValues.TryNumber(value, out var r):
                resolved.BorderRadius = r;
                break;
            case "opacity" when StyleValues.TryNumber(value, out var o):
                resolved.Opacity = Math.Max(0, Math.Min(1, o));
                break;
            case "font" when Enum.TryParse<FontRole>(value, ignoreCase: true, out var font):
                resolved.Font = font;
                break;
            case "font-size" when StyleValues.TryNumber(value, out var size):
                resolved.FontSize = size;
                break;
            case "font-style":
                resolved.FontStyle = value;
                break;
            case "text-align":
                resolved.TextAlign = value;
                break;
            case "white-space":
                resolved.NoWrap = value == "nowrap";
                break;
            case "text-overflow":
                resolved.Ellipsis = value == "ellipsis";
                break;
            default:
                Warn($"{where}: '{property}' = '{value}' can't be used; ignored.");
                break;
        }
    }

    private EffectPreset ReadEffect(string name, JsonObject e)
    {
        var preset = new EffectPreset();
        string? Get(string key) => e[key] is JsonString s ? Token(s.Value, $"effects.{name}.{key}") : e[key] is JsonNumber n ? n.ToString() : null;
        if (Get("kind") is { } kind)
        {
            if (kind is "plasma" or "fire" or "water")
            {
                preset.Kind = kind;
            }
            else
            {
                Warn($"effects.{name}: kind '{kind}' isn't plasma, fire or water; using plasma.");
            }
        }

        if (Get("colorA") is { } a && StyleValues.TryColor(a, out var ca))
        {
            preset.ColorA = ca;
        }

        if (Get("colorB") is { } b && StyleValues.TryColor(b, out var cb))
        {
            preset.ColorB = cb;
        }

        if (Get("pixels") is { } px && double.TryParse(px, NumberStyles.Float, CultureInfo.InvariantCulture, out var pixels))
        {
            preset.Pixels = pixels;
        }

        if (Get("speed") is { } sp && double.TryParse(sp, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
        {
            preset.Speed = speed;
        }

        return preset;
    }

    private void Warn(string message)
    {
        if (!_warnings.Contains(message))
        {
            _warnings.Add(message);
        }
    }
}

/// <summary>Which font file and size a text uses (Retro Fonts: the pixel font, at whole multiples of its design size).</summary>
public static class FontChoice
{
    /// <summary>The font file (as in the bundle) and the size to draw at, for a role, a requested size and the UI scale.</summary>
    public static (string Font, double Size) For(FontRole role, double requestedSize, bool retroFonts, double scale)
    {
        var size = requestedSize * scale;
        if (retroFonts && role != FontRole.Smooth)
        {
            var (font, design) = role switch
            {
                FontRole.Mono => ("ark-pixel-12px-monospaced", 12.0),
                FontRole.Heading => ("ark-pixel-12px-proportional", 12.0),
                _ => requestedSize < 11.5 ? ("ark-pixel-10px-proportional", 10.0) : ("ark-pixel-12px-proportional", 12.0),
            };
            var multiple = Math.Max(1, Math.Round(size / design));
            if (role == FontRole.Heading)
            {
                multiple = Math.Max(2, multiple);
            }

            return (font, design * multiple);
        }

        return role switch
        {
            FontRole.Mono => ("JetBrainsMono-Regular", size),
            FontRole.Heading => ("Rubik-SemiBold", Math.Max(size, 16 * scale)),
            _ => ("Rubik-Regular", size),
        };
    }
}
