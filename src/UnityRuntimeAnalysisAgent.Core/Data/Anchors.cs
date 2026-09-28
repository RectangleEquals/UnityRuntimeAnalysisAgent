using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;

namespace UnityRuntimeAnalysisAgent.Core.Data;

/// <summary>The metadata tables an anchor's token can point at (the token's high byte).</summary>
public enum TokenKind
{
    /// <summary>Not a definition table the agent resolves.</summary>
    Other = 0,

    /// <summary><c>0x02</c> TypeDef.</summary>
    Type = 0x02,

    /// <summary><c>0x04</c> Field.</summary>
    Field = 0x04,

    /// <summary><c>0x06</c> MethodDef (methods and constructors).</summary>
    Method = 0x06,

    /// <summary><c>0x14</c> Event.</summary>
    Event = 0x14,

    /// <summary><c>0x17</c> Property.</summary>
    Property = 0x17,
}

/// <summary>Token helpers.</summary>
public static class Tokens
{
    /// <summary>The table a token points at.</summary>
    public static TokenKind KindOf(long token) => ((token >> 24) & 0xFF) switch
    {
        0x02 => TokenKind.Type,
        0x04 => TokenKind.Field,
        0x06 => TokenKind.Method,
        0x14 => TokenKind.Event,
        0x17 => TokenKind.Property,
        _ => TokenKind.Other,
    };
}

/// <summary>
/// Every loaded module by its version id (MVID), kept current as assemblies load. An MVID identifies one exact build of one
/// assembly, which is what makes an anchor exact.
/// </summary>
public sealed class ModuleMap : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Module> _modules = new();
    private readonly AppDomain _domain;

    /// <summary>Maps the modules loaded in <paramref name="domain"/> (the current one by default) and follows new loads.</summary>
    public ModuleMap(AppDomain? domain = null)
    {
        _domain = domain ?? AppDomain.CurrentDomain;
        _domain.AssemblyLoad += OnAssemblyLoad;
        foreach (var assembly in _domain.GetAssemblies())
        {
            Add(assembly);
        }
    }

    /// <summary>The loaded modules (a snapshot).</summary>
    public IReadOnlyList<Module> Modules
    {
        get
        {
            lock (_gate)
            {
                return _modules.Values.ToList();
            }
        }
    }

    /// <summary>The module with this MVID, if loaded.</summary>
    public bool TryGet(Guid mvid, out Module module)
    {
        lock (_gate)
        {
            return _modules.TryGetValue(mvid, out module!);
        }
    }

    /// <summary>Adds an assembly's modules (also done automatically for assemblies loaded later).</summary>
    public void Add(Assembly assembly)
    {
        Module[] modules;
        try
        {
            modules = assembly.GetModules();
        }
        catch (Exception)
        {
            return; // some dynamic assemblies can't list their modules
        }

        lock (_gate)
        {
            foreach (var module in modules)
            {
                try
                {
                    // The same build loaded twice (e.g. from bytes) has the same MVID: anchors keep binding to the first copy.
                    if (!_modules.ContainsKey(module.ModuleVersionId))
                    {
                        _modules.Add(module.ModuleVersionId, module);
                    }
                }
                catch (Exception)
                {
                    // a module without a readable MVID can't be anchored
                }
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => _domain.AssemblyLoad -= OnAssemblyLoad;

    private void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args) => Add(args.LoadedAssembly);
}

/// <summary>
/// Turns anchors into live types and members: <c>Module.ResolveType/Field/Method</c> for type, field and method tokens,
/// and a per-module index (built on first use) for properties and events. Never binds by name.
/// </summary>
public sealed class AnchorResolver
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private readonly ModuleMap _modules;
    private readonly ConcurrentDictionary<Module, Dictionary<int, MemberInfo>> _propertiesAndEvents = new();

    /// <summary>Creates a resolver over the loaded modules.</summary>
    public AnchorResolver(ModuleMap modules) => _modules = modules;

    /// <summary>The module map.</summary>
    public ModuleMap Modules => _modules;

    /// <summary>A type reference: an anchor object, or the full name of a primitive/BCL type.</summary>
    public Type ResolveTypeRef(JsonValue? typeRef, string param) => typeRef switch
    {
        JsonString name => ResolveTypeName(name.Value, param),
        JsonObject => ResolveType(Read(typeRef, param), param),
        _ => throw DataErrors.InvalidParams(param, $"{param} must be an anchor or a type name."),
    };

    /// <summary>A type by anchor (a TypeDef token, with <c>typeArgs</c> for a constructed generic type).</summary>
    public Type ResolveType(Anchor anchor, string param)
    {
        if (Tokens.KindOf(anchor.Token) != TokenKind.Type)
        {
            throw DataErrors.InvalidParams(param + ".token", $"{param} isn't a type (token 0x{anchor.Token:x8}).");
        }

        var type = (Type)ResolveDefinition(anchor, param);
        return anchor.TypeArgs is { Count: > 0 } ? Construct(type, anchor.TypeArgs, param) : type;
    }

    /// <summary>Any member (or type) by anchor, bound to its generic arguments when the anchor carries them.</summary>
    public MemberInfo ResolveMember(Anchor anchor, string param)
    {
        var kind = Tokens.KindOf(anchor.Token);
        if (kind == TokenKind.Type)
        {
            return ResolveType(anchor, param);
        }

        var member = ResolveDefinition(anchor, param);
        if (anchor.TypeArgs is { Count: > 0 })
        {
            var declaring = Construct(member.DeclaringType!, anchor.TypeArgs, param + ".typeArgs");
            member = declaring.GetMembers(Declared).First(m => m.MetadataToken == member.MetadataToken);
        }

        if (anchor.MethodArgs is { Count: > 0 })
        {
            if (member is not MethodInfo { IsGenericMethodDefinition: true } generic || generic.GetGenericArguments().Length != anchor.MethodArgs.Count)
            {
                throw DataErrors.InvalidParams(param + ".methodArgs", $"{Describe(anchor)} doesn't take {anchor.MethodArgs.Count} generic argument(s).");
            }

            member = generic.MakeGenericMethod(anchor.MethodArgs.Select((a, i) => ResolveTypeRef(a, $"{param}.methodArgs[{i}]")).ToArray());
        }

        return member;
    }

    /// <summary>Binds a member resolved from its definition to <paramref name="runtimeType"/> (or its base) when the member's
    /// declaring type is generic: <c>List&lt;T&gt;._size</c> read on a <c>List&lt;int&gt;</c>. Returns null if the type
    /// doesn't have the member.</summary>
    public static MemberInfo? BindTo(MemberInfo member, Type runtimeType)
    {
        var declaring = member.DeclaringType;
        if (declaring is null)
        {
            return member;
        }

        for (var t = runtimeType; t is not null; t = t.BaseType)
        {
            var definition = t.IsGenericType ? t.GetGenericTypeDefinition() : t;
            var target = declaring.IsGenericType ? declaring.GetGenericTypeDefinition() : declaring;
            if (definition == target && t.Module == declaring.Module)
            {
                return t == declaring ? member : t.GetMembers(Declared).FirstOrDefault(m => m.MetadataToken == member.MetadataToken);
            }
        }

        return null;
    }

    /// <summary>A primitive/BCL type by full name (e.g. <c>System.Int32</c>, <c>System.String[]</c>).</summary>
    public static Type ResolveTypeName(string name, string param)
    {
        var type = Type.GetType(name, throwOnError: false) ?? typeof(object).Assembly.GetType(name, throwOnError: false);
        return type ?? throw DataErrors.InvalidParams(param, $"'{name}' isn't a primitive or BCL type: other types are given as anchors.");
    }

    /// <summary>Reads an anchor from JSON (INVALID_PARAMS when it isn't one).</summary>
    public static Anchor Read(JsonValue? json, string param)
    {
        try
        {
            var anchor = Anchor.Read(json, param);
            if (!Guid.TryParse(anchor.Mvid, out _))
            {
                throw DataErrors.InvalidParams(param + ".mvid", $"{param}.mvid isn't a GUID.");
            }

            return anchor;
        }
        catch (UnityLudometry.Protocol.ProtocolException)
        {
            throw;
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or ArgumentException)
        {
            throw DataErrors.InvalidParams(param, $"{param} isn't an anchor: {e.Message}");
        }
    }

    private MemberInfo ResolveDefinition(Anchor anchor, string param)
    {
        if (!Guid.TryParse(anchor.Mvid, out var mvid))
        {
            throw DataErrors.InvalidParams(param + ".mvid", $"{param}.mvid isn't a GUID.");
        }

        if (!_modules.TryGet(mvid, out var module))
        {
            throw DataErrors.IndexStale(anchor, $"Module {anchor.Mvid} is not loaded.", LoadedBuildsOf(anchor));
        }

        var token = unchecked((int)anchor.Token);
        MemberInfo? member;
        try
        {
            member = Tokens.KindOf(anchor.Token) switch
            {
                TokenKind.Type => module.ResolveType(token),
                TokenKind.Field => module.ResolveField(token),
                TokenKind.Method => module.ResolveMethod(token),
                TokenKind.Property or TokenKind.Event => PropertiesAndEvents(module).TryGetValue(token, out var found) ? found : null,
                _ => throw DataErrors.InvalidParams(param + ".token", $"Token 0x{anchor.Token:x8} isn't a type, field, method, property or event definition."),
            };
        }
        catch (ArgumentException)
        {
            member = null;
        }

        return member ?? throw DataErrors.IndexStale(anchor, $"Token 0x{anchor.Token:x8} doesn't resolve in {module.Assembly.GetName().Name} ({anchor.Mvid}).",
            new[] { (module.Assembly.GetName().Name ?? "?", anchor.Mvid) });
    }

    private Dictionary<int, MemberInfo> PropertiesAndEvents(Module module) => _propertiesAndEvents.GetOrAdd(module, m =>
    {
        var index = new Dictionary<int, MemberInfo>();
        foreach (var type in LoadableTypes(m))
        {
            foreach (var property in type.GetProperties(Declared))
            {
                index[property.MetadataToken] = property;
            }

            foreach (var ev in type.GetEvents(Declared))
            {
                index[ev.MetadataToken] = ev;
            }
        }

        return index;
    });

    private Type Construct(Type definition, List<JsonValue> typeArgs, string param)
    {
        if (!definition.IsGenericTypeDefinition || definition.GetGenericArguments().Length != typeArgs.Count)
        {
            throw DataErrors.InvalidParams(param, $"{definition.FullName} doesn't take {typeArgs.Count} generic argument(s).");
        }

        try
        {
            return definition.MakeGenericType(typeArgs.Select((a, i) => ResolveTypeRef(a, $"{param}[{i}]")).ToArray());
        }
        catch (ArgumentException e)
        {
            throw DataErrors.InvalidParams(param, $"The generic arguments don't fit {definition.FullName}: {e.Message}");
        }
    }

    // Informative only (never used to bind): the loaded builds of the assembly that seems to define the anchor's type.
    private IEnumerable<(string, string)> LoadedBuildsOf(Anchor anchor)
    {
        var name = anchor.Name;
        if (string.IsNullOrEmpty(name))
        {
            return Array.Empty<(string, string)>();
        }

        var typeName = name!.Split(new[] { "::" }, StringSplitOptions.None)[0].Replace('/', '+');
        return _modules.Modules
            .Where(m => SafeGetType(m, typeName) is not null)
            .Select(m => (m.Assembly.GetName().Name ?? "?", m.ModuleVersionId.ToString()))
            .ToList();
    }

    private static Type? SafeGetType(Module module, string name)
    {
        try
        {
            return module.GetType(name, throwOnError: false, ignoreCase: false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static IEnumerable<Type> LoadableTypes(Module module)
    {
        try
        {
            return module.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.Where(t => t is not null)!;
        }
        catch (Exception)
        {
            return Array.Empty<Type>();
        }
    }

    private static string Describe(Anchor anchor) => anchor.Name ?? $"{anchor.Mvid}/0x{anchor.Token:x8}";
}

/// <summary>
/// Turns live types and members into anchors (<c>(mvid, token)</c> of the definition, plus generic arguments), with a
/// readable name in dnlib's style (nested types with <c>/</c>, members as <c>Type::member</c>) so that static and runtime
/// data read alike.
/// </summary>
public static class AnchorWriter
{
    /// <summary>Whether <paramref name="type"/> can be anchored (arrays, pointers, by-refs and generic parameters can't).</summary>
    public static bool CanAnchor(Type type) => !type.IsArray && !type.IsPointer && !type.IsByRef && !type.IsGenericParameter;

    /// <summary>A type's anchor (constructed generic types carry their arguments).</summary>
    public static Anchor ForType(Type type)
    {
        if (!CanAnchor(type))
        {
            throw new ArgumentException($"{type} has no definition to anchor.", nameof(type));
        }

        var definition = type.IsGenericType && !type.IsGenericTypeDefinition ? type.GetGenericTypeDefinition() : type;
        var anchor = new Anchor { Mvid = definition.Module.ModuleVersionId.ToString(), Token = (uint)definition.MetadataToken, Name = TypeName(type) };
        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            anchor.TypeArgs = type.GetGenericArguments().Select(TypeRef).ToList();
        }

        return anchor;
    }

    /// <summary>A member's anchor: a type, field, method, constructor, property or event.</summary>
    public static Anchor ForMember(MemberInfo member)
    {
        if (member is Type type)
        {
            return ForType(type);
        }

        var declaring = member.DeclaringType;
        var definition = member;
        if (member is MethodInfo { IsGenericMethod: true, IsGenericMethodDefinition: false } method)
        {
            definition = method.GetGenericMethodDefinition();
        }

        var anchor = new Anchor { Mvid = member.Module.ModuleVersionId.ToString(), Token = (uint)definition.MetadataToken, Name = MemberName(member) };
        if (declaring is { IsGenericType: true, IsGenericTypeDefinition: false })
        {
            anchor.TypeArgs = declaring.GetGenericArguments().Select(TypeRef).ToList();
        }

        if (member is MethodInfo { IsGenericMethod: true, IsGenericMethodDefinition: false } constructed)
        {
            anchor.MethodArgs = constructed.GetGenericArguments().Select(TypeRef).ToList();
        }

        return anchor;
    }

    /// <summary>A type reference: the full name for BCL types (<c>System.Int32</c>) and anything that can't be anchored,
    /// an anchor otherwise.</summary>
    public static JsonValue TypeRef(Type type)
    {
        if (type.Assembly == typeof(object).Assembly && !type.IsGenericParameter && (!type.IsGenericType || type.GetGenericArguments().All(a => a.Assembly == typeof(object).Assembly)))
        {
            return JsonValue.From(type.FullName ?? type.Name);
        }

        return CanAnchor(type) ? ForType(type).ToJson() : JsonValue.From(type.AssemblyQualifiedName ?? type.FullName ?? type.Name);
    }

    /// <summary>A type's readable name: <c>Ns.Outer/Inner</c>, generic arguments in angle brackets.</summary>
    public static string TypeName(Type type)
    {
        if (type.IsGenericParameter)
        {
            return type.Name;
        }

        if (type.IsArray)
        {
            return TypeName(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        }

        var definition = type.IsGenericType && !type.IsGenericTypeDefinition ? type.GetGenericTypeDefinition() : type;
        var name = (definition.FullName ?? definition.Name).Replace('+', '/');
        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            name += "<" + string.Join(",", type.GetGenericArguments().Select(TypeName)) + ">";
        }

        return name;
    }

    /// <summary>A member's readable name: <c>Ns.Type::Method(Ns.Arg,System.Int32)</c>, <c>Ns.Type::field</c>.</summary>
    public static string MemberName(MemberInfo member)
    {
        if (member is Type type)
        {
            return TypeName(type);
        }

        var prefix = member.DeclaringType is null ? string.Empty : TypeName(member.DeclaringType) + "::";
        if (member is MethodBase method)
        {
            var parameters = string.Join(",", method.GetParameters().Select(p => TypeName(p.ParameterType)));
            var generic = method is MethodInfo { IsGenericMethod: true } m ? "<" + string.Join(",", m.GetGenericArguments().Select(TypeName)) + ">" : string.Empty;
            return $"{prefix}{method.Name}{generic}({parameters})";
        }

        return prefix + member.Name;
    }

    /// <summary><c>code://&lt;assembly&gt;@&lt;mvid&gt;/&lt;token&gt;</c> for a type or member.</summary>
    public static string CodeLocator(MemberInfo member)
    {
        var definition = member is Type { IsGenericType: true, IsGenericTypeDefinition: false } t ? t.GetGenericTypeDefinition() : member;
        return string.Format(CultureInfo.InvariantCulture, "code://{0}@{1}/{2}", member.Module.Assembly.GetName().Name, member.Module.ModuleVersionId, (uint)definition.MetadataToken);
    }

    internal static string Signature(MethodBase method)
    {
        var text = new StringBuilder(method.Name).Append('(');
        text.Append(string.Join(",", method.GetParameters().Select(p => TypeName(p.ParameterType))));
        return text.Append(')').ToString();
    }
}
