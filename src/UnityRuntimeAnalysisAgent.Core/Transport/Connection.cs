using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Envelopes;
using UnityLudometry.Protocol.Framing;
using UnityLudometry.Protocol.Json;
using UnityRuntimeAnalysisAgent.Core.Hosting;

namespace UnityRuntimeAnalysisAgent.Core.Transport;

/// <summary>Why a connection closed.</summary>
public sealed class ConnectionClosedEventArgs : EventArgs
{
    /// <summary>Creates the event data.</summary>
    public ConnectionClosedEventArgs(string reason, Exception? error)
    {
        Reason = reason;
        Error = error;
    }

    /// <summary>A short description.</summary>
    public string Reason { get; }

    /// <summary>The failure, if the close wasn't graceful.</summary>
    public Exception? Error { get; }
}

/// <summary>
/// One client connection: a dedicated read loop (framing, envelope parsing) and a single-writer outbound queue, so
/// responses and events never interleave within a frame. Threads, not async I/O, keep it portable to Unity's Mono.
/// </summary>
/// <remarks>
/// Frame-level errors (oversized, empty or truncated frames) leave the stream out of sync, so the connection closes.
/// A frame with malformed JSON is dropped (the stream stays in sync); an invalid envelope that carries an id is
/// answered with <c>INVALID_FRAME</c>.
/// </remarks>
public sealed class Connection : IDisposable
{
    private static int s_nextId;

    private readonly Stream _stream;
    private readonly int _maxFrameBytes;
    private readonly IAgentLogger _log;
    private readonly Action<Connection, RequestEnvelope> _onRequest;
    private readonly BlockingCollection<byte[]?> _outbound = new();
    private readonly HashSet<string> _subscriptions = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private Thread? _reader;
    private Thread? _writer;
    private long _eventSeq;
    private int _closed;

    /// <summary>Creates a connection over an accepted stream. Call <see cref="Start"/> to begin.</summary>
    public Connection(Stream stream, string transport, int maxFrameBytes, IAgentLogger log, Action<Connection, RequestEnvelope> onRequest)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        Transport = transport;
        _maxFrameBytes = maxFrameBytes;
        _log = log;
        _onRequest = onRequest;
        Id = Interlocked.Increment(ref s_nextId);
    }

    /// <summary>A process-unique id (for logs and ownership).</summary>
    public int Id { get; }

    /// <summary><c>pipe</c> or <c>tcp</c>.</summary>
    public string Transport { get; }

    /// <summary>Whether the client passed the handshake.</summary>
    public bool Authenticated { get; internal set; }

    /// <summary>The connecting client's name and version (from <c>hello</c>).</summary>
    public string? ClientName { get; internal set; }

    /// <summary>Whether the connection is closed.</summary>
    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>Raised once when the connection closes (owners clean up per-connection resources).</summary>
    public event EventHandler<ConnectionClosedEventArgs>? Closed;

    /// <summary>Starts the read and write threads.</summary>
    public void Start()
    {
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = $"URAA connection {Id} writer" };
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = $"URAA connection {Id} reader" };
        _writer.Start();
        _reader.Start();
    }

    /// <summary>The next event sequence number of this connection.</summary>
    public long NextEventSeq() => Interlocked.Increment(ref _eventSeq);

    /// <summary>Whether the connection is subscribed to an event kind.</summary>
    public bool IsSubscribed(string kind)
    {
        lock (_gate)
        {
            return _subscriptions.Contains(kind);
        }
    }

    /// <summary>Adds event subscriptions.</summary>
    public void Subscribe(IEnumerable<string> kinds)
    {
        lock (_gate)
        {
            _subscriptions.UnionWith(kinds);
        }
    }

    /// <summary>Removes subscriptions (all when <paramref name="kinds"/> is null). Returns the kinds removed.</summary>
    public List<string> Unsubscribe(IEnumerable<string>? kinds)
    {
        lock (_gate)
        {
            var removed = new List<string>();
            foreach (var kind in kinds ?? new List<string>(_subscriptions))
            {
                if (_subscriptions.Remove(kind))
                {
                    removed.Add(kind);
                }
            }

            removed.Sort(StringComparer.Ordinal);
            return removed;
        }
    }

    /// <summary>Queues an envelope for sending. Responses above the frame limit are replaced by an error.</summary>
    public void Send(Envelope envelope)
    {
        var bytes = envelope.ToUtf8Bytes();
        if (bytes.Length > _maxFrameBytes && envelope is ResponseEnvelope response)
        {
            var error = new ProtocolError
            {
                Code = ErrorCodes.Internal,
                Message = $"The response of {bytes.Length} bytes exceeds the frame limit of {_maxFrameBytes} bytes.",
            };
            bytes = ResponseEnvelope.Failure(response.Id, error, response.Context).ToUtf8Bytes();
        }
        else if (bytes.Length > _maxFrameBytes)
        {
            _log.Warning($"Connection {Id}: dropped an event of {bytes.Length} bytes (over the frame limit).");
            return;
        }

        Enqueue(bytes);
    }

    /// <summary>Sends an envelope, then closes the connection once it has been written.</summary>
    public void SendAndClose(Envelope envelope)
    {
        Send(envelope);
        Enqueue(null);
    }

    /// <summary>Closes the connection (idempotent).</summary>
    public void Close(string reason, Exception? error = null)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        try
        {
            _outbound.CompleteAdding();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _stream.Dispose();
        }
        catch (Exception e)
        {
            _log.Debug($"Connection {Id}: error while closing the stream: {e.Message}");
        }

        _log.Debug($"Connection {Id} closed: {reason}");
        try
        {
            Closed?.Invoke(this, new ConnectionClosedEventArgs(reason, error));
        }
        catch (Exception e)
        {
            _log.Error($"Connection {Id}: a close handler failed.", e);
        }
    }

    /// <inheritdoc />
    public void Dispose() => Close("disposed");

    private void Enqueue(byte[]? item)
    {
        try
        {
            _outbound.Add(item);
        }
        catch (InvalidOperationException)
        {
            // Closed: nothing more is sent.
        }
    }

    private void ReadLoop()
    {
        var reader = new FrameReader(_stream, _maxFrameBytes);
        try
        {
            while (!IsClosed)
            {
                var frame = reader.ReadFrame();
                if (frame is null)
                {
                    Close("closed by the client");
                    return;
                }

                HandleFrame(frame);
            }
        }
        catch (ProtocolException e)
        {
            _log.Warning($"Connection {Id}: {e.Code}: {e.Message} Closing.");
            Close(e.Code, e);
        }
        catch (Exception e) when (e is IOException || e is ObjectDisposedException || e is InvalidOperationException)
        {
            Close("the stream failed", e);
        }
    }

    private void HandleFrame(byte[] frame)
    {
        JsonValue json;
        try
        {
            json = JsonValue.Parse(frame);
        }
        catch (ProtocolException e)
        {
            _log.Warning($"Connection {Id}: dropped a frame with malformed JSON ({e.Message}).");
            return;
        }

        Envelope envelope;
        try
        {
            envelope = Envelope.Parse(json);
        }
        catch (ProtocolException e)
        {
            if (json is JsonObject o && o["id"] is JsonString id && id.Value.Length > 0)
            {
                Send(ResponseEnvelope.Failure(id.Value, e.ToError()));
            }
            else
            {
                _log.Warning($"Connection {Id}: dropped an invalid envelope without an id ({e.Message}).");
            }

            return;
        }

        if (envelope is RequestEnvelope request)
        {
            try
            {
                _onRequest(this, request);
            }
            catch (Exception e)
            {
                _log.Error($"Connection {Id}: handling {request.Method} failed.", e);
                Send(ResponseEnvelope.Failure(request.Id, new ProtocolError { Code = ErrorCodes.Internal, Message = "Internal agent error." }, request.Context));
            }
        }
        else
        {
            _log.Debug($"Connection {Id}: ignored a client {envelope.Kind} envelope.");
        }
    }

    private void WriteLoop()
    {
        var writer = new FrameWriter(_stream, _maxFrameBytes);
        try
        {
            foreach (var item in _outbound.GetConsumingEnumerable())
            {
                if (item is null)
                {
                    Close("closed after the final response");
                    return;
                }

                writer.WriteFrame(item);
            }
        }
        catch (Exception e) when (e is IOException || e is ObjectDisposedException || e is ProtocolException || e is InvalidOperationException)
        {
            Close("writing failed", e);
        }
    }
}
