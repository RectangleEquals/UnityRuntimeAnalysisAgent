using System;
using System.Collections.Generic;
using System.Globalization;
using UnityRuntimeAnalysisAgent.Core.Input;

namespace UnityRuntimeAnalysisAgent.Overlay.Views;

/// <summary>
/// What the overlay shows about an input session, shared by the renderers: a banner at the top of
/// the screen — the countdown warning (who, why, what, how to take over), "in control" while active, the end notice, and
/// "you have control" while paused — and a coloured frame around the screen while the assistant is in control.
/// </summary>
public static class InputNoticeView
{
    /// <summary>The banner's width at most (reference pixels).</summary>
    public const double MaxWidth = 520;

    /// <summary>The text that changes what the banner shows (part of the renderers' redraw signature).</summary>
    public static string Signature(InputNotice? notice) => notice is null ? string.Empty
        : notice.State + ":" + (notice.SecondsLeft is { } s ? Math.Ceiling(s).ToString(CultureInfo.InvariantCulture) : "-") + ":" + notice.Ending;

    /// <summary>Whether the screen frame shows (the assistant is in control).</summary>
    public static bool Framed(InputNotice? notice) => notice?.State == InputSessionState.Active;

    /// <summary>The banner for a notice. <paramref name="escape"/> makes text safe for the renderer.</summary>
    public static ViewNode Banner(InputNotice notice, Func<string, string> escape)
    {
        var seconds = notice.SecondsLeft is { } s ? Math.Max(1, (int)Math.Ceiling(s)).ToString(CultureInfo.InvariantCulture) : string.Empty;
        var (title, detail) = notice.State switch
        {
            InputSessionState.Countdown => ($"{notice.Client} will control the game in {seconds}…", $"It will drive: {string.Join(", ", notice.Devices)}."),
            InputSessionState.Paused => ("You have control.", $"{notice.Client} paused its input and will ask before it continues."),
            _ when notice.Ending => ($"Control ends in {seconds}…", $"{notice.Client} is finishing."),
            _ => ($"{notice.Client} is controlling the game.", null),
        };
        var banner = new ViewNode { Type = NodeType.Panel, Id = "input-banner", Classes = { "input-banner" } };
        if (notice.State == InputSessionState.Paused)
        {
            banner.Classes.Add("input-paused");
        }

        banner.Children.Add(Line(escape(title), "input-title"));
        if (notice.State != InputSessionState.Paused)
        {
            banner.Children.Add(Line(escape("Why: " + notice.Reason), "value"));
        }

        if (detail is not null)
        {
            banner.Children.Add(Line(escape(detail), "dim"));
        }

        if (notice.State is InputSessionState.Countdown or InputSessionState.Active)
        {
            banner.Children.Add(Line(escape("To take over: " + notice.Takeover + "."), "dim"));
        }

        return banner;
    }

    /// <summary>The frame around the screen: four thin bars along its edges (x, y, width, height), never over the game.</summary>
    public static IEnumerable<(double X, double Y, double Width, double Height)> FrameBars(double width, double height, double thickness = 3)
    {
        yield return (0, 0, width, thickness);
        yield return (0, height - thickness, width, thickness);
        yield return (0, thickness, thickness, height - (2 * thickness));
        yield return (width - thickness, thickness, thickness, height - (2 * thickness));
    }

    /// <summary>One bar of the frame (it never takes the pointer).</summary>
    public static ViewNode Bar(int index) => new() { Type = NodeType.Stack, Id = "input-frame-" + index.ToString(CultureInfo.InvariantCulture), Classes = { "input-frame" } };

    private static ViewNode Line(string text, string style)
    {
        var line = new ViewNode { Type = NodeType.Text, Text = text, Classes = { style } };
        line.Style["white-space"] = "normal";
        return line;
    }
}
