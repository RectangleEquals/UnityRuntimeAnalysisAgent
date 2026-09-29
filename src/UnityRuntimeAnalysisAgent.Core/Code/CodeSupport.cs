using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Data;

namespace UnityRuntimeAnalysisAgent.Core.Code;

/// <summary>
/// Assembly-name globs (<c>*</c>, <c>?</c>, case-insensitive). Bulk jobs always leave out the agent's own assemblies, the
/// protocol package and Harmony (<c>0Harmony</c>); every other default comes from the caller.
/// </summary>
public sealed class AssemblyFilter
{
    // Exact names: a game's own assembly may well start with the same words.
    private static readonly HashSet<string> AgentAssemblies = new(StringComparer.Ordinal)
    {
        "UnityRuntimeAnalysisAgent.Core", "UnityRuntimeAnalysisAgent.Unity", "UnityRuntimeAnalysisAgent.Overlay", "UnityRuntimeAnalysisAgent.Api",
        "UnityRuntimeAnalysisAgent.BepInEx5", "UnityLudometry.Protocol",
    };
    private readonly Regex[] _include;
    private readonly Regex[] _exclude;
    private readonly bool _builtIn;

    /// <summary>Creates a filter; <paramref name="builtInExclusions"/> adds the agent/protocol/Harmony exclusions.</summary>
    public AssemblyFilter(IEnumerable<string>? include, IEnumerable<string>? exclude, bool builtInExclusions)
    {
        _include = (include ?? Array.Empty<string>()).Select(Glob).ToArray();
        _exclude = (exclude ?? Array.Empty<string>()).Select(Glob).ToArray();
        _builtIn = builtInExclusions;
    }

    /// <summary>Everything (no exclusions at all).</summary>
    public static AssemblyFilter All { get; } = new(null, null, builtInExclusions: false);

    /// <summary>Whether the assembly named <paramref name="name"/> is in.</summary>
    public bool Includes(string name)
    {
        if (_builtIn && (IsAgentAssembly(name) || name.StartsWith("0Harmony", StringComparison.Ordinal)))
        {
            return false;
        }

        return (_include.Length == 0 || _include.Any(g => g.IsMatch(name))) && !_exclude.Any(g => g.IsMatch(name));
    }

    /// <summary>Whether the agent itself loaded (or is) this assembly.</summary>
    public static bool IsAgentAssembly(string name) => AgentAssemblies.Contains(name);

    private static Regex Glob(string glob) =>
        new("^" + Regex.Escape(glob).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}

/// <summary>How members and types are described: kinds, visibility, signatures and decoded attributes.</summary>
public static class Describe
{
    /// <summary>All declared members of a type, in token order.</summary>
    public const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary><c>class</c>, <c>struct</c>, <c>interface</c>, <c>enum</c> or <c>delegate</c>.</summary>
    public static string TypeKind(Type type) =>
        type.IsInterface ? "interface" : type.IsEnum ? "enum" : type.IsValueType ? "struct" : typeof(Delegate).IsAssignableFrom(type.BaseType) ? "delegate" : "class";

    /// <summary><c>field</c>, <c>method</c>, <c>constructor</c>, <c>property</c>, <c>event</c> or <c>nestedType</c>.</summary>
    public static string MemberKind(MemberInfo member) => member switch
    {
        FieldInfo => "field",
        ConstructorInfo => "constructor",
        MethodInfo => "method",
        PropertyInfo => "property",
        EventInfo => "event",
        _ => "nestedType",
    };

    /// <summary><c>public</c>, <c>private</c>, <c>protected</c>, <c>internal</c>, <c>protected internal</c> or <c>private protected</c>.</summary>
    public static string Visibility(MemberInfo member)
    {
        switch (member)
        {
            case FieldInfo f:
                return f.IsPublic ? "public" : f.IsPrivate ? "private" : f.IsFamily ? "protected" : f.IsAssembly ? "internal" : f.IsFamilyOrAssembly ? "protected internal" : "private protected";
            case MethodBase m:
                return m.IsPublic ? "public" : m.IsPrivate ? "private" : m.IsFamily ? "protected" : m.IsAssembly ? "internal" : m.IsFamilyOrAssembly ? "protected internal" : "private protected";
            case PropertyInfo p:
                return Visibility((MemberInfo?)p.GetGetMethod(true) ?? p.GetSetMethod(true)!);
            case EventInfo e:
                return Visibility(e.GetAddMethod(true)!);
            case Type t:
                return t.IsPublic || t.IsNestedPublic ? "public" : t.IsNestedPrivate ? "private" : t.IsNestedFamily ? "protected" : t.IsNestedFamORAssem ? "protected internal" : t.IsNestedFamANDAssem ? "private protected" : "internal";
            default:
                return "public";
        }
    }

    /// <summary>Whether the member is static.</summary>
    public static bool IsStatic(MemberInfo member) => member switch
    {
        FieldInfo f => f.IsStatic,
        MethodBase m => m.IsStatic,
        PropertyInfo p => ((MethodBase?)p.GetGetMethod(true) ?? p.GetSetMethod(true))?.IsStatic ?? false,
        EventInfo e => e.GetAddMethod(true)?.IsStatic ?? false,
        Type t => t.IsAbstract && t.IsSealed,
        _ => false,
    };

    /// <summary>A readable signature: <c>System.Void Add(Ns.Item,System.Int32)</c>, <c>System.Int32 count</c>.</summary>
    public static string Signature(MemberInfo member) => member switch
    {
        FieldInfo f => $"{AnchorWriter.TypeName(f.FieldType)} {f.Name}",
        ConstructorInfo c => $"{c.Name}({Parameters(c)})",
        MethodInfo m => $"{AnchorWriter.TypeName(m.ReturnType)} {m.Name}{GenericArgs(m)}({Parameters(m)})",
        PropertyInfo p => $"{AnchorWriter.TypeName(p.PropertyType)} {p.Name}{IndexText(p)}",
        EventInfo e => $"{AnchorWriter.TypeName(e.EventHandlerType!)} {e.Name}",
        Type t => AnchorWriter.TypeName(t),
        _ => member.Name,
    };

    /// <summary>The full names of the attributes on a member (never instantiated).</summary>
    public static List<string> AttributeNames(MemberInfo member) => AttributeData(member).Select(a => SafeName(a)).ToList();

    /// <summary>The attributes on a member or assembly, decoded from metadata (never instantiated).</summary>
    public static IList<CustomAttributeData> AttributeData(object target)
    {
        try
        {
            return target switch
            {
                MemberInfo m => CustomAttributeData.GetCustomAttributes(m),
                Assembly a => CustomAttributeData.GetCustomAttributes(a),
                Module mo => CustomAttributeData.GetCustomAttributes(mo),
                System.Reflection.ParameterInfo p => CustomAttributeData.GetCustomAttributes(p),
                _ => Array.Empty<CustomAttributeData>(),
            };
        }
        catch (Exception)
        {
            return Array.Empty<CustomAttributeData>(); // an attribute type from a missing assembly
        }
    }

    /// <summary>An attribute as <c>{type, ctorArgs, namedArgs}</c>.</summary>
    public static JsonObject Attribute(CustomAttributeData data)
    {
        var result = new JsonObject { { "type", JsonValue.From(SafeName(data)) } };
        try
        {
            result.Add("ctorArgs", new JsonArray(data.ConstructorArguments.Select(a => (JsonValue?)Argument(a))));
            var named = new JsonObject();
            foreach (var arg in data.NamedArguments ?? (IList<CustomAttributeNamedArgument>)Array.Empty<CustomAttributeNamedArgument>())
            {
                named.Set(arg.MemberName, Argument(arg.TypedValue));
            }

            result.Add("namedArgs", named);
        }
        catch (Exception e)
        {
            result.Add("error", JsonValue.From(e.Message));
        }

        return result;
    }

    /// <summary>The attribute's full name (or what's known of it when its assembly is missing).</summary>
    public static string SafeName(CustomAttributeData data)
    {
        try
        {
            return data.AttributeType.FullName ?? data.AttributeType.Name;
        }
        catch (Exception)
        {
            return data.ToString();
        }
    }

    /// <summary>Whether a member carries an attribute with this full name.</summary>
    public static bool HasAttribute(MemberInfo member, string fullName) => AttributeData(member).Any(a => SafeName(a) == fullName);

    private static JsonValue Argument(CustomAttributeTypedArgument argument) => argument.Value switch
    {
        null => JsonNull.Instance,
        string s => JsonValue.From(s),
        bool b => JsonValue.From(b),
        char c => JsonValue.From(c.ToString()),
        Type t => JsonValue.From(t.FullName ?? t.Name),
        float f => float.IsNaN(f) || float.IsInfinity(f) ? JsonValue.From(f.ToString(CultureInfo.InvariantCulture)) : JsonNumber.FromRawText(f.ToString("R", CultureInfo.InvariantCulture)),
        double d => double.IsNaN(d) || double.IsInfinity(d) ? JsonValue.From(d.ToString(CultureInfo.InvariantCulture)) : new JsonNumber(d),
        ulong u => new JsonNumber(u),
        IEnumerable<CustomAttributeTypedArgument> items => new JsonArray(items.Select(i => (JsonValue?)Argument(i))),
        IConvertible number => new JsonNumber(Convert.ToInt64(number, CultureInfo.InvariantCulture)),
        var other => JsonValue.From(other.ToString()),
    };

    private static string Parameters(MethodBase method) =>
        SafeReflection.Parameters(method) is { } known ? string.Join(",", known.Select(p => AnchorWriter.TypeName(p.ParameterType))) : "?";

    private static string IndexText(PropertyInfo property) => SafeReflection.IndexParameters(property) switch
    {
        null => "[?]",
        { Length: 0 } => string.Empty,
        var index => "[" + string.Join(",", index.Select(i => AnchorWriter.TypeName(i.ParameterType))) + "]",
    };

    private static string GenericArgs(MethodInfo method) =>
        method.IsGenericMethod ? "<" + string.Join(",", method.GetGenericArguments().Select(AnchorWriter.TypeName)) + ">" : string.Empty;
}

/// <summary>
/// The loaded assemblies, described: identity, where they came from, and the SHA-256 of the file on disk (computed once
/// per path and write time). Assemblies loaded later are picked up as they load.
/// </summary>
public sealed class AssemblyCatalog
{
    private readonly ConcurrentDictionary<string, (DateTime Written, string Sha256)> _hashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ModuleMap _modules;

    /// <summary>Creates the catalogue over the module map.</summary>
    public AssemblyCatalog(ModuleMap modules) => _modules = modules;

    /// <summary>The loaded assemblies, by name.</summary>
    public IReadOnlyList<Assembly> Assemblies(AssemblyFilter filter) => _modules.Modules
        .Select(m => m.Assembly)
        .Distinct()
        .Where(a => filter.Includes(Name(a)))
        .OrderBy(Name, StringComparer.Ordinal)
        .ThenBy(a => a.ManifestModule.ModuleVersionId)
        .ToList();

    /// <summary>An assembly by MVID or name (<c>NOT_FOUND</c> otherwise).</summary>
    public Assembly Find(string? mvid, string? name, string param)
    {
        if (mvid is not null)
        {
            return Guid.TryParse(mvid, out var id) && _modules.TryGet(id, out var module)
                ? module.Assembly
                : throw DataErrors.NotFound(param, $"No loaded module {mvid}.");
        }

        return Assemblies(AssemblyFilter.All).FirstOrDefault(a => Name(a) == name) ?? throw DataErrors.NotFound(param, $"No loaded assembly '{name}'.");
    }

    /// <summary>The summary of an assembly.</summary>
    public AssemblySummary Summary(Assembly assembly)
    {
        var name = Name(assembly);
        var location = Location(assembly);
        return new AssemblySummary
        {
            Name = name,
            Version = assembly.GetName().Version?.ToString() ?? "0.0.0.0",
            Mvid = assembly.ManifestModule.ModuleVersionId.ToString(),
            Location = location,
            FileSha256 = location is null ? null : FileSha256(location),
            LoadedFromBytes = !assembly.IsDynamic && location is null,
            IsDynamic = assembly.IsDynamic,
            LoadedByAgent = AssemblyFilter.IsAgentAssembly(name),
            ReferencedAssemblies = SafeReferences(assembly),
            TypeCount = assembly.IsDynamic ? 0 : assembly.ManifestModule is { } m ? AnchorResolver.LoadableTypes(m).Count() : 0,
        };
    }

    /// <summary>The simple name of an assembly.</summary>
    public static string Name(Assembly assembly) => assembly.GetName().Name ?? "?";

    /// <summary>The file an assembly was loaded from, or null (dynamic, or loaded from bytes).</summary>
    public static string? Location(Assembly assembly)
    {
        if (assembly.IsDynamic)
        {
            return null;
        }

        try
        {
            return string.IsNullOrEmpty(assembly.Location) ? null : assembly.Location;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The SHA-256 of a file (cached per path and write time), or null when it can't be read.</summary>
    public string? FileSha256(string path)
    {
        try
        {
            var written = File.GetLastWriteTimeUtc(path);
            if (_hashes.TryGetValue(path, out var cached) && cached.Written == written)
            {
                return cached.Sha256;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sha = SHA256.Create();
            var hash = Hex(sha.ComputeHash(stream));
            _hashes[path] = (written, hash);
            return hash;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Lowercase hex.</summary>
    public static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();

    private static List<string> SafeReferences(Assembly assembly)
    {
        try
        {
            return assembly.GetReferencedAssemblies().Select(r => r.Name ?? "?").OrderBy(n => n, StringComparer.Ordinal).ToList();
        }
        catch (Exception)
        {
            return new List<string>();
        }
    }
}

/// <summary>
/// Every loadable type of the loaded modules, and who derives from whom, built on first use and extended as assemblies
/// load. Types that fail to load are skipped (their assemblies report the errors).
/// </summary>
public sealed class TypeIndex
{
    private readonly object _gate = new();
    private readonly ModuleMap _modules;
    private readonly HashSet<Module> _indexed = new();
    private readonly Dictionary<Type, List<Type>> _subtypes = new();
    private readonly Dictionary<Type, List<Type>> _implementers = new();
    private readonly List<Type> _types = new();

    /// <summary>Creates the index over the module map.</summary>
    public TypeIndex(ModuleMap modules) => _modules = modules;

    /// <summary>Every loadable type (a snapshot).</summary>
    public IReadOnlyList<Type> Types
    {
        get
        {
            Refresh();
            lock (_gate)
            {
                return _types.ToList();
            }
        }
    }

    /// <summary>Direct subtypes of a type (generic definitions match their constructions).</summary>
    public IReadOnlyList<Type> DirectSubtypes(Type type)
    {
        Refresh();
        lock (_gate)
        {
            return _subtypes.TryGetValue(Key(type), out var list) ? list.ToList() : new List<Type>();
        }
    }

    /// <summary>Types that implement an interface (directly or through a base), definitions matched generically.</summary>
    public IReadOnlyList<Type> Implementers(Type @interface)
    {
        Refresh();
        lock (_gate)
        {
            return _implementers.TryGetValue(Key(@interface), out var list) ? list.ToList() : new List<Type>();
        }
    }

    /// <summary>The generic definition of a type, or the type.</summary>
    public static Type Key(Type type) => type.IsGenericType && !type.IsGenericTypeDefinition ? type.GetGenericTypeDefinition() : type;

    private void Refresh()
    {
        foreach (var module in _modules.Modules)
        {
            lock (_gate)
            {
                if (!_indexed.Add(module))
                {
                    continue;
                }
            }

            var types = AnchorResolver.LoadableTypes(module).ToList();
            lock (_gate)
            {
                foreach (var type in types)
                {
                    _types.Add(type);
                    try
                    {
                        if (type.BaseType is { } baseType)
                        {
                            Add(_subtypes, Key(baseType), type);
                        }

                        foreach (var @interface in type.GetInterfaces())
                        {
                            Add(_implementers, Key(@interface), type);
                        }
                    }
                    catch (Exception)
                    {
                        // a base or interface from a missing assembly
                    }
                }
            }
        }
    }

    private static void Add(Dictionary<Type, List<Type>> map, Type key, Type value)
    {
        if (!map.TryGetValue(key, out var list))
        {
            map[key] = list = new List<Type>();
        }

        list.Add(value);
    }
}
