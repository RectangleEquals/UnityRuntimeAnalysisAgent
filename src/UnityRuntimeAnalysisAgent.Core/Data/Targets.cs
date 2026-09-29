using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Abstractions;
using UnityRuntimeAnalysisAgent.Core.Dispatch;

namespace UnityRuntimeAnalysisAgent.Core.Data;

/// <summary>The session's data model: anchors, handles, variables, refs, cursors, and the codec around them.</summary>
public sealed class DataModel : IDisposable
{
    /// <summary>Creates the data model.</summary>
    public DataModel(IUnityApi unity, int maxHandles, int maxRefs = 50_000, Func<DateTime>? now = null)
    {
        Unity = unity;
        Modules = new ModuleMap();
        Anchors = new AnchorResolver(Modules);
        StaticInit = new Live.StaticInitRegistry();
        Handles = new HandleTable(unity, maxHandles, StaticInit.Observe);
        Variables = new VariableStore(Handles);
        Expansions = new ExpansionRegistry(maxRefs);
        Cursors = new CursorStore(now);
        Targets = new TargetResolver(this);
        Reader = new ValueReader(this);
    }

    /// <summary>Unity.</summary>
    public IUnityApi Unity { get; }

    /// <summary>Loaded modules by MVID.</summary>
    public ModuleMap Modules { get; }

    /// <summary>Anchor → type/member.</summary>
    public AnchorResolver Anchors { get; }

    /// <summary>Live object handles.</summary>
    public HandleTable Handles { get; }

    /// <summary>Which types' static constructors are known to have run.</summary>
    public Live.StaticInitRegistry StaticInit { get; }

    /// <summary>Named references.</summary>
    public VariableStore Variables { get; }

    /// <summary>Redaction refs.</summary>
    public ExpansionRegistry Expansions { get; }

    /// <summary>Paging cursors.</summary>
    public CursorStore Cursors { get; }

    /// <summary>Targets and member paths.</summary>
    public TargetResolver Targets { get; }

    /// <summary>JSON → live values.</summary>
    public ValueReader Reader { get; }

    /// <summary>A value writer for one encoding (live → JSON).</summary>
    public ValueWriter Writer(ViewOptions view, long frame) => new(this, view, frame);

    /// <summary>Releases every handle and stops following assembly loads.</summary>
    public void Dispose()
    {
        Variables.DeleteHandleVariables();
        Handles.ReleaseAll(includePinned: true);
        Modules.Dispose();
    }
}

/// <summary>
/// Where a value is: the root target, the member path from it (with exploratory names already turned into anchors), and
/// its durable locator when it has one. <see cref="Expressible"/> is false when the path can't be written down (e.g. a
/// dictionary entry under a key that isn't a string or number).
/// </summary>
public sealed class Place
{
    /// <summary>The root target (null for a retained value).</summary>
    public Target? Root { get; set; }

    /// <summary>The member path from the root.</summary>
    public List<MemberPathStep> Path { get; set; } = new();

    /// <summary>Whether <see cref="Path"/> leads back to the value.</summary>
    public bool Expressible { get; set; } = true;

    /// <summary>The locator up to the owner (<c>live://Main/Player#Ns.Inventory</c>), or null.</summary>
    public string? LocatorBase { get; set; }

    /// <summary>The locator's member path so far (<c>.items[3]</c>).</summary>
    public string LocatorSuffix { get; set; } = string.Empty;

    /// <summary>The full locator, or null.</summary>
    public string? Locator => LocatorBase is null ? null : LocatorBase + LocatorSuffix;

    /// <summary>A child place, one step further.</summary>
    public Place Then(MemberPathStep? step, string locatorSegment) => new()
    {
        Root = Root,
        Path = step is null ? Path : new List<MemberPathStep>(Path) { step },
        Expressible = Expressible && step is not null,
        LocatorBase = LocatorBase,
        LocatorSuffix = LocatorSuffix + locatorSegment,
    };

    /// <summary>A place rooted at an object's handle (for values whose path can't be written down).</summary>
    public static Place AtHandle(long h, string? locator) => new() { Root = new Target { H = h }, LocatorBase = locator };
}

/// <summary>The outcome of resolving a target and walking a member path.</summary>
public sealed class Resolved
{
    /// <summary>The value (null for the statics of a type, or a null member).</summary>
    public object? Value { get; set; }

    /// <summary>The type to read members against: the runtime type of <see cref="Value"/>, or the declared type.</summary>
    public Type Type { get; set; } = typeof(object);

    /// <summary>Whether this stands for a type's static members (a <c>static</c> target with no steps yet).</summary>
    public bool IsStatic { get; set; }

    /// <summary>Where it is.</summary>
    public Place Place { get; set; } = new();

    /// <summary>The last member read (for names and anchors).</summary>
    public MemberInfo? Member { get; set; }

    /// <summary>Notes (exploratory name resolution).</summary>
    public List<Warning> Warnings { get; } = new();
}

/// <summary>
/// Resolves targets (<c>{h}</c>, <c>{var}</c>, <c>{static}</c>, <c>{gameObject}</c>) and walks member paths (members by
/// anchor, list indexes, dictionary keys, components, children, and exploratory names). Main thread: reading members
/// can run game code (property getters, static constructors), and scene lookups call into Unity.
/// </summary>
public sealed class TargetResolver
{
    private const BindingFlags AllInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags AllStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly DataModel _data;

    /// <summary>Creates the resolver.</summary>
    public TargetResolver(DataModel data) => _data = data;

    /// <summary>Resolves a target (not yet walked).</summary>
    public Resolved Resolve(Target target, string param)
    {
        if (target.H is { } h)
        {
            var value = _data.Handles.Resolve(h);
            return Rooted(value, new Target { H = h });
        }

        if (target.Var is { } name)
        {
            var variable = _data.Variables.Get(name, param + ".var");
            switch (variable.Kind)
            {
                case VariableKind.Handle:
                    var resolved = Rooted(_data.Handles.Resolve(variable.H), new Target { Var = name });
                    return resolved;
                case VariableKind.Static:
                    return Static(variable.Static!, new Target { Var = name }, param + ".var");
                default:
                    throw DataErrors.InvalidParams(param + ".var", $"The variable '{name}' holds a JSON value, not an object or a type.");
            }
        }

        if (target.Static is { } anchor)
        {
            return Static(anchor, new Target { Static = anchor }, param + ".static");
        }

        if (target.GameObject is { } go)
        {
            var found = _data.Unity.FindGameObject(go.Path, go.Scene)
                ?? throw DataErrors.NotFound(param + ".gameObject", $"No GameObject '{go.Path}'{(go.Scene is null ? string.Empty : $" in scene '{go.Scene}'")}.");
            return Rooted(found, new Target { GameObject = go });
        }

        throw DataErrors.InvalidParams(param, $"{param} needs one of h, var, static or gameObject.");
    }

    /// <summary>Resolves a target and walks a member path from it.</summary>
    public Resolved Resolve(Target target, IReadOnlyList<MemberPathStep>? path, string param, string pathParam) =>
        Walk(Resolve(target, param), path, pathParam);

    /// <summary>Walks a member path from a resolved start.</summary>
    public Resolved Walk(Resolved start, IReadOnlyList<MemberPathStep>? path, string param)
    {
        var current = start;
        if (path is null)
        {
            return current;
        }

        for (var i = 0; i < path.Count; i++)
        {
            current = Step(current, path[i], $"{param}[{i}]");
        }

        return current;
    }

    /// <summary>Walks a member path and returns every stop: the start, then one per step (for writes that must put a
    /// changed struct back into its container).</summary>
    public List<Resolved> WalkChain(Resolved start, IReadOnlyList<MemberPathStep> path, string param)
    {
        var chain = new List<Resolved> { start };
        for (var i = 0; i < path.Count; i++)
        {
            chain.Add(Step(chain[i], path[i], $"{param}[{i}]"));
        }

        return chain;
    }

    /// <summary>The locator base for a live object: scene objects and loaded assets have one, other objects don't.</summary>
    public string? LocatorBaseOf(object value)
    {
        var type = value.GetType();
        if (!UnityTypes.IsUnityObject(type))
        {
            return null;
        }

        if (_data.Unity.Locate(value) is { } address)
        {
            return Locators.Scene(address, type, string.Empty);
        }

        return _data.Unity.Describe(value) is { } facts ? Locators.Asset(type, facts.Name, facts.InstanceId, string.Empty) : null;
    }

    /// <summary>The name a field is shown under: an auto-property's backing field shows as the property.</summary>
    public static string DisplayName(MemberInfo member)
    {
        var name = member.Name;
        if (member is FieldInfo && name.StartsWith("<", StringComparison.Ordinal))
        {
            var end = name.IndexOf(">k__BackingField", StringComparison.Ordinal);
            if (end > 1)
            {
                return name.Substring(1, end - 1);
            }
        }

        return name;
    }

    /// <summary>Reads a field or property (game exceptions become <c>GAME_EXCEPTION</c>).</summary>
    public static object? Read(MemberInfo member, object? instance)
    {
        try
        {
            return member switch
            {
                FieldInfo field => field.GetValue(instance),
                PropertyInfo property => property.GetValue(instance, null),
                _ => throw new ArgumentException($"{member.Name} isn't a field or property."),
            };
        }
        catch (TargetInvocationException e)
        {
            throw AgentErrors.Game(e);
        }
        catch (TypeInitializationException e)
        {
            throw AgentErrors.Game(e);
        }
    }

    private Resolved Rooted(object value, Target root) => new()
    {
        Value = value,
        Type = value.GetType(),
        Place = new Place { Root = root, LocatorBase = LocatorBaseOf(value) },
    };

    private Resolved Static(Anchor anchor, Target root, string param)
    {
        var type = _data.Anchors.ResolveType(anchor, param);
        return new Resolved { Type = type, IsStatic = true, Place = new Place { Root = root, LocatorBase = Locators.Static(type, string.Empty) } };
    }

    private Resolved Step(Resolved current, MemberPathStep step, string param)
    {
        if (current.Value is null && !current.IsStatic)
        {
            throw DataErrors.NotFound(param, $"{param}: the value before this step is null.");
        }

        if (step.Member is { } anchor)
        {
            var member = _data.Anchors.ResolveMember(anchor, param + ".member");
            return ReadMember(current, Bind(current, member, param), step, param);
        }

        if (step.Name is { } name)
        {
            var member = FindByName(current, name, param);
            var next = ReadMember(current, member, new MemberPathStep { Member = AnchorWriter.ForMember(member) }, param);
            next.Warnings.Add(new Warning { Code = "EXPLORATORY_NAME", Message = $"{param}: '{name}' was resolved by name to {AnchorWriter.MemberName(member)}." });
            return next;
        }

        if (step.Index is { } index)
        {
            return Element(current, index, step, param);
        }

        if (step.Key is { } key)
        {
            return Entry(current, key, step, param);
        }

        if (step.Component is { } componentAnchor)
        {
            var type = _data.Anchors.ResolveType(componentAnchor, param + ".component");
            RequireSceneObject(current, param);
            var component = _data.Unity.GetComponent(current.Value!, type)
                ?? throw DataErrors.NotFound(param, $"{param}: the GameObject has no {AnchorWriter.TypeName(type)}.");
            var place = current.Place.Then(step, string.Empty);
            if (_data.Unity.Locate(component) is { } address)
            {
                place.LocatorBase = Locators.Scene(address, component.GetType(), string.Empty);
                place.LocatorSuffix = string.Empty;
            }

            return Carry(current, new Resolved { Value = component, Type = component.GetType(), Place = place });
        }

        if (step.Child is { } childPath)
        {
            RequireSceneObject(current, param);
            var child = _data.Unity.FindChild(current.Value!, childPath)
                ?? throw DataErrors.NotFound(param, $"{param}: no child '{childPath}'.");
            var place = current.Place.Then(step, string.Empty);
            place.LocatorBase = LocatorBaseOf(child);
            place.LocatorSuffix = string.Empty;
            return Carry(current, new Resolved { Value = child, Type = child.GetType(), Place = place });
        }

        throw DataErrors.InvalidParams(param, $"{param} needs one of member, name, index, key, component or child.");
    }

    private MemberInfo Bind(Resolved current, MemberInfo member, string param)
    {
        if (member is not (FieldInfo or PropertyInfo))
        {
            throw DataErrors.InvalidParams(param + ".member", $"{AnchorWriter.MemberName(member)} isn't a field or property.");
        }

        if (member is PropertyInfo property && !SafeReflection.IsPlainReadable(property))
        {
            throw DataErrors.InvalidParams(param + ".member", $"{AnchorWriter.MemberName(member)} can't be read as a value (no getter, or indexed).");
        }

        var bound = AnchorResolver.BindTo(member, current.Type)
            ?? throw DataErrors.InvalidParams(param + ".member", $"{AnchorWriter.TypeName(current.Type)} has no member {AnchorWriter.MemberName(member)}.");
        if (IsStatic(bound) != current.IsStatic)
        {
            throw DataErrors.InvalidParams(param + ".member", current.IsStatic
                ? $"{AnchorWriter.MemberName(member)} is an instance member, but the target is a type's statics."
                : $"{AnchorWriter.MemberName(member)} is static: use a static target.");
        }

        return bound;
    }

    private Resolved ReadMember(Resolved current, MemberInfo member, MemberPathStep step, string param)
    {
        var value = Read(member, current.IsStatic ? null : current.Value);
        var declared = member is FieldInfo f ? f.FieldType : ((PropertyInfo)member).PropertyType;
        return Carry(current, new Resolved
        {
            Value = value,
            Type = value?.GetType() ?? declared,
            Member = member,
            Place = current.Place.Then(step, Locators.Member(DisplayName(member))),
        });
    }

    private MemberInfo FindByName(Resolved current, string name, string param)
    {
        var flags = current.IsStatic ? AllStatic : AllInstance;
        var found = new List<MemberInfo>();
        for (var t = current.Type; t is not null; t = t.BaseType)
        {
            foreach (var member in t.GetMembers(flags | BindingFlags.DeclaredOnly))
            {
                var readable = member is FieldInfo || member is PropertyInfo p && SafeReflection.IsPlainReadable(p);
                if (readable && (member.Name == name || DisplayName(member) == name))
                {
                    found.Add(member);
                }
            }
        }

        // An auto-property and its backing field are the same value: keep the property.
        found = found.Where(m => !(m is FieldInfo && found.Any(p => p is PropertyInfo && p.Name == DisplayName(m) && p.DeclaringType == m.DeclaringType))).ToList();
        if (found.Count == 0)
        {
            throw DataErrors.NotFound(param + ".name", $"{AnchorWriter.TypeName(current.Type)} has no {(current.IsStatic ? "static" : "instance")} field or property '{name}'.");
        }

        if (found.Count > 1)
        {
            throw DataErrors.Ambiguous($"{param}: '{name}' matches {found.Count} members of {AnchorWriter.TypeName(current.Type)}.", found.Select(AnchorWriter.ForMember));
        }

        return found[0];
    }

    private Resolved Element(Resolved current, long index, MemberPathStep step, string param)
    {
        object? element;
        switch (current.Value)
        {
            case Array array when array.Rank == 1:
                element = index < array.Length ? array.GetValue(index) : throw OutOfRange(param, index, array.Length);
                break;
            case IList list:
                element = index < list.Count ? list[(int)index] : throw OutOfRange(param, index, list.Count);
                break;
            case IEnumerable sequence when Collections.IsCollection(current.Value.GetType()):
                var items = sequence.Cast<object?>().ToList();
                element = index < items.Count ? items[(int)index] : throw OutOfRange(param, index, items.Count);
                break;
            default:
                throw DataErrors.InvalidParams(param + ".index", $"{param}: {AnchorWriter.TypeName(current.Type)} isn't a list.");
        }

        return Carry(current, new Resolved
        {
            Value = element,
            Type = element?.GetType() ?? Collections.ElementType(current.Type) ?? typeof(object),
            Place = current.Place.Then(step, Locators.Index(index)),
        });
    }

    private Resolved Entry(Resolved current, JsonValue key, MemberPathStep step, string param)
    {
        if (current.Value is not IDictionary dictionary)
        {
            throw DataErrors.InvalidParams(param + ".key", $"{param}: {AnchorWriter.TypeName(current.Type)} isn't a dictionary.");
        }

        var (keyType, valueType) = Collections.DictionaryTypes(current.Type);
        var live = _data.Reader.Read(key, keyType, param + ".key");
        if (live is null || !dictionary.Contains(live))
        {
            throw DataErrors.NotFound(param + ".key", $"{param}: no entry {key}.");
        }

        var value = dictionary[live];
        return Carry(current, new Resolved
        {
            Value = value,
            Type = value?.GetType() ?? valueType,
            Place = current.Place.Then(step, Locators.Key(key)),
        });
    }

    private static void RequireSceneObject(Resolved current, string param)
    {
        if (current.Value is null || !UnityTypes.IsSceneObject(current.Value.GetType()))
        {
            throw DataErrors.InvalidParams(param, $"{param}: {AnchorWriter.TypeName(current.Type)} isn't a GameObject or a component.");
        }
    }

    private static Resolved Carry(Resolved from, Resolved to)
    {
        to.Warnings.AddRange(from.Warnings);
        return to;
    }

    private static bool IsStatic(MemberInfo member) => member switch
    {
        FieldInfo f => f.IsStatic,
        PropertyInfo p => (p.GetGetMethod(true) ?? p.GetSetMethod(true))?.IsStatic ?? false,
        _ => false,
    };

    private static Exception OutOfRange(string param, long index, int count) =>
        DataErrors.NotFound(param + ".index", $"{param}: index {index} is outside the {count} items.");
}

/// <summary>Collection shape helpers.</summary>
internal static class Collections
{
    /// <summary>Whether enumerating the type is safe (a materialised collection, not a lazy sequence).</summary>
    public static bool IsCollection(Type type) =>
        typeof(ICollection).IsAssignableFrom(type) || GenericInterface(type, typeof(ICollection<>)) is not null || GenericInterface(type, typeof(IReadOnlyCollection<>)) is not null;

    /// <summary>The element type of a list or collection, when known.</summary>
    public static Type? ElementType(Type type) =>
        type.IsArray ? type.GetElementType() : GenericInterface(type, typeof(IEnumerable<>))?.GetGenericArguments()[0];

    /// <summary>The key and value types of a dictionary (object when not generic).</summary>
    public static (Type Key, Type Value) DictionaryTypes(Type type)
    {
        var generic = GenericInterface(type, typeof(IDictionary<,>));
        return generic is null ? (typeof(object), typeof(object)) : (generic.GetGenericArguments()[0], generic.GetGenericArguments()[1]);
    }

    /// <summary>The count of a collection without enumerating it, when it has one.</summary>
    public static int? Count(object value)
    {
        if (value is ICollection collection)
        {
            return collection.Count;
        }

        var generic = GenericInterface(value.GetType(), typeof(ICollection<>)) ?? GenericInterface(value.GetType(), typeof(IReadOnlyCollection<>));
        return generic?.GetProperty("Count")?.GetValue(value, null) as int?;
    }

    public static Type? GenericInterface(Type type, Type definition)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == definition)
        {
            return type;
        }

        return type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == definition);
    }
}
