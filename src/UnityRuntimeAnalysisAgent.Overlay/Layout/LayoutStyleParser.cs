using System;
using System.Globalization;
using System.Linq;

namespace UnityRuntimeAnalysisAgent.Overlay.Layout;

/// <summary>Sets layout properties from their USS names and values (<c>flex-direction: row</c>, <c>margin: 4 auto</c>).</summary>
public static class LayoutStyleParser
{
    /// <summary>Whether a USS property name is a layout property of the supported subset.</summary>
    public static bool IsLayoutProperty(string name) => LayoutStyle.PropertyNames.Contains(name);

    /// <summary>Applies one property. Returns null, or why the value couldn't be used (the property is then left as it was).</summary>
    public static string? Apply(LayoutStyle style, string name, string value)
    {
        value = value.Trim();
        switch (name)
        {
            case "flex-direction":
                return Enum(value, v => style.FlexDirection = v, ("column", FlexDirection.Column), ("column-reverse", FlexDirection.ColumnReverse),
                    ("row", FlexDirection.Row), ("row-reverse", FlexDirection.RowReverse));
            case "flex-wrap":
                return Enum(value, v => style.FlexWrap = v, ("nowrap", FlexWrap.NoWrap), ("wrap", FlexWrap.Wrap), ("wrap-reverse", FlexWrap.WrapReverse));
            case "flex-grow":
                return Number(value, v => style.FlexGrow = v);
            case "flex-shrink":
                return Number(value, v => style.FlexShrink = v);
            case "flex-basis":
                return Len(value, v => style.FlexBasis = v);
            case "align-items":
                return AlignValue(value, v => style.AlignItems = v, allowAuto: false);
            case "align-self":
                return AlignValue(value, v => style.AlignSelf = v, allowAuto: true);
            case "align-content":
                return AlignValue(value, v => style.AlignContent = v, allowAuto: false);
            case "justify-content":
                return Enum(value, v => style.JustifyContent = v, ("flex-start", Justify.FlexStart), ("center", Justify.Center), ("flex-end", Justify.FlexEnd),
                    ("space-between", Justify.SpaceBetween), ("space-around", Justify.SpaceAround));
            case "position":
                return Enum(value, v => style.Position = v, ("relative", PositionType.Relative), ("absolute", PositionType.Absolute));
            case "left":
                return Len(value, v => style.Offsets.Left = v);
            case "top":
                return Len(value, v => style.Offsets.Top = v);
            case "right":
                return Len(value, v => style.Offsets.Right = v);
            case "bottom":
                return Len(value, v => style.Offsets.Bottom = v);
            case "width":
                return Len(value, v => style.Width = v);
            case "height":
                return Len(value, v => style.Height = v);
            case "min-width":
                return Len(value, v => style.MinWidth = v);
            case "min-height":
                return Len(value, v => style.MinHeight = v);
            case "max-width":
                return Len(value, v => style.MaxWidth = v);
            case "max-height":
                return Len(value, v => style.MaxHeight = v);
            case "margin":
                return Shorthand(value, style.Margin, allowAuto: true);
            case "margin-left":
                return Len(value, v => style.Margin.Left = v);
            case "margin-top":
                return Len(value, v => style.Margin.Top = v);
            case "margin-right":
                return Len(value, v => style.Margin.Right = v);
            case "margin-bottom":
                return Len(value, v => style.Margin.Bottom = v);
            case "padding":
                return Shorthand(value, style.Padding, allowAuto: false);
            case "padding-left":
                return Len(value, v => style.Padding.Left = v, allowAuto: false);
            case "padding-top":
                return Len(value, v => style.Padding.Top = v, allowAuto: false);
            case "padding-right":
                return Len(value, v => style.Padding.Right = v, allowAuto: false);
            case "padding-bottom":
                return Len(value, v => style.Padding.Bottom = v, allowAuto: false);
            case "border-width":
                return Shorthand(value, style.Border, allowAuto: false, pixelsOnly: true);
            case "border-left-width":
                return Len(value, v => style.Border.Left = v, allowAuto: false, pixelsOnly: true);
            case "border-top-width":
                return Len(value, v => style.Border.Top = v, allowAuto: false, pixelsOnly: true);
            case "border-right-width":
                return Len(value, v => style.Border.Right = v, allowAuto: false, pixelsOnly: true);
            case "border-bottom-width":
                return Len(value, v => style.Border.Bottom = v, allowAuto: false, pixelsOnly: true);
            case "display":
                return Enum(value, v => style.Display = v, ("flex", Display.Flex), ("none", Display.None));
            case "overflow":
                return Enum(value, v => style.Overflow = v, ("visible", Overflow.Visible), ("hidden", Overflow.Hidden), ("scroll", Overflow.Scroll));
            default:
                return $"'{name}' isn't a supported layout property (UI Toolkit's flexbox subset only; for example no gap or order).";
        }
    }

    private static string? Enum<T>(string value, Action<T> set, params (string Name, T Value)[] choices)
    {
        foreach (var (choiceName, choiceValue) in choices)
        {
            if (choiceName.Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                set(choiceValue);
                return null;
            }
        }

        return $"'{value}' isn't one of {string.Join(", ", choices.Select(c => c.Name))}.";
    }

    private static string? AlignValue(string value, Action<Align> set, bool allowAuto)
    {
        var choices = new[] { ("flex-start", Align.FlexStart), ("center", Align.Center), ("flex-end", Align.FlexEnd), ("stretch", Align.Stretch) };
        return Enum(value, set, allowAuto ? choices.Prepend(("auto", Align.Auto)).ToArray() : choices);
    }

    private static string? Number(string value, Action<double> set)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number >= 0)
        {
            set(number);
            return null;
        }

        return $"'{value}' isn't a number (0 or more).";
    }

    private static string? Len(string value, Action<Length> set, bool allowAuto = true, bool pixelsOnly = false)
    {
        if (!Length.TryParse(value, out var length))
        {
            return $"'{value}' isn't a length (auto, 12, 12px or 50%).";
        }

        if (length.IsExplicitAuto && !allowAuto)
        {
            return "auto isn't allowed here.";
        }

        if (pixelsOnly && length.Unit == LengthUnit.Percent)
        {
            return "only pixels are allowed here.";
        }

        set(length);
        return null;
    }

    // 1–4 values: all; vertical horizontal; top horizontal bottom; top right bottom left.
    private static string? Shorthand(string value, Edges edges, bool allowAuto, bool pixelsOnly = false)
    {
        var parts = value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 4)
        {
            return $"'{value}' needs 1 to 4 lengths.";
        }

        var lengths = new Length[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var error = Len(parts[i], v => lengths[i] = v, allowAuto, pixelsOnly);
            if (error is not null)
            {
                return error;
            }
        }

        var (top, right, bottom, left) = lengths.Length switch
        {
            1 => (lengths[0], lengths[0], lengths[0], lengths[0]),
            2 => (lengths[0], lengths[1], lengths[0], lengths[1]),
            3 => (lengths[0], lengths[1], lengths[2], lengths[1]),
            _ => (lengths[0], lengths[1], lengths[2], lengths[3]),
        };
        edges.Top = top;
        edges.Right = right;
        edges.Bottom = bottom;
        edges.Left = left;
        return null;
    }
}
