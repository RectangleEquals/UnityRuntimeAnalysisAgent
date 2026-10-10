using System;
using System.Collections.Generic;
using System.Globalization;
using UnityLudometry.Protocol.Json;
using UnityRuntimeAnalysisAgent.Core.Overlay;
using UnityRuntimeAnalysisAgent.Overlay.Layout;

namespace UnityRuntimeAnalysisAgent.Overlay.Views;

/// <summary>
/// The panel's tab bar. Tabs wrap onto more rows while that leaves room for the tab's content; when they'd take three
/// rows or more (two on screens 720 pixels high or less) they become one row that scrolls: <c>&lt;</c> and <c>&gt;</c>
/// at its sides and the mouse wheel over it move it a tab at a time, gliding there like a text box's scrolling, and the
/// selected tab is always in view. Shared by the renderers, so the bar behaves the same however it's drawn: they build
/// it with <see cref="Build"/> and, between rebuilds, move its <see cref="RowId"/> to <see cref="ShownAt"/>.
/// </summary>
public sealed class TabStrip
{
    /// <summary>The command the strip's arrows and the wheel over it run (<c>{by: -1|1}</c>).</summary>
    public const string ScrollCommand = "tabs.scroll";

    /// <summary>The id of the strip's window onto the tabs.</summary>
    public const string ViewportId = "tabs-view";

    /// <summary>The id of the row of tabs inside the window: its left margin is minus the scroll offset.</summary>
    public const string RowId = "tabs-row";

    private readonly List<double> _starts = new(); // each tab's left edge in the row
    private double _max; // the furthest the row scrolls
    private double _target; // where the row is going (pixels)
    private double _from; // where the glide started
    private double _glideStart = double.NegativeInfinity;
    private string? _followed; // the selected tab the strip last brought into view

    /// <summary>Whether the last bar built was a strip.</summary>
    public bool Scrolling { get; private set; }

    /// <summary>Whether the row is still gliding at a time.</summary>
    public bool Gliding(double now) => Scrolling && now - _glideStart < TextBox.GlideSeconds;

    /// <summary>
    /// The row's scroll offset at a time: a fixed-length ease-out (cubic) from where it was when the target last
    /// changed, the same glide as a text box's.
    /// </summary>
    public double ShownAt(double now)
    {
        var t = Math.Max(0, Math.Min(1, (now - _glideStart) / TextBox.GlideSeconds));
        var eased = 1 - Math.Pow(1 - t, 3);
        return _from + ((_target - _from) * eased);
    }

    /// <summary>Moves the strip by whole tabs (negative: back). False when it can't move that way.</summary>
    public bool Scroll(int by)
    {
        if (!Scrolling || by == 0)
        {
            return false;
        }

        var target = _target;
        for (var step = 0; step < Math.Abs(by); step++)
        {
            var next = by > 0 ? _max : 0;
            foreach (var start in _starts)
            {
                if (by > 0 && start > target + 0.5)
                {
                    next = Math.Min(start, _max);
                    break;
                }

                if (by < 0 && start < target - 0.5)
                {
                    next = start;
                }
            }

            target = next;
        }

        return GlideTo(target, TextBox.Clock());
    }

    /// <summary>
    /// The width the bar has in a panel: the panel's inner width less the header's other <paramref name="items"/>.
    /// </summary>
    public static double Available(double panelWidth, ViewNode panel, IEnumerable<ViewNode> items, Theme theme, Func<ViewNode, string, double> textWidth)
    {
        var layout = theme.Resolve(panel).Layout;
        var width = panelWidth - Px(layout.Padding.Left) - Px(layout.Padding.Right) - Px(layout.Border.Left) - Px(layout.Border.Right);
        foreach (var item in items)
        {
            var margin = theme.Resolve(item).Layout.Margin;
            width -= SizeGroups.Width(item, null, theme, textWidth) + Px(margin.Left) + Px(margin.Right);
        }

        return Math.Max(0, width);
    }

    /// <summary>
    /// Builds the bar: <paramref name="tabs"/> as (id, title), <paramref name="selected"/> the open tab's id,
    /// <paramref name="available"/> the bar's width (reference pixels), <paramref name="screenHeight"/> the screen's
    /// height in pixels, <paramref name="textWidth"/> the renderer's one-line text measurement for a node, and
    /// <paramref name="now"/> the glide's clock (<see cref="TextBox.Clock"/>).
    /// </summary>
    public ViewNode Build(IReadOnlyList<(string Id, string Title)> tabs, string selected, double available, double screenHeight, Theme theme, Func<ViewNode, string, double> textWidth, double now)
    {
        var nodes = new List<ViewNode>();
        var widths = new List<double>();
        var selectedIndex = 0;
        for (var i = 0; i < tabs.Count; i++)
        {
            var node = new ViewNode { Type = NodeType.Text, Id = "tab-" + tabs[i].Id, Text = tabs[i].Title, Classes = { "tab" }, Command = "tab.open", Args = new JsonObject { { "tab", new JsonString(tabs[i].Id) } } };
            nodes.Add(node);
            widths.Add(SizeGroups.Width(node, null, theme, textWidth));
            selectedIndex = tabs[i].Id == selected ? i : selectedIndex;
        }

        var bar = new ViewNode { Type = NodeType.Stack, Id = "tabs", Classes = { "tabs" } };
        bar.Style["flex-direction"] = "row";
        bar.Style["flex-grow"] = "1";
        bar.Style["flex-shrink"] = "1";
        var rows = Rows(widths, available);
        Scrolling = rows >= 3 || (rows >= 2 && screenHeight <= 720);
        if (!Scrolling)
        {
            bar.Style["flex-wrap"] = "wrap";
            bar.Children.AddRange(nodes);
            _target = _from = 0;
            _glideStart = double.NegativeInfinity;
            _followed = null;
            return bar;
        }

        bar.Style["align-items"] = "center";
        bar.Style["min-width"] = "0";
        var back = Arrow("tabs-back", "<", -1);
        var forward = Arrow("tabs-forward", ">", 1);
        var room = Math.Max(0, available - SizeGroups.Width(back, null, theme, textWidth) - SizeGroups.Width(forward, null, theme, textWidth) - (2 * Gap));
        _starts.Clear();
        double total = 0;
        foreach (var width in widths)
        {
            _starts.Add(total);
            total += width;
        }

        _max = Math.Max(0, total - room);
        if (_followed != selected)
        {
            // The selected tab in view whenever it changes (it glides there like any other move).
            _followed = selected;
            var start = _starts[selectedIndex];
            var end = start + widths[selectedIndex];
            GlideTo(start < _target ? start : end > _target + room ? end - room : _target, now);
        }

        GlideTo(_target, now); // within the range if the width changed
        var row = new ViewNode { Type = NodeType.Stack, Id = RowId };
        row.Style["flex-direction"] = "row";
        row.Style["flex-shrink"] = "0";
        row.Style["align-items"] = "center";
        row.Style["margin-left"] = (-ShownAt(now)).ToString("0.##", CultureInfo.InvariantCulture);
        row.Children.AddRange(nodes);
        var view = new ViewNode { Type = NodeType.Stack, Id = ViewportId };
        view.Style["flex-direction"] = "row";
        view.Style["flex-grow"] = "1";
        view.Style["flex-shrink"] = "1";
        view.Style["min-width"] = "0";
        view.Style["overflow"] = "hidden";
        view.Style["align-items"] = "center";
        view.Style["margin"] = "0 " + Gap.ToString(CultureInfo.InvariantCulture);
        view.Children.Add(row);

        Enable(back, _target > 0.5);
        Enable(forward, _target < _max - 0.5);
        bar.Children.Add(back);
        bar.Children.Add(view);
        bar.Children.Add(forward);
        return bar;
    }

    // Between the arrows and the tabs (pixels).
    private const double Gap = 2;

    private bool GlideTo(double target, double now)
    {
        target = Math.Max(0, Math.Min(_max, target));
        if (Math.Abs(target - _target) < 0.01)
        {
            _target = target;
            return false;
        }

        _from = ShownAt(now);
        _glideStart = now;
        _target = target;
        return true;
    }

    // How many rows the tabs wrap onto in a width.
    private static int Rows(List<double> widths, double available)
    {
        var rows = 1;
        double used = 0;
        foreach (var width in widths)
        {
            if (used > 0 && used + width > available)
            {
                rows++;
                used = 0;
            }

            used += width;
        }

        return rows;
    }

    private static double Px(Length length) => length.Resolve(double.NaN) is var px && !double.IsNaN(px) ? px : 0;

    private static ViewNode Arrow(string id, string text, int by)
    {
        var arrow = new ViewNode { Type = NodeType.Button, Id = id, Text = text, Command = ScrollCommand, Args = new JsonObject { { "by", new JsonNumber(by) } } };
        arrow.Style["padding"] = "0 $space.2";
        return arrow;
    }

    // An arrow with nothing more that way is dimmed and does nothing.
    private static void Enable(ViewNode arrow, bool enabled)
    {
        if (!enabled)
        {
            arrow.Command = null;
            arrow.Args = null;
            arrow.Style["opacity"] = "0.35";
        }
    }
}
