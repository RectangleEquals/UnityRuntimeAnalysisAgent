using System;
using System.IO;
using UnityRuntimeAnalysisAgent.Core.Hosting;

namespace UnityRuntimeAnalysisAgent.Core.Transport;

/// <summary>Starts the configured transport: <c>auto</c> tries the named pipe and falls back to TCP if it can't be created.</summary>
public static class TransportFactory
{
    /// <summary>Creates and starts a transport. Throws if the requested (or every) transport fails.</summary>
    /// <param name="createPipe">Pipe factory (tests inject failures); defaults to <see cref="PipeTransport"/>.</param>
    public static ITransport Start(TransportMode mode, string pipeName, IAgentLogger log, Action<Stream, Action> onAccepted,
        Func<string, IAgentLogger, ITransport>? createPipe = null)
    {
        createPipe ??= (name, logger) => new PipeTransport(name, logger);
        if (mode is TransportMode.Pipe or TransportMode.Auto)
        {
            ITransport? pipe = null;
            try
            {
                pipe = createPipe(pipeName, log);
                pipe.Start(onAccepted);
                return pipe;
            }
            catch (Exception e) when (mode == TransportMode.Auto)
            {
                pipe?.Dispose();
                log.Warning($"The named pipe couldn't be created ({e.Message}); falling back to TCP on loopback.", e);
            }
        }

        var tcp = new TcpTransport(log);
        tcp.Start(onAccepted);
        return tcp;
    }
}
