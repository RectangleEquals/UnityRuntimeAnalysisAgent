using System;
using System.Collections.Generic;
using System.Linq;
using UnityLudometry.Protocol.Json;

namespace UnityRuntimeAnalysisAgent.Overlay.Views;

/// <summary>
/// The data a view shows: every path its bindings read (texts, tooltips, binds, lists, visibility conditions and command
/// arguments), item-relative ones (<c>@.x</c>) left to the lists they belong to. A tab's view is redrawn only when the
/// values at these paths change, not when anything else in the tab's data does (a client's connection time ticking, a
/// notification's age): a redraw replaces the panel's elements, which a needless one does under the pointer.
/// </summary>
public static class ViewDependencies
{
    /// <summary>The paths a view reads, each once.</summary>
    public static IReadOnlyList<string> Of(ViewDocument view)
    {
        var paths = new List<string>();
        Collect(view.Root, paths);
        return paths.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>A key that changes exactly when one of the values at <paramref name="paths"/> does.</summary>
    public static string Key(IReadOnlyList<string> paths, JsonValue? data) =>
        string.Join("\u0001", paths.Select(p => Bindings.Value(p, data)?.ToString() ?? "\u0002").ToArray());

    private static void Collect(ViewNode node, List<string> paths)
    {
        Template(node.Text, paths);
        Template(node.Tooltip, paths);
        Path(node.Bind, paths);
        Path(node.Items, paths);
        Condition(node.Visible, paths);
        if (node.Args is not null)
        {
            Args(node.Args, paths);
        }

        foreach (var column in node.Columns)
        {
            Path(column.Bind, paths);
        }

        if (node.Template is { } template)
        {
            Collect(template, paths);
        }

        foreach (var child in node.Children)
        {
            Collect(child, paths);
        }
    }

    // An absolute path (item-relative ones are read through their list's items).
    private static void Path(string? path, List<string> paths)
    {
        var p = path?.Trim();
        if (!string.IsNullOrEmpty(p) && p != "@" && !p!.StartsWith("@.", StringComparison.Ordinal))
        {
            paths.Add(p);
        }
    }

    // The {path|format} bindings in a text.
    private static void Template(string? text, List<string> paths)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        for (var i = 0; i < text!.Length; i++)
        {
            if (text[i] != '{')
            {
                continue;
            }

            if (i + 1 < text.Length && text[i + 1] == '{')
            {
                i++; // an escaped brace
                continue;
            }

            var end = text.IndexOf('}', i + 1);
            if (end < 0)
            {
                return;
            }

            var binding = text.Substring(i + 1, end - i - 1);
            var bar = binding.IndexOf('|');
            Path(bar < 0 ? binding : binding.Substring(0, bar), paths);
            i = end;
        }
    }

    // A visibility condition: path, !path, path == value, path != value.
    private static void Condition(string? condition, List<string> paths)
    {
        var text = condition?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        foreach (var op in new[] { "!=", "==" })
        {
            var at = text!.IndexOf(op, StringComparison.Ordinal);
            if (at > 0)
            {
                Path(text.Substring(0, at), paths);
                return;
            }
        }

        Path(text!.TrimStart('!'), paths);
    }

    private static void Args(JsonValue value, List<string> paths)
    {
        switch (value)
        {
            case JsonString s:
                Template(s.Value, paths);
                break;
            case JsonObject o:
                foreach (var pair in o)
                {
                    Args(pair.Value, paths);
                }

                break;
            case JsonArray a:
                foreach (var entry in a)
                {
                    Args(entry, paths);
                }

                break;
        }
    }
}
