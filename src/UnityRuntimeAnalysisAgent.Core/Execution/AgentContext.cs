using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Api;
using UnityRuntimeAnalysisAgent.Core.Data;
using UnityRuntimeAnalysisAgent.Core.Hosting;
using UnityRuntimeAnalysisAgent.Core.Instrumentation;
using UnityRuntimeAnalysisAgent.Core.Runtime;

namespace UnityRuntimeAnalysisAgent.Core.Execution;

/// <summary>What <see cref="AgentContext"/> needs from the agent.</summary>
internal sealed class ContextServices
{
    public ContextServices(DataModel data, MainThreadPump pump, EventHub events, Instrumenter instrumenter, IAgentLogger log)
    {
        Data = data;
        Pump = pump;
        Events = events;
        Instrumenter = instrumenter;
        Log = log;
    }

    public DataModel Data { get; }

    public MainThreadPump Pump { get; }

    public EventHub Events { get; }

    public Instrumenter Instrumenter { get; }

    public IAgentLogger Log { get; }
}

/// <summary>
/// The <see cref="IAgentContext"/> of one snippet run (collecting its log lines, emitted events and result), or the
/// agent-wide one live patches reach through <see cref="AgentApi.Current"/> (collecting nothing).
/// </summary>
internal sealed class AgentContext : IAgentContext, IAgentLog, IAgentWait, IAgentInstrumentation
{
    public const int MaxCollected = 1000;

    private readonly ContextServices _services;
    private readonly string? _session;
    private readonly bool _collect;
    private readonly List<string> _logs = new();
    private readonly List<EmittedItem> _emitted = new();
    private readonly List<HitCounter> _counters = new();
    private int _droppedLogs;
    private int _droppedEmits;

    public AgentContext(ContextServices services, JsonObject? args, string? session, IDictionary<string, object?> sessionState,
        CancellationToken cancellation, string? outDir, bool collect)
    {
        _services = services;
        _session = session;
        _collect = collect;
        Args = args is null ? new Dictionary<string, object?>() : (IReadOnlyDictionary<string, object?>)PlainJson.ToObject(args)!;
        Session = sessionState;
        Cancellation = cancellation;
        OutDir = outDir;
        Vars = new AgentVars(services.Data, () => services.Pump.Clock.FrameCount);
    }

    public IReadOnlyDictionary<string, object?> Args { get; }

    public IAgentVars Vars { get; }

    public IDictionary<string, object?> Session { get; }

    public IAgentLog Log => this;

    public IAgentWait Wait => this;

    public IAgentInstrumentation Hooks => this;

    public CancellationToken Cancellation { get; }

    public string? OutDir { get; }

    /// <summary>The value set with <see cref="Return"/>.</summary>
    public object? Returned { get; private set; }

    public List<string> Logs
    {
        get
        {
            lock (_logs)
            {
                var logs = new List<string>(_logs);
                if (_droppedLogs > 0)
                {
                    logs.Add($"[agent] {_droppedLogs} more line(s) not kept");
                }

                return logs;
            }
        }
    }

    public List<EmittedItem> Emitted
    {
        get
        {
            lock (_emitted)
            {
                var emitted = new List<EmittedItem>(_emitted);
                if (_droppedEmits > 0)
                {
                    emitted.Add(new EmittedItem { Kind = "agent.dropped", Payload = new JsonNumber((long)_droppedEmits) });
                }

                return emitted;
            }
        }
    }

    public long Handle(object value) => _services.Data.Handles.Mint(value ?? throw new ArgumentNullException(nameof(value)));

    public object? Resolve(long handle)
    {
        try
        {
            return _services.Data.Handles.Resolve(handle);
        }
        catch (ProtocolException e)
        {
            throw new ArgumentException(e.Message, nameof(handle));
        }
    }

    public MemberInfo Resolve(string anchorJson)
    {
        try
        {
            return _services.Data.Anchors.ResolveMember(AnchorResolver.Read(JsonValue.Parse(anchorJson), "anchor"), "anchor");
        }
        catch (Exception e) when (e is ProtocolException or FormatException)
        {
            throw new ArgumentException(e.Message, nameof(anchorJson));
        }
    }

    public void Return(object? value) => Returned = value;

    public void Emit(string kind, object? payload = null)
    {
        if (string.IsNullOrEmpty(kind))
        {
            throw new ArgumentException("An emitted event needs a kind.", nameof(kind));
        }

        var json = payload is null ? null : _services.Data.Writer(ViewOptions.From(null), _services.Pump.Clock.FrameCount).Write(payload, new Place());
        _services.Events.Publish(EventKinds.ExecEmit, new ExecEmitEventParams { Kind = kind, Payload = json, Session = _session });
        if (_collect)
        {
            lock (_emitted)
            {
                if (_emitted.Count < MaxCollected)
                {
                    _emitted.Add(new EmittedItem { Kind = kind, Payload = json });
                }
                else
                {
                    _droppedEmits++;
                }
            }
        }
    }

    // ---- IAgentLog --------------------------------------------------------------------------------------------------------

    void IAgentLog.Info(string message) => Write(AgentLogLevel.Info, "info", message);

    void IAgentLog.Warning(string message) => Write(AgentLogLevel.Warning, "warning", message);

    void IAgentLog.Error(string message) => Write(AgentLogLevel.Error, "error", message);

    // ---- IAgentWait -------------------------------------------------------------------------------------------------------

    object IAgentWait.Frames(int count) => PumpWait.Frames(count);

    object IAgentWait.Seconds(double seconds) => PumpWait.Realtime(seconds * 1000.0);

    object IAgentWait.Until(Func<bool> condition, int timeoutMs) => PumpWait.Until(condition, Math.Max(1, timeoutMs));

    object IAgentWait.EndOfFrame => PumpWait.EndOfFrame;

    // ---- IAgentInstrumentation --------------------------------------------------------------------------------------------

    IAgentHitCounter IAgentInstrumentation.Count(MethodBase method)
    {
        var counter = new HitCounter(method ?? throw new ArgumentNullException(nameof(method)), _services.Instrumenter);
        try
        {
            _services.Instrumenter.Attach(method, counter, force: false);
        }
        catch (ProtocolException e)
        {
            throw new InvalidOperationException(e.Message);
        }

        if (_collect)
        {
            lock (_counters)
            {
                _counters.Add(counter);
            }
        }

        return counter;
    }

    /// <summary>Removes the run's hit counters (a run's instrumentation never outlives it).</summary>
    public void End()
    {
        List<HitCounter> counters;
        lock (_counters)
        {
            counters = new List<HitCounter>(_counters);
            _counters.Clear();
        }

        foreach (var counter in counters)
        {
            counter.Dispose();
        }
    }

    private void Write(AgentLogLevel level, string label, string message)
    {
        _services.Log.Log(level, (_session is null ? "[snippet] " : $"[snippet {_session}] ") + message);
        if (!_collect)
        {
            return;
        }

        lock (_logs)
        {
            if (_logs.Count < MaxCollected)
            {
                _logs.Add($"[{label}] {message}");
            }
            else
            {
                _droppedLogs++;
            }
        }
    }

    private sealed class HitCounter : IAgentHitCounter, IMethodSink
    {
        private readonly Instrumenter _instrumenter;
        private long _hits;
        private int _disposed;

        public HitCounter(MethodBase method, Instrumenter instrumenter)
        {
            Method = method;
            _instrumenter = instrumenter;
        }

        public MethodBase Method { get; }

        public long Hits => Interlocked.Read(ref _hits);

        public object WaitForHits(long count, int timeoutMs) => PumpWait.Until(() => Hits >= count, Math.Max(1, timeoutMs));

        public object? Enter(CallContext call)
        {
            Interlocked.Increment(ref _hits);
            return null;
        }

        public void Exit(CallContext call, object token, object? result, bool hasResult, long elapsedTicks)
        {
        }

        public void Throw(CallContext call, object token, Exception exception, long elapsedTicks)
        {
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _instrumenter.Detach(Method, this);
            }
        }
    }
}

/// <summary>The agent's variables as <see cref="IAgentVars"/>.</summary>
internal sealed class AgentVars : IAgentVars
{
    private readonly DataModel _data;
    private readonly Func<long> _frame;

    public AgentVars(DataModel data, Func<long> frame)
    {
        _data = data;
        _frame = frame;
    }

    public IReadOnlyList<string> Names => _data.Variables.List().Select(v => v.Name).ToList();

    public object? Get(string name) => TryGet(name, out var value) ? value : throw new KeyNotFoundException($"No variable '{name}'.");

    public bool TryGet(string name, out object? value)
    {
        value = null;
        var variable = _data.Variables.List().FirstOrDefault(v => v.Name == name);
        if (variable is null)
        {
            return false;
        }

        try
        {
            value = variable.Kind switch
            {
                VariableKind.Handle => _data.Handles.Resolve(variable.H),
                VariableKind.Static => _data.Anchors.ResolveType(variable.Static!, "variable"),
                _ => PlainJson.ToObject(variable.Value),
            };
        }
        catch (ProtocolException e)
        {
            throw new ArgumentException($"Variable '{name}': {e.Message}", nameof(name));
        }

        return true;
    }

    public void Set(string name, object? value)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("A variable needs a name.", nameof(name));
        }

        if (value is null || value is string || value is bool || value.GetType().IsPrimitive || value is decimal)
        {
            _data.Variables.SetValue(name, _data.Writer(ViewOptions.From(null), _frame()).Write(value, new Place(), fullString: true));
        }
        else
        {
            _data.Variables.SetHandle(name, _data.Handles.Mint(value));
        }
    }

    public bool Delete(string name) => _data.Variables.Delete(name);
}

/// <summary>JSON as plain CLR values: numbers as long or double, strings, booleans, null, <c>object?[]</c> and
/// dictionaries.</summary>
internal static class PlainJson
{
    public static object? ToObject(JsonValue? json) => json switch
    {
        null or JsonNull => null,
        JsonBoolean b => b.Value,
        JsonString s => s.Value,
        JsonNumber n => n.TryGetInt64(out var l) ? l : (object)n.GetDouble(),
        JsonArray a => a.Select(ToObject).ToArray(),
        JsonObject o => o.ToDictionary(p => p.Key, p => ToObject(p.Value), StringComparer.Ordinal),
        _ => json.ToString(),
    };
}
