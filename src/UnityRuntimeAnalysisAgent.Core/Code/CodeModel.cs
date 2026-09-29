using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityRuntimeAnalysisAgent.Core.Data;

namespace UnityRuntimeAnalysisAgent.Core.Code;

/// <summary>The session's view of code: the assembly catalogue, the type index and the cross-reference index.</summary>
public sealed class CodeModel
{
    private readonly ModuleMap _modules;

    /// <summary>Creates the code model over the data model's module map.</summary>
    public CodeModel(ModuleMap modules)
    {
        _modules = modules;
        Catalog = new AssemblyCatalog(modules);
        Types = new TypeIndex(modules);
        Xrefs = new XrefIndex(modules);
    }

    /// <summary>The loaded assemblies.</summary>
    public AssemblyCatalog Catalog { get; }

    /// <summary>Loaded types and their hierarchy.</summary>
    public TypeIndex Types { get; }

    /// <summary>Cross-references.</summary>
    public XrefIndex Xrefs { get; }

    /// <summary>A loaded type by full name (exploratory; the first loaded build wins), or null.</summary>
    public Type? FindType(string fullName)
    {
        foreach (var module in _modules.Modules.OrderBy(m => m.Assembly.GetName().Name, StringComparer.Ordinal))
        {
            try
            {
                if (module.GetType(fullName, throwOnError: false, ignoreCase: false) is { } type)
                {
                    return type;
                }
            }
            catch (Exception)
            {
                // a module that can't be searched
            }
        }

        return null;
    }

    /// <summary>Unity's base types the rules need, when loaded.</summary>
    public IEnumerable<Type> UnityBaseTypes() =>
        new[] { FindType("UnityEngine.MonoBehaviour"), FindType("UnityEngine.ScriptableObject") }.Where(t => t is not null)!;
}
