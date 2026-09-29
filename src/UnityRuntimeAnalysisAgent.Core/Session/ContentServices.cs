using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Abstractions;
using UnityRuntimeAnalysisAgent.Core.Code;
using UnityRuntimeAnalysisAgent.Core.Content;
using UnityRuntimeAnalysisAgent.Core.Data;
using UnityRuntimeAnalysisAgent.Core.Dispatch;
using UnityRuntimeAnalysisAgent.Core.Jobs;
using UnityRuntimeAnalysisAgent.Core.Runtime;

namespace UnityRuntimeAnalysisAgent.Core.Session;

/// <summary>
/// Content: what's loaded (with type-specific summaries), what the runtime catalogs say exists (Addressables, AssetBundles),
/// loading more (in <c>ReadOnly+Load</c> or <c>Full</c>), exporting what the running game holds (the pixels of
/// textures in use, objects' data, text assets) to files, and the content scan. Reading and
/// loading run on the main thread; exports and scans are jobs that read on the main thread in small pieces and encode and
/// write on a worker.
/// </summary>
internal sealed class ContentServices
{
    private const int DefaultPage = 100;
    private const int MaxPage = 5000;
    private const int ScanChunk = 200;
    private const int MaxScanLocations = 200;
    private const int MaxScanDependencies = 64;
    private const string FileSchemaVersion = "1";
    private readonly DataModel _data;
    private readonly CodeModel _code;
    private readonly MainThreadPump _pump;
    private readonly JobManager _jobs;
    private readonly string _agentVersion;
    private readonly Dictionary<long, object> _operations = new();
    private long _nextOperation;

    public ContentServices(DataModel data, CodeModel code, MainThreadPump pump, JobManager jobs, string agentVersion)
    {
        _data = data;
        _code = code;
        _pump = pump;
        _jobs = jobs;
        _agentVersion = agentVersion;
    }

    private IUnityApi Unity => _data.Unity;

    private IContentApi Content => Unity.Content ?? throw DataErrors.Unsupported("Content needs the Unity runtime.");

    private IAddressablesApi Addressables => Content.Addressables ?? throw DataErrors.Unsupported("This game doesn't use Addressables.");

    private long Frame => _pump.Clock.FrameCount;

    private long RealtimeMs => (long)(_pump.Clock.Realtime * 1000);

    // ---- loaded content ---------------------------------------------------------------------------------------------------

    [RpcMethod(Methods.ContentList)]
    public ProtocolMessage List(RequestContext context, ContentListParams p)
    {
        List<object> matching;
        int offset;
        if (p.Cursor is not null)
        {
            var page = _data.Cursors.Take<ContentPage>(p.Cursor, "params.cursor");
            (matching, offset) = (page.Objects, page.Offset);
        }
        else
        {
            var type = ResolveType(p.Type, "params.type");
            var nameRegex = p.NameRegex is null ? null : Pattern(p.NameRegex, "params.nameRegex");
            matching = Loaded(type)
                .Where(o => p.IncludeHidden == true || (Content.HideFlags(o) & 1) == 0) // HideInHierarchy: engine-internal objects
                .Select(o => (Object: o, Facts: Unity.Describe(o)))
                .Where(x => x.Facts is not null && (nameRegex is null || nameRegex.IsMatch(x.Facts.Value.Name)))
                .OrderBy(x => x.Facts!.Value.Name, StringComparer.Ordinal).ThenBy(x => x.Facts!.Value.InstanceId)
                .Select(x => x.Object).ToList();
            offset = 0;
        }

        var limit = Limit(p.Limit);
        var items = new List<ContentItem>();
        var encoder = new SummaryEncoder(o => _data.Handles.DescribeValue(o).ToJson());
        foreach (var value in matching.Skip(offset).Take(limit))
        {
            if (Unity.Describe(value) is not { } facts)
            {
                continue; // destroyed since the first page
            }

            items.Add(new ContentItem
            {
                H = _data.Handles.Mint(value),
                Name = facts.Name ?? string.Empty,
                Type = AnchorWriter.ForType(value.GetType()),
                InstanceId = facts.InstanceId,
                Summary = Summarize(value, encoder),
                Locator = _data.Targets.LocatorBaseOf(value) ?? Locators.Asset(value.GetType(), facts.Name, facts.InstanceId, string.Empty),
            });
        }

        var next = offset + limit;
        return new ContentListResult
        {
            Items = items,
            Cursor = next < matching.Count ? _data.Cursors.Mint(new ContentPage(matching, next)) : null,
            Total = matching.Count,
        };
    }

    [RpcMethod(Methods.ContentSummary, DefaultTimeoutMs = 60_000)]
    public ProtocolMessage Summary(RequestContext context, ContentSummaryParams p)
    {
        var items = new List<ContentTypeSummary>();
        if (p.Types is not { Count: > 0 })
        {
            // Every loaded type, counted in one scan; memory is only estimated for the types asked about.
            foreach (var pair in Unity.CountObjectsByType(UnityObjectType()).OrderBy(x => x.Key.FullName, StringComparer.Ordinal))
            {
                items.Add(new ContentTypeSummary { Type = pair.Key.FullName ?? pair.Key.Name, Count = pair.Value, EstimatedBytes = null });
            }

            return new ContentSummaryResult { Items = items };
        }

        for (var i = 0; i < p.Types.Count; i++)
        {
            var type = ResolveTypeName(p.Types[i], $"params.types[{i}]");
            long? bytes = 0;
            var count = 0;
            foreach (var value in Loaded(type))
            {
                count++;
                var size = Content.RuntimeMemorySize(value);
                bytes = size is null ? null : bytes + size;
            }

            items.Add(new ContentTypeSummary { Type = type.FullName ?? type.Name, Count = count, EstimatedBytes = count == 0 ? 0 : bytes });
        }

        return new ContentSummaryResult { Items = items };
    }

    // ---- Addressables --------------------------------------------------------------------------------------------------

    [RpcMethod(Methods.AddressablesInfo)]
    public ProtocolMessage AddressablesInfo(RequestContext context)
    {
        var addressables = Addressables;
        var locators = addressables.LocatorIds().ToList();
        return new AddressablesInfoResult { Version = addressables.Version, Locators = locators, RuntimePath = addressables.RuntimePath, CatalogCount = locators.Count };
    }

    [RpcMethod(Methods.AddressablesKeys, DefaultTimeoutMs = 60_000)]
    public ProtocolMessage AddressablesKeys(RequestContext context, AddressablesKeysParams p)
    {
        var addressables = Addressables;
        KeysPage page;
        if (p.Cursor is not null)
        {
            page = _data.Cursors.Take<KeysPage>(p.Cursor, "params.cursor");
        }
        else
        {
            var keyRegex = p.KeyRegex is null ? null : Pattern(p.KeyRegex, "params.keyRegex");
            var keys = addressables.Keys(p.Locator).Select(k => (Key: k, Text: KeyText(k)))
                .Where(k => keyRegex is null || keyRegex.IsMatch(k.Text))
                .GroupBy(k => k.Text, StringComparer.Ordinal).Select(g => g.First()) // the same key in several catalogs
                .OrderBy(k => k.Text, StringComparer.Ordinal).Select(k => k.Key).ToList();
            page = new KeysPage(keys, 0, p.ResourceType, p.Locator);
        }

        var limit = Limit(p.Limit);
        var items = new List<AddressableKey>();
        foreach (var key in page.Keys.Skip(page.Offset).Take(limit))
        {
            var locations = addressables.Locate(key, null, page.Locator)
                .Where(l => page.ResourceType is null || l.ResourceType == page.ResourceType).Select(Location).ToList();
            if (page.ResourceType is null || locations.Count > 0)
            {
                items.Add(new AddressableKey { Key = KeyText(key), Locations = locations });
            }
        }

        var next = page.Offset + limit;
        return new AddressablesKeysResult
        {
            Items = items,
            Cursor = next < page.Keys.Count ? _data.Cursors.Mint(page with { Offset = next }) : null,
            Total = page.ResourceType is null ? page.Keys.Count : null,
        };
    }

    [RpcMethod(Methods.AddressablesLocate)]
    public ProtocolMessage AddressablesLocate(RequestContext context, AddressablesLocateParams p) => new AddressablesLocateResult
    {
        Locations = Addressables.Locate(p.Key, p.Type is null ? null : ResolveTypeName(p.Type, "params.type"), null).Select(Location).ToList(),
    };

    [RpcMethod(Methods.AddressablesLoad, DefaultTimeoutMs = 120_000, MaxTimeoutMs = 600_000)]
    public IEnumerable AddressablesLoad(RequestContext context, AddressablesLoadParams p)
    {
        var addressables = Addressables;
        var type = p.Type is null ? UnityObjectType() : ResolveTypeName(p.Type, "params.type");
        var clock = Stopwatch.StartNew();
        var operation = addressables.LoadAsync(p.Key, type);
        var id = ++_nextOperation;
        _operations[id] = operation; // kept until addressables.release, even if the load fails: Addressables wants it released
        AddressablesOperation state;
        while (!(state = addressables.Poll(operation)).Done)
        {
            if (p.TimeoutMs is { } timeout && clock.ElapsedMilliseconds > timeout)
            {
                throw new ProtocolException(ErrorCodes.Timeout, $"'{p.Key}' didn't load within {timeout} ms (operation {id} is still pending; release it with addressables.release).");
            }

            yield return PumpWait.NextFrame;
        }

        if (!state.Succeeded)
        {
            throw new ProtocolException(ErrorCodes.NotFound, $"Loading '{p.Key}' failed: {state.Error ?? "no reason given"} (release operation {id} with addressables.release).");
        }

        var loaded = state.Result is IList list && state.Result is not string ? list.Cast<object?>() : new[] { state.Result };
        context.Target = "addr://" + p.Key;
        yield return new AddressablesLoadResult
        {
            OpHandle = id,
            Items = loaded.Where(o => o is not null).Select(o => _data.Handles.DescribeValue(o!)).ToList(),
            Frame = Frame,
            RealtimeMs = RealtimeMs,
        };
    }

    [RpcMethod(Methods.AddressablesRelease)]
    public ProtocolMessage AddressablesRelease(RequestContext context, AddressablesReleaseParams p)
    {
        if (!_operations.TryGetValue(p.OpHandle, out var operation))
        {
            throw DataErrors.NotFound("params.opHandle", $"No Addressables operation {p.OpHandle} (it was released already, or never existed).");
        }

        Addressables.Release(operation);
        _operations.Remove(p.OpHandle);
        return new AddressablesReleaseResult { Released = true };
    }

    // ---- AssetBundles and Resources -------------------------------------------------------------------------------------------

    [RpcMethod(Methods.BundlesLoaded)]
    public ProtocolMessage BundlesLoaded(RequestContext context, BundlesLoadedParams p) => new BundlesLoadedResult
    {
        Items = Content.LoadedBundles().Select(bundle =>
        {
            var facts = Content.DescribeBundle(bundle, p.IncludeAssetNames == true);
            return new BundleInfo { H = _data.Handles.Mint(bundle), Name = facts.Name, IsStreamedSceneAssetBundle = facts.IsStreamedSceneAssetBundle, AssetNames = facts.AssetNames, ScenePaths = facts.ScenePaths };
        }).ToList(),
    };

    [RpcMethod(Methods.BundlesLoad)]
    public ProtocolMessage BundlesLoad(RequestContext context, BundlesLoadParams p)
    {
        var bundle = _data.Handles.Resolve(p.Bundle);
        if (!ContentRules.IsA(bundle.GetType(), "UnityEngine.AssetBundle"))
        {
            throw ProtocolException.InvalidParams("params.bundle", $"Handle {p.Bundle} isn't an AssetBundle (it's a {bundle.GetType().FullName}).");
        }

        var loaded = Content.LoadFromBundle(bundle, p.AssetName, p.Type is null ? null : ResolveTypeName(p.Type, "params.type"));
        context.Target = loaded is null ? null : LocatorOf(loaded);
        return new BundlesLoadResult { Descriptor = loaded is null ? null : _data.Handles.DescribeValue(loaded), Frame = Frame, RealtimeMs = RealtimeMs };
    }

    [RpcMethod(Methods.ResourcesLoad)]
    public ProtocolMessage ResourcesLoad(RequestContext context, ResourcesLoadParams p)
    {
        var loaded = Content.ResourcesLoad(p.Path, p.Type is null ? null : ResolveTypeName(p.Type, "params.type"));
        context.Target = loaded is null ? null : LocatorOf(loaded);
        return new ResourcesLoadResult { Descriptor = loaded is null ? null : _data.Handles.DescribeValue(loaded), Frame = Frame, RealtimeMs = RealtimeMs };
    }

    [RpcMethod(Methods.ResourcesLoadAll)]
    public ProtocolMessage ResourcesLoadAll(RequestContext context, ResourcesLoadAllParams p)
    {
        var type = p.Type is null ? null : ResolveTypeName(p.Type, "params.type");
        var limit = Limit(p.Limit);
        return _jobs.Start("resources.loadAll", job => job.RunOnMain(() =>
        {
            var loaded = Content.ResourcesLoadAll(p.Path, type);
            return new ResourcesLoadAllJobResult
            {
                Items = loaded.Take(limit).Select(o => _data.Handles.DescribeValue(o)).ToList(),
                Truncated = loaded.Count > limit,
            };
        }), context.Context);
    }

    // ---- export ----------------------------------------------------------------------------------------------------------------

    [RpcMethod(Methods.ContentExportStart)]
    public ProtocolMessage ExportStart(RequestContext context, ContentExportStartParams p)
    {
        if (!Path.IsPathRooted(p.OutDir))
        {
            throw ProtocolException.InvalidParams("params.outDir", "outDir must be an absolute path.");
        }

        _ = Content; // UNSUPPORTED now rather than inside the job
        return _jobs.Start("content.export", job => Export(job, p), context.Context);
    }

    private ProtocolMessage Export(JobContext job, ContentExportStartParams p)
    {
        var outDir = Path.GetFullPath(p.OutDir);
        Directory.CreateDirectory(outDir);
        var sources = job.RunOnMain(() => ExportSources(p.Items));
        var warnings = new List<Warning>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exported = 0;
        var skipped = 0;
        using var manifest = new NdjsonFileWriter(Path.Combine(outDir, $"export-{job.JobId}.ndjson"), "export_manifest", FileSchemaVersion, _agentVersion,
            new JsonObject { { "outDir", JsonValue.From(outDir) } });
        var queue = new Queue<ExportSource>(sources);
        var done = 0;
        while (queue.Count > 0)
        {
            job.Cancellation.ThrowIfCancellationRequested();
            var source = queue.Dequeue();
            job.Progress("export", done++, done + queue.Count, source.Name);

            // Main thread: read what the file needs (pixels, samples, geometry, bytes, JSON). Worker: encode and write.
            var read = job.RunOnMain(() => ReadForExport(source));
            if (read.Warning is not null)
            {
                warnings.Add(new Warning { Code = read.Warning, Message = $"{source.Type.FullName} '{source.Name}': {read.Detail}" });
                skipped++;
                continue;
            }

            var bytes = read.Encode!();
            var folder = Path.Combine(outDir, ContentRules.SafeFileName(source.Type.Name));
            var path = UniquePath(folder, ContentRules.SafeFileName(source.Name), read.Extension!, used);
            var sha256 = Hex(SHA256.Create().ComputeHash(bytes));
            if (File.Exists(path) && (p.Overwrite != true || Hex(SHA256.Create().ComputeHash(File.ReadAllBytes(path))) == sha256))
            {
                skipped++; // kept as it is: overwrite is off, or the file already has this content
                continue;
            }

            try
            {
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(path, bytes);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new ProtocolException(ErrorCodes.IoFailed, $"Can't write {path}: {e.Message}", null, e);
            }

            manifest.Write(new ExportedFile
            {
                Rec = "file",
                Path = path,
                Sha256 = sha256,
                Size = bytes.Length,
                Source = new ExportSourceRecord(source).Record,
                Format = read.Format!,
                Warnings = read.Notes.Count > 0 ? read.Notes : null,
            }.ToJson());
            exported++;
        }

        return new ContentExportStartJobResult { Manifest = manifest.Complete(), Exported = exported, Skipped = skipped, Warnings = warnings.Count > 0 ? warnings : null };
    }

    private List<ExportSource> ExportSources(List<ExportItem> items)
    {
        var sources = new List<ExportSource>();
        var seen = new HashSet<object>(IdentityComparer.Instance);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            IEnumerable<object> found;
            if (item.Target is not null)
            {
                var value = _data.Targets.Resolve(item.Target, $"params.items[{i}].target").Value
                    ?? throw ProtocolException.InvalidParams($"params.items[{i}].target", "The target is a type's statics, not an object.");
                found = new[] { value };
            }
            else if (item.Type is not null)
            {
                var type = ResolveTypeName(item.Type, $"params.items[{i}].type");
                var nameRegex = item.NameRegex is null ? null : Pattern(item.NameRegex, $"params.items[{i}].nameRegex");
                found = Loaded(type).Where(o => nameRegex is null || (Unity.Describe(o) is { } f && nameRegex.IsMatch(f.Name)));
            }
            else
            {
                throw ProtocolException.InvalidParams($"params.items[{i}]", "Each item needs a target or a type.");
            }

            foreach (var value in found)
            {
                if (seen.Add(value) && Unity.Describe(value) is { } facts)
                {
                    sources.Add(new ExportSource(value, value.GetType(), facts.Name ?? string.Empty, facts.InstanceId, _data.Targets.LocatorBaseOf(value)));
                }
            }
        }

        // A stable order, so that repeated exports give files the same names.
        return sources.OrderBy(s => s.Type.FullName, StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.InstanceId).ToList();
    }

    private ExportRead ReadForExport(ExportSource source)
    {
        if (Unity.IsDestroyed(source.Value))
        {
            return ExportRead.Skip("DESTROYED", "it was destroyed before it could be exported");
        }

        switch (ContentRules.KindOf(source.Type))
        {
            case ContentKind.Image:
                var pixels = Content.ReadPixels(source.Value);
                return pixels is null ? ExportRead.Skip("IMAGE_NOT_READABLE", "its pixels couldn't be read back") : new ExportRead("png", ".png", () => PngEncoder.Encode(pixels));
            case ContentKind.Text:
                var bytes = Content.TextAssetBytes(source.Value) ?? Array.Empty<byte>();
                var text = TextRules.LooksLikeText(bytes);
                return new ExportRead(text ? "text" : "bytes", text ? ".txt" : ".bytes", () => bytes);
            case ContentKind.Static:
                return ExportRead.Skip("STATIC_ASSET", "its data doesn't change at runtime; extract it from the game's files with a static tool");
            default:
                if (ContentRules.ExportsSummary(source.Type) && Content.Summary(source.Value) is { } summary)
                {
                    return Json(summary);
                }

                var place = new Place { Root = new Target { H = _data.Handles.Mint(source.Value) }, LocatorBase = source.Locator };
                var json = _data.Writer(new ViewOptions { Depth = 4, ExpandRootUnityObject = true }, Frame).Write(source.Value, place).ToString();
                return new ExportRead("json", ".json", () => System.Text.Encoding.UTF8.GetBytes(json));
        }

        ExportRead Json(IDictionary<string, object?> summary)
        {
            var text = new SummaryEncoder(o => SummaryEncoder.Reference(Unity, o)).Encode(summary).ToString();
            return new ExportRead("json", ".json", () => System.Text.Encoding.UTF8.GetBytes(text));
        }
    }

    // ---- scan -----------------------------------------------------------------------------------------------------------

    [RpcMethod(Methods.ContentScanStart)]
    public ProtocolMessage ScanStart(RequestContext context, ContentScanStartParams p)
    {
        var types = (p.Types ?? new List<string> { "UnityEngine.Object" }).Select((t, i) => ResolveTypeName(t, $"params.types[{i}]")).ToList();
        _ = Content;
        return _jobs.Start("content.scan", job => Scan(job, p, types), context.Context);
    }

    private ProtocolMessage Scan(JobContext job, ContentScanStartParams p, List<Type> types)
    {
        var clock = Stopwatch.StartNew();
        var source = new JsonObject { { "types", new JsonArray(types.Select(t => JsonValue.From(t.FullName ?? t.Name)).ToList()) } };
        using var writer = new NdjsonFileWriter(p.OutFile, "content_scan", FileSchemaVersion, _agentVersion, source);
        var encoder = new SummaryEncoder(o => SummaryEncoder.Reference(Unity, o));

        if (p.IncludeResourcesLoaded != false)
        {
            // Loaded assets (objects that aren't in a scene), summarized a few hundred per frame.
            var assets = job.RunOnMain(() =>
            {
                var seen = new HashSet<object>(IdentityComparer.Instance);
                return types.SelectMany(Loaded).Where(o => seen.Add(o) && Unity.Locate(o) is null && !ContentRules.IsA(o.GetType(), "UnityEngine.Component") && !ContentRules.IsA(o.GetType(), "UnityEngine.AssetBundle")).ToList();
            });
            for (var start = 0; start < assets.Count; start += ScanChunk)
            {
                job.Cancellation.ThrowIfCancellationRequested();
                job.Progress("assets", start, assets.Count);
                var records = job.RunOnMain(() => assets.Skip(start).Take(ScanChunk).Select(o =>
                {
                    if (Unity.Describe(o) is not { } facts)
                    {
                        return null;
                    }

                    return new ScanAsset
                    {
                        Rec = "asset",
                        Name = facts.Name ?? string.Empty,
                        Type = AnchorWriter.ForType(o.GetType()),
                        InstanceId = facts.InstanceId,
                        Summary = Summarize(o, encoder),
                        Locator = Locators.Asset(o.GetType(), facts.Name, facts.InstanceId, string.Empty),
                        Bundle = null,
                    }.ToJson();
                }).Where(r => r is not null).ToList());
                foreach (var record in records)
                {
                    writer.Write(record!);
                }
            }
        }

        if (p.IncludeAddressablesKeys != false && Content.Addressables is { } addressables)
        {
            var keys = job.RunOnMain(() => addressables.Keys(null).GroupBy(KeyText, StringComparer.Ordinal).Select(g => g.First()).OrderBy(KeyText, StringComparer.Ordinal).ToList());
            for (var start = 0; start < keys.Count; start += ScanChunk)
            {
                job.Cancellation.ThrowIfCancellationRequested();
                job.Progress("addressables", start, keys.Count);
                var records = job.RunOnMain(() => keys.Skip(start).Take(ScanChunk)
                    .Select(k => new ScanAddressable { Rec = "addressable", Key = KeyText(k), Locations = BoundedLocations(addressables.Locate(k, null, null), writer) }.ToJson()).ToList());
                foreach (var record in records)
                {
                    writer.Write(record);
                }
            }
        }

        if (p.IncludeBundles != false)
        {
            var bundles = job.RunOnMain(() => Content.LoadedBundles().Select(b => Content.DescribeBundle(b, includeAssetNames: true)).ToList());
            foreach (var bundle in bundles)
            {
                writer.Write(new ScanBundle { Rec = "bundle", Name = bundle.Name, IsStreamedSceneAssetBundle = bundle.IsStreamedSceneAssetBundle, AssetNames = bundle.AssetNames, ScenePaths = bundle.ScenePaths }.ToJson());
            }
        }

        return new ContentScanStartJobResult { File = writer.Complete(), DurationMs = clock.ElapsedMilliseconds };
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------------

    private JsonObject Summarize(object value, SummaryEncoder encoder)
    {
        IDictionary<string, object?>? summary;
        try
        {
            summary = Content.Summary(value);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new JsonObject { { "error", JsonValue.From($"{e.GetType().Name}: {e.Message}") } };
        }

        if (summary is null && ContentRules.IsA(value.GetType(), "UnityEngine.ScriptableObject"))
        {
            summary = SummaryEncoder.FieldOverview(value);
        }

        return summary is null ? new JsonObject() : encoder.Encode(summary);
    }

    private IReadOnlyList<object> Loaded(Type type) => Unity.FindObjectsOfTypeAll(type).Where(o => !Unity.IsDestroyed(o)).ToList();

    private Type UnityObjectType() => _code.FindType("UnityEngine.Object") ?? throw DataErrors.Unsupported("UnityEngine.Object isn't loaded.");

    private Type ResolveType(JsonValue type, string param) => type switch
    {
        JsonString name => ResolveTypeName(name.Value, param),
        JsonObject anchor => _data.Anchors.ResolveType(UnityLudometry.Protocol.Messages.Anchor.Read(anchor, param), param),
        _ => throw ProtocolException.InvalidParams(param, $"{param} must be a full type name or an anchor."),
    };

    private Type ResolveTypeName(string name, string param)
    {
        var type = _code.FindType(name) ?? throw DataErrors.NotFound(param, $"No loaded type is named '{name}'.");
        if (!UnityTypes.IsUnityObject(type))
        {
            throw ProtocolException.InvalidParams(param, $"{name} isn't a Unity object type.");
        }

        return type;
    }

    private string? LocatorOf(object value) => _data.Targets.LocatorBaseOf(value);

    // A label or group key can stand for thousands of locations, each with its dependencies: a record keeps the first
    // ones, and the footer counts what was left out (the individual addresses have records of their own).
    private static List<AddressableLocation> BoundedLocations(IReadOnlyList<AddressableLocationFacts> locations, NdjsonFileWriter writer)
    {
        if (locations.Count > MaxScanLocations)
        {
            writer.CountRedaction("addressableLocations");
        }

        return locations.Take(MaxScanLocations).Select(l =>
        {
            var location = Location(l);
            if (location.Dependencies.Count > MaxScanDependencies)
            {
                writer.CountRedaction("addressableDependencies");
                location.Dependencies = location.Dependencies.Take(MaxScanDependencies).ToList();
            }

            return location;
        }).ToList();
    }

    private static AddressableLocation Location(AddressableLocationFacts l) => new()
    {
        InternalId = l.InternalId, ProviderId = l.ProviderId, ResourceType = l.ResourceType, Dependencies = l.Dependencies, PrimaryKey = l.PrimaryKey,
    };

    private static string KeyText(object key) => key as string ?? Convert.ToString(key, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    private static string UniquePath(string folder, string name, string extension, HashSet<string> used)
    {
        var path = Path.Combine(folder, name + extension);
        for (var n = 2; !used.Add(path); n++)
        {
            path = Path.Combine(folder, $"{name}_{n}{extension}");
        }

        return path;
    }

    private static string Hex(byte[] bytes) => string.Concat(bytes.Select(b => b.ToString("x2")));

    private static Regex Pattern(string pattern, string param)
    {
        try
        {
            return new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException e)
        {
            throw ProtocolException.InvalidParams(param, $"{param} isn't a valid regular expression: {e.Message}");
        }
    }

    private static int Limit(long? limit) => (int)Math.Max(1, Math.Min(limit ?? DefaultPage, MaxPage));

    private sealed record ContentPage(List<object> Objects, int Offset);

    private sealed record KeysPage(List<object> Keys, int Offset, string? ResourceType, string? Locator);

    private sealed record ExportSource(object Value, Type Type, string Name, long InstanceId, string? Locator);

    private sealed class ExportSourceRecord
    {
        public ExportSourceRecord(ExportSource source) => Record = new UnityLudometry.Protocol.Messages.ExportSource
        {
            Type = source.Type.FullName ?? source.Type.Name,
            Name = source.Name,
            InstanceId = source.InstanceId,
            Locator = source.Locator ?? Locators.Asset(source.Type, source.Name, source.InstanceId, string.Empty),
        };

        public UnityLudometry.Protocol.Messages.ExportSource Record { get; }
    }

    private sealed class ExportRead
    {
        public ExportRead(string format, string extension, Func<byte[]> encode)
        {
            Format = format;
            Extension = extension;
            Encode = encode;
        }

        private ExportRead()
        {
        }

        public string? Format { get; private set; }

        public string? Extension { get; private set; }

        public Func<byte[]>? Encode { get; private set; }

        public string? Warning { get; private set; }

        public string? Detail { get; private set; }

        public List<string> Notes { get; } = new();

        public static ExportRead Skip(string warning, string detail) => new() { Warning = warning, Detail = detail };
    }
}
