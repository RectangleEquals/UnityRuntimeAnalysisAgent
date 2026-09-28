using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Dispatch;

namespace UnityRuntimeAnalysisAgent.Core.Data;

/// <summary>
/// JSON → live, for writes, invocations, query conditions, dictionary keys in member paths and probe
/// parameters. Accepts JSON primitives (range-checked), strings for strings/chars/enums/GUIDs, <c>{h}</c>/<c>{var}</c>
/// for objects, anchors or BCL names for types, tagged values (<c>i64</c>, <c>f64</c>, <c>guid</c>, Unity structs…),
/// arrays, <c>{t:"dict"}</c>, and <c>{t:"new"}</c> construction. Anything else is <c>INVALID_PARAMS</c> with
/// <c>{param, expectedType, got}</c>.
/// </summary>
public sealed class ValueReader
{
    private const BindingFlags AllInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly DataModel _data;

    /// <summary>Creates the reader.</summary>
    public ValueReader(DataModel data) => _data = data;

    /// <summary>Converts <paramref name="json"/> to a value of <paramref name="target"/>.</summary>
    public object? Read(JsonValue? json, Type target, string param)
    {
        json ??= JsonNull.Instance;
        var nullable = Nullable.GetUnderlyingType(target);
        if (json is JsonNull)
        {
            return !target.IsValueType || nullable is not null ? null : throw DataErrors.InvalidValue(param, Name(target), json, "a value type can't be null");
        }

        target = nullable ?? target;
        if (json is JsonObject obj)
        {
            if (obj.ContainsKey("h") && obj.Count == 1 || obj.ContainsKey("var") && obj.Count == 1)
            {
                return Reference(obj, target, param);
            }

            if (obj["t"] is JsonString { Value: var tag })
            {
                return Tagged(obj, tag, target, param);
            }

            if (obj.ContainsKey("enum") && obj["value"] is JsonNumber enumNumber && target.IsEnum)
            {
                return Enum.ToObject(target, Integer(enumNumber, Enum.GetUnderlyingType(target), param));
            }

            if (obj.ContainsKey("mvid") && obj.ContainsKey("token"))
            {
                return Member(obj, target, param);
            }

            throw DataErrors.InvalidValue(param, Name(target), json, "unrecognised object (expected {h}, {var}, an anchor, or a tagged value)");
        }

        if (target == typeof(object))
        {
            return Natural(json, param);
        }

        if (json is JsonArray array)
        {
            return Sequence(array, target, param);
        }

        if (typeof(Type).IsAssignableFrom(target) && json is JsonString typeName)
        {
            return AnchorResolver.ResolveTypeName(typeName.Value, param);
        }

        return Primitive(json, target, param);
    }

    private object? Primitive(JsonValue json, Type target, string param)
    {
        switch (json)
        {
            case JsonBoolean b when target == typeof(bool):
                return b.Value;
            case JsonString s when target == typeof(string):
                return s.Value;
            case JsonString s when target == typeof(char):
                return s.Value.Length == 1 ? s.Value[0] : throw DataErrors.InvalidValue(param, "System.Char", json, "needs exactly one character");
            case JsonString s when target == typeof(Guid):
                return Guid.TryParse(s.Value, out var guid) ? guid : throw DataErrors.InvalidValue(param, "System.Guid", json);
            case JsonString s when target.IsEnum:
                return ParseEnum(s.Value, target, param, json);
            case JsonNumber n when target.IsEnum:
                return Enum.ToObject(target, Integer(n, Enum.GetUnderlyingType(target), param));
            case JsonNumber n when IsNumeric(target):
                return Number(n, target, param);
            case JsonString s when IsNumeric(target) && (s.Value is "NaN" or "Infinity" or "-Infinity") && (target == typeof(float) || target == typeof(double)):
                return NonFinite(s.Value, target);
        }

        throw DataErrors.InvalidValue(param, Name(target), json);
    }

    private object? Reference(JsonObject obj, Type target, string param)
    {
        object? value;
        if (obj["h"] is JsonNumber h && h.TryGetInt64(out var handle))
        {
            value = _data.Handles.Resolve(handle);
        }
        else if (obj["var"] is JsonString name)
        {
            var variable = _data.Variables.Get(name.Value, param + ".var");
            value = variable.Kind switch
            {
                VariableKind.Handle => _data.Handles.Resolve(variable.H),
                VariableKind.Value => Read(variable.Value, target, param),
                _ => typeof(Type).IsAssignableFrom(target)
                    ? _data.Anchors.ResolveType(variable.Static!, param + ".var")
                    : throw DataErrors.InvalidValue(param, Name(target), obj, $"the variable '{name.Value}' holds a type's statics"),
            };
        }
        else
        {
            throw DataErrors.InvalidValue(param, Name(target), obj);
        }

        if (value is not null && !target.IsInstanceOfType(value))
        {
            throw DataErrors.InvalidValue(param, Name(target), obj, $"it refers to a {AnchorWriter.TypeName(value.GetType())}");
        }

        return value;
    }

    private object? Member(JsonObject obj, Type target, string param)
    {
        var anchor = AnchorResolver.Read(obj, param);
        var member = _data.Anchors.ResolveMember(anchor, param);
        return target.IsInstanceOfType(member) ? member : throw DataErrors.InvalidValue(param, Name(target), obj, $"it's a {member.MemberType}");
    }

    private object? Tagged(JsonObject obj, string tag, Type target, string param)
    {
        var v = obj["v"] as JsonString;
        switch (tag)
        {
            case "i64" or "u64" when v is not null && IsNumeric(target):
                return Number(JsonNumber.FromRawText(v.Value), target, param);
            case "f32" or "f64" when v is not null && (target == typeof(float) || target == typeof(double)):
                return NonFinite(v.Value, target);
            case "guid" when v is not null && target == typeof(Guid):
                return Guid.TryParse(v.Value, out var guid) ? guid : throw DataErrors.InvalidValue(param, "System.Guid", obj);
            case "datetime" when v is not null && target == typeof(DateTime):
                return DateTime.Parse(v.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            case "datetimeoffset" when v is not null && target == typeof(DateTimeOffset):
                return DateTimeOffset.Parse(v.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            case "timespan" when v is not null && target == typeof(TimeSpan):
                return TimeSpan.ParseExact(v.Value, "c", CultureInfo.InvariantCulture);
            case "dict":
                return Dictionary(obj, target, param);
            case "list" when obj["items"] is JsonArray items:
                return Sequence(items, target, param + ".items");
            case "new":
                return New(obj, target, param);
            default:
                if (target.FullName == "UnityEngine." + tag || (target == typeof(object) && tag.Length > 0))
                {
                    return UnityStruct(obj, tag, target, param);
                }

                throw DataErrors.InvalidValue(param, Name(target), obj, $"a '{tag}' value doesn't fit");
        }
    }

    private object UnityStruct(JsonObject obj, string tag, Type target, string param)
    {
        if (target.FullName != "UnityEngine." + tag)
        {
            throw DataErrors.InvalidValue(param, Name(target), obj, $"a {tag} needs a UnityEngine.{tag} target");
        }

        var value = Activator.CreateInstance(target)!;
        foreach (var property in obj.Where(p => p.Key != "t"))
        {
            var field = target.GetField(property.Key, AllInstance) ?? target.GetField("m_" + char.ToUpperInvariant(property.Key[0]) + property.Key.Substring(1), AllInstance)
                ?? UnityStructAlias(target, property.Key)
                ?? throw DataErrors.InvalidValue($"{param}.{property.Key}", Name(target), property.Value, $"{tag} has no component '{property.Key}'");
            field.SetValue(value, Read(property.Value, field.FieldType, $"{param}.{property.Key}"));
        }

        return value;
    }

    private static FieldInfo? UnityStructAlias(Type target, string key) => key switch
    {
        "x" => target.GetField("m_XMin", AllInstance),
        "y" => target.GetField("m_YMin", AllInstance),
        "value" => target.GetField("m_Mask", AllInstance),
        _ => null,
    };

    private object Sequence(JsonArray array, Type target, string param)
    {
        var element = target.IsArray ? target.GetElementType()! : Collections.ElementType(target) ?? typeof(object);
        var items = array.Select((item, i) => Read(item, element, $"{param}[{i}]")).ToList();
        if (target.IsArray || target == typeof(object))
        {
            var result = Array.CreateInstance(element, items.Count);
            for (var i = 0; i < items.Count; i++)
            {
                result.SetValue(items[i], i);
            }

            return result;
        }

        var concrete = target.IsInterface || target.IsAbstract ? typeof(List<>).MakeGenericType(element) : target;
        if (!target.IsAssignableFrom(concrete))
        {
            throw DataErrors.InvalidValue(param, Name(target), array, "not a list type this agent can build");
        }

        var collection = Activator.CreateInstance(concrete)!;
        var add = concrete.GetMethod("Add", new[] { element }) ?? throw DataErrors.InvalidValue(param, Name(target), array, "the collection has no Add");
        foreach (var item in items)
        {
            add.Invoke(collection, new[] { item });
        }

        return collection;
    }

    private object Dictionary(JsonObject obj, Type target, string param)
    {
        var (keyType, valueType) = Collections.DictionaryTypes(target);
        var concrete = target.IsInterface || target.IsAbstract || target == typeof(object)
            ? typeof(Dictionary<,>).MakeGenericType(keyType, valueType)
            : target;
        if (target != typeof(object) && !target.IsAssignableFrom(concrete))
        {
            throw DataErrors.InvalidValue(param, Name(target), obj, "not a dictionary type this agent can build");
        }

        var dictionary = (IDictionary)Activator.CreateInstance(concrete)!;
        if (obj["entries"] is not JsonArray entries)
        {
            throw DataErrors.InvalidValue(param + ".entries", Name(target), obj["entries"], "needs an array of {k, v}");
        }

        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i] is not JsonObject entry || !entry.ContainsKey("k"))
            {
                throw DataErrors.InvalidValue($"{param}.entries[{i}]", "{k, v}", entries[i]);
            }

            var key = Read(entry["k"], keyType, $"{param}.entries[{i}].k") ?? throw DataErrors.InvalidValue($"{param}.entries[{i}].k", Name(keyType), entry["k"], "a key can't be null");
            dictionary[key] = Read(entry["v"], valueType, $"{param}.entries[{i}].v");
        }

        return dictionary;
    }

    private object New(JsonObject obj, Type target, string param)
    {
        var type = _data.Anchors.ResolveTypeRef(obj["type"], param + ".type");
        if (!target.IsAssignableFrom(type))
        {
            throw DataErrors.InvalidValue(param + ".type", Name(target), obj["type"], $"{AnchorWriter.TypeName(type)} isn't a {Name(target)}");
        }

        if (type.IsAbstract || type.IsInterface)
        {
            throw DataErrors.InvalidValue(param + ".type", Name(target), obj["type"], "abstract types can't be constructed");
        }

        object instance;
        try
        {
            if (obj["uninitialized"] is JsonBoolean { Value: true })
            {
                instance = FormatterServices.GetUninitializedObject(type);
            }
            else if (obj["ctor"] is { } ctorJson and not JsonNull)
            {
                var ctor = _data.Anchors.ResolveMember(AnchorResolver.Read(ctorJson, param + ".ctor"), param + ".ctor") as ConstructorInfo;
                if (ctor is null || ctor.DeclaringType != type)
                {
                    throw DataErrors.InvalidValue(param + ".ctor", $"a constructor of {AnchorWriter.TypeName(type)}", ctorJson);
                }

                var parameters = ctor.GetParameters();
                var args = obj["args"] as JsonArray ?? new JsonArray();
                if (args.Count != parameters.Length)
                {
                    throw DataErrors.InvalidValue(param + ".args", $"{parameters.Length} argument(s)", args);
                }

                instance = ctor.Invoke(parameters.Select((p, i) => Read(args[i], p.ParameterType, $"{param}.args[{i}]")).ToArray());
            }
            else
            {
                instance = Activator.CreateInstance(type, nonPublic: true)!;
            }
        }
        catch (TargetInvocationException e)
        {
            throw AgentErrors.Game(e);
        }
        catch (MissingMethodException)
        {
            throw DataErrors.InvalidValue(param, AnchorWriter.TypeName(type), obj, "it has no parameterless constructor: pass ctor + args, or uninitialized:true");
        }

        if (obj["fields"] is JsonObject fields)
        {
            foreach (var field in fields)
            {
                var info = FindField(type, field.Key)
                    ?? throw DataErrors.InvalidValue($"{param}.fields.{field.Key}", AnchorWriter.TypeName(type), field.Value, $"it has no field '{field.Key}'");
                info.SetValue(instance, Read(field.Value, info.FieldType, $"{param}.fields.{field.Key}"));
            }
        }

        return instance;
    }

    private object? Natural(JsonValue json, string param) => json switch
    {
        JsonBoolean b => b.Value,
        JsonString s => s.Value,
        JsonNumber n when n.TryGetInt64(out var l) => l,
        JsonNumber n => n.GetDouble(),
        JsonArray a => a.Select((item, i) => Natural(item, $"{param}[{i}]")).ToArray(),
        _ => throw DataErrors.InvalidValue(param, "System.Object", json),
    };

    private static object Number(JsonNumber n, Type target, string param)
    {
        if (target == typeof(float) || target == typeof(double) || target == typeof(decimal))
        {
            var d = n.GetDouble();
            if (target == typeof(float))
            {
                return float.IsInfinity((float)d) ? throw DataErrors.InvalidValue(param, "System.Single", n, "out of range") : (object)(float)d;
            }

            return target == typeof(double) ? d : n.TryGetDecimal(out var m) ? m : throw DataErrors.InvalidValue(param, "System.Decimal", n);
        }

        return Integer(n, target, param);
    }

    private static object Integer(JsonNumber n, Type target, string param)
    {
        try
        {
            if (target == typeof(ulong))
            {
                return n.TryGetUInt64(out var u) ? u : throw new OverflowException();
            }

            if (!n.TryGetInt64(out var value))
            {
                throw new OverflowException();
            }

            return Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
        }
        catch (OverflowException)
        {
            throw DataErrors.InvalidValue(param, Name(target), n, "not a whole number in range");
        }
    }

    private static object NonFinite(string text, Type target)
    {
        var d = text switch { "NaN" => double.NaN, "Infinity" => double.PositiveInfinity, _ => double.NegativeInfinity };
        return target == typeof(float) ? (object)(float)d : d;
    }

    private static object ParseEnum(string text, Type target, string param, JsonValue json)
    {
        try
        {
            return Enum.Parse(target, text, ignoreCase: false);
        }
        catch (ArgumentException)
        {
            throw DataErrors.InvalidValue(param, Name(target), json, $"not a {target.Name} name ({string.Join(", ", Enum.GetNames(target))})");
        }
    }

    private static FieldInfo? FindField(Type? type, string name)
    {
        for (; type is not null; type = type.BaseType)
        {
            var field = type.GetField(name, AllInstance | BindingFlags.DeclaredOnly) ?? type.GetField($"<{name}>k__BackingField", AllInstance | BindingFlags.DeclaredOnly);
            if (field is not null)
            {
                return field;
            }
        }

        return null;
    }

    private static bool IsNumeric(Type type) => Type.GetTypeCode(type) is TypeCode.SByte or TypeCode.Byte or TypeCode.Int16 or TypeCode.UInt16
        or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal;

    private static string Name(Type type) => AnchorWriter.TypeName(type);
}
