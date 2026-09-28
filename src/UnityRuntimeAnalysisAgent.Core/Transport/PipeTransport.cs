using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using UnityRuntimeAnalysisAgent.Core.Hosting;

namespace UnityRuntimeAnalysisAgent.Core.Transport;

/// <summary>
/// The primary transport: a named pipe <c>\\.\pipe\ulm-agent-&lt;pid&gt;</c>, byte mode, up to 4 concurrent clients.
/// Access is restricted to the current user where the runtime supports pipe ACLs; otherwise the default pipe security
/// applies and the session token is the gate (<see cref="AclApplied"/> reports which).
/// </summary>
public sealed class PipeTransport : ITransport
{
    /// <summary>Maximum concurrent clients.</summary>
    public const int MaxInstances = 4;

    private readonly IAgentLogger _log;
    private readonly Semaphore _slots = new(MaxInstances, MaxInstances);
    private readonly ManualResetEvent _stop = new(false);
    private Thread? _acceptThread;
    private volatile bool _stopping;

    /// <summary>Creates the transport for a pipe name such as <c>ulm-agent-1234</c>.</summary>
    public PipeTransport(string pipeName, IAgentLogger log)
    {
        PipeName = pipeName;
        _log = log;
    }

    /// <inheritdoc />
    public string Kind => "pipe";

    /// <inheritdoc />
    public string? PipeName { get; }

    /// <inheritdoc />
    public int? Port => null;

    /// <summary>Whether the current-user-only ACL is applied (null until the first pipe instance exists).</summary>
    public bool? AclApplied { get; private set; }

    /// <summary>Why the ACL couldn't be applied, if it wasn't.</summary>
    public string? AclFailure { get; private set; }

    /// <inheritdoc />
    public void Start(Action<Stream, Action> onAccepted)
    {
        // Create the first instance synchronously, so a failure (e.g. pipes unsupported) surfaces here for the fallback.
        var first = CreateInstance();
        _acceptThread = new Thread(() => AcceptLoop(first, onAccepted)) { IsBackground = true, Name = "URAA pipe accept" };
        _acceptThread.Start();
    }

    private NamedPipeServerStream CreateInstance()
    {
        if (PipeSecurityBinder.TryCreate(PipeName!, MaxInstances, out var secured, out var failure))
        {
            AclApplied ??= true;
            return secured!;
        }

        if (AclApplied is null)
        {
            AclApplied = false;
            AclFailure = failure;
            _log.Warning($"The pipe can't be restricted to the current user ({failure}); the session token is the only gate.");
        }

        return new NamedPipeServerStream(PipeName!, PipeDirection.InOut, MaxInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    private void AcceptLoop(NamedPipeServerStream? next, Action<Stream, Action> onAccepted)
    {
        while (!_stopping)
        {
            // One slot per client; wait for a free slot (or stop) before offering another instance.
            if (WaitHandle.WaitAny(new WaitHandle[] { _stop, _slots }) == 0)
            {
                next?.Dispose();
                return;
            }

            NamedPipeServerStream server;
            try
            {
                server = next ?? CreateInstance();
                next = null;
                server.WaitForConnection();
            }
            catch (Exception e) when (e is IOException || e is ObjectDisposedException || e is InvalidOperationException || e is UnauthorizedAccessException)
            {
                _slots.Release();
                if (_stopping)
                {
                    return;
                }

                _log.Warning("Accepting a pipe client failed; retrying.", e);
                Thread.Sleep(100);
                continue;
            }

            if (_stopping)
            {
                server.Dispose();
                return;
            }

            var released = 0;
            onAccepted(server, () =>
            {
                if (Interlocked.Exchange(ref released, 1) == 0)
                {
                    server.Dispose();
                    try
                    {
                        _slots.Release();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
            });
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_stopping)
        {
            return;
        }

        _stopping = true;
        _stop.Set();
        // Unblock a pending WaitForConnection by connecting to it once.
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName!, PipeDirection.InOut);
            client.Connect(200);
        }
        catch (Exception)
        {
            // Nothing was waiting.
        }

        _acceptThread?.Join(2000);
    }
}
