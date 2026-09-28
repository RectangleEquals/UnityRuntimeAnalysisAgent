using System;
using System.IO;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Hosting;

namespace UnityRuntimeAnalysisAgent.Core.Discovery;

/// <summary>
/// Writes the discovery file <c>&lt;ProvidersDir&gt;/agent-&lt;pid&gt;.json</c> atomically once the agent listens, and deletes it on
/// shutdown. Without a providers directory nothing is published (the agent still listens, but no client can find it).
/// </summary>
public sealed class DiscoveryPublisher : IDisposable
{
    private readonly IAgentLogger _log;
    private string? _path;

    /// <summary>Creates the publisher.</summary>
    public DiscoveryPublisher(IAgentLogger log) => _log = log;

    /// <summary>The published file, if any.</summary>
    public string? PublishedPath => _path;

    /// <summary>Publishes (or re-publishes) the file. Returns its path, or null when <paramref name="providersDir"/> is unset.</summary>
    public string? Publish(string? providersDir, DiscoveryFile info)
    {
        if (string.IsNullOrWhiteSpace(providersDir))
        {
            _log.Warning("Discovery.ProvidersDir is not set: the agent is listening but publishes no discovery file, so no client can connect.");
            return null;
        }

        Directory.CreateDirectory(providersDir);
        var path = Path.Combine(providersDir, $"agent-{info.Pid}.json");
        var temp = path + ".tmp";
        using (var writer = new JsonWriter())
        {
            info.WriteJson(writer);
            File.WriteAllBytes(temp, writer.ToArray());
        }

        if (File.Exists(path))
        {
            File.Replace(temp, path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(temp, path);
        }

        _path = path;
        _log.Info($"Published the discovery file {path}.");
        return path;
    }

    /// <summary>Deletes the published file (idempotent).</summary>
    public void Dispose()
    {
        var path = _path;
        _path = null;
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception e)
        {
            _log.Warning($"Couldn't delete the discovery file {path}.", e);
        }
    }
}
