using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityLudometry.Protocol.Json;
using UnityRuntimeAnalysisAgent.Core.Abstractions;
using UnityRuntimeAnalysisAgent.Core.Data;

namespace UnityRuntimeAnalysisAgent.Core.Content;

/// <summary>What an object exports as.</summary>
public enum ContentKind
{
    /// <summary>JSON (the value encoding, or the summary for materials).</summary>
    Data,

    /// <summary>A PNG of the pixels in use (textures, sprites, render textures).</summary>
    Image,

    /// <summary>The raw bytes of a <c>TextAsset</c>.</summary>
    Text,

    /// <summary>An asset whose data doesn't change at runtime (audio, meshes, fonts): not exported here, static
    /// extraction reads it from the game's files.</summary>
    Static,
}

/// <summary>Content helpers: kinds, summaries as JSON, file names.</summary>
public static class ContentRules
{
    /// <summary>What <paramref name="type"/> exports as (by the full names of its base types).</summary>
    public static ContentKind KindOf(Type type)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            switch (t.FullName)
            {
                case "UnityEngine.Sprite":
                case "UnityEngine.Texture":
                    return ContentKind.Image;
                case "UnityEngine.TextAsset":
                    return ContentKind.Text;
                case "UnityEngine.AudioClip":
                case "UnityEngine.Mesh":
                case "UnityEngine.Font":
                case "TMPro.TMP_FontAsset":
                    return ContentKind.Static;
            }
        }

        return ContentKind.Data;
    }

    /// <summary>Whether a type's export is its summary (materials: shader, properties and texture references).</summary>
    public static bool ExportsSummary(Type type) => IsA(type, "UnityEngine.Material");

    /// <summary>Whether <paramref name="type"/> is or derives from the type named <paramref name="fullName"/>.</summary>
    public static bool IsA(Type type, string fullName)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            if (t.FullName == fullName)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A file name made from an object name: characters that aren't allowed in file names become <c>_</c>.</summary>
    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var text = new StringBuilder(name.Length);
        foreach (var c in name.Trim())
        {
            text.Append(Array.IndexOf(invalid, c) >= 0 || c < 32 ? '_' : c);
        }

        var result = text.ToString().Trim('.', ' ');
        return result.Length == 0 ? "unnamed" : result.Length > 120 ? result.Substring(0, 120) : result;
    }
}

/// <summary>Text helpers.</summary>
public static class TextRules
{
    /// <summary>Whether bytes look like UTF-8 text (no NULs, valid UTF-8).</summary>
    public static bool LooksLikeText(byte[] bytes)
    {
        if (Array.IndexOf(bytes, (byte)0) >= 0)
        {
            return false;
        }

        try
        {
            new UTF8Encoding(false, true).GetString(bytes);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>
/// Turns a summary (plain values, dictionaries, lists and Unity objects) into JSON. Unity objects become a descriptor
/// (with a handle, for interactive results) or a plain reference (for files, which outlive the session's handles).
/// </summary>
public sealed class SummaryEncoder
{
    private const int MaxItems = 64;
    private const int MaxDepth = 4;
    private readonly Func<object, JsonValue> _unityObject;

    /// <summary>Creates the encoder; <paramref name="unityObject"/> encodes a nested Unity object.</summary>
    public SummaryEncoder(Func<object, JsonValue> unityObject) => _unityObject = unityObject;

    /// <summary>A summary as a JSON object.</summary>
    public JsonObject Encode(IDictionary<string, object?> summary)
    {
        var json = new JsonObject();
        foreach (var pair in summary)
        {
            json.Add(pair.Key, Value(pair.Value, 0));
        }

        return json;
    }

    /// <summary>A plain reference to a Unity object (for files): type, name and instance id.</summary>
    public static JsonValue Reference(IUnityApi unity, object value)
    {
        var json = new JsonObject { { "type", JsonValue.From(value.GetType().FullName ?? value.GetType().Name) } };
        if (unity.Describe(value) is { } facts)
        {
            json.Add("name", JsonValue.From(facts.Name));
            json.Add("instanceId", new JsonNumber(facts.InstanceId));
        }

        return json;
    }

    /// <summary>The overview of a ScriptableObject (or any object without a type-specific summary): its serialized
    /// fields at depth 1 (plain values; the type name for anything else).</summary>
    public static IDictionary<string, object?> FieldOverview(object value)
    {
        var fields = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        for (var type = value.GetType(); type is not null && type.Namespace?.StartsWith("UnityEngine", StringComparison.Ordinal) != true; type = type.BaseType)
        {
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (fields.Count >= 32 || fields.ContainsKey(field.Name) || field.IsNotSerialized
                    || !(field.IsPublic || field.GetCustomAttributes(false).Any(a => a.GetType().Name == "SerializeField")))
                {
                    continue;
                }

                object? fieldValue;
                try
                {
                    fieldValue = field.GetValue(value);
                }
                catch (Exception e) when (e is TargetInvocationException or FieldAccessException or NotSupportedException)
                {
                    continue;
                }

                fields[field.Name] = fieldValue is null or string or bool or Enum || fieldValue.GetType().IsPrimitive ? fieldValue
                    : fieldValue is ICollection collection ? $"{field.FieldType.Name} ({collection.Count})"
                    : field.FieldType.FullName;
            }
        }

        return new Dictionary<string, object?> { { "fields", fields } };
    }

    private JsonValue Value(object? value, int depth)
    {
        switch (value)
        {
            case null:
                return JsonNull.Instance;
            case string s:
                return JsonValue.From(s);
            case bool b:
                return JsonValue.From(b);
            case Enum e:
                return JsonValue.From(e.ToString());
            case float f:
                return float.IsNaN(f) || float.IsInfinity(f) ? JsonValue.From(f.ToString(CultureInfo.InvariantCulture))
                    : new JsonNumber(double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)); // shortest float form
            case double d:
                return double.IsNaN(d) || double.IsInfinity(d) ? JsonValue.From(d.ToString(CultureInfo.InvariantCulture)) : new JsonNumber(d);
            case sbyte or byte or short or ushort or int or uint or long:
                return new JsonNumber(Convert.ToInt64(value, CultureInfo.InvariantCulture));
            case ulong ul:
                return new JsonNumber(ul);
            case char c:
                return JsonValue.From(c.ToString());
        }

        if (UnityTypes.IsUnityObject(value.GetType()))
        {
            return _unityObject(value);
        }

        if (depth >= MaxDepth)
        {
            return JsonValue.From(value.ToString() ?? string.Empty);
        }

        if (value is IDictionary dictionary)
        {
            var json = new JsonObject();
            foreach (DictionaryEntry entry in dictionary)
            {
                if (json.Count >= MaxItems)
                {
                    break;
                }

                json.Add(Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty, Value(entry.Value, depth + 1));
            }

            return json;
        }

        if (value is IEnumerable items)
        {
            var array = new JsonArray();
            foreach (var item in items)
            {
                if (array.Count >= MaxItems)
                {
                    break;
                }

                array.Add(Value(item, depth + 1));
            }

            return array;
        }

        return JsonValue.From(value.ToString() ?? string.Empty);
    }
}
