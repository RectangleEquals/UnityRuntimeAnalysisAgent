namespace UnityRuntimeAnalysisAgent.Integration;

/// <summary>
/// Locates the machine-local integration settings (a JSON file outside the repository).
/// Integration tests are skipped when the environment variable isn't set, e.g. in CI.
/// </summary>
internal static class IntegrationSettings
{
    public const string EnvironmentVariable = "URAA_INTEGRATION_SETTINGS";

    public static string? SettingsPath
    {
        get
        {
            var path = Environment.GetEnvironmentVariable(EnvironmentVariable);
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
    }

    public static void SkipIfUnavailable()
    {
        Assert.SkipWhen(SettingsPath is null, $"{EnvironmentVariable} is not set; integration tests run locally only.");
        Assert.SkipUnless(File.Exists(SettingsPath), $"Integration settings file not found: {SettingsPath}");
    }
}
