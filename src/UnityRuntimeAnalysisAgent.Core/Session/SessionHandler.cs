using System;
using System.Collections.Concurrent;
using System.Threading;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Envelopes;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Dispatch;
using UnityRuntimeAnalysisAgent.Core.Transport;

namespace UnityRuntimeAnalysisAgent.Core.Session;

/// <summary>
/// The connection-facing side of request handling: the handshake rules, then every request goes to the
/// <see cref="Dispatcher"/> and its outcome back to the connection, with the request's <c>context</c> echoed. Requests in
/// flight are tracked per connection, so <c>cancel {id}</c> can stop them and a closing connection cancels its own.
/// </summary>
public sealed class SessionHandler
{
    private readonly SessionToken _token;
    private readonly Dispatcher _dispatcher;
    private readonly Func<AgentInfo> _agentInfo;
    private readonly ConcurrentDictionary<(int Connection, string Id), CancellationTokenSource> _inFlight = new();

    /// <summary>Creates the handler.</summary>
    public SessionHandler(SessionToken token, Dispatcher dispatcher, Func<AgentInfo> agentInfo)
    {
        _token = token;
        _dispatcher = dispatcher;
        _agentInfo = agentInfo;
    }

    /// <summary>When the agent started (for uptime).</summary>
    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    /// <summary>Handles one request from a connection (called on the connection's read thread; never blocks on the work).</summary>
    public void Handle(Connection connection, RequestEnvelope request)
    {
        if (!connection.Authenticated)
        {
            Handshake(connection, request);
            return;
        }

        if (request.Method == Methods.Hello)
        {
            connection.Send(ResponseEnvelope.Success(request.Id, _agentInfo().ToJson(), request.Context));
            return;
        }

        var cancellation = new CancellationTokenSource();
        var key = (connection.Id, request.Id);
        if (!_inFlight.TryAdd(key, cancellation))
        {
            connection.Send(ResponseEnvelope.Failure(request.Id,
                new ProtocolError { Code = ErrorCodes.InvalidParams, Message = $"A request with id '{request.Id}' is already in flight on this connection." },
                request.Context));
            return;
        }

        var context = new RequestContext(request.Id, request.Method, request.Params, request.Context, "client", connection.ClientName, connection, cancellation.Token)
        {
            TimeoutMs = request.TimeoutMs,
        };
        _dispatcher.Dispatch(context, outcome =>
        {
            _inFlight.TryRemove(key, out _);
            cancellation.Dispose();
            connection.Send(outcome.Error is { } error
                ? ResponseEnvelope.Failure(request.Id, error, request.Context)
                : ResponseEnvelope.Success(request.Id, outcome.Result!, request.Context));
        });
    }

    /// <summary>Cancels a request in flight on a connection. Returns whether one was found.</summary>
    public bool Cancel(Connection connection, string requestId)
    {
        if (!_inFlight.TryGetValue((connection.Id, requestId), out var cancellation))
        {
            return false;
        }

        try
        {
            cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false; // finished meanwhile
        }
    }

    /// <summary>Cancels every request in flight on a connection (when it closes).</summary>
    public void CancelAll(Connection connection)
    {
        foreach (var pair in _inFlight)
        {
            if (pair.Key.Connection == connection.Id)
            {
                Cancel(connection, pair.Key.Id);
            }
        }
    }

    private void Handshake(Connection connection, RequestEnvelope request)
    {
        if (request.Method != Methods.Hello)
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
                AgentErrors.Data(("hint", new JsonString(ProtocolVersion.Major == 0
                    ? "Before protocol 1.0, the agent and the orchestrator need the same protocol version. Install matching releases."
                    : "Install an agent and orchestrator with the same protocol major version."))));
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
}
