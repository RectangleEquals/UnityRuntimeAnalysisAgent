using System;
using System.Collections.Generic;
using System.Linq;
using UnityRuntimeAnalysisAgent.Core.Overlay;

namespace UnityRuntimeAnalysisAgent.Overlay.Views;

/// <summary>
/// How a <see cref="TextBox"/> looks in a view (every renderer draws the same nodes); whatever shows a text box adds these
/// nodes (a prompt's card does, for its answer).
/// </summary>
public static class TextBoxView
{
    /// <summary>
    /// The nodes showing a text box (the box, with notes on lines scrolled out of view and the length once it scrolls),
    /// for a parent to add in order. The text is wrapped into lines with the renderer's own measurement
    /// (<paramref name="metrics"/>, null before the first layout: lines then end at line breaks only); those lines are
    /// what Up, Down, Page Up/Down, Home and End move along. A window of <see cref="TextBox.VisibleLines"/> of them scrolls
    /// only when the caret leaves it, and the caret is drawn over the text at its character, so the text never moves.
    /// <paramref name="focused"/> draws the caret and the selection.
    /// </summary>
    public static IEnumerable<ViewNode> Build(TextBox box, bool focused, Func<string, string> escape, FieldMetrics? metrics)
    {
        var nodes = new List<ViewNode>();
        var content = box.Text;
        var lines = metrics is not null && metrics.Width > 0
            ? CaretLayout.Wrap(content, metrics.Width, metrics.Measure)
            : CaretLayout.Wrap(content, double.PositiveInfinity, _ => 0);
        box.SetLines(lines.Select(l => l.Start).ToList());
        var (caretLine, column) = CaretLayout.Locate(lines, box.Caret);
        var shown = box.VisibleLines;
        var first = Math.Max(0, Math.Min(box.FirstLine, lines.Count - shown));
        if (box.FollowCaret)
        {
            // Typing or moving the caret brings it back into view; scrolling the field (the wheel) leaves it where it is.
            if (caretLine < first)
            {
                first = caretLine;
            }
            else if (caretLine >= first + shown)
            {
                first = caretLine - shown + 1;
            }
        }

        box.FirstLine = first;
        // A renderer that slides the text gets all the lines (it clips them to the window and slides them by the glide every
        // frame: scrolling rebuilds nothing); the others get the window's lines where the glide is now.
        var windowed = lines.Count > shown;
        var slides = metrics?.Slides == true;
        var above = first; // the scroll target's lines above the window
        first = slides ? 0 : Math.Max(0, Math.Min(box.ShownFirstLine, lines.Count - shown));
        var count = slides ? lines.Count : Math.Min(shown, lines.Count - first);
        if (!slides)
        {
            above = first;
        }
        var caretShown = caretLine >= first && caretLine < first + count; // a caret scrolled out of view isn't drawn
        var visible = new List<string>();
        var offsets = new List<(int Source, int Shown)>(); // where each shown line starts, in the box's text and in the shown text
        var caret = 0;
        for (var i = first; i < first + count; i++)
        {
            var text = CaretLayout.LineText(content, lines[i]);
            var at = visible.Sum(v => v.Length + 1);
            if (i == caretLine)
            {
                caret = at + Math.Min(column, text.Length);
            }

            offsets.Add((lines[i].Start, at));
            visible.Add(text);
        }

        // A position in the box's text, in the shown text (positions outside the shown lines clamp to its start or end).
        int Shown(int position)
        {
            for (var i = 0; i < offsets.Count; i++)
            {
                if (position < offsets[i].Source)
                {
                    return offsets[i].Shown;
                }

                if (position <= offsets[i].Source + visible[i].Length)
                {
                    return offsets[i].Shown + position - offsets[i].Source;
                }
            }

            return visible.Sum(v => v.Length + 1) - (visible.Count > 0 ? 1 : 0);
        }

        if (above > 0)
        {
            nodes.Add(Dim(above == 1 ? "1 more line above" : $"{above} more lines above"));
        }

        var shownText = string.Join("\n", visible.ToArray());
        if (shownText.EndsWith("\n", StringComparison.Ordinal))
        {
            shownText += " "; // text layout leaves out an empty last line: a space keeps it (and the caret on it) in the box
        }

        var node = new ViewNode
        {
            Type = NodeType.Text, Text = escape(shownText.Length == 0 ? " " : shownText), Classes = { "field", focused ? "field-focused" : "field-paused" },
            Box = box, // a click in it gives it the keyboard
            Caret = focused && caretShown ? caret : null,
            Window = windowed && slides ? shown : null,
            ScrollBase = first,
            Selection = focused && box.Selection is { } selected && Shown(selected.Start) < Shown(selected.End) ? (Shown(selected.Start), Shown(selected.End)) : null,
        };
        node.Style["margin-top"] = "$space.2";
        nodes.Add(node);
        var below = lines.Count - (above + Math.Min(shown, lines.Count));
        if (below > 0)
        {
            nodes.Add(Dim(below == 1 ? "1 more line below" : $"{below} more lines below"));
        }

        if (lines.Count > shown)
        {
            nodes.Add(Dim($"{content.Length} / {box.MaxLength} characters"));
        }

        return nodes;
    }

    private static ViewNode Dim(string text) => new() { Type = NodeType.Text, Text = text, Classes = { "dim" } };
}
