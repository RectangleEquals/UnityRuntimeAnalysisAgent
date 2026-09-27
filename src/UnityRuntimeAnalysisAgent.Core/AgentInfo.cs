namespace UnityRuntimeAnalysisAgent.Core;

/// <summary>Static identity of the agent. Populated further as milestones land.</summary>
public static class AgentInfo
{
    /// <summary>The BepInEx plugin GUID (also the config file name stem).</summary>
    public const string PluginGuid = "com.github.rectangleequals.unityruntimeanalysisagent";

    /// <summary>Human-readable plugin name.</summary>
    public const string PluginName = "UnityRuntimeAnalysisAgent";
}
