using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Abstractions;

namespace UnityRuntimeAnalysisAgent.Core.Data;

/// <summary>
/// The session's live object references. Handles are <b>strong</b> references (weak ones would let short-lived objects
/// vanish between two requests), capped with least-recently-used eviction; handles that variables hold are never evicted.
/// The same object always maps to the same handle while it's referenced. Thread-safe.
/// </summary>
public sealed class HandleTable
{
    private const int TombstoneCap = 10_000;
    private readonly object _gate = new();
    private readonly IUnityApi _unity;
    private readonly Dictionary<long, Entry> _entries = new();
    private readonly Dictionary<object, long> _byObject = new(IdentityComparer.Instance);
    private readonly LinkedList<long> _recency = new();
    private readonly Dictionary<long, (string Type, string? Name)> _tombstones = new();
    private readonly Queue<long> _tombstoneOrder = new();
    private long _next = 1;

    /// <summary>Creates a table holding at most <paramref name="max"/> unpinned handles.</summary>
    public HandleTable(IUnityApi unity, int max)
    {
        _unity = unity;
        Max = max;
    }

    /// <summary>The cap on handles (pinned ones don't count against eviction).</summary>
    public int Max { get; }

    /// <summary>Handles currently held.</summary>
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

    /// <summary>The handle of <paramref name="value"/>, minting one if it has none. Call on the main thread for Unity
    /// objects: their instance id and name are read now, so they can be reported from any thread later.</summary>
    public long Mint(object value) => Mint(value, describe: true);

    /// <summary>Like <see cref="Mint(object)"/>, without calling into Unity (safe mode: values captured inside hooks).</summary>
    public long MintWithoutUnity(object value) => Mint(value, describe: false);

    private long Mint(object value, bool describe)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        var unity = describe && UnityTypes.IsUnityObject(value.GetType()) ? SafeDescribe(value) : null;
        lock (_gate)
        {
            if (_byObject.TryGetValue(value, out var existing))
            {
                var entry = _entries[existing];
                if (unity is not null)
                {
                    entry.Unity = unity;
                }

                Touch(entry);
                return existing;
            }

            var h = _next++;
            var created = new Entry(h, value, unity) { Node = _recency.AddLast(h) };
            _entries.Add(h, created);
            _byObject.Add(value, h);
            Evict();
            return h;
        }
    }

    /// <summary>The object behind <paramref name="h"/>: <c>HANDLE_EXPIRED</c> if it's unknown, released, evicted, or a
    /// destroyed Unity object.</summary>
    public object Resolve(long h)
    {
        Entry? entry;
        lock (_gate)
        {
            if (_entries.TryGetValue(h, out entry))
            {
                Touch(entry);
            }
        }

        if (entry is null)
        {
            throw Expired(h);
        }

        if (entry.IsUnity && _unity.IsDestroyed(entry.Value))
        {
            throw DataErrors.HandleExpired(h, $"Handle {h} refers to a destroyed object.", AnchorWriter.TypeName(entry.Value.GetType()), entry.Unity?.Name);
        }

        return entry.Value;
    }

    /// <summary>Whether <paramref name="h"/> is held (without checking the object's liveness).</summary>
    public bool Contains(long h)
    {
        lock (_gate)
        {
            return _entries.ContainsKey(h);
        }
    }

    /// <summary>The descriptor of a held handle (<c>HANDLE_EXPIRED</c> if it isn't held). Safe from any thread.</summary>
    public HandleDescriptor Describe(long h)
    {
        Entry? entry;
        lock (_gate)
        {
            _entries.TryGetValue(h, out entry);
        }

        return entry is null ? throw Expired(h) : Describe(entry);
    }

    /// <summary>Mints (or finds) the handle of <paramref name="value"/> and describes it.</summary>
    public HandleDescriptor DescribeValue(object value) => Describe(Mint(value));

    /// <summary>Held handles in id order, from <paramref name="after"/> (exclusive), at most <paramref name="limit"/>.</summary>
    public IReadOnlyList<HandleDescriptor> List(long after, int limit, out bool more)
    {
        List<Entry> page;
        lock (_gate)
        {
            var ids = _entries.Keys.Where(h => h > after).OrderBy(h => h).Take(limit + 1).ToList();
            more = ids.Count > limit;
            page = ids.Take(limit).Select(h => _entries[h]).ToList();
        }

        return page.Select(Describe).ToList();
    }

    /// <summary>Releases handles; returns how many were released and which weren't held.</summary>
    public int Release(IEnumerable<long> handles, List<long> unknown)
    {
        var released = 0;
        lock (_gate)
        {
            foreach (var h in handles)
            {
                if (_entries.TryGetValue(h, out var entry))
                {
                    Remove(entry);
                    released++;
                }
                else
                {
                    unknown.Add(h);
                }
            }
        }

        return released;
    }

    /// <summary>Releases every handle, or every handle no variable holds.</summary>
    public int ReleaseAll(bool includePinned)
    {
        lock (_gate)
        {
            var doomed = _entries.Values.Where(e => includePinned || e.Pins == 0).ToList();
            foreach (var entry in doomed)
            {
                Remove(entry);
            }

            return doomed.Count;
        }
    }

    /// <summary>Keeps <paramref name="h"/> from eviction (a variable holds it).</summary>
    public void Pin(long h)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(h, out var entry))
            {
                entry.Pins++;
            }
        }
    }

    /// <summary>Undoes one <see cref="Pin"/>.</summary>
    public void Unpin(long h)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(h, out var entry) && entry.Pins > 0)
            {
                entry.Pins--;
            }
        }
    }

    private HandleDescriptor Describe(Entry entry)
    {
        var type = entry.Value.GetType();
        var descriptor = new HandleDescriptor { H = entry.H, Type = AnchorWriter.CanAnchor(type) ? AnchorWriter.ForType(type) : AnchorWriter.ForType(typeof(object)) };
        if (entry.IsUnity)
        {
            descriptor.Unity = new UnityObjectInfo
            {
                InstanceId = entry.Unity?.InstanceId ?? 0,
                Name = entry.Unity?.Name,
                Destroyed = _unity.IsDestroyed(entry.Value),
            };
        }

        return descriptor;
    }

    private UnityObjectFacts? SafeDescribe(object value)
    {
        try
        {
            return _unity.Describe(value);
        }
        catch (Exception)
        {
            return null; // off the main thread, or destroyed: facts stay unknown
        }
    }

    private Exception Expired(long h)
    {
        lock (_gate)
        {
            return _tombstones.TryGetValue(h, out var last)
                ? DataErrors.HandleExpired(h, $"Handle {h} was released.", last.Type, last.Name)
                : DataErrors.HandleExpired(h, $"Handle {h} is unknown.");
        }
    }

    private void Touch(Entry entry)
    {
        _recency.Remove(entry.Node!);
        _recency.AddLast(entry.Node!);
    }

    private void Evict()
    {
        var node = _recency.First;
        while (_entries.Count > Max && node is not null)
        {
            var next = node.Next;
            var entry = _entries[node.Value];
            if (entry.Pins == 0)
            {
                Remove(entry);
            }

            node = next;
        }
    }

    private void Remove(Entry entry)
    {
        _entries.Remove(entry.H);
        _byObject.Remove(entry.Value);
        _recency.Remove(entry.Node!);
        _tombstones[entry.H] = (AnchorWriter.TypeName(entry.Value.GetType()), entry.Unity?.Name);
        _tombstoneOrder.Enqueue(entry.H);
        while (_tombstoneOrder.Count > TombstoneCap)
        {
            _tombstones.Remove(_tombstoneOrder.Dequeue());
        }
    }

    private sealed class Entry
    {
        public Entry(long h, object value, UnityObjectFacts? unity)
        {
            H = h;
            Value = value;
            Unity = unity;
            IsUnity = UnityTypes.IsUnityObject(value.GetType());
        }

        public long H { get; }

        public object Value { get; }

        public bool IsUnity { get; }

        public UnityObjectFacts? Unity { get; set; }

        public int Pins { get; set; }

        public LinkedListNode<long>? Node { get; set; }
    }
}

/// <summary>What a variable holds.</summary>
public enum VariableKind
{
    /// <summary>A live object, through its handle.</summary>
    Handle,

    /// <summary>The static members of a type.</summary>
    Static,

    /// <summary>A JSON value.</summary>
    Value,
}

/// <summary>A named reference: to an object (its handle), to a type's statics, or to a JSON value.</summary>
public sealed class Variable
{
    internal Variable(string name, VariableKind kind, long h, Anchor? staticAnchor, JsonValue? value)
    {
        Name = name;
        Kind = kind;
        H = h;
        Static = staticAnchor;
        Value = value;
    }

    /// <summary>The name.</summary>
    public string Name { get; }

    /// <summary>What it holds.</summary>
    public VariableKind Kind { get; }

    /// <summary>The handle (for <see cref="VariableKind.Handle"/>).</summary>
    public long H { get; }

    /// <summary>The type (for <see cref="VariableKind.Static"/>).</summary>
    public Anchor? Static { get; }

    /// <summary>The value (for <see cref="VariableKind.Value"/>).</summary>
    public JsonValue? Value { get; }
}

/// <summary>
/// The session's variables (they survive reconnects). A handle-bound variable holds its handle (and so its strong
/// reference) until it's deleted. Thread-safe.
/// </summary>
public sealed class VariableStore
{
    private readonly object _gate = new();
    private readonly HandleTable _handles;
    private readonly Dictionary<string, Variable> _variables = new(StringComparer.Ordinal);

    /// <summary>Creates the store.</summary>
    public VariableStore(HandleTable handles) => _handles = handles;

    /// <summary>Binds <paramref name="name"/> to a handle; returns whether it replaced a binding.</summary>
    public bool SetHandle(string name, long h) => Set(new Variable(name, VariableKind.Handle, h, null, null));

    /// <summary>Binds <paramref name="name"/> to a type's statics.</summary>
    public bool SetStatic(string name, Anchor type) => Set(new Variable(name, VariableKind.Static, 0, type, null));

    /// <summary>Binds <paramref name="name"/> to a JSON value.</summary>
    public bool SetValue(string name, JsonValue value) => Set(new Variable(name, VariableKind.Value, 0, null, value));

    /// <summary>The variable, or <c>NOT_FOUND</c>.</summary>
    public Variable Get(string name, string param)
    {
        lock (_gate)
        {
            return _variables.TryGetValue(name, out var variable) ? variable : throw DataErrors.NotFound(param, $"No variable '{name}'.");
        }
    }

    /// <summary>All variables, by name.</summary>
    public IReadOnlyList<Variable> List()
    {
        lock (_gate)
        {
            return _variables.Values.OrderBy(v => v.Name, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>Deletes a variable (releasing its hold on a handle).</summary>
    public bool Delete(string name)
    {
        lock (_gate)
        {
            if (!_variables.TryGetValue(name, out var variable))
            {
                return false;
            }

            _variables.Remove(name);
            if (variable.Kind == VariableKind.Handle)
            {
                _handles.Unpin(variable.H);
            }

            return true;
        }
    }

    /// <summary>Deletes every handle-bound variable (when all handles are released with their variables).</summary>
    public void DeleteHandleVariables()
    {
        lock (_gate)
        {
            foreach (var name in _variables.Values.Where(v => v.Kind == VariableKind.Handle).Select(v => v.Name).ToList())
            {
                Delete(name);
            }
        }
    }

    private bool Set(Variable variable)
    {
        lock (_gate)
        {
            var replaced = Delete(variable.Name);
            _variables[variable.Name] = variable;
            if (variable.Kind == VariableKind.Handle)
            {
                _handles.Pin(variable.H);
            }

            return replaced;
        }
    }
}

/// <summary>
/// Paging cursors: opaque, session-scoped, and expired after 10 minutes. Thread-safe.
/// </summary>
public sealed class CursorStore
{
    /// <summary>How long a cursor stays valid.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private const int Cap = 10_000;
    private readonly object _gate = new();
    private readonly Func<DateTime> _now;
    private readonly Dictionary<string, (object State, DateTime Expires)> _cursors = new(StringComparer.Ordinal);

    /// <summary>Creates the store (<paramref name="now"/> is the clock).</summary>
    public CursorStore(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.UtcNow);

    /// <summary>A new cursor for <paramref name="state"/>.</summary>
    public string Mint(object state)
    {
        var bytes = new byte[12];
        using (var random = RandomNumberGenerator.Create())
        {
            random.GetBytes(bytes);
        }

        var cursor = "c" + BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
        lock (_gate)
        {
            Prune();
            _cursors[cursor] = (state, _now() + Lifetime);
        }

        return cursor;
    }

    /// <summary>The state behind a cursor: <c>INVALID_PARAMS</c> if it's unknown, expired or of another kind.</summary>
    public T Take<T>(string cursor, string param)
    {
        lock (_gate)
        {
            Prune();
            if (_cursors.TryGetValue(cursor, out var entry) && entry.State is T state)
            {
                return state;
            }
        }

        throw UnityLudometry.Protocol.ProtocolException.InvalidParams(param, $"{param} is unknown or expired (cursors last {Lifetime.TotalMinutes:0} minutes): start again without it.");
    }

    private void Prune()
    {
        var now = _now();
        foreach (var key in _cursors.Where(c => c.Value.Expires <= now).Select(c => c.Key).ToList())
        {
            _cursors.Remove(key);
        }

        foreach (var key in _cursors.OrderBy(c => c.Value.Expires).Take(Math.Max(0, _cursors.Count - Cap)).Select(c => c.Key).ToList())
        {
            _cursors.Remove(key);
        }
    }
}
