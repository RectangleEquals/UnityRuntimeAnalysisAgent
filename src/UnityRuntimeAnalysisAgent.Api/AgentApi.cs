using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("UnityRuntimeAnalysisAgent.Core")]

namespace UnityRuntimeAnalysisAgent.Api;

/// <summary>The agent, for code that isn't given a context (live patches).</summary>
public static class AgentApi
{
    /// <summary>
    /// The agent's context while it runs, or null (before it starts, after it stops, or when it isn't installed). Live
    /// patches use it from the game's main thread. It has no arguments, its <see cref="IAgentContext.Session"/> is one
    /// dictionary shared by every live patch, its hit counters stay until disposed, emitted events go to every client,
    /// and <see cref="IAgentContext.Return"/> does nothing.
    /// </summary>
    public static IAgentContext? Current { get; internal set; }
}
