using UnityRuntimeAnalysisAgent.Core;

namespace UnityRuntimeAnalysisAgent.Core.Tests;

public sealed class ProtocolPinTests
{
    [Fact]
    public void The_pinned_protocol_is_the_expected_version()
    {
        // Update this deliberately together with the external/protocol submodule pin: a protocol version change can
        // change the wire format, and before 1.0 the orchestrator must match major.minor exactly.
        Assert.Equal("0.1", AgentInfo.ProtocolVersionText);
    }
}
