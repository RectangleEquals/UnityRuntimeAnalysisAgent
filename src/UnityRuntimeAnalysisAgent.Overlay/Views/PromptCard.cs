using System;
using System.Collections.Generic;
using System.Linq;
using UnityLudometry.Protocol.Json;
using UnityRuntimeAnalysisAgent.Core.Overlay;

namespace UnityRuntimeAnalysisAgent.Overlay.Views;

/// <summary>
/// A prompt's card next to the arrow (every renderer builds the same one): the title, the message and its buttons, one
/// per line at the card's full width (a row of several overflowed the card); or, while the prompt's text field is open,
/// what's been typed so far with Send and Cancel.
/// </summary>
public static class PromptCard
{
    /// <summary>
    /// The card's view node. <paramref name="escape"/> is the renderer's text escaping; <paramref name="field"/> is its
    /// measurement of the text field as last laid out (null before the first layout: lines then end at line breaks only).
    /// </summary>
    public static ViewNode Build(Prompt prompt, PromptRegistry prompts, Func<string, string> escape, FieldMetrics? field = null)
    {
        var card = new ViewNode { Type = NodeType.Panel, Id = "prompt-" + prompt.Id, Classes = { "card" } };
        card.Style["margin-top"] = "$space.2";
        card.Style["flex-shrink"] = "0"; // in a full panel (the Activity tab) the lists below give up space, not the card
        if (prompt.Title is { } title)
        {
            card.Children.Add(new ViewNode { Type = NodeType.Text, Text = escape(title), Classes = { "heading" } });
        }

        card.Children.Add(new ViewNode { Type = NodeType.Text, Text = escape(prompt.Message) });
        var buttons = new ViewNode { Type = NodeType.Stack };
        buttons.Style["flex-direction"] = "column";
        buttons.Style["margin-top"] = "$space.2";
        if (prompts.Editing == prompt.Id)
        {
            AddField(card, prompts, escape, field);
            buttons.Children.Add(Button("Send", "prompt.sendText", null));
            buttons.Children.Add(Button("Cancel", "prompt.cancelText", null));
        }
        else
        {
            foreach (var label in prompt.Buttons)
            {
                buttons.Children.Add(Button(escape(label), "prompt.answer", new JsonObject { { "id", new JsonString(prompt.Id) }, { "button", new JsonString(label) } }));
            }
        }

        card.Children.Add(buttons);
        return card;
    }

    // The text field: the answer wrapped into lines with the renderer's own measurement (those lines are what Up, Down,
    // Page Up/Down, Home and End move along), a window of PromptRegistry.VisibleLines of them that scrolls only when the
    // caret leaves it, and the caret drawn over the text at its character (ViewNode.Caret), so the text never moves.
    private static void AddField(ViewNode card, PromptRegistry prompts, Func<string, string> escape, FieldMetrics? metrics)
    {
        var draft = prompts.Draft;
        var lines = metrics is not null && metrics.Width > 0
            ? CaretLayout.Wrap(draft, metrics.Width, metrics.Measure)
            : CaretLayout.Wrap(draft, double.PositiveInfinity, _ => 0);
        prompts.SetLines(lines.Select(l => l.Start).ToList());
        var (caretLine, column) = CaretLayout.Locate(lines, prompts.Caret);
        var shown = PromptRegistry.VisibleLines;
        var first = Math.Max(0, Math.Min(prompts.FirstLine, lines.Count - shown));
        if (prompts.FollowCaret)
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

        prompts.FirstLine = first;
        // A renderer that slides the text gets all the lines (it clips them to the window and slides them by the glide every
        // frame: scrolling rebuilds nothing); the others get the window's lines where the glide is now.
        var windowed = lines.Count > shown;
        var slides = metrics?.Slides == true;
        var above = first; // the scroll target's lines above the window
        first = slides ? 0 : Math.Max(0, Math.Min(prompts.ShownFirstLine, lines.Count - shown));
        var count = slides ? lines.Count : Math.Min(shown, lines.Count - first);
        if (!slides)
        {
            above = first;
        }
        var caretShown = caretLine >= first && caretLine < first + count; // a caret scrolled out of view isn't drawn
        var visible = new List<string>();
        var offsets = new List<(int Draft, int Shown)>(); // where each shown line starts, in the draft and in the shown text
        var caret = 0;
        for (var i = first; i < first + count; i++)
        {
            var text = CaretLayout.LineText(draft, lines[i]);
            var at = visible.Sum(v => v.Length + 1);
            if (i == caretLine)
            {
                caret = at + Math.Min(column, text.Length);
            }

            offsets.Add((lines[i].Start, at));
            visible.Add(text);
        }

        // A draft position in the shown text (positions outside the shown lines clamp to its start or end).
        int Shown(int position)
        {
            for (var i = 0; i < offsets.Count; i++)
            {
                if (position < offsets[i].Draft)
                {
                    return offsets[i].Shown;
                }

                if (position <= offsets[i].Draft + visible[i].Length)
                {
                    return offsets[i].Shown + position - offsets[i].Draft;
                }
            }

            return visible.Sum(v => v.Length + 1) - (visible.Count > 0 ? 1 : 0);
        }

        var hint = prompts.Focused
            ? "Enter sends, Shift+Enter adds a line, Esc closes:"
            : "Paused: the game has the keyboard. Click the text to keep typing:";
        card.Children.Add(new ViewNode { Type = NodeType.Text, Text = hint, Classes = { "dim" } });
        if (above > 0)
        {
            card.Children.Add(Dim(above == 1 ? "1 more line above" : $"{above} more lines above"));
        }

        var shownText = string.Join("\n", visible.ToArray());
        if (shownText.EndsWith("\n", StringComparison.Ordinal))
        {
            shownText += " "; // text layout leaves out an empty last line: a space keeps it (and the caret on it) in the box
        }

        var node = new ViewNode
        {
            Type = NodeType.Text, TextBox = true, Text = escape(shownText.Length == 0 ? " " : shownText), Classes = { "field", prompts.Focused ? "field-focused" : "field-paused" },
            Command = prompts.Focused ? null : "prompt.focusText", // a click in the paused field resumes typing
            Caret = prompts.Focused && caretShown ? caret : null,
            Window = windowed && slides ? shown : null,
            ScrollBase = first,
            Selection = prompts.Focused && prompts.Selection is { } selected && Shown(selected.Start) < Shown(selected.End) ? (Shown(selected.Start), Shown(selected.End)) : null,
        };
        node.Style["margin-top"] = "$space.2";
        card.Children.Add(node);
        var below = lines.Count - (above + Math.Min(shown, lines.Count));
        if (below > 0)
        {
            card.Children.Add(Dim(below == 1 ? "1 more line below" : $"{below} more lines below"));
        }

        if (lines.Count > shown)
        {
            card.Children.Add(Dim($"{draft.Length} / {PromptRegistry.MaxTextLength} characters"));
        }
    }

    /// <summary>
    /// Puts the waiting prompts' cards into a view's <c>prompt-cards</c> stack (the Activity tab's Questions), so the
    /// expanded overlay shows the same cards as the ones next to the arrow.
    /// </summary>
    public static void Fill(ViewNode view, PromptRegistry prompts, Func<string, string> escape, FieldMetrics? field)
    {
        var target = view.Descendants().FirstOrDefault(n => n.Id == "prompt-cards");
        if (target is null)
        {
            return;
        }

        target.Children.Clear();
        foreach (var prompt in prompts.Pending)
        {
            target.Children.Add(Build(prompt, prompts, escape, field));
        }
    }

    private static ViewNode Dim(string text) => new() { Type = NodeType.Text, Text = text, Classes = { "dim" } };

    private static ViewNode Button(string text, string command, JsonObject? args)
    {
        var button = new ViewNode { Type = NodeType.Button, Text = text, Command = command, Args = args };
        button.Style["margin-top"] = "$space.1";
        return button;
    }
}
