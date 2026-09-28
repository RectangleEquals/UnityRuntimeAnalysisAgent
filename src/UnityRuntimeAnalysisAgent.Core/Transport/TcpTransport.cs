using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityRuntimeAnalysisAgent.Core.Hosting;

namespace UnityRuntimeAnalysisAgent.Core.Transport;

/// <summary>The TCP fallback: a listener on the loopback address only, on an ephemeral port.</summary>
public sealed class TcpTransport : ITransport
{
    private readonly IAgentLogger _log;
    private TcpListener? _listener;
    private Thread? _acceptThread;
    private volatile bool _stopping;

    /// <summary>Creates the transport.</summary>
    public TcpTransport(IAgentLogger log) => _log = log;

    /// <inheritdoc />
    public string Kind => "tcp";

    /// <inheritdoc />
    public string? PipeName => null;

    /// <inheritdoc />
    public int? Port { get; private set; }

    /// <inheritdoc />
    public void Start(Action<Stream, Action> onAccepted)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptThread = new Thread(() => AcceptLoop(onAccepted)) { IsBackground = true, Name = "URAA tcp accept" };
        _acceptThread.Start();
    }

    private void AcceptLoop(Action<Stream, Action> onAccepted)
    {
        while (!_stopping)
        {
            TcpClient client;
            try
            {
                client = _listener!.AcceptTcpClient();
            }
            catch (Exception e) when (e is SocketException || e is ObjectDisposedException || e is InvalidOperationException)
            {
                if (!_stopping)
                {
                    _log.Warning("The TCP listener stopped unexpectedly.", e);
                }

                return;
            }

            client.NoDelay = true;
            onAccepted(client.GetStream(), () => client.Close());
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stopping = true;
        try
        {
            _listener?.Stop();
        }
        catch (SocketException)
        {
        }
    }
}
