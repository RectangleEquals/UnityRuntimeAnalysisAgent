using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Envelopes;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Abstractions;
using UnityRuntimeAnalysisAgent.Core.Code;
using UnityRuntimeAnalysisAgent.Core.Data;
using UnityRuntimeAnalysisAgent.Core.Dispatch;
using UnityRuntimeAnalysisAgent.Core.Jobs;
using UnityRuntimeAnalysisAgent.Core.Live;
using UnityRuntimeAnalysisAgent.Core.Runtime;
using Reflect = UnityRuntimeAnalysisAgent.Core.Code.Describe;

namespace UnityRuntimeAnalysisAgent.Core.Session;

/// <summary>
/// Live state: scenes, the GameObject hierarchy, objects and statics, declarative queries, collections, event listeners,
/// and — in Full mode — changes (set, invoke, create, destroy, …). Main thread. Every value uses the data model's
/// encoding, every result carries the frame it was read in, and changes carry locators (the dispatcher audits them).
/// </summary>
internal sealed class LiveServices
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 10_000;
    private const int QueryChunk = 2000;
    private const double QueryBudgetMs = 4;
    private readonly DataModel _data;
    private readonly CodeModel _code;
    private readonly MainThreadPump _pump;
    private readonly JobManager _jobs;
    private readonly ModeController _modes;
    private readonly ConditionEvaluator _conditions;

    public LiveServices(DataModel data, CodeModel code, MainThreadPump pump, JobManager jobs, ModeController modes)
    {
        _data = data;
        _code = code;
        _pump = pump;
        _jobs = jobs;
        _modes = modes;
        _conditions = new ConditionEvaluator(data);
    }

    private IUnityApi Unity => _data.Unity;

    private long Frame => _pump.Clock.FrameCount;

    private long RealtimeMs => (long)(_pump.Clock.Realtime * 1000);

    // ---- scenes -------------------------------------------------------------------------------------------------------

    [RpcMethod(Methods.SceneList)]
    public ProtocolMessage SceneList(RequestContext context) => new SceneListResult
    {
        Items = Unity.Scenes().Select(SceneInfo).ToList(),
        SceneCountInBuildSettings = Unity.SceneCountInBuildSettings,
    };

    [RpcMethod(Methods.SceneRoots)]
    public ProtocolMessage SceneRoots(RequestContext context, SceneRootsParams p) => new SceneRootsResult
    {
        Items = Unity.SceneRoots(FindScene(p.Scene, "params.scene").Handle).Select(Describe).ToList(),
    };

    // ---- GameObjects ----------------------------------------------------------------------------------------------------

    [RpcMethod(Methods.GoTree)]
    public ProtocolMessage GoTree(RequestContext context, GoTreeParams p)
    {
        IEnumerable<object> roots = p.Root is not null
            ? new[] { GameObjectOf(Resolve(p.Root, null, "params.root").Value, "params.root") }
            : p.Scene is null or JsonNull ? Unity.Scenes().SelectMany(s => Unity.SceneRoots(s.Handle)) : Unity.SceneRoots(FindScene(p.Scene, "params.scene").Handle);
        var depth = (int)Math.Max(0, Math.Min(p.Depth ?? 2, 16));
        var budget = (int)Math.Max(1, Math.Min(p.Limit ?? 1000, MaxLimit));
        var nameRegex = p.Filter?.NameRegex is { } pattern ? Pattern(pattern, "params.filter.nameRegex") : null;
        var componentType = p.Filter?.ComponentType is { } anchor ? _data.Anchors.ResolveType(anchor, "params.filter.componentType") : null;
        var truncated = false;

        bool Matches(GameObjectFacts facts) =>
            (nameRegex is null || nameRegex.IsMatch(facts.Name)) && (p.Filter?.Tag is null || facts.Tag == p.Filter.Tag) && (p.Filter?.Layer is null || facts.Layer == p.Filter.Layer)
            && (componentType is null || facts.Components.Any(c => componentType.IsInstanceOfType(c.Component)));

        GameObjectNode? Node(object go, int level)
        {
            var facts = Unity.DescribeGameObject(go)!;
            if (p.IncludeInactive == false && !facts.ActiveInHierarchy)
            {
                return null;
            }

            var children = new List<GameObjectNode>();
            if (level < depth)
            {
                foreach (var child in facts.Children)
                {
                    if (budget <= 0)
                    {
                        truncated = true;
                        break;
                    }

                    if (Node(child, level + 1) is { } node)
                    {
                        children.Add(node);
                    }
                }
            }

            // A node stays when it matches the filter or holds a node that does.
            if (!Matches(facts) && children.Count == 0)
            {
                return null;
            }

            budget--;
            return new GameObjectNode
            {
                H = _data.Handles.Mint(go),
                Name = facts.Name,
                Path = facts.Path,
                ActiveSelf = facts.ActiveSelf,
                ActiveInHierarchy = facts.ActiveInHierarchy,
                Tag = facts.Tag,
                Layer = facts.Layer,
                Components = p.IncludeComponents == false ? null : Components(facts),
                ChildCount = facts.Children.Count,
                Children = level < depth ? children : null,
            };
        }

        var items = new List<GameObjectNode>();
        foreach (var root in roots)
        {
            if (budget <= 0)
            {
                truncated = true;
                break;
            }

            if (Node(root, 0) is { } node)
            {
                items.Add(node);
            }
        }

        return new GoTreeResult { Items = items, Truncated = truncated };
    }

    [RpcMethod(Methods.GoFind)]
    public ProtocolMessage GoFind(RequestContext context, GoFindParams p)
    {
        var componentType = p.ComponentType is null ? null : _data.Anchors.ResolveType(p.ComponentType, "params.componentType");
        var limit = Limit(p.Limit);
        if (p.Path is not null && p.Name is null && p.NameRegex is null && p.Tag is null && componentType is null)
        {
            var found = Unity.FindGameObject(p.Path, p.Scene);
            return new GoFindResult { Items = found is null ? new List<HandleDescriptor>() : new List<HandleDescriptor> { Describe(found) } };
        }

        var nameRegex = p.NameRegex is null ? null : Pattern(p.NameRegex, "params.nameRegex");
        var items = new List<HandleDescriptor>();
        foreach (var facts in AllGameObjects(p.Scene))
        {
            if (items.Count >= limit)
            {
                break;
            }

            if ((p.IncludeInactive == true || facts.ActiveInHierarchy) && (p.Path is null || facts.Path == p.Path) && (p.Name is null || facts.Name == p.Name)
                && (nameRegex is null || nameRegex.IsMatch(facts.Name)) && (p.Tag is null || facts.Tag == p.Tag)
                && (componentType is null || facts.Components.Any(c => componentType.IsInstanceOfType(c.Component))))
            {
                items.Add(Describe(facts.GameObject));
            }
        }

        return new GoFindResult { Items = items };
    }

    [RpcMethod(Methods.GoGet)]
    public ProtocolMessage GoGet(RequestContext context, GoGetParams p)
    {
        var go = GameObjectOf(Resolve(p.Target, null, "params.target").Value, "params.target");
        var facts = Unity.DescribeGameObject(go)!;
        var writer = _data.Writer(ViewOptions.Default, Frame);
        JsonValue Value(object? v) => writer.Write(v, new Place());
        return new GoGetResult
        {
            GameObject = Describe(go),
            Path = facts.Path,
            Scene = facts.Scene,
            Transform = new TransformInfo
            {
                LocalPosition = Value(facts.LocalPosition), LocalRotation = Value(facts.LocalRotation), LocalScale = Value(facts.LocalScale),
                Position = Value(facts.Position), Rotation = Value(facts.Rotation), LossyScale = Value(facts.LossyScale),
            },
            Parent = facts.Parent is null ? null : Describe(facts.Parent),
            SiblingIndex = facts.SiblingIndex,
            ActiveSelf = facts.ActiveSelf,
            ActiveInHierarchy = facts.ActiveInHierarchy,
            HideFlags = facts.HideFlags,
            Tag = facts.Tag,
            Layer = facts.Layer,
            Components = Components(facts),
            Locator = LocatorOf(go),
            Frame = Frame,
            RealtimeMs = RealtimeMs,
        };
    }

    [RpcMethod(Methods.GoPath)]
    public ProtocolMessage GoPath(RequestContext context, GoPathParams p)
    {
        var go = GameObjectOf(Resolve(p.Target, null, "params.target").Value, "params.target");
        var facts = Unity.DescribeGameObject(go)!;
        return new GoPathResult { Path = facts.Path, Scene = facts.Scene, Locator = LocatorOf(go) ?? $"live://{facts.Scene}/{facts.Path}#{go.GetType().FullName}" };
    }

    // ---- objects ----------------------------------------------------------------------------------------------------------

    [RpcMethod(Methods.ObjFind)]
    public ProtocolMessage ObjFind(RequestContext context, ObjFindParams p)
    {
        var type = _data.Anchors.ResolveType(p.Type, "params.type");
        var limit = Limit(p.Limit);
        var items = new List<HandleDescriptor>();
        var truncated = false;
        foreach (var candidate in Candidates(type, p.Scope, p.IncludeInactive ?? false, "params.scope"))
        {
            if (!SafeMatch(candidate, p.Where, "params.where"))
            {
                continue;
            }

            if (items.Count >= limit)
            {
                truncated = true;
                break;
            }

            items.Add(Describe(candidate));
        }

        return new ObjFindResult { Items = items, Truncated = truncated };
    }

    [RpcMethod(Methods.ObjInspect)]
    public ProtocolMessage ObjInspect(RequestContext context, ObjInspectParams p)
    {
        var resolved = Resolve(p.Target, p.Path, "params.target", "params.path");
        var view = ViewOptions.From(p.View);
        view.ExpandRootUnityObject = true;
        return new ObjInspectResult
        {
            Value = _data.Writer(view, Frame).Write(resolved.Value, resolved.Place),
            Locator = resolved.Place.Locator,
            Warnings = resolved.Warnings.Count > 0 ? resolved.Warnings : null,
            Frame = Frame,
            RealtimeMs = RealtimeMs,
        };
    }

    [RpcMethod(Methods.ObjGet)]
    public ProtocolMessage ObjGet(RequestContext context, ObjGetParams p)
    {
        var start = _data.Targets.Resolve(p.Target, "params.target");
        var writer = _data.Writer(ViewOptions.From(p.View), Frame);
        var values = new List<Observation>();
        var actuals = new List<object?>();
        var warnings = new List<Warning>();
        for (var i = 0; i < p.Paths.Count; i++)
        {
            try
            {
                var resolved = _data.Targets.Walk(start, p.Paths[i], $"params.paths[{i}]");
                warnings.AddRange(resolved.Warnings);
                values.Add(new Observation { Value = writer.Write(resolved.Value, resolved.Place), Locator = resolved.Place.Locator, Frame = Frame, RealtimeMs = RealtimeMs });
                actuals.Add(resolved.Value);
            }
            catch (Exception e) when (e is ProtocolException or GameCodeException)
            {
                values.Add(new Observation { Value = ErrorValue(e), Frame = Frame, RealtimeMs = RealtimeMs });
                actuals.Add(null);
            }
        }

        return new ObjGetResult
        {
            Values = values,
            Expected = p.Expected is null ? null : Expected(p.Expected, i => i < actuals.Count ? (actuals[i], values[i].Value) : ((object?)null, JsonNull.Instance)),
            Warnings = warnings.Count > 0 ? warnings : null,
            Frame = Frame,
            RealtimeMs = RealtimeMs,
        };
    }

    [RpcMethod(Methods.ObjSnapshot)]
    public ProtocolMessage ObjSnapshot(RequestContext context, ObjSnapshotParams p)
    {
        var writer = _data.Writer(ViewOptions.From(p.View), Frame);
        var rows = new List<SnapshotRow>();
        var matches = new List<SnapshotExpectedMatch>();
        for (var row = 0; row < p.Targets.Count; row++)
        {
            var result = new SnapshotRow { Target = p.Targets[row], Values = new List<JsonValue>() };
            var actuals = new List<object?>();
            try
            {
                var start = _data.Targets.Resolve(p.Targets[row], $"params.targets[{row}]");
                if (start.Value is { } value && !start.IsStatic)
                {
                    result.Descriptor = Describe(value);
                }

                for (var i = 0; i < p.Paths.Count; i++)
                {
                    try
                    {
                        var resolved = _data.Targets.Walk(start, p.Paths[i], $"params.paths[{i}]");
                        result.Values.Add(writer.Write(resolved.Value, resolved.Place));
                        actuals.Add(resolved.Value);
                    }
                    catch (Exception e) when (e is ProtocolException or GameCodeException)
                    {
                        result.Values.Add(ErrorValue(e));
                        actuals.Add(null);
                    }
                }
            }
            catch (ProtocolException e)
            {
                result.Error = e.ToError(); // this target is gone or wrong; the other rows still count
            }

            if (p.Expected is not null && result.Error is null)
            {
                var index = row;
                matches.AddRange(Expected(p.Expected, i => i < actuals.Count ? (actuals[i], result.Values[i]) : ((object?)null, JsonNull.Instance))
                    .Select(m => new SnapshotExpectedMatch { Row = index, Path = m.Path, Match = m.Match, Expected = m.Expected, Actual = m.Actual }));
            }

            rows.Add(result);
        }

        return new ObjSnapshotResult { Rows = rows, Expected = p.Expected is null ? null : matches, Frame = Frame, RealtimeMs = RealtimeMs };
    }

    [RpcMethod(Methods.ObjDescribe)]
    public ProtocolMessage ObjDescribe(RequestContext context, ObjDescribeParams p)
    {
        var resolved = _data.Targets.Resolve(p.Target, "params.target");
        var value = resolved.Value ?? throw ProtocolException.InvalidParams("params.target", "params.target is a type's statics: use static.get.");
        var view = new ViewOptions { Depth = 1, Anchors = true, ExpandRootUnityObject = true };
        var encoded = _data.Writer(view, Frame).Write(value, resolved.Place) as JsonObject;
        var members = new List<DescribedMember>();
        var anchors = encoded?["anchors"] as JsonObject;
        foreach (var (kind, key) in new[] { ("field", "fields"), ("property", "props") })
        {
            if (encoded?[key] is JsonObject group)
            {
                foreach (var pair in group)
                {
                    members.Add(new DescribedMember
                    {
                        Name = pair.Key,
                        Kind = kind,
                        Value = pair.Value,
                        Anchor = anchors?[pair.Key] is JsonObject a ? UnityLudometry.Protocol.Messages.Anchor.Read(a, "anchor") : null,
                    });
                }
            }
        }

        return new ObjDescribeResult { Descriptor = Describe(value), Members = members, Frame = Frame, RealtimeMs = RealtimeMs };
    }

    // ---- statics ----------------------------------------------------------------------------------------------------------

    [RpcMethod(Methods.StaticGet)]
    public ProtocolMessage StaticGet(RequestContext context, StaticGetParams p)
    {
        var type = _data.Anchors.ResolveType(p.Type, "params.type");
        if (p.AllowInit == true && _modes.Current != AgentMode.Full)
        {
            throw AgentErrors.ModeForbidden(Methods.StaticGet + " with allowInit", AgentMode.Full, _modes.Current);
        }

        var members = p.Members is { Count: > 0 }
            ? p.Members.Select((a, i) => _data.Anchors.ResolveMember(a, $"params.members[{i}]")).ToList()
            : type.GetFields(Reflect.Declared).Where(f => f.IsStatic && !f.IsLiteral && !f.Name.StartsWith("<", StringComparison.Ordinal)).Cast<MemberInfo>()
                .Concat(p.Properties == true ? type.GetProperties(Reflect.Declared).Where(pr => Reflect.IsStatic(pr) && SafeReflection.IsPlainReadable(pr)) : Enumerable.Empty<MemberInfo>())
                .OrderBy(m => m.MetadataToken).ToList();
        var readable = p.AllowInit == true || _data.StaticInit.CanRead(type);
        var writer = _data.Writer(ViewOptions.From(p.View), Frame);
        var values = new List<StaticValue>();
        var actuals = new List<object?>();
        foreach (var member in members)
        {
            var place = new Place { Root = new Target { Static = AnchorWriter.ForType(type) }, LocatorBase = Locators.Static(type, string.Empty) }
                .Then(new MemberPathStep { Member = AnchorWriter.ForMember(member) }, Locators.Member(TargetResolver.DisplayName(member)));
            JsonValue encoded;
            object? actual = null;
            if (!readable)
            {
                encoded = new JsonObject { { "skipped", JsonValue.From("static_ctor_not_observed") } };
            }
            else
            {
                try
                {
                    actual = TargetResolver.Read(member, null);
                    encoded = writer.Write(actual, place);
                }
                catch (Exception e) when (e is ProtocolException or GameCodeException or ArgumentException)
                {
                    encoded = ErrorValue(e);
                }
            }

            values.Add(new StaticValue { Member = AnchorWriter.ForMember(member), Name = TargetResolver.DisplayName(member), Value = encoded });
            actuals.Add(actual);
        }

        if (readable)
        {
            _data.StaticInit.Observe(type); // the read ran the static constructor if it hadn't run
        }

        return new StaticGetResult
        {
            Values = values,
            Expected = p.Expected is null ? null : Expected(p.Expected, i => i < actuals.Count ? (actuals[i], values[i].Value) : ((object?)null, JsonNull.Instance)),
            Frame = Frame,
            RealtimeMs = RealtimeMs,
        };
    }

    [RpcMethod(Methods.StaticSingletons)]
    public ProtocolMessage StaticSingletons(RequestContext context, StaticSingletonsParams p)
    {
        IEnumerable<Type> types = p.Types is { Count: > 0 }
            ? p.Types.Select((a, i) => _data.Anchors.ResolveType(a, $"params.types[{i}]")).ToList()
            : _code.Types.Types.Where(t => p.Assembly is null || t.Assembly.GetName().Name == p.Assembly).OrderBy(t => t.Assembly.GetName().Name, StringComparer.Ordinal).ThenBy(t => t.MetadataToken);
        var items = new List<SingletonInstance>();
        foreach (var type in types)
        {
            if (type.ContainsGenericParameters)
            {
                continue;
            }

            var candidates = SafeMembers(type).Where(m => m is FieldInfo { IsStatic: true, IsLiteral: false } || (m is PropertyInfo pr && Reflect.IsStatic(pr) && SafeReflection.IsPlainReadable(pr)));
            foreach (var member in candidates)
            {
                var memberType = member is FieldInfo f ? f.FieldType : ((PropertyInfo)member).PropertyType;
                var reason = member.Name.StartsWith("<", StringComparison.Ordinal) ? null : SurveyJob.SingletonReason(type, member, memberType);
                if (reason is null)
                {
                    continue;
                }

                object? instance = null;
                if (_data.StaticInit.CanRead(type))
                {
                    try
                    {
                        instance = TargetResolver.Read(member, null);
                    }
                    catch (Exception e) when (e is GameCodeException or ArgumentException)
                    {
                        reason += "; reading it failed";
                    }
                }
                else
                {
                    reason += "; not read: static_ctor_not_observed";
                }

                items.Add(new SingletonInstance
                {
                    Type = AnchorWriter.ForType(type),
                    Member = AnchorWriter.ForMember(member),
                    Instance = instance is null || instance.GetType().IsValueType || (UnityTypes.IsUnityObject(instance.GetType()) && Unity.IsDestroyed(instance)) ? null : Describe(instance),
                    Reason = reason,
                });
            }
        }

        return new StaticSingletonsResult { Items = items };
    }

    // ---- queries ----------------------------------------------------------------------------------------------------------

    [RpcMethod(Methods.ObjQuery, DefaultTimeoutMs = 60_000, MaxTimeoutMs = 600_000)]
    public IEnumerable ObjQuery(RequestContext context, ObjQueryParams p)
    {
        var state = new QueryState(this, p);
        while (!state.Step(QueryBudgetMs))
        {
            yield return PumpWait.NextFrame; // spread over frames: the game keeps its frame rate
        }

        yield return state.Result();
    }

    [RpcMethod(Methods.ObjQueryStart)]
    public ProtocolMessage ObjQueryStart(RequestContext context, ObjQueryParams p) => _jobs.Start("obj.query", job =>
    {
        var state = job.RunOnMain(() => new QueryState(this, p));
        while (!job.RunOnMain(() => state.Step(QueryBudgetMs)))
        {
            job.Cancellation.ThrowIfCancellationRequested();
            job.Progress("query", state.Scanned, state.Total);
        }

        return job.RunOnMain(state.Result);
    }, context.Context);

    // ---- collections and listeners ------------------------------------------------------------------------------------------

    [RpcMethod(Methods.CollPage)]
    public ProtocolMessage CollPage(RequestContext context, CollPageParams p)
    {
        var resolved = Resolve(p.Target, p.Path, "params.target", "params.path");
        var collection = resolved.Value as IEnumerable ?? throw ProtocolException.InvalidParams("params.path", "The value at params.path isn't a collection.");
        var offset = (int)Math.Max(0, p.Offset ?? 0);
        var limit = Limit(p.Limit);
        var writer = _data.Writer(ViewOptions.From(p.View), Frame);
        var items = new List<JsonValue>();
        var index = 0;
        var entries = collection is IDictionary dictionary ? Entries(dictionary).Cast<object?>() : collection.Cast<object?>();
        foreach (var item in entries)
        {
            if (index >= offset && items.Count < limit)
            {
                items.Add(writer.Write(item, collection is IDictionary ? resolved.Place.Then(null, string.Empty) : resolved.Place.Then(new MemberPathStep { Index = index }, Locators.Index(index))));
            }

            index++;
        }

        return new CollPageResult { Count = Collections.Count(collection) ?? index, Offset = offset, Items = items, Frame = Frame, RealtimeMs = RealtimeMs };
    }

    [RpcMethod(Methods.CollCount)]
    public ProtocolMessage CollCount(RequestContext context, CollCountParams p)
    {
        var resolved = Resolve(p.Target, p.Path, "params.target", "params.path");
        var count = resolved.Value is { } value ? Collections.Count(value) : null;
        return new CollCountResult
        {
            Count = count ?? throw ProtocolException.InvalidParams("params.path", "The value at params.path isn't a counted collection (lazy sequences aren't enumerated)."),
            Frame = Frame,
            RealtimeMs = RealtimeMs,
        };
    }

    [RpcMethod(Methods.EventListeners)]
    public ProtocolMessage EventListeners(RequestContext context, EventListenersParams p)
    {
        var (ev, instance) = Event(p.Target, p.Event);
        var backing = EventAccess.Backing(ev, instance);
        return new EventListenersResult
        {
            Items = backing is null ? new List<Listener>() : backing.GetInvocationList().Select(d => new Listener
            {
                Target = d.Target is null ? null : Describe(d.Target),
                Method = AnchorWriter.ForMember(d.Method),
            }).ToList(),
        };
    }

    [RpcMethod(Methods.UnityEventListeners)]
    public ProtocolMessage UnityEventListeners(RequestContext context, UnityEventListenersParams p)
    {
        var resolved = Resolve(p.Target, p.Path, "params.target", "params.path");
        if (resolved.Value is null || !UnityTypes.IsUnityEvent(resolved.Value.GetType()))
        {
            throw ProtocolException.InvalidParams("params.path", "The value at params.path isn't a UnityEvent.");
        }

        var encoded = (JsonObject)_data.Writer(ViewOptions.Default, Frame).Write(resolved.Value, resolved.Place);
        return new UnityEventListenersResult
        {
            Persistent = (encoded["persistent"] as JsonArray ?? new JsonArray()).Cast<JsonObject>().Select(l => new PersistentListener
            {
                Target = l["target"] is JsonObject t ? HandleDescriptor.Read(t, "target") : null,
                Method = ((JsonString)l["method"]!).Value,
            }).ToList(),
            RuntimeListeners = encoded["runtimeListeners"] is JsonNumber n && n.TryGetInt64(out var count) ? count : 0,
        };
    }

    // ---- changes (Full mode; the dispatcher refuses them otherwise and audits them) --------------------------------------------

    [RpcMethod(Methods.ObjSet)]
    public ProtocolMessage ObjSet(RequestContext context, ObjSetParams p)
    {
        var start = _data.Targets.Resolve(p.Target, "params.target");
        var chain = _data.Targets.WalkChain(start, p.Path, "params.path");
        var writer = _data.Writer(ViewOptions.Default, Frame);
        var place = chain[chain.Count - 1].Place;
        var previous = writer.Write(chain[chain.Count - 1].Value, place);
        PathWriter.Set(_data, chain, p.Path, p.Value, "params");
        var current = _data.Targets.Walk(_data.Targets.Resolve(p.Target, "params.target"), p.Path, "params.path");
        context.Target = place.Locator;
        return new ObjSetResult { Previous = previous, Current = writer.Write(current.Value, current.Place), Locator = place.Locator, Frame = Frame, RealtimeMs = RealtimeMs };
    }

    [RpcMethod(Methods.ObjInvoke)]
    public ProtocolMessage ObjInvoke(RequestContext context, ObjInvokeParams p)
    {
        var target = _data.Targets.Resolve(p.Target, "params.target");
        var method = _data.Anchors.ResolveMember(p.Method, "params.method") as MethodBase
            ?? throw ProtocolException.InvalidParams("params.method", "params.method must be a method.");
        if (!target.IsStatic)
        {
            method = AnchorResolver.BindTo(method, target.Type) as MethodBase
                ?? throw ProtocolException.InvalidParams("params.method", $"{AnchorWriter.TypeName(target.Type)} has no method {AnchorWriter.MemberName(method)}.");
        }

        if (p.TypeArgs is { Count: > 0 })
        {
            method = method is MethodInfo { IsGenericMethodDefinition: true } generic && generic.GetGenericArguments().Length == p.TypeArgs.Count
                ? generic.MakeGenericMethod(p.TypeArgs.Select((t, i) => _data.Anchors.ResolveTypeRef(t, $"params.typeArgs[{i}]")).ToArray())
                : throw ProtocolException.InvalidParams("params.typeArgs", $"{AnchorWriter.MemberName(method)} doesn't take {p.TypeArgs.Count} type argument(s).");
        }

        if (method.IsStatic != target.IsStatic)
        {
            throw ProtocolException.InvalidParams("params.target", method.IsStatic ? "The method is static: use a static target." : "The method is an instance method: the target is a type's statics.");
        }

        var parameters = SafeReflection.Parameters(method)
            ?? throw DataErrors.Unsupported($"{AnchorWriter.MemberName(method)} is an engine internal call: its parameters can't be read safely, so it isn't invoked.");
        var args = p.Args ?? new List<JsonValue>();
        if (args.Count > parameters.Length)
        {
            throw ProtocolException.InvalidParams("params.args", $"{AnchorWriter.MemberName(method)} takes {parameters.Length} argument(s).");
        }

        var live = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var type = parameters[i].ParameterType.IsByRef ? parameters[i].ParameterType.GetElementType()! : parameters[i].ParameterType;
            live[i] = i < args.Count ? _data.Reader.Read(args[i], type, $"params.args[{i}]")
                : parameters[i].HasDefaultValue ? parameters[i].DefaultValue
                : parameters[i].IsOut ? (type.IsValueType ? Activator.CreateInstance(type) : null)
                : throw ProtocolException.InvalidParams("params.args", $"Argument {i} ({parameters[i].Name}) is missing.");
        }

        var clock = Stopwatch.StartNew();
        object? returned;
        try
        {
            returned = method is ConstructorInfo ctor ? ctor.Invoke(target.Value, live) : method.Invoke(target.Value, live);
        }
        catch (TargetInvocationException e)
        {
            throw AgentErrors.Game(e);
        }

        var writer = _data.Writer(ViewOptions.From(p.View), Frame);
        var outArgs = new JsonObject();
        for (var i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].ParameterType.IsByRef)
            {
                outArgs.Add(parameters[i].Name ?? $"arg{i}", writer.Write(live[i], new Place()));
            }
        }

        context.Target = target.Place.Locator;
        return new ObjInvokeResult
        {
            ReturnValue = method is MethodInfo { ReturnType: var r } && r == typeof(void) ? JsonNull.Instance : writer.Write(returned, new Place()),
            OutArgs = outArgs.Count > 0 ? outArgs : null,
            DurationMs = clock.ElapsedMilliseconds,
            Locator = target.Place.Locator,
            Frame = Frame,
            RealtimeMs = RealtimeMs,
        };
    }

    [RpcMethod(Methods.ObjCreate)]
    public ProtocolMessage ObjCreate(RequestContext context, ObjCreateParams p)
    {
        var type = _data.Anchors.ResolveType(p.Type, "params.type");
        object created;
        if (IsUnity(type, "UnityEngine.ScriptableObject"))
        {
            created = Unity.CreateScriptableObject(type);
        }
        else if (UnityTypes.IsUnityObject(type))
        {
            throw ProtocolException.InvalidParams("params.type", $"{AnchorWriter.TypeName(type)} is a Unity object: use go.create or component.add.");
        }
        else
        {
            var spec = new JsonObject { { "t", JsonValue.From("new") }, { "type", AnchorWriter.ToJson(p.Type) } };
            if (p.Ctor is not null)
            {
                spec.Add("ctor", AnchorWriter.ToJson(p.Ctor));
            }

            if (p.Args is not null)
            {
                spec.Add("args", new JsonArray(p.Args));
            }

            if (p.Fields is not null)
            {
                spec.Add("fields", p.Fields);
            }

            created = _data.Reader.Read(spec, typeof(object), "params") ?? throw ProtocolException.InvalidParams("params.type", "Nothing was created.");
        }

        context.Target = LocatorOf(created);
        return new ObjCreateResult { Descriptor = Describe(created), Locator = LocatorOf(created), Frame = Frame, RealtimeMs = RealtimeMs };
    }

    [RpcMethod(Methods.GoCreate)]
    public ProtocolMessage GoCreate(RequestContext context, GoCreateParams p)
    {
        var parent = p.Parent is null ? null : GameObjectOf(Resolve(p.Parent, null, "params.parent").Value, "params.parent");
        var types = (p.Components ?? new List<UnityLudometry.Protocol.Messages.Anchor>()).Select((a, i) => _data.Anchors.ResolveType(a, $"params.components[{i}]")).ToList();
        object go;
        try
        {
            go = Unity.CreateGameObject(p.Name, parent, p.Scene);
        }
        catch (ArgumentException e)
        {
            throw ProtocolException.InvalidParams("params.scene", e.Message);
        }

        foreach (var type in types)
        {
            Unity.AddComponent(go, type);
        }

        context.Target = LocatorOf(go);
        return new GoCreateResult { Descriptor = Describe(go), Locator = LocatorOf(go), Frame = Frame, RealtimeMs = RealtimeMs };
    }

    [RpcMethod(Methods.GoInstantiate)]
    public ProtocolMessage GoInstantiate(RequestContext context, GoInstantiateParams p)
    {
        var original = Resolve(p.Original, null, "params.original").Value;
        if (original is null || !UnityTypes.IsUnityObject(original.GetType()))
        {
            throw ProtocolException.InvalidParams("params.original", "params.original must be a Unity object.");
        }

        var parent = p.Parent is null ? null : GameObjectOf(Resolve(p.Parent, null, "params.parent").Value, "params.parent");
        var position = UnityValue(p.Position, "UnityEngine.Vector3", "params.position");
        var rotation = UnityValue(p.Rotation, "UnityEngine.Quaternion", "params.rotation");
        var clone = Unity.Instantiate(original, parent, position, rotation);
        context.Target = LocatorOf(clone);
        return new GoInstantiateResult { Descriptor = Describe(clone), Locator = LocatorOf(clone), Frame = Frame, RealtimeMs = RealtimeMs };
    }

    [RpcMethod(Methods.GoSetActive)]
    public ProtocolMessage GoSetActive(RequestContext context, GoSetActiveParams p)
    {
        var go = GameObjectOf(Resolve(p.Target, null, "params.target").Value, "params.target");
        var previous = Unity.DescribeGameObject(go)!.ActiveSelf;
        Unity.SetActive(go, p.Active);
        context.Target = LocatorOf(go);
        return new GoSetActiveResult { Previous = previous, Active = p.Active, Locator = LocatorOf(go), Frame = Frame, RealtimeMs = RealtimeMs };
    }

    [RpcMethod(Methods.ObjDestroy)]
    public ProtocolMessage ObjDestroy(RequestContext context, ObjDestroyParams p)
    {
        var value = Resolve(p.Target, null, "params.target").Value;
        if (value is null || !UnityTypes.IsUnityObject(value.GetType()))
        {
            throw ProtocolException.InvalidParams("params.target", "Only Unity objects are destroyed.");
        }

        var locator = LocatorOf(value);
        Unity.Destroy(value, p.Immediate ?? false);
        context.Target = locator;
        return new ObjDestroyResult { Destroyed = true, Locator = locator, Frame = Frame, RealtimeMs = RealtimeMs };
    }

    [RpcMethod(Methods.ComponentAdd)]
    public ProtocolMessage ComponentAdd(RequestContext context, ComponentAddParams p)
    {
        var go = GameObjectOf(Resolve(p.Target, null, "params.target").Value, "params.target");
        var type = _data.Anchors.ResolveType(p.Type, "params.type");
        if (!UnityRules.IsComponent(type))
        {
            throw ProtocolException.InvalidParams("params.type", $"{AnchorWriter.TypeName(type)} isn't a component.");
        }

        var component = Unity.AddComponent(go, type);
        context.Target = LocatorOf(component);
        return new ComponentAddResult { Descriptor = Describe(component), Locator = LocatorOf(component), Frame = Frame, RealtimeMs = RealtimeMs };
    }

    [RpcMethod(Methods.ComponentRemove)]
    public ProtocolMessage ComponentRemove(RequestContext context, ComponentRemoveParams p)
    {
        var component = Resolve(p.Target, null, "params.target").Value;
        if (component is null || !UnityRules.IsComponent(component.GetType()))
        {
            throw ProtocolException.InvalidParams("params.target", "params.target must be a component.");
        }

        if (component.GetType().FullName == "UnityEngine.Transform")
        {
            throw ProtocolException.InvalidParams("params.target", "A GameObject's Transform can't be removed.");
        }

        var locator = LocatorOf(component);
        Unity.Destroy(component, immediate: false);
        context.Target = locator;
        return new ComponentRemoveResult { Removed = true, Locator = locator, Frame = Frame, RealtimeMs = RealtimeMs };
    }

    [RpcMethod(Methods.CollAdd)]
    public ProtocolMessage CollAdd(RequestContext context, CollAddParams p)
    {
        var resolved = Resolve(p.Target, p.Path, "params.target", "params.path");
        var collection = resolved.Value ?? throw ProtocolException.InvalidParams("params.path", "The collection is null.");
        var count = CollectionEditor.Add(_data, collection, p.Value, p.Key, "params.path");
        context.Target = resolved.Place.Locator;
        return new CollAddResult { Count = count, Locator = resolved.Place.Locator, Frame = Frame, RealtimeMs = RealtimeMs };
    }

    [RpcMethod(Methods.CollRemove)]
    public ProtocolMessage CollRemove(RequestContext context, CollRemoveParams p)
    {
        var resolved = Resolve(p.Target, p.Path, "params.target", "params.path");
        var collection = resolved.Value ?? throw ProtocolException.InvalidParams("params.path", "The collection is null.");
        var removed = CollectionEditor.Remove(_data, collection, p.Index, p.Key, p.Value, "params.path");
        context.Target = resolved.Place.Locator;
        return new CollRemoveResult { Removed = removed, Count = Collections.Count(collection) ?? 0, Locator = resolved.Place.Locator, Frame = Frame, RealtimeMs = RealtimeMs };
    }

    [RpcMethod(Methods.CollSet)]
    public ProtocolMessage CollSet(RequestContext context, CollSetParams p)
    {
        if ((p.Index is null) == (p.Key is null))
        {
            throw ProtocolException.InvalidParams("params", "coll.set takes exactly one of index and key.");
        }

        var step = p.Index is { } index ? new MemberPathStep { Index = index } : new MemberPathStep { Key = p.Key };
        var path = new List<MemberPathStep>(p.Path) { step };
        return ObjSet(context, new ObjSetParams { Target = p.Target, Path = path, Value = p.Value }) is ObjSetResult set
            ? new CollSetResult { Previous = set.Previous, Current = set.Current, Locator = set.Locator, Frame = set.Frame, RealtimeMs = set.RealtimeMs }
            : throw new InvalidOperationException();
    }

    [RpcMethod(Methods.EventRaise)]
    public ProtocolMessage EventRaise(RequestContext context, EventRaiseParams p)
    {
        var (ev, instance) = Event(p.Target, p.Event);
        var backing = EventAccess.Backing(ev, instance);
        if (backing is null)
        {
            return new EventRaiseResult { ListenersInvoked = 0, Frame = Frame, RealtimeMs = RealtimeMs };
        }

        var invoke = backing.GetType().GetMethod("Invoke")!;
        var parameters = invoke.GetParameters();
        var args = p.Args ?? new List<JsonValue>();
        var live = parameters.Select((param, i) => i < args.Count ? _data.Reader.Read(args[i], param.ParameterType, $"params.args[{i}]") : null).ToArray();
        try
        {
            backing.DynamicInvoke(live);
        }
        catch (TargetInvocationException e)
        {
            throw AgentErrors.Game(e);
        }

        context.Target = instance is null ? null : LocatorOf(instance);
        return new EventRaiseResult { ListenersInvoked = backing.GetInvocationList().Length, Locator = instance is null ? null : LocatorOf(instance), Frame = Frame, RealtimeMs = RealtimeMs };
    }

    // ---- helpers --------------------------------------------------------------------------------------------------------

    internal sealed class QueryState
    {
        private readonly LiveServices _services;
        private readonly ObjQueryParams _p;
        private readonly List<object> _candidates;
        private readonly List<(object Candidate, object?[] Sort)> _matched = new();
        private int _next;

        public QueryState(LiveServices services, ObjQueryParams p)
        {
            _services = services;
            _p = p;
            _candidates = services.QueryCandidates(p.From).ToList();
        }

        public int Scanned => _next;

        public int Total => _candidates.Count;

        public int Errors { get; private set; }

        // Evaluates candidates until the chunk or the time budget is used; true when all are done.
        public bool Step(double budgetMs)
        {
            var clock = Stopwatch.StartNew();
            var end = Math.Min(_candidates.Count, _next + QueryChunk);
            while (_next < end && clock.Elapsed.TotalMilliseconds < budgetMs)
            {
                var candidate = _candidates[_next++];
                try
                {
                    if (_services._conditions.Matches(candidate, _p.Where, "params.where"))
                    {
                        var sort = (_p.OrderBy ?? new List<QueryOrder>()).Select((o, i) => _services.ValueAt(candidate, o.Path, $"params.orderBy[{i}].path")).ToArray();
                        _matched.Add((candidate, sort));
                    }
                }
                catch (Exception e) when (e is ProtocolException or GameCodeException or InvalidCastException or ArgumentException or RegexMatchTimeoutException)
                {
                    Errors++;
                }
            }

            return _next >= _candidates.Count;
        }

        public ObjQueryResult Result()
        {
            IEnumerable<(object Candidate, object?[] Sort)> ordered = _matched;
            var orders = _p.OrderBy ?? new List<QueryOrder>();
            for (var i = orders.Count - 1; i >= 0; i--)
            {
                var index = i;
                var desc = orders[i].Desc == true;
                ordered = desc ? ordered.OrderByDescending(m => m.Sort[index], SortComparer.Instance) : ordered.OrderBy(m => m.Sort[index], SortComparer.Instance);
            }

            var writer = _services._data.Writer(ViewOptions.From(_p.View), _services.Frame);
            var rows = ordered.Take(Limit(_p.Limit)).Select(m => new QueryRow
            {
                Target = _services.Describe(m.Candidate),
                Values = (_p.Select ?? new List<List<MemberPathStep>>()).Select((path, i) =>
                {
                    try
                    {
                        var resolved = _services._data.Targets.Walk(new Resolved { Value = m.Candidate, Type = m.Candidate.GetType(), Place = new Place() }, path, $"params.select[{i}]");
                        return writer.Write(resolved.Value, resolved.Place);
                    }
                    catch (Exception e) when (e is ProtocolException or GameCodeException)
                    {
                        return ErrorValue(e);
                    }
                }).ToList(),
            }).ToList();
            return new ObjQueryResult { Rows = rows, Scanned = _next, Matched = _matched.Count, Errors = Errors, Frame = _services.Frame, RealtimeMs = _services.RealtimeMs };
        }
    }

    private sealed class SortComparer : IComparer<object?>
    {
        public static readonly SortComparer Instance = new();

        public int Compare(object? x, object? y) => x is null ? (y is null ? 0 : -1) : y is null ? 1
            : x is IComparable c && x.GetType() == y.GetType() ? c.CompareTo(y) : string.CompareOrdinal(x.ToString(), y.ToString());
    }

    private IEnumerable<object> QueryCandidates(QuerySource from)
    {
        if (from.Type is not null)
        {
            return Candidates(_data.Anchors.ResolveType(from.Type, "params.from.type"), from.Scope, includeInactive: true, "params.from.scope");
        }

        if (from.Targets is { Count: > 0 })
        {
            return from.Targets.Select((t, i) => _data.Targets.Resolve(t, $"params.from.targets[{i}]").Value).Where(v => v is not null).Cast<object>();
        }

        if (from.Target is not null)
        {
            var resolved = Resolve(from.Target, from.Path, "params.from.target", "params.from.path");
            return resolved.Value is IDictionary dictionary ? dictionary.Values.Cast<object?>().Where(v => v is not null).Cast<object>()
                : resolved.Value is IEnumerable items and not string ? items.Cast<object?>().Where(v => v is not null).Cast<object>()
                : throw ProtocolException.InvalidParams("params.from.path", "The value at params.from.path isn't a collection.");
        }

        return Enumerable.Empty<object>(); // nothing to search
    }

    private object? ValueAt(object candidate, List<MemberPathStep> path, string param) =>
        _data.Targets.Walk(new Resolved { Value = candidate, Type = candidate.GetType(), Place = new Place() }, path, param).Value;

    // Candidates for a type: Unity objects by scope (scene: in a loaded scene; assets: not in a scene; all: both).
    private IEnumerable<object> Candidates(Type type, string? scope, bool includeInactive, string param)
    {
        if (!UnityTypes.IsUnityObject(type))
        {
            throw ProtocolException.InvalidParams(param, $"{AnchorWriter.TypeName(type)} isn't a Unity object: there's no heap scan, so plain objects are reached from targets, statics or collections.");
        }

        scope ??= "scene";
        if (scope is not ("scene" or "all" or "assets"))
        {
            throw ProtocolException.InvalidParams(param, "scope must be scene, all or assets.");
        }

        foreach (var candidate in Unity.FindObjectsOfTypeAll(type))
        {
            var inScene = Unity.Locate(candidate) is not null;
            if ((scope == "scene" && !inScene) || (scope == "assets" && inScene))
            {
                continue;
            }

            if (inScene && !includeInactive && Unity.DescribeGameObject(candidate) is { ActiveInHierarchy: false })
            {
                continue;
            }

            yield return candidate;
        }
    }

    private bool SafeMatch(object candidate, List<Condition>? where, string param)
    {
        try
        {
            return _conditions.Matches(candidate, where, param);
        }
        catch (Exception e) when (e is ProtocolException or GameCodeException or InvalidCastException or ArgumentException or RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private List<ExpectedMatch> Expected(JsonObject expected, Func<int, (object? Live, JsonValue Encoded)> actual)
    {
        var matches = new List<ExpectedMatch>();
        foreach (var pair in expected)
        {
            if (!int.TryParse(pair.Key, out var index) || index < 0)
            {
                throw ProtocolException.InvalidParams("params.expected", $"params.expected keys are path indexes ('{pair.Key}' isn't).");
            }

            var (live, encoded) = actual(index);
            bool match;
            try
            {
                match = _conditions.Equal(live, pair.Value, "params.expected." + pair.Key) || JsonValue.DeepEquals(encoded, pair.Value);
            }
            catch (ProtocolException)
            {
                match = JsonValue.DeepEquals(encoded, pair.Value);
            }

            matches.Add(new ExpectedMatch { Path = pair.Key, Match = match, Expected = pair.Value, Actual = encoded });
        }

        return matches;
    }

    private (EventInfo Event, object? Instance) Event(Target? target, UnityLudometry.Protocol.Messages.Anchor anchor)
    {
        var member = _data.Anchors.ResolveMember(anchor, "params.event");
        if (member is not EventInfo ev)
        {
            throw ProtocolException.InvalidParams("params.event", "params.event must be an event.");
        }

        if (target is null)
        {
            return Reflect.IsStatic(ev) ? (ev, null) : throw ProtocolException.InvalidParams("params.target", "An instance event needs params.target.");
        }

        var resolved = _data.Targets.Resolve(target, "params.target");
        if (resolved.IsStatic)
        {
            return (ev, null);
        }

        var bound = AnchorResolver.BindTo(ev, resolved.Type) as EventInfo
            ?? throw ProtocolException.InvalidParams("params.event", $"{AnchorWriter.TypeName(resolved.Type)} has no event {ev.Name}.");
        return (bound, resolved.Value);
    }

    private Resolved Resolve(Target target, List<MemberPathStep>? path, string param, string pathParam = "params.path") =>
        _data.Targets.Resolve(target, path, param, pathParam);

    private object GameObjectOf(object? value, string param) =>
        value is not null && Unity.DescribeGameObject(value) is { } facts ? facts.GameObject : throw ProtocolException.InvalidParams(param, $"{param} isn't a GameObject or a component.");

    private IEnumerable<GameObjectFacts> AllGameObjects(string? scene)
    {
        var roots = scene is null ? Unity.Scenes().SelectMany(s => Unity.SceneRoots(s.Handle)) : Unity.SceneRoots(FindScene(JsonValue.From(scene), "params.scene").Handle);
        var pending = new Stack<object>(roots.Reverse());
        while (pending.Count > 0)
        {
            var facts = Unity.DescribeGameObject(pending.Pop());
            if (facts is null)
            {
                continue;
            }

            yield return facts;
            for (var i = facts.Children.Count - 1; i >= 0; i--)
            {
                pending.Push(facts.Children[i]);
            }
        }
    }

    private SceneFacts FindScene(JsonValue? scene, string param)
    {
        var scenes = Unity.Scenes();
        return scene switch
        {
            null or JsonNull => scenes.FirstOrDefault(s => s.IsActive) ?? scenes.FirstOrDefault() ?? throw DataErrors.NotFound(param, "No scene is loaded."),
            JsonString { Value: "ddol" } => scenes.FirstOrDefault(s => s.IsDontDestroyOnLoad) ?? throw DataErrors.NotFound(param, "No DontDestroyOnLoad scene."),
            JsonString name => scenes.FirstOrDefault(s => s.Name == name.Value) ?? throw DataErrors.NotFound(param, $"No loaded scene '{name.Value}'."),
            JsonNumber n when n.TryGetInt32(out var index) => scenes.FirstOrDefault(s => s.BuildIndex == index) ?? throw DataErrors.NotFound(param, $"No loaded scene with build index {index}."),
            _ => throw ProtocolException.InvalidParams(param, $"{param} must be a scene name, a build index or \"ddol\"."),
        };
    }

    private List<ComponentInfo> Components(GameObjectFacts facts) => facts.Components.Select(c => new ComponentInfo
    {
        H = _data.Handles.Mint(c.Component),
        Type = AnchorWriter.ForType(c.Component.GetType()),
        Enabled = c.Enabled,
    }).ToList();

    private object? UnityValue(JsonValue? json, string typeName, string param)
    {
        if (json is null or JsonNull)
        {
            return null;
        }

        var type = _code.FindType(typeName) ?? throw DataErrors.Unsupported($"{typeName} isn't loaded.");
        return _data.Reader.Read(json, type, param);
    }

    private bool IsUnity(Type type, string baseName)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            if (t.FullName == baseName)
            {
                return true;
            }
        }

        return false;
    }

    private HandleDescriptor Describe(object value) => _data.Handles.Describe(_data.Handles.Mint(value));

    private string? LocatorOf(object value) => _data.Targets.LocatorBaseOf(value);

    private static IEnumerable<KeyValuePair<object, object?>> Entries(IDictionary dictionary)
    {
        var e = dictionary.GetEnumerator();
        while (e.MoveNext())
        {
            yield return new KeyValuePair<object, object?>(e.Key, e.Value);
        }
    }

    private static IEnumerable<MemberInfo> SafeMembers(Type type)
    {
        try
        {
            return type.GetMembers(Reflect.Declared);
        }
        catch (Exception)
        {
            return Array.Empty<MemberInfo>();
        }
    }

    internal static SceneInfo SceneInfo(SceneFacts s) => new()
    {
        Handle = s.Handle, Name = s.Name, Path = s.Path, BuildIndex = s.BuildIndex, IsLoaded = s.IsLoaded, IsActive = s.IsActive,
        RootCount = s.RootCount, IsDontDestroyOnLoad = s.IsDontDestroyOnLoad,
    };

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

    private static int Limit(long? limit) => (int)Math.Max(1, Math.Min(limit ?? DefaultLimit, MaxLimit));

    private static JsonValue ErrorValue(Exception e)
    {
        var error = e is ProtocolException p ? p.ToError() : new ProtocolError { Code = ErrorCodes.GameException, Message = e.InnerException?.Message ?? e.Message };
        return new JsonObject { { "error", AgentErrors.ToJson(error) } };
    }
}
