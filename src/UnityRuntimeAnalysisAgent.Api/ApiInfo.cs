namespace UnityRuntimeAnalysisAgent.Api;

/// <summary>Version information of the public Api surface.</summary>
public static class ApiInfo
{
    /// <summary>
    /// The Api version. The major version changes on breaking changes and is checked by the orchestrator
    /// against the templates it compiles snippets, live patches and mod tests with; the minor version changes when
    /// something is added (0.2: the in-game test framework).
    /// </summary>
    public const string ApiVersion = "0.2";
}
