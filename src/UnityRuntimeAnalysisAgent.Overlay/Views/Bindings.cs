using System;
using System.Globalization;
using System.Text;
using UnityLudometry.Protocol.Json;
using UnityRuntimeAnalysisAgent.Core.Overlay;

namespace UnityRuntimeAnalysisAgent.Overlay.Views;

/// <summary>
/// Reads view-model data for a view: values at paths, text with <c>{path}</c> (and <c>{path|format}</c>) bindings, and
/// visibility conditions. In a list or tree template, paths starting with <c>@</c> refer to the current item
/// (<c>@.method</c>, <c>@</c> for the item itself); other paths refer to the tab's data.
/// </summary>
public static class Bindings
{
    /// <summary>The value at a path (null when missing).</summary>
    public static JsonValue? Value(string path, JsonValue? data, JsonValue? item = null)
    {
        path = path.Trim();
        if (path == "@")
        {
            return item;
        }

        return path.StartsWith("@.", StringComparison.Ordinal) ? OverlayViewModels.At(item, path.Substring(2)) : OverlayViewModels.At(data, path);
    }

    /// <summary>
    /// Text with its bindings filled in. Formats: a .NET number format (<c>0.0</c>, <c>N0</c>), <c>bytes</c> (KiB/MiB),
    /// <c>ms</c> (a duration), <c>count</c> (the length of a list). Missing values show as <c>–</c>; <c>{{</c> is a brace.
    /// </summary>
    public static string Text(string? template, JsonValue? data, JsonValue? item = null)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        for (var i = 0; i < template!.Length; i++)
        {
            var c = template[i];
            if (c == '{' && i + 1 < template.Length && template[i + 1] == '{')
            {
                text.Append('{');
                i++;
                continue;
            }

            if (c == '}' && i + 1 < template.Length && template[i + 1] == '}')
            {
                text.Append('}');
                i++;
                continue;
            }

            var end = c == '{' ? template.IndexOf('}', i + 1) : -1;
            if (end < 0)
            {
                text.Append(c);
                continue;
            }

            var binding = template.Substring(i + 1, end - i - 1);
            var bar = binding.IndexOf('|');
            var path = bar < 0 ? binding : binding.Substring(0, bar);
            var format = bar < 0 ? null : binding.Substring(bar + 1).Trim();
            text.Append(Format(Value(path, data, item), format));
            i = end;
        }

        return text.ToString();
    }

    /// <summary>Whether a condition holds: <c>path</c> (truthy), <c>!path</c>, <c>path == value</c>, <c>path != value</c>; empty is true.</summary>
    public static bool Visible(string? condition, JsonValue? data, JsonValue? item = null)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return true;
        }

        var text = condition!.Trim();
        foreach (var (op, equal) in new[] { ("!=", false), ("==", true) })
        {
            var at = text.IndexOf(op, StringComparison.Ordinal);
            if (at > 0)
            {
                var actual = Plain(Value(text.Substring(0, at), data, item));
                var expected = text.Substring(at + 2).Trim().Trim('\'', '"');
                return string.Equals(actual, expected, StringComparison.Ordinal) == equal;
            }
        }

        return text.StartsWith("!", StringComparison.Ordinal) ? !Truthy(Value(text.Substring(1), data, item)) : Truthy(Value(text, data, item));
    }

    /// <summary>Whether a value counts as true: not null/false/0/empty.</summary>
    public static bool Truthy(JsonValue? value) => value switch
    {
        null or JsonNull => false,
        JsonBoolean b => b.Value,
        JsonNumber n => n.GetDouble() != 0,
        JsonString s => s.Value.Length > 0,
        JsonArray a => a.Count > 0,
        _ => true,
    };

    /// <summary>A value as plain text (strings without quotes).</summary>
    public static string Plain(JsonValue? value) => value switch
    {
        null or JsonNull => "",
        JsonString s => s.Value,
        JsonBoolean b => b.Value ? "true" : "false",
        _ => value.ToString(),
    };

    private static string Format(JsonValue? value, string? format)
    {
        if (value is null or JsonNull)
        {
            return "–";
        }

        if (format is null)
        {
            return Plain(value);
        }

        switch (format)
        {
            case "count":
                return value is JsonArray a ? a.Count.ToString(CultureInfo.InvariantCulture) : value is JsonObject o && o["items"] is JsonArray items ? items.Count.ToString(CultureInfo.InvariantCulture) : "0";
            case "bytes" when value is JsonNumber n:
                var bytes = n.GetDouble();
                return bytes >= 1024 * 1024 * 1024 ? (bytes / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " GiB"
                    : bytes >= 1024 * 1024 ? (bytes / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MiB"
                    : bytes >= 1024 ? (bytes / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " KiB"
                    : bytes.ToString("0", CultureInfo.InvariantCulture) + " B";
            case "ms" when value is JsonNumber n:
                var ms = n.GetDouble();
                return ms < 1000 ? ms.ToString("0", CultureInfo.InvariantCulture) + " ms"
                    : ms < 60_000 ? (ms / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " s"
                    : TimeSpan.FromMilliseconds(ms).ToString(ms < 3_600_000 ? @"m\:ss" : @"h\:mm\:ss", CultureInfo.InvariantCulture);
            default:
                return value is JsonNumber number ? number.GetDouble().ToString(format, CultureInfo.InvariantCulture) : Plain(value);
        }
    }
}
