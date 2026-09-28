using System;
using System.Collections.Generic;
using System.Linq;
using UnityLudometry.Protocol.Messages;

namespace UnityRuntimeAnalysisAgent.Core.Dispatch;

/// <summary>
/// Optional capabilities of this game and runtime (tags such as <c>module:addressables</c>). The reflection binders of
/// optional Unity modules report here once they've probed; until then a module is unavailable, and methods that need it
/// answer <c>UNSUPPORTED</c>.
/// </summary>
public sealed class CapabilitySet
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ModuleCapability> _modules = new(StringComparer.Ordinal);

    /// <summary>Records a module's availability (tag <c>module:&lt;name&gt;</c>).</summary>
    public void SetModule(string name, bool available, string? version = null, string? reason = null)
    {
        lock (_gate)
        {
            _modules[name] = new ModuleCapability { Name = name, Available = available, Version = version, Reason = reason };
        }
    }

    /// <summary>Whether a capability tag is available.</summary>
    public bool IsAvailable(string tag)
    {
        const string modulePrefix = "module:";
        if (!tag.StartsWith(modulePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        lock (_gate)
        {
            return _modules.TryGetValue(tag.Substring(modulePrefix.Length), out var module) && module.Available;
        }
    }

    /// <summary>The modules probed so far (for <c>agent.capabilities</c>).</summary>
    public List<ModuleCapability> Modules()
    {
        lock (_gate)
        {
            return _modules.Values.OrderBy(m => m.Name, StringComparer.Ordinal).ToList();
        }
    }
}
