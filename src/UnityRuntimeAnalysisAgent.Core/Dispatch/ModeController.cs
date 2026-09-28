using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Json;
using UnityRuntimeAnalysisAgent.Core.Hosting;

namespace UnityRuntimeAnalysisAgent.Core.Dispatch;

/// <summary>
/// The agent's permission mode: <c>ReadOnly</c> &lt; <c>ReadOnly+Load</c> &lt; <c>Full</c>. It starts from
/// <c>Security.Mode</c>. Remote clients can only lower it; raising it is the user's decision, made through the configuration.
/// </summary>
public sealed class ModeController
{
    private readonly object _gate = new();
    private readonly IAgentLogger _log;
    private AgentMode _mode;

    /// <summary>Creates the controller with the configured mode.</summary>
    public ModeController(AgentMode initial, IAgentLogger log)
    {
        _mode = initial;
        _log = log;
    }

    /// <summary>The current mode.</summary>
    public AgentMode Current
    {
        get
        {
            lock (_gate)
            {
                return _mode;
            }
        }
    }

    /// <summary>The rank of a mode (higher allows more).</summary>
    public static int Rank(AgentMode mode) => mode switch
    {
        AgentMode.ReadOnly => 0,
        AgentMode.ReadOnlyLoad => 1,
        _ => 2,
    };

    /// <summary>Whether the current mode allows something that needs <paramref name="required"/>.</summary>
    public bool Allows(AgentMode required) => Rank(Current) >= Rank(required);

    /// <summary>Lowers (or keeps) the mode. Raising it throws <c>MODE_FORBIDDEN</c>. Returns the previous mode.</summary>
    public AgentMode Lower(AgentMode requested)
    {
        lock (_gate)
        {
            var previous = _mode;
            if (Rank(requested) > Rank(previous))
            {
                throw new ProtocolException(
                    ErrorCodes.ModeForbidden,
                    $"The mode can only be lowered remotely ({AgentModes.ToWire(previous)} → {AgentModes.ToWire(requested)} is a raise).",
                    AgentErrors.Data(
                        ("requiredMode", new JsonString(AgentModes.ToWire(requested))),
                        ("hint", new JsonString("Raising the mode is the user's decision: ULM writes Security.Mode to the agent's config and the game restarts."))));
            }

            _mode = requested;
            if (previous != requested)
            {
                _log.Warning($"Mode lowered from {AgentModes.ToWire(previous)} to {AgentModes.ToWire(requested)}.");
            }

            return previous;
        }
    }
}
