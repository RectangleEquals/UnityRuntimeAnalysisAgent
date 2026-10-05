using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityLudometry.Protocol.Json;

namespace UnityRuntimeAnalysisAgent.Core.Overlay;

/// <summary>
/// The tabs' data beyond the raw results: display rows (Activity, Pinned, Inspector members, Logs), the FPS history
/// for the Status sparkline, and the Inspector's actions (scene, select by handle, history, lock, send to the client).
/// </summary>
public sealed partial class OverlayController
{
    private const int FpsHistorySize = 60;
    private readonly Queue<double> _fps = new();

    /// <summary>The object behind a handle (set by the host); throws for an expired handle.</summary>
    public Func<long, object>? ResolveHandle { get; set; }

    /// <summary>Selects an object by handle for the Inspector (from the hierarchy or a list). Returns false when it's gone.</summary>
    public bool SelectHandle(long handle, string label, string? locator = null)
    {
        if (ResolveHandle is null)
        {
            return false;
        }

        object target;
        try
        {
            target = ResolveHandle(handle);
        }
        catch (Exception)
        {
            Toasts.Add($"'{label}' is no longer there.", ToastLevel.Warning, "agent", _lastNow, 4);
            return false;
        }

        Selection.Select(new SelectionEntry(target, label, locator, "overlay"));
        Views.Refresh("inspector");
        return true;
    }

    /// <summary>Shows a scene's hierarchy in the Inspector (null: the active scene).</summary>
    public void SelectScene(string? scene)
    {
        Views.State.InspectorScene = string.IsNullOrEmpty(scene) ? null : scene;
        Views.Refresh("inspector");
    }

    /// <summary>Opens or closes the Inspector lock (opening needs Full mode; the reason is shown otherwise).</summary>
    public void ToggleLock()
    {
        if (!Lock.Locked)
        {
            Lock.Lock();
            return;
        }

        if (!Lock.Unlock())
        {
            Toasts.Add(Lock.WhyLocked ?? "The inspector can't be unlocked now.", ToastLevel.Warning, "agent", _lastNow, 5);
        }
    }

    private readonly List<JsonObject> _testRows = new();
    private string? _testSummary;
    private string? _testJob;

    /// <summary>Records the agent's test events (<c>test.result</c>, <c>test.finished</c>) for the Mods &amp; Tests tab and the report.</summary>
    public void RecordTestEvent(string kind, UnityLudometry.Protocol.Messages.ProtocolMessage payload)
    {
        lock (_testRows)
        {
            RecordTest(payload);
        }
    }

    private void RecordTest(UnityLudometry.Protocol.Messages.ProtocolMessage payload)
    {
        switch (payload)
        {
            case UnityLudometry.Protocol.Messages.TestResultEventParams result:
                if (result.JobId != _testJob)
                {
                    _testJob = result.JobId;
                    _testRows.Clear();
                    _testSummary = "Running…";
                }

                var r = result.Result;
                _testRows.Add(new JsonObject
                {
                    { "status", new JsonString(r.Status) },
                    { "failed", r.Status is "passed" or "skipped" ? JsonBoolean.False : JsonBoolean.True },
                    { "text", new JsonString($"{r.Status,-7} {r.Test.Fixture}.{r.Test.Name} · {r.DurationMs} ms{(string.IsNullOrEmpty(r.Message) ? string.Empty : " · " + r.Message)}") },
                });
                break;
            case UnityLudometry.Protocol.Messages.TestFinishedEventParams finished:
                var t = finished.Totals;
                _testSummary = $"{t.Passed} passed, {t.Failed} failed, {t.Error} errors, {t.Timeout} timed out, {t.Skipped} skipped";
                Toasts.Add("Tests: " + _testSummary, t.Failed + t.Error + t.Timeout > 0 ? ToastLevel.Warning : ToastLevel.Success, "test", _lastNow, 6);
                break;
        }
    }

    /// <summary>The last test run as report lines: the totals, then each failed test with its message.</summary>
    public IReadOnlyList<string> LastTestRun()
    {
        lock (_testRows)
        {
            return _testSummary is null
                ? Array.Empty<string>()
                : new[] { _testSummary }.Concat(_testRows.Where(r => r["failed"] is JsonBoolean { Value: true }).Select(r => ((JsonString)r["text"]!).Value)).ToList();
        }
    }

    private void AddTestState(JsonObject state)
    {
        lock (_testRows)
        {
            state.Set("testSummary", _testSummary is null ? JsonNull.Instance : new JsonString(_testSummary));
            state.Set("testRows", new JsonArray(_testRows.Cast<JsonValue>()));
        }
    }

    /// <summary>Disconnects every client (set by the host; the same as E-STOP's last step).</summary>
    public Action DisconnectClients { get; set; } = () => { };

    /// <summary>Names the selection as a session variable (<c>pickN</c>), so the client can refer to it.</summary>
    public void SendSelection()
    {
        if (Selection.Current is not { Destroyed: false } current)
        {
            Toasts.Add("Nothing selected.", ToastLevel.Info, "agent", _lastNow, 3);
            return;
        }

        var name = Picks.Next(_ => false);
        Queries.Act("vars.set", new JsonObject
        {
            { "name", new JsonString(name) },
            { "target", new JsonObject { { "h", new JsonNumber(_handleOf(current.Target)) } } },
        }, (_, error) => Toasts.Add(error is null ? $"Sent as '{name}': the client can now refer to it by that name." : $"Couldn't send it: {error.Message}",
            error is null ? ToastLevel.Success : ToastLevel.Warning, "agent", _lastNow, 5));
    }

    /// <summary>The Logs tab's level filter.</summary>
    public void SetLogLevel(string level)
    {
        Views.State.LogLevel = level;
        Views.Refresh("logs");
    }

    /// <summary>The Activity tab's filter (<c>all</c>, <c>mutating</c>, <c>errors</c>).</summary>
    public void SetActivityFilter(string filter)
    {
        Views.State.ActivityFilter = filter;
        Views.Refresh("activity");
    }

    // Raw results → what the views show.
    private void Postprocess(string tab, JsonObject data)
    {
        switch (tab)
        {
            case "status":
                if (OverlayViewModels.At(data, "metrics.fps") is JsonNumber fps)
                {
                    _fps.Enqueue(Math.Round(fps.GetDouble(), 1));
                    while (_fps.Count > FpsHistorySize)
                    {
                        _fps.Dequeue();
                    }
                }

                data.Set("fpsHistory", new JsonArray(_fps.Select(v => (JsonValue)new JsonNumber(v))));
                data.Set("memory", new JsonString(Megabytes(OverlayViewModels.At(data, "metrics.gcTotalMemory"))));
                data.Set("frameTime", new JsonString(FrameTime(OverlayViewModels.At(data, "metrics.frameTimeMs"))));
                data.Set("uptime", new JsonString(OverlayViewModels.At(data, "info.uptimeMs") is JsonNumber up ? FormatAgo(up.GetDouble() / 1000).Replace(" ago", string.Empty) : "?"));
                break;
            case "activity":
                data.Set("rows", ActivityRows(OverlayViewModels.At(data, "activity.items") as JsonArray));
                data.Set("jobRows", JobRows(OverlayViewModels.At(data, "jobs.items") as JsonArray));
                break;
            case "inspector":
                data.Set("members", Members(OverlayViewModels.At(data, "selection.value")));
                data.Set("selectionLabel", new JsonString(Selection.Current is { } current ? current.Label + (current.Destroyed ? " (destroyed)" : string.Empty) : "Nothing selected"));
                data.Set("locked", Lock.Locked ? JsonBoolean.True : JsonBoolean.False);
                data.Set("canBack", Selection.CanGoBack ? JsonBoolean.True : JsonBoolean.False);
                data.Set("canForward", Selection.CanGoForward ? JsonBoolean.True : JsonBoolean.False);
                break;
            case "pinned":
                if (OverlayViewModels.At(data, "vars.items") is JsonArray vars)
                {
                    data.Set("varRows", new JsonArray(vars.OfType<JsonObject>().Select(v => (JsonValue)new JsonObject
                    {
                        { "name", v["name"] ?? JsonNull.Instance },
                        { "kind", v["kind"] ?? JsonNull.Instance },
                        { "value", new JsonString(ValueText(v["value"])) },
                    })));
                }

                break;
            case "logs":
                if (OverlayViewModels.At(data, "logs.items") is JsonArray logs)
                {
                    data.Set("rows", new JsonArray(logs.OfType<JsonObject>().Reverse().Select(e => (JsonValue)new JsonObject
                    {
                        { "level", e["level"] ?? JsonNull.Instance },
                        { "source", e["source"] ?? JsonNull.Instance },
                        { "text", new JsonString($"[{Text(e["level"])}] {Text(e["source"])}: {Text(e["message"])}") },
                    })));
                }

                break;
        }
    }

    private JsonArray ActivityRows(JsonArray? items)
    {
        var rows = new JsonArray();
        if (items is null)
        {
            return rows;
        }

        foreach (var item in items.OfType<JsonObject>().Reverse())
        {
            var error = item["errorCode"] is JsonString e ? e.Value : null;
            if (Views.State.ActivityFilter == "errors" && error is null)
            {
                continue;
            }

            var mutating = item["mutating"] is JsonBoolean { Value: true };
            var target = item["target"] is JsonString t ? " " + t.Value : string.Empty;
            var duration = item["durationMs"] is JsonNumber d ? $"{d.GetDouble():0} ms" : "…";
            rows.Add(new JsonObject
            {
                { "id", item["id"] ?? JsonNull.Instance },
                { "mutating", mutating ? JsonBoolean.True : JsonBoolean.False },
                { "error", error is null ? JsonBoolean.False : JsonBoolean.True },
                { "text", new JsonString($"{(mutating ? "● " : string.Empty)}{Text(item["method"])}{target}  ·  {Text(item["source"])}  ·  {duration}{(error is null ? string.Empty : "  ·  " + error)}") },
            });
        }

        return rows;
    }

    private static JsonArray JobRows(JsonArray? items)
    {
        var rows = new JsonArray();
        foreach (var info in items?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            var state = Text(info["state"]);
            var done = OverlayViewModels.At(info, "progress.done") is JsonNumber dn ? dn.GetDouble() : 0;
            var total = OverlayViewModels.At(info, "progress.total") is JsonNumber tn && tn.GetDouble() > 0 ? tn.GetDouble() : 0;
            var progress = state == "succeeded" ? 1 : total > 0 ? Math.Min(1, done / total) : 0;
            var phase = OverlayViewModels.At(info, "progress.phase") is JsonString ph ? " · " + ph.Value : string.Empty;
            rows.Add(new JsonObject
            {
                { "jobId", info["jobId"] ?? JsonNull.Instance },
                { "text", new JsonString($"{Text(info["kind"])} · {state}{phase}") },
                { "progress", new JsonNumber(progress) },
                { "running", state is "running" or "queued" ? JsonBoolean.True : JsonBoolean.False },
            });
        }

        return rows;
    }

    // An inspected object's members as rows (name, value as short text, type).
    private static JsonArray Members(JsonValue? value)
    {
        var rows = new JsonArray();
        var fields = value is JsonObject o ? o["fields"] as JsonObject ?? o["members"] as JsonObject : null;
        if (fields is null)
        {
            if (value is not null && value is not JsonNull)
            {
                rows.Add(new JsonObject { { "name", new JsonString("(value)") }, { "value", new JsonString(ValueText(value)) } });
            }

            return rows;
        }

        foreach (var pair in fields)
        {
            rows.Add(new JsonObject { { "name", new JsonString(pair.Key) }, { "value", new JsonString(ValueText(pair.Value)) } });
        }

        return rows;
    }

    /// <summary>A value in the agent's encoding as one short line (numbers, vectors, objects by type, lists by count, stubs).</summary>
    public static string ValueText(JsonValue? value)
    {
        switch (value)
        {
            case null or JsonNull:
                return "null";
            case JsonString s:
                return "\"" + (s.Value.Length > 80 ? s.Value.Substring(0, 80) + "…" : s.Value) + "\"";
            case JsonNumber n:
                return n.GetDouble().ToString("0.####", CultureInfo.InvariantCulture);
            case JsonBoolean b:
                return b.Value ? "true" : "false";
            case JsonArray a:
                return $"[{a.Count} items]";
            case JsonObject o when o["redacted"] is JsonObject r:
                return $"… ({Text(r["reason"])}, ref {Text(r["ref"])})";
            case JsonObject o when o["enum"] is not null:
                return Text(o["name"]) is { Length: > 0 } name ? name : Text(o["value"]);
            case JsonObject o:
                var t = Text(o["t"]);
                if (t is "Vector2" or "Vector3" or "Vector4" or "Quaternion")
                {
                    var parts = new[] { "x", "y", "z", "w" }.Where(k => o[k] is JsonNumber).Select(k => ((JsonNumber)o[k]!).GetDouble().ToString("0.###", CultureInfo.InvariantCulture));
                    return "(" + string.Join(", ", parts) + ")";
                }

                if (t == "list")
                {
                    return $"[{Text(o["count"])} items]";
                }

                var unity = OverlayViewModels.At(o, "unity.name") is JsonString u ? $" '{u.Value}'" : string.Empty;
                var type = OverlayViewModels.At(o, "type.name") is JsonString tn ? tn.Value : t;
                return type + unity;
            default:
                return value.ToString() ?? string.Empty;
        }
    }

    private static string Text(JsonValue? value) => value switch
    {
        null or JsonNull => string.Empty,
        JsonString s => s.Value,
        JsonNumber n => n.GetDouble().ToString("0.###", CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    // metrics.get's frame times ({mean, p95, max} ms) as one line.
    private static string FrameTime(JsonValue? times)
    {
        static string Ms(JsonValue? v) => v is JsonNumber n ? n.GetDouble().ToString("0.0", CultureInfo.InvariantCulture) : "?";
        return times switch
        {
            JsonObject o => $"{Ms(o["mean"])} ms (p95 {Ms(o["p95"])} · max {Ms(o["max"])})",
            JsonNumber n => Ms(n) + " ms",
            _ => "? ms",
        };
    }

    private static string Megabytes(JsonValue? bytes) => bytes is JsonNumber n ? $"{n.GetDouble() / (1024 * 1024):0.0} MiB" : "?";
}
