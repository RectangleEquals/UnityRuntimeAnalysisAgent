using System;

namespace UnityRuntimeAnalysisAgent.Core.Hosting;

/// <summary>Log levels of the agent's own log.</summary>
public enum AgentLogLevel
{
    /// <summary>Verbose diagnostics.</summary>
    Debug,

    /// <summary>Normal operation.</summary>
    Info,

    /// <summary>Something was limited, skipped or fell back.</summary>
    Warning,

    /// <summary>A failure.</summary>
    Error,
}

/// <summary>The agent's own log (routed to the loader's log by the shim).</summary>
public interface IAgentLogger
{
    /// <summary>Writes one entry.</summary>
    void Log(AgentLogLevel level, string message, Exception? exception = null);
}

/// <summary>Convenience methods for <see cref="IAgentLogger"/>.</summary>
public static class AgentLoggerExtensions
{
    /// <summary>Writes a debug entry.</summary>
    public static void Debug(this IAgentLogger log, string message) => log.Log(AgentLogLevel.Debug, message);

    /// <summary>Writes an info entry.</summary>
    public static void Info(this IAgentLogger log, string message) => log.Log(AgentLogLevel.Info, message);

    /// <summary>Writes a warning.</summary>
    public static void Warning(this IAgentLogger log, string message, Exception? exception = null) => log.Log(AgentLogLevel.Warning, message, exception);

    /// <summary>Writes an error.</summary>
    public static void Error(this IAgentLogger log, string message, Exception? exception = null) => log.Log(AgentLogLevel.Error, message, exception);
}

/// <summary>Passes entries at or above <see cref="MinLevel"/> on to another logger (<c>Agent.LogLevel</c>, <c>agent.logLevel</c>).</summary>
public sealed class LevelFilteringLogger : IAgentLogger
{
    private readonly IAgentLogger _inner;
    private int _minLevel;

    /// <summary>Wraps a logger.</summary>
    public LevelFilteringLogger(IAgentLogger inner, AgentLogLevel minLevel)
    {
        _inner = inner;
        _minLevel = (int)minLevel;
    }

    /// <summary>The least level written.</summary>
    public AgentLogLevel MinLevel
    {
        get => (AgentLogLevel)System.Threading.Volatile.Read(ref _minLevel);
        set => System.Threading.Volatile.Write(ref _minLevel, (int)value);
    }

    /// <inheritdoc />
    public void Log(AgentLogLevel level, string message, Exception? exception = null)
    {
        if (level >= MinLevel)
        {
            _inner.Log(level, message, exception);
        }
    }
}

/// <summary>A logger that discards everything.</summary>
public sealed class NullAgentLogger : IAgentLogger
{
    /// <summary>The shared instance.</summary>
    public static readonly NullAgentLogger Instance = new();

    private NullAgentLogger()
    {
    }

    /// <inheritdoc />
    public void Log(AgentLogLevel level, string message, Exception? exception = null)
    {
    }
}
