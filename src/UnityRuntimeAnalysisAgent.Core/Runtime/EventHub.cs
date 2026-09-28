using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Hosting;
using UnityRuntimeAnalysisAgent.Core.Transport;

namespace UnityRuntimeAnalysisAgent.Core.Runtime;

/// <summary>
/// Delivers events to the connections subscribed to them. High-rate kinds (<c>log</c>, <c>hook.hits</c>,
/// <c>watch.changes</c>, <c>event.raised</c>, <c>exception</c>) are batched per connection as <c>{items, dropped}</c>:
/// a batch goes out every <c>throttleMs</c> or when it reaches <c>maxBatch</c> items. Events lost to a connection's
/// back-pressure cap leave a gap in <c>seq</c>, and for batched kinds their items are counted in the next batch's
/// <c>dropped</c>; nothing is dropped silently.
/// </summary>
public sealed class EventHub : IDisposable
{
    /// <summary>The kinds delivered in batches.</summary>
    public static readonly IReadOnlyCollection<string> BatchedKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        EventKinds.Log, EventKinds.HookHits, EventKinds.WatchChanges, EventKinds.EventRaised, EventKinds.Exception,
    };

    private readonly Func<IReadOnlyList<Connection>> _connections;
    private readonly IAgentLogger _log;
    private readonly Func<double> _nowMs;
    private readonly object _gate = new();
    private readonly Dictionary<int, Dictionary<string, Subscription>> _subscriptions = new();
    private readonly Dictionary<string, Func<JsonObject, JsonValue, bool>> _filters = new(StringComparer.Ordinal);
    private Timer? _timer;

    /// <summary>Creates the hub over the host's live connections.</summary>
    public EventHub(Func<IReadOnlyList<Connection>> connections, IAgentLogger log, Func<double>? nowMs = null)
    {
        _connections = connections;
        _log = log;
        var clock = Stopwatch.StartNew();
        _nowMs = nowMs ?? (() => clock.Elapsed.TotalMilliseconds);
    }

    /// <summary>Kinds this build emits (reported in capabilities).</summary>
    public ISet<string> EmittedKinds { get; } = new SortedSet<string>(StringComparer.Ordinal);

    /// <summary>Flushes due batches every <paramref name="periodMs"/>.</summary>
    public void Start(int periodMs = 20)
    {
        _timer ??= new Timer(_ => Flush(), null, periodMs, periodMs);
    }

    /// <summary>Registers the filter of a kind: given the subscriber's filter object and an item, whether to deliver it.</summary>
    public void RegisterFilter(string kind, Func<JsonObject, JsonValue, bool> filter)
    {
        lock (_gate)
        {
            _filters[kind] = filter;
        }
    }

    /// <summary>Subscribes a connection. Unknown kinds are reported, not refused.</summary>
    public EventsSubscribeResult Subscribe(Connection connection, EventsSubscribeParams p)
    {
        var known = p.Kinds.Where(k => EventRegistry.Find(k) is not null).Distinct(StringComparer.Ordinal).ToList();
        var unknown = p.Kinds.Where(k => EventRegistry.Find(k) is null).Distinct(StringComparer.Ordinal).ToList();
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(connection.Id, out var kinds))
            {
                _subscriptions[connection.Id] = kinds = new Dictionary<string, Subscription>(StringComparer.Ordinal);
            }

            foreach (var kind in known)
            {
                var filter = p.Filter?[kind] as JsonObject;
                kinds[kind] = new Subscription(p.ThrottleMs ?? 100, (int)(p.MaxBatch ?? 500), filter, _nowMs());
            }
        }

        connection.Subscribe(known);
        return new EventsSubscribeResult { Subscribed = known, Unknown = unknown };
    }

    /// <summary>Unsubscribes (all kinds when null), flushing what's pending first. Returns the kinds removed.</summary>
    public List<string> Unsubscribe(Connection connection, IEnumerable<string>? kinds)
    {
        var removed = connection.Unsubscribe(kinds);
        lock (_gate)
        {
            if (_subscriptions.TryGetValue(connection.Id, out var state))
            {
                foreach (var kind in removed)
                {
                    if (BatchedKinds.Contains(kind) && state.TryGetValue(kind, out var sub))
                    {
                        SendBatch(connection, kind, sub);
                    }

                    state.Remove(kind);
                }
            }
        }

        return removed;
    }

    /// <summary>Sends an event (a non-batched kind) to every subscribed connection.</summary>
    public void Publish(string kind, ProtocolMessage payload, JsonObject? context = null)
    {
        if (EventRegistry.Find(kind) is null)
        {
            throw new ArgumentException($"{kind} is not an event kind of the protocol.", nameof(kind));
        }

        if (BatchedKinds.Contains(kind))
        {
            throw new ArgumentException($"{kind} is delivered in batches: use PublishItem.", nameof(kind));
        }

        var json = payload.ToJson();
        foreach (var connection in _connections())
        {
            if (connection.Authenticated && connection.IsSubscribed(kind))
            {
                connection.SendEvent(kind, json, context);
            }
        }
    }

    /// <summary>Adds an item of a batched kind for every subscribed connection (sent with the next batch).</summary>
    public void PublishItem(string kind, JsonValue item)
    {
        if (!BatchedKinds.Contains(kind))
        {
            throw new ArgumentException($"{kind} isn't a batched kind: use Publish.", nameof(kind));
        }

        Func<JsonObject, JsonValue, bool>? filter;
        lock (_gate)
        {
            _filters.TryGetValue(kind, out filter);
        }

        foreach (var connection in _connections())
        {
            if (!connection.Authenticated || !connection.IsSubscribed(kind))
            {
                continue;
            }

            lock (_gate)
            {
                if (!_subscriptions.TryGetValue(connection.Id, out var state) || !state.TryGetValue(kind, out var sub))
                {
                    continue;
                }

                if (sub.Filter is not null && filter is not null && !SafeFilter(filter, sub.Filter, item))
                {
                    continue;
                }

                sub.Pending.Add(item);
                if (sub.Pending.Count >= sub.MaxBatch)
                {
                    SendBatch(connection, kind, sub);
                }
            }
        }
    }

    /// <summary>Sends every batch whose throttle interval has passed (called by the timer; public for tests).</summary>
    public void Flush(bool all = false)
    {
        var now = _nowMs();
        var connections = _connections().ToDictionary(c => c.Id);
        lock (_gate)
        {
            foreach (var id in _subscriptions.Keys.ToList())
            {
                if (!connections.TryGetValue(id, out var connection) || connection.IsClosed)
                {
                    _subscriptions.Remove(id);
                    continue;
                }

                foreach (var pair in _subscriptions[id])
                {
                    if (!BatchedKinds.Contains(pair.Key))
                    {
                        // Only batches carry a dropped count; other kinds show drops as gaps in seq.
                        connection.TakeDropped(pair.Key);
                        continue;
                    }

                    var sub = pair.Value;
                    sub.CarriedDropped += connection.TakeDropped(pair.Key);
                    if ((sub.Pending.Count > 0 || sub.CarriedDropped > 0) && (all || now - sub.LastSentMs >= sub.ThrottleMs))
                    {
                        SendBatch(connection, pair.Key, sub);
                    }
                }
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        Flush(all: true);
    }

    private void SendBatch(Connection connection, string kind, Subscription sub)
    {
        var items = new JsonArray();
        var count = Math.Min(sub.Pending.Count, sub.MaxBatch);
        foreach (var item in sub.Pending.Take(count))
        {
            items.Add(item);
        }

        sub.Pending.RemoveRange(0, count);
        var dropped = sub.CarriedDropped + connection.TakeDropped(kind);
        sub.CarriedDropped = 0;
        var payload = new JsonObject();
        payload.Add("items", items);
        payload.Add("dropped", new JsonNumber(dropped));
        // If this batch is dropped too, its items and the drops it reports are counted again (never lost).
        connection.SendEvent(kind, payload, null, (int)Math.Min(int.MaxValue, count + dropped));
        sub.LastSentMs = _nowMs();
    }

    private bool SafeFilter(Func<JsonObject, JsonValue, bool> filter, JsonObject subscriberFilter, JsonValue item)
    {
        try
        {
            return filter(subscriberFilter, item);
        }
        catch (Exception e)
        {
            _log.Warning("An event filter failed; delivering the item.", e);
            return true;
        }
    }

    private sealed class Subscription
    {
        public Subscription(long throttleMs, int maxBatch, JsonObject? filter, double nowMs)
        {
            ThrottleMs = throttleMs;
            MaxBatch = Math.Max(1, maxBatch);
            Filter = filter;
            LastSentMs = nowMs;
        }

        public long ThrottleMs { get; }

        public int MaxBatch { get; }

        public JsonObject? Filter { get; }

        public List<JsonValue> Pending { get; } = new();

        public double LastSentMs { get; set; }

        public long CarriedDropped { get; set; }
    }
}
