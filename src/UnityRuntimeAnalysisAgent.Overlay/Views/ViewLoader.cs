using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityLudometry.Protocol.Json;
using UnityRuntimeAnalysisAgent.Overlay.Layout;

namespace UnityRuntimeAnalysisAgent.Overlay.Views;

/// <summary>
/// Reads view files. Unknown node types, unknown properties and invalid style values are skipped with a warning that
/// says where; layout properties are limited to UI Toolkit's flexbox subset. A view that can't be read at all (not JSON,
/// no root) throws.
/// </summary>
public static class ViewLoader
{
    /// <summary>The visual (non-layout) style properties renderers apply.</summary>
    public static readonly IReadOnlyCollection<string> VisualProperties = new[]
    {
        "color", "background-color", "border-color", "border-radius", "opacity", "font", "font-size", "font-style", "text-align",
        "white-space", "text-overflow",
    };

    private static readonly Dictionary<string, NodeType> Types = Enum.GetValues(typeof(NodeType)).Cast<NodeType>()
        .ToDictionary(t => char.ToLowerInvariant(t.ToString()[0]) + t.ToString().Substring(1), t => t, StringComparer.Ordinal);

    private static readonly HashSet<string> NodeProperties = new(StringComparer.Ordinal)
    {
        "type", "id", "class", "style", "text", "bind", "visible", "command", "args", "icon", "image", "effect", "tooltip", "items",
        "children", "childrenPath", "template", "columns", "range", "wrapRows", "sizeGroup", "reserve",
    };

    /// <summary>Reads a view from its JSON text.</summary>
    /// <exception cref="FormatException">Not a JSON object with a <c>root</c> node.</exception>
    public static ViewDocument Load(string name, string json)
    {
        JsonValue parsed;
        try
        {
            parsed = JsonValue.Parse(json);
        }
        catch (Exception e)
        {
            throw new FormatException($"View '{name}' isn't valid JSON: {e.Message}", e);
        }

        if (parsed is not JsonObject document || document["root"] is not JsonObject root)
        {
            throw new FormatException($"View '{name}' needs an object with a \"root\" node.");
        }

        var warnings = new List<string>();
        var node = ReadNode(root, "root", warnings) ?? throw new FormatException($"View '{name}': the root node couldn't be read ({string.Join("; ", warnings)}).");
        var ids = node.Descendants().Where(n => n.Id is not null).GroupBy(n => n.Id).Where(g => g.Count() > 1).Select(g => g.Key);
        foreach (var id in ids)
        {
            warnings.Add($"id '{id}' is used more than once.");
        }

        return new ViewDocument(name, node, warnings);
    }

    /// <summary>Checks one style property and value (layout or visual). Returns null, or why it's refused.</summary>
    public static string? CheckStyle(string property, string value)
    {
        if (value.IndexOf('$') >= 0)
        {
            return null; // uses theme tokens: checked when the theme resolves it
        }

        if (LayoutStyleParser.IsLayoutProperty(property))
        {
            return LayoutStyleParser.Apply(new LayoutStyle(), property, value);
        }

        return property switch
        {
            "color" or "background-color" or "border-color" => StyleValues.TryColor(value, out _) ? null : $"'{value}' isn't a colour (#RGB, #RRGGBB, #RRGGBBAA or transparent).",
            "border-radius" or "font-size" => StyleValues.TryNumber(value, out var n) && n >= 0 ? null : $"'{value}' isn't a size in pixels.",
            "opacity" => StyleValues.TryNumber(value, out var o) && o >= 0 && o <= 1 ? null : $"'{value}' isn't an opacity from 0 to 1.",
            "font" => value is "pixel" or "smooth" or "mono" or "heading" ? null : $"'{value}' isn't a font (pixel, smooth, mono, heading).",
            "font-style" => value is "normal" or "bold" or "italic" or "bold-and-italic" ? null : $"'{value}' isn't normal, bold, italic or bold-and-italic.",
            "text-align" => value is "left" or "center" or "right" ? null : $"'{value}' isn't left, center or right.",
            "white-space" => value is "normal" or "nowrap" ? null : $"'{value}' isn't normal or nowrap.",
            "text-overflow" => value is "clip" or "ellipsis" ? null : $"'{value}' isn't clip or ellipsis.",
            _ => $"'{property}' isn't a supported style property.",
        };
    }

    private static ViewNode? ReadNode(JsonObject json, string where, List<string> warnings)
    {
        var typeName = json["type"] is JsonString t ? t.Value : "panel";
        if (!Types.TryGetValue(typeName, out var type))
        {
            warnings.Add($"{where}: unknown node type '{typeName}'; skipped.");
            return null;
        }

        var node = new ViewNode { Type = type };
        foreach (var pair in json)
        {
            if (!NodeProperties.Contains(pair.Key))
            {
                warnings.Add($"{where}: unknown property '{pair.Key}'; ignored.");
            }
        }

        node.Id = Str(json, "id", where, warnings);
        var label = node.Id is null ? where : $"{where} ({node.Id})";
        switch (json["class"])
        {
            case JsonString s:
                node.Classes.AddRange(s.Value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
                break;
            case JsonArray a:
                node.Classes.AddRange(a.OfType<JsonString>().Select(c => c.Value));
                break;
        }

        if (json["style"] is JsonObject style)
        {
            foreach (var pair in style)
            {
                var value = pair.Value is JsonString sv ? sv.Value : pair.Value.ToString();
                if (CheckStyle(pair.Key, value) is { } problem)
                {
                    warnings.Add($"{label}: style '{pair.Key}': {problem} Ignored.");
                    continue;
                }

                node.Style[pair.Key] = value;
            }
        }

        node.Text = Str(json, "text", label, warnings);
        node.Bind = Str(json, "bind", label, warnings);
        node.Visible = Str(json, "visible", label, warnings);
        node.Command = Str(json, "command", label, warnings);
        node.Args = json["args"] as JsonObject;
        node.Icon = Str(json, "icon", label, warnings);
        node.Image = Str(json, "image", label, warnings);
        node.Effect = Str(json, "effect", label, warnings);
        node.Tooltip = Str(json, "tooltip", label, warnings);
        node.Items = Str(json, "items", label, warnings);
        node.ChildrenPath = Str(json, "childrenPath", label, warnings);
        node.WrapRows = json["wrapRows"] is JsonBoolean { Value: true };
        node.SizeGroup = Str(json, "sizeGroup", label, warnings);
        node.Reserve = json["reserve"] is JsonBoolean { Value: true };
        if (json["range"] is JsonArray range && range.Count == 2 && range[0] is JsonNumber min && range[1] is JsonNumber max)
        {
            node.Range = (min.GetDouble(), max.GetDouble());
        }

        if (json["template"] is JsonObject template)
        {
            node.Template = ReadNode(template, label + ".template", warnings);
        }

        if (json["columns"] is JsonArray columns)
        {
            foreach (var column in columns.OfType<JsonObject>())
            {
                node.Columns.Add(new ViewColumn
                {
                    Header = column["header"] is JsonString h ? h.Value : "",
                    Bind = column["bind"] is JsonString b ? b.Value : "",
                    Width = column["width"] is JsonString w ? w.Value : column["width"] is JsonNumber wn ? wn.ToString() : null,
                });
            }
        }

        if (json["children"] is JsonArray children)
        {
            var index = 0;
            foreach (var child in children)
            {
                if (child is JsonObject childJson && ReadNode(childJson, $"{label}.children[{index}]", warnings) is { } read)
                {
                    node.Children.Add(read);
                }

                index++;
            }
        }

        Check(node, label, warnings);
        return node;
    }

    // What each type needs to be useful.
    private static void Check(ViewNode node, string where, List<string> warnings)
    {
        void Need(bool ok, string what)
        {
            if (!ok)
            {
                warnings.Add($"{where}: a {Types.First(p => p.Value == node.Type).Key} needs {what}.");
            }
        }

        switch (node.Type)
        {
            case NodeType.Button:
                Need(node.Command is not null, "a command");
                break;
            case NodeType.Toggle or NodeType.Slider or NodeType.TextField or NodeType.Progress or NodeType.Sparkline:
                Need(node.Bind is not null, "a bind path");
                break;
            case NodeType.List or NodeType.Tree:
                Need(node.Items is not null && node.Template is not null, "items and a template");
                break;
            case NodeType.Table:
                Need(node.Items is not null && node.Columns.Count > 0, "items and columns");
                break;
            case NodeType.Dropdown:
                Need(node.Bind is not null && node.Items is not null, "a bind path and items");
                break;
            case NodeType.Icon:
                Need(node.Icon is not null, "an icon");
                break;
            case NodeType.Image:
                Need(node.Image is not null, "an image");
                break;
            case NodeType.Effect:
                Need(node.Effect is not null, "an effect preset");
                break;
        }

        if (node.Type == NodeType.Slider && node.Range is null)
        {
            node.Range = (0, 1);
        }
    }

    private static string? Str(JsonObject json, string name, string where, List<string> warnings)
    {
        switch (json[name])
        {
            case null or JsonNull:
                return null;
            case JsonString s:
                return s.Value;
            default:
                warnings.Add($"{where}: '{name}' must be text; ignored.");
                return null;
        }
    }
}

/// <summary>Style value parsing shared by views, themes and renderers.</summary>
public static class StyleValues
{
    /// <summary>Reads <c>#RGB</c>, <c>#RRGGBB</c>, <c>#RRGGBBAA</c> or <c>transparent</c> as 0xRRGGBBAA.</summary>
    public static bool TryColor(string text, out uint rgba)
    {
        text = text.Trim();
        rgba = 0;
        if (text == "transparent")
        {
            return true;
        }

        if (!text.StartsWith("#", StringComparison.Ordinal))
        {
            return false;
        }

        var hex = text.Substring(1);
        if (hex.Length == 3)
        {
            hex = string.Concat(hex.Select(c => new string(c, 2)));
        }

        if ((hex.Length != 6 && hex.Length != 8) || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        rgba = hex.Length == 6 ? (value << 8) | 0xFF : value;
        return true;
    }

    /// <summary>Reads a plain number or <c>12px</c>.</summary>
    public static bool TryNumber(string text, out double value)
    {
        text = text.Trim();
        if (text.EndsWith("px", StringComparison.OrdinalIgnoreCase))
        {
            text = text.Substring(0, text.Length - 2);
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
