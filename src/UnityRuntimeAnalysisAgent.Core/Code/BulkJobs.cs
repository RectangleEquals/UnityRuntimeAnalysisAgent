using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Abstractions;
using UnityRuntimeAnalysisAgent.Core.Data;
using UnityRuntimeAnalysisAgent.Core.Jobs;

namespace UnityRuntimeAnalysisAgent.Core.Code;

/// <summary>Reuses a finished output file whose header carries the same cache key and whose footer checksum still holds.</summary>
public static class OutputCache
{
    /// <summary>A key over everything that determines a file's content.</summary>
    public static string Key(params object[] parts)
    {
        using var sha = SHA256.Create();
        return AssemblyCatalog.Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("|", parts))));
    }

    /// <summary>The file's description when it can be reused, or null.</summary>
    public static OutputFile? TryReuse(string path, string key)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var lines = File.ReadAllLines(path, new UTF8Encoding(false));
            if (lines.Length < 2 || JsonValue.Parse(lines[0]) is not JsonObject header || JsonValue.Parse(lines[lines.Length - 1]) is not JsonObject footer
                || (header["source"] as JsonObject)?["cacheKey"] is not JsonString { Value: var cached } || cached != key
                || footer["sha256"] is not JsonString { Value: var sha256 } || footer["counts"] is not JsonObject counts)
            {
                return null;
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (var i = 0; i < lines.Length - 1; i++)
            {
                hash.AppendData(Encoding.UTF8.GetBytes(lines[i] + "\n"));
            }

            return AssemblyCatalog.Hex(hash.GetHashAndReset()) == sha256
                ? new OutputFile { Path = path, Bytes = new FileInfo(path).Length, Sha256 = sha256, Counts = counts }
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The assemblies a file covers, for its header: <c>[{name, mvid}]</c>.</summary>
    public static JsonArray Assemblies(IEnumerable<Assembly> assemblies) => new(assemblies.Select(a => (JsonValue?)new JsonObject
    {
        { "name", JsonValue.From(AssemblyCatalog.Name(a)) },
        { "mvid", JsonValue.From(a.ManifestModule.ModuleVersionId.ToString()) },
    }));
}

/// <summary>Anchors for records, cached while one assembly is written (the same members are referenced over and over).</summary>
internal sealed class RecordAnchors
{
    private readonly Dictionary<MemberInfo, JsonValue?> _cache = new();

    public int Skipped { get; private set; }

    public void Clear() => _cache.Clear();

    /// <summary>The anchor, or null (counted) for members and types without a definition: array methods, generic parameters.</summary>
    public JsonValue? Of(MemberInfo member)
    {
        if (!_cache.TryGetValue(member, out var anchor))
        {
            anchor = Make(member);
            _cache[member] = anchor;
        }

        if (anchor is null)
        {
            Skipped++;
        }

        return anchor;
    }

    private static JsonValue? Make(MemberInfo member)
    {
        try
        {
            if (member is Type type)
            {
                while (type.IsArray || type.IsByRef || type.IsPointer)
                {
                    type = type.GetElementType()!;
                }

                return AnchorWriter.CanAnchor(type) ? AnchorWriter.ToJson(AnchorWriter.ForType(type)) : null;
            }

            return member.DeclaringType is { } declaring && !AnchorWriter.CanAnchor(declaring) ? null : AnchorWriter.ToJson(AnchorWriter.ForMember(member));
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>The <c>il.index</c> job: the runtime cross-reference index as NDJSON.</summary>
public sealed class IlIndexJob
{
    /// <summary>The schema version of the file.</summary>
    public const string SchemaVersion = "1";

    private static readonly string[] AllRecords = { "calls", "fieldAccess", "strings", "allocations", "typeRefs" };
    private readonly AssemblyCatalog _catalog;
    private readonly string _agentVersion;

    /// <summary>Creates the job runner.</summary>
    public IlIndexJob(AssemblyCatalog catalog, string agentVersion)
    {
        _catalog = catalog;
        _agentVersion = agentVersion;
    }

    /// <summary>Runs the job (on the job's worker thread).</summary>
    public ProtocolMessage Run(JobContext job, IlIndexStartParams p)
    {
        var clock = Stopwatch.StartNew();
        var records = (p.Records is { Count: > 0 } requested ? requested : AllRecords.ToList()).Distinct().OrderBy(r => r, StringComparer.Ordinal).ToList();
        var tokens = records.Contains("tokens");
        var assemblies = _catalog.Assemblies(new AssemblyFilter(p.Include, p.Exclude, builtInExclusions: true)).Where(a => !a.IsDynamic).ToList();
        var key = OutputCache.Key("il_index", SchemaVersion, string.Join(",", records), string.Join(",", assemblies.Select(a => AssemblyCatalog.Name(a) + "@" + a.ManifestModule.ModuleVersionId)));
        if (OutputCache.TryReuse(p.OutFile, key) is { } reused)
        {
            return new IlIndexStartJobResult { File = reused, Cached = true, DurationMs = clock.ElapsedMilliseconds };
        }

        var source = new JsonObject
        {
            { "cacheKey", JsonValue.From(key) },
            { "records", new JsonArray(records.Select(r => (JsonValue?)JsonValue.From(r))) },
            { "assemblies", OutputCache.Assemblies(assemblies) },
        };
        using var writer = new NdjsonFileWriter(p.OutFile, "il_index", SchemaVersion, _agentVersion, source);
        var anchors = new RecordAnchors();
        for (var i = 0; i < assemblies.Count; i++)
        {
            job.Progress("assemblies", i, assemblies.Count, AssemblyCatalog.Name(assemblies[i]));
            anchors.Clear();
            foreach (var module in assemblies[i].GetModules())
            {
                foreach (var method in XrefScanner.Methods(module))
                {
                    job.Cancellation.ThrowIfCancellationRequested();
                    var methodAnchor = anchors.Of(method);
                    if (methodAnchor is null)
                    {
                        continue;
                    }

                    XrefScanner.Scan(method, xref => Write(writer, xref, methodAnchor, records, tokens, anchors), reason => writer.Write(new JsonObject
                    {
                        { "rec", JsonValue.From("error") },
                        { "method", methodAnchor },
                        { "reason", JsonValue.From(reason) },
                    }));
                }
            }
        }

        job.Progress("assemblies", assemblies.Count, assemblies.Count);
        var result = new IlIndexStartJobResult { File = writer.Complete(), Cached = false, DurationMs = clock.ElapsedMilliseconds };
        if (anchors.Skipped > 0)
        {
            result.Extra = new JsonObject { { "skipped", new JsonObject { { "operandsWithoutDefinition", JsonValue.From(anchors.Skipped) } } } };
        }

        return result;
    }

    private static void Write(NdjsonFileWriter writer, Xref xref, JsonValue method, List<string> records, bool tokens, RecordAnchors anchors)
    {
        JsonObject? record = null;
        switch (xref.Kind)
        {
            case XrefKind.Call when records.Contains("calls") && anchors.Of(xref.Target!) is { } callee:
                record = new JsonObject { { "rec", JsonValue.From("call") }, { "caller", method }, { "callee", callee }, { "opcode", JsonValue.From(xref.Opcode) }, { "offset", JsonValue.From(xref.Offset) } };
                break;
            case XrefKind.Field when records.Contains("fieldAccess") && anchors.Of(xref.Target!) is { } field:
                record = new JsonObject
                {
                    { "rec", JsonValue.From("field") }, { "method", method }, { "field", field }, { "access", JsonValue.From(xref.Access!) },
                    { "static", JsonValue.From(xref.Static) }, { "offset", JsonValue.From(xref.Offset) },
                };
                break;
            case XrefKind.String when records.Contains("strings"):
                record = new JsonObject { { "rec", JsonValue.From("string") }, { "method", method }, { "literal", JsonValue.From(xref.Literal!) }, { "offset", JsonValue.From(xref.Offset) } };
                break;
            case XrefKind.Alloc when records.Contains("allocations") && anchors.Of(xref.Target!) is { } allocated:
                record = new JsonObject { { "rec", JsonValue.From("alloc") }, { "method", method }, { "type", allocated }, { "opcode", JsonValue.From(xref.Opcode) }, { "offset", JsonValue.From(xref.Offset) } };
                break;
            case XrefKind.TypeRef when records.Contains("typeRefs") && anchors.Of(xref.Target!) is { } referenced:
                record = new JsonObject { { "rec", JsonValue.From("typeref") }, { "method", method }, { "type", referenced }, { "usage", JsonValue.From(xref.Access!) }, { "offset", JsonValue.From(xref.Offset) } };
                break;
        }

        if (record is not null)
        {
            if (tokens)
            {
                record.Add("token", JsonValue.From(xref.Token));
            }

            writer.Write(record);
        }
    }
}

/// <summary>What the survey was asked for.</summary>
public sealed class SurveyRequest
{
    /// <summary>The parameters.</summary>
    public SurveyStartParams Params { get; set; } = new();

    /// <summary>Count instances of every component and ScriptableObject type.</summary>
    public bool CountAll { get; set; }

    /// <summary>Count instances of these types.</summary>
    public List<Type> CountTypes { get; set; } = new();

    /// <summary>Reads <c>countInstances</c> (<c>false</c>, <c>true</c> or type anchors; <c>INVALID_PARAMS</c> otherwise).</summary>
    public static SurveyRequest From(SurveyStartParams p, AnchorResolver anchors)
    {
        var request = new SurveyRequest { Params = p };
        switch (p.CountInstances)
        {
            case null or JsonNull or JsonBoolean { Value: false }:
                break;
            case JsonBoolean { Value: true }:
                request.CountAll = true;
                break;
            case JsonArray types:
                request.CountTypes = types.Select((t, i) => anchors.ResolveTypeRef(t, $"params.countInstances[{i}]")).ToList();
                break;
            default:
                throw ProtocolException.InvalidParams("params.countInstances", "params.countInstances must be false, true or an array of type anchors.");
        }

        return request;
    }
}

/// <summary>
/// The <c>survey</c> job: a catalogue of the included assemblies' code as NDJSON (assemblies, types, members with IL
/// hashes, Unity messages, serialized fields, custom-serializer markers, statics that look like singletons, instance
/// counts), in stable order (assembly name, then token). Metadata only on the worker; instance counts on the main thread
/// in small batches.
/// </summary>
public sealed class SurveyJob
{
    /// <summary>The schema version of the file.</summary>
    public const string SchemaVersion = "1";

    private static readonly HashSet<string> InstanceNames = new(StringComparer.Ordinal) { "Instance", "instance", "_instance", "s_instance", "s_Instance", "m_Instance", "Singleton" };
    private readonly AssemblyCatalog _catalog;
    private readonly Func<string, Type?> _findType;
    private readonly IUnityApi _unity;
    private readonly string _agentVersion;
    private readonly string? _unityVersion;
    private long _typeTicks;
    private long _memberTicks;
    private long _ilTicks;
    private long _attributeTicks;
    private long _instanceTicks;
    private long _writeTicks;

    /// <summary>Creates the job runner (<paramref name="findType"/> finds a loaded type by full name, for Unity's types).</summary>
    public SurveyJob(AssemblyCatalog catalog, Func<string, Type?> findType, IUnityApi unity, string agentVersion, string? unityVersion)
    {
        _catalog = catalog;
        _findType = findType;
        _unity = unity;
        _agentVersion = agentVersion;
        _unityVersion = unityVersion;
    }

    /// <summary>Runs the job (on the job's worker thread).</summary>
    public ProtocolMessage Run(JobContext job, SurveyRequest request)
    {
        var clock = Stopwatch.StartNew();
        var p = request.Params;
        (_typeTicks, _memberTicks, _ilTicks, _attributeTicks, _instanceTicks, _writeTicks) = (0, 0, 0, 0, 0, 0);
        var rules = new UnityRules(new[] { _findType("UnityEngine.MonoBehaviour"), _findType("UnityEngine.ScriptableObject") }.Where(t => t is not null)!, _unityVersion, p.Messages);
        var markers = (Attributes: p.SerializerMarkers?.Attributes ?? new List<string>(), BaseTypes: p.SerializerMarkers?.BaseTypes ?? new List<string>());
        var assemblies = _catalog.Assemblies(new AssemblyFilter(p.Include, p.Exclude, builtInExclusions: true)).Where(a => !a.IsDynamic).ToList();
        var counting = request.CountAll || request.CountTypes.Count > 0;
        var key = OutputCache.Key("survey", SchemaVersion, _unityVersion ?? "?", string.Join(",", assemblies.Select(a => AssemblyCatalog.Name(a) + "@" + a.ManifestModule.ModuleVersionId)),
            string.Join(",", markers.Attributes), string.Join(",", markers.BaseTypes), string.Join(",", p.Messages ?? new List<string>()));
        if (!counting && p.Force != true && OutputCache.TryReuse(p.OutFile, key) is { } reused)
        {
            return new SurveyStartJobResult { File = reused, DurationMs = clock.ElapsedMilliseconds, Extra = new JsonObject { { "cached", JsonValue.From(true) } } };
        }

        var warnings = new JsonArray();
        if (!rules.Available)
        {
            warnings.Add(JsonValue.From("UnityEngine isn't loaded: Unity messages, serialized fields and instance counts were skipped."));
        }

        var source = new JsonObject
        {
            { "cacheKey", JsonValue.From(key) },
            { "unityVersion", _unityVersion is null ? JsonNull.Instance : JsonValue.From(_unityVersion) },
            { "assemblies", OutputCache.Assemblies(assemblies) },
            { "warnings", warnings },
        };
        using var writer = new NdjsonFileWriter(p.OutFile, "survey", SchemaVersion, _agentVersion, source);
        var toCount = new List<Type>(request.CountTypes);
        for (var i = 0; i < assemblies.Count; i++)
        {
            job.Progress("assemblies", i, assemblies.Count, AssemblyCatalog.Name(assemblies[i]));
            WriteAssembly(job, writer, assemblies[i], rules, markers, request.CountAll && rules.Available ? toCount : null);
        }

        if (rules.Available && toCount.Count > 0)
        {
            CountInstances(job, writer, toCount);
        }

        job.Progress("done", assemblies.Count, assemblies.Count);
        static long Ms(long ticks) => ticks * 1000 / Stopwatch.Frequency;
        return new SurveyStartJobResult
        {
            File = writer.Complete(),
            DurationMs = clock.ElapsedMilliseconds,
            Extra = new JsonObject
            {
                {
                    "phasesMs", new JsonObject
                    {
                        { "types", JsonValue.From(Ms(_typeTicks)) }, { "members", JsonValue.From(Ms(_memberTicks)) }, { "ilHashes", JsonValue.From(Ms(_ilTicks)) },
                        { "attributes", JsonValue.From(Ms(_attributeTicks)) }, { "instances", JsonValue.From(Ms(_instanceTicks)) },
                        { "memberWrites", JsonValue.From(Ms(_writeTicks)) },
                    }
                },
            },
        };
    }

    private void WriteAssembly(JobContext job, NdjsonFileWriter writer, Assembly assembly, UnityRules rules, (List<string> Attributes, List<string> BaseTypes) markers, List<Type>? toCount)
    {
        var name = AssemblyCatalog.Name(assembly);
        var loadErrors = new List<string>();
        var types = new List<Type>();
        foreach (var module in assembly.GetModules())
        {
            try
            {
                types.AddRange(module.GetTypes());
            }
            catch (ReflectionTypeLoadException e)
            {
                types.AddRange(e.Types.Where(t => t is not null)!);
                loadErrors.AddRange(e.LoaderExceptions.Where(x => x is not null).Select(x => x!.Message).Distinct());
            }
        }

        var summary = _catalog.Summary(assembly);
        var record = new JsonObject
        {
            { "rec", JsonValue.From("assembly") }, { "name", JsonValue.From(name) }, { "version", JsonValue.From(summary.Version) },
            { "mvid", JsonValue.From(summary.Mvid) }, { "location", summary.Location is null ? JsonNull.Instance : JsonValue.From(summary.Location) },
            { "referencedAssemblies", new JsonArray(summary.ReferencedAssemblies.Select(r => (JsonValue?)JsonValue.From(r))) },
        };
        if (loadErrors.Count > 0)
        {
            record.Add("loadErrors", new JsonArray(loadErrors.Select(e => (JsonValue?)JsonValue.From(e))));
        }

        writer.Write(record);
        foreach (var error in loadErrors)
        {
            writer.Write(Error("assembly", name, error));
        }

        foreach (var type in types.OrderBy(t => t.MetadataToken))
        {
            job.Cancellation.ThrowIfCancellationRequested();
            try
            {
                var started = Stopwatch.GetTimestamp();
                WriteType(writer, type, rules, markers);
                _typeTicks += Stopwatch.GetTimestamp() - started;
                if (toCount is not null && !type.IsAbstract && !type.ContainsGenericParameters && (UnityRules.IsComponent(type) || rules.IsScriptableObject(type)))
                {
                    toCount.Add(type);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                writer.Write(Error("type", SafeName(type), e.GetBaseException().Message));
            }
        }
    }

    private void WriteType(NdjsonFileWriter writer, Type type, UnityRules rules, (List<string> Attributes, List<string> BaseTypes) markers)
    {
        var anchor = AnchorWriter.ToJson(AnchorWriter.ForType(type));
        var flags = new JsonArray();
        if (UnityRules.IsComponent(type))
        {
            flags.Add(JsonValue.From("unityComponent"));
        }

        if (rules.IsScriptableObject(type))
        {
            flags.Add(JsonValue.From("scriptableObject"));
        }

        if (UnityRules.IsSerializableAttributed(type))
        {
            flags.Add(JsonValue.From("serializable"));
        }

        if (type.IsAbstract && !type.IsInterface && !type.IsSealed)
        {
            flags.Add(JsonValue.From("abstract"));
        }

        if (type.IsAbstract && type.IsSealed)
        {
            flags.Add(JsonValue.From("static"));
        }

        var record = new JsonObject { { "rec", JsonValue.From("type") }, { "anchor", anchor }, { "fullName", JsonValue.From(AnchorWriter.TypeName(type)) }, { "kind", JsonValue.From(Describe.TypeKind(type)) } };
        if (type.BaseType is { } baseType && AnchorWriter.CanAnchor(baseType))
        {
            record.Add("base", AnchorWriter.ToJson(AnchorWriter.ForType(baseType)));
        }

        record.Add("interfaces", new JsonArray(Safe(() => type.GetInterfaces(), Array.Empty<Type>()).Where(AnchorWriter.CanAnchor).Select(i => (JsonValue?)AnchorWriter.ToJson(AnchorWriter.ForType(i)))));
        if (type.IsGenericTypeDefinition)
        {
            record.Add("genericParams", new JsonArray(type.GetGenericArguments().Select(g => (JsonValue?)JsonValue.From(g.Name))));
        }

        record.Add("attributes", Names(Describe.AttributeNames(type)));
        record.Add("flags", flags);
        writer.Write(record);

        var members = type.GetMembers(Describe.Declared).Where(m => m is not Type).OrderBy(m => m.MetadataToken).ToList();
        foreach (var member in members)
        {
            try
            {
                var started = Stopwatch.GetTimestamp();
                var memberRecord = Member(member, anchor);
                var writing = Stopwatch.GetTimestamp();
                writer.Write(memberRecord);
                _writeTicks += Stopwatch.GetTimestamp() - writing;
                _memberTicks += Stopwatch.GetTimestamp() - started;
            }
            catch (Exception e)
            {
                writer.Write(Error("member", AnchorWriter.MemberName(member), e.GetBaseException().Message));
            }
        }

        if (rules.Available && (rules.IsMonoBehaviour(type) || rules.IsScriptableObject(type)))
        {
            foreach (var method in members.OfType<MethodInfo>().Where(m => !m.IsStatic && rules.IsMessage(m.Name)))
            {
                writer.Write(new JsonObject { { "rec", JsonValue.From("unity_message") }, { "type", anchor }, { "method", AnchorWriter.ToJson(AnchorWriter.ForMember(method)) }, { "message", JsonValue.From(method.Name) } });
            }
        }

        var fields = members.OfType<FieldInfo>().ToList();
        var unitySerialized = new HashSet<FieldInfo>();
        if (rules.Available && rules.SerializesFieldsOf(type))
        {
            foreach (var field in fields)
            {
                if (UnityRules.Rule(field) is not { } rule)
                {
                    continue;
                }

                var verdict = rules.Verdict(field, rule);
                if (verdict == "serialized")
                {
                    unitySerialized.Add(field);
                }

                writer.Write(new JsonObject
                {
                    { "rec", JsonValue.From("serialized_field") }, { "type", anchor }, { "field", AnchorWriter.ToJson(AnchorWriter.ForMember(field)) }, { "rule", JsonValue.From(rule) },
                    { "fieldType", JsonValue.From(AnchorWriter.TypeName(field.FieldType)) }, { "verdict", JsonValue.From(verdict) },
                });
            }
        }

        WriteCustomSerializers(writer, type, anchor, fields, unitySerialized, markers);
        foreach (var member in members.Where(m => m is FieldInfo { IsStatic: true, IsLiteral: false } || m is PropertyInfo p && p.CanRead && Describe.IsStatic(p)))
        {
            if (member.Name.StartsWith("<", StringComparison.Ordinal))
            {
                continue; // compiler-generated caches
            }

            var memberType = member is FieldInfo f ? f.FieldType : ((PropertyInfo)member).PropertyType;
            var reason = SingletonReason(type, member, memberType);
            writer.Write(new JsonObject
            {
                { "rec", JsonValue.From("static") }, { "member", AnchorWriter.ToJson(AnchorWriter.ForMember(member)) }, { "kind", JsonValue.From(Describe.MemberKind(member)) },
                { "type", JsonValue.From(AnchorWriter.TypeName(memberType)) },
                { "readOnly", JsonValue.From(member is FieldInfo field ? field.IsInitOnly : !((PropertyInfo)member).CanWrite) },
                { "singletonCandidate", JsonValue.From(reason is not null) },
            }.With("reason", reason));
        }
    }

    private static void WriteCustomSerializers(NdjsonFileWriter writer, Type type, JsonValue anchor, List<FieldInfo> fields, HashSet<FieldInfo> unitySerialized,
        (List<string> Attributes, List<string> BaseTypes) markers)
    {
        if (markers.Attributes.Count == 0 && markers.BaseTypes.Count == 0)
        {
            return;
        }

        var typeMarker = markers.Attributes.FirstOrDefault(a => Describe.HasAttribute(type, a)) ?? BaseMarker(type, markers.BaseTypes);
        if (typeMarker is not null)
        {
            // What the custom serializer adds: instance fields Unity itself leaves out.
            var affected = fields.Where(f => !f.IsStatic && !f.IsLiteral && !unitySerialized.Contains(f) && !f.IsNotSerialized).Select(f => (JsonValue?)AnchorWriter.ToJson(AnchorWriter.ForMember(f)));
            writer.Write(new JsonObject { { "rec", JsonValue.From("custom_serializer") }, { "type", anchor }, { "marker", JsonValue.From(typeMarker) }, { "members", new JsonArray(affected) } });
        }

        foreach (var marker in markers.Attributes.Where(m => m != typeMarker)) // a marked type's record already lists its fields
        {
            var marked = fields.Where(f => Describe.HasAttribute(f, marker)).ToList();
            if (marked.Count > 0)
            {
                writer.Write(new JsonObject { { "rec", JsonValue.From("custom_serializer") }, { "type", anchor }, { "marker", JsonValue.From(marker) },
                    { "members", new JsonArray(marked.Select(f => (JsonValue?)AnchorWriter.ToJson(AnchorWriter.ForMember(f)))) } });
            }
        }
    }

    private static string? BaseMarker(Type type, List<string> baseTypes)
    {
        for (var t = type.BaseType; t is not null; t = t.BaseType)
        {
            var name = TypeIndex.Key(t).FullName;
            if (name is not null && baseTypes.Contains(name))
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>Why a static member looks like a singleton (self-typed, a generic singleton base, "Instance" naming), or null.</summary>
    public static string? SingletonReason(Type declaring, MemberInfo member, Type memberType)
    {
        if (memberType == declaring)
        {
            return "self-typed static";
        }

        if (declaring.IsGenericTypeDefinition && memberType.IsGenericParameter && declaring.GetGenericArguments().Contains(memberType))
        {
            return "generic singleton base";
        }

        return InstanceNames.Contains(member.Name) && !memberType.IsValueType && memberType != typeof(string) ? "Instance naming" : null;
    }

    // One Resources.FindObjectsOfTypeAll scan per base type (Component, ScriptableObject, or the type itself), counted by
    // exact type; each asked-for type's count is the sum over its subtypes. Scanning per type would scan every object once
    // per type, on the main thread: that hitches the game.
    private void CountInstances(JobContext job, NdjsonFileWriter writer, List<Type> types)
    {
        var started = Stopwatch.GetTimestamp();
        var bases = new[] { _findType("UnityEngine.Component"), _findType("UnityEngine.ScriptableObject") }.Where(t => t is not null).Cast<Type>().ToList();
        var byBase = types.GroupBy(t => bases.FirstOrDefault(b => b.IsAssignableFrom(t)) ?? t).ToList();
        var exact = new Dictionary<Type, int>();
        var failed = new HashSet<Type>();
        foreach (var group in byBase)
        {
            job.Cancellation.ThrowIfCancellationRequested();
            job.Progress("instances", exact.Count, types.Count, group.Key.FullName);
            try
            {
                foreach (var pair in job.RunOnMain(() => _unity.CountObjectsByType(group.Key)))
                {
                    exact[pair.Key] = pair.Value;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failed.UnionWith(group);
            }
        }

        foreach (var type in types)
        {
            if (failed.Contains(type) || !AnchorWriter.CanAnchor(type))
            {
                writer.Write(Error("type", SafeName(type), "Its instances couldn't be counted."));
                continue;
            }

            var count = exact.Where(e => type.IsAssignableFrom(e.Key)).Sum(e => e.Value);
            writer.Write(new JsonObject { { "rec", JsonValue.From("instance_count") }, { "type", AnchorWriter.ToJson(AnchorWriter.ForType(type)) }, { "count", JsonValue.From(count) }, { "includesAssets", JsonValue.From(true) } });
        }

        _instanceTicks += Stopwatch.GetTimestamp() - started;
    }

    private JsonObject Member(MemberInfo member, JsonValue declaring)
    {
        var attributeStart = Stopwatch.GetTimestamp();
        var attributes = Names(Describe.AttributeNames(member));
        _attributeTicks += Stopwatch.GetTimestamp() - attributeStart;
        var record = new JsonObject
        {
            { "rec", JsonValue.From("member") }, { "anchor", AnchorWriter.ToJson(AnchorWriter.ForMember(member)) }, { "declaringType", declaring },
            { "kind", JsonValue.From(Describe.MemberKind(member)) }, { "name", JsonValue.From(member.Name) }, { "signature", JsonValue.From(Describe.Signature(member)) },
            { "static", JsonValue.From(Describe.IsStatic(member)) }, { "visibility", JsonValue.From(Describe.Visibility(member)) },
            { "attributes", attributes },
        };
        switch (member)
        {
            case FieldInfo field:
                record.Add("fieldType", JsonValue.From(AnchorWriter.TypeName(field.FieldType)));
                record.Add("readOnly", JsonValue.From(field.IsInitOnly));
                record.Add("literal", JsonValue.From(field.IsLiteral));
                break;
            case MethodBase method:
                var ilStart = Stopwatch.GetTimestamp();
                var il = IlReader.Body(method);
                record.Add("ilSize", JsonValue.From(il?.Length ?? 0));
                if (il is not null)
                {
                    record.Add("ilHash", JsonValue.From(IlReader.Hash(il)));
                }

                _ilTicks += Stopwatch.GetTimestamp() - ilStart;

                record.Add("virtual", JsonValue.From(method.IsVirtual));
                record.Add("abstract", JsonValue.From(method.IsAbstract));
                record.Add("override", JsonValue.From(method is MethodInfo mi && mi.IsVirtual && Safe(() => mi.GetBaseDefinition() != mi, false)));
                record.Add("generic", JsonValue.From(method.IsGenericMethodDefinition));
                record.Add("hasBody", JsonValue.From(il is not null));
                record.Add("implFlags", JsonValue.From(method.GetMethodImplementationFlags().ToString()));
                break;
            case PropertyInfo property:
                record.With("getter", property.GetGetMethod(true) is { } get ? AnchorWriter.ToJson(AnchorWriter.ForMember(get)) : null);
                record.With("setter", property.GetSetMethod(true) is { } set ? AnchorWriter.ToJson(AnchorWriter.ForMember(set)) : null);
                break;
            case EventInfo ev:
                record.With("adder", ev.GetAddMethod(true) is { } add ? AnchorWriter.ToJson(AnchorWriter.ForMember(add)) : null);
                record.With("remover", ev.GetRemoveMethod(true) is { } remove ? AnchorWriter.ToJson(AnchorWriter.ForMember(remove)) : null);
                break;
        }

        return record;
    }

    private static JsonObject Error(string scope, string subject, string message) => new()
    {
        { "rec", JsonValue.From("error") }, { "scope", JsonValue.From(scope) }, { "subject", JsonValue.From(subject) }, { "message", JsonValue.From(message) },
    };

    private static JsonArray Names(IEnumerable<string> names) => new(names.Select(n => (JsonValue?)JsonValue.From(n)));

    private static string SafeName(Type type)
    {
        try
        {
            return AnchorWriter.TypeName(type);
        }
        catch (Exception)
        {
            return type.Name;
        }
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return fallback;
        }
    }
}

internal static class JsonObjectExtensions
{
    /// <summary>Adds a property when the value isn't null; returns the object.</summary>
    public static JsonObject With(this JsonObject obj, string name, object? value)
    {
        switch (value)
        {
            case null:
                break;
            case JsonValue json:
                obj.Add(name, json);
                break;
            case string s:
                obj.Add(name, JsonValue.From(s));
                break;
        }

        return obj;
    }
}
