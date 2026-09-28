using System;
using System.IO;

namespace UnityRuntimeAnalysisAgent.Core.Transport;

/// <summary>A listener that accepts client connections and hands each accepted stream to a callback.</summary>
public interface ITransport : IDisposable
{
    /// <summary><c>pipe</c> or <c>tcp</c>.</summary>
    string Kind { get; }

    /// <summary>The pipe name (pipe transport), else null.</summary>
    string? PipeName { get; }

    /// <summary>The loopback port (TCP transport), else null.</summary>
    int? Port { get; }

    /// <summary>Starts listening. Throws if the listener can't be created. Each accepted stream goes to <paramref name="onAccepted"/>
    /// together with a callback to call once that connection has closed (it frees the slot).</summary>
    void Start(Action<Stream, Action> onAccepted);
}
