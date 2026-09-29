using System;
using System.Collections.Generic;
using System.Globalization;
using UnityLudometry.Protocol;

namespace UnityRuntimeAnalysisAgent.Core.Hosting;

/// <summary>How the agent listens.</summary>
public enum TransportMode
{
    /// <summary>A named pipe, falling back to TCP on loopback if the pipe can't be created.</summary>
    Auto,

    /// <summary>A named pipe only.</summary>
    Pipe,

    /// <summary>TCP on loopback only.</summary>
    Tcp,
}

/// <summary>The agent's configuration, read from an <see cref="IConfigSource"/>. Invalid values fall back to the default with a warning.</summary>
public sealed class AgentConfig
{
    /// <summary>Config key of <see cref="ProvidersDir"/>.</summary>
    public const string ProvidersDirKey = "Discovery.ProvidersDir";

    /// <summary>Config key of <see cref="Mode"/>.</summary>
    public const string ModeKey = "Security.Mode";

    /// <summary>Config key of <see cref="Transport"/>.</summary>
    public const string TransportModeKey = "Transport.Mode";

    /// <summary>Config key of <see cref="MaxFrameBytes"/>.</summary>
    public const string MaxFrameBytesKey = "Transport.MaxFrameBytes";

    /// <summary>Config key of <see cref="LogLevel"/>.</summary>
    public const string LogLevelKey = "Agent.LogLevel";

    /// <summary>Config key of <see cref="FrameBudgetMs"/>.</summary>
    public const string FrameBudgetMsKey = "Pump.FrameBudgetMs";

    /// <summary>Config key of <see cref="StallMs"/>.</summary>
    public const string StallMsKey = "Pump.StallMs";

    /// <summary>Config key of <see cref="MaxConcurrentJobs"/>.</summary>
    public const string MaxConcurrentJobsKey = "Jobs.MaxConcurrent";

    /// <summary>Config key of <see cref="MaxEventQueueBytes"/>.</summary>
    public const string MaxEventQueueBytesKey = "Events.MaxQueueBytes";

    /// <summary>Config key of <see cref="MaxHandles"/>.</summary>
    public const string MaxHandlesKey = "Handles.Max";

    /// <summary>Config key of <see cref="MaxInstrumentedMethods"/>.</summary>
    public const string MaxInstrumentedMethodsKey = "Instrumentation.MaxMethods";

    /// <summary>Config key of <see cref="RemoveInstrumentationOnDisconnect"/>.</summary>
    public const string RemoveOnDisconnectKey = "Instrumentation.RemoveOnDisconnect";

    /// <summary>Where the discovery file is written. No default: without it the agent listens but publishes nothing.</summary>
    public string? ProvidersDir { get; set; }

    /// <summary>What the agent may do (default <see cref="AgentMode.ReadOnly"/>).</summary>
    public AgentMode Mode { get; set; } = AgentMode.ReadOnly;

    /// <summary>How the agent listens (default <see cref="TransportMode.Auto"/>).</summary>
    public TransportMode Transport { get; set; } = TransportMode.Auto;

    /// <summary>The largest frame accepted and sent (default 16 MiB).</summary>
    public int MaxFrameBytes { get; set; } = UnityLudometry.Protocol.Framing.FrameLimits.DefaultMaxFrameBytes;

    /// <summary>The agent's own log verbosity (default Info).</summary>
    public AgentLogLevel LogLevel { get; set; } = AgentLogLevel.Info;

    /// <summary>Main-thread time per frame for the agent's work, in milliseconds (default 4).</summary>
    public int FrameBudgetMs { get; set; } = 4;

    /// <summary>How long without a frame before the main thread counts as stalled, in milliseconds (default 3000).</summary>
    public int StallMs { get; set; } = 3000;

    /// <summary>CPU-heavy jobs running at once (default 2).</summary>
    public int MaxConcurrentJobs { get; set; } = 2;

    /// <summary>Unsent event bytes per connection before the oldest events are dropped (default 8 MiB).</summary>
    public int MaxEventQueueBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>Live object handles kept before the least recently used are released (default 20,000).</summary>
    public int MaxHandles { get; set; } = 20_000;

    /// <summary>Methods instrumented at once (default 2,000).</summary>
    public int MaxInstrumentedMethods { get; set; } = 2000;

    /// <summary>Remove a client's non-persistent instrumentation when it disconnects (default true).</summary>
    public bool RemoveInstrumentationOnDisconnect { get; set; } = true;

    /// <summary>Problems found while reading (each already resolved to a default).</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    private readonly List<string> _warnings = new();

    /// <summary>Reads the configuration.</summary>
    public static AgentConfig Read(IConfigSource source)
    {
        var config = new AgentConfig();
        var providers = source.Get(ProvidersDirKey);
        config.ProvidersDir = string.IsNullOrWhiteSpace(providers) ? null : providers!.Trim();

        var mode = source.Get(ModeKey);
        if (!string.IsNullOrWhiteSpace(mode))
        {
            if (AgentModes.TryParse(mode!.Trim(), out var parsed))
            {
                config.Mode = parsed;
            }
            else
            {
                config._warnings.Add($"{ModeKey} '{mode}' is not ReadOnly, ReadOnly+Load or Full; using ReadOnly.");
            }
        }

        var transport = source.Get(TransportModeKey);
        if (!string.IsNullOrWhiteSpace(transport))
        {
            switch (transport!.Trim().ToLowerInvariant())
            {
                case "auto":
                    config.Transport = TransportMode.Auto;
                    break;
                case "pipe":
                    config.Transport = TransportMode.Pipe;
                    break;
                case "tcp":
                    config.Transport = TransportMode.Tcp;
                    break;
                default:
                    config._warnings.Add($"{TransportModeKey} '{transport}' is not auto, pipe or tcp; using auto.");
                    break;
            }
        }

        var frame = source.Get(MaxFrameBytesKey);
        if (!string.IsNullOrWhiteSpace(frame))
        {
            if (int.TryParse(frame, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) && bytes >= 1024)
            {
                config.MaxFrameBytes = bytes;
            }
            else
            {
                config._warnings.Add($"{MaxFrameBytesKey} '{frame}' is not a number of bytes (at least 1024); using {config.MaxFrameBytes}.");
            }
        }

        var level = source.Get(LogLevelKey);
        if (!string.IsNullOrWhiteSpace(level))
        {
            if (Enum.TryParse<AgentLogLevel>(level!.Trim(), ignoreCase: true, out var parsedLevel) && Enum.IsDefined(typeof(AgentLogLevel), parsedLevel))
            {
                config.LogLevel = parsedLevel;
            }
            else
            {
                config._warnings.Add($"{LogLevelKey} '{level}' is not Debug, Info, Warning or Error; using Info.");
            }
        }

        config.FrameBudgetMs = config.ReadInt(source, FrameBudgetMsKey, config.FrameBudgetMs, 1, 100);
        config.StallMs = config.ReadInt(source, StallMsKey, config.StallMs, 250, 600_000);
        config.MaxConcurrentJobs = config.ReadInt(source, MaxConcurrentJobsKey, config.MaxConcurrentJobs, 1, 16);
        config.MaxEventQueueBytes = config.ReadInt(source, MaxEventQueueBytesKey, config.MaxEventQueueBytes, 64 * 1024, 256 * 1024 * 1024);
        config.MaxHandles = config.ReadInt(source, MaxHandlesKey, config.MaxHandles, 100, 1_000_000);
        config.MaxInstrumentedMethods = config.ReadInt(source, MaxInstrumentedMethodsKey, config.MaxInstrumentedMethods, 1, 20_000);
        config.RemoveInstrumentationOnDisconnect = config.ReadBool(source, RemoveOnDisconnectKey, config.RemoveInstrumentationOnDisconnect);
        return config;
    }

    /// <summary>The effective caps, for <c>agent.info</c> and <c>agent.capabilities</c> (config key → value).</summary>
    public IReadOnlyList<KeyValuePair<string, long>> Limits() => new[]
    {
        new KeyValuePair<string, long>(MaxFrameBytesKey, MaxFrameBytes),
        new KeyValuePair<string, long>(FrameBudgetMsKey, FrameBudgetMs),
        new KeyValuePair<string, long>(StallMsKey, StallMs),
        new KeyValuePair<string, long>(MaxConcurrentJobsKey, MaxConcurrentJobs),
        new KeyValuePair<string, long>(MaxEventQueueBytesKey, MaxEventQueueBytes),
        new KeyValuePair<string, long>(MaxHandlesKey, MaxHandles),
        new KeyValuePair<string, long>(MaxInstrumentedMethodsKey, MaxInstrumentedMethods),
    };

    private bool ReadBool(IConfigSource source, string key, bool fallback)
    {
        var text = source.Get(key);
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        if (bool.TryParse(text!.Trim(), out var value))
        {
            return value;
        }

        _warnings.Add($"{key} '{text}' is not true or false; using {fallback.ToString().ToLowerInvariant()}.");
        return fallback;
    }

    private int ReadInt(IConfigSource source, string key, int fallback, int min, int max)
    {
        var text = source.Get(key);
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        if (int.TryParse(text!.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max)
        {
            return value;
        }

        _warnings.Add($"{key} '{text}' is not a whole number from {min} to {max}; using {fallback}.");
        return fallback;
    }
}
