using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityLudometry.Protocol.Envelopes;
using UnityLudometry.Protocol.Json;

namespace UnityRuntimeAnalysisAgent.Core.Overlay;

/// <summary>
/// The overlay's way into the agent's services: the same methods clients call, dispatched in-process (source
/// <c>overlay</c>), so every mode check applies. Reads for the views aren't recorded in the Activity feed; actions are.
/// </summary>
public interface IOverlayQueries
{
    /// <summary>Runs a read for a view; <paramref name="done"/> gets the result or the error (on the main thread).</summary>
    void Read(string method, JsonObject parameters, Action<JsonValue?, ProtocolError?> done);

    /// <summary>Runs an action the user took in the overlay (recorded and audited as <c>source: overlay</c>).</summary>
    void Act(string method, JsonObject parameters, Action<JsonValue?, ProtocolError?> done);
}

/// <summary>One read a tab needs: its result goes under <see cref="Key"/> in the tab's data.</summary>
public sealed class TabQuery
{
    /// <summary>Creates the query; <paramref name="parameters"/> returning null skips it this time (e.g. nothing selected).</summary>
    public TabQuery(string key, string method, Func<JsonObject?> parameters)
    {
        Key = key;
        Method = method;
        Parameters = parameters;
    }

    /// <summary>Where the result goes.</summary>
    public string Key { get; }

    /// <summary>The method.</summary>
    public string Method { get; }

    /// <summary>The parameters for this refresh (null: skip).</summary>
    public Func<JsonObject?> Parameters { get; }
}

/// <summary>
/// The tab view models: for each tab, the reads it needs, refreshed at <c>Overlay.RefreshHz</c> while the
/// panel is expanded (only the visible tab), composed with the overlay's own state into one JSON tree that the views
/// bind to by path (<c>status.info.mode</c>, <c>overlay.toasts[0].text</c>…). Nothing refreshes while collapsed or
/// hidden, and a tab never has two refreshes in flight.
/// </summary>
public sealed class OverlayViewModels
{
    private readonly IOverlayQueries _queries;
    private readonly OverlayModel _model;
    private readonly Func<JsonObject> _local;
    private readonly Dictionary<string, IReadOnlyList<TabQuery>> _tabs;
    private readonly Dictionary<string, JsonObject> _data = new();
    private readonly HashSet<string> _inFlight = new();
    private double _nextRefresh;

    /// <summary>Creates the view models.</summary>
    /// <param name="queries">In-process access to the services.</param>
    /// <param name="model">The overlay's state (which tab, expanded or not).</param>
    /// <param name="local">The overlay's own state as JSON (toasts, prompts, selection, lock, E-STOP…), built on demand.</param>
    /// <param name="selectionHandle">The handle of the Inspector's selection, if any.</param>
    public OverlayViewModels(IOverlayQueries queries, OverlayModel model, Func<JsonObject> local, Func<long?> selectionHandle)
    {
        _queries = queries;
        _model = model;
        _local = local;
        _tabs = Tabs(selectionHandle);
    }

    /// <summary>Raised when a tab's data was replaced (with the tab's id).</summary>
    public event Action<string>? Refreshed;

    /// <summary>The tabs and their reads.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<TabQuery>> TabQueries => _tabs;

    /// <summary>How many refreshes ran (for the performance counters: zero while hidden).</summary>
    public long RefreshCount { get; private set; }

    /// <summary>A tab's latest data (empty until its first refresh), with the overlay's own state under <c>overlay</c>.</summary>
    public JsonObject Data(string tab)
    {
        var data = _data.TryGetValue(tab, out var d) ? Copy(d) : new JsonObject();
        data.Set("overlay", _local());
        return data;
    }

    /// <summary>Called every frame with unscaled realtime (seconds): refreshes the visible tab when it's due.</summary>
    public void Tick(double now)
    {
        if (_model.State != OverlayVisibility.Expanded || now < _nextRefresh)
        {
            return;
        }

        _nextRefresh = now + 1.0 / Math.Max(1, _model.Settings.RefreshHz);
        Refresh(_model.Tab);
    }

    /// <summary>Refreshes one tab now (e.g. right after it's selected). Returns false when one is already in flight.</summary>
    public bool Refresh(string tab)
    {
        if (!_tabs.TryGetValue(tab, out var queries) || !_inFlight.Add(tab))
        {
            return false;
        }

        RefreshCount++;
        var result = new JsonObject();
        var pending = queries.Select(q => (Query: q, Params: q.Parameters())).Where(q => q.Params is not null).ToList();
        if (pending.Count == 0)
        {
            Complete(tab, result);
            return true;
        }

        var remaining = pending.Count;
        foreach (var (query, parameters) in pending)
        {
            _queries.Read(query.Method, parameters!, (value, error) =>
            {
                result.Set(query.Key, error is null
                    ? value ?? JsonNull.Instance
                    : new JsonObject { { "error", new JsonObject { { "code", new JsonString(error.Code) }, { "message", new JsonString(error.Message) } } } });
                if (--remaining == 0)
                {
                    Complete(tab, result);
                }
            });
        }

        return true;
    }

    /// <summary>A value at a binding path (<c>info.health.pump.alive</c>, <c>items[2].method</c>) in some data, or null.</summary>
    public static JsonValue? At(JsonValue? root, string path)
    {
        var node = root;
        foreach (var part in path.Split('.'))
        {
            if (node is null || part.Length == 0)
            {
                return null;
            }

            var name = part;
            var bracket = part.IndexOf('[');
            var indices = new List<int>();
            if (bracket >= 0)
            {
                name = part.Substring(0, bracket);
                foreach (var index in part.Substring(bracket).Split(new[] { '[', ']' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out var i))
                    {
                        return null;
                    }

                    indices.Add(i);
                }
            }

            if (name.Length > 0)
            {
                node = node is JsonObject o && o.TryGetValue(name, out var child) ? child : null;
            }

            foreach (var i in indices)
            {
                node = node is JsonArray a && i < a.Count ? a[i] : null;
            }
        }

        return node;
    }

    private void Complete(string tab, JsonObject result)
    {
        _data[tab] = result;
        _inFlight.Remove(tab);
        Refreshed?.Invoke(tab);
    }

    private static JsonObject Copy(JsonObject source)
    {
        var copy = new JsonObject();
        foreach (var pair in source)
        {
            copy.Set(pair.Key, pair.Value);
        }

        return copy;
    }

    private static Dictionary<string, IReadOnlyList<TabQuery>> Tabs(Func<long?> selectionHandle)
    {
        static TabQuery Q(string key, string method, string parameters = "{}") =>
            new(key, method, () => (JsonObject)JsonValue.Parse(parameters));

        return new Dictionary<string, IReadOnlyList<TabQuery>>
        {
            ["status"] = new[] { Q("info", "agent.info"), Q("metrics", "metrics.get"), Q("app", "app.info") },
            ["activity"] = new[] { Q("activity", "activity.list", "{\"limit\":100}"), Q("jobs", "job.list") },
            ["inspector"] = new[]
            {
                Q("scenes", "scene.list"),
                new TabQuery("selection", "obj.inspect", () => selectionHandle() is { } h
                    ? new JsonObject { { "target", new JsonObject { { "h", new JsonNumber(h) } } } }
                    : null),
            },
            ["pinned"] = new[] { Q("vars", "vars.list"), Q("watches", "watch.list") },
            ["instrumentation"] = new[] { Q("hooks", "hook.list"), Q("watches", "watch.list"), Q("patches", "patch.list"), Q("rules", "rule.list"), Q("jobs", "job.list") },
            ["mods"] = new[] { Q("mods", "mod.list"), Q("tests", "test.list") },
            ["logs"] = new[] { Q("logs", "logs.tail", "{\"limit\":200}") },
            ["control"] = new[] { Q("time", "time.info"), Q("app", "app.info"), Q("info", "agent.info") },
            ["settings"] = Array.Empty<TabQuery>(),
        };
    }
}
