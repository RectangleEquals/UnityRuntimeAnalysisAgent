using System;
using System.Threading;
using System.Reflection;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Abstractions;
using UnityRuntimeAnalysisAgent.Core.Discovery;
using UnityRuntimeAnalysisAgent.Core.Code;
using UnityRuntimeAnalysisAgent.Core.Data;
using UnityRuntimeAnalysisAgent.Core.Diagnostics;
using UnityRuntimeAnalysisAgent.Core.Dispatch;
using UnityRuntimeAnalysisAgent.Core.Execution;
using UnityRuntimeAnalysisAgent.Core.Instrumentation;
using UnityRuntimeAnalysisAgent.Core.Jobs;
using UnityRuntimeAnalysisAgent.Core.Overlay;
using UnityRuntimeAnalysisAgent.Core.Runtime;
using UnityRuntimeAnalysisAgent.Core.Session;
using UnityRuntimeAnalysisAgent.Core.Transport;

namespace UnityRuntimeAnalysisAgent.Core.Hosting;

/// <summary>
/// The composition root. It builds every component from the configuration and wires them together. <see cref="Start"/>
/// brings them up in dependency order (pump, event delivery, transport, discovery file). <see cref="Shutdown"/> removes
/// every side effect, idempotently: it cancels jobs, runs the cleanups later components registered (patches, hooks,
/// subscriptions…) in reverse order, flushes and stops events, closes connections and the transport, deletes the
/// discovery file, and finally destroys the pump host.
/// </summary>
public sealed class AgentHost : IDisposable
{
    private readonly AgentConfig _config;
    private readonly AgentEnvironment _environment;
    private readonly IUnityApi? _unity;
    private readonly List<Connection> _connections = new();
    private readonly List<(string Name, Action Action)> _cleanups = new();
    private readonly object _gate = new();
    private readonly DiscoveryPublisher _discovery;
    private readonly Func<string, IAgentLogger, ITransport>? _createPipe;
    private readonly PumpWatchdog _watchdog;
    private ITransport? _transport;
    private bool _started;
    private bool _stopped;

    /// <summary>Creates the host (nothing runs until <see cref="Start"/>).</summary>
    /// <param name="config">The configuration (usually the loader's config file).</param>
    /// <param name="environment">Versions and process facts.</param>
    /// <param name="log">The agent's log.</param>
    /// <param name="unity">Unity access. Without it there is no main-thread pump: main-thread methods fail with <c>MAIN_THREAD_UNAVAILABLE</c>.</param>
    /// <param name="loader">The loader, when running inside one.</param>
    /// <param name="pipeName">The pipe name (default <c>ulm-agent-&lt;pid&gt;</c>).</param>
    /// <param name="createPipe">A custom pipe transport factory (e.g. to force the TCP fallback).</param>
    public AgentHost(IConfigSource config, AgentEnvironment environment, IAgentLogger log, IUnityApi? unity = null, ILoaderApi? loader = null,
        string? pipeName = null, Func<string, IAgentLogger, ITransport>? createPipe = null)
    {
        _config = AgentConfig.Read(config);
        _environment = environment;
        _unity = unity;
        Loader = loader;
        Logs = new LogBuffer(_config.LogBufferSize, LogClock);
        Log = new LevelFilteringLogger(new BufferingLogger(log, Logs), _config.LogLevel);
        _createPipe = createPipe;
        PipeName = pipeName ?? $"ulm-agent-{environment.ProcessId}";
        Token = SessionToken.Generate();
        _discovery = new DiscoveryPublisher(Log);

        Modes = new ModeController(_config.Mode, Log);
        Capabilities = new CapabilitySet();
        Pump = new MainThreadPump(unity ?? HeadlessUnity.Instance, _config.FrameBudgetMs, Log);
        _watchdog = new PumpWatchdog(Pump, unity ?? HeadlessUnity.Instance, _config.StallMs, Log);
        Activity = new ActivityFeed(1000, Log);
        Events = new EventHub(Connections, Log);
        Events.EmittedKinds.Add(EventKinds.JobProgress);
        Events.EmittedKinds.Add(EventKinds.JobFinished);
        Jobs = new JobManager(_config.MaxConcurrentJobs, Events, Pump, Log);
        Data = new DataModel(unity ?? HeadlessUnity.Instance, _config.MaxHandles);
        Code = new CodeModel(Data.Modules);
        Events.EmittedKinds.Add(EventKinds.CodeAssemblyLoaded);
        Data.Modules.AssemblyLoaded += OnAssemblyLoaded;
        Dispatcher = new Dispatcher(Modes, Capabilities, Pump, Activity, Log);
        Session = new SessionHandler(Token, Dispatcher, BuildAgentInfo);
        Dispatcher.Register(new SessionService(this));
        Dispatcher.Register(new JobService(Jobs));
        Dispatcher.Register(new ActivityService(Activity));
        Dispatcher.Register(new BatchService(Dispatcher, Pump));
        Dispatcher.Register(new DiagnosticsService(this));
        Dispatcher.Register(new DataServices(Data, Pump));
        Dispatcher.Register(new CodeServices(Data, Code, Jobs, environment));
        var live = new LiveServices(Data, Code, Pump, Jobs, Modes);
        Dispatcher.Register(live);
        Events.EmittedKinds.Add(EventKinds.SceneChanged);
        if (unity is not null)
        {
            unity.SceneChanged += OnSceneChanged;
        }

        Dispatcher.Register(new ContentServices(Data, Code, Pump, Jobs, environment.AgentVersion));
        Dispatcher.Register(new LogServices(Logs, Events));
        Events.EmittedKinds.Add(EventKinds.Log);
        if (unity is not null)
        {
            unity.LogMessage += Logs.AddUnity;
        }

        if (loader is not null)
        {
            loader.LoaderLog += OnLoaderLog;
        }

        RegisterCleanup("detach the log feeds", () =>
        {
            if (unity is not null)
            {
                unity.LogMessage -= Logs.AddUnity;
            }

            if (loader is not null)
            {
                loader.LoaderLog -= OnLoaderLog;
            }
        });

        Instrumentation = new InstrumentationServices(Data, Code, Pump, Jobs, Events, Logs, Modes, _config.MaxInstrumentedMethods,
            _config.RemoveInstrumentationOnDisconnect, environment.AgentVersion, Warn);
        Dispatcher.Register(Instrumentation);
        RegisterCleanup("remove instrumentation", Instrumentation.Dispose);
        var contextServices = new ContextServices(Data, Pump, Events, Instrumentation.Instrumenter, Log);
        Execution = new ExecutionServices(contextServices, loader, Warn);
        Dispatcher.Register(Execution);
        Instrumentation.RegisterTrigger(Execution.Trigger);
        RegisterCleanup("revert live patches", Execution.Dispose);
        Events.EmittedKinds.Add(EventKinds.ExecEmit);
        Dispatcher.Register(new MetricsServices(Pump, unity ?? HeadlessUnity.Instance, Jobs, environment.AgentVersion));
        Screenshots = new ScreenshotServices(Data, Pump, unity ?? HeadlessUnity.Instance);
        Dispatcher.Register(Screenshots);
        var control = new ControlServices(Data, Pump, unity ?? HeadlessUnity.Instance);
        Dispatcher.Register(control);
        Instrumentation.RegisterTrigger(control.Trigger);
        Rules = new RuleServices(Data, Pump, unity ?? HeadlessUnity.Instance, Logs, Events, Modes, Instrumentation.Hooks,
            new RuleActors(control, Screenshots, live, Execution.Snippets, Activity),
            new RuleLimits
            {
                MaxActive = _config.RulesMaxActive,
                MaxFiresPerMinute = _config.RulesMaxFiresPerMinute,
                MaxCapturesPerMinute = _config.RulesMaxCapturesPerMinute,
                MaxPauseMs = _config.RulesMaxPauseMs,
                RemoveOnDisconnect = _config.RemoveInstrumentationOnDisconnect,
            },
            Warn);
        Dispatcher.Register(Rules);
        RegisterCleanup("end rules and release their pauses", Rules.Dispose);
        Events.EmittedKinds.Add(EventKinds.RuleFired);
        Events.EmittedKinds.Add(EventKinds.RuleProgress);
        Dispatcher.Register(new TestServices(contextServices, Execution.Assemblies, Jobs, Logs, unity ?? HeadlessUnity.Instance, environment.AgentVersion,
            environment.UnityVersion ?? string.Empty));
        foreach (var kind in new[] { EventKinds.TestStarted, EventKinds.TestResult, EventKinds.TestFinished })
        {
            Events.EmittedKinds.Add(kind);
        }

        var probes = new ProbeServices(new Probes.ProbeExecutor(Dispatcher, Data, Pump, Jobs), Jobs, Events, Modes);
        Dispatcher.Register(probes);
        RegisterCleanup("disarm waiting probes", probes.Dispose);
        Events.EmittedKinds.Add(EventKinds.ProbeProgress);
        foreach (var kind in new[] { EventKinds.HookHits, EventKinds.TraceRecords, EventKinds.WatchChanges, EventKinds.EventRaised, EventKinds.Exception, EventKinds.AgentWarning })
        {
            Events.EmittedKinds.Add(kind);
        }

        Capabilities.SetModule("firstChanceExceptions", ExceptionMonitor.FirstChanceSupported, null, ExceptionMonitor.FirstChanceSupported ? null : "The runtime has no AppDomain.FirstChanceException.");
        var ui = unity?.Ui;
        Capabilities.SetModule("ugui", ui?.UguiStatus.Available ?? false, ui?.UguiStatus.Version, ui is null ? "There is no game (no Unity)." : ui.UguiStatus.Reason);
        Capabilities.SetModule("tmp", ui?.TmpStatus.Available ?? false, ui?.TmpStatus.Version, ui is null ? "There is no game (no Unity)." : ui.TmpStatus.Reason);
        if (unity?.Content is { } content)
        {
            var addressables = content.AddressablesStatus;
            Capabilities.SetModule("addressables", addressables.Available, addressables.Version, addressables.Reason);
        }

        foreach (var warning in _config.Warnings)
        {
            Log.Warning(warning);
        }

        var overlaySettings = OverlaySettings.Read(config);
        foreach (var warning in overlaySettings.Warnings)
        {
            Log.Warning(warning);
        }

        if (overlaySettings.Enabled)
        {
            Overlay = CreateOverlay(overlaySettings, control);
        }
    }

    /// <summary>The overlay's model layer, when <c>Overlay.Enabled</c> (a renderer draws it).</summary>
    public OverlayController? Overlay { get; }

    /// <summary>The data model: anchors, handles, variables, refs and the value codec.</summary>
    public DataModel Data { get; }

    /// <summary>Code introspection: assemblies, types and cross-references.</summary>
    public CodeModel Code { get; }

    /// <summary>The session token of this start.</summary>
    public SessionToken Token { get; }

    /// <summary>The effective configuration.</summary>
    public AgentConfig Config => _config;

    /// <summary>The agent's log (its level follows <c>Agent.LogLevel</c> and <c>agent.logLevel</c>).</summary>
    public LevelFilteringLogger Log { get; }

    /// <summary>The loader, if any.</summary>
    public ILoaderApi? Loader { get; }

    /// <summary>The pipe name the pipe transport uses.</summary>
    public string PipeName { get; }

    /// <summary>The permission mode.</summary>
    public ModeController Modes { get; }

    /// <summary>Optional capabilities (module binders report here).</summary>
    public CapabilitySet Capabilities { get; }

    /// <summary>The main-thread pump.</summary>
    public MainThreadPump Pump { get; }

    /// <summary>The activity and audit feed.</summary>
    public ActivityFeed Activity { get; }

    /// <summary>Event delivery.</summary>
    public EventHub Events { get; }

    /// <summary>Background jobs.</summary>
    public JobManager Jobs { get; }

    /// <summary>Method dispatch (later components register their services here).</summary>
    public Dispatcher Dispatcher { get; }

    /// <summary>The connection-facing request handling.</summary>
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

    /// <summary>Registers a cleanup to run at shutdown (in reverse order of registration), e.g. removing patches.</summary>
    public void RegisterCleanup(string name, Action action)
    {
        lock (_gate)
        {
            _cleanups.Add((name, action));
        }
    }

    /// <summary>Starts the pump (when Unity is available), event delivery, the transport and the discovery file.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
        }

        if (_unity is not null)
        {
            Pump.Start();
            _watchdog.Start();
        }

        Events.Start();
        _transport = TransportFactory.Start(_config.Transport, PipeName, Log, OnAccepted, _createPipe);
        _discovery.Publish(_config.ProvidersDir, BuildDiscoveryFile());
        Log.Info($"Agent ready: {_environment.AgentVersion}, protocol {ProtocolVersion.Text}, mode {AgentModes.ToWire(Modes.Current)}, "
            + (_transport.Kind == "pipe" ? $"pipe {_transport.PipeName}." : $"tcp 127.0.0.1:{_transport.Port}."));
    }

    /// <summary>Sends an event to every connection subscribed to its kind.</summary>
    public void Publish(string kind, ProtocolMessage payload, JsonObject? context = null) => Events.Publish(kind, payload, context);

    /// <summary>Automation rules.</summary>
    internal RuleServices Rules { get; }

    /// <summary>Instrumentation: hooks, traces, profiles, verification, watches, event subscriptions, exceptions.</summary>
    internal InstrumentationServices Instrumentation { get; }

    /// <summary>Screenshots (the overlay sets their hooks).</summary>
    internal ScreenshotServices Screenshots { get; }

    /// <summary>The unified log (Unity, the loader, the agent).</summary>
    public LogBuffer Logs { get; }

    /// <summary>Snippets, live patches and mod hot-reload.</summary>
    internal ExecutionServices Execution { get; }

    // The frame (null before the first) and the game's real time, for log entries written on any thread.
    private (long? Frame, long RealtimeMs) LogClock()
    {
        var pump = Pump;
        if (pump is null || !pump.HasTicked)
        {
            return (null, 0);
        }

        var clock = pump.Clock;
        return (clock.FrameCount, (long)(clock.Realtime * 1000));
    }

    // The loader relays Unity's own log ("Unity Log") and the agent's lines too: those already have their own feed.
    private void OnLoaderLog(LoaderLogEntry entry)
    {
        if (entry.Source == "Unity Log" || entry.Source.StartsWith("UnityRuntimeAnalysisAgent", StringComparison.Ordinal))
        {
            return;
        }

        Logs.AddLoader(entry);
    }

    // An agent.warning to subscribers, and the log.
    private void Warn(string code, string message, JsonObject data)
    {
        Log.Warning(message);
        if (!_stopped && Events.HasSubscribers(EventKinds.AgentWarning))
        {
            Events.Publish(EventKinds.AgentWarning, new AgentWarningParams { Code = code, Message = message, Data = data });
        }
    }

    /// <summary>Removes every side effect of the agent (idempotent).</summary>
    public void Shutdown()
    {
        List<(string Name, Action Action)> cleanups;
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            cleanups = Enumerable.Reverse(_cleanups).ToList();
        }

        Step("cancel jobs", () => Jobs.CancelAll());
        foreach (var (name, action) in cleanups)
        {
            Step(name, action);
        }

        Step("stop events", Events.Dispose);
        Step("close connections", () =>
        {
            _transport?.Dispose();
            foreach (var connection in Connections())
            {
                connection.Close("agent shutdown");
            }
        });
        Step("release handles and variables", Data.Dispose);
        Step("delete the discovery file", _discovery.Dispose);
        Step("stop the watchdog", _watchdog.Dispose);
        Step("destroy the pump host", Pump.Stop);
        Log.Info("Agent stopped.");
    }

    /// <inheritdoc />
    public void Dispose() => Shutdown();

    // code.assemblyLoaded: the summary hashes the file, so it's built off the loading thread, and only when someone listens.
    private void OnAssemblyLoaded(Assembly assembly)
    {
        if (_stopped || !Events.HasSubscribers(EventKinds.CodeAssemblyLoaded))
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var clock = Pump.Clock;
                Events.Publish(EventKinds.CodeAssemblyLoaded, new AssemblyLoadedEventParams
                {
                    Assembly = Code.Catalog.Summary(assembly),
                    Frame = clock.FrameCount,
                    RealtimeMs = (long)(clock.Realtime * 1000),
                });
            }
            catch (Exception e)
            {
                Log.Warning($"code.assemblyLoaded for {assembly.GetName().Name} failed: {e.Message}");
            }
        });
    }

    // Unity raises scene callbacks on the main thread; the event is sent from there (sending only queues).
    private void OnSceneChanged(SceneChange change)
    {
        if (_stopped || !Events.HasSubscribers(EventKinds.SceneChanged))
        {
            return;
        }

        try
        {
            var clock = Pump.Clock;
            Events.Publish(EventKinds.SceneChanged, new SceneChangedEventParams
            {
                Change = change.Change,
                Scene = LiveServices.SceneInfo(change.Scene),
                PreviousActive = change.PreviousActive,
                Mode = change.Mode,
                Frame = clock.FrameCount,
                RealtimeMs = (long)(clock.Realtime * 1000),
            });
        }
        catch (Exception e)
        {
            Log.Warning($"scene.changed for {change.Scene.Name} failed: {e.Message}");
        }
    }

    internal AgentInfo BuildAgentInfo()
    {
        var pumpAlive = Pump.IsStarted && (_unity?.IsPumpHostAlive ?? false);
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
            Loader = new LoaderInfo { Name = Loader?.LoaderName ?? _environment.LoaderName, Version = Loader?.LoaderVersion ?? _environment.LoaderVersion },
            Mode = Modes.Current,
            Transport = _transport?.Kind ?? "pipe",
            StartedAt = FormatTimestamp(Session.StartedUtc),
            UptimeMs = (long)(DateTime.UtcNow - Session.StartedUtc).TotalMilliseconds,
            Limits = LimitsJson(),
            Health = new AgentHealth
            {
                Pump = new PumpHealth
                {
                    Alive = pumpAlive,
                    LastTickFrame = Pump.HasTicked ? Pump.Clock.FrameCount : null,
                    QueueLength = Pump.QueueLength,
                    StalledMs = _watchdog.StalledMs,
                    RecreatedCount = Pump.RecreatedCount,
                },
                Connections = ConnectionCount,
                JobsRunning = Jobs.Running,
                HooksActive = Instrumentation.Hooks.Count,
                PatchesActive = Instrumentation.Instrumenter.PatchedMethods + Execution.Patches.PatchedMethods,
                Handles = Data.Handles.Count,
                AssembliesLoadedByAgent = Execution.Assemblies.Loaded,
            },
        };
    }

    internal AgentCapabilities BuildCapabilities()
    {
        var methods = Dispatcher.Methods.Select(m => new MethodCapability
        {
            Name = m.Descriptor.Name,
            Thread = m.Descriptor.Thread switch { MethodThread.Main => "main", MethodThread.Mixed => "mixed", _ => "any" },
            MinMode = m.Descriptor.MinMode,
            Mutating = m.Descriptor.Mutating,
            Job = m.Descriptor.Job,
            DefaultTimeoutMs = m.DefaultTimeoutMs,
            MaxTimeoutMs = m.MaxTimeoutMs,
            Requires = m.Descriptor.Requires.Count > 0 ? m.Descriptor.Requires.ToList() : null,
        }).ToList();
        methods.Add(new MethodCapability { Name = Methods.Hello, Thread = "any", MinMode = AgentMode.ReadOnly });
        return new AgentCapabilities
        {
            AgentVersion = _environment.AgentVersion,
            ApiVersion = _environment.ApiVersion,
            Protocol = ProtocolVersionInfo.Current,
            Methods = methods.OrderBy(m => m.Name, StringComparer.Ordinal).ToList(),
            EventKinds = Events.EmittedKinds.ToList(),
            Modules = Capabilities.Modules(),
            Limits = LimitsJson(),
        };
    }

    private IReadOnlyList<Connection> Connections()
    {
        lock (_gate)
        {
            return _connections.ToArray();
        }
    }

    private void Step(string name, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            Log.Error($"Shutdown step '{name}' failed.", e);
        }
    }

    private JsonObject LimitsJson()
    {
        var limits = new JsonObject();
        foreach (var pair in _config.Limits())
        {
            limits.Add(pair.Key, new JsonNumber(pair.Value));
        }

        return limits;
    }

    private void OnAccepted(Stream stream, Action release) => AcceptConnection(stream, _transport?.Kind ?? "?", release);

    /// <summary>Serves a client over an already-connected stream (the transports use this; so can in-process
    /// clients). Returns the connection, or null if the host has stopped. <paramref name="release"/> runs when it closes.</summary>
    public Connection? AcceptConnection(Stream stream, string transportKind, Action? release = null)
    {
        release ??= () => { };
        var connection = new Connection(stream, transportKind, _config.MaxFrameBytes, Log, Session.Handle)
        {
            MaxEventQueueBytes = _config.MaxEventQueueBytes,
        };
        lock (_gate)
        {
            if (_stopped)
            {
                stream.Dispose();
                release();
                return null;
            }

            _connections.Add(connection);
        }

        connection.Closed += (_, _) =>
        {
            lock (_gate)
            {
                _connections.Remove(connection);
            }

            Session.CancelAll(connection);
            Instrumentation.OnDisconnect(connection.Id.ToString(CultureInfo.InvariantCulture));
            Rules.OnDisconnect(connection.Id.ToString(CultureInfo.InvariantCulture));
            release();
        };
        connection.Start();
        return connection;
    }

    // The overlay's model: E-STOP wired to the services, prompt answers as events, ticked every frame on the main thread.
    private OverlayController CreateOverlay(OverlaySettings settings, ControlServices control)
    {
        var steps = new EStopSteps
        {
            LowerMode = () =>
            {
                Modes.Lower(AgentMode.ReadOnly);
                return Modes.Current;
            },
            RevertPatches = () => Execution.RevertPatches(),
            RemoveInstrumentation = Instrumentation.RemoveAll,
            CancelJobs = () => Jobs.CancelActive(),
            CancelRules = () => Rules.CancelAll(),
            Pause = () => control.Pause(),
            Emit = report => Events.Publish(EventKinds.OverlayEstop, report),
            Disconnect = () =>
            {
                List<Connection> open;
                lock (_gate)
                {
                    open = _connections.ToList();
                }

                foreach (var connection in open)
                {
                    connection.CloseWhenSent("E-STOP");
                }
            },
            Clock = () => (Pump.Clock.FrameCount, (long)(Pump.Clock.Realtime * 1000)),
        };
        var overlay = new OverlayController(settings, Loader as IConfigWriter, Modes, new DispatcherQueries(Dispatcher, Pump), steps, Log, Data.Handles.Mint);
        foreach (var kind in new[] { EventKinds.OverlayEstop, EventKinds.OverlayPromptResult, EventKinds.OverlayPicked, EventKinds.OverlayRequest })
        {
            Events.EmittedKinds.Add(kind);
        }

        overlay.Prompts.Answered += (prompt, button) => Events.Publish(EventKinds.OverlayPromptResult, new PromptResultEventParams { Id = prompt.Id, Button = button });
        Action<FrameTime> tick = clock => overlay.Tick(clock.Realtime);
        Pump.Ticked += tick;
        RegisterCleanup("stop the overlay's model", () => Pump.Ticked -= tick);
        return overlay;
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
        Loader = new LoaderInfo { Name = Loader?.LoaderName ?? _environment.LoaderName, Version = Loader?.LoaderVersion ?? _environment.LoaderVersion },
        UnityVersion = _environment.UnityVersion,
        Mode = Modes.Current,
        StartedAt = FormatTimestamp(Session.StartedUtc),
    };

    private static string FormatTimestamp(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>Stands in for Unity when the host runs without it (tools): the pump never starts.</summary>
    private sealed class HeadlessUnity : IUnityApi
    {
        public static readonly HeadlessUnity Instance = new();

        public bool IsPumpHostAlive => false;

        public void CreatePumpHost(Action tick, Action endOfFrame)
        {
        }

        public void RecreatePumpHost()
        {
        }

        public void DestroyPumpHost()
        {
        }

        public FrameTime ReadFrameTime() => default;

        public bool IsDestroyed(object unityObject) => unityObject is null;

        public UnityObjectFacts? Describe(object unityObject) => null;

        public object? FindGameObject(string path, string? scene) => null;

        public object? GetComponent(object gameObjectOrComponent, Type componentType) => null;

        public object? FindChild(object gameObjectOrComponent, string path) => null;

        public SceneAddress? Locate(object unityObject) => null;

        public IReadOnlyDictionary<Type, int> CountObjectsByType(Type baseType) => new Dictionary<Type, int>();

        public IReadOnlyList<SceneFacts> Scenes() => Array.Empty<SceneFacts>();

        public int SceneCountInBuildSettings => 0;

        public IReadOnlyList<object> SceneRoots(int sceneHandle) => Array.Empty<object>();

        public GameObjectFacts? DescribeGameObject(object gameObjectOrComponent) => null;

        public IReadOnlyList<object> FindObjectsOfTypeAll(Type type) => Array.Empty<object>();

        public object CreateGameObject(string name, object? parent, string? scene) => throw new NotSupportedException("No Unity in this process.");

        public object Instantiate(object original, object? parent, object? position, object? rotation) => throw new NotSupportedException("No Unity in this process.");

        public void SetActive(object gameObject, bool active) => throw new NotSupportedException("No Unity in this process.");

        public void Destroy(object unityObject, bool immediate) => throw new NotSupportedException("No Unity in this process.");

        public object AddComponent(object gameObjectOrComponent, Type componentType) => throw new NotSupportedException("No Unity in this process.");

        public object CreateScriptableObject(Type type) => throw new NotSupportedException("No Unity in this process.");

        public IContentApi? Content => null;

        public IGameControl? Control => null;

        public IUiApi? Ui => null;

        public ICaptureApi? Capture => null;

        public ProfilerMemory ReadProfilerMemory() => default;

        public event Action<string, string, string>? LogMessage
        {
            add { }
            remove { }
        }

        public event Action<SceneChange>? SceneChanged
        {
            add { }
            remove { }
        }
    }
}
