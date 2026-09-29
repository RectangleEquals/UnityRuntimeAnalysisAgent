using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityRuntimeAnalysisAgent.Core.Abstractions;

namespace UnityRuntimeAnalysisAgent.Unity;

/// <summary>
/// Addressables by reflection (the package is optional and its API moves between versions): the resource locators and
/// their keys and locations, <c>LoadAssetAsync&lt;T&gt;</c> and <c>Release</c>. The assemblies are loaded by name when the
/// game ships them but hasn't touched them yet (the runtime loads assemblies lazily).
/// </summary>
public sealed class AddressablesBinder : ModuleBinder, IAddressablesApi
{
    private const BindingFlags Static = BindingFlags.Public | BindingFlags.Static;
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.Instance;
    private PropertyInfo _locators = null!;
    private PropertyInfo? _runtimePath;
    private Type _locatorType = null!;
    private Type _locationType = null!;
    private MethodInfo _loadAssetAsync = null!;
    private MethodInfo _releaseGeneric = null!;

    /// <summary>Creates the binder (module <c>addressables</c>).</summary>
    public AddressablesBinder()
        : base("addressables")
    {
    }

    /// <inheritdoc />
    public string? RuntimePath => _runtimePath?.GetValue(null, null) as string;

    /// <inheritdoc />
    protected override void Bind()
    {
        var addressables = Find("UnityEngine.AddressableAssets.Addressables", "Unity.Addressables");
        _locatorType = Find("UnityEngine.AddressableAssets.ResourceLocators.IResourceLocator", "Unity.Addressables");
        _locationType = Find("UnityEngine.ResourceManagement.ResourceLocations.IResourceLocation", "Unity.ResourceManager");
        _locators = addressables.GetProperty("ResourceLocators", Static) ?? throw new ModuleUnavailableException("Addressables.ResourceLocators doesn't exist in this version.");
        _runtimePath = addressables.GetProperty("RuntimePath", Static);
        _loadAssetAsync = addressables.GetMethods(Static).FirstOrDefault(m => m.Name == "LoadAssetAsync" && m.IsGenericMethodDefinition
                && m.GetParameters() is { Length: 1 } p && p[0].ParameterType == typeof(object))
            ?? throw new ModuleUnavailableException("Addressables.LoadAssetAsync<T>(object key) doesn't exist in this version.");
        _releaseGeneric = addressables.GetMethods(Static).FirstOrDefault(m => m.Name == "Release" && m.IsGenericMethodDefinition
                && m.GetParameters() is { Length: 1 } p && p[0].ParameterType.IsGenericType && p[0].ParameterType.Name.StartsWith("AsyncOperationHandle", StringComparison.Ordinal))
            ?? throw new ModuleUnavailableException("Addressables.Release<T>(AsyncOperationHandle<T>) doesn't exist in this version.");
        Version = addressables.Assembly.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false).OfType<AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> LocatorIds() => Locators().Select(l => Get(l, "LocatorId") as string ?? l.GetType().Name).ToList();

    /// <inheritdoc />
    public IEnumerable<object> Keys(string? locatorId)
    {
        foreach (var locator in Locators())
        {
            if (locatorId is not null && Get(locator, "LocatorId") as string != locatorId)
            {
                continue;
            }

            if (Get(locator, "Keys") is IEnumerable keys)
            {
                foreach (var key in keys)
                {
                    if (key is not null)
                    {
                        yield return key;
                    }
                }
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<AddressableLocationFacts> Locate(object key, Type? type, string? locatorId)
    {
        var found = new List<AddressableLocationFacts>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var locator in Locators())
        {
            if (locatorId is not null && Get(locator, "LocatorId") as string != locatorId)
            {
                continue;
            }

            var locate = locator.GetType().GetMethod("Locate", Instance, null, new[] { typeof(object), typeof(Type), typeof(IList<>).MakeGenericType(_locationType).MakeByRefType() }, null)
                ?? _locatorType.GetMethod("Locate");
            if (locate is null)
            {
                continue;
            }

            var args = new object?[] { key, type ?? typeof(object), null };
            if (locate.Invoke(locator, args) is true && args[2] is IEnumerable locations)
            {
                foreach (var location in locations)
                {
                    var facts = Facts(location);
                    if (seen.Add(facts.InternalId + "|" + facts.ResourceType))
                    {
                        found.Add(facts);
                    }
                }
            }
        }

        return found;
    }

    /// <inheritdoc />
    public object LoadAsync(object key, Type type)
    {
        try
        {
            var handle = _loadAssetAsync.MakeGenericMethod(type).Invoke(null, new[] { key })!;
            return new Operation(handle, type);
        }
        catch (TargetInvocationException e)
        {
            throw e.InnerException ?? e;
        }
    }

    /// <inheritdoc />
    public AddressablesOperation Poll(object operation)
    {
        var handle = ((Operation)operation).Handle;
        if (Get(handle, "IsDone") is not true)
        {
            return new AddressablesOperation(false, false, null, null);
        }

        var succeeded = Get(handle, "Status")?.ToString() == "Succeeded";
        return new AddressablesOperation(true, succeeded, succeeded ? Get(handle, "Result") : null, (Get(handle, "OperationException") as Exception)?.Message);
    }

    /// <inheritdoc />
    public void Release(object operation)
    {
        var op = (Operation)operation;
        try
        {
            _releaseGeneric.MakeGenericMethod(op.Type).Invoke(null, new[] { op.Handle });
        }
        catch (TargetInvocationException e)
        {
            throw e.InnerException ?? e;
        }
    }

    private IEnumerable<object> Locators() => _locators.GetValue(null, null) is IEnumerable locators ? locators.Cast<object>().Where(l => l is not null) : Enumerable.Empty<object>();

    private AddressableLocationFacts Facts(object location) => new()
    {
        InternalId = Get(location, "InternalId") as string ?? string.Empty,
        ProviderId = Get(location, "ProviderId") as string ?? string.Empty,
        ResourceType = (Get(location, "ResourceType") as Type)?.FullName ?? string.Empty,
        Dependencies = Get(location, "Dependencies") is IEnumerable dependencies
            ? dependencies.Cast<object>().Where(d => d is not null).Select(d => Get(d, "InternalId") as string ?? string.Empty).ToList()
            : new List<string>(),
        PrimaryKey = Get(location, "PrimaryKey") as string ?? string.Empty,
    };

    // Interface members are found on the interface too (explicit implementations don't show on the class).
    private object? Get(object target, string name)
    {
        var type = target.GetType();
        var property = type.GetProperty(name, Instance)
            ?? (type.GetInterfaces().FirstOrDefault(i => i == _locatorType || i == _locationType || i.GetProperty(name) is not null)?.GetProperty(name));
        return property?.GetValue(target, null);
    }

    // By full name among the loaded assemblies, else by loading the package's assembly by name.
    private static Type Find(string fullName, string assemblyName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.GetType(fullName, throwOnError: false) is { } loaded)
            {
                return loaded;
            }
        }

        try
        {
            return Type.GetType($"{fullName}, {assemblyName}", throwOnError: false) ?? throw new ModuleUnavailableException($"{fullName} isn't available (the game doesn't ship {assemblyName}).");
        }
        catch (Exception e) when (e is System.IO.FileLoadException or BadImageFormatException or TypeLoadException)
        {
            throw new ModuleUnavailableException($"{assemblyName} couldn't be loaded: {e.Message}");
        }
    }

    private sealed class Operation
    {
        public Operation(object handle, Type type)
        {
            Handle = handle;
            Type = type;
        }

        public object Handle { get; }

        public Type Type { get; }
    }
}
