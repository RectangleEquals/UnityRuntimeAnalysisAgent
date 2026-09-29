using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityRuntimeAnalysisAgent.Core.Abstractions;
using UnityRuntimeAnalysisAgent.Core.Hosting;

namespace UnityRuntimeAnalysisAgent.BepInEx5;

/// <summary>Core's view of BepInEx 5: its config file, logging, plugins, keyboard shortcuts and plugin hosting.</summary>
public sealed class BepInEx5LoaderApi : ILoaderApi, IDisposable
{
    private readonly ConfigFile _configFile;
    private readonly Dictionary<string, ConfigEntryBase> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Listener _listener;
    private GameObject? _pluginHost;

    /// <summary>Binds every agent setting (with its default and description) in the plugin's config file.</summary>
    public BepInEx5LoaderApi(ConfigFile configFile)
    {
        _configFile = configFile;
        foreach (var key in ConfigKeys.All)
        {
            _entries[key.Key] = Bind(key);
        }

        Config = new EntrySource(_entries);
        _listener = new Listener(this);
        BepInEx.Logging.Logger.Listeners.Add(_listener);
    }

    /// <inheritdoc />
    public string LoaderName => "BepInEx";

    /// <inheritdoc />
    public string LoaderVersion => typeof(ConfigFile).Assembly.GetName().Version?.ToString() ?? "5";

    /// <inheritdoc />
    public IConfigSource Config { get; }

    /// <inheritdoc />
    public IReadOnlyList<LoaderPluginInfo> Plugins =>
        Chainloader.PluginInfos.Values.Select(p => new LoaderPluginInfo(p.Metadata.GUID, p.Metadata.Name, p.Metadata.Version.ToString(), p.Location)).ToList();

    /// <inheritdoc />
    public event Action<LoaderLogEntry>? LoaderLog;

    /// <inheritdoc />
    public bool IsShortcutPressed(string configKey) =>
        _entries.TryGetValue(configKey, out var entry) && entry is ConfigEntry<KeyboardShortcut> shortcut && shortcut.Value.IsDown();

    /// <inheritdoc />
    public IAgentLogger CreateLog(string source) => new SourceLogger(BepInEx.Logging.Logger.CreateLogSource(source));

    /// <inheritdoc />
    public object? FindPluginInstance(string guid) =>
        Chainloader.PluginInfos.TryGetValue(guid, out var info) && info.Instance != null ? info.Instance : null;

    /// <inheritdoc />
    public LoaderPluginInfo? PluginMetadata(Type type)
    {
        if (!typeof(BaseUnityPlugin).IsAssignableFrom(type) || type.IsAbstract)
        {
            return null;
        }

        var metadata = MetadataHelper.GetMetadata(type);
        return metadata is null ? null : new LoaderPluginInfo(metadata.GUID, metadata.Name, metadata.Version.ToString(), null);
    }

    /// <inheritdoc />
    public object InstantiatePlugin(Type pluginType)
    {
        if (!typeof(MonoBehaviour).IsAssignableFrom(pluginType))
        {
            throw new ArgumentException($"{pluginType.FullName} isn't a MonoBehaviour.", nameof(pluginType));
        }

        if (_pluginHost == null)
        {
            _pluginHost = new GameObject("UnityRuntimeAnalysisAgent plugins") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(_pluginHost);
        }

        return _pluginHost.AddComponent(pluginType);
    }

    /// <inheritdoc />
    public void DestroyPlugin(object instance)
    {
        if (instance is UnityEngine.Object unityObject && unityObject != null)
        {
            UnityEngine.Object.Destroy(unityObject);
        }
    }

    /// <summary>Detaches from BepInEx's logging and flushes its disk log, which BepInEx 5 only flushes on a timer: without
    /// this, the agent's last lines (its shutdown) can be missing from <c>LogOutput.log</c> when the game quits.</summary>
    public void Dispose()
    {
        BepInEx.Logging.Logger.Listeners.Remove(_listener);
        foreach (var disk in BepInEx.Logging.Logger.Listeners.OfType<DiskLogListener>().ToList())
        {
            try
            {
                disk.LogWriter?.Flush();
            }
            catch (ObjectDisposedException)
            {
                // BepInEx already closed its log.
            }
        }
    }

    private ConfigEntryBase Bind(ConfigKey key)
    {
        var acceptable = key.Kind == ConfigKind.Choice ? new AcceptableValueList<string>(key.Choices.ToArray()) : null;
        var description = new ConfigDescription(key.Description, acceptable);
        return key.Kind switch
        {
            ConfigKind.Bool => _configFile.Bind(key.Section, key.Name, bool.Parse(key.Default), description),
            ConfigKind.Int => _configFile.Bind(key.Section, key.Name, int.Parse(key.Default, CultureInfo.InvariantCulture), description),
            ConfigKind.Float => _configFile.Bind(key.Section, key.Name, float.Parse(key.Default, CultureInfo.InvariantCulture), description),
            ConfigKind.Shortcut => _configFile.Bind(key.Section, key.Name, key.Default.Length == 0 ? KeyboardShortcut.Empty : KeyboardShortcut.Deserialize(key.Default), description),
            _ => _configFile.Bind(key.Section, key.Name, key.Default, description),
        };
    }

    private static AgentLogLevel ToAgentLevel(LogLevel level) =>
        (level & (LogLevel.Fatal | LogLevel.Error)) != 0 ? AgentLogLevel.Error
        : (level & LogLevel.Warning) != 0 ? AgentLogLevel.Warning
        : (level & (LogLevel.Message | LogLevel.Info)) != 0 ? AgentLogLevel.Info
        : AgentLogLevel.Debug;

    /// <summary>The settings as Core reads them: each bound entry's value in its text form.</summary>
    private sealed class EntrySource : IConfigSource
    {
        private readonly Dictionary<string, ConfigEntryBase> _entries;

        public EntrySource(Dictionary<string, ConfigEntryBase> entries) => _entries = entries;

        public string? Get(string key) => _entries.TryGetValue(key, out var entry) ? entry.GetSerializedValue() : null;
    }

    private sealed class SourceLogger : IAgentLogger
    {
        private readonly ManualLogSource _source;

        public SourceLogger(ManualLogSource source) => _source = source;

        public void Log(AgentLogLevel level, string message, Exception? exception = null)
        {
            var text = exception is null ? message : $"{message}{Environment.NewLine}{exception}";
            switch (level)
            {
                case AgentLogLevel.Error:
                    _source.LogError(text);
                    break;
                case AgentLogLevel.Warning:
                    _source.LogWarning(text);
                    break;
                case AgentLogLevel.Info:
                    _source.LogInfo(text);
                    break;
                default:
                    _source.LogDebug(text);
                    break;
            }
        }
    }

    /// <summary>Forwards every line BepInEx logs (the game's Unity log included) to <see cref="LoaderLog"/>.</summary>
    private sealed class Listener : ILogListener
    {
        private readonly BepInEx5LoaderApi _owner;

        public Listener(BepInEx5LoaderApi owner) => _owner = owner;

        public void LogEvent(object sender, LogEventArgs eventArgs)
        {
            var handler = _owner.LoaderLog;
            if (handler is null)
            {
                return;
            }

            try
            {
                handler(new LoaderLogEntry(eventArgs.Source?.SourceName ?? "?", ToAgentLevel(eventArgs.Level), eventArgs.Data?.ToString() ?? string.Empty));
            }
            catch (Exception)
            {
                // A listener must never break the loader's logging.
            }
        }

        public void Dispose()
        {
        }
    }
}
