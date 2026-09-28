using UnityLudometry.Protocol;

namespace UnityRuntimeAnalysisAgent.Core;

/// <summary>Static identity of the agent. Populated further as milestones land.</summary>
public static class AgentIdentity
{
    /// <summary>The BepInEx plugin GUID (also the config file name stem).</summary>
    public const string PluginGuid = "com.github.rectangleequals.unityruntimeanalysisagent";

    /// <summary>Human-readable plugin name.</summary>
    public const string PluginName = "UnityRuntimeAnalysisAgent";

    /// <summary>The protocol version this agent speaks (<c>major.minor</c>), from the pinned protocol package.</summary>
    public static string ProtocolVersionText => ProtocolVersion.Text;
}
