using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace UnityRuntimeAnalysisAgent.Core.Overlay;

/// <summary>
/// An editable text box, a self-contained control: it owns its text, caret and selection, its layout lines (as the
/// overlay wrapped them) and its scroll position, and edits them (typing, the editing keys, the mouse, the clipboard).
/// It doesn't decide what Enter or Escape mean: it raises <see cref="Submitted"/> and <see cref="Cancelled"/> for its owner
/// (a prompt sends its answer; another owner may do something else). Which box has the keyboard is
/// <see cref="KeyboardFocus"/>'s business.
/// </summary>
public sealed class TextBox
{
    /// <summary>How long a glide to a new scroll position takes, in seconds.</summary>
    public const double GlideSeconds = 0.18;

    /// <summary>The longest gap between two presses at the same place that makes a double click, in seconds.</summary>
    public const double DoubleClickSeconds = 0.5;

    private List<int> _lineStarts = new() { 0 };
    private int? _column; // the column Up and Down keep to across shorter lines (cleared by any other edit or move)
    private int _firstLine;
    private double _glideFrom;
    private double _glideStart = double.NegativeInfinity;
    private double _pressedAt = double.NegativeInfinity; // the last press (Press), for double clicks
    private int _pressedPosition = -1;
    private (int Start, int End)? _pressedWord; // the word a double click selected: a drag then extends by words
    private bool _dragged; // the pointer left the pressed character since the press
    private double? _dragScrollAt; // the last DragBeyond step while the pointer is past the window (null: it isn't)
    private int _dragScrollDirection;
    private double _dragScrollCarry; // lines due but not scrolled yet

    /// <summary>Creates an empty box.</summary>
    public TextBox(int maxLength = 20000, int visibleLines = 8)
    {
        MaxLength = maxLength;
        VisibleLines = visibleLines;
    }

    /// <summary>Seconds on a steady clock, for the scroll glide (replaceable in tests).</summary>
    public static Func<double> Clock { get; set; } = () => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>Raised on Enter (Shift+Enter adds a line instead).</summary>
    public event Action<TextBox>? Submitted;

    /// <summary>Raised on Escape.</summary>
    public event Action<TextBox>? Cancelled;

    /// <summary>The longest text it takes.</summary>
    public int MaxLength { get; }

    /// <summary>How many lines it shows at once (the rest scroll).</summary>
    public int VisibleLines { get; }

    /// <summary>Changes with every change of the text, caret, selection or scroll target (renderers redraw on it).</summary>
    public int Version { get; private set; }

    /// <summary>The text.</summary>
    public string Text { get; private set; } = string.Empty;

    /// <summary>Where typing goes in <see cref="Text"/> (0 = before the first character).</summary>
    public int Caret { get; private set; }

    /// <summary>Where the selection starts when Shift extends it (the caret is its other end); null without one.</summary>
    public int? SelectionAnchor { get; private set; }

    /// <summary>The selected part of <see cref="Text"/>, or null when nothing is selected.</summary>
    public (int Start, int End)? Selection => SelectionAnchor is int anchor && anchor != Caret ? (Math.Min(anchor, Caret), Math.Max(anchor, Caret)) : null;

    /// <summary>The selected text, or null.</summary>
    public string? SelectedText => Selection is { } s ? Text.Substring(s.Start, s.End - s.Start) : null;

    /// <summary>Whether it has more lines than it shows (only then does the mouse wheel scroll it).</summary>
    public bool CanScroll => _lineStarts.Count > VisibleLines;

    /// <summary>
    /// Where each line of <see cref="Text"/> starts, as the overlay last laid it out (wrapping included; see
    /// <see cref="SetLines"/>): Up, Down, Page Up, Page Down, Home, End and the mouse work on these lines.
    /// </summary>
    public IReadOnlyList<int> LineStarts => _lineStarts;

    /// <summary>
    /// The first line the box shows (the scroll target; the drawing glides there, see <see cref="ShownLineAt"/>). The
    /// view keeps the caret's line in view while <see cref="FollowCaret"/>.
    /// </summary>
    public int FirstLine
    {
        get => _firstLine;
        set
        {
            value = Math.Max(0, value);
            if (value == _firstLine)
            {
                return;
            }

            var now = Clock();
            _glideFrom = ShownLineAt(now);
            _glideStart = now;
            _firstLine = value;
            Version++;
        }
    }

    /// <summary>
    /// Whether the view keeps the caret in view: on after any typing or caret move, off after the box was scrolled
    /// (<see cref="ScrollLines"/>) until the next one.
    /// </summary>
    public bool FollowCaret { get; private set; } = true;

    /// <summary>
    /// Where the drawing's window starts at a time (in lines; fractions slide the text): a fixed-length ease-out (cubic)
    /// from wherever it was when <see cref="FirstLine"/> last changed, so scrolling glides instead of jumping.
    /// </summary>
    public double ShownLineAt(double now)
    {
        var t = Math.Max(0, Math.Min(1, (now - _glideStart) / GlideSeconds));
        var eased = 1 - Math.Pow(1 - t, 3);
        return _glideFrom + ((_firstLine - _glideFrom) * eased);
    }

    /// <summary>The first whole line of the drawing's window now (the rest is a fraction of a line).</summary>
    public int ShownFirstLine => (int)Math.Floor(ShownLineAt(Clock()));

    /// <summary>Scrolls by some lines (positive = down) without moving the caret.</summary>
    public void ScrollLines(int delta)
    {
        if (delta == 0)
        {
            return;
        }

        FirstLine += delta; // the view clamps it to the text
        FollowCaret = false;
    }

    /// <summary>
    /// The overlay's latest layout of <see cref="Text"/>: where each line starts, wrapping included (the first is 0). Line
    /// moves and the mouse use the lines shown; until the overlay lays the box out, lines end at line breaks.
    /// </summary>
    public void SetLines(IReadOnlyList<int> starts)
    {
        if (starts.Count > 0 && starts[0] == 0)
        {
            _lineStarts = starts.ToList();
        }
    }

    /// <summary>
    /// Typed characters, inserted at the caret: backspace <c>\b</c>, the <see cref="EditKeys"/> (line break, caret moves,
    /// selection, delete) and Enter (<c>\n</c> or <c>\r</c>), which raises <see cref="Submitted"/> and drops whatever
    /// follows it.
    /// </summary>
    public void Type(string? characters)
    {
        if (string.IsNullOrEmpty(characters))
        {
            return;
        }

        FollowCaret = true; // any key brings the caret back into view
        var extend = false;
        foreach (var c in characters!)
        {
            if (c is '\n' or '\r')
            {
                Version++;
                Submitted?.Invoke(this);
                return;
            }

            if (c == EditKeys.Extend)
            {
                extend = true; // the next caret move extends the selection
                continue;
            }

            Key(c, extend);
            extend = false;
        }

        Version++;
    }

    /// <summary>Escape: raises <see cref="Cancelled"/>.</summary>
    public void Cancel() => Cancelled?.Invoke(this);

    /// <summary>Pasted text at the caret, replacing the selection (line breaks kept, tabs become spaces).</summary>
    public bool Paste(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var clean = new string(text!.Replace("\r\n", "\n").Replace('\r', '\n').Select(c => c == '\t' ? ' ' : c).Where(c => c == '\n' || !char.IsControl(c)).ToArray());
        if (clean.Length == 0)
        {
            return false;
        }

        Insert(clean);
        FollowCaret = true;
        _column = null;
        Version++;
        return true;
    }

    /// <summary>Takes the selected text out (for Ctrl+X); null when nothing is selected.</summary>
    public string? Cut()
    {
        if (SelectedText is not { } text)
        {
            return null;
        }

        DeleteSelection();
        _column = null;
        Version++;
        return text;
    }

    /// <summary>
    /// A mouse press or drag: the caret goes to a line of the layout (<see cref="LineStarts"/>) and a column on it; with
    /// <paramref name="extend"/> (a drag, or Shift) the selection stretches from where it started.
    /// </summary>
    public void PlaceCaret(int line, int column, bool extend)
    {
        if (extend)
        {
            SelectionAnchor ??= Caret;
        }
        else
        {
            SelectionAnchor = null;
        }

        Caret = PositionOf(line, column);
        if (SelectionAnchor == Caret)
        {
            SelectionAnchor = null;
        }

        _column = null;
        Version++;
    }

    /// <summary>
    /// A mouse press on a line of the layout and a column on it: places the caret (<paramref name="extend"/>, Shift,
    /// stretches the selection instead). A second press at the same place within <see cref="DoubleClickSeconds"/> selects
    /// the word there, wherever the caret was. The box counts the clicks itself, so a redraw between the two presses
    /// doesn't break a double click.
    /// </summary>
    public void Press(int line, int column, bool extend)
    {
        var at = PositionOf(line, column);
        var now = Clock();
        var twice = !extend && now - _pressedAt <= DoubleClickSeconds && Math.Abs(at - _pressedPosition) <= 1;
        _pressedAt = twice ? double.NegativeInfinity : now; // a third press starts over
        _pressedPosition = at;
        _dragged = false;
        _pressedWord = null;
        _dragScrollAt = null;
        if (twice)
        {
            SelectWord(line, column);
            _pressedWord = Selection;
            return;
        }

        PlaceCaret(line, column, extend);
    }

    /// <summary>
    /// The pointer dragged, with the button held since <see cref="Press"/>, to a line and column: the selection stretches
    /// from where the press was (by whole words after a double click). Until the pointer leaves the pressed character,
    /// nothing changes (a hand's tremor doesn't undo a double click).
    /// </summary>
    public void DragTo(int line, int column)
    {
        _dragScrollAt = null; // back inside the window
        Drag(line, column);
    }

    /// <summary>
    /// A drag held past the top (<paramref name="linesOutside"/> negative) or the bottom (positive) of the box's window,
    /// by that many line heights; renderers call it every frame while the pointer stays there, moving or not. A box with
    /// more lines than it shows scrolls toward the pointer a line at a time (the first at once, then at
    /// <paramref name="linesPerSecond"/>, which the caller sets by the distance), and the selection stretches to the
    /// window's edge line, at the column
    /// <paramref name="columnOn"/> gives for that line (the pointer's x). It stops at the first or last line.
    /// </summary>
    public void DragBeyond(double linesOutside, double linesPerSecond, Func<int, int> columnOn)
    {
        var direction = Math.Sign(linesOutside);
        if (direction == 0)
        {
            _dragScrollAt = null;
            return;
        }

        var now = Clock();
        if (_dragScrollAt is not { } last || direction != _dragScrollDirection)
        {
            _dragScrollCarry = 1; // the first line at once
        }
        else
        {
            _dragScrollCarry += Math.Max(0, linesPerSecond) * (now - last);
        }

        _dragScrollAt = now;
        _dragScrollDirection = direction;
        var steps = (int)Math.Floor(_dragScrollCarry);
        _dragScrollCarry -= steps;
        if (CanScroll && steps > 0)
        {
            FirstLine = Math.Max(0, Math.Min(_lineStarts.Count - VisibleLines, _firstLine + (direction * steps)));
        }

        var shownLast = Math.Min(_lineStarts.Count - 1, _firstLine + VisibleLines - 1);
        var line = direction < 0 ? Math.Min(_firstLine, shownLast) : shownLast;
        var column = columnOn(line);
        if (PositionOf(line, column) != Caret || steps > 0)
        {
            Drag(line, column);
        }
    }

    private void Drag(int line, int column)
    {
        var at = PositionOf(line, column);
        if (!_dragged && at == _pressedPosition)
        {
            return;
        }

        _dragged = true;
        if (_pressedWord is not { } word)
        {
            PlaceCaret(line, column, extend: true);
            return;
        }

        // By words: the double-clicked word stays selected, and the caret goes to the far end of the word under the
        // pointer.
        var (start, end) = WordAt(at) ?? (at, at);
        if (at < word.Start)
        {
            SelectionAnchor = word.End;
            Caret = start;
        }
        else
        {
            SelectionAnchor = word.Start;
            Caret = Math.Max(end, word.End);
        }

        _column = null;
        Version++;
    }

    /// <summary>A double click: selects the word (or the run of spaces or symbols) there.</summary>
    public void SelectWord(int line, int column)
    {
        var at = PositionOf(line, column);
        Version++;
        _column = null;
        if (WordAt(at) is not { } word)
        {
            Caret = at;
            SelectionAnchor = null;
            return;
        }

        SelectionAnchor = word.Start;
        Caret = word.End;
    }

    // What a character is for word moves: a line break, a space, a word character, or punctuation (a run of each is a
    // word of its own, as in code editors).
    private static int Kind(char c) => c == '\n' ? 0 : char.IsWhiteSpace(c) ? 1 : char.IsLetterOrDigit(c) || c == '_' ? 2 : 3;

    // Ctrl+Right: past the spaces after the caret, then to the end of the word or punctuation run there; at the end of a
    // line, to the start of the next one.
    private int WordEndAfter(int at)
    {
        var start = at;
        while (at < Text.Length && Kind(Text[at]) == 1)
        {
            at++;
        }

        if (at < Text.Length && Text[at] == '\n')
        {
            return at == start ? at + 1 : at;
        }

        if (at < Text.Length)
        {
            var kind = Kind(Text[at]);
            while (at < Text.Length && Kind(Text[at]) == kind)
            {
                at++;
            }
        }

        return at;
    }

    // Ctrl+Left: back past the spaces before the caret, then to the start of the word or punctuation run there; at the
    // start of a line, to the end of the line before.
    private int WordStartBefore(int at)
    {
        var start = at;
        while (at > 0 && Kind(Text[at - 1]) == 1)
        {
            at--;
        }

        if (at > 0 && Text[at - 1] == '\n')
        {
            return at == start ? at - 1 : at;
        }

        if (at > 0)
        {
            var kind = Kind(Text[at - 1]);
            while (at > 0 && Kind(Text[at - 1]) == kind)
            {
                at--;
            }
        }

        return at;
    }

    // The word (or run of spaces or symbols) at a position, within its line; null on an empty line.
    private (int Start, int End)? WordAt(int at)
    {
        bool Word(char c) => char.IsLetterOrDigit(c) || c == '_';
        var probe = at < Text.Length && Text[at] != '\n' ? at : at - 1;
        if (probe < 0 || Text[probe] == '\n')
        {
            return null;
        }

        var kind = Word(Text[probe]);
        var start = probe;
        while (start > 0 && Text[start - 1] != '\n' && Word(Text[start - 1]) == kind)
        {
            start--;
        }

        var end = probe + 1;
        while (end < Text.Length && Text[end] != '\n' && Word(Text[end]) == kind)
        {
            end++;
        }

        return (start, end);
    }

    // One editing key or character.
    private void Key(char c, bool extend)
    {
        var vertical = c is EditKeys.Up or EditKeys.Down or EditKeys.PageUp or EditKeys.PageDown;
        var move = vertical || c is EditKeys.Left or EditKeys.Right or EditKeys.WordLeft or EditKeys.WordRight or EditKeys.Home or EditKeys.End or EditKeys.DocumentStart or EditKeys.DocumentEnd;
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
                return;
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
                    Text = Text.Remove(Caret - 1, 1);
                    Caret--;
                }

                break;
            case EditKeys.Delete:
                if (!DeleteSelection() && Caret < Text.Length)
                {
                    Text = Text.Remove(Caret, 1);
                }

                break;
            case EditKeys.SelectAll:
                SelectionAnchor = 0;
                Caret = Text.Length;
                break;
            case EditKeys.Left:
                Caret = Math.Max(0, Caret - 1);
                break;
            case EditKeys.Right:
                Caret = Math.Min(Text.Length, Caret + 1);
                break;
            case EditKeys.WordLeft:
                Caret = WordStartBefore(Caret);
                break;
            case EditKeys.WordRight:
                Caret = WordEndAfter(Caret);
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
                Caret = Text.Length;
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
    }

    // Removes the selected text (the caret goes where it was). False when nothing was selected.
    private bool DeleteSelection()
    {
        if (Selection is not { } s)
        {
            SelectionAnchor = null;
            return false;
        }

        Text = Text.Remove(s.Start, s.End - s.Start);
        Caret = s.Start;
        SelectionAnchor = null;
        ResetLines();
        return true;
    }

    // Inserts text at the caret, replacing the selection (as much as fits), and keeps the lines usable until the overlay
    // lays the box out again.
    private void Insert(string text)
    {
        DeleteSelection();
        var room = MaxLength - Text.Length;
        if (room <= 0 || text.Length == 0)
        {
            return;
        }

        var insert = text.Length > room ? text.Substring(0, room) : text;
        Text = Text.Insert(Caret, insert);
        Caret += insert.Length;
        if (insert.IndexOf('\n') >= 0)
        {
            ResetLines();
        }
    }

    // Lines ending only at line breaks: what the line moves use until the overlay reports its wrapped layout.
    private void ResetLines()
    {
        _lineStarts = new List<int> { 0 };
        for (var i = 0; i < Text.Length; i++)
        {
            if (Text[i] == '\n')
            {
                _lineStarts.Add(i + 1);
            }
        }
    }

    // The line a position is on (at a wrap point: the next line).
    private int LineOf(int position)
    {
        var line = 0;
        for (var i = 0; i < _lineStarts.Count && _lineStarts[i] <= position; i++)
        {
            line = i;
        }

        return line;
    }

    // Where the caret goes for End on a line: before its line break, or before the space it wrapped at.
    private int LineEnd(int line) => line + 1 < _lineStarts.Count ? Math.Max(_lineStarts[line], Math.Min(Text.Length, _lineStarts[line + 1] - 1)) : Text.Length;

    // Up/Down (and pages): to the same column on the target line, or its end when it's shorter; the column is kept across
    // shorter lines. Past the first or last line, to the start or the end.
    private void MoveLines(int delta)
    {
        if (_lineStarts.Count == 0 || _lineStarts[_lineStarts.Count - 1] > Text.Length)
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
            Caret = Text.Length;
        }
        else
        {
            Caret = Math.Min(_lineStarts[target] + column, LineEnd(target));
        }

        _column = column;
    }

    // A text position from a line of the layout and a column on it (clamped to the line).
    private int PositionOf(int line, int column)
    {
        if (_lineStarts.Count == 0 || _lineStarts[_lineStarts.Count - 1] > Text.Length)
        {
            ResetLines();
        }

        line = Math.Max(0, Math.Min(line, _lineStarts.Count - 1));
        var start = _lineStarts[line];
        var end = line + 1 < _lineStarts.Count ? _lineStarts[line + 1] : Text.Length;
        if (line + 1 < _lineStarts.Count && end > start && (Text[end - 1] == '\n' || Text[end - 1] == ' '))
        {
            end--; // before the line break, or the space the line wrapped at
        }

        return start + Math.Max(0, Math.Min(column, end - start));
    }
}
