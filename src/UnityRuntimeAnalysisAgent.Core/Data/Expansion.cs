using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Abstractions;

namespace UnityRuntimeAnalysisAgent.Core.Data;

/// <summary>How values are encoded (the protocol's <c>view</c>, clamped to its maximums).</summary>
public sealed class ViewOptions
{
    /// <summary>The default view.</summary>
    public static readonly ViewOptions Default = new();

    /// <summary>Container levels expanded (default 2, max 8).</summary>
    public int Depth { get; set; } = 2;

    /// <summary>Members per object (default 64, max 1,000).</summary>
    public int MaxMembers { get; set; } = 64;

    /// <summary>Items per list or dictionary (default 64, max 10,000).</summary>
    public int MaxItems { get; set; } = 64;

    /// <summary>Characters per string (default 1,024, max 1,048,576).</summary>
    public int MaxString { get; set; } = 1024;

    /// <summary>Run property getters, each guarded (default false).</summary>
    public bool Properties { get; set; }

    /// <summary>Include non-public members (default true).</summary>
    public bool NonPublic { get; set; } = true;

    /// <summary>Enumerate lazy <c>IEnumerable</c>s, which may have side effects (default false).</summary>
    public bool Enumerate { get; set; }

    /// <summary>Include member anchors (default false).</summary>
    public bool Anchors { get; set; }

    /// <summary>Unity structs as compact tagged objects (default true).</summary>
    public bool UnityStructsCompact { get; set; } = true;

    /// <summary>Safe mode: no getters, no calls into Unity (for values captured inside hooks).</summary>
    public bool Safe { get; set; }

    /// <summary>Expand a Unity object at the root instead of describing it (for inspecting one object).</summary>
    public bool ExpandRootUnityObject { get; set; }

    /// <summary>The options of a protocol view (null → the defaults), clamped to the maximums.</summary>
    public static ViewOptions From(View? view)
    {
        var options = new ViewOptions();
        if (view is null)
        {
            return options;
        }

        options.Depth = Clamp(view.Depth, options.Depth, 8);
        options.MaxMembers = Clamp(view.MaxMembers, options.MaxMembers, 1000);
        options.MaxItems = Clamp(view.MaxItems, options.MaxItems, 10_000);
        options.MaxString = Clamp(view.MaxString, options.MaxString, 1_048_576);
        options.Properties = view.Properties ?? options.Properties;
        options.NonPublic = view.NonPublic ?? options.NonPublic;
        options.Enumerate = view.Enumerate ?? options.Enumerate;
        options.Anchors = view.Anchors ?? options.Anchors;
        options.UnityStructsCompact = view.UnityStructsCompact ?? options.UnityStructsCompact;
        return options;
    }

    /// <summary>A copy.</summary>
    public ViewOptions Clone() => (ViewOptions)MemberwiseClone();

    private static int Clamp(long? value, int fallback, int max) => value is null ? fallback : (int)Math.Max(0, Math.Min(max, value.Value));
}

/// <summary>What a redaction ref re-reads: a root (target, or an object retained at capture time) and a member path.</summary>
public sealed class ExpansionEntry
{
    /// <summary>Where the value starts (null when <see cref="Retained"/> is used).</summary>
    public Target? Root { get; set; }

    /// <summary>The steps from the root to the value.</summary>
    public List<MemberPathStep> Path { get; set; } = new();

    /// <summary>A value kept as it was (values that can't be re-read by path, or captured values).</summary>
    public object? Retained { get; set; }

    /// <summary>Whether <see cref="Retained"/> is set (it may be a boxed struct, or null).</summary>
    public bool HasRetained { get; set; }

    /// <summary>The view that produced the stub.</summary>
    public ViewOptions View { get; set; } = ViewOptions.Default;

    /// <summary>The frame of the read that produced the stub.</summary>
    public long Frame { get; set; }

    /// <summary>The value's durable address, when it has one.</summary>
    public string? Locator { get; set; }

    /// <summary>The items the stub stands for (<c>[from, to)</c>), for list and dictionary tails.</summary>
    public (int From, int To)? Range { get; set; }
}

/// <summary>
/// Redaction refs: session-scoped tokens (<c>x&lt;run&gt;:&lt;n&gt;</c>) for values a limit left out, kept LRU (default
/// 50,000). Refs cited in reports can be pinned. An evicted ref fails with <c>REF_EXPIRED</c> carrying the value's
/// locator, which stays resolvable on its own. Thread-safe.
/// </summary>
public sealed class ExpansionRegistry
{
    private static int s_runs;
    private readonly object _gate = new();
    private readonly string _prefix;
    private readonly Dictionary<string, (ExpansionEntry Entry, LinkedListNode<string> Node)> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _recency = new();
    private readonly HashSet<string> _pinned = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _tombstones = new(StringComparer.Ordinal);
    private readonly Queue<string> _tombstoneOrder = new();
    private long _next;

    /// <summary>Creates a registry keeping at most <paramref name="max"/> unpinned refs.</summary>
    public ExpansionRegistry(int max = 50_000)
    {
        Max = max;
        var run = unchecked((System.Threading.Interlocked.Increment(ref s_runs) * 7919) + (Environment.TickCount & 0xFFFF));
        _prefix = "x" + (run & 0xFFFFF).ToString("x", CultureInfo.InvariantCulture) + ":";
    }

    /// <summary>The cap on unpinned refs.</summary>
    public int Max { get; }

    /// <summary>Refs currently held.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>A new ref for <paramref name="entry"/>.</summary>
    public string Mint(ExpansionEntry entry)
    {
        lock (_gate)
        {
            var id = _prefix + (++_next).ToString(CultureInfo.InvariantCulture);
            _entries.Add(id, (entry, _recency.AddLast(id)));
            var node = _recency.First;
            while (_entries.Count > Max && node is not null)
            {
                var next = node.Next;
                if (!_pinned.Contains(node.Value))
                {
                    Evict(node.Value);
                }

                node = next;
            }

            return id;
        }
    }

    /// <summary>The entry behind a ref, or <c>REF_EXPIRED</c>.</summary>
    public ExpansionEntry Get(string reference)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(reference, out var held))
            {
                _recency.Remove(held.Node);
                _recency.AddLast(held.Node);
                return held.Entry;
            }

            throw DataErrors.RefExpired(reference, _tombstones.TryGetValue(reference, out var locator) ? locator : null);
        }
    }

    /// <summary>Keeps a ref from eviction (refs cited in a report).</summary>
    public bool Pin(string reference)
    {
        lock (_gate)
        {
            return _entries.ContainsKey(reference) && _pinned.Add(reference);
        }
    }

    private void Evict(string id)
    {
        var (entry, node) = _entries[id];
        _entries.Remove(id);
        _recency.Remove(node);
        _tombstones[id] = entry.Locator;
        _tombstoneOrder.Enqueue(id);
        while (_tombstoneOrder.Count > Max)
        {
            _tombstones.Remove(_tombstoneOrder.Dequeue());
        }
    }
}

/// <summary>
/// Durable addresses of live values: <c>live://&lt;scene&gt;/&lt;path&gt;#&lt;Component&gt;.&lt;members&gt;</c>,
/// <c>live://ddol/…</c>, <c>live://static/&lt;Type&gt;.&lt;members&gt;</c>, <c>live://asset/&lt;Type&gt;/&lt;name&gt;#&lt;id&gt;</c>.
/// Member paths are written <c>.name</c>, <c>[3]</c> and <c>["key"]</c>.
/// </summary>
public static class Locators
{
    /// <summary>The locator of a type's static member path.</summary>
    public static string Static(Type type, string memberPath) => $"live://static/{type.FullName}{memberPath}";

    /// <summary>The locator of a scene object's member path (<paramref name="owner"/> is the component, or the GameObject).</summary>
    public static string Scene(SceneAddress address, Type owner, string memberPath) => $"live://{address.Scene}/{address.Path}#{owner.FullName}{memberPath}";

    /// <summary>The locator of a loaded asset's member path.</summary>
    public static string Asset(Type type, string? name, long instanceId, string memberPath) =>
        string.Format(CultureInfo.InvariantCulture, "live://asset/{0}/{1}#{2}{3}", type.FullName, name, instanceId, memberPath);

    /// <summary>A member-path segment for a member.</summary>
    public static string Member(string name) => "." + name;

    /// <summary>A member-path segment for a list index.</summary>
    public static string Index(long index) => "[" + index.ToString(CultureInfo.InvariantCulture) + "]";

    /// <summary>A member-path segment for a dictionary key (a JSON string or number).</summary>
    public static string Key(UnityLudometry.Protocol.Json.JsonValue key) => "[" + key + "]";
}

/// <summary>A locator taken apart.</summary>
public sealed class ParsedLocator
{
    /// <summary><c>code</c>, <c>il</c>, <c>live</c>, <c>addr</c>, <c>hit</c> or <c>trace</c>.</summary>
    public string Scheme { get; set; } = string.Empty;

    /// <summary>For <c>live</c>: <c>scene</c>, <c>ddol</c>, <c>static</c> or <c>asset</c>.</summary>
    public string? LiveKind { get; set; }

    /// <summary>The scene name (<c>live://&lt;scene&gt;/…</c>).</summary>
    public string? Scene { get; set; }

    /// <summary>The GameObject path, the static type's full name, or the asset's type.</summary>
    public string? Path { get; set; }

    /// <summary>The component's (or GameObject's) type name, or the asset's name.</summary>
    public string? Owner { get; set; }

    /// <summary>The asset's instance id.</summary>
    public long? InstanceId { get; set; }

    /// <summary>The member path after the owner (names, indexes and keys).</summary>
    public List<MemberPathStep> Members { get; set; } = new();

    /// <summary>For <c>code</c>/<c>il</c>: the assembly name.</summary>
    public string? Assembly { get; set; }

    /// <summary>For <c>code</c>/<c>il</c>: the anchor.</summary>
    public Anchor? Anchor { get; set; }

    /// <summary>For <c>il</c>: the IL offset.</summary>
    public string? IlOffset { get; set; }

    /// <summary>Parses a locator (<c>INVALID_PARAMS</c> when malformed).</summary>
    public static ParsedLocator Parse(string locator, string param)
    {
        var schemeEnd = locator.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
        {
            throw DataErrors.InvalidParams(param, $"'{locator}' isn't a locator.");
        }

        var parsed = new ParsedLocator { Scheme = locator.Substring(0, schemeEnd) };
        var rest = locator.Substring(schemeEnd + 3);
        switch (parsed.Scheme)
        {
            case "code":
            case "il":
                ParseCode(parsed, rest, locator, param);
                break;
            case "live":
                ParseLive(parsed, rest, locator, param);
                break;
            case "addr":
            case "hit":
            case "trace":
                parsed.Path = rest;
                break;
            default:
                throw DataErrors.InvalidParams(param, $"'{parsed.Scheme}' isn't a locator scheme.");
        }

        return parsed;
    }

    private static void ParseCode(ParsedLocator parsed, string rest, string locator, string param)
    {
        var hash = rest.IndexOf('#');
        if (hash >= 0)
        {
            parsed.IlOffset = rest.Substring(hash + 1);
            rest = rest.Substring(0, hash);
        }

        var at = rest.LastIndexOf('@');
        var slash = rest.LastIndexOf('/');
        if (at <= 0 || slash < at || !Guid.TryParse(rest.Substring(at + 1, slash - at - 1), out var mvid)
            || !long.TryParse(rest.Substring(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var token))
        {
            throw DataErrors.InvalidParams(param, $"'{locator}' isn't a code locator (code://<assembly>@<mvid>/<token>).");
        }

        parsed.Assembly = rest.Substring(0, at);
        parsed.Anchor = new Anchor { Mvid = mvid.ToString(), Token = token };
    }

    private static void ParseLive(ParsedLocator parsed, string rest, string locator, string param)
    {
        var slash = rest.IndexOf('/');
        if (slash <= 0)
        {
            throw DataErrors.InvalidParams(param, $"'{locator}' isn't a live locator.");
        }

        var first = rest.Substring(0, slash);
        var tail = rest.Substring(slash + 1);
        switch (first)
        {
            case "static":
                parsed.LiveKind = "static";
                parsed.Path = tail; // "<Type full name>.<members>": split by the resolver, which knows which types exist
                break;
            case "asset":
                parsed.LiveKind = "asset";
                var typeEnd = tail.IndexOf('/');
                var hash = tail.LastIndexOf('#');
                if (typeEnd <= 0 || hash < typeEnd)
                {
                    throw DataErrors.InvalidParams(param, $"'{locator}' isn't an asset locator (live://asset/<Type>/<name>#<instanceId>).");
                }

                parsed.Path = tail.Substring(0, typeEnd);
                parsed.Owner = tail.Substring(typeEnd + 1, hash - typeEnd - 1);
                var idAndMembers = tail.Substring(hash + 1);
                var digits = idAndMembers.TakeWhile((c, i) => char.IsDigit(c) || (i == 0 && c == '-')).Count();
                parsed.InstanceId = long.Parse(idAndMembers.Substring(0, digits), CultureInfo.InvariantCulture);
                parsed.Members = ParseMembers(idAndMembers.Substring(digits), locator, param);
                break;
            default:
                parsed.LiveKind = first == "ddol" ? "ddol" : "scene";
                parsed.Scene = first;
                var sharp = tail.IndexOf('#');
                if (sharp <= 0)
                {
                    throw DataErrors.InvalidParams(param, $"'{locator}' isn't a scene locator (live://<scene>/<path>#<Component>.<members>).");
                }

                parsed.Path = tail.Substring(0, sharp);
                parsed.Owner = tail.Substring(sharp + 1); // "<Component full name>.<members>": split by the resolver
                break;
        }
    }

    /// <summary>Parses <c>.name[3]["key"]</c> into name, index and key steps.</summary>
    public static List<MemberPathStep> ParseMembers(string text, string locator, string param)
    {
        var steps = new List<MemberPathStep>();
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '.')
            {
                var end = i + 1;
                while (end < text.Length && text[end] != '.' && text[end] != '[')
                {
                    end++;
                }

                if (end == i + 1)
                {
                    throw DataErrors.InvalidParams(param, $"'{locator}' has an empty member name.");
                }

                steps.Add(new MemberPathStep { Name = text.Substring(i + 1, end - i - 1) });
                i = end;
            }
            else if (text[i] == '[')
            {
                var close = FindClose(text, i);
                if (close < 0)
                {
                    throw DataErrors.InvalidParams(param, $"'{locator}' has an unclosed '['.");
                }

                var inner = text.Substring(i + 1, close - i - 1);
                if (inner.StartsWith("\"", StringComparison.Ordinal))
                {
                    steps.Add(new MemberPathStep { Key = UnityLudometry.Protocol.Json.JsonValue.Parse(inner) });
                }
                else if (long.TryParse(inner, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                {
                    steps.Add(new MemberPathStep { Index = index });
                }
                else
                {
                    steps.Add(new MemberPathStep { Key = UnityLudometry.Protocol.Json.JsonValue.Parse(inner) });
                }

                i = close + 1;
            }
            else
            {
                throw DataErrors.InvalidParams(param, $"'{locator}': unexpected '{text[i]}' in the member path.");
            }
        }

        return steps;
    }

    private static int FindClose(string text, int open)
    {
        var inString = false;
        for (var i = open + 1; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }
            }
            else if (c == '"')
            {
                inString = true;
            }
            else if (c == ']')
            {
                return i;
            }
        }

        return -1;
    }
}
