using System;
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
    /// <summary>Whether the blinking caret is showing now (on and off about twice a second).</summary>
    public static bool CaretVisible => Environment.TickCount / 530 % 2 == 0;

    /// <summary>The card's view node; <paramref name="escape"/> is the renderer's text escaping.</summary>
    public static ViewNode Build(Prompt prompt, PromptRegistry prompts, Func<string, string> escape)
    {
        var card = new ViewNode { Type = NodeType.Panel, Id = "prompt-" + prompt.Id, Classes = { "card" } };
        card.Style["margin-top"] = "$space.2";
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
            // The typed text, then a separate blinking caret a hair to its right (never mistaken for a typed "l").
            var field = new ViewNode { Type = NodeType.Stack };
            field.Style["flex-direction"] = "row";
            field.Style["margin-top"] = "$space.2";
            field.Children.Add(new ViewNode { Type = NodeType.Text, Text = escape(prompts.Draft), Classes = { "value" } });
            var caret = new ViewNode { Type = NodeType.Text, Text = "|", Classes = { "value" } };
            caret.Style["margin-left"] = "$space.1";
            caret.Style["color"] = "$color.accent";
            caret.Style["opacity"] = CaretVisible ? "1" : "0";
            field.Children.Add(caret);
            card.Children.Add(new ViewNode { Type = NodeType.Text, Text = "Type your answer, then Send (or Enter):", Classes = { "dim" } });
            card.Children.Add(field);
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

    private static ViewNode Button(string text, string command, JsonObject? args)
    {
        var button = new ViewNode { Type = NodeType.Button, Text = text, Command = command, Args = args };
        button.Style["margin-top"] = "$space.1";
        return button;
    }
}
