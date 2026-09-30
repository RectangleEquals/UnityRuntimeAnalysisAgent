using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityRuntimeAnalysisAgent.Overlay.Layout;

/// <summary>Measures a leaf's content (text, an image) for a maximum width (infinity: unconstrained).</summary>
public delegate (double Width, double Height) MeasureContent(double maxWidth);

/// <summary>A box in the layout tree; after <see cref="FlexLayout.Calculate"/> its rectangle is set.</summary>
public sealed class LayoutNode
{
    /// <summary>Creates a node.</summary>
    public LayoutNode(LayoutStyle? style = null, string? name = null)
    {
        Style = style ?? new LayoutStyle();
        Name = name;
    }

    /// <summary>A name (for tests and diagnostics).</summary>
    public string? Name { get; set; }

    /// <summary>The layout properties.</summary>
    public LayoutStyle Style { get; }

    /// <summary>The children, in order.</summary>
    public List<LayoutNode> Children { get; } = new();

    /// <summary>Measures a leaf's content (text, images); null for plain boxes.</summary>
    public MeasureContent? Measure { get; set; }

    /// <summary>Left edge, relative to the parent's border box (pixels).</summary>
    public double X { get; internal set; }

    /// <summary>Top edge, relative to the parent's border box.</summary>
    public double Y { get; internal set; }

    /// <summary>Border-box width.</summary>
    public double Width { get; internal set; }

    /// <summary>Border-box height.</summary>
    public double Height { get; internal set; }

    /// <summary>Adds children and returns this node.</summary>
    public LayoutNode Add(params LayoutNode[] children)
    {
        Children.AddRange(children);
        return this;
    }

    /// <summary>Every node with its rectangle relative to the root (for renderers and tests).</summary>
    public IEnumerable<(LayoutNode Node, double X, double Y, double Width, double Height)> Flatten(double originX = 0, double originY = 0)
    {
        var x = originX + X;
        var y = originY + Y;
        yield return (this, x, y, Width, Height);
        foreach (var child in Children)
        {
            foreach (var entry in child.Flatten(x, y))
            {
                yield return entry;
            }
        }
    }
}

/// <summary>
/// Flexbox layout with UI Toolkit's subset and defaults (Yoga's behaviour): flex basis, grow and shrink with min/max
/// freezing, wrapping, <c>align-items/self/content</c>, <c>justify-content</c>, auto margins, reversed directions,
/// relative offsets and absolutely positioned children. Sizes and positions are border-box pixels; percentages refer to
/// the parent's content box. No automatic minimum sizes (as in Yoga).
/// </summary>
public static class FlexLayout
{
    /// <summary>Lays out a tree in the given space (NaN for an unconstrained dimension: the root fits its content).</summary>
    public static void Calculate(LayoutNode root, double availableWidth, double availableHeight)
    {
        var s = root.Style;
        var margin = Margins(s, availableWidth);
        var width = Clamp(s.Width.Resolve(availableWidth), s, true, availableWidth, availableHeight);
        var height = Clamp(s.Height.Resolve(availableHeight), s, false, availableWidth, availableHeight);
        if (double.IsNaN(width) && !double.IsNaN(availableWidth))
        {
            width = Clamp(availableWidth - margin.Left - margin.Right, s, true, availableWidth, availableHeight);
        }

        if (double.IsNaN(height) && !double.IsNaN(availableHeight))
        {
            height = Clamp(availableHeight - margin.Top - margin.Bottom, s, false, availableWidth, availableHeight);
        }

        var size = Run(root, width, height, availableWidth, availableHeight, availableWidth, availableHeight, place: true);
        root.X = margin.Left;
        root.Y = margin.Top;
        root.Width = size.Width;
        root.Height = size.Height;
    }

    private readonly struct Box
    {
        public Box(double left, double top, double right, double bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public double Left { get; }

        public double Top { get; }

        public double Right { get; }

        public double Bottom { get; }

        public double Horizontal => Left + Right;

        public double Vertical => Top + Bottom;
    }

    private sealed class Item
    {
        public Item(LayoutNode node) => Node = node;

        public LayoutNode Node { get; }

        public Box Margin;
        public bool AutoMainStart;
        public bool AutoMainEnd;
        public bool AutoCrossStart;
        public bool AutoCrossEnd;
        public double Basis;
        public double Main;
        public double Cross;
        public bool Frozen;
        public double MainPos;
        public double CrossPos;
        public Align Align;

        public double MainMargins(bool row) => row ? Margin.Horizontal : Margin.Vertical;

        public double CrossMargins(bool row) => row ? Margin.Vertical : Margin.Horizontal;
    }

    private sealed class Line
    {
        public List<Item> Items { get; } = new();

        public double Cross;

        public double Position;
    }

    // Lays out one node. width/height: its border-box size when already decided (NaN: size to content, within the
    // available space). With place, its children get their final positions and sizes (recursively).
    private static (double Width, double Height) Run(LayoutNode node, double width, double height, double availableWidth, double availableHeight,
        double ownerWidth, double ownerHeight, bool place)
    {
        var s = node.Style;
        if (s.Display == Display.None)
        {
            ZeroSubtree(node);
            return (0, 0);
        }

        var padding = Resolve(s.Padding, ownerWidth);
        var border = Resolve(s.Border, ownerWidth);
        var chromeW = padding.Horizontal + border.Horizontal;
        var chromeH = padding.Vertical + border.Vertical;
        var flow = node.Children.Where(c => c.Style.Display != Display.None && c.Style.Position == PositionType.Relative).ToList();

        if (node.Measure is { } measure && node.Children.Count == 0)
        {
            var maxInner = !double.IsNaN(width) ? width - chromeW : !double.IsNaN(availableWidth) ? availableWidth - chromeW : double.PositiveInfinity;
            var content = measure(Math.Max(0, maxInner));
            var w = !double.IsNaN(width) ? width : Clamp(content.Width + chromeW, s, true, ownerWidth, ownerHeight);
            var h = !double.IsNaN(height) ? height : Clamp(content.Height + chromeH, s, false, ownerWidth, ownerHeight);
            return (Math.Max(w, chromeW), Math.Max(h, chromeH));
        }

        var row = s.FlexDirection is FlexDirection.Row or FlexDirection.RowReverse;
        var innerW = double.IsNaN(width) ? double.NaN : Math.Max(0, width - chromeW);
        var innerH = double.IsNaN(height) ? double.NaN : Math.Max(0, height - chromeH);
        var availInnerW = !double.IsNaN(innerW) ? innerW : double.IsNaN(availableWidth) ? double.NaN : Math.Max(0, availableWidth - chromeW);
        var availInnerH = !double.IsNaN(innerH) ? innerH : double.IsNaN(availableHeight) ? double.NaN : Math.Max(0, availableHeight - chromeH);
        var mainDef = row ? innerW : innerH;
        var crossDef = row ? innerH : innerW;
        var availMain = row ? availInnerW : availInnerH;
        var availCross = row ? availInnerH : availInnerW;
        // Percentages of children refer to this node's content box when it's known.
        var refW = innerW;
        var refH = innerH;

        // 1. Flex basis and hypothetical main size of every item.
        var items = new List<Item>();
        foreach (var child in flow)
        {
            var cs = child.Style;
            var item = new Item(child) { Margin = Margins(cs, refW) };
            item.AutoMainStart = (row ? cs.Margin.Left : cs.Margin.Top).IsExplicitAuto;
            item.AutoMainEnd = (row ? cs.Margin.Right : cs.Margin.Bottom).IsExplicitAuto;
            item.AutoCrossStart = (row ? cs.Margin.Top : cs.Margin.Left).IsExplicitAuto;
            item.AutoCrossEnd = (row ? cs.Margin.Bottom : cs.Margin.Right).IsExplicitAuto;
            item.Align = cs.AlignSelf == Align.Auto ? s.AlignItems : cs.AlignSelf;
            var styleMain = (row ? cs.Width : cs.Height).Resolve(row ? refW : refH);
            var styleCross = (row ? cs.Height : cs.Width).Resolve(row ? refH : refW);
            var basis = cs.FlexBasis.Resolve(mainDef);
            if (double.IsNaN(basis))
            {
                basis = styleMain;
            }

            if (double.IsNaN(basis))
            {
                // Content size: the cross size is known when it's set or when the item is stretched across a known line.
                var crossForMeasure = !double.IsNaN(styleCross) ? styleCross
                    : item.Align == Align.Stretch && !item.AutoCrossStart && !item.AutoCrossEnd && !double.IsNaN(crossDef) ? Math.Max(0, crossDef - item.CrossMargins(row)) : double.NaN;
                crossForMeasure = Clamp(crossForMeasure, cs, !row, refW, refH);
                var measured = row
                    ? Run(child, double.NaN, crossForMeasure, Sub(availMain, item.MainMargins(row)), Sub(availCross, item.CrossMargins(row)), refW, refH, place: false)
                    : Run(child, crossForMeasure, double.NaN, Sub(availCross, item.CrossMargins(row)), Sub(availMain, item.MainMargins(row)), refW, refH, place: false);
                basis = row ? measured.Width : measured.Height;
            }

            item.Basis = Math.Max(basis, MinChrome(cs, row, refW));
            item.Main = Clamp(item.Basis, cs, row, refW, refH);
            items.Add(item);
        }

        // 2. Lines.
        var lines = new List<Line>();
        var wrap = s.FlexWrap != FlexWrap.NoWrap;
        var current = new Line();
        var used = 0.0;
        foreach (var item in items)
        {
            var outer = item.Main + item.MainMargins(row);
            if (wrap && current.Items.Count > 0 && !double.IsNaN(availMain) && used + outer > availMain + 0.001)
            {
                lines.Add(current);
                current = new Line();
                used = 0;
            }

            current.Items.Add(item);
            used += outer;
        }

        lines.Add(current);

        // 3. Flexible lengths per line.
        foreach (var line in lines)
        {
            Flex(line.Items, row, mainDef, availMain, refW, refH);
        }

        // 4. This node's main size.
        var mainSize = mainDef;
        if (double.IsNaN(mainSize))
        {
            var longest = lines.Max(l => l.Items.Sum(i => i.Main + i.MainMargins(row)));
            if (!double.IsNaN(availMain))
            {
                longest = Math.Min(longest, availMain);
            }

            mainSize = Clamp(longest + (row ? chromeW : chromeH), s, row, ownerWidth, ownerHeight) - (row ? chromeW : chromeH);
        }

        // 5. Cross sizes of items and lines.
        foreach (var line in lines)
        {
            foreach (var item in line.Items)
            {
                var cs = item.Node.Style;
                var styleCross = (row ? cs.Height : cs.Width).Resolve(row ? refH : refW);
                if (!double.IsNaN(styleCross))
                {
                    item.Cross = Clamp(styleCross, cs, !row, refW, refH);
                    continue;
                }

                var measured = row
                    ? Run(item.Node, item.Main, double.NaN, item.Main, Sub(availCross, item.CrossMargins(row)), refW, refH, place: false)
                    : Run(item.Node, double.NaN, item.Main, Sub(availCross, item.CrossMargins(row)), item.Main, refW, refH, place: false);
                item.Cross = row ? measured.Height : measured.Width;
            }

            line.Cross = line.Items.Count == 0 ? 0 : line.Items.Max(i => i.Cross + i.CrossMargins(row));
        }

        if (!wrap && !double.IsNaN(crossDef))
        {
            lines[0].Cross = crossDef;
        }

        var crossSize = crossDef;
        if (double.IsNaN(crossSize))
        {
            var total = lines.Sum(l => l.Cross);
            if (!double.IsNaN(availCross) && !(row ? s.Height : s.Width).IsAuto)
            {
                total = Math.Min(total, availCross);
            }

            crossSize = Clamp(total + (row ? chromeH : chromeW), s, !row, ownerWidth, ownerHeight) - (row ? chromeH : chromeW);
        }

        // 6. align-content (more lines, or one wrapping line in a known cross size).
        var freeCross = crossSize - lines.Sum(l => l.Cross);
        var lineStart = 0.0;
        var lineGap = 0.0;
        if (wrap)
        {
            switch (s.AlignContent)
            {
                case Align.FlexEnd:
                    lineStart = freeCross;
                    break;
                case Align.Center:
                    lineStart = freeCross / 2;
                    break;
                case Align.Stretch when freeCross > 0:
                    foreach (var line in lines)
                    {
                        line.Cross += freeCross / lines.Count;
                    }

                    break;
            }
        }

        var cursor = lineStart;
        foreach (var line in lines)
        {
            line.Position = cursor;
            cursor += line.Cross + lineGap;
        }

        // 7. Stretch, then place along both axes.
        foreach (var line in lines)
        {
            foreach (var item in line.Items)
            {
                var cs = item.Node.Style;
                if (item.Align == Align.Stretch && !item.AutoCrossStart && !item.AutoCrossEnd && (row ? cs.Height : cs.Width).IsAuto)
                {
                    item.Cross = Clamp(Math.Max(0, line.Cross - item.CrossMargins(row)), cs, !row, refW, refH);
                }
            }

            PlaceMain(line.Items, row, mainSize, s.JustifyContent, s.FlexDirection is FlexDirection.RowReverse or FlexDirection.ColumnReverse);
            foreach (var item in line.Items)
            {
                var outer = item.Cross + item.CrossMargins(row);
                var free = line.Cross - outer;
                double offset;
                if (item.AutoCrossStart && item.AutoCrossEnd)
                {
                    offset = Math.Max(0, free) / 2;
                }
                else if (item.AutoCrossStart)
                {
                    offset = Math.Max(0, free);
                }
                else if (item.AutoCrossEnd)
                {
                    offset = 0;
                }
                else
                {
                    offset = item.Align switch
                    {
                        Align.FlexEnd => free,
                        Align.Center => free / 2,
                        _ => 0,
                    };
                }

                var leadingCross = s.FlexWrap == FlexWrap.WrapReverse ? (row ? item.Margin.Bottom : item.Margin.Right) : (row ? item.Margin.Top : item.Margin.Left);
                item.CrossPos = line.Position + offset + leadingCross;
            }
        }

        var nodeWidth = row ? mainSize + chromeW : crossSize + chromeW;
        var nodeHeight = row ? crossSize + chromeH : mainSize + chromeH;
        if (!double.IsNaN(width))
        {
            nodeWidth = width;
        }

        if (!double.IsNaN(height))
        {
            nodeHeight = height;
        }

        if (!place)
        {
            return (nodeWidth, nodeHeight);
        }

        // 8. Final positions: reversed directions mirror, offsets shift, and every child lays out its own subtree.
        var reverseMain = s.FlexDirection is FlexDirection.RowReverse or FlexDirection.ColumnReverse;
        var reverseCross = s.FlexWrap == FlexWrap.WrapReverse;
        var childRefW = nodeWidth - chromeW;
        var childRefH = nodeHeight - chromeH;
        foreach (var item in items)
        {
            var main = reverseMain ? mainSize - item.MainPos - item.Main : item.MainPos;
            var cross = reverseCross ? crossSize - item.CrossPos - item.Cross : item.CrossPos;
            var w = row ? item.Main : item.Cross;
            var h = row ? item.Cross : item.Main;
            var x = border.Left + padding.Left + (row ? main : cross);
            var y = border.Top + padding.Top + (row ? cross : main);
            var cs = item.Node.Style;
            var left = cs.Offsets.Left.Resolve(childRefW);
            var top = cs.Offsets.Top.Resolve(childRefH);
            x += !double.IsNaN(left) ? left : -Zero(cs.Offsets.Right.Resolve(childRefW));
            y += !double.IsNaN(top) ? top : -Zero(cs.Offsets.Bottom.Resolve(childRefH));
            var size = Run(item.Node, w, h, w, h, childRefW, childRefH, place: true);
            Set(item.Node, x, y, size.Width, size.Height);
        }

        foreach (var child in node.Children.Where(c => c.Style.Display != Display.None && c.Style.Position == PositionType.Absolute))
        {
            PlaceAbsolute(child, s, nodeWidth, nodeHeight, padding, border, row);
        }

        foreach (var hidden in node.Children.Where(c => c.Style.Display == Display.None))
        {
            ZeroSubtree(hidden);
        }

        return (nodeWidth, nodeHeight);
    }

    // Grows or shrinks the items of a line to fill (or fit) the main size, freezing items at their min/max.
    private static void Flex(List<Item> items, bool row, double mainDef, double availMain, double refW, double refH)
    {
        var target = !double.IsNaN(mainDef) ? mainDef : availMain;
        if (double.IsNaN(target) || items.Count == 0)
        {
            return;
        }

        var hypothetical = items.Sum(i => i.Main + i.MainMargins(row));
        var growing = hypothetical < target;
        if (growing && double.IsNaN(mainDef))
        {
            return; // sized to content: free space isn't handed out
        }

        foreach (var item in items)
        {
            var cs = item.Node.Style;
            item.Frozen = growing ? cs.FlexGrow <= 0 : cs.FlexShrink <= 0 || hypothetical <= target;
            if (item.Frozen)
            {
                continue;
            }

            item.Main = item.Basis;
        }

        for (var round = 0; round <= items.Count; round++)
        {
            var unfrozen = items.Where(i => !i.Frozen).ToList();
            if (unfrozen.Count == 0)
            {
                break;
            }

            var free = target - items.Sum(i => (i.Frozen ? i.Main : i.Basis) + i.MainMargins(row));
            var totalGrow = unfrozen.Sum(i => i.Node.Style.FlexGrow);
            var totalShrink = unfrozen.Sum(i => i.Node.Style.FlexShrink * i.Basis);
            var violation = 0.0;
            foreach (var item in unfrozen)
            {
                var cs = item.Node.Style;
                var size = growing
                    ? item.Basis + (totalGrow > 0 ? free * cs.FlexGrow / totalGrow : 0)
                    : item.Basis + (totalShrink > 0 ? free * cs.FlexShrink * item.Basis / totalShrink : 0);
                var clamped = Math.Max(Clamp(size, cs, row, refW, refH), MinChrome(cs, row, refW));
                violation += clamped - size;
                item.Main = clamped;
            }

            if (Math.Abs(violation) < 0.0001)
            {
                break;
            }

            foreach (var item in unfrozen)
            {
                var cs = item.Node.Style;
                var unclamped = growing
                    ? item.Basis + (totalGrow > 0 ? free * cs.FlexGrow / totalGrow : 0)
                    : item.Basis + (totalShrink > 0 ? free * cs.FlexShrink * item.Basis / totalShrink : 0);
                var diff = item.Main - unclamped;
                if ((violation > 0 && diff > 0) || (violation < 0 && diff < 0))
                {
                    item.Frozen = true;
                }
            }
        }
    }

    // Auto margins take free space first; then justify-content.
    // Positions are computed from the main start; in reversed directions that's the physical end, so the physical end
    // margin (and auto margin) leads.
    private static void PlaceMain(List<Item> items, bool row, double mainSize, Justify justify, bool reverse)
    {
        var free = mainSize - items.Sum(i => i.Main + i.MainMargins(row));
        var autos = items.Sum(i => (i.AutoMainStart ? 1 : 0) + (i.AutoMainEnd ? 1 : 0));
        var autoShare = autos > 0 && free > 0 ? free / autos : 0;
        if (autos > 0)
        {
            free = Math.Max(0, free - autoShare * autos);
        }

        var n = items.Count;
        double start = 0, gap = 0;
        if (autos == 0)
        {
            switch (justify)
            {
                case Justify.Center:
                    start = free / 2;
                    break;
                case Justify.FlexEnd:
                    start = free;
                    break;
                case Justify.SpaceBetween when free > 0 && n > 1:
                    gap = free / (n - 1);
                    break;
                case Justify.SpaceAround when free > 0:
                    gap = free / n;
                    start = gap / 2;
                    break;
            }
        }

        var cursor = start;
        foreach (var item in items)
        {
            var leading = row ? item.Margin.Left : item.Margin.Top;
            var trailing = row ? item.Margin.Right : item.Margin.Bottom;
            var autoLeading = item.AutoMainStart;
            var autoTrailing = item.AutoMainEnd;
            if (reverse)
            {
                (leading, trailing) = (trailing, leading);
                (autoLeading, autoTrailing) = (autoTrailing, autoLeading);
            }

            cursor += (autoLeading ? autoShare : 0) + leading;
            item.MainPos = cursor;
            cursor += item.Main + trailing + (autoTrailing ? autoShare : 0) + gap;
        }
    }

    // Out of the flow: offsets in the parent's padding box; without offsets, placed by the parent's justify/align.
    private static void PlaceAbsolute(LayoutNode child, LayoutStyle parent, double parentWidth, double parentHeight, Box padding, Box border, bool row)
    {
        var cs = child.Style;
        var boxW = parentWidth - border.Horizontal;
        var boxH = parentHeight - border.Vertical;
        var margin = Margins(cs, boxW);
        var left = cs.Offsets.Left.Resolve(boxW);
        var right = cs.Offsets.Right.Resolve(boxW);
        var top = cs.Offsets.Top.Resolve(boxH);
        var bottom = cs.Offsets.Bottom.Resolve(boxH);
        var width = cs.Width.Resolve(boxW);
        var height = cs.Height.Resolve(boxH);
        if (double.IsNaN(width) && !double.IsNaN(left) && !double.IsNaN(right))
        {
            width = Math.Max(0, boxW - left - right - margin.Horizontal);
        }

        if (double.IsNaN(height) && !double.IsNaN(top) && !double.IsNaN(bottom))
        {
            height = Math.Max(0, boxH - top - bottom - margin.Vertical);
        }

        width = Clamp(width, cs, true, boxW, boxH);
        height = Clamp(height, cs, false, boxW, boxH);
        var size = Run(child, width, height, boxW, boxH, boxW, boxH, place: true);
        var innerW = parentWidth - border.Horizontal - padding.Horizontal;
        var innerH = parentHeight - border.Vertical - padding.Vertical;
        double x, y;
        if (!double.IsNaN(left))
        {
            x = border.Left + left + margin.Left;
        }
        else if (!double.IsNaN(right))
        {
            x = parentWidth - border.Right - right - margin.Right - size.Width;
        }
        else
        {
            x = border.Left + padding.Left + margin.Left + Along(row ? JustifyAlign(parent.JustifyContent) : SelfAlign(cs, parent), innerW - size.Width - margin.Horizontal);
        }

        if (!double.IsNaN(top))
        {
            y = border.Top + top + margin.Top;
        }
        else if (!double.IsNaN(bottom))
        {
            y = parentHeight - border.Bottom - bottom - margin.Bottom - size.Height;
        }
        else
        {
            y = border.Top + padding.Top + margin.Top + Along(row ? SelfAlign(cs, parent) : JustifyAlign(parent.JustifyContent), innerH - size.Height - margin.Vertical);
        }

        Set(child, x, y, size.Width, size.Height);
    }

    private static Align JustifyAlign(Justify justify) => justify switch
    {
        Justify.Center => Align.Center,
        Justify.FlexEnd => Align.FlexEnd,
        _ => Align.FlexStart,
    };

    private static Align SelfAlign(LayoutStyle child, LayoutStyle parent) => child.AlignSelf == Align.Auto ? parent.AlignItems : child.AlignSelf;

    private static double Along(Align align, double free) => align switch
    {
        Align.Center => free / 2,
        Align.FlexEnd => free,
        _ => 0,
    };

    private static void Set(LayoutNode node, double x, double y, double width, double height)
    {
        node.X = x;
        node.Y = y;
        node.Width = width;
        node.Height = height;
    }

    private static void ZeroSubtree(LayoutNode node)
    {
        Set(node, 0, 0, 0, 0);
        foreach (var child in node.Children)
        {
            ZeroSubtree(child);
        }
    }

    private static Box Margins(LayoutStyle s, double reference) => new(Zero(s.Margin.Left.Resolve(reference)), Zero(s.Margin.Top.Resolve(reference)),
        Zero(s.Margin.Right.Resolve(reference)), Zero(s.Margin.Bottom.Resolve(reference)));

    private static Box Resolve(Edges edges, double reference) => new(Zero(edges.Left.Resolve(reference)), Zero(edges.Top.Resolve(reference)),
        Zero(edges.Right.Resolve(reference)), Zero(edges.Bottom.Resolve(reference)));

    private static double MinChrome(LayoutStyle s, bool row, double reference) => row
        ? Zero(s.Padding.Left.Resolve(reference)) + Zero(s.Padding.Right.Resolve(reference)) + Zero(s.Border.Left.Resolve(reference)) + Zero(s.Border.Right.Resolve(reference))
        : Zero(s.Padding.Top.Resolve(reference)) + Zero(s.Padding.Bottom.Resolve(reference)) + Zero(s.Border.Top.Resolve(reference)) + Zero(s.Border.Bottom.Resolve(reference));

    private static double Clamp(double value, LayoutStyle s, bool horizontal, double refW, double refH)
    {
        if (double.IsNaN(value))
        {
            return value;
        }

        var min = (horizontal ? s.MinWidth : s.MinHeight).Resolve(horizontal ? refW : refH);
        var max = (horizontal ? s.MaxWidth : s.MaxHeight).Resolve(horizontal ? refW : refH);
        if (!double.IsNaN(max))
        {
            value = Math.Min(value, max);
        }

        if (!double.IsNaN(min))
        {
            value = Math.Max(value, min);
        }

        return Math.Max(0, value);
    }

    private static double Sub(double value, double amount) => double.IsNaN(value) ? double.NaN : Math.Max(0, value - amount);

    private static double Zero(double value) => double.IsNaN(value) ? 0 : value;
}
