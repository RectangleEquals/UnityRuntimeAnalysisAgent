using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityLudometry.Protocol.Json;

namespace UnityRuntimeAnalysisAgent.Core.Overlay;

/// <summary>A connected client, as the report lists it.</summary>
public sealed class ReportClient
{
    /// <summary>Its name and version.</summary>
    public string Name { get; set; } = "";

    /// <summary>When it connected.</summary>
    public DateTime ConnectedUtc { get; set; }

    /// <summary>Messages received and sent.</summary>
    public long Messages { get; set; }

    /// <summary>Bytes received and sent.</summary>
    public long Bytes { get; set; }

    /// <summary>Events dropped for it.</summary>
    public long DroppedEvents { get; set; }
}

/// <summary>
/// What the Copy report is made from: the same results the tabs read (protocol JSON), plus what only the host knows.
/// The session token is never an input.
/// </summary>
public sealed class ReportInputs
{
    /// <summary>When the report is made.</summary>
    public DateTime NowUtc { get; set; } = DateTime.UtcNow;

    /// <summary><c>agent.info</c>.</summary>
    public JsonValue? Info { get; set; }

    /// <summary><c>metrics.get</c>.</summary>
    public JsonValue? Metrics { get; set; }

    /// <summary>The connected clients.</summary>
    public IReadOnlyList<ReportClient> Clients { get; set; } = Array.Empty<ReportClient>();

    /// <summary><c>activity.list</c> (everything the feed holds, newest last).</summary>
    public JsonValue? Activity { get; set; }

    /// <summary><c>job.list</c>.</summary>
    public JsonValue? Jobs { get; set; }

    /// <summary><c>hook.list</c>, <c>watch.list</c>, <c>patch.list</c>, <c>rule.list</c>.</summary>
    public JsonValue? Hooks { get; set; }

    /// <inheritdoc cref="Hooks"/>
    public JsonValue? Watches { get; set; }

    /// <inheritdoc cref="Hooks"/>
    public JsonValue? Patches { get; set; }

    /// <inheritdoc cref="Hooks"/>
    public JsonValue? Rules { get; set; }

    /// <summary><c>mod.list</c>.</summary>
    public JsonValue? Mods { get; set; }

    /// <summary>The last test run's summary line and failed tests (message each), if any.</summary>
    public IReadOnlyList<string> LastTestRun { get; set; } = Array.Empty<string>();

    /// <summary><c>vars.list</c>.</summary>
    public JsonValue? Vars { get; set; }

    /// <summary>The Inspector's selection: its label and locator, and <c>obj.inspect</c> of it (depth 1).</summary>
    public string? SelectionLabel { get; set; }

    /// <inheritdoc cref="SelectionLabel"/>
    public string? SelectionLocator { get; set; }

    /// <inheritdoc cref="SelectionLabel"/>
    public JsonValue? Selection { get; set; }

    /// <summary>The last captures: path, frame and rule id.</summary>
    public IReadOnlyList<string> Captures { get; set; } = Array.Empty<string>();

    /// <summary>How many warnings and errors the log buffer holds, the last error lines (messages only) and its seq range.</summary>
    public long WarningCount { get; set; }

    /// <inheritdoc cref="WarningCount"/>
    public IReadOnlyList<string> LastErrors { get; set; } = Array.Empty<string>();

    /// <inheritdoc cref="WarningCount"/>
    public string? LogSeqRange { get; set; }

    /// <summary>Where the logs are (the loader's log, the player log), instead of their contents.</summary>
    public IReadOnlyList<string> LogPaths { get; set; } = Array.Empty<string>();

    /// <summary>Settings that differ from their defaults (key → value).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> ChangedSettings { get; set; } = Array.Empty<KeyValuePair<string, string>>();
}

/// <summary>
/// The Copy report: a Markdown summary for pasting into an issue or a chat. Large or deep values stay
/// redaction stubs; every stub is listed in the Redactions table and its ref pinned, so the reader can expand exactly
/// that item later (<c>value.expand</c>). Log contents, payloads and image data are identified, never inlined; the
/// session token is never included.
/// </summary>
public static class CopyReport
{
    /// <summary>Mutations and executions listed in full (older ones are counted and given as an id range).</summary>
    public const int MaxMutations = 200;

    /// <summary>Recent requests listed.</summary>
    public const int MaxRecent = 50;

    /// <summary>Finished jobs listed.</summary>
    public const int MaxFinishedJobs = 20;

    /// <summary>Captures listed.</summary>
    public const int MaxCaptures = 10;

    /// <summary>Error lines listed.</summary>
    public const int MaxErrors = 10;

    /// <summary>Inline values longer than this become a note (their stub, if any, is in the table).</summary>
    public const int MaxInlineChars = 1024;

    private static readonly string[] SecretNames = { "token", "sessionToken", "secret" };

    /// <summary>Builds the report. <paramref name="pin"/> is called for every ref cited, so it stays expandable.</summary>
    public static string Build(ReportInputs inputs, Action<string> pin)
    {
        var stubs = new List<(string Where, JsonObject Stub)>();
        var md = new StringBuilder();
        var info = inputs.Info as JsonObject;
        md.AppendLine("# UnityRuntimeAnalysisAgent report").AppendLine();
        md.AppendLine($"- Time: {inputs.NowUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC");
        md.AppendLine($"- Agent {S(info, "agentVersion")} (commit {S(info, "gitCommit")}), protocol {Text(Get(info, "protocol"))}");
        md.AppendLine($"- Unity {S(info, "unityVersion")}, {S(info, "scriptingBackend")}, {S(info, "platform")}; loader {S(Get(info, "loader"), "name")} {S(Get(info, "loader"), "version")}");
        md.AppendLine($"- Mode: **{S(info, "mode")}**, transport {S(info, "transport")}, up {Ms(Get(info, "uptimeMs"))}").AppendLine();

        md.AppendLine("## Connection");
        if (inputs.Clients.Count == 0)
        {
            md.AppendLine("No client connected.");
        }

        foreach (var c in inputs.Clients)
        {
            md.AppendLine($"- {c.Name}: connected {c.ConnectedUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture)} UTC, {c.Messages} messages, {c.Bytes} bytes, {c.DroppedEvents} dropped events");
        }

        md.AppendLine().AppendLine("## Health");
        var pump = Get(Get(info, "health"), "pump");
        var metrics = inputs.Metrics as JsonObject;
        md.AppendLine($"- Pump: alive {S(pump, "alive")}, queue {S(pump, "queueLength")}, stalled {S(pump, "stalledMs")} ms, recreated {S(pump, "recreatedCount")}x");
        md.AppendLine($"- Frames: {S(metrics, "fps")} fps, frame time mean {S(Get(metrics, "frameTimeMs"), "mean")} / p95 {S(Get(metrics, "frameTimeMs"), "p95")} ms");
        md.AppendLine($"- Memory: GC {Bytes(Get(metrics, "gcTotalMemory"))}, Mono used {Bytes(Get(metrics, "monoUsed"))}, working set {Bytes(Get(metrics, "processWorkingSet"))}").AppendLine();

        Activity(md, Items(inputs.Activity));
        Jobs(md, Items(inputs.Jobs));

        md.AppendLine("## Instrumentation");
        md.AppendLine($"- Hooks ({Items(inputs.Hooks).Count}): " + List(Items(inputs.Hooks), h => $"{Name(Get(h, "method"))} {S(h, "hits")} hits"));
        md.AppendLine($"- Watches ({Items(inputs.Watches).Count}): " + List(Items(inputs.Watches), w => $"{S(w, "watchId")} {S(w, "changes")} changes"));
        md.AppendLine($"- Rules ({Items(inputs.Rules).Count}): " + List(Items(inputs.Rules), r => $"{S(r, "ruleId")} {S(r, "state")}, fired {S(r, "fired")}"));
        md.AppendLine($"- Live patch sets ({Items(inputs.Patches).Count}): " + List(Items(inputs.Patches), p => $"{S(p, "patchSetId")} → " + List(Items(Get(p, "targets")), Name)));
        md.AppendLine();

        md.AppendLine("## Mods & tests");
        md.AppendLine($"- Mods ({Items(inputs.Mods).Count}): " + List(Items(inputs.Mods), m => $"{S(m, "name")} {S(m, "version")} ({S(m, "source")})"));
        md.AppendLine("- Last test run: " + (inputs.LastTestRun.Count == 0 ? "none" : string.Join("; ", inputs.LastTestRun)));
        md.AppendLine();

        md.AppendLine("## Pinned");
        var vars = Items(inputs.Vars);
        if (vars.Count == 0)
        {
            md.AppendLine("Nothing pinned.");
        }

        foreach (var v in vars)
        {
            md.AppendLine($"- `{S(v, "name")}`: {Value(Get(v, "value"), "vars." + S(v, "name"), stubs)}");
        }

        md.AppendLine().AppendLine("## Current selection");
        if (inputs.SelectionLabel is null)
        {
            md.AppendLine("Nothing selected.");
        }
        else
        {
            md.AppendLine($"- {inputs.SelectionLabel}{(inputs.SelectionLocator is null ? "" : $" (`{inputs.SelectionLocator}`)")}");
            var value = Get(inputs.Selection, "value");
            md.AppendLine($"  - Type: {Name(Get(value, "type"))}");
            foreach (var group in new[] { "fields", "props" })
            {
                if (Get(value, group) is JsonObject members)
                {
                    foreach (var member in members)
                    {
                        md.AppendLine($"  - {member.Key}: {Value(member.Value, "selection." + member.Key, stubs)}");
                    }
                }
            }
        }

        md.AppendLine().AppendLine("## Captures");
        md.AppendLine(inputs.Captures.Count == 0 ? "None." : string.Join("\n", inputs.Captures.Reverse().Take(MaxCaptures).Reverse().Select(c => "- " + c)));
        md.AppendLine().AppendLine("## Warnings");
        md.AppendLine($"{inputs.WarningCount} warning(s) and error(s) in the log buffer{(inputs.LogSeqRange is null ? "" : $" (seq {inputs.LogSeqRange}; `logs.tail` / `logs.search` for the lines)")}.");
        foreach (var line in inputs.LastErrors.Reverse().Take(MaxErrors).Reverse())
        {
            md.AppendLine("- " + OneLine(line));
        }

        md.AppendLine().AppendLine("## Log files");
        md.AppendLine(inputs.LogPaths.Count == 0 ? "Unknown." : string.Join("\n", inputs.LogPaths.Select(p => $"- `{p}`")));
        md.AppendLine().AppendLine("## Settings (changed from the defaults)");
        md.AppendLine(inputs.ChangedSettings.Count == 0 ? "All defaults." : string.Join("\n", inputs.ChangedSettings.Select(s => $"- {s.Key} = {s.Value}")));

        md.AppendLine().AppendLine("## Redactions");
        md.AppendLine("The session token and the discovery file are never included (policy).");
        if (stubs.Count > 0)
        {
            md.AppendLine().AppendLine("| # | name / path | type | locator | reason | size | ref |").AppendLine("|---|---|---|---|---|---|---|");
            var n = 0;
            foreach (var (where, stub) in stubs)
            {
                var reference = S(stub, "ref");
                if (reference.Length > 0)
                {
                    pin(reference);
                }

                md.AppendLine($"| {++n} | {Cell(where)} | {Cell(Name(Get(stub, "type")))} | {Cell(Text(Get(stub, "locator")))} | {Cell(S(stub, "reason"))} | {Cell(Size(Get(stub, "size")))} | `{reference}` |");
            }
        }

        return md.ToString();
    }

    private static void Activity(StringBuilder md, IReadOnlyList<JsonObject> items)
    {
        md.AppendLine("## Activity");
        md.AppendLine("By method: " + (items.Count == 0 ? "none" : string.Join(", ", items.GroupBy(i => S(i, "method")).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} ×{g.Count()}"))));
        var mutations = items.Where(i => Get(i, "mutating") is JsonBoolean { Value: true } || Get(i, "assembly") is JsonObject).ToList();
        md.AppendLine().AppendLine($"Mutations and executions ({mutations.Count}):");
        if (mutations.Count > MaxMutations)
        {
            var older = mutations.Take(mutations.Count - MaxMutations).ToList();
            md.AppendLine($"- {older.Count} older ones, ids {S(older[0], "id")}–{S(older[older.Count - 1], "id")} (`activity.get`)");
        }

        foreach (var m in mutations.Skip(Math.Max(0, mutations.Count - MaxMutations)))
        {
            md.AppendLine($"- #{S(m, "id")} {S(m, "startedAt")} {S(m, "method")} ({S(m, "source")}){Target(m)}{Asm(m)} → {(S(m, "errorCode") is { Length: > 0 } code ? code : "ok")}");
        }

        md.AppendLine().AppendLine($"Last {Math.Min(MaxRecent, items.Count)} requests:");
        foreach (var r in items.Skip(Math.Max(0, items.Count - MaxRecent)))
        {
            md.AppendLine($"- #{S(r, "id")} {S(r, "method")} ({S(r, "source")}) {S(r, "durationMs")} ms → {(S(r, "errorCode") is { Length: > 0 } code ? code : "ok")}");
        }

        md.AppendLine();
    }

    private static void Jobs(StringBuilder md, IReadOnlyList<JsonObject> items)
    {
        md.AppendLine("## Jobs");
        var running = items.Where(j => S(j, "state") is "queued" or "running").ToList();
        var finished = items.Except(running).ToList();
        md.AppendLine($"Running ({running.Count}): " + List(running, j => $"{S(j, "jobId")} {S(j, "kind")}"));
        md.AppendLine($"Finished (last {Math.Min(MaxFinishedJobs, finished.Count)} of {finished.Count}):");
        foreach (var j in finished.Skip(Math.Max(0, finished.Count - MaxFinishedJobs)))
        {
            md.AppendLine($"- {S(j, "jobId")} {S(j, "kind")} {S(j, "state")}{(Get(j, "error") is JsonObject e ? $": {S(e, "code")} {OneLine(S(e, "message"))}" : "")}");
        }

        md.AppendLine();
    }

    // A value inline (scalars, small JSON) or as its stub; stubs anywhere inside are collected for the table.
    private static string Value(JsonValue? value, string where, List<(string, JsonObject)> stubs)
    {
        Collect(value, where, stubs);
        if (value is JsonObject o && o["redacted"] is JsonObject stub)
        {
            return $"*redacted* ({S(stub, "reason")}, {Name(Get(stub, "type"))}; ref `{S(stub, "ref")}`)";
        }

        var text = Scrub(value)?.ToString() ?? "null";
        return text.Length <= MaxInlineChars ? $"`{text}`" : $"*{text.Length} characters, not inlined* (`vars.get` / `obj.get`)";
    }

    private static void Collect(JsonValue? value, string where, List<(string, JsonObject)> stubs)
    {
        switch (value)
        {
            case JsonObject o when o["redacted"] is JsonObject stub:
                stubs.Add((where, stub));
                break;
            case JsonObject o:
                foreach (var pair in o)
                {
                    Collect(pair.Value, where + "." + pair.Key, stubs);
                }

                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    Collect(a[i], $"{where}[{i}]", stubs);
                }

                break;
        }
    }

    // Removes anything named like a secret (defence in depth: no input should carry one).
    private static JsonValue? Scrub(JsonValue? value)
    {
        if (value is JsonObject o)
        {
            var copy = new JsonObject();
            foreach (var pair in o)
            {
                copy.Set(pair.Key, SecretNames.Any(n => n.Equals(pair.Key, StringComparison.OrdinalIgnoreCase)) ? new JsonString("(policy: not included)") : Scrub(pair.Value));
            }

            return copy;
        }

        if (value is JsonArray a)
        {
            var copy = new JsonArray();
            foreach (var item in a)
            {
                copy.Add(Scrub(item));
            }

            return copy;
        }

        return value;
    }

    private static IReadOnlyList<JsonObject> Items(JsonValue? list) =>
        Get(list, "items") is JsonArray items ? items.OfType<JsonObject>().ToList() : Array.Empty<JsonObject>();

    private static JsonValue? Get(JsonValue? node, string name) => node is JsonObject o ? o[name] : null;

    private static string S(JsonValue? node, string name) => Text(Get(node, name));

    private static string Text(JsonValue? value) => value switch
    {
        null or JsonNull => "",
        JsonString s => s.Value,
        JsonObject o when o["name"] is JsonString n => n.Value,
        _ => value.ToString(),
    };

    private static string Name(JsonValue? anchorOrType) => anchorOrType is JsonObject o && o["name"] is JsonString n ? n.Value : Text(anchorOrType);

    private static string Target(JsonObject entry) => Text(Get(entry, "target")) is { Length: > 0 } t ? $" `{t}`" : "";

    private static string Asm(JsonObject entry) => Get(entry, "assembly") is JsonObject a ? $" [{S(a, "name")} sha256 {S(a, "sha256")}]" : "";

    private static string List(IReadOnlyList<JsonObject> items, Func<JsonObject, string> line) => items.Count == 0 ? "none" : string.Join("; ", items.Select(line));

    private static string Ms(JsonValue? value) => value is JsonNumber n ? TimeSpan.FromMilliseconds(n.GetDouble()).ToString(@"d\.hh\:mm\:ss", CultureInfo.InvariantCulture) : "?";

    private static string Bytes(JsonValue? value) => value is JsonNumber n ? $"{n.GetDouble() / (1024 * 1024):0.0} MiB" : "?";

    private static string Size(JsonValue? size) => size is JsonObject o
        ? string.Join(", ", new[] { ("count", S(o, "count")), ("length", S(o, "length")), ("bytes", S(o, "estBytes")) }.Where(p => p.Item2.Length > 0).Select(p => $"{p.Item1} {p.Item2}"))
        : "";

    private static string OneLine(string text) => text.Replace("\r", " ").Replace("\n", " ");

    private static string Cell(string text) => OneLine(text).Replace("|", "\\|");
}
