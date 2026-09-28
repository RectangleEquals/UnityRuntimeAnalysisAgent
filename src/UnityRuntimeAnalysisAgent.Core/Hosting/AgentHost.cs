using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Envelopes;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Discovery;
using UnityRuntimeAnalysisAgent.Core.Session;
using UnityRuntimeAnalysisAgent.Core.Transport;

namespace UnityRuntimeAnalysisAgent.Core.Hosting;

/// <summary>
/// The composition root: reads the configuration, starts the transport and the session handling, publishes the discovery
/// file, and shuts everything down again (idempotently).
/// </summary>
public sealed class AgentHost : IDisposable
{
    private readonly AgentConfig _config;
    private readonly AgentEnvironment _environment;
    private readonly IAgentLogger _log;
    private readonly List<Connection> _connections = new();
    private readonly object _gate = new();
    private readonly DiscoveryPublisher _discovery;
    private readonly Func<string, IAgentLogger, ITransport>? _createPipe;
    private ITransport? _transport;
    private bool _stopped;

    /// <summary>Creates the host (nothing runs until <see cref="Start"/>).</summary>
    /// <param name="createPipe">Test hook: a custom pipe transport factory (e.g. to force the TCP fallback).</param>
    public AgentHost(IConfigSource config, AgentEnvironment environment, IAgentLogger log, string? pipeName = null,
        Func<string, IAgentLogger, ITransport>? createPipe = null)
    {
        _config = AgentConfig.Read(config);
        _environment = environment;
        _log = log;
        _createPipe = createPipe;
        PipeName = pipeName ?? $"ulm-agent-{environment.ProcessId}";
        Token = SessionToken.Generate();
        _discovery = new DiscoveryPublisher(log);
        Session = new SessionHandler(Token, BuildAgentInfo, CompleteCapabilities);
        foreach (var warning in _config.Warnings)
        {
            log.Warning(warning);
        }
    }

    /// <summary>The session token of this start.</summary>
    public SessionToken Token { get; }

    /// <summary>The effective configuration.</summary>
    public AgentConfig Config => _config;

    /// <summary>The pipe name the pipe transport uses.</summary>
    public string PipeName { get; }

    /// <summary>The protocol handling (later components register their methods here).</summary>
    public SessionHandler Session { get; }

    /// <summary>The running transport (after <see cref="Start"/>).</summary>
    public ITransport? Transport => _transport;

    /// <summary>The published discovery file, if any.</summary>
    public string? DiscoveryPath => _discovery.PublishedPath;

    /// <summary>Connections currently open.</summary>
    public int ConnectionCount
    {
        get
        {
            lock (_gate)
            {
                return _connections.Count;
            }
        }
    }

    /// <summary>Starts listening and publishes the discovery file.</summary>
    public void Start()
    {
        _transport = TransportFactory.Start(_config.Transport, PipeName, _log, OnAccepted, _createPipe);
        _log.Info(_transport.Kind == "pipe" ? $"Listening on pipe {_transport.PipeName}." : $"Listening on 127.0.0.1:{_transport.Port}.");
        _discovery.Publish(_config.ProvidersDir, BuildDiscoveryFile());
    }

    /// <summary>Sends an event to every authenticated connection subscribed to its kind.</summary>
    public void Publish(string kind, ProtocolMessage payload, JsonObject? context = null)
    {
        if (EventRegistry.Find(kind) is null)
        {
            throw new ArgumentException($"{kind} is not an event kind of the protocol.", nameof(kind));
        }

        Connection[] targets;
        lock (_gate)
        {
            targets = _connections.ToArray();
        }

        var json = payload.ToJson();
        foreach (var connection in targets)
        {
            if (connection.Authenticated && connection.IsSubscribed(kind))
            {
                connection.Send(new EventEnvelope { Method = kind, Seq = connection.NextEventSeq(), Params = json, Context = context });
            }
        }
    }

    /// <summary>Stops listening, closes every connection and deletes the discovery file (idempotent).</summary>
    public void Shutdown()
    {
        Connection[] open;
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            open = _connections.ToArray();
        }

        _discovery.Dispose();
        _transport?.Dispose();
        foreach (var connection in open)
        {
            connection.Close("agent shutdown");
        }

        _log.Info("Agent stopped.");
    }

    /// <inheritdoc />
    public void Dispose() => Shutdown();

    private void OnAccepted(Stream stream, Action release)
    {
        var connection = new Connection(stream, _transport?.Kind ?? "?", _config.MaxFrameBytes, _log, Session.Handle);
        lock (_gate)
        {
            if (_stopped)
            {
                stream.Dispose();
                release();
                return;
            }

            _connections.Add(connection);
        }

        connection.Closed += (_, _) =>
        {
            lock (_gate)
            {
                _connections.Remove(connection);
            }

            release();
        };
        connection.Start();
    }

    private DiscoveryFile BuildDiscoveryFile() => new()
    {
        Provider = "agent",
        Pid = _environment.ProcessId,
        ProcessName = _environment.ProcessName,
        ProcessPath = _environment.ProcessPath,
        Transport = _transport!.Kind,
        Pipe = _transport.PipeName,
        Port = _transport.Port,
        Token = Token.Value,
        Protocol = ProtocolVersionInfo.Current,
        AgentVersion = _environment.AgentVersion,
        Loader = new LoaderInfo { Name = _environment.LoaderName, Version = _environment.LoaderVersion },
        UnityVersion = _environment.UnityVersion,
        Mode = _config.Mode,
        StartedAt = FormatTimestamp(Session.StartedUtc),
    };

    private AgentInfo BuildAgentInfo()
    {
        int connections;
        lock (_gate)
        {
            connections = _connections.Count;
        }

        return new AgentInfo
        {
            AgentVersion = _environment.AgentVersion,
            GitCommit = _environment.GitCommit,
            Protocol = ProtocolVersionInfo.Current,
            Pid = _environment.ProcessId,
            ProcessName = _environment.ProcessName,
            UnityVersion = _environment.UnityVersion,
            ScriptingBackend = _environment.ScriptingBackend,
            Platform = _environment.Platform,
            Loader = new LoaderInfo { Name = _environment.LoaderName, Version = _environment.LoaderVersion },
            Mode = _config.Mode,
            Transport = _transport?.Kind ?? "pipe",
            StartedAt = FormatTimestamp(Session.StartedUtc),
            UptimeMs = (long)(DateTime.UtcNow - Session.StartedUtc).TotalMilliseconds,
            Limits = new JsonObject(),
            // The main-thread pump arrives with the dispatcher; until then it reports as not running.
            Health = new AgentHealth
            {
                Pump = new PumpHealth { Alive = false, LastTickFrame = null, QueueLength = 0, StalledMs = 0, RecreatedCount = 0 },
                Connections = connections,
            },
        };
    }

    private AgentCapabilities CompleteCapabilities(AgentCapabilities capabilities)
    {
        capabilities.AgentVersion = _environment.AgentVersion;
        capabilities.ApiVersion = _environment.ApiVersion;
        capabilities.Protocol = ProtocolVersionInfo.Current;
        capabilities.Limits = new JsonObject();
        return capabilities;
    }

    private static string FormatTimestamp(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
