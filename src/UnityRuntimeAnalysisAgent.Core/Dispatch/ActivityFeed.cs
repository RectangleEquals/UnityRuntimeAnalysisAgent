using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Hosting;

namespace UnityRuntimeAnalysisAgent.Core.Dispatch;

/// <summary>
/// A ring of the agent's recent activity: every call from a client (and later the overlay and rules), with who made it,
/// how long it took and how it ended. Mutating calls are also written to the agent log as <c>[audit]</c> lines. The full
/// request and response of each entry are kept, with large parts replaced by redaction stubs.
/// </summary>
public sealed class ActivityFeed
{
    /// <summary>Largest request or response kept as-is (serialized bytes); larger ones become a stub.</summary>
    public const int MaxDetailBytes = 4096;

    private readonly int _capacity;
    private readonly IAgentLogger _log;
    private readonly object _gate = new();
    private readonly LinkedList<Stored> _records = new();
    private long _nextId;

    /// <summary>Creates the feed, keeping the last <paramref name="capacity"/> entries.</summary>
    public ActivityFeed(int capacity, IAgentLogger log)
    {
        _capacity = Math.Max(1, capacity);
        _log = log;
    }

    /// <summary>Records one finished call and returns its entry.</summary>
    public ActivityEntry Record(
        string method,
        string source,
        string? client,
        bool mutating,
        DateTime startedUtc,
        long durationMs,
        JsonObject? request,
        JsonObject response,
        string? errorCode,
        string? target = null,
        AuditedAssembly? assembly = null)
    {
        ActivityEntry entry;
        lock (_gate)
        {
            entry = new ActivityEntry
            {
                Id = ++_nextId,
                Method = method,
                Source = source,
                Client = client,
                Mutating = mutating,
                StartedAt = startedUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                DurationMs = durationMs,
                Target = target,
                ErrorCode = errorCode,
                Assembly = assembly,
            };
            _records.AddLast(new Stored(entry, Summarize(request ?? new JsonObject()), Summarize(response)));
            while (_records.Count > _capacity)
            {
                _records.RemoveFirst();
            }
        }

        if (mutating)
        {
            _log.Info($"[audit] #{entry.Id} {method} by {client ?? source}: {errorCode ?? "ok"} in {durationMs} ms"
                + (target is null ? string.Empty : $" on {target}") + (assembly is null ? string.Empty : $" ({assembly.Name} {assembly.Sha256})"));
        }

        return entry;
    }

    /// <summary>Entries after <paramref name="sinceId"/>, oldest first, at most <paramref name="limit"/>; and how many matched.</summary>
    public (IReadOnlyList<ActivityEntry> Items, long Total) List(bool mutatingOnly, long sinceId, int limit)
    {
        lock (_gate)
        {
            var matching = _records.Select(r => r.Entry).Where(e => e.Id > sinceId && (!mutatingOnly || e.Mutating)).ToList();
            return (matching.Take(Math.Max(1, limit)).ToList(), matching.Count);
        }
    }

    /// <summary>One entry with its request and response, or null if it was evicted.</summary>
    public (ActivityEntry Entry, JsonObject Request, JsonObject Response)? Get(long id)
    {
        lock (_gate)
        {
            var record = _records.FirstOrDefault(r => r.Entry.Id == id);
            return record is null ? null : (record.Entry, record.Request, record.Response);
        }
    }

    private static JsonObject Summarize(JsonObject value)
    {
        var bytes = value.ToUtf8Bytes().Length;
        if (bytes <= MaxDetailBytes)
        {
            return value;
        }

        var size = new JsonObject();
        size.Add("estBytes", new JsonNumber(bytes));
        var info = new JsonObject();
        info.Add("reason", new JsonString("maxBytes"));
        info.Add("size", size);
        var stub = new JsonObject();
        stub.Add("redacted", info);
        return stub;
    }

    private sealed class Stored
    {
        public Stored(ActivityEntry entry, JsonObject request, JsonObject response)
        {
            Entry = entry;
            Request = request;
            Response = response;
        }

        public ActivityEntry Entry { get; }

        public JsonObject Request { get; }

        public JsonObject Response { get; }
    }
}
