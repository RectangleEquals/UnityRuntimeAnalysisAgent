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

        return config;
    }
}
