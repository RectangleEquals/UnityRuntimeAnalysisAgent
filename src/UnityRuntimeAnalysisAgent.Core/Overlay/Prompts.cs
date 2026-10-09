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

    /// <summary>The longest text a prompt accepts (room for a pasted multi-paragraph answer).</summary>
    public const int MaxTextLength = 20000;

    /// <summary>Raised with (prompt, button, typed text or null) when a prompt is answered or times out.</summary>
    public event Action<Prompt, string, string?>? Answered;

    /// <summary>The prompt whose text field is open, or null.</summary>
    public string? Editing { get; private set; }

    /// <summary>The text typed so far into the open text field.</summary>
    public string Draft { get; private set; } = string.Empty;

    /// <summary>Where typing goes in <see cref="Draft"/> (0 = before the first character).</summary>
    public int Caret { get; private set; }

    /// <summary>
    /// Whether the open text field takes the keyboard now. A click outside the overlay pauses it (the text stays) so the
    /// game gets the keyboard back; <see cref="Focus"/> resumes.
    /// </summary>
    public bool Focused { get; private set; }

    /// <summary>How many lines of the open text field show at once (the rest scroll).</summary>
    public const int VisibleLines = 8;

    /// <summary>
    /// Where each line of <see cref="Draft"/> starts, as the overlay last laid it out (wrapping included; see
    /// <see cref="SetLines"/>): Up, Down, Page Up, Page Down, Home and End move along these lines.
    /// </summary>
    public IReadOnlyList<int> LineStarts => _lineStarts;

    /// <summary>The first line the open text field shows (kept so the caret's line is in view).</summary>
    public int FirstLine { get; set; }

    /// <summary>
    /// Whether the open text field keeps its caret in view: on after any typing or caret move, off after the field was
    /// scrolled (<see cref="ScrollLines"/>) until the next one.
    /// </summary>
    public bool FollowCaret { get; private set; } = true;

    /// <summary>
    /// The line the field's window starts at as drawn: it eases toward <see cref="FirstLine"/> (see <see cref="Animate"/>),
    /// so scrolling, and bringing the caret back into view, glide instead of jumping.
    /// </summary>
    public double ShownLine { get; private set; }

    /// <summary>The first whole line of the glide (<see cref="ShownLine"/> rounded down); the rest is a fraction of a line.</summary>
    public int ShownFirstLine => (int)Math.Floor(ShownLine);

    /// <summary>Whether the latest mouse-wheel gesture belongs to the text field (set by the overlay's input).</summary>
    public bool WheelOnField { get; set; }

    /// <summary>How long the field's glide to a new scroll position takes, in seconds.</summary>
    public const double GlideSeconds = 0.18;

    private double _glideFrom;
    private int _glideTo;
    private double _glideTime = 1;

    /// <summary>
    /// Moves <see cref="ShownLine"/> toward <see cref="FirstLine"/>: a fixed-length ease-out (cubic) that restarts from
    /// where it is whenever the target changes, so it never jumps.
    /// </summary>
    public void Animate(double deltaSeconds)
    {
        if (FirstLine != _glideTo)
        {
            _glideFrom = ShownLine;
            _glideTo = FirstLine;
            _glideTime = 0;
        }

        _glideTime = Math.Min(1, _glideTime + (deltaSeconds / GlideSeconds));
        var eased = 1 - Math.Pow(1 - _glideTime, 3);
        ShownLine = _glideFrom + ((_glideTo - _glideFrom) * eased);
    }

    /// <summary>How long the wheel must rest before a new gesture can belong to something else, in seconds.</summary>
    public const double WheelGestureGap = 0.67;

    private double _lastWheel = double.NegativeInfinity;

    private bool _ignoreField;

    /// <summary>
    /// Whether a wheel event scrolls the text field. A wheel event with the pointer outside the field sets a flag that
    /// makes the field ignore the wheel; the flag clears once the wheel has rested for <see cref="WheelGestureGap"/>.
    /// With the pointer on the field and no flag, the field gets the event; otherwise what's under the pointer does.
    /// </summary>
    public bool ClaimWheel(bool overField, double now)
    {
        if (now - _lastWheel > WheelGestureGap)
        {
            _ignoreField = false; // the wheel rested: a new gesture
        }

        _lastWheel = now;
        if (!overField)
        {
            _ignoreField = true;
        }

        WheelOnField = overField && !_ignoreField;
        return WheelOnField;
    }

    /// <summary>Scrolls the open text field by some lines (positive = down) without moving the caret.</summary>
    public void ScrollLines(int delta)
    {
        if (Editing is null || delta == 0)
        {
            return;
        }

        FirstLine = Math.Max(0, FirstLine + delta); // the card clamps it to the text
        FollowCaret = false;
    }

    private List<int> _lineStarts = new() { 0 };

    // The column Up and Down keep to across shorter lines (cleared by any other edit or move).
    private int? _column;

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
            Caret = 0;
            Focused = true;
            FirstLine = 0;
            ShownLine = 0;
            _glideFrom = 0;
            _glideTo = 0;
            _glideTime = 1;
            FollowCaret = true;
            SelectionAnchor = null;
            ResetLines();
            return true;
        }

        Finish(prompt, button, null);
        return true;
    }

    /// <summary>
    /// Typed characters for the open text field, inserted at the caret: backspace <c>\b</c>, Enter <c>\n</c> or
    /// <c>\r</c> sends, and the <see cref="EditKeys"/> insert a line break, move the caret or delete. Returns whether
    /// anything changed.
    /// </summary>
    public bool Type(string? characters)
    {
        if (Editing is null || string.IsNullOrEmpty(characters))
        {
            return false;
        }

        FollowCaret = true; // any key brings the caret back into view
        var extend = false;
        foreach (var c in characters!)
        {
            if (c is '\n' or '\r')
            {
                SendText();
                return true;
            }

            if (c == EditKeys.Extend)
            {
                extend = true; // the next caret move extends the selection
                continue;
            }

            var vertical = c is EditKeys.Up or EditKeys.Down or EditKeys.PageUp or EditKeys.PageDown;
            var move = vertical || c is EditKeys.Left or EditKeys.Right or EditKeys.Home or EditKeys.End or EditKeys.DocumentStart or EditKeys.DocumentEnd;
            if (move)
            {
                if (extend)
                {
                    SelectionAnchor ??= Caret;
                }
                else if (Selection is { } selected && c is EditKeys.Left or EditKeys.Right)
                {
                    // Left or Right with a selection: to its start or end, as text editors do.
                    Caret = c == EditKeys.Left ? selected.Start : selected.End;
                    SelectionAnchor = null;
                    _column = null;
                    continue;
                }
                else
                {
                    SelectionAnchor = null;
                }
            }

            switch (c)
            {
                case '\b':
                    if (!DeleteSelection() && Caret > 0)
                    {
                        Draft = Draft.Remove(Caret - 1, 1);
                        Caret--;
                    }

                    break;
                case EditKeys.Delete:
                    if (!DeleteSelection() && Caret < Draft.Length)
                    {
                        Draft = Draft.Remove(Caret, 1);
                    }

                    break;
                case EditKeys.SelectAll:
                    SelectionAnchor = 0;
                    Caret = Draft.Length;
                    break;
                case EditKeys.Left:
                    Caret = Math.Max(0, Caret - 1);
                    break;
                case EditKeys.Right:
                    Caret = Math.Min(Draft.Length, Caret + 1);
                    break;
                case EditKeys.Home:
                    Caret = _lineStarts[LineOf(Caret)];
                    break;
                case EditKeys.End:
                    Caret = LineEnd(LineOf(Caret));
                    break;
                case EditKeys.DocumentStart:
                    Caret = 0;
                    break;
                case EditKeys.DocumentEnd:
                    Caret = Draft.Length;
                    break;
                case EditKeys.Up:
                    MoveLines(-1);
                    break;
                case EditKeys.Down:
                    MoveLines(1);
                    break;
                case EditKeys.PageUp:
                    MoveLines(-VisibleLines);
                    break;
                case EditKeys.PageDown:
                    MoveLines(VisibleLines);
                    break;
                case EditKeys.NewLine:
                    Insert("\n");
                    break;
                default:
                    if (!char.IsControl(c))
                    {
                        Insert(c.ToString());
                    }

                    break;
            }

            if (move && SelectionAnchor == Caret)
            {
                SelectionAnchor = null;
            }

            if (!vertical)
            {
                _column = null;
            }

            extend = false;
        }

        return true;
    }

    /// <summary>Where the selection starts when Shift extends it (the caret is its other end); null without one.</summary>
    public int? SelectionAnchor { get; private set; }

    /// <summary>The selected part of <see cref="Draft"/>, or null when nothing is selected.</summary>
    public (int Start, int End)? Selection => SelectionAnchor is int anchor && anchor != Caret ? (Math.Min(anchor, Caret), Math.Max(anchor, Caret)) : null;

    /// <summary>The selected text, or null.</summary>
    public string? SelectedText => Selection is { } s ? Draft.Substring(s.Start, s.End - s.Start) : null;

    // Removes the selected text (the caret goes where it was). False when nothing was selected.
    private bool DeleteSelection()
    {
        if (Selection is not { } s)
        {
            SelectionAnchor = null;
            return false;
        }

        Draft = Draft.Remove(s.Start, s.End - s.Start);
        Caret = s.Start;
        SelectionAnchor = null;
        ResetLines();
        return true;
    }

    /// <summary>
    /// The overlay's latest layout of <see cref="Draft"/>: where each line starts, wrapping included (the first is 0).
    /// Line moves use the lines shown; until the overlay lays the field out, lines end at line breaks.
    /// </summary>
    public void SetLines(IReadOnlyList<int> starts)
    {
        if (starts.Count > 0 && starts[0] == 0)
        {
            _lineStarts = starts.ToList();
        }
    }

    /// <summary>
    /// Adds pasted text at the caret (line breaks kept, tabs become spaces). Returns whether anything changed.
    /// </summary>
    public bool Paste(string? text)
    {
        if (Editing is null || string.IsNullOrEmpty(text))
        {
            return false;
        }

        var clean = new string(text!.Replace("\r\n", "\n").Replace('\r', '\n').Select(c => c == '\t' ? ' ' : c).Where(c => c == '\n' || !char.IsControl(c)).ToArray());
        var room = MaxTextLength - Draft.Length;
        if (room <= 0 || clean.Length == 0)
        {
            return false;
        }

        Insert(clean.Length > room ? clean.Substring(0, room) : clean);
        FollowCaret = true;
        _column = null;
        return true;
    }

    // Inserts text at the caret, replacing the selection (as much as fits), and keeps the lines usable until the overlay
    // lays the field out again.
    private void Insert(string text)
    {
        DeleteSelection();
        var room = MaxTextLength - Draft.Length;
        if (room <= 0 || text.Length == 0)
        {
            return;
        }

        var insert = text.Length > room ? text.Substring(0, room) : text;
        Draft = Draft.Insert(Caret, insert);
        Caret += insert.Length;
        if (insert.IndexOf('\n') >= 0)
        {
            ResetLines();
        }
    }

    // Lines ending only at line breaks: what Up and Down use until the overlay reports its wrapped layout.
    private void ResetLines()
    {
        _lineStarts = new List<int> { 0 };
        for (var i = 0; i < Draft.Length; i++)
        {
            if (Draft[i] == '\n')
            {
                _lineStarts.Add(i + 1);
            }
        }
    }

    // The line the caret is on (at a wrap point: the next line).
    private int LineOf(int caret)
    {
        var line = 0;
        for (var i = 0; i < _lineStarts.Count && _lineStarts[i] <= caret; i++)
        {
            line = i;
        }

        return line;
    }

    // Where the caret goes for End on a line: before its line break, or before the space it wrapped at.
    private int LineEnd(int line) => line + 1 < _lineStarts.Count ? Math.Max(_lineStarts[line], Math.Min(Draft.Length, _lineStarts[line + 1] - 1)) : Draft.Length;

    // Up/Down (and pages): to the same column on the target line, or its end when it's shorter; the column is kept across
    // shorter lines. Past the first or last line, to the start or the end.
    private void MoveLines(int delta)
    {
        if (_lineStarts.Count == 0 || _lineStarts[_lineStarts.Count - 1] > Draft.Length)
        {
            ResetLines(); // a layout from before the latest edit
        }

        var line = LineOf(Caret);
        var column = _column ?? Caret - _lineStarts[line];
        var target = line + delta;
        if (target < 0)
        {
            Caret = 0;
        }
        else if (target >= _lineStarts.Count)
        {
            Caret = Draft.Length;
        }
        else
        {
            Caret = Math.Min(_lineStarts[target] + column, LineEnd(target));
        }

        _column = column;
    }

    /// <summary>Takes the selected text out of the open text field (for Ctrl+X); null when nothing is selected.</summary>
    public string? Cut()
    {
        if (Editing is null || SelectedText is not { } text)
        {
            return null;
        }

        DeleteSelection();
        _column = null;
        return text;
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
        Caret = 0;
        SelectionAnchor = null;
        Focused = false;
    }

    /// <summary>Pauses the open text field (a click outside the overlay): the game gets the keyboard, the text stays.</summary>
    public void Blur() => Focused = false;

    /// <summary>Resumes typing into the open text field.</summary>
    public void Focus() => Focused = Editing is not null;

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

/// <summary>
/// Editing keys in the text passed to <see cref="PromptRegistry.Type"/> (control characters no keyboard types as text).
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

    /// <summary>Ctrl+A: selects all the text.</summary>
    public const char SelectAll = '\u0015';

    /// <summary>Delete: removes the character after the caret.</summary>
    public const char Delete = '\u007f';
}
