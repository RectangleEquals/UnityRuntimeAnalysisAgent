using System.Collections.Generic;
using UnityRuntimeAnalysisAgent.Core.Input;

namespace UnityRuntimeAnalysisAgent.Unity.Input;

/// <summary>The input layers the agent offers, in order of preference when several apply (actions first).</summary>
internal static class InputLayerSet
{
    /// <summary>One instance of each layer.</summary>
    public static IReadOnlyList<IInputLayer> Create() => System.Array.Empty<IInputLayer>();
}
