using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityRuntimeAnalysisAgent.Core.Data;

namespace UnityRuntimeAnalysisAgent.Core.Code;

/// <summary>
/// Generic Unity engine knowledge for the survey: which types are components and ScriptableObjects, which methods are
/// Unity messages, and which fields Unity serializes. Unity's types are found by name among the loaded assemblies; when
/// they aren't there, the rules are unavailable and the survey says so.
/// </summary>
public sealed class UnityRules
{
    /// <summary>The Unity messages detected when the caller doesn't give a list.</summary>
    public static readonly IReadOnlyList<string> DefaultMessages = new[]
    {
        "Awake", "OnEnable", "Start", "Update", "FixedUpdate", "LateUpdate", "OnDisable", "OnDestroy", "OnGUI", "OnValidate",
        "OnApplicationQuit", "OnApplicationPause", "OnApplicationFocus",
        "OnTriggerEnter", "OnTriggerStay", "OnTriggerExit", "OnTriggerEnter2D", "OnTriggerStay2D", "OnTriggerExit2D",
        "OnCollisionEnter", "OnCollisionStay", "OnCollisionExit", "OnCollisionEnter2D", "OnCollisionStay2D", "OnCollisionExit2D",
        "OnBecameVisible", "OnBecameInvisible",
        "OnMouseDown", "OnMouseUp", "OnMouseUpAsButton", "OnMouseEnter", "OnMouseExit", "OnMouseOver", "OnMouseDrag",
        "OnAnimatorMove", "OnAnimatorIK", "OnRenderObject", "OnWillRenderObject", "OnDrawGizmos", "OnDrawGizmosSelected", "Reset",
    };

    private static readonly HashSet<string> UnityValueTypes = new(StringComparer.Ordinal)
    {
        "Vector2", "Vector3", "Vector4", "Vector2Int", "Vector3Int", "Rect", "RectInt", "Bounds", "BoundsInt", "Quaternion", "Matrix4x4",
        "Color", "Color32", "LayerMask", "AnimationCurve", "Gradient", "RectOffset", "GUIStyle", "Hash128",
    };

    private readonly Regex[] _messages;
    private readonly bool _genericsSerialized;

    /// <summary>Creates the rules for a Unity version (null when unknown: generic fields count as not serialized).
    /// <paramref name="loadedTypes"/> needs to contain Unity's <c>MonoBehaviour</c> and <c>ScriptableObject</c> when loaded.</summary>
    public UnityRules(IEnumerable<Type> loadedTypes, string? unityVersion, IEnumerable<string>? messages)
    {
        foreach (var type in loadedTypes)
        {
            switch (type.FullName)
            {
                case "UnityEngine.MonoBehaviour":
                    MonoBehaviour ??= type;
                    break;
                case "UnityEngine.ScriptableObject":
                    ScriptableObject ??= type;
                    break;
            }
        }

        _messages = (messages ?? DefaultMessages).Select(m => new Regex("^" + Regex.Escape(m).Replace("\\*", ".*") + "$", RegexOptions.CultureInvariant)).ToArray();
        _genericsSerialized = AtLeast(unityVersion, 2020, 1);
    }

    /// <summary><c>UnityEngine.MonoBehaviour</c>, when loaded.</summary>
    public Type? MonoBehaviour { get; }

    /// <summary><c>UnityEngine.ScriptableObject</c>, when loaded.</summary>
    public Type? ScriptableObject { get; }

    /// <summary>Whether Unity's types were found.</summary>
    public bool Available => MonoBehaviour is not null;

    /// <summary>A component (derives from <c>UnityEngine.Component</c>).</summary>
    public static bool IsComponent(Type type) => UnityTypes.IsSceneObject(type) && type.FullName != "UnityEngine.GameObject";

    /// <summary>A ScriptableObject.</summary>
    public bool IsScriptableObject(Type type) => ScriptableObject is not null && ScriptableObject.IsAssignableFrom(type);

    /// <summary>A MonoBehaviour.</summary>
    public bool IsMonoBehaviour(Type type) => MonoBehaviour is not null && MonoBehaviour.IsAssignableFrom(type);

    /// <summary>Whether Unity serializes the fields of this type (MonoBehaviours, ScriptableObjects, [Serializable] types).</summary>
    public bool SerializesFieldsOf(Type type) => IsMonoBehaviour(type) || IsScriptableObject(type) || (IsSerializableAttributed(type) && !UnityTypes.IsUnityObject(type));

    /// <summary>Whether a method (by name) is one of the Unity messages looked for.</summary>
    public bool IsMessage(string name) => _messages.Any(m => m.IsMatch(name));

    /// <summary>The rule that makes Unity look at a field (<c>public</c>, <c>serializeField</c>, <c>serializeReference</c>),
    /// or null if it doesn't.</summary>
    public static string? Rule(FieldInfo field)
    {
        if (field.IsStatic || field.IsLiteral)
        {
            return null;
        }

        if (Describe.HasAttribute(field, "UnityEngine.SerializeReference"))
        {
            return "serializeReference";
        }

        return field.IsPublic ? "public" : Describe.HasAttribute(field, "UnityEngine.SerializeField") ? "serializeField" : null;
    }

    /// <summary>Unity's verdict on a field it looks at: <c>serialized</c>, <c>not_serializable_type</c> or <c>nonSerialized</c>.</summary>
    public string Verdict(FieldInfo field, string rule)
    {
        if (field.IsInitOnly || field.IsNotSerialized || Describe.HasAttribute(field, "System.NonSerializedAttribute"))
        {
            return "nonSerialized";
        }

        if (rule == "serializeReference")
        {
            var type = field.FieldType;
            return !type.IsValueType && type != typeof(string) && !UnityTypes.IsUnityObject(type) ? "serialized" : "not_serializable_type";
        }

        return IsSerializable(field.FieldType, allowCollection: true) ? "serialized" : "not_serializable_type";
    }

    /// <summary>Whether Unity can serialize a value of this type in a field.</summary>
    public bool IsSerializable(Type type, bool allowCollection)
    {
        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || UnityTypes.IsUnityObject(type))
        {
            return type != typeof(IntPtr) && type != typeof(UIntPtr);
        }

        if (type.Namespace == "UnityEngine" && UnityValueTypes.Contains(type.Name))
        {
            return true;
        }

        if (allowCollection && type.IsArray && type.GetArrayRank() == 1)
        {
            return IsSerializable(type.GetElementType()!, allowCollection: false);
        }

        if (allowCollection && type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            return IsSerializable(type.GetGenericArguments()[0], allowCollection: false);
        }

        if (type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition || type.IsPointer || typeof(Delegate).IsAssignableFrom(type))
        {
            return false;
        }

        if (type.IsGenericType && !_genericsSerialized)
        {
            return false;
        }

        return IsSerializableAttributed(type) && type.Assembly != typeof(object).Assembly;
    }

    /// <summary>Whether a type is marked <c>[Serializable]</c>.</summary>
    public static bool IsSerializableAttributed(Type type) => (type.Attributes & TypeAttributes.Serializable) != 0;

    private static bool AtLeast(string? version, int major, int minor)
    {
        if (version is null)
        {
            return false;
        }

        var parts = version.Split('.');
        return parts.Length >= 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ma)
            && int.TryParse(new string(parts[1].TakeWhile(char.IsDigit).ToArray()), NumberStyles.None, CultureInfo.InvariantCulture, out var mi)
            && (ma > major || (ma == major && mi >= minor));
    }
}
