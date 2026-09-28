using System;
using System.Collections.Generic;
using UnityRuntimeAnalysisAgent.Core.Hosting;

namespace UnityRuntimeAnalysisAgent.Core.Abstractions;

/// <summary>A line from the loader's own log (e.g. BepInEx's log listeners).</summary>
public sealed class LoaderLogEntry
{
    /// <summary>Creates an entry.</summary>
    public LoaderLogEntry(string source, AgentLogLevel level, string message)
    {
        Source = source;
        Level = level;
        Message = message;
    }

    /// <summary>The log source (plugin name, "Unity", …).</summary>
    public string Source { get; }

    /// <summary>The level.</summary>
    public AgentLogLevel Level { get; }

    /// <summary>The message.</summary>
    public string Message { get; }
}

/// <summary>A plugin the loader has loaded.</summary>
public sealed class LoaderPluginInfo
{
    /// <summary>Creates the description.</summary>
    public LoaderPluginInfo(string guid, string name, string version, string? assemblyPath)
    {
        Guid = guid;
        Name = name;
        Version = version;
        AssemblyPath = assemblyPath;
    }

    /// <summary>The plugin id.</summary>
    public string Guid { get; }

    /// <summary>The display name.</summary>
    public string Name { get; }

    /// <summary>The version.</summary>
    public string Version { get; }

    /// <summary>Where its assembly was loaded from, if known.</summary>
    public string? AssemblyPath { get; }
}

/// <summary>What Core needs from the mod loader. The loader shim implements it; tests use a fake.</summary>
public interface ILoaderApi
{
    /// <summary>The loader's name (e.g. BepInEx).</summary>
    string LoaderName { get; }

    /// <summary>The loader's version.</summary>
    string LoaderVersion { get; }

    /// <summary>The agent's configuration, as the loader stores it.</summary>
    IConfigSource Config { get; }

    /// <summary>The plugins the loader has loaded.</summary>
    IReadOnlyList<LoaderPluginInfo> Plugins { get; }

    /// <summary>Whether the keyboard shortcut stored under a config key is pressed this frame (main thread only).</summary>
    bool IsShortcutPressed(string configKey);

    /// <summary>A log that writes through the loader's logging.</summary>
    IAgentLogger CreateLog(string source);

    /// <summary>Raised for every line the loader logs.</summary>
    event Action<LoaderLogEntry>? LoaderLog;
}
