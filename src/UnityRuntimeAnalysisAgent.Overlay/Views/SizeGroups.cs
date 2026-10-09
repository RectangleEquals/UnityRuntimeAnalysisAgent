using System;
using System.Collections.Generic;
using System.Linq;
using UnityLudometry.Protocol.Json;
using UnityRuntimeAnalysisAgent.Overlay.Layout;

namespace UnityRuntimeAnalysisAgent.Overlay.Views;

/// <summary>
/// Widths shared by a group of nodes (<see cref="ViewNode.SizeGroup"/>): every node of a group gets the natural width of
/// its widest member, so a column of controls lines up (for example each row's buttons in a list, whatever their
/// labels). Measured from the data: a list's members are measured for every item, not just the rows on screen, so the
/// width doesn't change as the list scrolls.
/// </summary>
public static class SizeGroups
{
    /// <summary>
    /// The width of each group in a view for its data. <paramref name="textWidth"/> is the renderer's text measurement
    /// for a node (its font and size from the theme) on one line.
    /// </summary>
    public static IReadOnlyDictionary<string, double> Measure(ViewNode root, JsonValue? data, Theme theme, Func<ViewNode, string, double> textWidth)
    {
        var widths = new Dictionary<string, double>(StringComparer.Ordinal);
        Walk(root, data, null, theme, textWidth, widths);
        return widths;
    }

    private static void Walk(ViewNode node, JsonValue? data, JsonValue? item, Theme theme, Func<ViewNode, string, double> textWidth, Dictionary<string, double> widths)
    {
        if (node.SizeGroup is { } group && Bindings.Visible(node.Visible, data, item))
        {
            var width = Natural(node, data, item, theme, textWidth, reserve: node.Reserve);
            widths[group] = Math.Max(widths.TryGetValue(group, out var w) ? w : 0, width);
        }

        if (node.Template is { } template && node.Items is { } path && Bindings.Value(path, data, item) is JsonArray items)
        {
            foreach (var entry in items)
            {
                Walk(template, data, entry, theme, textWidth, widths);
            }
        }

        foreach (var child in node.Children)
        {
            Walk(child, data, item, theme, textWidth, widths);
        }
    }

    // A node's width from its content: its text (and icon), or its visible children side by side (a row) or the widest
    // (a column), plus its padding and border. A fixed width in pixels wins. With reserve, hidden children count too.
    private static double Natural(ViewNode node, JsonValue? data, JsonValue? item, Theme theme, Func<ViewNode, string, double> textWidth, bool reserve)
    {
        var style = theme.Resolve(node);
        var layout = style.Layout;
        var fixedWidth = layout.Width.Resolve(double.NaN);
        if (!double.IsNaN(fixedWidth))
        {
            return fixedWidth;
        }

        double content;
        if (node.Children.Count > 0)
        {
            var row = layout.FlexDirection is FlexDirection.Row or FlexDirection.RowReverse;
            content = 0;
            foreach (var child in node.Children.Where(c => reserve || Bindings.Visible(c.Visible, data, item)))
            {
                var margin = Px(theme.Resolve(child).Layout.Margin.Left) + Px(theme.Resolve(child).Layout.Margin.Right);
                var width = Natural(child, data, item, theme, textWidth, reserve) + margin;
                content = row ? content + width : Math.Max(content, width);
            }
        }
        else
        {
            var text = node.Text is null ? string.Empty : Bindings.Text(node.Text, data, item);
            content = text.Length > 0 ? textWidth(node, text) : 0;
            if (node.Icon is not null)
            {
                content += text.Length > 0 ? ViewPresenter.IconSpace : ViewPresenter.IconSize;
            }
        }

        var minWidth = layout.MinWidth.Resolve(double.NaN);
        var total = content + Px(layout.Padding.Left) + Px(layout.Padding.Right) + Px(layout.Border.Left) + Px(layout.Border.Right);
        return double.IsNaN(minWidth) ? total : Math.Max(minWidth, total);
    }

    private static double Px(Length length) => length.Resolve(double.NaN) is var px && !double.IsNaN(px) ? px : 0;
}
