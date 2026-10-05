using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Envelopes;
using UnityLudometry.Protocol.Json;
using UnityRuntimeAnalysisAgent.Core.Dispatch;
using UnityRuntimeAnalysisAgent.Core.Hosting;

namespace UnityRuntimeAnalysisAgent.Core.Overlay;

/// <summary>
/// What the tabs need from the host beyond the agent's methods: the connected clients (Status, the arrow's tint), the
/// <c>Overlay.*</c> settings (Settings tab, edited and persisted), and the Copy report.
/// </summary>
public sealed partial class OverlayController
{
    private const string SettingPromptPrefix = "setting:";
    private readonly IConfigWriter? _writer;
    private readonly ModeController _modes;
    private readonly Dictionary<string, string> _settingValues = new(StringComparer.Ordinal);
    private bool _settingPromptsWired;

    /// <summary>The connected clients (set by the host).</summary>
    public Func<IReadOnlyList<ReportClient>> Clients { get; set; } = () => Array.Empty<ReportClient>();

    /// <summary>The configuration the settings are read from (set by the host).</summary>
    public IConfigSource? Config { get; set; }

    /// <summary>Pins a ref cited in a Copy report so it stays expandable (set by the host).</summary>
    public Action<string> PinRef { get; set; } = _ => { };

    /// <summary>Where the logs are (the loader's log, the player log), for the Copy report (set by the host).</summary>
    public Func<IReadOnlyList<string>> LogPaths { get; set; } = () => Array.Empty<string>();

    /// <summary>A client counts as busy while it sent or received something this recently.</summary>
    public static readonly TimeSpan BusyWindow = TimeSpan.FromSeconds(1.5);

    /// <summary>Whether the overlay may switch to this mode: only ever lower than the current one (raising needs a client and the user's consent).</summary>
    public bool CanLowerTo(string wireMode) =>
        AgentModes.TryParse(wireMode, out var mode) && ModeController.Rank(mode) < ModeController.Rank(_modes.Current);

    /// <summary>The overlay's settings as rows for the Settings tab: key, value, default, kind, choices, changed.</summary>
    public JsonArray SettingRows()
    {
        var rows = new JsonArray();
        foreach (var key in ConfigKeys.All.Where(k => k.Section == "Overlay"))
        {
            var full = key.Section + "." + key.Name;
            var value = SettingValue(key);
            var row = new JsonObject
            {
                { "key", new JsonString(full) },
                { "name", new JsonString(key.Name) },
                { "value", new JsonString(value.Length > 0 ? value : "(none)") },
                { "default", new JsonString(key.Default) },
                { "description", new JsonString(key.Description) },
                { "kind", new JsonString(key.Kind.ToString().ToLowerInvariant()) },
                { "changed", value != key.Default ? JsonBoolean.True : JsonBoolean.False },
            };
            if (key.Kind == ConfigKind.Bool)
            {
                row.Set("on", string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ? JsonBoolean.True : JsonBoolean.False);
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>Saves a setting (persisted through the loader). Returns false for an unknown key or a value it can't take.</summary>
    public bool SetSetting(string fullKey, string value)
    {
        var key = Find(fullKey);
        if (key is null || (key.Choices.Count > 0 && !key.Choices.Contains(value)))
        {
            return false;
        }

        switch (key.Kind)
        {
            case ConfigKind.Bool when !bool.TryParse(value, out _):
            case ConfigKind.Int when !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _):
            case ConfigKind.Float when !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _):
                return false;
        }

        _settingValues[fullKey] = value;
        _writer?.Set(fullKey, value);
        Toasts.Add($"{key.Name} = {(value.Length > 0 ? value : "(none)")}. Saved; it applies after the game restarts.", ToastLevel.Info, "agent", _lastNow, 4);
        return true;
    }

    /// <summary>Flips a true/false setting.</summary>
    public bool ToggleSetting(string fullKey) =>
        Find(fullKey) is { Kind: ConfigKind.Bool } key && SetSetting(fullKey, string.Equals(SettingValue(key), "true", StringComparison.OrdinalIgnoreCase) ? "false" : "true");

    /// <summary>Moves a choice setting to its next choice (wrapping).</summary>
    public bool CycleSetting(string fullKey)
    {
        if (Find(fullKey) is not { Choices.Count: > 0 } key)
        {
            return false;
        }

        var index = key.Choices.ToList().IndexOf(SettingValue(key));
        return SetSetting(fullKey, key.Choices[(index + 1) % key.Choices.Count]);
    }

    /// <summary>Steps a number setting (whole numbers by 1, others by 0.05).</summary>
    public bool StepSetting(string fullKey, int direction)
    {
        var key = Find(fullKey);
        if (key is null || !double.TryParse(SettingValue(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return false;
        }

        return key.Kind switch
        {
            ConfigKind.Int => SetSetting(fullKey, ((int)number + Math.Sign(direction)).ToString(CultureInfo.InvariantCulture)),
            ConfigKind.Float => SetSetting(fullKey, Math.Round(number + (0.05 * Math.Sign(direction)), 2).ToString("0.##", CultureInfo.InvariantCulture)),
            _ => false,
        };
    }

    /// <summary>Asks for a new value of a text setting with an overlay prompt (typed answer; or reset, or cancel).</summary>
    public bool EditSetting(string fullKey)
    {
        var key = Find(fullKey);
        if (key is null)
        {
            return false;
        }

        WireSettingPrompts();
        var id = SettingPromptPrefix + fullKey;
        if (Prompts.Pending.Any(p => p.Id == id))
        {
            return true;
        }

        Prompts.Show(id, key.Name, $"{key.Description} Now: {Display(SettingValue(key))}. Default: {Display(key.Default)}.",
            new[] { "Type a new value", "Reset to default", "Cancel" }, _lastNow, textButton: "Type a new value");
        return true;
    }

    /// <summary>Resets a setting to its default.</summary>
    public bool ResetSetting(string fullKey) => Find(fullKey) is { } key && SetSetting(fullKey, key.Default);

    /// <summary>
    /// Gathers what the Copy report needs (the same reads the tabs use, plus the host's own data) and builds it; the
    /// Markdown goes to <paramref name="done"/> on the main thread. Refs it cites are pinned.
    /// </summary>
    public void BuildReport(Action<string> done)
    {
        var reads = new (string Key, string Method, JsonObject Params)[]
        {
            ("info", "agent.info", new JsonObject()),
            ("metrics", "metrics.get", new JsonObject()),
            ("activity", "activity.list", new JsonObject { { "limit", new JsonNumber(1000) } }),
            ("jobs", "job.list", new JsonObject()),
            ("hooks", "hook.list", new JsonObject()),
            ("watches", "watch.list", new JsonObject()),
            ("patches", "patch.list", new JsonObject()),
            ("rules", "rule.list", new JsonObject()),
            ("mods", "mod.list", new JsonObject()),
            ("vars", "vars.list", new JsonObject()),
            ("logs", "logs.tail", new JsonObject { { "limit", new JsonNumber(500) } }),
            ("app", "app.info", new JsonObject()),
        }.ToList();
        var selection = Selection.Current;
        if (selection is { Destroyed: false })
        {
            reads.Add(("selection", "obj.inspect", new JsonObject { { "target", new JsonObject { { "h", new JsonNumber(_handleOf(selection.Target)) } } } }));
        }

        var results = new Dictionary<string, JsonValue?>(StringComparer.Ordinal);
        var remaining = reads.Count;
        foreach (var (key, method, parameters) in reads)
        {
            Queries.Read(method, parameters, (value, error) =>
            {
                results[key] = error is null ? value : null;
                if (--remaining == 0)
                {
                    done(CopyReport.Build(ReportInputs(results, selection), PinRef));
                }
            });
        }
    }

    // The overlay's own state the tabs bind to, beyond LocalState: clients, settings, the mode.
    private void AddHostState(JsonObject state)
    {
        var now = DateTime.UtcNow;
        var clients = Clients();
        state.Set("clients", new JsonArray(clients.Select(c => (JsonValue)new JsonObject
        {
            { "name", new JsonString(c.Name) },
            { "since", new JsonString(FormatAgo((now - c.ConnectedUtc).TotalSeconds)) },
            { "messages", new JsonNumber(c.Messages) },
            { "bytes", new JsonString(FormatBytes(c.Bytes)) },
            { "dropped", new JsonNumber(c.DroppedEvents) },
            { "busy", now - c.LastActivityUtc < BusyWindow ? JsonBoolean.True : JsonBoolean.False },
        })));
        state.Set("connected", clients.Count > 0 ? JsonBoolean.True : JsonBoolean.False);
        state.Set("mode", new JsonString(AgentModes.ToWire(_modes.Current)));
        state.Set("full", _modes.Current == UnityLudometry.Protocol.AgentMode.Full ? JsonBoolean.True : JsonBoolean.False);
        state.Set("settings", SettingRows());
        AddTestState(state);
    }

    private ReportInputs ReportInputs(Dictionary<string, JsonValue?> r, SelectionEntry? selection)
    {
        var logs = r.TryGetValue("logs", out var l) ? OverlayViewModels.At(l, "items") as JsonArray : null;
        var entries = logs?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
        string Level(JsonObject e) => e["level"] is JsonString s ? s.Value : "";
        var problems = entries.Where(e => Level(e) is "warning" or "error" or "exception" or "assert").ToList();
        var seqs = entries.Select(e => e["seq"] is JsonNumber n ? (long)n.GetDouble() : -1).Where(n => n >= 0).ToList();
        return new ReportInputs
        {
            Info = Get(r, "info"),
            Metrics = Get(r, "metrics"),
            Clients = Clients(),
            Activity = Get(r, "activity"),
            Jobs = Get(r, "jobs"),
            Hooks = Get(r, "hooks"),
            Watches = Get(r, "watches"),
            Patches = Get(r, "patches"),
            Rules = Get(r, "rules"),
            Mods = Get(r, "mods"),
            Vars = Get(r, "vars"),
            SelectionLabel = selection?.Label,
            SelectionLocator = selection?.Locator,
            Selection = Get(r, "selection"),
            WarningCount = problems.Count,
            LastErrors = LastOf(problems.Where(e => Level(e) != "warning").Select(e => e["message"] is JsonString m ? m.Value : "").ToList(), 10),
            LogSeqRange = seqs.Count > 0 ? $"{seqs.Min()}–{seqs.Max()}" : null,
            LastTestRun = LastTestRun(),
            LogPaths = LogPaths().Concat(OverlayViewModels.At(Get(r, "app"), "consoleLogPath") is JsonString player && player.Value.Length > 0 ? new[] { player.Value } : Array.Empty<string>()).ToList(),
            ChangedSettings = ConfigKeys.All.Where(k => k.Section == "Overlay" && SettingValue(k) != k.Default)
                .Select(k => new KeyValuePair<string, string>(k.Section + "." + k.Name, SettingValue(k))).ToList(),
        };
    }

    private static List<string> LastOf(List<string> items, int count) => items.Skip(Math.Max(0, items.Count - count)).ToList();

    private static JsonValue? Get(Dictionary<string, JsonValue?> r, string key) => r.TryGetValue(key, out var v) ? v : null;

    private string SettingValue(ConfigKey key)
    {
        var full = key.Section + "." + key.Name;
        return _settingValues.TryGetValue(full, out var saved) ? saved : Config?.Get(full) ?? key.Default;
    }

    private static ConfigKey? Find(string fullKey) => ConfigKeys.All.FirstOrDefault(k => k.Section + "." + k.Name == fullKey);

    private static string Display(string value) => value.Length > 0 ? value : "(none)";

    // A setting's edit prompt: the typed text becomes the value, "Reset to default" resets it.
    private void WireSettingPrompts()
    {
        if (_settingPromptsWired)
        {
            return;
        }

        _settingPromptsWired = true;
        Prompts.Answered += (prompt, button, text) =>
        {
            if (!prompt.Id.StartsWith(SettingPromptPrefix, StringComparison.Ordinal))
            {
                return;
            }

            var key = prompt.Id.Substring(SettingPromptPrefix.Length);
            if (button == "Reset to default")
            {
                ResetSetting(key);
            }
            else if (text is not null && !SetSetting(key, text.Trim()))
            {
                Toasts.Add($"'{text.Trim()}' isn't a value {key} can take.", ToastLevel.Warning, "agent", _lastNow, 6);
            }
        };
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KiB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MiB",
    };
}
