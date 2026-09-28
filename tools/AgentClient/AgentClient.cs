using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Envelopes;
using UnityLudometry.Protocol.Framing;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;

namespace UnityRuntimeAnalysisAgent.Client;

/// <summary>An error response from the agent, a failed handshake, or a lost connection (<see cref="ConnectionClosed"/>).</summary>
public sealed class AgentClientException(string code, string message, JsonObject? data = null, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>The client-side code for a request that failed because the connection is gone.</summary>
    public const string ConnectionClosed = "CONNECTION_CLOSED";

    /// <summary>The protocol error code.</summary>
    public string Code { get; } = code;

    /// <summary>The error's data.</summary>
    public JsonObject? ErrorData { get; } = data;
}

/// <summary>A minimal agent client: connect (pipe, TCP or discovery file), authenticate, send requests, receive events.</summary>
public sealed class AgentClient : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly FrameWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ResponseEnvelope>> _pending = new();
    private readonly Channel<EventEnvelope> _events = Channel.CreateUnbounded<EventEnvelope>();
    private readonly CancellationTokenSource _closing = new();
    private readonly Task _readLoop;
    private int _nextId;
    private volatile AgentClientException? _closedBy;
    private int _disposed;

    private AgentClient(Stream stream)
    {
        _stream = stream;
        _writer = new FrameWriter(stream);
        _readLoop = Task.Run(ReadLoopAsync);
    }

    /// <summary>The agent's info from the handshake.</summary>
    public AgentInfo Info { get; private set; } = new();

    /// <summary>Events as they arrive.</summary>
    public ChannelReader<EventEnvelope> Events => _events.Reader;

    /// <summary>Raised for every event (on the read loop).</summary>
    public event Action<EventEnvelope>? EventReceived;

    /// <summary>Connects through a discovery file written by the agent.</summary>
    public static async Task<AgentClient> ConnectDiscoveryAsync(string discoveryFile, string clientName = "AgentConsole", CancellationToken cancellationToken = default)
    {
        var info = DiscoveryFile.Read(JsonValue.Parse(await File.ReadAllBytesAsync(discoveryFile, cancellationToken)), "discovery");
        return info.Transport == "tcp" && info.Port is { } port
            ? await ConnectTcpAsync((int)port, info.Token, clientName, cancellationToken)
            : await ConnectPipeAsync(info.Pipe ?? throw new AgentClientException("INVALID_PARAMS", "The discovery file names no pipe."), info.Token, clientName, cancellationToken);
    }

    /// <summary>Connects to a named pipe and authenticates.</summary>
    public static async Task<AgentClient> ConnectPipeAsync(string pipeName, string token, string clientName = "AgentConsole", CancellationToken cancellationToken = default)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000, cancellationToken);
        return await HandshakeAsync(new AgentClient(pipe), token, clientName, cancellationToken);
    }

    /// <summary>Connects to the TCP fallback on loopback and authenticates.</summary>
    public static async Task<AgentClient> ConnectTcpAsync(int port, string token, string clientName = "AgentConsole", CancellationToken cancellationToken = default)
    {
        var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
        return await HandshakeAsync(new AgentClient(tcp.GetStream()), token, clientName, cancellationToken);
    }

    private static async Task<AgentClient> HandshakeAsync(AgentClient client, string token, string clientName, CancellationToken cancellationToken)
    {
        var hello = new HelloParams { Token = token, Client = new ClientInfo { Name = clientName, Version = "0.1" }, Protocol = ProtocolVersionInfo.Current };
        try
        {
            client.Info = AgentInfo.Read(await client.CallAsync(Methods.Hello, hello.ToJson(), cancellationToken), "result");
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    /// <summary>Sends a request and returns the raw response envelope (errors included).</summary>
    public async Task<ResponseEnvelope> SendAsync(string method, JsonObject? parameters = null, CancellationToken cancellationToken = default)
    {
        var id = "c-" + Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<ResponseEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            // The read loop fails every pending request when it ends; this covers requests registered after that.
            if (_closedBy is { } closedBy)
            {
                throw new AgentClientException(closedBy.Code, closedBy.Message, null, closedBy.InnerException);
            }

            var request = new RequestEnvelope { Id = id, Method = method, Params = parameters };
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await _writer.WriteFrameAsync(request.ToUtf8Bytes(), cancellationToken);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                throw new AgentClientException(AgentClientException.ConnectionClosed, "The connection to the agent is closed.", null, e);
            }
            finally
            {
                _writeLock.Release();
            }

            using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return await completion.Task;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Sends a request and returns its result; an error response throws <see cref="AgentClientException"/>.</summary>
    public async Task<JsonValue> CallAsync(string method, JsonObject? parameters = null, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(method, parameters, cancellationToken);
        if (response.Error is { } error)
        {
            throw new AgentClientException(error.Code, error.Message, error.Data);
        }

        return response.Result ?? JsonNull.Instance;
    }

    private async Task ReadLoopAsync()
    {
        var reader = new FrameReader(_stream);
        Exception? failure = null;
        try
        {
            while (true)
            {
                var frame = await reader.ReadFrameAsync(_closing.Token);
                if (frame is null)
                {
                    break;
                }

                switch (Envelope.Parse(frame))
                {
                    case ResponseEnvelope response when _pending.TryGetValue(response.Id, out var completion):
                        completion.TrySetResult(response);
                        break;
                    case EventEnvelope ev:
                        _events.Writer.TryWrite(ev);
                        EventReceived?.Invoke(ev);
                        break;
                }
            }
        }
        catch (Exception e) when (e is IOException || e is ObjectDisposedException || e is OperationCanceledException || e is ProtocolException)
        {
            failure = e;
        }

        var closed = new AgentClientException(AgentClientException.ConnectionClosed, "The agent closed the connection.", null, failure);
        _closedBy = closed;
        _events.Writer.TryComplete();
        foreach (var completion in _pending.Values)
        {
            completion.TrySetException(closed);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _closing.Cancel();
        await _stream.DisposeAsync();
        try
        {
            await _readLoop;
        }
        catch (Exception)
        {
            // Closed.
        }

        _closing.Dispose();
        _writeLock.Dispose();
    }
}
