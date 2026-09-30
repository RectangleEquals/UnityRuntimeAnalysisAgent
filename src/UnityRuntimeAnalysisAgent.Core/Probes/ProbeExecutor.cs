using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Envelopes;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Data;
using UnityRuntimeAnalysisAgent.Core.Dispatch;
using UnityRuntimeAnalysisAgent.Core.Jobs;
using UnityRuntimeAnalysisAgent.Core.Runtime;

namespace UnityRuntimeAnalysisAgent.Core.Probes;

/// <summary>
/// Probes: one request/result shape over the agent's own methods, for the orchestrator's planner to record as evidence.
/// Each kind runs the methods it is built on through the dispatcher (so their checks, threads, modes and audit apply),
/// then derives a status (<c>ok</c>, <c>partial</c>, <c>failed</c>, <c>needs_trigger</c>, <c>stale</c>) and evidence
/// (locators with short excerpts). A probe that needs gameplay and saw nothing yet stays armed until it sees something,
/// is cancelled, or times out; <c>probe.result</c> collects it.
/// </summary>
internal sealed class ProbeExecutor : IDisposable
{
    public const int DefaultTimeoutMs = 30_000;
    public const int MaxTimeoutMs = 600_000;
    public const int DefaultWindowMs = 1000;
    public const int MaxEvidence = 50;
    public const int MaxExcerpt = 300;
    public const int MaxKeptResults = 500;
    public const int InspectedInstances = 3;

    private readonly Dispatcher _dispatcher;
    private readonly DataModel _data;
    private readonly MainThreadPump _pump;
    private readonly JobManager _jobs;
    private readonly ConcurrentDictionary<string, Armed> _armed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ProbeResult> _finished = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _finishedOrder = new();

    public ProbeExecutor(Dispatcher dispatcher, DataModel data, MainThreadPump pump, JobManager jobs)
    {
        _dispatcher = dispatcher;
        _data = data;
        _pump = pump;
        _jobs = jobs;
    }

    /// <summary>Runs one probe. Never throws for the probe's own failures: they become its status and error.</summary>
    public async Task<ProbeResult> RunAsync(Probe probe, JsonObject? context, CancellationToken cancellation)
    {
        var clock = Stopwatch.StartNew();
        var startFrame = _pump.Clock.FrameCount;
        var run = new ProbeRun(this, probe, context, cancellation);
        ProbeResult result;
        try
        {
            if (_armed.ContainsKey(probe.Id))
            {
                throw ProtocolException.InvalidParams("params.probe.id", $"Probe {probe.Id} is armed already: collect it with probe.result or disarm it with probe.cancel.");
            }

            var members = (probe.Anchors ?? new List<Anchor>()).Select((a, i) => _data.Anchors.ResolveMember(a, $"params.probe.anchors[{i}]")).ToList();
            result = await Kind(run, members).ConfigureAwait(false);
            foreach (var member in members)
            {
                run.Evidence(AnchorWriter.CodeLocator(member), AnchorWriter.MemberName(member));
            }
        }
        catch (Exception e)
        {
            var error = e switch
            {
                ProtocolException p => p.ToError(),
                OperationCanceledException => new ProtocolError { Code = ErrorCodes.Cancelled, Message = "The probe was cancelled." },
                _ => new ProtocolError { Code = ErrorCodes.Internal, Message = $"{e.GetType().Name}: {e.Message}" },
            };
            result = new ProbeResult { Status = error.Code == ErrorCodes.IndexStale ? "stale" : "failed", Error = error };
        }

        result.Id = probe.Id;
        result.Evidence = run.EvidenceList;
        result.Metrics = new ProbeMetrics { DurationMs = clock.ElapsedMilliseconds, Frames = Math.Max(0, _pump.Clock.FrameCount - startFrame) };
        if (result.Status != "needs_trigger")
        {
            Keep(result);
        }

        return result;
    }

    /// <summary><c>probe.result</c>: an armed probe is checked again (and completed once it saw something); a finished one
    /// is returned as it was.</summary>
    public async Task<ProbeResult> CollectAsync(string id)
    {
        if (_armed.TryGetValue(id, out var armed))
        {
            if (armed.Finishing is { } finishing)
            {
                return await finishing.ConfigureAwait(false);
            }

            var result = await armed.CollectAsync(final: false).ConfigureAwait(false);
            return result.Status == "needs_trigger" ? result : await Finish(id, armed, Task.FromResult(result)).ConfigureAwait(false);
        }

        return _finished.TryGetValue(id, out var finished) ? finished : throw DataErrors.NotFound("params.probeId", $"No probe {id} (unknown, or finished long ago).");
    }

    /// <summary><c>probe.cancel</c>: disarms a waiting probe (removing what it had set up).</summary>
    public async Task<bool> CancelAsync(string id)
    {
        if (!_armed.TryRemove(id, out var armed))
        {
            return false;
        }

        armed.Dispose();
        await armed.DisarmAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>Disarms everything (shutdown).</summary>
    public void Dispose()
    {
        foreach (var id in _armed.Keys.ToList())
        {
            if (_armed.TryRemove(id, out var armed))
            {
                armed.Dispose();
                try
                {
                    armed.DisarmAsync().Wait(TimeSpan.FromSeconds(2));
                }
                catch (Exception)
                {
                    // Shutting down: the instrumentation goes with the agent anyway.
                }
            }
        }
    }

    // ---- kinds --------------------------------------------------------------------------------------------------------------

    private Task<ProbeResult> Kind(ProbeRun run, List<MemberInfo> members) => run.Probe.Kind switch
    {
        "read_statics" => ReadStatics(run, members),
        "find_instances" => FindInstances(run, members),
        "read_members" => ReadMembers(run, members),
        "query" => Simple(run, Methods.ObjQuery, run.Params()),
        "content" => Content(run),
        "watch" => Watch(run, members),
        "hook_verify" => HookVerify(run, members),
        "trace" => Trace(run, members),
        "call" => Call(run, members),
        _ => throw ProtocolException.InvalidParams("params.probe.kind", $"'{run.Probe.Kind}' isn't a probe kind."),
    };

    private async Task<ProbeResult> Simple(ProbeRun run, string method, JsonObject parameters)
    {
        var result = await run.CallAsync(method, parameters).ConfigureAwait(false);
        run.EvidenceFrom(result);
        return Done(result);
    }

    private Task<ProbeResult> ReadStatics(ProbeRun run, List<MemberInfo> members)
    {
        var parameters = run.Params();
        if (!parameters.ContainsKey("type"))
        {
            var type = members.Select(m => m as Type ?? m.DeclaringType).FirstOrDefault() ?? throw ProtocolException.InvalidParams("params.probe.anchors", "A read_statics probe needs a type or member anchor (or params.type).");
            parameters.Set("type", AnchorWriter.ForType(type).ToJson());
            if (!parameters.ContainsKey("members") && members.Any(m => m is not Type))
            {
                parameters.Set("members", new JsonArray(members.Where(m => m is not Type).Select(m => (JsonValue)AnchorWriter.ForMember(m).ToJson()).ToList()));
            }
        }

        return Simple(run, Methods.StaticGet, parameters);
    }

    private async Task<ProbeResult> FindInstances(ProbeRun run, List<MemberInfo> members)
    {
        var parameters = run.Params();
        if (!parameters.ContainsKey("type"))
        {
            var type = members.Select(m => m as Type ?? m.DeclaringType).FirstOrDefault() ?? throw ProtocolException.InvalidParams("params.probe.anchors", "A find_instances probe needs a type anchor (or params.type).");
            parameters.Set("type", AnchorWriter.ForType(type).ToJson());
        }

        var found = await run.CallAsync(Methods.ObjFind, parameters).ConfigureAwait(false);
        var inspected = new JsonArray();
        var partial = false;
        foreach (var handle in Handles(found).Take(InspectedInstances))
        {
            try
            {
                var inspection = await run.CallAsync(Methods.ObjInspect, new JsonObject { { "target", Handle(handle) } }).ConfigureAwait(false);
                run.EvidenceFrom(inspection); // the inspection carries the instance's locator
                inspected.Add(inspection);
            }
            catch (ProtocolException)
            {
                partial = true; // gone between find and inspect
            }
        }

        run.EvidenceFrom(found);
        return Done(new JsonObject { { "found", found }, { "inspected", inspected } }, partial);
    }

    private async Task<ProbeResult> ReadMembers(ProbeRun run, List<MemberInfo> members)
    {
        var parameters = run.Params();
        var fields = members.Where(m => m is FieldInfo or PropertyInfo).ToList();
        if (!parameters.ContainsKey("paths"))
        {
            if (fields.Count == 0)
            {
                throw ProtocolException.InvalidParams("params.probe.anchors", "A read_members probe needs field or property anchors (or params.paths).");
            }

            parameters.Set("paths", new JsonArray(fields.Select(f => (JsonValue)new JsonArray { new JsonObject { { "member", AnchorWriter.ForMember(f).ToJson() } } }).ToList()));
        }

        if (!parameters.ContainsKey("targets"))
        {
            var owner = fields.Select(f => f.DeclaringType).FirstOrDefault() ?? throw ProtocolException.InvalidParams("params.probe.params.targets", "Give params.targets, or member anchors whose type has instances.");
            var isStatic = fields.All(f => f is FieldInfo { IsStatic: true } || f is PropertyInfo p && (p.GetGetMethod(true)?.IsStatic ?? false));
            if (isStatic)
            {
                parameters.Set("targets", new JsonArray { new JsonObject { { "static", AnchorWriter.ForType(owner!).ToJson() } } });
            }
            else
            {
                var limit = parameters["limit"] is JsonNumber n && n.TryGetInt64(out var l) ? l : 20;
                parameters.Remove("limit");
                var found = await run.CallAsync(Methods.ObjFind, new JsonObject { { "type", AnchorWriter.ForType(owner!).ToJson() }, { "limit", new JsonNumber(limit) } }).ConfigureAwait(false);
                parameters.Set("targets", new JsonArray(Handles(found).Select(h => (JsonValue)Handle(h)).ToList()));
                if (((JsonArray)parameters["targets"]!).Count == 0)
                {
                    run.EvidenceFrom(found);
                    return Done(new JsonObject { { "rows", new JsonArray() } }, partial: true);
                }
            }
        }
        else
        {
            parameters.Remove("limit");
        }

        return await Simple(run, Methods.ObjSnapshot, parameters).ConfigureAwait(false);
    }

    private Task<ProbeResult> Content(ProbeRun run)
    {
        var parameters = run.Params();
        var addressables = parameters["source"] is JsonString { Value: "addressables" };
        parameters.Remove("source");
        return Simple(run, addressables ? Methods.AddressablesKeys : Methods.ContentList, parameters);
    }

    private async Task<ProbeResult> Watch(ProbeRun run, List<MemberInfo> members)
    {
        var parameters = run.Params();
        var windowMs = run.Take(parameters, "windowMs", DefaultWindowMs);
        if (!parameters.ContainsKey("target") && members.FirstOrDefault(m => m is FieldInfo or PropertyInfo) is { } member)
        {
            parameters.Set("target", new JsonObject { { "static", AnchorWriter.ForType(member.DeclaringType!).ToJson() } });
            parameters.Set("path", new JsonArray { new JsonObject { { "member", AnchorWriter.ForMember(member).ToJson() } } });
        }

        var added = await run.CallAsync(Methods.WatchAdd, parameters).ConfigureAwait(false);
        var watchId = Text(added, "watchId");
        var armed = new Armed(run, async final =>
        {
            var changes = await run.CallAsync(Methods.WatchChanges, new JsonObject { { "watchId", new JsonString(watchId) } }).ConfigureAwait(false);
            var changed = changes["items"] is JsonArray items
                && items.OfType<JsonObject>().Any(c => c["previous"]?.ToString() != c["current"]?.ToString());
            run.EvidenceFrom(changes);
            var result = new JsonObject { { "initial", added["initial"] ?? JsonNull.Instance }, { "changes", changes } };
            return changed || final ? Done(result) : NeedsTrigger(result);
        }, () => run.CallAsync(Methods.WatchRemove, new JsonObject { { "watchId", new JsonString(watchId) } }));
        return await Settle(run, armed, windowMs).ConfigureAwait(false);
    }

    private async Task<ProbeResult> HookVerify(ProbeRun run, List<MemberInfo> members)
    {
        var parameters = run.Params();
        if (!parameters.ContainsKey("method"))
        {
            parameters.Set("method", AnchorWriter.ForMember(Method(members, "hook_verify")).ToJson());
        }

        var verified = await run.CallAsync(Methods.HookVerify, parameters).ConfigureAwait(false);
        run.EvidenceFrom(verified);
        var fired = verified["fired"] is JsonBoolean { Value: true };
        if (fired || !run.RequiresGameplay)
        {
            return Done(verified, partial: !fired);
        }

        // Nothing happened while verifying: stay armed with a hook until the gameplay comes.
        var added = await run.CallAsync(Methods.HookAdd, new JsonObject { { "method", parameters["method"]! }, { "phases", new JsonArray { new JsonString("enter") } }, { "maxHits", new JsonNumber(100L) } })
            .ConfigureAwait(false);
        var hookId = Text(added, "hookId");
        var armed = new Armed(run, async final =>
        {
            var hits = await run.CallAsync(Methods.HookHits, new JsonObject { { "hookId", new JsonString(hookId) } }).ConfigureAwait(false);
            var records = hits["items"] as JsonArray ?? new JsonArray();
            foreach (var record in records.OfType<JsonObject>().Take(10))
            {
                if (record["seq"] is JsonNumber seq)
                {
                    run.Evidence($"hit://{hookId}/{seq}", Excerpt(record));
                }
            }

            var result = new JsonObject { { "verify", verified }, { "hits", hits } };
            return records.Count > 0 ? Done(result) : final ? Done(result, partial: true) : NeedsTrigger(result);
        }, () => run.CallAsync(Methods.HookRemove, new JsonObject { { "hookId", new JsonString(hookId) } }));
        return await Settle(run, armed, 0).ConfigureAwait(false);
    }

    private async Task<ProbeResult> Trace(ProbeRun run, List<MemberInfo> members)
    {
        var parameters = run.Params();
        var windowMs = run.Take(parameters, "windowMs", DefaultWindowMs);
        var outDir = run.Probe.OutDir ?? throw ProtocolException.InvalidParams("params.probe.outDir", "A trace probe writes its records to a file: give outDir.");
        if (!Path.IsPathRooted(outDir) || !Directory.Exists(outDir))
        {
            throw ProtocolException.InvalidParams("params.probe.outDir", "outDir must be an existing absolute directory.");
        }

        if (!parameters.ContainsKey("methods") && !parameters.ContainsKey("include"))
        {
            parameters.Set("methods", new JsonArray(members.OfType<MethodBase>().Select(m => (JsonValue)AnchorWriter.ForMember(m).ToJson()).ToList()));
        }

        parameters.Set("outFile", new JsonString(Path.Combine(outDir, $"probe-{Safe(run.Probe.Id)}.trace.ndjson")));
        var stop = parameters["stop"] as JsonObject ?? new JsonObject();
        if (!stop.ContainsKey("durationMs"))
        {
            // Without gameplay to wait for, the window is the trace; otherwise it may run until the probe times out.
            stop.Set("durationMs", new JsonNumber((long)(run.RequiresGameplay ? run.TimeoutMs : windowMs)));
        }

        parameters.Set("stop", stop);
        var started = await run.CallAsync(Methods.TraceStart, parameters).ConfigureAwait(false);
        var jobId = Text(started, "jobId");
        var armed = new Armed(run, async final =>
        {
            var info = _jobs.Get(jobId);
            var recorded = info.Progress?.Done ?? 0;
            if (info.State is "queued" or "running")
            {
                if (recorded == 0 && !final)
                {
                    return NeedsTrigger(new JsonObject { { "jobId", new JsonString(jobId) }, { "records", new JsonNumber(0L) } });
                }

                await run.CallAsync(Methods.TraceStop, new JsonObject { { "jobId", new JsonString(jobId) } }).ConfigureAwait(false);
                info = await Task.Run(() => _jobs.Wait(jobId, 30_000, CancellationToken.None)).ConfigureAwait(false);
            }

            if (info.State != "succeeded")
            {
                throw new ProtocolException(info.Error?.Code ?? ErrorCodes.Internal, info.Error?.Message ?? $"The trace ended {info.State}.");
            }

            var result = (JsonObject)info.Result!;
            var records = result["records"] is JsonNumber r && r.TryGetInt64(out var count) ? count : 0;
            if (records == 0 && run.RequiresGameplay && !final)
            {
                return NeedsTrigger(result);
            }

            // Every method that was called (nested ones too), with its call count.
            if (result["summary"] is JsonObject { } summary && summary["methods"] is JsonArray called)
            {
                foreach (var entry in called.OfType<JsonObject>().Take(MaxEvidence))
                {
                    if (entry["method"] is JsonObject anchor && Resolve(anchor) is { } method)
                    {
                        run.Evidence(AnchorWriter.CodeLocator(method), $"{AnchorWriter.MemberName(method)} ×{entry["calls"]}");
                    }
                }
            }

            run.Evidence($"trace://{jobId}/1", $"{records} record(s) in {Path.GetFileName(Text(parameters, "outFile"))}");
            return Done(result, partial: records == 0 || result["truncated"] is JsonBoolean { Value: true });
        }, async () =>
        {
            if (_jobs.Get(jobId).State is "queued" or "running")
            {
                await run.CallAsync(Methods.TraceStop, new JsonObject { { "jobId", new JsonString(jobId) } }).ConfigureAwait(false);
            }
        });
        return await Settle(run, armed, windowMs).ConfigureAwait(false);
    }

    private Task<ProbeResult> Call(ProbeRun run, List<MemberInfo> members)
    {
        if (run.Probe.Consented != true)
        {
            throw ProtocolException.InvalidParams("params.probe.consented", "A call probe runs game code: it needs consented: true (the user's approval).");
        }

        var method = Method(members, "call");
        var parameters = run.Params();
        if (!parameters.ContainsKey("method"))
        {
            parameters.Set("method", AnchorWriter.ForMember(method).ToJson());
        }

        if (!parameters.ContainsKey("target"))
        {
            parameters.Set("target", method.IsStatic ? new JsonObject { { "static", AnchorWriter.ForType(method.DeclaringType!).ToJson() } }
                : throw ProtocolException.InvalidParams("params.probe.params.target", "An instance method needs params.target."));
        }

        return Simple(run, Methods.ObjInvoke, parameters);
    }

    // Waits a window for the armed probe to see something; if it didn't and the probe needs gameplay, it stays armed.
    private async Task<ProbeResult> Settle(ProbeRun run, Armed armed, int windowMs)
    {
        if (windowMs > 0)
        {
            try
            {
                await Task.Delay(windowMs, run.Cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await armed.DisarmAsync().ConfigureAwait(false);
                throw new ProtocolException(ErrorCodes.Cancelled, "The probe was cancelled.");
            }
        }

        var result = await armed.CollectAsync(final: !run.RequiresGameplay).ConfigureAwait(false);
        if (result.Status != "needs_trigger")
        {
            await armed.DisarmAsync().ConfigureAwait(false);
            return result;
        }

        armed.StartTimer(run.TimeoutMs, () => Expire(run.Probe.Id));
        _armed[run.Probe.Id] = armed;
        return result;
    }

    // The probe's time is up: its last collection is its result.
    private void Expire(string id)
    {
        if (_armed.TryGetValue(id, out var armed))
        {
            _ = Finish(id, armed, Task.Run(async () =>
            {
                try
                {
                    return await armed.CollectAsync(final: true).ConfigureAwait(false);
                }
                catch (ProtocolException e)
                {
                    return new ProbeResult { Id = id, Status = "failed", Error = e.ToError(), Evidence = armed.Run.EvidenceList, Metrics = armed.Run.Metrics() };
                }
            }));
        }
    }

    // Completes an armed probe once (whoever comes first: its trigger seen by probe.result, or its timeout): the result is
    // kept before the probe leaves the armed list, so probe.result always finds one or the other.
    private Task<ProbeResult> Finish(string id, Armed armed, Task<ProbeResult> final)
    {
        lock (armed)
        {
            return armed.Finishing ??= Complete();
        }

        async Task<ProbeResult> Complete()
        {
            var result = await final.ConfigureAwait(false);
            Keep(result);
            _armed.TryRemove(id, out _);
            armed.Dispose();
            await armed.DisarmAsync().ConfigureAwait(false);
            return result;
        }
    }

    private void Keep(ProbeResult result)
    {
        _finished[result.Id] = result;
        _finishedOrder.Enqueue(result.Id);
        while (_finishedOrder.Count > MaxKeptResults && _finishedOrder.TryDequeue(out var old))
        {
            if (!_finishedOrder.Contains(old))
            {
                _finished.TryRemove(old, out _);
            }
        }
    }

    private static ProbeResult Done(JsonValue result, bool partial = false) => new()
    {
        Status = partial || HasErrors(result) ? "partial" : "ok",
        Result = result,
    };

    private static ProbeResult NeedsTrigger(JsonValue result) => new() { Status = "needs_trigger", Result = result };

    // A result with error values in it (a member that couldn't be read, a row whose target is gone) is partial.
    private static bool HasErrors(JsonValue value) => value switch
    {
        JsonObject o => o["error"] is JsonObject { } e && e.ContainsKey("code") || o.Any(p => p.Value is not null && HasErrors(p.Value)),
        JsonArray a => a.Any(v => v is not null && HasErrors(v)),
        _ => false,
    };

    private static IEnumerable<long> Handles(JsonValue found) =>
        (found as JsonObject)?["items"] is JsonArray items
            ? items.OfType<JsonObject>().Select(i => i["h"] is JsonNumber n && n.TryGetInt64(out var h) ? h : 0).Where(h => h > 0)
            : Enumerable.Empty<long>();

    private static JsonObject Handle(long h) => new() { { "h", new JsonNumber(h) } };

    private static string Text(JsonValue value, string key) => (value as JsonObject)?[key] is JsonString s ? s.Value : throw new ProtocolException(ErrorCodes.Internal, $"The result has no {key}.");

    private static MethodBase Method(List<MemberInfo> members, string kind) =>
        members.OfType<MethodBase>().FirstOrDefault() ?? throw ProtocolException.InvalidParams("params.probe.anchors", $"A {kind} probe needs a method anchor.");

    private MemberInfo? Resolve(JsonObject anchor)
    {
        try
        {
            return _data.Anchors.ResolveMember(AnchorResolver.Read(anchor, "anchor"), "anchor");
        }
        catch (ProtocolException)
        {
            return null;
        }
    }

    private static string Safe(string id) => new(id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());

    internal static string Excerpt(JsonValue value)
    {
        var text = value is JsonString s ? s.Value : value.ToString();
        return text.Length <= MaxExcerpt ? text : text.Substring(0, MaxExcerpt - 1) + "…";
    }

    /// <summary>One probe's execution: its parameters, sub-calls and collected evidence.</summary>
    internal sealed class ProbeRun
    {
        private readonly ProbeExecutor _owner;
        private readonly JsonObject? _context;
        private readonly List<ProbeEvidence> _evidence = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly long _startFrame;

        public ProbeRun(ProbeExecutor owner, Probe probe, JsonObject? context, CancellationToken cancellation)
        {
            _owner = owner;
            Probe = probe;
            _context = context;
            Cancellation = cancellation;
            _startFrame = owner._pump.Clock.FrameCount;
            TimeoutMs = (int)Math.Max(1, Math.Min(probe.Limits?.TimeoutMs ?? DefaultTimeoutMs, MaxTimeoutMs));
        }

        public Probe Probe { get; }

        public CancellationToken Cancellation { get; }

        public int TimeoutMs { get; }

        public bool RequiresGameplay => Probe.RequiresGameplay ?? false;

        public List<ProbeEvidence> EvidenceList
        {
            get
            {
                lock (_evidence)
                {
                    return _evidence.ToList();
                }
            }
        }

        public ProbeMetrics Metrics() => new() { DurationMs = _clock.ElapsedMilliseconds, Frames = Math.Max(0, _owner._pump.Clock.FrameCount - _startFrame) };

        /// <summary>A copy of the probe's params.</summary>
        public JsonObject Params() => Probe.Params is { } p ? (JsonObject)JsonValue.Parse(p.ToString()) : new JsonObject();

        /// <summary>Removes a probe-only parameter from the params and returns it.</summary>
        public int Take(JsonObject parameters, string key, int fallback)
        {
            var value = parameters[key] is JsonNumber n && n.TryGetInt64(out var v) ? (int)Math.Max(0, Math.Min(v, MaxTimeoutMs)) : fallback;
            parameters.Remove(key);
            return value;
        }

        /// <summary>Calls one of the agent's methods as this probe (throws its error).</summary>
        public Task<JsonObject> CallAsync(string method, JsonObject parameters)
        {
            var completion = new TaskCompletionSource<JsonObject>();
            var request = new RequestContext($"probe:{Probe.Id}:{Guid.NewGuid():N}", method, parameters, _context, "probe", "probe " + Probe.Id, null, Cancellation);
            _owner._dispatcher.Dispatch(request, outcome =>
            {
                if (outcome.Error is { } error)
                {
                    completion.TrySetException(new ProtocolException(error.Code, error.Message, error.Data));
                }
                else
                {
                    completion.TrySetResult(outcome.Result as JsonObject ?? new JsonObject());
                }
            });
            return completion.Task;
        }

        public void Evidence(string locator, JsonValue excerpt)
        {
            lock (_evidence)
            {
                if (_evidence.Count < MaxEvidence && _evidence.All(e => e.Locator != locator))
                {
                    _evidence.Add(new ProbeEvidence { Locator = locator, Excerpt = excerpt is JsonString ? excerpt : new JsonString(Excerpt(excerpt)) });
                }
            }
        }

        public void Evidence(string locator, string excerpt) => Evidence(locator, new JsonString(excerpt.Length <= MaxExcerpt ? excerpt : excerpt.Substring(0, MaxExcerpt - 1) + "…"));

        /// <summary>Every locator in a result, with the object it belongs to as the excerpt.</summary>
        public void EvidenceFrom(JsonValue value)
        {
            switch (value)
            {
                case JsonObject o:
                    if (o["locator"] is JsonString { Value: var locator } && IsLocator(locator))
                    {
                        Evidence(locator, Excerpt(o));
                    }

                    foreach (var property in o)
                    {
                        if (property.Value is JsonObject or JsonArray)
                        {
                            EvidenceFrom(property.Value);
                        }
                    }

                    break;
                case JsonArray a:
                    foreach (var item in a)
                    {
                        if (item is not null)
                        {
                            EvidenceFrom(item);
                        }
                    }

                    break;
            }
        }

        private static bool IsLocator(string text) =>
            text.StartsWith("code://", StringComparison.Ordinal) || text.StartsWith("il://", StringComparison.Ordinal) || text.StartsWith("live://", StringComparison.Ordinal)
            || text.StartsWith("addr://", StringComparison.Ordinal) || text.StartsWith("hit://", StringComparison.Ordinal) || text.StartsWith("trace://", StringComparison.Ordinal);
    }

    /// <summary>A probe waiting for gameplay: how to look at it again, and how to take it down.</summary>
    private sealed class Armed : IDisposable
    {
        private readonly Func<bool, Task<ProbeResult>> _collect;
        private readonly Func<Task> _disarm;
        private Timer? _timer;
        private int _disarmed;

        public Armed(ProbeRun run, Func<bool, Task<ProbeResult>> collect, Func<Task> disarm)
        {
            Run = run;
            _collect = collect;
            _disarm = disarm;
        }

        public ProbeRun Run { get; }

        /// <summary>Set once the probe is being completed (by its trigger or its timeout).</summary>
        public Task<ProbeResult>? Finishing { get; set; }

        public async Task<ProbeResult> CollectAsync(bool final)
        {
            var result = await _collect(final).ConfigureAwait(false);
            result.Id = Run.Probe.Id;
            result.Evidence = Run.EvidenceList;
            result.Metrics = Run.Metrics();
            return result;
        }

        public void StartTimer(int timeoutMs, Action expire) => _timer = new Timer(_ => expire(), null, timeoutMs, Timeout.Infinite);

        public async Task DisarmAsync()
        {
            if (Interlocked.Exchange(ref _disarmed, 1) == 0)
            {
                try
                {
                    await _disarm().ConfigureAwait(false);
                }
                catch (ProtocolException)
                {
                    // Already gone (the hook hit its limit, the trace ended, the watch was cleared).
                }
            }
        }

        public void Dispose() => _timer?.Dispose();
    }
}
