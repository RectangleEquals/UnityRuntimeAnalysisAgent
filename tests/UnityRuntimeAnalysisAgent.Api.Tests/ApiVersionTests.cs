using UnityRuntimeAnalysisAgent.Api;

namespace UnityRuntimeAnalysisAgent.Api.Tests;

public sealed class ApiVersionTests
{
    [Fact]
    public void Api_version_is_major_dot_minor()
    {
        // The public API snapshot test (PublicApiGenerator) is added when the Api gets its surface.
        var parts = ApiInfo.ApiVersion.Split('.');
        Assert.Equal(2, parts.Length);
        Assert.All(parts, p => Assert.True(int.TryParse(p, out _)));
    }
}
