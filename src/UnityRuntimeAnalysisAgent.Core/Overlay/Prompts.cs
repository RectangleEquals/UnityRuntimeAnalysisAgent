using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityRuntimeAnalysisAgent.Core.Overlay;

/// <summary>A question from the client waiting for the user.</summary>
public sealed class Prompt
{
    internal Prompt(string id, string? title, string message, IReadOnlyList<string> buttons, double shownAt, double? timeoutSeconds, string? attachProbe)
    {
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

    /// <summary>Seconds left, or null without a timeout.</summary>
    public double? Remaining(double now) => TimeoutSeconds is { } t ? Math.Max(0, t - (now - ShownAt)) : null;
}

/// <summary>
/// The prompts waiting for the user. An answer (a button or the timeout, reported as <c>timeout</c>) removes the prompt
/// and raises <see cref="Answered"/> once, which becomes the <c>overlay.promptResult</c> event and feeds the rules'
/// <c>prompt</c> condition.
/// </summary>
public sealed class PromptRegistry
{
    /// <summary>The answer reported when a prompt times out.</summary>
    public const string Timeout = "timeout";

    private readonly List<Prompt> _pending = new();

    /// <summary>Raised with (prompt, button) when a prompt is answered or times out.</summary>
    public event Action<Prompt, string>? Answered;

    /// <summary>The prompts waiting, oldest first.</summary>
    public IReadOnlyList<Prompt> Pending => _pending;

    /// <summary>Shows a prompt.</summary>
    /// <exception cref="ArgumentException">The id is already waiting, or there are no buttons.</exception>
    public Prompt Show(string id, string? title, string message, IReadOnlyList<string> buttons, double now, int? timeoutMs = null, string? attachProbe = null)
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

        var prompt = new Prompt(id, title, message, buttons.ToArray(), now, timeoutMs / 1000.0, attachProbe);
        _pending.Add(prompt);
        return prompt;
    }

    /// <summary>The user pressed a button. Returns false when the prompt or the button doesn't exist.</summary>
    public bool Answer(string id, string button)
    {
        var prompt = _pending.FirstOrDefault(p => p.Id == id);
        if (prompt is null || !prompt.Buttons.Contains(button))
        {
            return false;
        }

        Finish(prompt, button);
        return true;
    }

    /// <summary>Times out prompts whose time is up.</summary>
    public void Tick(double now)
    {
        foreach (var prompt in _pending.Where(p => p.Remaining(now) is 0).ToList())
        {
            Finish(prompt, Timeout);
        }
    }

    private void Finish(Prompt prompt, string button)
    {
        _pending.Remove(prompt);
        Answered?.Invoke(prompt, button);
    }
}
