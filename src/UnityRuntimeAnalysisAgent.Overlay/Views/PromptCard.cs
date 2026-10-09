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
        if (prompt.Field is { } box)
        {
            var id = new JsonObject { { "id", new JsonString(prompt.Id) } };
            var focused = prompts.Keyboard.Focused == box;
            card.Children.Add(Dim(focused
                ? "Enter sends, Shift+Enter adds a line, Esc closes:"
                : "Paused: the game has the keyboard. Click the text to keep typing:"));
            card.Children.AddRange(TextBoxView.Build(box, focused, escape, field));
            buttons.Children.Add(Button("Send", "prompt.sendText", id));
            buttons.Children.Add(Button("Cancel", "prompt.cancelText", id));
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
