using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityRuntimeAnalysisAgent.Core.Overlay;

/// <summary>A question from the client waiting for the user.</summary>
public sealed class Prompt
{
    internal Prompt(string id, string? title, string message, IReadOnlyList<string> buttons, double shownAt, double? timeoutSeconds, string? attachProbe, string? textButton)
    {
        TextButton = textButton;
        Id = id;
        Title = title;
        Message = message;
        Buttons = buttons;
        ShownAt = shownAt;
        TimeoutSeconds = timeoutSeconds;
        AttachProbe = attachProbe;
    }

    /// <summary>The client's id for it.</summary>
    public string Id { get; }

    /// <summary>The title.</summary>
    public string? Title { get; }

    /// <summary>The message.</summary>
    public string Message { get; }

    /// <summary>The buttons, in order.</summary>
    public IReadOnlyList<string> Buttons { get; }

    /// <summary>When it was shown (unscaled realtime, seconds).</summary>
    public double ShownAt { get; }

    /// <summary>When it times out, or null for never.</summary>
    public double? TimeoutSeconds { get; }

    /// <summary>The probe it belongs to, when the client said so.</summary>
    public string? AttachProbe { get; }

    /// <summary>The button that opens a text field (its answer carries the typed text), or null.</summary>
    public string? TextButton { get; }

    /// <summary>Seconds left, or null without a timeout.</summary>
    public double? Remaining(double now) => TimeoutSeconds is { } t ? Math.Max(0, t - (now - ShownAt)) : null;
}

/// <summary>
/// The prompts waiting for the user. An answer (a button or the timeout, reported as <c>timeout</c>) removes the prompt
/// and raises <see cref="Answered"/> once, which becomes the <c>overlay.promptResult</c> event and feeds the rules'
/// <c>prompt</c> condition. A prompt's text button opens a text field instead (one prompt at a time): typed text goes
/// into <see cref="Draft"/>, and sending it answers with the text button and the text.
/// </summary>
public sealed class PromptRegistry
{
    /// <summary>The answer reported when a prompt times out.</summary>
    public const string Timeout = "timeout";

    private readonly List<Prompt> _pending = new();

    /// <summary>The longest text a prompt accepts.</summary>
    public const int MaxTextLength = 500;

    /// <summary>Raised with (prompt, button, typed text or null) when a prompt is answered or times out.</summary>
    public event Action<Prompt, string, string?>? Answered;

    /// <summary>The prompt whose text field is open, or null.</summary>
    public string? Editing { get; private set; }

    /// <summary>The text typed so far into the open text field.</summary>
    public string Draft { get; private set; } = string.Empty;

    /// <summary>The prompts waiting, oldest first.</summary>
    public IReadOnlyList<Prompt> Pending => _pending;

    /// <summary>Shows a prompt.</summary>
    /// <exception cref="ArgumentException">The id is already waiting, or there are no buttons.</exception>
    public Prompt Show(string id, string? title, string message, IReadOnlyList<string> buttons, double now, int? timeoutMs = null, string? attachProbe = null, string? textButton = null)
    {
        if (_pending.Any(p => p.Id == id))
        {
            throw new ArgumentException($"A prompt with id '{id}' is already waiting.", nameof(id));
        }

        if (buttons.Count == 0 || buttons.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("A prompt needs at least one button, and no empty ones.", nameof(buttons));
        }

        if (buttons.Any(b => b == Timeout))
        {
            throw new ArgumentException($"'{Timeout}' is reserved for prompts that time out.", nameof(buttons));
        }

        if (textButton is not null && !buttons.Contains(textButton))
        {
            throw new ArgumentException($"The text button '{textButton}' isn't one of the buttons.", nameof(textButton));
        }

        var prompt = new Prompt(id, title, message, buttons.ToArray(), now, timeoutMs / 1000.0, attachProbe, textButton);
        _pending.Add(prompt);
        return prompt;
    }

    /// <summary>
    /// The user pressed a button. The text button opens the prompt's text field instead of answering. Returns false when
    /// the prompt or the button doesn't exist.
    /// </summary>
    public bool Answer(string id, string button)
    {
        var prompt = _pending.FirstOrDefault(p => p.Id == id);
        if (prompt is null || !prompt.Buttons.Contains(button))
        {
            return false;
        }

        if (button == prompt.TextButton)
        {
            Editing = id;
            Draft = string.Empty;
            return true;
        }

        Finish(prompt, button, null);
        return true;
    }

    /// <summary>
    /// Typed characters for the open text field (as Unity reports them per frame: backspace <c>\b</c>, Enter
    /// <c>\n</c> or <c>\r</c> sends). Returns whether anything changed.
    /// </summary>
    public bool Type(string? characters)
    {
        if (Editing is null || string.IsNullOrEmpty(characters))
        {
            return false;
        }

        foreach (var c in characters!)
        {
            if (c is '\n' or '\r')
            {
                SendText();
                return true;
            }

            if (c == '\b')
            {
                Draft = Draft.Length > 0 ? Draft.Substring(0, Draft.Length - 1) : Draft;
            }
            else if (!char.IsControl(c) && Draft.Length < MaxTextLength)
            {
                Draft += c;
            }
        }

        return true;
    }

    /// <summary>Answers the prompt whose text field is open with its text button and the typed text.</summary>
    public bool SendText()
    {
        var prompt = _pending.FirstOrDefault(p => p.Id == Editing);
        if (prompt?.TextButton is not { } button)
        {
            return false;
        }

        Finish(prompt, button, Draft);
        return true;
    }

    /// <summary>Closes the open text field without answering (the prompt waits again).</summary>
    public void CancelText()
    {
        Editing = null;
        Draft = string.Empty;
    }

    /// <summary>Times out prompts whose time is up.</summary>
    public void Tick(double now)
    {
        foreach (var prompt in _pending.Where(p => p.Remaining(now) is 0).ToList())
        {
            Finish(prompt, Timeout, null);
        }
    }

    private void Finish(Prompt prompt, string button, string? text)
    {
        _pending.Remove(prompt);
        if (Editing == prompt.Id)
        {
            CancelText();
        }

        Answered?.Invoke(prompt, button, text);
    }
}
