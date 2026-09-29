using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Abstractions;
using UnityRuntimeAnalysisAgent.Core.Hosting;

namespace UnityRuntimeAnalysisAgent.Core.Diagnostics;

/// <summary>
/// The unified log: Unity's messages, the loader's (other plugins and mods) and the agent's own, in one ring with a
/// global sequence. Messages are capped at 8 KiB and stacks at 16 KiB (a cut says how much was left out). Writers can be
/// on any thread; a write never blocks for long and never throws.
/// </summary>
public sealed class LogBuffer
{
    public const int MaxMessageBytes = 8 * 1024;
    public const int MaxStackBytes = 16 * 1024;

    private static readonly string[] Levels = { "debug", "info", "warning", "error", "exception", "fatal" };

    [ThreadStatic]
    private static bool t_adding;

    private readonly object _gate = new();
    private readonly LogEntry?[] _ring;
    private readonly Func<(long? Frame, long RealtimeMs)> _clock;
    private long _nextSeq = 1;

    /// <summary>Creates a buffer of <paramref name="capacity"/> entries; <paramref name="clock"/> gives the current frame (null
    /// before the first) and the milliseconds since the game started.</summary>
    public LogBuffer(int capacity, Func<(long? Frame, long RealtimeMs)> clock)
    {
        _ring = new LogEntry?[Math.Max(16, capacity)];
        _clock = clock;
    }

    /// <summary>Raised after an entry is added (on the writer's thread).</summary>
    public event Action<LogEntry>? Added;

    /// <summary>The next sequence number to be assigned.</summary>
    public long NextSeq
    {
        get
        {
            lock (_gate)
            {
                return _nextSeq;
            }
        }
    }

    /// <summary>The oldest sequence number still in the buffer (<see cref="NextSeq"/> when empty).</summary>
    public long OldestSeq
    {
        get
        {
            lock (_gate)
            {
                return Math.Max(1, _nextSeq - _ring.Length);
            }
        }
    }

    /// <summary>The rank of a level (<c>debug</c> = 0 … <c>fatal</c> = 5), or -1 if it isn't one.</summary>
    public static int Rank(string level) => Array.IndexOf(Levels, level);

    /// <summary>A message from Unity's log callback (its type: Log, Warning, Error, Assert or Exception).</summary>
    public void AddUnity(string message, string stackTrace, string type)
    {
        var level = type switch
        {
            "Warning" => "warning",
            "Error" or "Assert" => "error",
            "Exception" => "exception",
            _ => "info",
        };
        Add("unity", "Unity", level, message, level is "error" or "exception" ? stackTrace : null, null);
    }

    /// <summary>A line from the loader's log.</summary>
    public void AddLoader(LoaderLogEntry entry) => Add("loader", entry.Source, LevelOf(entry.Level), entry.Message, null, null);

    /// <summary>A line from the agent's own log.</summary>
    public void AddAgent(AgentLogLevel level, string message, Exception? exception) =>
        Add("agent", "UnityRuntimeAnalysisAgent", LevelOf(level), exception is null ? message : $"{message} ({exception.GetType().Name}: {exception.Message})",
            exception?.ToString(), null);

    /// <summary>Inserts a marker entry; returns its sequence number.</summary>
    public long Mark(string label) => Add("agent", "marker", "info", label, null, label);

    /// <summary>Entries after <paramref name="sinceSeq"/> matching the filters, oldest first, and how many were evicted before
    /// the caller could read them.</summary>
    public (List<LogEntry> Items, long NextSeq, long Dropped) Tail(long sinceSeq, int limit, int minRank, ICollection<string>? sources, ICollection<string>? channels, Regex? regex)
    {
        var snapshot = Snapshot(sinceSeq + 1, long.MaxValue, out var next, out var oldest);
        var dropped = Math.Max(0, oldest - (sinceSeq + 1));
        var items = new List<LogEntry>();
        var last = sinceSeq;
        foreach (var entry in snapshot)
        {
            last = entry.Seq;
            if (Matches(entry, minRank, sources, channels, regex))
            {
                items.Add(entry);
                if (items.Count >= limit)
                {
                    return (items, entry.Seq, dropped);
                }
            }
        }

        return (items, Math.Max(last, next - 1), dropped);
    }

    /// <summary>Entries in [<paramref name="fromSeq"/>, <paramref name="toSeq"/>] whose message (or marker) matches.</summary>
    public List<LogEntry> Search(Regex regex, long fromSeq, long toSeq, int limit) =>
        Snapshot(fromSeq, toSeq, out _, out _).Where(e => regex.IsMatch(e.Message) || (e.Stack is not null && regex.IsMatch(e.Stack))).Take(limit).ToList();

    /// <summary>Whether an entry passes a subscriber's <c>log</c> filter (<c>{minLevel, sources, channels}</c>).</summary>
    public static bool Matches(LogEntry entry, int minRank, ICollection<string>? sources, ICollection<string>? channels, Regex? regex) =>
        Rank(entry.Level) >= minRank
        && (sources is null || sources.Count == 0 || sources.Contains(entry.Source))
        && (channels is null || channels.Count == 0 || channels.Contains(entry.Channel))
        && (regex is null || regex.IsMatch(entry.Message));

    private long Add(string source, string channel, string level, string message, string? stack, string? marker)
    {
        if (t_adding)
        {
            return 0; // a listener that logs while handling an entry: never recurse
        }

        t_adding = true;
        try
        {
            var (frame, realtimeMs) = SafeClock();
            LogEntry entry;
            lock (_gate)
            {
                entry = new LogEntry
                {
                    Seq = _nextSeq++,
                    RealtimeMs = realtimeMs,
                    Frame = frame,
                    Source = source,
                    Channel = channel,
                    Level = level,
                    Message = Cap(message ?? string.Empty, MaxMessageBytes),
                    Stack = string.IsNullOrEmpty(stack) ? null : Cap(stack!, MaxStackBytes),
                    Marker = marker,
                };
                _ring[entry.Seq % _ring.Length] = entry;
            }

            try
            {
                Added?.Invoke(entry);
            }
            catch (Exception)
            {
                // A listener's failure mustn't lose the entry or reach the writer.
            }

            return entry.Seq;
        }
        catch (Exception)
        {
            return 0;
        }
        finally
        {
            t_adding = false;
        }
    }

    private List<LogEntry> Snapshot(long fromSeq, long toSeq, out long next, out long oldest)
    {
        lock (_gate)
        {
            next = _nextSeq;
            oldest = Math.Max(1, _nextSeq - _ring.Length);
            var start = Math.Max(fromSeq, oldest);
            var end = Math.Min(toSeq, _nextSeq - 1);
            var items = new List<LogEntry>((int)Math.Max(0, Math.Min(end - start + 1, _ring.Length)));
            for (var seq = start; seq <= end; seq++)
            {
                if (_ring[seq % _ring.Length] is { } entry && entry.Seq == seq)
                {
                    items.Add(entry);
                }
            }

            return items;
        }
    }

    private (long? Frame, long RealtimeMs) SafeClock()
    {
        try
        {
            return _clock();
        }
        catch (Exception)
        {
            return (null, 0);
        }
    }

    private static string LevelOf(AgentLogLevel level) => level switch
    {
        AgentLogLevel.Debug => "debug",
        AgentLogLevel.Warning => "warning",
        AgentLogLevel.Error => "error",
        _ => "info",
    };

    // Caps a text at a UTF-8 byte budget, saying how much was cut.
    private static string Cap(string text, int maxBytes)
    {
        if (text.Length * 3 <= maxBytes || System.Text.Encoding.UTF8.GetByteCount(text) <= maxBytes)
        {
            return text;
        }

        var total = System.Text.Encoding.UTF8.GetByteCount(text);
        var keep = maxBytes - 64;
        var chars = Math.Min(text.Length, keep);
        while (chars > 0 && System.Text.Encoding.UTF8.GetByteCount(text.Substring(0, chars)) > keep)
        {
            chars -= Math.Max(1, chars / 8);
        }

        if (chars > 0 && char.IsHighSurrogate(text[chars - 1]))
        {
            chars--;
        }

        var kept = text.Substring(0, chars);
        return $"{kept}… [cut: {total - System.Text.Encoding.UTF8.GetByteCount(kept)} more bytes]";
    }
}
