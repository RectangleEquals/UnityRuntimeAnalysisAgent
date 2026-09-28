using System;
using System.Linq;
using System.Reflection;
using UnityRuntimeAnalysisAgent.Core.Dispatch;

namespace UnityRuntimeAnalysisAgent.Unity;

/// <summary>Thrown by a binder when something it needs is missing (the message becomes the module's reason).</summary>
public sealed class ModuleUnavailableException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ModuleUnavailableException(string reason)
        : base(reason)
    {
    }
}

/// <summary>
/// Base of the reflection binders for optional Unity modules (UI, TextMeshPro, Addressables, …). A binder resolves the
/// types and members it needs by name from the loaded assemblies the first time it's used, turns them into delegates, and
/// caches them. When something is missing, the module is reported as unavailable with the reason, and the methods that
/// need it answer <c>UNSUPPORTED</c>: one agent build works with games that don't ship a module, and across Unity versions.
/// </summary>
public abstract class ModuleBinder
{
    private readonly object _gate = new();
    private bool _bound;

    /// <summary>Creates a binder for a module (its capability tag is <c>module:&lt;name&gt;</c>).</summary>
    protected ModuleBinder(string module) => Module = module;

    /// <summary>The module name.</summary>
    public string Module { get; }

    /// <summary>Whether everything the binder needs was found.</summary>
    public bool Available { get; private set; }

    /// <summary>Why the module is unavailable, if it is.</summary>
    public string? Reason { get; private set; }

    /// <summary>The module's version, if the binder found one.</summary>
    public string? Version { get; protected set; }

    /// <summary>Binds on first use (thread-safe) and reports whether the module is available.</summary>
    public bool EnsureBound()
    {
        lock (_gate)
        {
            if (!_bound)
            {
                _bound = true;
                try
                {
                    Bind();
                    Available = true;
                }
                catch (ModuleUnavailableException e)
                {
                    Reason = e.Message;
                }
                catch (Exception e)
                {
                    Reason = $"Binding failed: {e.GetType().Name}: {e.Message}";
                }
            }

            return Available;
        }
    }

    /// <summary>Binds and records the outcome in the agent's capabilities.</summary>
    public void Report(CapabilitySet capabilities)
    {
        EnsureBound();
        capabilities.SetModule(Module, Available, Version, Reason);
    }

    /// <summary>Resolves everything the module needs; throws <see cref="ModuleUnavailableException"/> when something is missing.</summary>
    protected abstract void Bind();

    /// <summary>A type by full name from any loaded assembly (optionally only from assemblies with a given simple name).</summary>
    protected static Type RequireType(string fullName, string? assemblyName = null)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assemblyName is not null && !string.Equals(assembly.GetName().Name, assemblyName, StringComparison.Ordinal))
            {
                continue;
            }

            var type = assembly.GetType(fullName, throwOnError: false);
            if (type is not null)
            {
                return type;
            }
        }

        throw new ModuleUnavailableException($"{fullName} isn't loaded" + (assemblyName is null ? "." : $" (from {assemblyName})."));
    }

    /// <summary>A method as a delegate. Pass the parameter types to pick an overload.</summary>
    protected static TDelegate RequireMethod<TDelegate>(Type type, string name, BindingFlags flags, params Type[] parameterTypes)
        where TDelegate : Delegate
    {
        var method = type.GetMethods(flags).FirstOrDefault(m => m.Name == name && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameterTypes))
            ?? throw new ModuleUnavailableException($"{type.FullName}.{name}({string.Join(", ", parameterTypes.Select(t => t.Name))}) doesn't exist in this version.");
        return method.IsStatic
            ? (TDelegate)Delegate.CreateDelegate(typeof(TDelegate), method)
            : (TDelegate)Delegate.CreateDelegate(typeof(TDelegate), null, method);
    }

    /// <summary>A property getter as a delegate taking the instance (or nothing, for static properties).</summary>
    protected static TDelegate RequireGetter<TDelegate>(Type type, string name, BindingFlags flags)
        where TDelegate : Delegate
    {
        var getter = type.GetProperty(name, flags)?.GetGetMethod(nonPublic: true)
            ?? throw new ModuleUnavailableException($"{type.FullName}.{name} doesn't exist in this version.");
        return getter.IsStatic
            ? (TDelegate)Delegate.CreateDelegate(typeof(TDelegate), getter)
            : (TDelegate)Delegate.CreateDelegate(typeof(TDelegate), null, getter);
    }
}
