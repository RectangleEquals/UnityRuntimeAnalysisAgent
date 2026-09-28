using System.Collections.Concurrent;
using UnityRuntimeAnalysisAgent.Core.Hosting;

namespace UnityRuntimeAnalysisAgent.Core.Tests.Support;

/// <summary>Two connected in-memory streams: bytes written to one are read from the other. Blocking reads, like a pipe.</summary>
public static class MemoryDuplex
{
    public static (Stream A, Stream B) CreatePair()
    {
        var aToB = new BlockingCollection<byte[]>();
        var bToA = new BlockingCollection<byte[]>();
        return (new End(bToA, aToB), new End(aToB, bToA));
    }

    private sealed class End(BlockingCollection<byte[]> inbound, BlockingCollection<byte[]> outbound) : Stream
    {
        private byte[] _current = [];
        private int _offset;
        private bool _disposed;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_offset >= _current.Length)
            {
                if (!inbound.TryTake(out var next, Timeout.Infinite))
                {
                    return 0; // the peer closed, or this end was disposed
                }

                _current = next;
                _offset = 0;
            }

            var n = Math.Min(count, _current.Length - _offset);
            Buffer.BlockCopy(_current, _offset, buffer, offset, n);
            _offset += n;
            return n;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                outbound.Add(buffer.AsSpan(offset, count).ToArray());
            }
            catch (InvalidOperationException e)
            {
                throw new IOException("The peer closed the stream.", e);
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                outbound.CompleteAdding();
                inbound.CompleteAdding();
            }

            base.Dispose(disposing);
        }
    }
}

/// <summary>A logger that records entries (thread-safe).</summary>
public sealed class TestLogger : IAgentLogger
{
    private readonly ConcurrentQueue<(AgentLogLevel Level, string Message)> _entries = new();

    public IReadOnlyList<(AgentLogLevel Level, string Message)> Entries => _entries.ToArray();

    public void Log(AgentLogLevel level, string message, Exception? exception = null) => _entries.Enqueue((level, message));

    public bool Contains(AgentLogLevel level, string fragment) => _entries.Any(e => e.Level == level && e.Message.Contains(fragment, StringComparison.Ordinal));
}
