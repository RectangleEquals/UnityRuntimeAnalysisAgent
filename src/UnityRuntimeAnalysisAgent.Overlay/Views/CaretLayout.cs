using System;
using System.Collections.Generic;

namespace UnityRuntimeAnalysisAgent.Overlay.Views;

/// <summary>A renderer's text measurement for a text field, and the field's width (both in the renderer's units).</summary>
public sealed class FieldMetrics
{
    /// <summary>Creates it.</summary>
    public FieldMetrics(double width, Func<string, double> measure, bool slides = false)
    {
        Width = width;
        Measure = measure;
        Slides = slides;
    }

    /// <summary>
    /// Whether the renderer slides the field's text itself (clipped to its window, every frame): it then gets all the
    /// lines, so scrolling never rebuilds anything; otherwise it gets just the window's lines.
    /// </summary>
    public bool Slides { get; }

    /// <summary>The width the field's text wraps at.</summary>
    public double Width { get; }

    /// <summary>The width of one line of text in the field's font.</summary>
    public Func<string, double> Measure { get; }
}

/// <summary>
/// Where a text field's caret goes: word-wrapping a text into lines with a renderer's own text measurement (so line
/// breaks and the caret's position come from the same numbers), finding the caret's line and column, and the blink.
/// </summary>
public static class CaretLayout
{
    /// <summary>The fade-in part of the caret's blink, in seconds.</summary>
    public const double FadeIn = 0.45;

    /// <summary>The fade-out part of the caret's blink, in seconds.</summary>
    public const double FadeOut = 0.55;

    /// <summary>
    /// The caret's opacity <paramref name="sinceChange"/> seconds after the text or the caret last changed: a one-second
    /// squarish bell (a fast rise that flattens near full over 0.45 s, then a fade that holds near full before it drops
    /// over 0.55 s), starting at full so the caret shows at once while typing.
    /// </summary>
    public static float Opacity(double sinceChange)
    {
        if (sinceChange < 0)
        {
            return 1;
        }

        var t = (sinceChange + FadeIn) % (FadeIn + FadeOut);
        if (t < FadeIn)
        {
            var x = t / FadeIn;
            return (float)(1 - Math.Pow(1 - x, 3));
        }

        var y = (t - FadeIn) / FadeOut;
        return (float)(1 - Math.Pow(y, 3));
    }

    /// <summary>
    /// The text broken into lines no wider than <paramref name="width"/>: at line breaks, after spaces, or inside a word
    /// too long for a line. Each line is (start, length) in the text, its trailing spaces and line break included, so
    /// offsets map straight back.
    /// </summary>
    public static List<(int Start, int Length)> Wrap(string text, double width, Func<string, double> measure)
    {
        var lines = new List<(int Start, int Length)>();
        var paragraph = 0;
        while (true)
        {
            var newline = text.IndexOf('\n', paragraph);
            var end = newline < 0 ? text.Length : newline;
            WrapParagraph(text, paragraph, end, width, measure, lines);
            if (newline < 0)
            {
                return lines;
            }

            var (start, length) = lines[lines.Count - 1];
            lines[lines.Count - 1] = (start, length + 1); // the line break belongs to its line
            paragraph = newline + 1;
        }
    }

    /// <summary>A line's text without its line break.</summary>
    public static string LineText(string text, (int Start, int Length) line)
    {
        var length = line.Length > 0 && line.Start + line.Length <= text.Length && text[line.Start + line.Length - 1] == '\n' ? line.Length - 1 : line.Length;
        return text.Substring(line.Start, length);
    }

    /// <summary>The caret's line and column (a caret at a wrap point is at the start of the next line).</summary>
    public static (int Line, int Column) Locate(List<(int Start, int Length)> lines, int caret)
    {
        var line = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Start <= caret)
            {
                line = i;
            }
        }

        return (line, Math.Max(0, Math.Min(caret - lines[line].Start, lines[line].Length)));
    }

    // Lines for text[from..to) (no line breaks inside).
    private static void WrapParagraph(string text, int from, int to, double width, Func<string, double> measure, List<(int Start, int Length)> lines)
    {
        var start = from;
        var end = from; // the line so far is text[start..end)
        var i = from;
        while (i < to)
        {
            // The next word with the spaces after it.
            var wordEnd = i;
            while (wordEnd < to && text[wordEnd] != ' ')
            {
                wordEnd++;
            }

            while (wordEnd < to && text[wordEnd] == ' ')
            {
                wordEnd++;
            }

            if (measure(text.Substring(start, wordEnd - start).TrimEnd(' ')) <= width)
            {
                end = i = wordEnd;
                continue;
            }

            if (end > start)
            {
                lines.Add((start, end - start)); // the word starts the next line
                start = end;
                continue;
            }

            // A word wider than a line: break it at the last character that fits (at least one per line).
            var fit = start + 1;
            while (fit < wordEnd && measure(text.Substring(start, fit + 1 - start).TrimEnd(' ')) <= width)
            {
                fit++;
            }

            lines.Add((start, fit - start));
            start = end = i = fit;
        }

        lines.Add((start, to - start));
    }
}
