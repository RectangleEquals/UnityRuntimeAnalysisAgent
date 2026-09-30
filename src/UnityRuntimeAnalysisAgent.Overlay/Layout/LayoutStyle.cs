using System;
using System.Collections.Generic;
using System.Globalization;

namespace UnityRuntimeAnalysisAgent.Overlay.Layout;

/// <summary>The main axis and its direction.</summary>
public enum FlexDirection
{
    /// <summary>Top to bottom (UI Toolkit's default).</summary>
    Column,

    /// <summary>Bottom to top.</summary>
    ColumnReverse,

    /// <summary>Left to right.</summary>
    Row,

    /// <summary>Right to left.</summary>
    RowReverse,
}

/// <summary>Whether items wrap onto more lines.</summary>
public enum FlexWrap
{
    /// <summary>One line (default).</summary>
    NoWrap,

    /// <summary>More lines, towards the cross end.</summary>
    Wrap,

    /// <summary>More lines, towards the cross start.</summary>
    WrapReverse,
}

/// <summary>Cross-axis alignment (<c>align-items</c>, <c>align-self</c>, <c>align-content</c>).</summary>
public enum Align
{
    /// <summary><c>align-self</c> only: use the parent's <c>align-items</c>.</summary>
    Auto,

    /// <summary>At the cross start.</summary>
    FlexStart,

    /// <summary>Centred.</summary>
    Center,

    /// <summary>At the cross end.</summary>
    FlexEnd,

    /// <summary>Stretched across the line (default for <c>align-items</c>).</summary>
    Stretch,
}

/// <summary>Main-axis distribution (<c>justify-content</c>).</summary>
public enum Justify
{
    /// <summary>Packed at the main start (default).</summary>
    FlexStart,

    /// <summary>Centred.</summary>
    Center,

    /// <summary>Packed at the main end.</summary>
    FlexEnd,

    /// <summary>Equal gaps between items.</summary>
    SpaceBetween,

    /// <summary>Equal space around each item.</summary>
    SpaceAround,
}

/// <summary>In the flow, or placed by offsets.</summary>
public enum PositionType
{
    /// <summary>In the flow; offsets shift it after layout (default).</summary>
    Relative,

    /// <summary>Out of the flow, placed by its offsets in the parent's padding box.</summary>
    Absolute,
}

/// <summary>Laid out, or not at all.</summary>
public enum Display
{
    /// <summary>Laid out (default).</summary>
    Flex,

    /// <summary>Takes no space; its subtree isn't laid out.</summary>
    None,
}

/// <summary>Whether children drawing outside are clipped (no effect on layout).</summary>
public enum Overflow
{
    /// <summary>Not clipped (default).</summary>
    Visible,

    /// <summary>Clipped to the padding box.</summary>
    Hidden,
}

/// <summary>
/// A length: not set, <c>auto</c>, pixels or a percentage of the containing size. For sizes, not set and auto are the same;
/// for margins, not set is 0 and auto takes free space.
/// </summary>
public readonly struct Length : IEquatable<Length>
{
    private Length(LengthUnit unit, double value)
    {
        Unit = unit;
        Value = value;
    }

    /// <summary>Not set (the default).</summary>
    public static Length Undefined => default;

    /// <summary><c>auto</c>, written explicitly.</summary>
    public static Length Auto => new(LengthUnit.Auto, 0);

    /// <summary>The unit.</summary>
    public LengthUnit Unit { get; }

    /// <summary>The number (pixels or percent).</summary>
    public double Value { get; }

    /// <summary>Whether it's auto or not set (a size that comes from the content).</summary>
    public bool IsAuto => Unit is LengthUnit.Undefined or LengthUnit.Auto;

    /// <summary>Whether it's an explicit <c>auto</c> (an auto margin).</summary>
    public bool IsExplicitAuto => Unit == LengthUnit.Auto;

    /// <summary>Pixels.</summary>
    public static Length Px(double value) => new(LengthUnit.Pixel, value);

    /// <summary>A percentage (0–100) of the containing size.</summary>
    public static Length Percent(double value) => new(LengthUnit.Percent, value);

    /// <summary>The length in pixels against a reference size, or NaN (auto, or a percentage of an unknown size).</summary>
    public double Resolve(double reference) => Unit switch
    {
        LengthUnit.Pixel => Value,
        LengthUnit.Percent => double.IsNaN(reference) ? double.NaN : reference * Value / 100,
        _ => double.NaN,
    };

    /// <summary>Reads <c>auto</c>, <c>12</c>, <c>12px</c> or <c>50%</c>.</summary>
    public static bool TryParse(string text, out Length length)
    {
        text = text.Trim();
        length = Auto;
        if (text.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var percent = text.EndsWith("%", StringComparison.Ordinal);
        var number = percent ? text.Substring(0, text.Length - 1) : text.EndsWith("px", StringComparison.OrdinalIgnoreCase) ? text.Substring(0, text.Length - 2) : text;
        if (!double.TryParse(number.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        length = percent ? Percent(value) : Px(value);
        return true;
    }

    /// <inheritdoc/>
    public bool Equals(Length other) => Unit == other.Unit && Value.Equals(other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Length other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => (Unit, Value).GetHashCode();

    /// <inheritdoc/>
    public override string ToString() => Unit switch
    {
        LengthUnit.Pixel => Value.ToString(CultureInfo.InvariantCulture) + "px",
        LengthUnit.Percent => Value.ToString(CultureInfo.InvariantCulture) + "%",
        LengthUnit.Auto => "auto",
        _ => "unset",
    };
}

/// <summary>A length's unit.</summary>
public enum LengthUnit
{
    /// <summary>Not set.</summary>
    Undefined,

    /// <summary><c>auto</c>.</summary>
    Auto,

    /// <summary>Pixels.</summary>
    Pixel,

    /// <summary>Percent of the containing size.</summary>
    Percent,
}

/// <summary>Four lengths: left, top, right, bottom.</summary>
public sealed class Edges
{
    /// <summary>Left.</summary>
    public Length Left { get; set; }

    /// <summary>Top.</summary>
    public Length Top { get; set; }

    /// <summary>Right.</summary>
    public Length Right { get; set; }

    /// <summary>Bottom.</summary>
    public Length Bottom { get; set; }

    /// <summary>Sets all four.</summary>
    public Edges All(Length value)
    {
        Left = Top = Right = Bottom = value;
        return this;
    }
}

/// <summary>
/// The layout properties of one node: exactly UI Toolkit's flexbox subset, with its defaults (a column stack,
/// <c>flex-shrink: 1</c>, stretched items, no automatic minimum size).
/// </summary>
public sealed class LayoutStyle
{
    /// <summary>The layout properties a view may set, as USS names.</summary>
    public static readonly IReadOnlyCollection<string> PropertyNames = new[]
    {
        "flex-direction", "flex-wrap", "flex-grow", "flex-shrink", "flex-basis", "align-items", "align-self", "align-content",
        "justify-content", "position", "left", "top", "right", "bottom", "width", "height", "min-width", "min-height",
        "max-width", "max-height", "margin", "margin-left", "margin-top", "margin-right", "margin-bottom", "padding",
        "padding-left", "padding-top", "padding-right", "padding-bottom", "border-width", "border-left-width",
        "border-top-width", "border-right-width", "border-bottom-width", "display", "overflow",
    };

    /// <summary>The main axis.</summary>
    public FlexDirection FlexDirection { get; set; } = FlexDirection.Column;

    /// <summary>Wrapping.</summary>
    public FlexWrap FlexWrap { get; set; } = FlexWrap.NoWrap;

    /// <summary>Share of free space taken.</summary>
    public double FlexGrow { get; set; }

    /// <summary>Share of missing space given up.</summary>
    public double FlexShrink { get; set; } = 1;

    /// <summary>The starting main size.</summary>
    public Length FlexBasis { get; set; }

    /// <summary>Cross alignment of children.</summary>
    public Align AlignItems { get; set; } = Align.Stretch;

    /// <summary>This node's cross alignment (auto: the parent's).</summary>
    public Align AlignSelf { get; set; } = Align.Auto;

    /// <summary>Distribution of lines when wrapping.</summary>
    public Align AlignContent { get; set; } = Align.FlexStart;

    /// <summary>Main-axis distribution of children.</summary>
    public Justify JustifyContent { get; set; } = Justify.FlexStart;

    /// <summary>Relative or absolute.</summary>
    public PositionType Position { get; set; } = PositionType.Relative;

    /// <summary>Offsets (left/top/right/bottom).</summary>
    public Edges Offsets { get; } = new();

    /// <summary>Width.</summary>
    public Length Width { get; set; }

    /// <summary>Height.</summary>
    public Length Height { get; set; }

    /// <summary>Minimum width.</summary>
    public Length MinWidth { get; set; }

    /// <summary>Minimum height.</summary>
    public Length MinHeight { get; set; }

    /// <summary>Maximum width.</summary>
    public Length MaxWidth { get; set; }

    /// <summary>Maximum height.</summary>
    public Length MaxHeight { get; set; }

    /// <summary>Margins (auto margins absorb free space).</summary>
    public Edges Margin { get; } = new();

    /// <summary>Padding.</summary>
    public Edges Padding { get; } = new();

    /// <summary>Border widths.</summary>
    public Edges Border { get; } = new();

    /// <summary>Laid out or not.</summary>
    public Display Display { get; set; } = Display.Flex;

    /// <summary>Clipping (no effect on layout).</summary>
    public Overflow Overflow { get; set; } = Overflow.Visible;
}
