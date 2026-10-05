using System;
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
    private readonly LinkedList<Outgoing> _outbound = new();
    private readonly Dictionary<string, long> _dropped = new(StringComparer.Ordinal);
    private long _queuedEventBytes;
    private bool _outboundCompleted;
    private readonly HashSet<string> _subscriptions = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private long _messagesIn;
    private long _messagesOut;
    private long _bytes;
    private long _droppedTotal;
    private long _lastActivityTicks = DateTime.UtcNow.Ticks;
    private Thread? _reader;
    private Thread? _writer;
    private long _eventSeq;
    private int _closed;
    private volatile string? _closeReason;

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

    /// <summary>The next event sequence number of this connection. Numbers are taken when an event is queued, so an
    /// event dropped under back-pressure leaves a gap the client can see (batched kinds also count them in <c>dropped</c>).</summary>
    public long NextEventSeq() => Interlocked.Increment(ref _eventSeq);

    /// <summary>The cap on queued, unsent event bytes (<c>Events.MaxQueueBytes</c>). Over it, the oldest queued events are
    /// dropped and counted. Responses are never dropped.</summary>
    public long MaxEventQueueBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>Queued, unsent event bytes.</summary>
    public long QueuedEventBytes
    {
        get
        {
            lock (_outbound)
            {
                return _queuedEventBytes;
            }
        }
    }

    /// <summary>Queues an event. <paramref name="items"/> is how many items it carries (for the dropped count).</summary>
    public void SendEvent(string kind, JsonObject parameters, JsonObject? context = null, int items = 1)
    {
        lock (_outbound)
        {
            if (_outboundCompleted)
            {
                return;
            }

            var frame = new EventEnvelope { Method = kind, Seq = NextEventSeq(), Params = parameters, Context = context }.ToUtf8Bytes();
            var estimate = frame.Length;
            if (estimate > _maxFrameBytes)
            {
                _log.Warning($"Connection {Id}: dropped a {kind} event of {estimate} bytes (over the frame limit).");
                _dropped[kind] = (_dropped.TryGetValue(kind, out var tooBig) ? tooBig : 0) + items;
                Interlocked.Add(ref _droppedTotal, items);
                return;
            }

            // Back-pressure: drop the oldest queued events (never responses) until the new one fits.
            var node = _outbound.First;
            while (_queuedEventBytes + estimate > MaxEventQueueBytes && node is not null)
            {
                var next = node.Next;
                if (node.Value.EventKind is { } droppedKind)
                {
                    _queuedEventBytes -= node.Value.Bytes;
                    _dropped[droppedKind] = (_dropped.TryGetValue(droppedKind, out var n) ? n : 0) + node.Value.Items;
                    Interlocked.Add(ref _droppedTotal, node.Value.Items);
                    _outbound.Remove(node);
                }

                node = next;
            }

            _outbound.AddLast(new Outgoing(frame, kind, estimate, items));
            _queuedEventBytes += estimate;
            Monitor.Pulse(_outbound);
        }
    }

    /// <summary>How many items of a kind were dropped under back-pressure since the last call (then resets).</summary>
    public long TakeDropped(string kind)
    {
        lock (_outbound)
        {
            if (!_dropped.TryGetValue(kind, out var n))
            {
                return 0;
            }

            _dropped.Remove(kind);
            return n;
        }
    }



    /// <summary>When the connection was accepted.</summary>
    public DateTime ConnectedUtc { get; } = DateTime.UtcNow;

    /// <summary>Messages received from the client.</summary>
    public long MessagesIn => Interlocked.Read(ref _messagesIn);

    /// <summary>Messages sent to the client (responses and events).</summary>
    public long MessagesOut => Interlocked.Read(ref _messagesOut);

    /// <summary>Bytes received and sent (frames).</summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>Events dropped under back-pressure, over the whole connection.</summary>
    public long DroppedTotal => Interlocked.Read(ref _droppedTotal);

    /// <summary>When the last message came or went (UTC), for "busy" indicators.</summary>
    public DateTime LastActivityUtc => new(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);

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

    /// <summary>Queues an envelope for sending. Responses above the frame limit are replaced by an error. Events go
    /// through <see cref="SendEvent"/> (numbered when written).</summary>
    public void Send(Envelope envelope)
    {
        if (envelope is EventEnvelope ev)
        {
            SendEvent(ev.Method, ev.Params, ev.Context);
            return;
        }

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
        Enqueue(bytes);
    }

    /// <summary>Sends an envelope, then closes the connection once it has been written.</summary>
    public void SendAndClose(Envelope envelope)
    {
        Send(envelope);
        Enqueue(null);
    }

    /// <summary>Closes the connection once everything already queued (responses, events) has been written.</summary>
    public void CloseWhenSent(string reason)
    {
        _closeReason = reason;
        Enqueue(null);
    }

    /// <summary>Closes the connection (idempotent).</summary>
    public void Close(string reason, Exception? error = null)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _log.Info($"Connection {Id} closed: {reason}{(error is null ? string.Empty : $" ({error.GetType().Name}: {error.Message})")}.");
        lock (_outbound)
        {
            _outboundCompleted = true;
            _outbound.Clear();
            _queuedEventBytes = 0;
            Monitor.PulseAll(_outbound);
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

    private void Enqueue(byte[]? frame)
    {
        lock (_outbound)
        {
            if (_outboundCompleted)
            {
                return; // closed: nothing more is sent
            }

            _outbound.AddLast(frame is null ? Outgoing.CloseMarker : new Outgoing(frame, null, frame.Length, 0));
            if (frame is null)
            {
                _outboundCompleted = true;
            }

            Monitor.Pulse(_outbound);
        }
    }

    private Outgoing? Dequeue()
    {
        lock (_outbound)
        {
            while (_outbound.Count == 0)
            {
                if (_outboundCompleted)
                {
                    return null;
                }

                Monitor.Wait(_outbound);
            }

            var item = _outbound.First!.Value;
            _outbound.RemoveFirst();
            if (item.EventKind is not null)
            {
                _queuedEventBytes -= item.Bytes;
            }

            return item;
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
        Interlocked.Increment(ref _messagesIn);
        Interlocked.Add(ref _bytes, frame.Length);
        Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
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
            while (Dequeue() is { } item)
            {
                if (item.IsCloseMarker)
                {
                    Close(_closeReason ?? "closed after the final response");
                    return;
                }

                writer.WriteFrame(item.Frame!);
                Interlocked.Increment(ref _messagesOut);
                Interlocked.Add(ref _bytes, item.Frame!.Length);
                Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
            }
        }
        catch (Exception e) when (e is IOException || e is ObjectDisposedException || e is ProtocolException || e is InvalidOperationException)
        {
            Close("writing failed", e);
        }
    }

    private sealed class Outgoing
    {
        public static readonly Outgoing CloseMarker = new(null, null, 0, 0, isCloseMarker: true);

        public Outgoing(byte[]? frame, string? eventKind, long bytes, int items, bool isCloseMarker = false)
        {
            Frame = frame;
            EventKind = eventKind;
            Bytes = bytes;
            Items = items;
            IsCloseMarker = isCloseMarker;
        }

        public byte[]? Frame { get; }

        public string? EventKind { get; }

        public long Bytes { get; }

        public int Items { get; }

        public bool IsCloseMarker { get; }
    }
}
