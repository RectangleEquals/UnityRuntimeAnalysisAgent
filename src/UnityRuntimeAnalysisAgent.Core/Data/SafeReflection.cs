using System.Reflection;

namespace UnityRuntimeAnalysisAgent.Core.Data;

/// <summary>
/// Reflection calls that can take the whole game down. Mono aborts the process (a native error no <c>catch</c> can stop)
/// when it reads the parameters of an internal call whose engine function isn't registered and whose parameters or
/// return value carry marshaling information: <c>GetParameters</c> looks the function up to marshal it. Engine builds
/// ship such bindings (e.g. Unity's Baselib networking), so the agent never reads the parameters of internal calls: their
/// anchors stay exact, only their parameter lists are unknown.
/// </summary>
public static class SafeReflection
{
    /// <summary>Whether a method is an internal call (implemented inside the engine or runtime).</summary>
    public static bool IsInternalCall(MethodBase method)
    {
        try
        {
            return (method.GetMethodImplementationFlags() & MethodImplAttributes.InternalCall) != 0;
        }
        catch (System.Exception)
        {
            return true; // unknown: treat as unsafe
        }
    }

    /// <summary>The parameters of a method, or null when reading them isn't safe (internal calls).</summary>
    public static ParameterInfo[]? Parameters(MethodBase method) => IsInternalCall(method) ? null : method.GetParameters();

    /// <summary>The index parameters of a property, or null when reading them isn't safe (an internal-call accessor).</summary>
    public static ParameterInfo[]? IndexParameters(PropertyInfo property)
    {
        var accessor = (MethodBase?)property.GetGetMethod(true) ?? property.GetSetMethod(true);
        return accessor is not null && IsInternalCall(accessor) ? null : property.GetIndexParameters();
    }

    /// <summary>Whether a property can be read as a plain value: readable, not indexed, and known to be so.</summary>
    public static bool IsPlainReadable(PropertyInfo property) => property.CanRead && IndexParameters(property) is { Length: 0 };
}
