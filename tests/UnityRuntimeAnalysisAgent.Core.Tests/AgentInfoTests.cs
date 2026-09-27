using UnityRuntimeAnalysisAgent.Core;

namespace UnityRuntimeAnalysisAgent.Core.Tests;

public sealed class AgentInfoTests
{
    [Fact]
    public void Plugin_guid_matches_the_agreed_identifier()
    {
        // The orchestrator writes BepInEx\config\<guid>.cfg, so this value is part of the orchestrator integration contract.
        Assert.Equal("com.github.rectangleequals.unityruntimeanalysisagent", AgentInfo.PluginGuid);
    }
}
