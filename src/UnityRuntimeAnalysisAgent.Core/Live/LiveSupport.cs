using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Data;
using UnityRuntimeAnalysisAgent.Core.Dispatch;

namespace UnityRuntimeAnalysisAgent.Core.Live;

/// <summary>
/// The static-constructor policy: reading a static field can run the type's static constructor, which is game code. Types
/// without one are read freely; types with one only once the agent has seen them initialized (an instance exists, a read
/// already happened, or — later — a hook saw one of its methods run), or when the caller allows it (Full mode).
/// </summary>
public sealed class StaticInitRegistry
{
    private readonly ConcurrentDictionary<Type, bool> _observed = new();

    /// <summary>Records that a type (and its bases) has been initialized.</summary>
    public void Observe(Type type)
    {
        for (var t = type; t is not null && _observed.TryAdd(t, true); t = t.BaseType)
        {
        }
    }

    /// <summary>Whether reading the type's statics can't run a static constructor the game hasn't run yet.</summary>
    public bool CanRead(Type type) => SafeInitializer(type) is null || _observed.ContainsKey(type);

    private static ConstructorInfo? SafeInitializer(Type type)
    {
        try
        {
            return type.TypeInitializer;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>Query and filter conditions (<c>where</c>): a path from the candidate, an operator, a value.</summary>
public sealed class ConditionEvaluator
{
    private readonly DataModel _data;

    /// <summary>Creates the evaluator.</summary>
    public ConditionEvaluator(DataModel data) => _data = data;

    /// <summary>Whether <paramref name="candidate"/> meets every condition (an exception means it doesn't; the caller
    /// counts it as an error).</summary>
    public bool Matches(object candidate, IReadOnlyList<Condition>? conditions, string param)
    {
        if (conditions is null)
        {
            return true;
        }

        for (var i = 0; i < conditions.Count; i++)
        {
            if (!Matches(candidate, conditions[i], $"{param}[{i}]"))
            {
                return false;
            }
        }

        return true;
    }

    private bool Matches(object candidate, Condition condition, string param)
    {
        var start = new Resolved { Value = candidate, Type = candidate.GetType(), Place = new Place() };
        var value = _data.Targets.Walk(start, condition.Path, param + ".path").Value;
        if (value is not null && UnityTypes.IsUnityObject(value.GetType()) && _data.Unity.IsDestroyed(value))
        {
            value = null; // Unity's == null
        }

        return Test(value, condition.Op, condition.Value, param);
    }

    /// <summary>Whether a live value meets one operator (<c>eq</c>, <c>lt</c>, <c>regex</c>, …) against a JSON value.</summary>
    public bool Test(object? value, string op, JsonValue? json, string param)
    {
        var condition = new Condition { Op = op, Value = json };
        switch (condition.Op)
        {
            case "isNull":
                return value is null;
            case "notNull":
                return value is not null;
            case "typeIs":
                return value is not null && _data.Anchors.ResolveTypeRef(condition.Value, param + ".value").IsInstanceOfType(value);
            case "regex":
                return value is not null && new Regex(((JsonString)condition.Value!).Value, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).IsMatch(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
            case "contains":
                return Contains(value, condition.Value, param);
            case "in":
                return condition.Value is JsonArray options && options.Any(o => Equal(value, o, param));
            case "eq":
                return Equal(value, condition.Value, param);
            case "ne":
                return !Equal(value, condition.Value, param);
            case "lt" or "le" or "gt" or "ge":
                if (value is null)
                {
                    return false;
                }

                var order = Compare(value, condition.Value, param);
                return condition.Op switch { "lt" => order < 0, "le" => order <= 0, "gt" => order > 0, _ => order >= 0 };
            default:
                throw ProtocolException.InvalidParams(param + ".op", $"'{condition.Op}' isn't an operator.");
        }
    }

    /// <summary>Equality of a live value and a JSON value (converted to the live value's type).</summary>
    public bool Equal(object? value, JsonValue? json, string param)
    {
        if (value is null || json is null or JsonNull)
        {
            return value is null && json is null or JsonNull;
        }

        if (json is JsonObject { } reference && (reference.ContainsKey("h") || reference.ContainsKey("var")))
        {
            return ReferenceEquals(value, _data.Reader.Read(json, typeof(object), param + ".value"));
        }

        var other = _data.Reader.Read(json, value.GetType(), param + ".value");
        return value is string text ? string.Equals(text, other as string, StringComparison.Ordinal) : Equals(value, other);
    }

    private int Compare(object value, JsonValue? json, string param)
    {
        var other = _data.Reader.Read(json, value.GetType(), param + ".value");
        return value is IComparable comparable ? comparable.CompareTo(other) : throw ProtocolException.InvalidParams(param, $"{AnchorWriter.TypeName(value.GetType())} can't be ordered.");
    }

    private bool Contains(object? value, JsonValue? json, string param) => value switch
    {
        null => false,
        string text => text.IndexOf(((JsonString)json!).Value, StringComparison.Ordinal) >= 0,
        IEnumerable items => items.Cast<object?>().Any(item => Equal(item, json, param)),
        _ => false,
    };
}

/// <summary>Writes through member paths: the last step's member, element or entry, then (for struct containers) back up
/// the chain until a reference type or the root takes the change.</summary>
public static class PathWriter
{
    /// <summary>Sets the value at the end of <paramref name="chain"/> (from <c>TargetResolver.WalkChain</c>).</summary>
    public static void Set(DataModel data, List<Resolved> chain, IReadOnlyList<MemberPathStep> path, JsonValue? json, string param)
    {
        if (path.Count == 0)
        {
            throw ProtocolException.InvalidParams(param, $"{param} needs at least one step: the member, element or entry to set.");
        }

        var last = chain[chain.Count - 1];
        var containerAt = chain.Count - 2;
        var step = last.Place.Path.Count > 0 ? last.Place.Path[last.Place.Path.Count - 1] : path[path.Count - 1];
        var decoded = Decode(data, chain[containerAt], last, step, json, param);
        var updated = SetOne(chain[containerAt], last, step, decoded, param);

        // Struct containers are copies: put each one back into its own container.
        for (var i = containerAt; i > 0 && chain[i].Value is { } box && box.GetType().IsValueType; i--)
        {
            var parentStep = chain[i].Place.Path[chain[i].Place.Path.Count - 1];
            updated = SetOne(chain[i - 1], chain[i], parentStep, updated, param);
        }
    }

    private static object? Decode(DataModel data, Resolved container, Resolved last, MemberPathStep step, JsonValue? json, string param)
    {
        Type target = last.Member switch
        {
            FieldInfo f => f.FieldType,
            PropertyInfo p => p.PropertyType,
            _ => step.Key is not null ? Collections.DictionaryTypes(container.Type).Value : Collections.ElementType(container.Type) ?? typeof(object),
        };
        return data.Reader.Read(json, target, param + ".value");
    }

    // Sets one step on its container and returns the container (the boxed copy, for struct write-back).
    private static object? SetOne(Resolved container, Resolved child, MemberPathStep step, object? value, string param)
    {
        var instance = container.IsStatic ? null : container.Value;
        try
        {
            switch (child.Member)
            {
                case FieldInfo field when step.Member is not null || step.Name is not null:
                    if (field.IsLiteral || field.IsInitOnly)
                    {
                        throw ProtocolException.InvalidParams(param, $"{AnchorWriter.MemberName(field)} is read-only.");
                    }

                    field.SetValue(instance, value);
                    return instance;
                case PropertyInfo property when step.Member is not null || step.Name is not null:
                    var setter = property.GetSetMethod(true) ?? throw ProtocolException.InvalidParams(param, $"{AnchorWriter.MemberName(property)} has no setter.");
                    setter.Invoke(instance, new[] { value });
                    return instance;
            }

            switch (instance)
            {
                case Array array when step.Index is { } i:
                    array.SetValue(value, i);
                    return array;
                case IList list when step.Index is { } i:
                    list[(int)i] = value;
                    return list;
                case IDictionary dictionary when step.Key is not null:
                    var key = child.Place.Path.Count > 0 ? KeyOf(dictionary, step) : null;
                    dictionary[key ?? throw ProtocolException.InvalidParams(param, "The key isn't in the dictionary.")] = value;
                    return dictionary;
            }
        }
        catch (TargetInvocationException e)
        {
            throw AgentErrors.Game(e);
        }

        throw ProtocolException.InvalidParams(param, $"{param}: that step can't be written.");
    }

    // The live key the step was resolved with (the resolver checked it exists).
    private static object? KeyOf(IDictionary dictionary, MemberPathStep step)
    {
        foreach (var key in dictionary.Keys)
        {
            var text = key is string s ? JsonValue.From(s) : key is Enum e ? JsonValue.From(e.ToString()) : (JsonValue)new JsonNumber(Convert.ToDecimal(key, CultureInfo.InvariantCulture));
            if (JsonValue.DeepEquals(text, step.Key) || (step.Key is JsonNumber n && key is IConvertible && n.RawText == Convert.ToString(key, CultureInfo.InvariantCulture)))
            {
                return key;
            }
        }

        return null;
    }
}

/// <summary>Adding to, removing from and replacing in lists, sets and dictionaries.</summary>
public static class CollectionEditor
{
    /// <summary>Adds a value (or a key/value entry); returns the new count.</summary>
    public static int Add(DataModel data, object collection, JsonValue? value, JsonValue? key, string param)
    {
        switch (collection)
        {
            case Array:
                throw ProtocolException.InvalidParams(param, "Arrays have a fixed size: use coll.set.");
            case IDictionary dictionary:
                var (keyType, valueType) = Collections.DictionaryTypes(collection.GetType());
                if (key is null or JsonNull)
                {
                    throw ProtocolException.InvalidParams("params.key", "Adding to a dictionary needs params.key.");
                }

                dictionary.Add(data.Reader.Read(key, keyType, "params.key")!, data.Reader.Read(value, valueType, "params.value"));
                return dictionary.Count;
            case IList list:
                list.Add(data.Reader.Read(value, Collections.ElementType(collection.GetType()) ?? typeof(object), "params.value"));
                return list.Count;
        }

        var element = Collections.ElementType(collection.GetType()) ?? typeof(object);
        var add = collection.GetType().GetMethod("Add", new[] { element }) ?? throw ProtocolException.InvalidParams(param, $"{AnchorWriter.TypeName(collection.GetType())} has no Add.");
        Invoke(add, collection, data.Reader.Read(value, element, "params.value"));
        return Collections.Count(collection) ?? 0;
    }

    /// <summary>Removes by index, key or value; returns whether something was removed.</summary>
    public static bool Remove(DataModel data, object collection, long? index, JsonValue? key, JsonValue? value, string param)
    {
        if (collection is Array)
        {
            throw ProtocolException.InvalidParams(param, "Arrays have a fixed size: use coll.set.");
        }

        if (index is { } i)
        {
            if (collection is not IList list)
            {
                throw ProtocolException.InvalidParams("params.index", "Only lists are removed from by index.");
            }

            if (i >= list.Count)
            {
                return false;
            }

            list.RemoveAt((int)i);
            return true;
        }

        if (key is not null && collection is IDictionary dictionary)
        {
            var live = data.Reader.Read(key, Collections.DictionaryTypes(collection.GetType()).Key, "params.key")!;
            var had = dictionary.Contains(live);
            dictionary.Remove(live);
            return had;
        }

        var element = Collections.ElementType(collection.GetType()) ?? typeof(object);
        var remove = collection.GetType().GetMethod("Remove", new[] { element }) ?? throw ProtocolException.InvalidParams(param, $"{AnchorWriter.TypeName(collection.GetType())} has no Remove.");
        return Invoke(remove, collection, data.Reader.Read(value, element, "params.value")) is true;
    }

    private static object? Invoke(MethodInfo method, object target, object? argument)
    {
        try
        {
            return method.Invoke(target, new[] { argument });
        }
        catch (TargetInvocationException e)
        {
            throw AgentErrors.Game(e);
        }
    }
}

/// <summary>C# events: the delegate behind an event (its backing field), its listeners, and raising it.</summary>
public static class EventAccess
{
    /// <summary>The event's backing delegate (field-like events), or null when there is none or nobody listens.</summary>
    public static Delegate? Backing(EventInfo ev, object? instance)
    {
        for (var t = instance?.GetType() ?? ev.DeclaringType; t is not null; t = t.BaseType)
        {
            var field = t.GetField(ev.Name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field is not null && typeof(Delegate).IsAssignableFrom(field.FieldType))
            {
                return field.GetValue(field.IsStatic ? null : instance) as Delegate;
            }
        }

        throw ProtocolException.InvalidParams("params.event", $"{ev.Name} has no backing delegate field (a custom add/remove event): its listeners can't be read.");
    }
}
