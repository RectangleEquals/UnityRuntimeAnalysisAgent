using System;
using UnityRuntimeAnalysisAgent.Core.Overlay;
using UnityRuntimeAnalysisAgent.Overlay.Views;

namespace UnityRuntimeAnalysisAgent.Overlay.Runtime;

/// <summary>What the arrow shows, the same in every renderer: its status tint and the badge count.</summary>
public static class OverlayArrow
{
    /// <summary>The arrow's status.</summary>
    public static ArrowStatus Status(OverlayController c) => OverlayModel.Status(0, false, c.Prompts.Pending.Count, c.EStop.Engaged, false);

    /// <summary>The theme colour token (without <c>$color.</c>) for a status.</summary>
    public static string Tint(ArrowStatus status) => status switch
    {
        ArrowStatus.Alert => "error",
        ArrowStatus.Busy => "warn",
        ArrowStatus.Connected => "accent",
        _ => "text-dim",
    };

    /// <summary>The badge count: pending prompts and notifications on screen.</summary>
    public static int BadgeCount(OverlayController c) => c.Prompts.Pending.Count + c.Toasts.Visible.Count;

    /// <summary>The badge as a view node (null without anything to count).</summary>
    public static ViewNode? Badge(OverlayController c)
    {
        var count = BadgeCount(c);
        return count == 0 ? null : new ViewNode { Type = NodeType.Badge, Id = "arrow-badge", Text = count > 99 ? "99+" : count.ToString(System.Globalization.CultureInfo.InvariantCulture), Classes = { "badge" } };
    }

    /// <summary>Where the badge goes: over the arrow's corner away from the edge, kept on screen.</summary>
    public static (double X, double Y) BadgePosition(OverlayRect arrow, OverlayEdge edge, double w, double h, double screenWidth, double screenHeight)
    {
        var x = edge == OverlayEdge.Right ? arrow.X - w * 0.4 : arrow.X + arrow.Width - w * 0.6;
        var y = edge == OverlayEdge.Top ? arrow.Y + arrow.Height - h * 0.6 : arrow.Y - h * 0.4;
        return (Math.Max(0, Math.Min(x, screenWidth - w)), Math.Max(0, Math.Min(y, screenHeight - h)));
    }
}
