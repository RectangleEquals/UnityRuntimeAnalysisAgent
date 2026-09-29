using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;

namespace UnityRuntimeAnalysisAgent.Core.Data;

/// <summary>
/// Live → JSON. Primitives are native (64-bit integers outside ±2⁵³ and non-finite floats are tagged),
/// enums carry their type and names, Unity structs are recognised by full name, Unity objects become handle descriptors,
/// collections are counted, and other objects are written field by field in declaration order. Whenever a limit stops the
/// writer, the value is replaced by a <b>redaction stub</b> whose ref re-reads it later: nothing is dropped
/// silently. One writer encodes one result (shared objects inside it are written once, then as <c>{ref: h}</c>).
/// Main thread, unless the view is in safe mode.
/// </summary>
public sealed class ValueWriter
{
    private const long MaxSafeInteger = 9_007_199_254_740_992; // 2^53
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    private static readonly Dictionary<string, (string Tag, (string Json, string Field)[] Parts)> UnityStructs = new(StringComparer.Ordinal)
    {
        ["UnityEngine.Vector2"] = ("Vector2", new[] { ("x", "x"), ("y", "y") }),
        ["UnityEngine.Vector3"] = ("Vector3", new[] { ("x", "x"), ("y", "y"), ("z", "z") }),
        ["UnityEngine.Vector4"] = ("Vector4", new[] { ("x", "x"), ("y", "y"), ("z", "z"), ("w", "w") }),
        ["UnityEngine.Vector2Int"] = ("Vector2Int", new[] { ("x", "m_X"), ("y", "m_Y") }),
        ["UnityEngine.Vector3Int"] = ("Vector3Int", new[] { ("x", "m_X"), ("y", "m_Y"), ("z", "m_Z") }),
        ["UnityEngine.Quaternion"] = ("Quaternion", new[] { ("x", "x"), ("y", "y"), ("z", "z"), ("w", "w") }),
        ["UnityEngine.Color"] = ("Color", new[] { ("r", "r"), ("g", "g"), ("b", "b"), ("a", "a") }),
        ["UnityEngine.Color32"] = ("Color32", new[] { ("r", "r"), ("g", "g"), ("b", "b"), ("a", "a") }),
        ["UnityEngine.Rect"] = ("Rect", new[] { ("x", "m_XMin"), ("y", "m_YMin"), ("width", "m_Width"), ("height", "m_Height") }),
        ["UnityEngine.RectInt"] = ("RectInt", new[] { ("x", "m_XMin"), ("y", "m_YMin"), ("width", "m_Width"), ("height", "m_Height") }),
        ["UnityEngine.Bounds"] = ("Bounds", new[] { ("center", "m_Center"), ("extents", "m_Extents") }),
        ["UnityEngine.LayerMask"] = ("LayerMask", new[] { ("value", "m_Mask") }),
        ["UnityEngine.Matrix4x4"] = ("Matrix4x4", Enumerable.Range(0, 16).Select(i => ($"m{i % 4}{i / 4}", $"m{i % 4}{i / 4}")).ToArray()),
    };

    private readonly DataModel _data;
    private readonly ViewOptions _view;
    private readonly long _frame;
    private readonly Dictionary<object, long> _written = new(IdentityComparer.Instance);
    private readonly Dictionary<MemberInfo, Anchor> _anchors = new();
    private readonly SortedDictionary<string, int> _redactions = new(StringComparer.Ordinal);

    /// <summary>Creates a writer for one result.</summary>
    public ValueWriter(DataModel data, ViewOptions view, long frame)
    {
        _data = data;
        _view = view;
        _frame = frame;
    }

    /// <summary>Redactions so far, by reason (for summaries and file footers).</summary>
    public IReadOnlyDictionary<string, int> Redactions => _redactions;

    /// <summary>Encodes <paramref name="value"/>, found at <paramref name="place"/>. <paramref name="range"/> pages a list or
    /// dictionary; <paramref name="fullString"/> writes a string up to 1 MiB whatever the view's <c>maxString</c>.</summary>
    public JsonValue Write(object? value, Place place, (int From, int To)? range = null, bool fullString = false) =>
        Encode(value, place, _view.Depth, root: true, member: null, range, fullString);

    /// <summary>A redaction stub (for callers that withhold or cut values themselves, e.g. <c>policy</c>).</summary>
    public JsonObject Stub(string reason, object? value, Place place, string? member = null, int? count = null, int? length = null,
        string? preview = null, (int From, int To)? range = null, bool? live = null)
    {
        Count(reason);
        var info = new JsonObject { { "reason", JsonValue.From(reason) } };
        long? h = value is not null && !value.GetType().IsValueType && value is not string ? Mint(value) : null;
        if (reason != "policy")
        {
            info.Add("ref", JsonValue.From(MintRef(value, place, range, h)));
        }

        if (value is not null && AnchorWriter.CanAnchor(value.GetType()))
        {
            info.Add("type", AnchorWriter.ToJson(AnchorWriter.ForType(value.GetType())));
        }

        if (member is not null)
        {
            info.Add("member", JsonValue.From(member));
        }

        if (place.Locator is { } locator)
        {
            info.Add("locator", JsonValue.From(locator));
        }

        if (place.Root is not null && place.Expressible && place.Path.Count > 0)
        {
            info.Add("path", new JsonArray(place.Path.Select(s => (JsonValue?)s.ToJson())));
        }

        if (h is not null)
        {
            info.Add("h", JsonValue.From(h.Value));
        }

        info.Add("size", new JsonObject
        {
            { "count", count is null ? JsonNull.Instance : JsonValue.From(count.Value) },
            { "length", length is null ? JsonNull.Instance : JsonValue.From(length.Value) },
            { "estBytes", length is null ? JsonNull.Instance : JsonValue.From(length.Value * 2L) },
        });
        if (preview is not null)
        {
            info.Add("preview", JsonValue.From(preview));
        }

        if (range is { } r)
        {
            info.Add("range", new JsonArray(new JsonValue?[] { JsonValue.From(r.From), JsonValue.From(r.To) }));
        }

        if (live is not null)
        {
            info.Add("live", JsonValue.From(live.Value));
        }

        return new JsonObject { { "redacted", info } };
    }

    private JsonValue Encode(object? value, Place place, int depth, bool root, string? member, (int From, int To)? range = null, bool fullString = false)
    {
        switch (value)
        {
            case null:
                return JsonNull.Instance;
            case bool b:
                return JsonValue.From(b);
            case string s:
                var max = fullString ? Math.Max(_view.MaxString, 1_048_576) : _view.MaxString;
                return s.Length <= max ? JsonValue.From(s) : Stub("maxString", s, place, member, length: s.Length, preview: s.Substring(0, max));
            case char c:
                return JsonValue.From(c.ToString());
            case sbyte or byte or short or ushort or int or uint:
                return new JsonNumber(Convert.ToInt64(value, CultureInfo.InvariantCulture));
            case long l:
                return l >= -MaxSafeInteger && l <= MaxSafeInteger ? new JsonNumber(l) : Tagged("i64", l.ToString(CultureInfo.InvariantCulture));
            case ulong u:
                return u <= MaxSafeInteger ? new JsonNumber(u) : Tagged("u64", u.ToString(CultureInfo.InvariantCulture));
            case float f:
                return float.IsNaN(f) || float.IsInfinity(f) ? Tagged("f32", NonFinite(f)) : JsonNumber.FromRawText(f.ToString("R", CultureInfo.InvariantCulture));
            case double d:
                return double.IsNaN(d) || double.IsInfinity(d) ? Tagged("f64", NonFinite(d)) : new JsonNumber(d);
            case decimal m:
                return new JsonNumber(m);
            case IntPtr p:
                return Encode(p.ToInt64(), place, depth, root, member);
            case UIntPtr up:
                return Encode(up.ToUInt64(), place, depth, root, member);
            case Enum e:
                return EnumValue(e);
            case Guid g:
                return Tagged("guid", g.ToString("D"));
            case DateTime dt:
                return Tagged("datetime", dt.ToString("o", CultureInfo.InvariantCulture));
            case DateTimeOffset dto:
                return Tagged("datetimeoffset", dto.ToString("o", CultureInfo.InvariantCulture));
            case TimeSpan ts:
                return Tagged("timespan", ts.ToString("c", CultureInfo.InvariantCulture));
            case Type t:
                return AnchorWriter.CanAnchor(t) ? AnchorWriter.ToJson(AnchorWriter.ForType(t)) : AnchorWriter.TypeRef(t);
            case MemberInfo mi:
                return Guarded(() => AnchorWriter.ToJson(AnchorWriter.ForMember(mi)));
            case Delegate del:
                return DelegateValue(del);
        }

        var type = value.GetType();
        if (UnityTypes.IsUnityObject(type) && !(root && _view.ExpandRootUnityObject))
        {
            return Descriptor(value);
        }

        if (UnityStructs.TryGetValue(type.FullName ?? string.Empty, out var unityStruct) && _view.UnityStructsCompact)
        {
            return UnityStruct(value, unityStruct) ?? ObjectValue(value, type, place, depth);
        }

        if (!type.IsValueType && _written.TryGetValue(value, out var seen))
        {
            return new JsonObject { { "ref", JsonValue.From(seen) } };
        }

        if (UnityTypes.IsUnityEvent(type))
        {
            return UnityEventValue(value);
        }

        var lazy = value is IEnumerable && !(value is IDictionary) && !type.IsArray && !Collections.IsCollection(type);
        if (lazy && !_view.Enumerate)
        {
            return Stub("notEnumerated", value, place, member);
        }

        if (depth <= 0)
        {
            return Stub("depth", value, place, member, count: value is IEnumerable && !lazy ? Collections.Count(value) : null);
        }

        if (!type.IsValueType)
        {
            _written[value] = Mint(value);
        }

        return value switch
        {
            IDictionary dictionary => DictionaryValue(dictionary, place, depth, range),
            IEnumerable sequence when lazy => LazyValue(sequence, place, depth),
            IEnumerable sequence => ListValue(sequence, place, depth, range),
            _ => ObjectValue(value, type, place, depth),
        };
    }

    private JsonValue ListValue(IEnumerable sequence, Place place, int depth, (int From, int To)? range)
    {
        var count = Collections.Count(sequence) ?? 0;
        var from = Math.Min(range?.From ?? 0, count);
        var to = Math.Min(range?.To ?? count, count);
        var items = new JsonArray();
        var index = 0;
        var emitted = 0;
        foreach (var item in sequence)
        {
            if (index >= to)
            {
                break;
            }

            if (index >= from)
            {
                if (emitted == _view.MaxItems)
                {
                    break;
                }

                items.Add(Encode(item, place.Then(new MemberPathStep { Index = index }, Locators.Index(index)), depth - 1, root: false, member: null));
                emitted++;
            }

            index++;
        }

        var stopped = from + emitted;
        if (stopped < count)
        {
            items.Add(Stub("maxItems", sequence, place, count: count, range: (stopped, count)));
        }

        var list = new JsonObject { { "t", JsonValue.From("list") }, { "count", JsonValue.From(count) }, { "items", items } };
        if (from > 0)
        {
            list.Add("from", JsonValue.From(from));
        }

        return list;
    }

    private JsonValue DictionaryValue(IDictionary dictionary, Place place, int depth, (int From, int To)? range)
    {
        var count = dictionary.Count;
        var from = Math.Min(range?.From ?? 0, count);
        var to = Math.Min(range?.To ?? count, count);
        var entries = new JsonArray();
        var index = 0;
        var emitted = 0;
        var enumerator = dictionary.GetEnumerator();
        while (index < to && enumerator.MoveNext())
        {
            if (index >= from)
            {
                if (emitted == _view.MaxItems)
                {
                    break;
                }

                var key = enumerator.Key;
                var keyPath = PathKey(key);
                var keyPlace = place.Then(null, string.Empty);
                var valuePlace = keyPath is null ? place.Then(null, string.Empty) : place.Then(new MemberPathStep { Key = keyPath }, Locators.Key(keyPath));
                entries.Add(new JsonObject
                {
                    { "k", Encode(key, keyPlace, depth - 1, root: false, member: null) },
                    { "v", Encode(enumerator.Value, valuePlace, depth - 1, root: false, member: null) },
                });
                emitted++;
            }

            index++;
        }

        var stopped = from + emitted;
        if (stopped < count)
        {
            entries.Add(Stub("maxItems", dictionary, place, count: count, range: (stopped, count)));
        }

        var dict = new JsonObject { { "t", JsonValue.From("dict") }, { "count", JsonValue.From(count) }, { "entries", entries } };
        if (from > 0)
        {
            dict.Add("from", JsonValue.From(from));
        }

        return dict;
    }

    // A lazy sequence the caller asked to enumerate: at most maxItems items; if there are more, a stub says so (the total
    // isn't known without enumerating everything).
    private JsonValue LazyValue(IEnumerable sequence, Place place, int depth)
    {
        var items = new JsonArray();
        var index = 0;
        try
        {
            foreach (var item in sequence)
            {
                if (index == _view.MaxItems)
                {
                    items.Add(Stub("maxItems", sequence, place));
                    break;
                }

                items.Add(Encode(item, place.Then(new MemberPathStep { Index = index }, Locators.Index(index)), depth - 1, root: false, member: null));
                index++;
            }
        }
        catch (Exception e)
        {
            items.Add(MemberError(e));
        }

        return new JsonObject { { "t", JsonValue.From("list") }, { "count", JsonValue.From(index) }, { "items", items }, { "lazy", JsonValue.From(true) } };
    }

    private JsonValue ObjectValue(object value, Type type, Place place, int depth)
    {
        var members = Members(type);
        var fields = new JsonObject();
        var props = new JsonObject();
        var anchors = new JsonObject();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var emitted = 0;
        foreach (var member in members)
        {
            if (emitted == _view.MaxMembers)
            {
                fields.Add("$more", Stub("maxMembers", value, place, count: members.Count));
                break;
            }

            var name = TargetResolver.DisplayName(member);
            var key = used.Add(name) ? name : $"{member.DeclaringType!.Name}.{name}";
            used.Add(key);
            var anchor = AnchorOf(member);
            var childPlace = place.Then(new MemberPathStep { Member = anchor }, Locators.Member(name));
            JsonValue encoded;
            try
            {
                encoded = Encode(TargetResolver.Read(member, value), childPlace, depth - 1, root: false, member: key);
            }
            catch (Exception e)
            {
                encoded = MemberError(e);
            }

            (member is PropertyInfo ? props : fields).Add(key, encoded);
            if (_view.Anchors)
            {
                anchors.Add(key, anchor.ToJson());
            }

            emitted++;
        }

        var result = new JsonObject { { "t", JsonValue.From("object") } };
        if (!type.IsValueType)
        {
            result.Add("h", JsonValue.From(Mint(value)));
        }

        result.Add("type", AnchorWriter.CanAnchor(type) ? AnchorWriter.ToJson(AnchorWriter.ForType(type)) : AnchorWriter.ToJson(AnchorWriter.ForType(typeof(object))));
        result.Add("fields", fields);
        if (props.Count > 0)
        {
            result.Add("props", props);
        }

        if (_view.Anchors)
        {
            result.Add("anchors", anchors);
        }

        return result;
    }

    // Fields base-first in declaration (token) order; then, with properties:true, readable non-indexed properties that
    // aren't already shown through their backing field. Unity's own bookkeeping fields are left out.
    private List<MemberInfo> Members(Type type)
    {
        var chain = new List<Type>();
        for (var t = type; t is not null && t != typeof(object) && t != typeof(ValueType); t = t.BaseType)
        {
            if (!UnityTypes.IsEngineBase(t))
            {
                chain.Insert(0, t);
            }
        }

        var members = new List<MemberInfo>();
        foreach (var t in chain)
        {
            members.AddRange(t.GetFields(Instance).Where(f => _view.NonPublic || f.IsPublic).OrderBy(f => f.MetadataToken));
        }

        if (_view.Properties && !_view.Safe)
        {
            var shown = new HashSet<string>(members.Select(TargetResolver.DisplayName), StringComparer.Ordinal);
            foreach (var t in chain)
            {
                members.AddRange(t.GetProperties(Instance)
                    .Where(p => SafeReflection.IsPlainReadable(p) && (_view.NonPublic || p.GetGetMethod() is not null) && !shown.Contains(p.Name))
                    .OrderBy(p => p.MetadataToken));
            }
        }

        return members;
    }

    private JsonValue? UnityStruct(object value, (string Tag, (string Json, string Field)[] Parts) layout)
    {
        var result = new JsonObject { { "t", JsonValue.From(layout.Tag) } };
        foreach (var (json, fieldName) in layout.Parts)
        {
            var field = value.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field is null)
            {
                return null; // an unexpected layout: fall back to the plain object encoding
            }

            var part = field.GetValue(value);
            result.Add(json, part is not null && UnityStructs.TryGetValue(part.GetType().FullName ?? string.Empty, out var nested)
                ? UnityStruct(part, nested) ?? JsonNull.Instance
                : part switch
                {
                    float f => float.IsNaN(f) || float.IsInfinity(f) ? JsonValue.From(NonFinite(f)) : JsonNumber.FromRawText(f.ToString("R", CultureInfo.InvariantCulture)),
                    null => JsonNull.Instance,
                    _ => new JsonNumber(Convert.ToInt64(part, CultureInfo.InvariantCulture)),
                });
        }

        return result;
    }

    private JsonValue UnityEventValue(object value)
    {
        var result = new JsonObject { { "t", JsonValue.From("unityEvent") } };
        if (_view.Safe)
        {
            return result;
        }

        try
        {
            var type = value.GetType();
            var count = (int)type.GetMethod("GetPersistentEventCount", Type.EmptyTypes)!.Invoke(value, null)!;
            var getTarget = type.GetMethod("GetPersistentTarget", new[] { typeof(int) })!;
            var getMethod = type.GetMethod("GetPersistentMethodName", new[] { typeof(int) })!;
            var persistent = new JsonArray();
            for (var i = 0; i < count; i++)
            {
                var target = getTarget.Invoke(value, new object[] { i });
                persistent.Add(new JsonObject
                {
                    { "target", target is null ? JsonNull.Instance : Descriptor(target) },
                    { "method", JsonValue.From(getMethod.Invoke(value, new object[] { i }) as string ?? string.Empty) },
                });
            }

            result.Add("persistent", persistent);
            var calls = FindField(type, "m_Calls")?.GetValue(value);
            if (calls is not null && FindField(calls.GetType(), "m_RuntimeCalls")?.GetValue(calls) is ICollection runtime)
            {
                result.Add("runtimeListeners", JsonValue.From(runtime.Count));
            }
        }
        catch (Exception)
        {
            // listeners can't be read in this Unity version: the tag alone still says what it is
        }

        return result;
    }

    private JsonValue DelegateValue(Delegate value)
    {
        var entries = new JsonArray();
        foreach (var single in value.GetInvocationList())
        {
            entries.Add(new JsonObject
            {
                { "target", single.Target is null ? JsonNull.Instance : Descriptor(single.Target) },
                { "method", Guarded(() => AnchorWriter.ToJson(AnchorWriter.ForMember(single.Method))) },
            });
        }

        return new JsonObject { { "delegate", entries } };
    }

    private JsonValue EnumValue(Enum value)
    {
        var underlying = Convert.GetTypeCode(value) == TypeCode.UInt64
            ? (JsonValue)new JsonNumber(Convert.ToUInt64(value, CultureInfo.InvariantCulture))
            : new JsonNumber(Convert.ToInt64(value, CultureInfo.InvariantCulture));
        return new JsonObject
        {
            { "enum", AnchorWriter.ToJson(AnchorWriter.ForType(value.GetType())) },
            { "value", underlying },
            { "name", JsonValue.From(value.ToString()) },
        };
    }

    private JsonValue Descriptor(object value) => _data.Handles.Describe(Mint(value)).ToJson();

    private long Mint(object value) => _view.Safe ? _data.Handles.MintWithoutUnity(value) : _data.Handles.Mint(value);

    private string MintRef(object? value, Place place, (int From, int To)? range, long? h)
    {
        var entry = new ExpansionEntry { View = _view, Frame = _frame, Locator = place.Locator, Range = range };
        if (place.Root is not null && place.Expressible)
        {
            entry.Root = place.Root;
            entry.Path = new List<MemberPathStep>(place.Path);
        }
        else if (h is not null)
        {
            entry.Root = new Target { H = h.Value };
        }
        else
        {
            entry.Retained = value;
            entry.HasRetained = true;
        }

        return _data.Expansions.Mint(entry);
    }

    private Anchor AnchorOf(MemberInfo member)
    {
        if (!_anchors.TryGetValue(member, out var anchor))
        {
            anchor = AnchorWriter.ForMember(member);
            _anchors[member] = anchor;
        }

        return anchor;
    }

    // Dictionary keys that can be written into a member path: strings, numbers and enum names.
    private static JsonValue? PathKey(object key) => key switch
    {
        string s => JsonValue.From(s),
        Enum e => JsonValue.From(e.ToString()),
        sbyte or byte or short or ushort or int or uint or long => new JsonNumber(Convert.ToInt64(key, CultureInfo.InvariantCulture)),
        ulong u => new JsonNumber(u),
        char c => JsonValue.From(c.ToString()),
        _ => null,
    };

    private static FieldInfo? FindField(Type? type, string name)
    {
        for (; type is not null; type = type.BaseType)
        {
            var field = type.GetField(name, Instance);
            if (field is not null)
            {
                return field;
            }
        }

        return null;
    }

    private static JsonValue Guarded(Func<JsonValue> read)
    {
        try
        {
            return read();
        }
        catch (Exception e)
        {
            return MemberError(e);
        }
    }

    private static JsonObject MemberError(Exception e)
    {
        var inner = e is TargetInvocationException { InnerException: { } i } ? i : e is Dispatch.GameCodeException { InnerException: { } g } ? g : e;
        var game = e is TargetInvocationException or Dispatch.GameCodeException;
        return new JsonObject
        {
            {
                "error", new JsonObject
                {
                    { "code", JsonValue.From(game ? "GAME_EXCEPTION" : "INTERNAL") },
                    { "message", JsonValue.From(inner.Message) },
                    { "data", new JsonObject { { "exceptionType", JsonValue.From(inner.GetType().FullName ?? inner.GetType().Name) } } },
                }
            },
        };
    }

    private static JsonObject Tagged(string tag, string value) => new() { { "t", JsonValue.From(tag) }, { "v", JsonValue.From(value) } };

    private static string NonFinite(double value) => double.IsNaN(value) ? "NaN" : value > 0 ? "Infinity" : "-Infinity";

    private void Count(string reason) => _redactions[reason] = _redactions.TryGetValue(reason, out var n) ? n + 1 : 1;
}
