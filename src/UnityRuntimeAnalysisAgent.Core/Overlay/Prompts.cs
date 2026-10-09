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

    /// <summary>Its answer's text box, once its text button opened it (null before, and after Cancel).</summary>
    public TextBox? Field { get; internal set; }

    /// <summary>Seconds left, or null without a timeout.</summary>
    public double? Remaining(double now) => TimeoutSeconds is { } t ? Math.Max(0, t - (now - ShownAt)) : null;
}

/// <summary>
/// The prompts waiting for the user, and their answers. An answer (a button or the timeout, reported as
/// <c>timeout</c>) removes the prompt and raises <see cref="Answered"/> once, which becomes the
/// <c>overlay.promptResult</c> event and feeds the rules' <c>prompt</c> condition. A prompt's text button opens its text
/// box instead (<see cref="Prompt.Field"/>): Enter in it sends the typed text as the answer, Escape closes it.
/// </summary>
public sealed class PromptRegistry
{
    /// <summary>The answer reported when a prompt times out.</summary>
    public const string Timeout = "timeout";

    private readonly List<Prompt> _pending = new();

    /// <summary>Creates it; its text boxes take the keyboard through <paramref name="keyboard"/>.</summary>
    public PromptRegistry(KeyboardFocus? keyboard = null)
    {
        Keyboard = keyboard ?? new KeyboardFocus();
    }

    /// <summary>Raised with (prompt, button, typed text or null) when a prompt is answered or times out.</summary>
    public event Action<Prompt, string, string?>? Answered;

    /// <summary>The overlay's keyboard focus (an opened text box takes it).</summary>
    public KeyboardFocus Keyboard { get; }

    /// <summary>The prompts waiting, oldest first.</summary>
    public IReadOnlyList<Prompt> Pending => _pending;

    /// <summary>The waiting prompts whose text box is open.</summary>
    public IEnumerable<Prompt> WithFields => _pending.Where(p => p.Field is not null);

    /// <summary>A waiting prompt's open text box, or null.</summary>
    public TextBox? FieldOf(string id) => _pending.FirstOrDefault(p => p.Id == id)?.Field;

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
    /// The user pressed a button. The text button opens the prompt's text box (or returns to it, text kept) and gives it
    /// the keyboard, instead of answering. Returns false when the prompt or the button doesn't exist.
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
            prompt.Field ??= Open(prompt);
            Keyboard.Focus(prompt.Field);
            return true;
        }

        Finish(prompt, button, null);
        return true;
    }

    /// <summary>Answers a prompt with its text button and its text box's text (Enter in the box does this).</summary>
    public bool SendText(string id)
    {
        var prompt = _pending.FirstOrDefault(p => p.Id == id);
        if (prompt?.TextButton is not { } button || prompt.Field is not { } box)
        {
            return false;
        }

        Finish(prompt, button, box.Text);
        return true;
    }

    /// <summary>Closes a prompt's text box without answering (Escape in the box does this); the prompt waits again.</summary>
    public void CancelText(string id)
    {
        if (_pending.FirstOrDefault(p => p.Id == id) is { Field: { } box } prompt)
        {
            Keyboard.Release(box);
            prompt.Field = null;
        }
    }

    /// <summary>Times out prompts whose time is up.</summary>
    public void Tick(double now)
    {
        foreach (var prompt in _pending.Where(p => p.Remaining(now) is 0).ToList())
        {
            Finish(prompt, Timeout, null);
        }
    }

    // A prompt's text box: Enter answers the prompt with the text, Escape closes the box.
    private TextBox Open(Prompt prompt)
    {
        var box = new TextBox();
        box.Submitted += _ => SendText(prompt.Id);
        box.Cancelled += _ => CancelText(prompt.Id);
        return box;
    }

    private void Finish(Prompt prompt, string button, string? text)
    {
        CancelText(prompt.Id);
        _pending.Remove(prompt);
        Answered?.Invoke(prompt, button, text);
    }
}

/// <summary>
/// Editing keys in the text passed to <see cref="TextBox.Type"/> (control characters no keyboard types as text).
/// </summary>
public static class EditKeys
{
    /// <summary>Home: the caret to the start of its line.</summary>
    public const char Home = '\u0001';

    /// <summary>Left arrow.</summary>
    public const char Left = '\u0002';

    /// <summary>End: the caret to the end of its line.</summary>
    public const char End = '\u0005';

    /// <summary>Right arrow.</summary>
    public const char Right = '\u0006';

    /// <summary>Up arrow: the line above.</summary>
    public const char Up = '\u000e';

    /// <summary>Down arrow: the line below.</summary>
    public const char Down = '\u000f';

    /// <summary>Page Up: a field's height of lines up.</summary>
    public const char PageUp = '\u0010';

    /// <summary>Page Down: a field's height of lines down.</summary>
    public const char PageDown = '\u0011';

    /// <summary>Ctrl+Home: the caret to the start of the text.</summary>
    public const char DocumentStart = '\u0012';

    /// <summary>Ctrl+End: the caret to the end of the text.</summary>
    public const char DocumentEnd = '\u0013';

    /// <summary>Shift+Enter: a line break (Enter alone sends).</summary>
    public const char NewLine = '\u000b';

    /// <summary>A prefix: the caret move after it extends the selection (Shift held).</summary>
    public const char Extend = '\u0014';

    /// <summary>Ctrl+Left: the caret to the start of the word before it (punctuation runs count as words).</summary>
    public const char WordLeft = '\u0016';

    /// <summary>Ctrl+Right: the caret to the end of the word after it (punctuation runs count as words).</summary>
    public const char WordRight = '\u0017';

    /// <summary>Ctrl+A: selects all the text.</summary>
    public const char SelectAll = '\u0015';

    /// <summary>Delete: removes the character after the caret.</summary>
    public const char Delete = '\u007f';
}
