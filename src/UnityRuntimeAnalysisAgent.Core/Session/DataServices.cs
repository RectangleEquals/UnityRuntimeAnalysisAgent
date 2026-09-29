using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Data;
using UnityRuntimeAnalysisAgent.Core.Dispatch;
using UnityRuntimeAnalysisAgent.Core.Runtime;

namespace UnityRuntimeAnalysisAgent.Core.Session;

/// <summary>
/// The data model's methods: <c>handles.*</c>, <c>vars.*</c>, <c>value.expand</c>, <c>locator.resolve</c> and the
/// exploratory <c>code.resolve</c>.
/// </summary>
internal sealed class DataServices
{
    private const int DefaultPage = 100;
    private const int MaxPage = 1000;
    private readonly DataModel _data;
    private readonly MainThreadPump _pump;

    public DataServices(DataModel data, MainThreadPump pump)
    {
        _data = data;
        _pump = pump;
    }

    [RpcMethod(Methods.HandlesList)]
    public ProtocolMessage HandlesList(RequestContext context, HandlesListParams p)
    {
        var after = p.Cursor is null ? 0L : _data.Cursors.Take<long>(p.Cursor, "params.cursor");
        var limit = (int)Math.Max(1, Math.Min(p.Limit ?? DefaultPage, MaxPage));
        var items = _data.Handles.List(after, limit, out var more);
        return new HandlesListResult
        {
            Items = items.ToList(),
            Cursor = more ? _data.Cursors.Mint(items[items.Count - 1].H) : null,
            Total = _data.Handles.Count,
        };
    }

    [RpcMethod(Methods.HandlesRelease)]
    public ProtocolMessage HandlesRelease(RequestContext context, HandlesReleaseParams p)
    {
        var unknown = new List<long>();
        var released = _data.Handles.Release(p.Handles, unknown);
        return new HandlesReleaseResult { Released = released, Unknown = unknown };
    }

    [RpcMethod(Methods.HandlesReleaseAll)]
    public ProtocolMessage HandlesReleaseAll(RequestContext context, HandlesReleaseAllParams p)
    {
        if (p.IncludeVariables == true)
        {
            _data.Variables.DeleteHandleVariables();
        }

        return new HandlesReleaseAllResult { Released = _data.Handles.ReleaseAll(includePinned: false) };
    }

    [RpcMethod(Methods.VarsSet)]
    public object VarsSet(RequestContext context, VarsSetParams p)
    {
        if ((p.Target is null) == (p.Value is null))
        {
            throw ProtocolException.InvalidParams("params", "vars.set takes exactly one of target and value.");
        }

        if (p.Value is not null)
        {
            var replaced = _data.Variables.SetValue(p.Name, p.Value);
            return Result(p.Name, replaced);
        }

        var target = p.Target!;
        if (target.GameObject is not null)
        {
            // Finding a GameObject calls into Unity: on the main thread, without holding this one.
            var deferred = new Deferred();
            _pump.Enqueue(new PumpWork(
                () =>
                {
                    var found = _data.Targets.Resolve(target, "params.target");
                    return _data.Handles.Mint(found.Value!);
                },
                h => deferred.Complete(Result(p.Name, _data.Variables.SetHandle(p.Name, (long)h!))),
                e => deferred.Fail(e),
                context.Cancellation));
            return deferred;
        }

        if (target.H is { } handle)
        {
            _data.Handles.Resolve(handle);
            return Result(p.Name, _data.Variables.SetHandle(p.Name, handle));
        }

        if (target.Static is { } anchor)
        {
            var type = _data.Anchors.ResolveType(anchor, "params.target.static");
            return Result(p.Name, _data.Variables.SetStatic(p.Name, AnchorWriter.ForType(type)));
        }

        if (target.Var is { } other)
        {
            var source = _data.Variables.Get(other, "params.target.var");
            var replaced = source.Kind switch
            {
                VariableKind.Handle => _data.Variables.SetHandle(p.Name, source.H),
                VariableKind.Static => _data.Variables.SetStatic(p.Name, source.Static!),
                _ => _data.Variables.SetValue(p.Name, source.Value!),
            };
            return Result(p.Name, replaced);
        }

        throw ProtocolException.InvalidParams("params.target", "params.target needs one of h, var, static or gameObject.");
    }

    [RpcMethod(Methods.VarsGet)]
    public ProtocolMessage VarsGet(RequestContext context, VarsGetParams p) =>
        new VarsGetResult { Variable = Info(_data.Variables.Get(p.Name, "params.name")) };

    [RpcMethod(Methods.VarsList)]
    public ProtocolMessage VarsList(RequestContext context) =>
        new VarsListResult { Items = _data.Variables.List().Select(Info).ToList() };

    [RpcMethod(Methods.VarsDelete)]
    public ProtocolMessage VarsDelete(RequestContext context, VarsDeleteParams p) =>
        new VarsDeleteResult { Deleted = _data.Variables.Delete(p.Name) };

    [RpcMethod(Methods.ValueExpand)]
    public ProtocolMessage ValueExpand(RequestContext context, ValueExpandParams p)
    {
        var entry = _data.Expansions.Get(p.Ref);
        var range = entry.Range;
        if (p.Range is JsonArray { Count: 2 } requested && requested[0] is JsonNumber from && requested[1] is JsonNumber to
            && from.TryGetInt32(out var f) && to.TryGetInt32(out var t))
        {
            if (f < 0 || t < f)
            {
                throw ProtocolException.InvalidParams("params.range", "params.range must be [from, to) with 0 <= from <= to.");
            }

            range = (f, t);
        }

        object? value;
        Place place;
        if (entry.HasRetained)
        {
            value = entry.WeakRetained is { } weak
                ? weak.Target ?? throw DataErrors.RefExpired(p.Ref, entry.Locator)
                : entry.Retained;
            place = new Place { LocatorBase = entry.Locator };
        }
        else
        {
            var resolved = _data.Targets.Resolve(entry.Root!, entry.Path, "params.ref", "params.ref");
            value = resolved.Value;
            place = resolved.Place;
        }

        var clock = _pump.Clock;
        var writer = _data.Writer(ViewOptions.From(p.View), clock.FrameCount);
        return new ValueExpandResult
        {
            Value = writer.Write(value, place, range, fullString: true),
            Locator = place.Locator ?? entry.Locator,
            Frame = clock.FrameCount,
            RealtimeMs = (long)(clock.Realtime * 1000),
        };
    }

    [RpcMethod(Methods.LocatorResolve)]
    public ProtocolMessage LocatorResolve(RequestContext context, LocatorResolveParams p)
    {
        var parsed = ParsedLocator.Parse(p.Locator, "params.locator");
        switch (parsed.Scheme)
        {
            case "code":
            case "il":
                var member = _data.Anchors.ResolveMember(parsed.Anchor!, "params.locator");
                return member is Type type
                    ? new LocatorResolveResult { Target = new Target { Static = AnchorWriter.ForType(type) }, Path = new List<MemberPathStep>() }
                    : new LocatorResolveResult
                    {
                        Target = new Target { Static = AnchorWriter.ForType(member.DeclaringType!) },
                        Path = new List<MemberPathStep> { new() { Member = AnchorWriter.ForMember(member) } },
                    };
            case "live" when parsed.LiveKind == "static":
                return ResolveStatic(parsed.Path!, p.Locator);
            case "live" when parsed.LiveKind is "scene" or "ddol":
                return ResolveScene(parsed, p.Locator);
            default:
                throw DataErrors.Unsupported($"This agent version can't resolve {parsed.Scheme}{(parsed.LiveKind is null ? string.Empty : "/" + parsed.LiveKind)} locators yet.");
        }
    }

    [RpcMethod(Methods.CodeResolve)]
    public ProtocolMessage CodeResolve(RequestContext context, CodeResolveParams p)
    {
        var candidates = new List<Anchor>();
        foreach (var type in FindTypes(p.Type, p.Assembly))
        {
            if (string.IsNullOrEmpty(p.Member))
            {
                candidates.Add(AnchorWriter.ForType(type));
                continue;
            }

            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            candidates.AddRange(type.GetMembers(all)
                .Where(m => m.Name == p.Member && (p.Signature is null || SignatureMatches(m, p.Signature)))
                .OrderBy(m => m.MetadataToken)
                .Select(AnchorWriter.ForMember));
        }

        return new CodeResolveResult { Candidates = candidates, Exploratory = true };
    }

    // ToString() reads parameters: not for internal calls (see SafeReflection).
    private static bool SignatureMatches(MemberInfo member, string signature) => member switch
    {
        MethodBase method when SafeReflection.IsInternalCall(method) => false,
        MethodBase method => method.ToString() == signature || AnchorWriter.Signature(method) == signature,
        PropertyInfo property when SafeReflection.IndexParameters(property) is null => false,
        _ => member.ToString() == signature,
    };

    private LocatorResolveResult ResolveStatic(string text, string locator)
    {
        // "<Type full name>.<members>": the longest dotted prefix that names a loaded type is the type.
        var dots = Enumerable.Range(0, text.Length).Where(i => text[i] == '.' || text[i] == '[').Concat(new[] { text.Length }).Reverse();
        foreach (var end in dots)
        {
            var type = FindTypes(text.Substring(0, end), null).FirstOrDefault();
            if (type is null)
            {
                continue;
            }

            var target = new Target { Static = AnchorWriter.ForType(type) };
            var steps = ParsedLocator.ParseMembers(text.Substring(end), locator, "params.locator");
            var resolved = _data.Targets.Resolve(target, steps, "params.locator", "params.locator");
            return new LocatorResolveResult { Target = target, Path = resolved.Place.Path };
        }

        throw DataErrors.NotFound("params.locator", $"No loaded type matches '{text}'.");
    }

    private LocatorResolveResult ResolveScene(ParsedLocator parsed, string locator)
    {
        var go = _data.Unity.FindGameObject(parsed.Path!, parsed.Scene)
            ?? throw DataErrors.NotFound("params.locator", $"No GameObject '{parsed.Path}' in scene '{parsed.Scene}'.");
        var owner = parsed.Owner!;
        var ends = Enumerable.Range(0, owner.Length).Where(i => owner[i] == '.' || owner[i] == '[').Concat(new[] { owner.Length }).Reverse();
        foreach (var end in ends)
        {
            var name = owner.Substring(0, end);
            object? found = name == go.GetType().FullName ? go : FindTypes(name, null).Select(t => _data.Unity.GetComponent(go, t)).FirstOrDefault(c => c is not null);
            if (found is null)
            {
                continue;
            }

            var h = _data.Handles.Mint(found);
            var steps = ParsedLocator.ParseMembers(owner.Substring(end), locator, "params.locator");
            var resolved = _data.Targets.Resolve(new Target { H = h }, steps, "params.locator", "params.locator");
            return new LocatorResolveResult { Target = new Target { H = h }, Path = resolved.Place.Path, Descriptor = _data.Handles.Describe(h) };
        }

        throw DataErrors.NotFound("params.locator", $"The GameObject '{parsed.Path}' has no component matching '{owner}'.");
    }

    // Exploratory lookup by full name (dnlib's '/' for nested types is accepted), optionally in one assembly.
    private IEnumerable<Type> FindTypes(string fullName, string? assembly)
    {
        var name = fullName.Replace('/', '+');
        return _data.Modules.Modules
            .Where(m => assembly is null || m.Assembly.GetName().Name == assembly)
            .Select(m => SafeGetType(m, name))
            .Where(t => t is not null)
            .Distinct()
            .OrderBy(t => t!.Assembly.GetName().Name, StringComparer.Ordinal)!;
    }

    private static Type? SafeGetType(Module module, string name)
    {
        try
        {
            return module.GetType(name, throwOnError: false, ignoreCase: false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private VarsSetResult Result(string name, bool replaced) => new() { Variable = Info(_data.Variables.Get(name, "params.name")), Replaced = replaced };

    private VariableInfo Info(Variable variable)
    {
        var info = new VariableInfo { Name = variable.Name, Kind = variable.Kind.ToString().ToLowerInvariant() };
        switch (variable.Kind)
        {
            case VariableKind.Handle when _data.Handles.Contains(variable.H):
                info.Descriptor = _data.Handles.Describe(variable.H);
                break;
            case VariableKind.Static:
                info.Static = variable.Static;
                break;
            case VariableKind.Value:
                info.Value = variable.Value;
                break;
        }

        return info;
    }
}
