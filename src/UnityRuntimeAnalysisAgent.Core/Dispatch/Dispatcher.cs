using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Envelopes;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Hosting;
using UnityRuntimeAnalysisAgent.Core.Runtime;
using UnityRuntimeAnalysisAgent.Core.Transport;

namespace UnityRuntimeAnalysisAgent.Core.Dispatch;

/// <summary>
/// Marks a method implementation. The method's thread, required mode, job flag, mutating flag and required capabilities
/// come from the protocol (<see cref="MethodRegistry"/>); the attribute adds the timeout policy.
/// </summary>
/// <remarks>
/// Signatures: <c>ProtocolMessage M(RequestContext context, TParams parameters)</c> or <c>ProtocolMessage M(RequestContext context)</c>.
/// Main-thread methods may instead return an iterator (a routine yielding <see cref="PumpWait"/>s, then the result).
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RpcMethodAttribute : Attribute
{
    /// <summary>Declares the implementation of protocol method <paramref name="name"/>.</summary>
    public RpcMethodAttribute(string name) => Name = name;

    /// <summary>The protocol method name.</summary>
    public string Name { get; }

    /// <summary>Timeout when the request doesn't set <c>timeoutMs</c>.</summary>
    public int DefaultTimeoutMs { get; set; } = 10_000;

    /// <summary>The largest <c>timeoutMs</c> a request may ask for.</summary>
    public int MaxTimeoutMs { get; set; } = 60_000;
}

/// <summary>What a method implementation sees of its request.</summary>
public sealed class RequestContext
{
    /// <summary>Creates the context of a request.</summary>
    public RequestContext(string id, string method, JsonObject? parameters, JsonObject? context, string source, string? client, Connection? connection, CancellationToken cancellation)
    {
        Id = id;
        Method = method;
        Params = parameters ?? new JsonObject();
        Context = context;
        Source = source;
        Client = client;
        Connection = connection;
        Cancellation = cancellation;
    }

    /// <summary>The request id.</summary>
    public string Id { get; }

    /// <summary>The method.</summary>
    public string Method { get; }

    /// <summary>The params object (empty when absent).</summary>
    public JsonObject Params { get; }

    /// <summary>The caller's opaque context: echoed into the response and every event the request produces.</summary>
    public JsonObject? Context { get; }

    /// <summary>Who made the call: <c>client</c>, <c>overlay</c> or <c>rule</c>.</summary>
    public string Source { get; }

    /// <summary>The client's name and version, for client calls.</summary>
    public string? Client { get; }

    /// <summary>The client connection, for client calls.</summary>
    public Connection? Connection { get; }

    /// <summary>Cancelled by <c>cancel</c>, the timeout, or the connection closing.</summary>
    public CancellationToken Cancellation { get; internal set; }

    /// <summary>The requested timeout (<c>timeoutMs</c>), if any.</summary>
    public long? TimeoutMs { get; set; }

    /// <summary>The locator of what the call acted on (recorded in the activity feed).</summary>
    public string? Target { get; set; }

    /// <summary>The assembly a call loaded or ran (recorded in the activity feed and the audit log).</summary>
    public AuditedAssembly? Assembly { get; set; }
}

/// <summary>A call's outcome: exactly one of a result and an error.</summary>
public sealed class Outcome
{
    private Outcome(JsonValue? result, ProtocolError? error)
    {
        Result = result;
        Error = error;
    }

    /// <summary>The result (success).</summary>
    public JsonValue? Result { get; }

    /// <summary>The error (failure).</summary>
    public ProtocolError? Error { get; }

    /// <summary>A success.</summary>
    public static Outcome Success(JsonValue result) => new(result, null);

    /// <summary>A failure.</summary>
    public static Outcome Failure(ProtocolError error) => new(null, error);
}

/// <summary>An implemented method: the protocol's description plus the handler and its timeout policy.</summary>
public sealed class MethodEntry
{
    private readonly object _target;
    private readonly MethodInfo _method;
    private readonly bool _takesParams;

    internal MethodEntry(MethodDescriptor descriptor, RpcMethodAttribute attribute, object target, MethodInfo method)
    {
        Descriptor = descriptor;
        DefaultTimeoutMs = attribute.DefaultTimeoutMs;
        MaxTimeoutMs = attribute.MaxTimeoutMs;
        _target = target;
        _method = method;
        var parameters = method.GetParameters();
        if (parameters.Length is < 1 or > 2 || parameters[0].ParameterType != typeof(RequestContext))
        {
            throw new ArgumentException($"{method.DeclaringType?.Name}.{method.Name}: expected (RequestContext[, TParams]).");
        }

        _takesParams = parameters.Length == 2;
        ParamsType = _takesParams ? parameters[1].ParameterType : null;
    }

    /// <summary>The protocol's description of the method.</summary>
    public MethodDescriptor Descriptor { get; }

    /// <summary>Timeout when the request doesn't set one.</summary>
    public int DefaultTimeoutMs { get; }

    /// <summary>The largest timeout a request may ask for.</summary>
    public int MaxTimeoutMs { get; }

    /// <summary>The params type the handler takes, if any.</summary>
    public Type? ParamsType { get; }

    internal ProtocolMessage? Decode(JsonObject parameters) => _takesParams ? Descriptor.ReadParams(parameters, "params") : null;

    internal object? Invoke(RequestContext context, ProtocolMessage? parameters)
    {
        try
        {
            return _method.Invoke(_target, _takesParams ? new object?[] { context, parameters } : new object?[] { context });
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }
}

/// <summary>
/// Routes requests to their implementations: lookup (<c>METHOD_NOT_FOUND</c>), mode check (<c>MODE_FORBIDDEN</c>),
/// capability check (<c>UNSUPPORTED</c>), params decoding (<c>INVALID_PARAMS</c>), timeout (<c>TIMEOUT</c>, or
/// <c>MAIN_THREAD_UNAVAILABLE</c> while the main thread is stalled), then the main-thread pump or a worker thread. Every
/// call ends in exactly one outcome and one activity entry.
/// </summary>
public sealed class Dispatcher
{
    private readonly Dictionary<string, MethodEntry> _methods = new(StringComparer.Ordinal);
    private readonly ModeController _modes;
    private readonly CapabilitySet _capabilities;
    private readonly MainThreadPump _pump;
    private readonly ActivityFeed _activity;
    private readonly IAgentLogger _log;
    private readonly object _gate = new();

    /// <summary>Creates the dispatcher.</summary>
    public Dispatcher(ModeController modes, CapabilitySet capabilities, MainThreadPump pump, ActivityFeed activity, IAgentLogger log)
    {
        _modes = modes;
        _capabilities = capabilities;
        _pump = pump;
        _activity = activity;
        _log = log;
    }

    /// <summary>Registers every <see cref="RpcMethodAttribute"/> method of a service object.</summary>
    public void Register(object service)
    {
        foreach (var method in service.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            var attribute = method.GetCustomAttribute<RpcMethodAttribute>();
            if (attribute is null)
            {
                continue;
            }

            var descriptor = MethodRegistry.Find(attribute.Name)
                ?? throw new ArgumentException($"{service.GetType().Name}.{method.Name}: {attribute.Name} is not a method of the protocol.");
            var entry = new MethodEntry(descriptor, attribute, service, method);
            lock (_gate)
            {
                _methods[attribute.Name] = entry;
            }
        }
    }

    /// <summary>The implemented methods, by name.</summary>
    public IReadOnlyList<MethodEntry> Methods
    {
        get
        {
            lock (_gate)
            {
                return _methods.Values.OrderBy(m => m.Descriptor.Name, StringComparer.Ordinal).ToList();
            }
        }
    }

    /// <summary>The implementation of a method, if this build has one.</summary>
    public MethodEntry? Find(string method)
    {
        lock (_gate)
        {
            return _methods.TryGetValue(method, out var entry) ? entry : null;
        }
    }

    /// <summary>Dispatches a request; <paramref name="done"/> is called exactly once, on some thread.</summary>
    public void Dispatch(RequestContext context, Action<Outcome> done)
    {
        var call = new Call(this, context, done);
        MethodEntry entry;
        ProtocolMessage? parameters;
        try
        {
            entry = Prepare(context, out parameters);
        }
        catch (Exception e)
        {
            call.Fail(e);
            return;
        }

        call.Entry = entry;
        call.ArmTimeout(entry);
        if (entry.Descriptor.Thread == MethodThread.Main)
        {
            _pump.Enqueue(new PumpWork(() => entry.Invoke(context, parameters), call.Succeed, call.Fail, context.Cancellation));
            return;
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                call.Succeed(entry.Invoke(context, parameters));
            }
            catch (Exception e)
            {
                call.Fail(e);
            }
        });
    }

    /// <summary>Dispatches a request and waits for its outcome (for callers already on a worker thread).</summary>
    public Outcome Invoke(RequestContext context)
    {
        Outcome? outcome = null;
        using var finished = new ManualResetEventSlim(false);
        Dispatch(context, o =>
        {
            outcome = o;
            finished.Set();
        });
        finished.Wait();
        return outcome!;
    }

    /// <summary>Runs a request right now on the calling thread, which must be the main thread (same-frame batches).</summary>
    public Outcome InvokeInline(RequestContext context)
    {
        Outcome? outcome = null;
        var call = new Call(this, context, o => outcome = o);
        try
        {
            var entry = Prepare(context, out var parameters);
            call.Entry = entry;
            call.Succeed(_pump.RunInline(() => entry.Invoke(context, parameters)));
        }
        catch (Exception e)
        {
            call.Fail(e);
        }

        return outcome!;
    }

    private MethodEntry Prepare(RequestContext context, out ProtocolMessage? parameters)
    {
        var entry = Find(context.Method);
        if (entry is null)
        {
            var known = MethodRegistry.Find(context.Method) is not null;
            throw new ProtocolException(
                ErrorCodes.MethodNotFound,
                known ? $"'{context.Method}' isn't implemented by this agent build." : $"Unknown method '{context.Method}'.",
                AgentErrors.Data(("hint", new JsonString("Check agent.capabilities."))));
        }

        var descriptor = entry.Descriptor;
        if (!_modes.Allows(descriptor.MinMode))
        {
            throw AgentErrors.ModeForbidden(descriptor.Name, descriptor.MinMode, _modes.Current);
        }

        var missing = descriptor.Requires.Where(tag => !_capabilities.IsAvailable(tag)).ToList();
        if (missing.Count > 0)
        {
            var requires = new JsonArray();
            foreach (var tag in missing)
            {
                requires.Add(new JsonString(tag));
            }

            throw new ProtocolException(ErrorCodes.Unsupported, $"{descriptor.Name} needs {string.Join(", ", missing)}, which this game doesn't have.",
                AgentErrors.Data(("requires", requires)));
        }

        parameters = entry.Decode(context.Params);
        return entry;
    }

    private void Record(RequestContext context, MethodEntry? entry, DateTime startedUtc, long durationMs, Outcome outcome)
    {
        try
        {
            var response = new JsonObject();
            if (outcome.Error is { } error)
            {
                response.Add("error", AgentErrors.ToJson(error));
            }
            else
            {
                response.Add("result", outcome.Result ?? JsonNull.Instance);
            }

            var mutating = entry?.Descriptor.Mutating ?? false;
            _activity.Record(context.Method, context.Source, context.Client, mutating, startedUtc, durationMs, context.Params, response,
                outcome.Error?.Code, context.Target, context.Assembly);
        }
        catch (Exception e)
        {
            _log.Error("Recording activity failed.", e);
        }
    }

    /// <summary>One call in flight: completes once, whichever of result, error, cancellation or timeout comes first.</summary>
    private sealed class Call
    {
        private readonly Dispatcher _dispatcher;
        private readonly RequestContext _context;
        private readonly Action<Outcome> _done;
        private readonly DateTime _startedUtc = DateTime.UtcNow;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private CancellationTokenSource? _cancellation;
        private CancellationTokenRegistration _onCancel;
        private Timer? _timer;
        private int _completed;

        public Call(Dispatcher dispatcher, RequestContext context, Action<Outcome> done)
        {
            _dispatcher = dispatcher;
            _context = context;
            _done = done;
        }

        public MethodEntry? Entry { get; set; }

        /// <summary>Arms the timeout, and links the handler's token to both the caller's cancellation and the timeout.</summary>
        public void ArmTimeout(MethodEntry entry)
        {
            var requested = _context.TimeoutMs ?? entry.DefaultTimeoutMs;
            var timeoutMs = (int)Math.Max(1, Math.Min(requested, entry.MaxTimeoutMs));
            var external = _context.Cancellation;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(external);
            _context.Cancellation = _cancellation.Token;
            _onCancel = external.Register(() => Fail(new OperationCanceledException(external)));
            _timer = new Timer(_ =>
            {
                var stalled = entry.Descriptor.Thread == MethodThread.Main && _dispatcher._pump.IsStalled;
                Fail(stalled
                    ? new ProtocolException(ErrorCodes.MainThreadUnavailable, "The game's main thread isn't responding (loading or hung).")
                    : new ProtocolException(ErrorCodes.Timeout, $"{entry.Descriptor.Name} didn't finish within {timeoutMs} ms."));
                // Answered already; now ask the handler to stop (cooperatively).
                try
                {
                    _cancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }, null, timeoutMs, Timeout.Infinite);
        }

        public void Succeed(object? result)
        {
            if (result is ProtocolMessage message)
            {
                Complete(Outcome.Success(message.ToJson()));
            }
            else
            {
                Fail(new InvalidOperationException($"{_context.Method} returned {(result is null ? "no result" : result.GetType().Name)} instead of a protocol message."));
            }
        }

        public void Fail(Exception error)
        {
            Complete(Outcome.Failure(AgentErrors.FromException(error, _dispatcher._log, _context.Method)));
        }

        private void Complete(Outcome outcome)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
            {
                return;
            }

            // The handler may still hold the token, so the source isn't disposed; the timer and the registration are.
            _onCancel.Dispose();
            _timer?.Dispose();
            _dispatcher.Record(_context, Entry, _startedUtc, _clock.ElapsedMilliseconds, outcome);
            try
            {
                _done(outcome);
            }
            catch (Exception e)
            {
                _dispatcher._log.Error("Sending a response failed.", e);
            }
        }
    }
}
