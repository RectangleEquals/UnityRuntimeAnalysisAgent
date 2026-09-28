using System;
using System.Collections.Generic;
using System.Linq;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Envelopes;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Transport;

namespace UnityRuntimeAnalysisAgent.Core.Session;

/// <summary>A method implementation: params in, result out; throws <see cref="ProtocolException"/> for protocol errors.</summary>
public delegate ProtocolMessage MethodHandler(RequestContext context);

/// <summary>What a method implementation sees of its request.</summary>
public sealed class RequestContext
{
    internal RequestContext(Connection connection, RequestEnvelope request)
    {
        Connection = connection;
        Request = request;
    }

    /// <summary>The client connection.</summary>
    public Connection Connection { get; }

    /// <summary>The request envelope.</summary>
    public RequestEnvelope Request { get; }

    /// <summary>The params object (empty when absent).</summary>
    public JsonObject Params => Request.Params ?? new JsonObject();
}

/// <summary>
/// Per-request protocol handling: the handshake rules, the session methods (<c>hello</c>, <c>ping</c>, <c>agent.info</c>,
/// <c>agent.capabilities</c>, <c>cancel</c>, <c>events.subscribe</c> / <c>events.unsubscribe</c>) and a method table that
/// later components extend. Unknown or not-yet-implemented methods answer <c>METHOD_NOT_FOUND</c>.
/// </summary>
public sealed class SessionHandler
{
    private readonly SessionToken _token;
    private readonly Func<AgentInfo> _agentInfo;
    private readonly Func<AgentCapabilities, AgentCapabilities> _completeCapabilities;
    private readonly Dictionary<string, MethodHandler> _methods = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>Creates the handler.</summary>
    /// <param name="token">The session token clients must present.</param>
    /// <param name="agentInfo">Produces the current <c>agent.info</c> payload.</param>
    /// <param name="completeCapabilities">Fills the version fields of <c>agent.capabilities</c> (methods and event kinds are added here).</param>
    public SessionHandler(SessionToken token, Func<AgentInfo> agentInfo, Func<AgentCapabilities, AgentCapabilities> completeCapabilities)
    {
        _token = token;
        _agentInfo = agentInfo;
        _completeCapabilities = completeCapabilities;
        Register(UnityLudometry.Protocol.Methods.Ping, Ping);
        Register(UnityLudometry.Protocol.Methods.AgentInfo, _ => _agentInfo());
        Register(UnityLudometry.Protocol.Methods.AgentCapabilities, _ => Capabilities());
        Register(UnityLudometry.Protocol.Methods.Cancel, Cancel);
        Register(UnityLudometry.Protocol.Methods.EventsSubscribe, Subscribe);
        Register(UnityLudometry.Protocol.Methods.EventsUnsubscribe, Unsubscribe);
    }

    /// <summary>Kinds this build emits (reported in capabilities).</summary>
    public ISet<string> EmittedEventKinds { get; } = new SortedSet<string>(StringComparer.Ordinal);

    /// <summary>When the agent started (for uptime).</summary>
    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    /// <summary>Registers (or replaces) a method implementation. The name must be a protocol method.</summary>
    public void Register(string method, MethodHandler handler)
    {
        if (MethodRegistry.Find(method) is null)
        {
            throw new ArgumentException($"{method} is not a method of the protocol.", nameof(method));
        }

        lock (_gate)
        {
            _methods[method] = handler;
        }
    }

    /// <summary>Methods currently implemented (hello included).</summary>
    public IReadOnlyList<string> ImplementedMethods()
    {
        lock (_gate)
        {
            return _methods.Keys.Concat(new[] { UnityLudometry.Protocol.Methods.Hello }).OrderBy(m => m, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>Handles one request from a connection (called on the connection's read thread).</summary>
    public void Handle(Connection connection, RequestEnvelope request)
    {
        if (!connection.Authenticated)
        {
            Handshake(connection, request);
            return;
        }

        if (request.Method == UnityLudometry.Protocol.Methods.Hello)
        {
            connection.Send(ResponseEnvelope.Success(request.Id, _agentInfo().ToJson(), request.Context));
            return;
        }

        MethodHandler? handler;
        lock (_gate)
        {
            _methods.TryGetValue(request.Method, out handler);
        }

        if (handler is null)
        {
            var known = MethodRegistry.Find(request.Method) is not null;
            var error = new ProtocolError
            {
                Code = ErrorCodes.MethodNotFound,
                Message = known ? $"'{request.Method}' isn't implemented by this agent build." : $"Unknown method '{request.Method}'.",
                Data = Hint("Check agent.capabilities."),
            };
            connection.Send(ResponseEnvelope.Failure(request.Id, error, request.Context));
            return;
        }

        try
        {
            var result = handler(new RequestContext(connection, request));
            connection.Send(ResponseEnvelope.Success(request.Id, result.ToJson(), request.Context));
        }
        catch (ProtocolException e)
        {
            connection.Send(ResponseEnvelope.Failure(request.Id, e.ToError(), request.Context));
        }
    }

    private void Handshake(Connection connection, RequestEnvelope request)
    {
        if (request.Method != UnityLudometry.Protocol.Methods.Hello)
        {
            Refuse(connection, request, ErrorCodes.HandshakeRequired, "The first request on a connection must be hello.", null);
            return;
        }

        HelloParams hello;
        try
        {
            hello = HelloParams.Read(request.Params, "params");
        }
        catch (ProtocolException e)
        {
            // Without a valid hello, nothing else may happen on this connection.
            connection.SendAndClose(ResponseEnvelope.Failure(request.Id, e.ToError(), request.Context));
            return;
        }

        if (!_token.Matches(hello.Token))
        {
            Refuse(connection, request, ErrorCodes.BadToken, "The session token is not valid for this agent.", null);
            return;
        }

        if (!ProtocolVersion.IsCompatible((int)hello.Protocol.Major, (int)hello.Protocol.Minor))
        {
            Refuse(connection, request, ErrorCodes.ProtocolMismatch,
                $"Client protocol {hello.Protocol.Major}.{hello.Protocol.Minor} is not compatible with agent protocol {ProtocolVersion.Text}.",
                Hint(ProtocolVersion.Major == 0
                    ? "Before protocol 1.0, the agent and the orchestrator need the same protocol version. Install matching releases."
                    : "Install an agent and orchestrator with the same protocol major version."));
            return;
        }

        connection.Authenticated = true;
        connection.ClientName = $"{hello.Client.Name} {hello.Client.Version}";
        connection.Send(ResponseEnvelope.Success(request.Id, _agentInfo().ToJson(), request.Context));
    }

    private static void Refuse(Connection connection, RequestEnvelope request, string code, string message, JsonObject? data)
    {
        connection.SendAndClose(ResponseEnvelope.Failure(request.Id, new ProtocolError { Code = code, Message = message, Data = data }, request.Context));
    }

    private static JsonObject Hint(string text)
    {
        var data = new JsonObject();
        data.Add("hint", new JsonString(text));
        return data;
    }

    private ProtocolMessage Ping(RequestContext context)
    {
        var p = PingParams.Read(context.Request.Params, "params");
        return new PingResult { Echo = p.Echo, UptimeMs = (long)(DateTime.UtcNow - StartedUtc).TotalMilliseconds, Frame = null };
    }

    private static ProtocolMessage Cancel(RequestContext context)
    {
        CancelParams.Read(context.Request.Params, "params");
        // Requests are answered synchronously so far, so nothing is ever in flight to cancel (jobs use job.cancel).
        return new CancelResult { Cancelled = false };
    }

    private ProtocolMessage Subscribe(RequestContext context)
    {
        var p = EventsSubscribeParams.Read(context.Request.Params, "params");
        if (p.Kinds.Count == 0)
        {
            throw ProtocolException.InvalidParams("params.kinds", "kinds must contain at least one event kind.");
        }

        var known = p.Kinds.Where(k => EventRegistry.Find(k) is not null).Distinct(StringComparer.Ordinal).ToList();
        var unknown = p.Kinds.Where(k => EventRegistry.Find(k) is null).Distinct(StringComparer.Ordinal).ToList();
        context.Connection.Subscribe(known);
        return new EventsSubscribeResult { Subscribed = known, Unknown = unknown };
    }

    private static ProtocolMessage Unsubscribe(RequestContext context)
    {
        var p = EventsUnsubscribeParams.Read(context.Request.Params, "params");
        return new EventsUnsubscribeResult { Unsubscribed = context.Connection.Unsubscribe(p.Kinds) };
    }

    private AgentCapabilities Capabilities()
    {
        var methods = ImplementedMethods().Select(name =>
        {
            var d = MethodRegistry.Find(name)!;
            return new MethodCapability
            {
                Name = d.Name,
                Thread = d.Thread switch { MethodThread.Main => "main", MethodThread.Mixed => "mixed", _ => "any" },
                MinMode = d.MinMode,
                Mutating = d.Mutating,
                Job = d.Job,
                Requires = d.Requires.Count > 0 ? d.Requires.ToList() : null,
            };
        }).ToList();
        return _completeCapabilities(new AgentCapabilities { Methods = methods, EventKinds = EmittedEventKinds.ToList() });
    }
}
