using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityLudometry.Protocol.Json;
using UnityRuntimeAnalysisAgent.Overlay.Layout;

namespace UnityRuntimeAnalysisAgent.Overlay.Views;

/// <summary>Measures text for layout (the renderer's own font metrics).</summary>
public interface ITextMeasurer
{
    /// <summary>The size of a text in a font role and size, wrapped at <paramref name="maxWidth"/> unless <paramref name="noWrap"/>.</summary>
    (double Width, double Height) Measure(string text, FontRole font, double size, double maxWidth, bool noWrap);
}

/// <summary>One drawable element after layout: its node, look, content, state and rectangle (relative to the view's root).</summary>
public sealed class RenderNode
{
    internal RenderNode(ViewNode source, string path, ResolvedStyle style, NodeState state, JsonValue? item)
    {
        Source = source;
        Path = path;
        Style = style;
        State = state;
        Item = item;
        Layout = new LayoutNode(style.Layout, path);
    }

    /// <summary>The view node it comes from (template nodes appear once per item).</summary>
    public ViewNode Source { get; }

    /// <summary>A stable path (ids, else indexes; list rows add their index): the key for state, focus and scrolling.</summary>
    public string Path { get; }

    /// <summary>The resolved look.</summary>
    public ResolvedStyle Style { get; }

    /// <summary>Hover/focus/active/disabled/checked.</summary>
    public NodeState State { get; }

    /// <summary>The list/tree/table item this node belongs to, if any.</summary>
    public JsonValue? Item { get; }

    /// <summary>The text to draw (bindings filled in), if any.</summary>
    public string? Text { get; internal set; }

    /// <summary>The bound value (toggle, slider, progress, sparkline, text field, dropdown).</summary>
    public JsonValue? Value { get; internal set; }

    /// <summary>The command a click runs, with its arguments (bindings resolved for the item).</summary>
    public string? Command { get; internal set; }

    /// <inheritdoc cref="Command"/>
    public JsonObject? Args { get; internal set; }

    /// <summary>Its tooltip (bindings filled in), or null.</summary>
    public string? Tooltip { get; internal set; }

    /// <summary>Whether it takes pointer and focus (buttons, toggles, rows with commands, inputs, nodes with a tooltip).</summary>
    public bool Interactive { get; internal set; }

    /// <summary>Whether its children are clipped to it (scrolling lists, overflow: hidden).</summary>
    public bool Clips { get; internal set; }

    /// <summary>The layout box (its rectangle after <see cref="ViewPresenter.Present"/>).</summary>
    public LayoutNode Layout { get; }

    /// <summary>The children, in drawing order.</summary>
    public List<RenderNode> Children { get; } = new();

    /// <summary>Absolute rectangle (relative to the view root), filled after layout.</summary>
    public (double X, double Y, double Width, double Height) Rect { get; internal set; }

    /// <summary>This node and its descendants.</summary>
    public IEnumerable<RenderNode> All()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var node in child.All())
            {
                yield return node;
            }
        }
    }
}

/// <summary>
/// The presentation pipeline the styled uGUI renderer uses: a view, its data, the theme and the interaction state become a
/// laid-out tree of render nodes (our flex engine, text measured by the renderer). Lists, trees and tables keep their own
/// scroll offset and build only the rows in view (fixed row height, from the first row); every row is
/// <c>flex-shrink: 0</c> and scroll viewports clip, so nothing squashes or spills. Renderer-independent and
/// unit-tested; the renderer only draws the result and reports pointer and keyboard input back.
/// </summary>
public sealed class ViewPresenter
{
    private readonly Theme _theme;
    private readonly ITextMeasurer _measurer;
    private readonly Dictionary<string, double> _scroll = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _tabs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);

    // Scrolling nodes' laid-out heights (a list that grows or stretches is taller than any height it names) and the
    // heights the current pass built rows for.
    /// <summary>An icon's size next to a label (Button, Badge, Toggle).</summary>
    public const double IconSize = 16;

    /// <summary>The room an icon takes left of its label: the icon and a gap.</summary>
    public const double IconSpace = IconSize + 4;

    private readonly Dictionary<string, double> _viewports = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _widths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _assumedWidths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _assumed = new(StringComparer.Ordinal);

    /// <summary>Creates the presenter.</summary>
    private IReadOnlyDictionary<string, double> _groups = new Dictionary<string, double>();
    private readonly Dictionary<string, int> _rowTargets = new(StringComparer.Ordinal); // ScrollToRow requests
    private const int SampledRows = 24; // rows measured for a list's row height

    public ViewPresenter(Theme theme, ITextMeasurer measurer)
    {
        _theme = theme;
        _measurer = measurer;
    }

    /// <summary>The path under the pointer, pressed and focused (set by the renderer from input).</summary>
    public string? Hovered { get; set; }

    /// <inheritdoc cref="Hovered"/>
    public string? Pressed { get; set; }

    /// <inheritdoc cref="Hovered"/>
    public string? Focused { get; set; }

    /// <summary>Node ids drawn in their checked state (e.g. the selected tab), set by the renderer.</summary>
    public ISet<string> CheckedIds { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The scroll offset of a scrolling node (pixels from the top).</summary>
    public double ScrollOf(string path) => _scroll.TryGetValue(path, out var y) ? y : 0;

    /// <summary>Scrolls a node by a delta (clamped on the next layout).</summary>
    public void ScrollBy(string path, double delta) => _scroll[path] = Math.Max(0, ScrollOf(path) + delta);

    /// <summary>Scrolls a list so a row is at its top (as far as the list scrolls), at the next <see cref="Present"/>.</summary>
    public void ScrollToRow(string listPath, int index) => _rowTargets[listPath] = Math.Max(0, index);

    /// <summary>Selects a tab of a tabs node.</summary>
    public void SelectTab(string path, int index) => _tabs[path] = Math.Max(0, index);

    /// <summary>Expands or collapses a tree row.</summary>
    public void ToggleExpanded(string path)
    {
        if (!_expanded.Remove(path))
        {
            _expanded.Add(path);
        }
    }

    /// <summary>Builds and lays out a view for the given space (reference pixels).</summary>
    public RenderNode Present(ViewDocument view, JsonValue? data, double width, double height)
    {
        RenderNode root;
        _groups = SizeGroups.Measure(view.Root, data, _theme, (node, text) =>
        {
            var style = _theme.Resolve(node);
            return _measurer.Measure(text, style.Font, style.FontSize, double.PositiveInfinity, true).Width;
        });
        for (var pass = 0; ; pass++)
        {
            _assumed.Clear();
            _assumedWidths.Clear();
            root = Build(view.Root, view.Name, data, null);
            FlexLayout.Calculate(root.Layout, width, height);
            var changed = false;
            foreach (var node in root.All())
            {
                if (_assumed.TryGetValue(node.Path, out var assumed))
                {
                    _viewports[node.Path] = Inner(node); // the rows' room: inside its padding and border
                    changed |= Math.Abs(node.Layout.Height - assumed) > 0.5;
                }

                if (_assumedWidths.TryGetValue(node.Path, out var assumedWidth))
                {
                    _widths[node.Path] = node.Layout.Width;
                    changed |= Math.Abs(node.Layout.Width - assumedWidth) > 0.5;
                }
            }

            // Rows were built for a viewport the layout didn't give: build them again for the real one (once).
            if (!changed || pass == 1)
            {
                break;
            }
        }

        _rowTargets.Clear(); // ScrollToRow requests are done

        Clamp(root);
        Place(root, 0, 0);
        return root;
    }

    private RenderNode Build(ViewNode node, string path, JsonValue? data, JsonValue? item)
    {
        var rowHover = item is not null && node.Command is null && HasHover(node); // a list row with a look of its own on hover (row:hover)
        var state = ((node.Command is not null || rowHover) && Hovered == path ? NodeState.Hover : NodeState.None)
            | (Pressed == path ? NodeState.Active : NodeState.None)
            | (Focused == path ? NodeState.Focus : NodeState.None)
            | (node.Id is { } id && CheckedIds.Contains(id) ? NodeState.Checked : NodeState.None);
        if (node.Type == NodeType.Toggle && Bindings.Truthy(node.Bind is null ? null : Bindings.Value(node.Bind, data, item)))
        {
            state |= NodeState.Checked;
        }

        var style = _theme.Resolve(node, state);
        if (node.SizeGroup is { } group && _groups.TryGetValue(group, out var groupWidth))
        {
            style.Layout.Width = Length.Px(groupWidth); // the group's width (SizeGroups)
        }
        var render = new RenderNode(node, path, style, state, item)
        {
            Text = node.Text is null ? null : Bindings.Text(node.Text, data, item),
            Value = node.Bind is null ? null : Bindings.Value(node.Bind, data, item),
            Command = node.Command,
            Args = node.Args is null ? null : ResolveArgs(node.Args, data, item),
            Tooltip = node.Tooltip is null ? null : Bindings.Text(node.Tooltip, data, item) is { Length: > 0 } tip ? tip : null,
            Interactive = node.Command is not null || node.Tooltip is not null || rowHover || node.Type is NodeType.Toggle or NodeType.Slider or NodeType.TextField or NodeType.Dropdown,
            Clips = style.Layout.Overflow != Overflow.Visible,
        };
        if (render.Text is { Length: > 0 } text && node.Type is NodeType.Text or NodeType.Button or NodeType.Badge or NodeType.Toggle)
        {
            render.Layout.Measure = maxWidth => _measurer.Measure(text, style.Font, style.FontSize, maxWidth, style.NoWrap);
            if (node.Icon is not null && node.Type is not NodeType.Text)
            {
                // The icon sits left of the text (IconSpace wide): the text is measured and drawn beside it, not under it.
                render.Layout.Measure = maxWidth =>
                {
                    var (w, h) = _measurer.Measure(text, style.Font, style.FontSize, Math.Max(1, maxWidth - IconSpace), style.NoWrap);
                    return (w + IconSpace, Math.Max(h, IconSize));
                };
            }
        }

        switch (node.Type)
        {
            case NodeType.List:
            case NodeType.Tree:
                BuildRows(render, node, path, data, item);
                break;
            case NodeType.Table:
                BuildTable(render, node, path, data, item);
                break;
            case NodeType.Tabs:
                BuildTabs(render, node, path, data, item);
                break;
            default:
                AddChildren(render, node.Children, path, data, item);
                break;
        }

        return render;
    }

    private void AddChildren(RenderNode parent, IEnumerable<ViewNode> children, string path, JsonValue? data, JsonValue? item)
    {
        var index = 0;
        foreach (var child in children)
        {
            var childPath = path + "/" + (child.Id ?? index.ToString(CultureInfo.InvariantCulture));
            index++;
            if (!Bindings.Visible(child.Visible, data, item))
            {
                continue;
            }

            var built = Build(child, childPath, data, item);
            parent.Children.Add(built);
            parent.Layout.Children.Add(built.Layout);
        }
    }

    // A scrolling list or tree: a clipping viewport with the rows in view (fixed height from the first row) and spacers.
    private void BuildRows(RenderNode list, ViewNode node, string path, JsonValue? data, JsonValue? item)
    {
        list.Clips = true;
        list.Layout.Style.Overflow = Overflow.Hidden;
        var rows = new List<(JsonValue Item, int Depth, string Path)>();
        if (node.Items is not null && Bindings.Value(node.Items, data, item) is { } items)
        {
            Flatten(rows, Items(items), node, path, 0);
        }

        if (node.Template is null || rows.Count == 0)
        {
            return;
        }

        if (node.WrapRows)
        {
            BuildWrappedRows(list, node, path, data, rows);
            return;
        }

        // Rows share one height: the tallest of the first ones (a list may mix kinds of rows, such as section headings).
        var rowHeight = rows.Take(SampledRows).Max(r => RowHeight(node.Template, data, r.Item));
        var viewport = Viewport(path, list.Style.Layout.Height);
        if (_rowTargets.TryGetValue(path, out var target))
        {
            _scroll[path] = target * rowHeight; // (kept until the last layout pass, which knows the list's real size)
        }

        _scroll[path] = Math.Max(0, Math.Min(ScrollOf(path), (rows.Count * rowHeight) - viewport)); // never past the last row

        var first = (int)Math.Floor(ScrollOf(path) / rowHeight);
        var count = (int)Math.Ceiling(viewport / rowHeight) + 1;
        for (var i = Math.Max(0, first); i < Math.Min(rows.Count, first + count); i++)
        {
            var (rowItem, depth, rowPath) = rows[i];
            var row = Build(node.Template, rowPath, data, rowItem);
            row.Layout.Style.FlexShrink = 0; // rows keep their height
            row.Layout.Style.Height = Length.Px(rowHeight); // and all have the same one, so the scroll maps to rows
            if (i == first)
            {
                // Only the rows in view are built: the first sits at the top, raised by the part of it scrolled away.
                row.Layout.Style.Margin.Top = Length.Px((first * rowHeight) - ScrollOf(path));
            }
            if (depth > 0)
            {
                row.Layout.Style.Padding.Left = Length.Px(depth * 12);
            }

            if (row.Command is null && node.Command is not null)
            {
                row.Command = node.Command;
                row.Args = node.Args is null ? new JsonObject { { "item", rowItem } } : ResolveArgs(node.Args, data, rowItem);
                row.Interactive = true;
            }

            list.Children.Add(row);
            list.Layout.Children.Add(row.Layout);
        }

        Spacer(list, Math.Max(0, rows.Count - first - count) * rowHeight);
        list.Value = new JsonNumber(rows.Count * rowHeight); // the content height, for clamping and the scrollbar
    }

    // Rows that size to their content (wrapRows): each is measured at the list's width, and the rows in view are found
    // from the running heights.
    private void BuildWrappedRows(RenderNode list, ViewNode node, string path, JsonValue? data, List<(JsonValue Item, int Depth, string Path)> rows)
    {
        var width = _widths.TryGetValue(path, out var known) ? known : 400;
        _assumedWidths[path] = width;
        var heights = rows.Select(r => RowHeight(node.Template!, data, r.Item, Math.Max(1, width - r.Depth * 12))).ToList();
        var viewport = Viewport(path, list.Style.Layout.Height);
        if (_rowTargets.TryGetValue(path, out var target))
        {
            _scroll[path] = heights.Take(target).Sum(); // (kept until the last layout pass, which knows the list's real size)
        }

        _scroll[path] = Math.Max(0, Math.Min(ScrollOf(path), heights.Sum() - viewport)); // never past the last row

        var scroll = ScrollOf(path);
        double top = 0;
        double above = 0;
        double below = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var height = heights[i];
            if (top + height <= scroll)
            {
                above += height;
            }
            else if (top >= scroll + viewport)
            {
                below += height;
            }
            else
            {
                var firstInView = list.Children.Count == 0;
                AddRow(list, node, data, rows[i]);
                if (firstInView)
                {
                    // Only the rows in view are built: the first sits at the top, raised by the part of it scrolled away.
                    list.Children[0].Layout.Style.Margin.Top = Length.Px(above - scroll);
                }
            }

            top += height;
        }

        Spacer(list, below);
        list.Value = new JsonNumber(top);
    }

    private void AddRow(RenderNode list, ViewNode node, JsonValue? data, (JsonValue Item, int Depth, string Path) entry)
    {
        var (rowItem, depth, rowPath) = entry;
        var row = Build(node.Template!, rowPath, data, rowItem);
        row.Layout.Style.FlexShrink = 0; // rows keep their height
        if (depth > 0)
        {
            row.Layout.Style.Padding.Left = Length.Px(depth * 12);
        }

        if (row.Command is null && node.Command is not null)
        {
            row.Command = node.Command;
            row.Args = node.Args is null ? new JsonObject { { "item", rowItem } } : ResolveArgs(node.Args, data, rowItem);
            row.Interactive = true;
        }

        list.Children.Add(row);
        list.Layout.Children.Add(row.Layout);
    }

    private void Flatten(List<(JsonValue, int, string)> rows, IEnumerable<JsonValue> items, ViewNode node, string path, int depth)
    {
        var index = 0;
        foreach (var entry in items)
        {
            var rowPath = path + "/" + depth.ToString(CultureInfo.InvariantCulture) + "." + (index++).ToString(CultureInfo.InvariantCulture);
            rows.Add((entry, depth, rowPath));
            if (node.Type == NodeType.Tree && node.ChildrenPath is not null && _expanded.Contains(rowPath)
                && Bindings.Value("@." + node.ChildrenPath, null, entry) is { } children)
            {
                Flatten(rows, Items(children), node, rowPath, depth + 1);
            }
        }
    }

    private void BuildTable(RenderNode table, ViewNode node, string path, JsonValue? data, JsonValue? item)
    {
        table.Clips = true;
        table.Layout.Style.Overflow = Overflow.Hidden;
        var header = Row(node, path + "/header", data, item, node.Columns.Select(c => c.Header), "label");
        table.Children.Add(header);
        table.Layout.Children.Add(header.Layout);
        var rows = node.Items is not null && Bindings.Value(node.Items, data, item) is { } items ? Items(items).ToList() : new List<JsonValue>();
        if (rows.Count == 0)
        {
            return;
        }

        var rowHeight = header.Layout.Style.Height.IsAuto ? 20 : header.Layout.Style.Height.Value;
        var viewport = Viewport(path, table.Style.Layout.Height) - rowHeight;
        var first = (int)Math.Floor(ScrollOf(path) / rowHeight);
        var count = (int)Math.Ceiling(viewport / rowHeight) + 1;
        Spacer(table, first * rowHeight);
        for (var i = Math.Max(0, first); i < Math.Min(rows.Count, first + count); i++)
        {
            var cells = node.Columns.Select(c => Bindings.Plain(Bindings.Value("@." + c.Bind, data, rows[i])));
            var row = Row(node, path + "/" + i.ToString(CultureInfo.InvariantCulture), data, rows[i], cells, "value");
            if (node.Command is not null)
            {
                row.Command = node.Command;
                row.Args = node.Args is null ? new JsonObject { { "item", rows[i] } } : ResolveArgs(node.Args, data, rows[i]);
                row.Interactive = true;
            }

            table.Children.Add(row);
            table.Layout.Children.Add(row.Layout);
        }

        Spacer(table, Math.Max(0, rows.Count - first - count) * rowHeight);
        table.Value = new JsonNumber((rows.Count + 1) * rowHeight);
    }

    // A table row: one text cell per column, widths from the columns (the rest shared).
    private RenderNode Row(ViewNode table, string path, JsonValue? data, JsonValue? item, IEnumerable<string> cells, string cellClass)
    {
        var rowNode = new ViewNode { Type = NodeType.Stack, Classes = { "row" } };
        var row = Build(rowNode, path, data, item);
        row.Layout.Style.FlexShrink = 0;
        var index = 0;
        foreach (var text in cells)
        {
            var column = table.Columns[index];
            var cellNode = new ViewNode { Type = NodeType.Text, Text = "{{}}", Classes = { cellClass } };
            cellNode.Style["white-space"] = "nowrap";
            cellNode.Style["text-overflow"] = "ellipsis";
            var cell = Build(cellNode, path + "/c" + index.ToString(CultureInfo.InvariantCulture), data, item);
            cell.Text = text;
            cell.Layout.Measure = maxWidth => _measurer.Measure(text, cell.Style.Font, cell.Style.FontSize, maxWidth, true);
            if (column.Width is { } width && Length.TryParse(width, out var length))
            {
                cell.Layout.Style.Width = length;
                cell.Layout.Style.FlexShrink = 0;
            }
            else
            {
                cell.Layout.Style.FlexGrow = 1;
                cell.Layout.Style.FlexBasis = Length.Px(0);
            }

            row.Children.Add(cell);
            row.Layout.Children.Add(cell.Layout);
            index++;
        }

        return row;
    }

    // Tabs: a header of tab buttons (the children's texts) and the selected child.
    private void BuildTabs(RenderNode tabs, ViewNode node, string path, JsonValue? data, JsonValue? item)
    {
        var visible = node.Children.Where(c => Bindings.Visible(c.Visible, data, item)).ToList();
        if (visible.Count == 0)
        {
            return;
        }

        var selected = Math.Min(_tabs.TryGetValue(path, out var s) ? s : 0, visible.Count - 1);
        tabs.Layout.Style.FlexDirection = FlexDirection.Column;
        var header = Build(new ViewNode { Type = NodeType.Stack, Classes = { "tabs" } }, path + "/header", data, item);
        header.Layout.Style.FlexDirection = FlexDirection.Row;
        for (var i = 0; i < visible.Count; i++)
        {
            var tabNode = new ViewNode { Type = NodeType.Text, Text = visible[i].Text ?? visible[i].Id ?? $"Tab {i + 1}", Classes = { "tab" }, Command = "tab.select" };
            var tabPath = path + "/tab" + i.ToString(CultureInfo.InvariantCulture);
            var tab = Build(tabNode, tabPath, data, item);
            if (i == selected)
            {
                tab = Rebuild(tab, NodeState.Checked, data, item);
            }

            tab.Args = new JsonObject { { "path", new JsonString(path) }, { "index", new JsonNumber(i) } };
            header.Children.Add(tab);
            header.Layout.Children.Add(tab.Layout);
        }

        tabs.Children.Add(header);
        tabs.Layout.Children.Add(header.Layout);
        var content = Build(visible[selected], path + "/" + (visible[selected].Id ?? selected.ToString(CultureInfo.InvariantCulture)), data, item);
        content.Layout.Style.FlexGrow = 1;
        tabs.Children.Add(content);
        tabs.Layout.Children.Add(content.Layout);
    }

    private RenderNode Rebuild(RenderNode node, NodeState extra, JsonValue? data, JsonValue? item)
    {
        var rebuilt = new RenderNode(node.Source, node.Path, _theme.Resolve(node.Source, node.State | extra), node.State | extra, item)
        {
            Text = node.Text, Command = node.Command, Args = node.Args, Interactive = node.Interactive, Value = node.Value,
        };
        if (rebuilt.Text is { Length: > 0 } text)
        {
            rebuilt.Layout.Measure = maxWidth => _measurer.Measure(text, rebuilt.Style.Font, rebuilt.Style.FontSize, maxWidth, rebuilt.Style.NoWrap);
        }

        return rebuilt;
    }

    private double RowHeight(ViewNode template, JsonValue? data, JsonValue item, double width = 400)
    {
        var probe = Build(template, "probe", data, item);
        FlexLayout.Calculate(probe.Layout, width, double.NaN);
        return Math.Max(1, probe.Layout.Height);
    }

    private static void Spacer(RenderNode parent, double height)
    {
        if (height <= 0)
        {
            return;
        }

        var spacer = new LayoutNode(new LayoutStyle { Height = Length.Px(height), FlexShrink = 0 }, "spacer");
        parent.Layout.Children.Add(spacer);
    }

    // A node's height inside its padding and border.
    private static double Inner(RenderNode node)
    {
        var style = node.Layout.Style;
        double Px(Length length) => length.Resolve(node.Layout.Width) is var px && !double.IsNaN(px) ? px : 0;
        return Math.Max(0, node.Layout.Height - Px(style.Padding.Top) - Px(style.Padding.Bottom) - Px(style.Border.Top) - Px(style.Border.Bottom));
    }

    // Whether the theme gives a node a look of its own on hover.
    private bool HasHover(ViewNode node)
    {
        var plain = _theme.Resolve(node);
        var hovered = _theme.Resolve(node, NodeState.Hover);
        return plain.BackgroundColor != hovered.BackgroundColor || plain.BorderColor != hovered.BorderColor || plain.Color != hovered.Color;
    }

    // Scroll offsets can't go past the content.
    private void Clamp(RenderNode node)
    {
        if (node.Clips && node.Value is JsonNumber content && _scroll.ContainsKey(node.Path))
        {
            _scroll[node.Path] = Math.Max(0, Math.Min(ScrollOf(node.Path), content.GetDouble() - Inner(node)));
        }

        foreach (var child in node.Children)
        {
            Clamp(child);
        }
    }

    private static void Place(RenderNode node, double originX, double originY)
    {
        var x = originX + node.Layout.X;
        var y = originY + node.Layout.Y;
        node.Rect = (x, y, node.Layout.Width, node.Layout.Height);
        foreach (var child in node.Children)
        {
            Place(child, x, y);
        }
    }

    private static IEnumerable<JsonValue> Items(JsonValue value) => value switch
    {
        JsonArray array => array,
        JsonObject o when o["items"] is JsonArray items => items,
        _ => Enumerable.Empty<JsonValue>(),
    };

    private static JsonObject ResolveArgs(JsonObject args, JsonValue? data, JsonValue? item) => Bindings.ResolveArgs(args, data, item);

    // The height rows are built for: the last laid-out height, else the named height, else a guess the next pass corrects.
    private double Viewport(string path, Length named)
    {
        var viewport = _viewports.TryGetValue(path, out var known) ? known : Number(named, 200);
        _assumed[path] = viewport;
        return viewport;
    }

    private static double Number(Length length, double fallback) => length.Unit == LengthUnit.Pixel ? length.Value : fallback;
}
