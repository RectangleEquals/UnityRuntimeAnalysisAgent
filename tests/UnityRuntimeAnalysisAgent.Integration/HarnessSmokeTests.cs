namespace UnityRuntimeAnalysisAgent.Integration;

public sealed class HarnessSmokeTests
{
    [Fact]
    public void Settings_are_available_when_running_locally()
    {
        IntegrationSettings.SkipIfUnavailable();
        Assert.True(File.Exists(IntegrationSettings.SettingsPath));
    }
}
