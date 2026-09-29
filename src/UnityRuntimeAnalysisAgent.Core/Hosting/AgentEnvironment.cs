using System;
using System.Diagnostics;
using System.Reflection;

namespace UnityRuntimeAnalysisAgent.Core.Hosting;

/// <summary>Facts about the process the agent runs in, supplied by the loader shim (or a tool hosting the agent).</summary>
public sealed class AgentEnvironment
{
    /// <summary>Agent version (SemVer).</summary>
    public string AgentVersion { get; set; } = BuildVersion().Version;

    /// <summary>Source commit of the agent build, if the build recorded it.</summary>
    public string? GitCommit { get; set; } = BuildVersion().Commit;

    /// <summary>Version of the public Api that snippets and mods compile against (<c>major.minor</c>).</summary>
    public string ApiVersion { get; set; } = "0.1";

    /// <summary>Unity version of the player.</summary>
    public string UnityVersion { get; set; } = "unknown";

    /// <summary><c>mono</c> or <c>il2cpp</c>.</summary>
    public string ScriptingBackend { get; set; } = "mono";

    /// <summary>Unity runtime platform, e.g. <c>WindowsPlayer</c>.</summary>
    public string Platform { get; set; } = "unknown";

    /// <summary>The mod loader's name.</summary>
    public string LoaderName { get; set; } = "none";

    /// <summary>The mod loader's version.</summary>
    public string LoaderVersion { get; set; } = "0";

    /// <summary>Process id.</summary>
    public int ProcessId { get; set; }

    /// <summary>Process name.</summary>
    public string ProcessName { get; set; } = string.Empty;

    /// <summary>Full path of the process executable.</summary>
    public string ProcessPath { get; set; } = string.Empty;

    /// <summary>An environment filled with the current process's id, name and path.</summary>
    public static AgentEnvironment ForCurrentProcess()
    {
        var environment = new AgentEnvironment();
        using var process = Process.GetCurrentProcess();
        environment.ProcessId = process.Id;
        environment.ProcessName = process.ProcessName;
        try
        {
            environment.ProcessPath = process.MainModule?.FileName ?? string.Empty;
        }
        catch (Exception)
        {
            // Some runtimes restrict MainModule; the path is informative for discovery matching only.
        }

        return environment;
    }

    /// <summary>The version and commit the build stamped (informational version <c>&lt;version&gt;+&lt;commit&gt;</c>).</summary>
    internal static (string Version, string? Commit) BuildVersion()
    {
        var info = typeof(AgentEnvironment).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(info))
        {
            return ("0.0.0", null);
        }

        var plus = info!.IndexOf('+');
        return plus >= 0 ? (info.Substring(0, plus), info.Substring(plus + 1)) : (info, null);
    }
}
