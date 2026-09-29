using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Code;
using UnityRuntimeAnalysisAgent.Core.Data;
using UnityRuntimeAnalysisAgent.Core.Dispatch;
using UnityRuntimeAnalysisAgent.Core.Hosting;
using UnityRuntimeAnalysisAgent.Core.Jobs;
using ProtocolParameterInfo = UnityLudometry.Protocol.Messages.ParameterInfo;

namespace UnityRuntimeAnalysisAgent.Core.Session;

/// <summary>
/// Code introspection: assemblies, types, members, attributes, IL, IL hashes, cross-references, and the survey and IL
/// index jobs. Metadata only (nothing runs a static constructor or instantiates an attribute), on worker threads.
/// </summary>
internal sealed class CodeServices
{
    private const int DefaultPage = 100;
    private const int MaxPage = 1000;
    private const int MaxTreeNodes = 2000;
    private readonly DataModel _data;
    private readonly CodeModel _code;
    private readonly JobManager _jobs;
    private readonly IlIndexJob _ilIndex;
    private readonly SurveyJob _survey;
    private readonly string _unityVersion;

    public CodeServices(DataModel data, CodeModel code, JobManager jobs, AgentEnvironment environment)
    {
        _data = data;
        _code = code;
        _jobs = jobs;
        _unityVersion = environment.UnityVersion;
        var unity = environment.UnityVersion == "unknown" ? null : environment.UnityVersion;
        _ilIndex = new IlIndexJob(code.Catalog, environment.AgentVersion);
        _survey = new SurveyJob(code.Catalog, code.FindType, data.Unity, environment.AgentVersion, unity);
    }

    [RpcMethod(Methods.CodeAssemblies, DefaultTimeoutMs = 120_000, MaxTimeoutMs = 600_000)]
    public ProtocolMessage Assemblies(RequestContext context, CodeAssembliesParams p) => new CodeAssembliesResult
    {
        Items = _code.Catalog.Assemblies(new AssemblyFilter(p.Include, p.Exclude, builtInExclusions: false)).Select(_code.Catalog.Summary).ToList(),
    };

    [RpcMethod(Methods.CodeAssembly)]
    public ProtocolMessage Assembly(RequestContext context, CodeAssemblyParams p)
    {
        if ((p.Mvid is null) == (p.Name is null))
        {
            throw ProtocolException.InvalidParams("params", "code.assembly takes exactly one of mvid and name.");
        }

        var assembly = _code.Catalog.Find(p.Mvid, p.Name, p.Mvid is null ? "params.name" : "params.mvid");
        return new CodeAssemblyResult
        {
            Assembly = _code.Catalog.Summary(assembly),
            Modules = assembly.GetModules().Select(m => new ModuleInfo { Name = m.Name, Mvid = m.ModuleVersionId.ToString(), ScopeName = m.ScopeName }).ToList(),
            Attributes = Attributes(assembly),
        };
    }

    [RpcMethod(Methods.CodeTypes, DefaultTimeoutMs = 120_000, MaxTimeoutMs = 600_000)]
    public ProtocolMessage Types(RequestContext context, CodeTypesParams p)
    {
        // Anchors first: a stale one is reported whether or not the cursor is still valid.
        _ = p.BaseType is null ? null : _data.Anchors.ResolveType(p.BaseType, "params.baseType");
        _ = p.Implements is null ? null : _data.Anchors.ResolveType(p.Implements, "params.implements");
        List<Type> matching;
        int offset;
        if (p.Cursor is not null)
        {
            var page = _data.Cursors.Take<TypesPage>(p.Cursor, "params.cursor");
            (matching, offset) = (page.Types, page.Offset);
        }
        else
        {
            matching = FilterTypes(p, context.Cancellation);
            offset = 0;
        }

        var limit = (int)Math.Max(1, Math.Min(p.Limit ?? DefaultPage, MaxPage));
        var items = matching.Skip(offset).Take(limit).Select(Summary).ToList();
        var next = offset + items.Count;
        return new CodeTypesResult
        {
            Items = items,
            Cursor = next < matching.Count ? _data.Cursors.Mint(new TypesPage(matching, next)) : null,
            Total = matching.Count,
        };
    }

    [RpcMethod(Methods.CodeType)]
    public ProtocolMessage Type(RequestContext context, CodeTypeParams p)
    {
        var type = _data.Anchors.ResolveType(p.Type, "params.type");
        var baseChain = new List<Anchor>();
        for (var t = type.BaseType; t is not null; t = t.BaseType)
        {
            if (AnchorWriter.CanAnchor(t))
            {
                baseChain.Add(AnchorWriter.ForType(t));
            }
        }

        var owners = p.Inherited == true ? new[] { type }.Concat(Bases(type)) : new[] { type };
        var result = new CodeTypeResult
        {
            Type = Summary(type),
            BaseChain = baseChain,
            Interfaces = Safe(() => type.GetInterfaces(), System.Type.EmptyTypes).Where(AnchorWriter.CanAnchor).Select(AnchorWriter.ForType).ToList(),
            NestedTypes = type.GetNestedTypes(Describe.Declared).OrderBy(n => n.MetadataToken).Select(AnchorWriter.ForType).ToList(),
            Attributes = Attributes(type),
            Members = owners.SelectMany(o => o.GetMembers(Describe.Declared).OrderBy(m => m.MetadataToken)).Select(MemberSummary).ToList(),
        };
        if (type.IsGenericTypeDefinition)
        {
            result.GenericParameters = type.GetGenericArguments().Select(g => new GenericParameter
            {
                Name = g.Name,
                Constraints = g.GetGenericParameterConstraints().Select(AnchorWriter.TypeName).ToList(),
            }).ToList();
        }

        return result;
    }

    [RpcMethod(Methods.CodeMember)]
    public ProtocolMessage Member(RequestContext context, CodeMemberParams p)
    {
        var member = _data.Anchors.ResolveMember(p.Member, "params.member");
        var result = new CodeMemberResult { Member = MemberSummary(member), Attributes = Attributes(member) };
        switch (member)
        {
            case FieldInfo field:
                result.FieldType = AnchorWriter.TypeName(field.FieldType);
                result.ReadOnly = field.IsInitOnly;
                if (field.IsLiteral)
                {
                    result.ConstantValue = Constant(Safe(() => field.GetRawConstantValue(), null));
                }

                var rules = new UnityRules(_code.UnityBaseTypes(), _unityVersion, null);
                if (rules.Available && field.DeclaringType is { } owner && rules.SerializesFieldsOf(owner) && UnityRules.Rule(field) is { } rule)
                {
                    result.Serialized = rules.Verdict(field, rule);
                }

                break;
            case MethodBase method:
                result.Parameters = SafeReflection.Parameters(method)?.Select(Parameter).ToList(); // unknown for internal calls
                if (method is MethodInfo info)
                {
                    result.ReturnType = AnchorWriter.TypeName(info.ReturnType);
                    result.GenericParameters = info.IsGenericMethod ? info.GetGenericArguments().Select(g => g.Name).ToList() : null;
                    var baseDefinition = Safe(() => info.GetBaseDefinition(), info);
                    if (baseDefinition != info)
                    {
                        result.BaseDefinition = AnchorWriter.ForMember(baseDefinition);
                    }
                }

                result.IsVirtual = method.IsVirtual;
                result.IsAbstract = method.IsAbstract;
                result.ImplFlags = method.GetMethodImplementationFlags().ToString();
                var body = Safe(() => method.GetMethodBody(), null);
                result.IlSize = IlReader.Body(method)?.Length ?? 0;
                if (body is not null)
                {
                    result.MaxStack = body.MaxStackSize;
                    result.Locals = body.LocalVariables.OrderBy(l => l.LocalIndex).Select(l => AnchorWriter.TypeName(l.LocalType)).ToList();
                    result.ExceptionClauses = body.ExceptionHandlingClauses.Count;
                }

                break;
            case PropertyInfo property:
                result.Getter = property.GetGetMethod(true) is { } get ? AnchorWriter.ForMember(get) : null;
                result.Setter = property.GetSetMethod(true) is { } set ? AnchorWriter.ForMember(set) : null;
                break;
            case EventInfo ev:
                result.Adder = ev.GetAddMethod(true) is { } add ? AnchorWriter.ForMember(add) : null;
                result.Remover = ev.GetRemoveMethod(true) is { } remove ? AnchorWriter.ForMember(remove) : null;
                result.Raiser = ev.GetRaiseMethod(true) is { } raise ? AnchorWriter.ForMember(raise) : null;
                var backing = ev.DeclaringType?.GetField(ev.Name, Describe.Declared);
                result.BackingField = backing is not null && typeof(Delegate).IsAssignableFrom(backing.FieldType) ? AnchorWriter.ForMember(backing) : null;
                break;
        }

        return result;
    }

    [RpcMethod(Methods.CodeHierarchy, DefaultTimeoutMs = 120_000, MaxTimeoutMs = 600_000)]
    public ProtocolMessage Hierarchy(RequestContext context, CodeHierarchyParams p)
    {
        var type = _data.Anchors.ResolveType(p.Type, "params.type");
        var depth = (int)Math.Max(1, Math.Min(p.Depth ?? 1, 20));
        var items = new List<HierarchyNode>();
        if (p.Direction == "up")
        {
            var child = type;
            for (var (t, level) = (type.BaseType, 1); t is not null && level <= depth; t = t.BaseType, level++)
            {
                items.Add(new HierarchyNode { Type = AnchorWriter.ForType(t), Depth = level, Parent = AnchorWriter.ForType(child) });
                child = t;
            }
        }
        else if (p.Direction == "down")
        {
            var frontier = new List<Type> { TypeIndex.Key(type) };
            for (var level = 1; level <= depth && frontier.Count > 0; level++)
            {
                var next = new List<Type>();
                foreach (var parent in frontier)
                {
                    context.Cancellation.ThrowIfCancellationRequested();
                    foreach (var sub in _code.Types.DirectSubtypes(parent).OrderBy(s => s.Assembly.GetName().Name, StringComparer.Ordinal).ThenBy(s => s.MetadataToken))
                    {
                        items.Add(new HierarchyNode { Type = AnchorWriter.ForType(sub), Depth = level, Parent = AnchorWriter.ForType(parent) });
                        next.Add(sub);
                    }
                }

                frontier = next;
            }
        }
        else
        {
            throw ProtocolException.InvalidParams("params.direction", "params.direction must be up or down.");
        }

        return new CodeHierarchyResult { Items = items };
    }

    [RpcMethod(Methods.CodeImplementations, DefaultTimeoutMs = 120_000, MaxTimeoutMs = 600_000)]
    public ProtocolMessage Implementations(RequestContext context, CodeImplementationsParams p)
    {
        var method = _data.Anchors.ResolveMember(p.Method, "params.method") as MethodInfo
            ?? throw ProtocolException.InvalidParams("params.method", "params.method must be a method.");
        var items = new List<Implementation>();
        var declaring = method.DeclaringType!;
        if (declaring.IsInterface)
        {
            var key = TypeIndex.Key(declaring);
            foreach (var implementer in _code.Types.Implementers(key).OrderBy(t => t.Assembly.GetName().Name, StringComparer.Ordinal).ThenBy(t => t.MetadataToken))
            {
                context.Cancellation.ThrowIfCancellationRequested();
                if (implementer.IsInterface)
                {
                    continue;
                }

                foreach (var constructed in Safe(() => implementer.GetInterfaces(), System.Type.EmptyTypes).Where(i => TypeIndex.Key(i) == key))
                {
                    var map = Safe(() => (InterfaceMapping?)implementer.GetInterfaceMap(constructed), null);
                    if (map is null)
                    {
                        continue;
                    }

                    for (var i = 0; i < map.Value.InterfaceMethods.Length; i++)
                    {
                        var target = map.Value.TargetMethods[i];
                        if (map.Value.InterfaceMethods[i].MetadataToken == method.MetadataToken && target.DeclaringType == implementer)
                        {
                            items.Add(new Implementation { Method = AnchorWriter.ForMember(target), DeclaringType = AnchorWriter.ForType(implementer), Via = "interface" });
                        }
                    }
                }
            }
        }
        else if (method.IsVirtual)
        {
            var root = XrefScanner.Key(Safe(() => method.GetBaseDefinition(), method));
            var pending = new Queue<Type>(_code.Types.DirectSubtypes(TypeIndex.Key(declaring)));
            var seen = new HashSet<Type>();
            while (pending.Count > 0)
            {
                context.Cancellation.ThrowIfCancellationRequested();
                var type = pending.Dequeue();
                if (!seen.Add(type))
                {
                    continue;
                }

                foreach (var candidate in type.GetMethods(Describe.Declared).Where(m => m.IsVirtual && m.Name == method.Name))
                {
                    if (XrefScanner.Key(Safe(() => candidate.GetBaseDefinition(), candidate)) == root)
                    {
                        items.Add(new Implementation { Method = AnchorWriter.ForMember(candidate), DeclaringType = AnchorWriter.ForType(type), Via = "override" });
                    }
                }

                foreach (var sub in _code.Types.DirectSubtypes(TypeIndex.Key(type)))
                {
                    pending.Enqueue(sub);
                }
            }
        }

        return new CodeImplementationsResult { Items = items };
    }

    [RpcMethod(Methods.CodeAttributes, DefaultTimeoutMs = 120_000, MaxTimeoutMs = 600_000)]
    public ProtocolMessage Attributes(RequestContext context, CodeAttributesParams p)
    {
        var targets = new HashSet<string>(p.Targets is { Count: > 0 } requested ? requested : new List<string> { "assembly", "type", "method", "field", "property", "event", "parameter" });
        var items = new List<AttributeUsage>();
        void Collect(object target, string kind, Func<Anchor> anchor)
        {
            foreach (var data in Describe.AttributeData(target).Where(a => Describe.SafeName(a) == p.Attribute))
            {
                items.Add(new AttributeUsage { Target = anchor(), TargetKind = kind, Attribute = AttributeInfo.Read(Describe.Attribute(data), "attribute") });
            }
        }

        foreach (var assembly in _code.Catalog.Assemblies(AssemblyFilter.All).Where(a => !a.IsDynamic))
        {
            context.Cancellation.ThrowIfCancellationRequested();
            if (targets.Contains("assembly"))
            {
                Collect(assembly, "assembly", () => new Anchor { Mvid = assembly.ManifestModule.ModuleVersionId.ToString(), Token = 0x20000001, Name = AssemblyCatalog.Name(assembly) });
            }

            foreach (var type in assembly.GetModules().SelectMany(AnchorResolver.LoadableTypes).OrderBy(t => t.MetadataToken))
            {
                context.Cancellation.ThrowIfCancellationRequested();
                if (targets.Contains("type"))
                {
                    Collect(type, "type", () => AnchorWriter.ForType(type));
                }

                foreach (var member in Safe(() => type.GetMembers(Describe.Declared), Array.Empty<MemberInfo>()).Where(m => m is not System.Type).OrderBy(m => m.MetadataToken))
                {
                    var kind = member is ConstructorInfo ? "method" : Describe.MemberKind(member);
                    if (targets.Contains(kind))
                    {
                        Collect(member, kind, () => AnchorWriter.ForMember(member));
                    }

                    if (targets.Contains("parameter") && member is MethodBase method)
                    {
                        foreach (var parameter in SafeReflection.Parameters(method) ?? Array.Empty<System.Reflection.ParameterInfo>())
                        {
                            Collect(parameter, "parameter", () => new Anchor
                            {
                                Mvid = method.Module.ModuleVersionId.ToString(),
                                Token = (uint)parameter.MetadataToken,
                                Name = $"{AnchorWriter.MemberName(method)}#{parameter.Name}",
                            });
                        }
                    }
                }
            }
        }

        return new CodeAttributesResult { Items = items };
    }

    [RpcMethod(Methods.CodeIlHashes)]
    public ProtocolMessage IlHashes(RequestContext context, CodeIlHashesParams p) => new CodeIlHashesResult
    {
        Items = p.Methods.Select((anchor, i) =>
        {
            var method = _data.Anchors.ResolveMember(anchor, $"params.methods[{i}]") as MethodBase
                ?? throw ProtocolException.InvalidParams($"params.methods[{i}]", $"params.methods[{i}] must be a method or constructor.");
            var il = IlReader.Body(method);
            return new IlHash { Anchor = AnchorWriter.ForMember(method), IlHashValue = il is null ? null : IlReader.Hash(il), IlSize = il?.Length ?? 0 };
        }).ToList(),
    };

    [RpcMethod(Methods.CodeIl)]
    public ProtocolMessage Il(RequestContext context, CodeIlParams p)
    {
        var method = Method(p.Method, "params.method");
        var resolve = p.ResolveOperands ?? true;
        var result = new CodeIlResult { Method = AnchorWriter.ForMember(method), IlSize = 0 };
        var il = IlReader.Body(method);
        var body = Safe(() => method.GetMethodBody(), null);
        if (il is null || body is null)
        {
            if (p.Format == "text")
            {
                result.Text = $"// {AnchorWriter.MemberName(method)}\n// no IL body (abstract, extern or runtime-implemented)\n";
            }
            else
            {
                result.Instructions = new List<UnityLudometry.Protocol.Messages.IlInstruction>();
            }

            return result;
        }

        var instructions = IlReader.Decode(il);
        result.IlSize = il.Length;
        result.MaxStack = body.MaxStackSize;
        result.InitLocals = body.InitLocals;
        result.Locals = body.LocalVariables.OrderBy(l => l.LocalIndex).Select(l => new IlLocal { Index = l.LocalIndex, Type = AnchorWriter.TypeName(l.LocalType) }).ToList();
        result.ExceptionClauses = body.ExceptionHandlingClauses.Select(c => new IlExceptionClause
        {
            Kind = c.Flags switch
            {
                ExceptionHandlingClauseOptions.Filter => "filter",
                ExceptionHandlingClauseOptions.Finally => "finally",
                ExceptionHandlingClauseOptions.Fault => "fault",
                _ => "catch",
            },
            TryOffset = c.TryOffset,
            TryLength = c.TryLength,
            HandlerOffset = c.HandlerOffset,
            HandlerLength = c.HandlerLength,
            CatchType = c.Flags == ExceptionHandlingClauseOptions.Clause ? Safe(() => AnchorWriter.TypeName(c.CatchType!), "?") : null,
        }).ToList();
        if (p.Format == "text")
        {
            result.Text = IlReader.Text(method, instructions, resolve);
        }
        else
        {
            result.Instructions = instructions.Select(i => new UnityLudometry.Protocol.Messages.IlInstruction
            {
                Offset = i.Offset,
                Opcode = i.OpCode.Name!,
                Operand = IlReader.Operand(i, method, resolve),
            }).ToList();
        }

        return result;
    }

    [RpcMethod(Methods.CodeCallers, DefaultTimeoutMs = 120_000, MaxTimeoutMs = 600_000)]
    public ProtocolMessage Callers(RequestContext context, CodeCallersParams p)
    {
        var method = Method(p.Method, "params.method");
        var budget = new TreeBudget();
        var items = CallerNodes(method, (int)Math.Max(1, Math.Min(p.Depth ?? 1, 5)), new HashSet<(Guid, int)> { XrefScanner.Key(method) }, budget, context.Cancellation);
        return new CodeCallersResult { Root = AnchorWriter.ForMember(method), Items = items, Limits = budget.Limits() };
    }

    [RpcMethod(Methods.CodeCallees, DefaultTimeoutMs = 120_000, MaxTimeoutMs = 600_000)]
    public ProtocolMessage Callees(RequestContext context, CodeCalleesParams p)
    {
        var method = Method(p.Method, "params.method");
        var budget = new TreeBudget();
        var items = CalleeNodes(method, (int)Math.Max(1, Math.Min(p.Depth ?? 1, 5)), new HashSet<(Guid, int)> { XrefScanner.Key(method) }, budget, context.Cancellation);
        return new CodeCalleesResult { Root = AnchorWriter.ForMember(method), Items = items, Limits = budget.Limits() };
    }

    [RpcMethod(Methods.CodeFieldAccess, DefaultTimeoutMs = 120_000, MaxTimeoutMs = 600_000)]
    public ProtocolMessage FieldAccess(RequestContext context, CodeFieldAccessParams p)
    {
        var field = _data.Anchors.ResolveMember(p.Field, "params.field") as FieldInfo
            ?? throw ProtocolException.InvalidParams("params.field", "params.field must be a field.");
        return new CodeFieldAccessResult
        {
            Items = _code.Xrefs.Referencing(field, XrefKind.Field, context.Cancellation)
                .Where(x => p.Access is null || x.Access == p.Access)
                .Select(x => new UnityLudometry.Protocol.Messages.FieldAccess { Method = AnchorWriter.ForMember(x.Method), Access = x.Access!, Offset = x.Offset })
                .ToList(),
            Limits = XrefIndex.Limits.ToList(),
        };
    }

    [RpcMethod(Methods.CodeStrings, DefaultTimeoutMs = 120_000, MaxTimeoutMs = 600_000)]
    public ProtocolMessage Strings(RequestContext context, CodeStringsParams p)
    {
        if ((p.Regex is null) == (p.Literal is null))
        {
            throw ProtocolException.InvalidParams("params", "code.strings takes exactly one of regex and literal.");
        }

        Regex pattern;
        try
        {
            pattern = new Regex(p.Regex ?? "^" + Regex.Escape(p.Literal!) + "$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException e)
        {
            throw ProtocolException.InvalidParams("params.regex", "params.regex isn't a valid regular expression: " + e.Message);
        }

        var limit = (int)Math.Max(1, Math.Min(p.Limit ?? DefaultPage, 10_000));
        var items = _code.Xrefs.Strings(pattern, new AssemblyFilter(null, null, builtInExclusions: true), context.Cancellation)
            .Take(limit)
            .Select(x => new StringUse { Method = AnchorWriter.ForMember(x.Method), Literal = x.Literal!, Offset = x.Offset })
            .ToList();
        return new CodeStringsResult { Items = items };
    }

    [RpcMethod(Methods.CodeAllocations, DefaultTimeoutMs = 120_000, MaxTimeoutMs = 600_000)]
    public ProtocolMessage Allocations(RequestContext context, CodeAllocationsParams p)
    {
        var type = _data.Anchors.ResolveType(p.Type, "params.type");
        return new CodeAllocationsResult
        {
            Items = _code.Xrefs.Referencing(type, XrefKind.Alloc, context.Cancellation)
                .Select(x => new AllocationSite { Method = AnchorWriter.ForMember(x.Method), Opcode = x.Opcode, Offset = x.Offset })
                .ToList(),
        };
    }

    [RpcMethod(Methods.IlIndexStart)]
    public ProtocolMessage IlIndexStart(RequestContext context, IlIndexStartParams p) =>
        _jobs.Start("il.index", job => _ilIndex.Run(job, p), context.Context);

    [RpcMethod(Methods.SurveyStart)]
    public ProtocolMessage SurveyStart(RequestContext context, SurveyStartParams p) =>
        _jobs.Start("survey", job => _survey.Run(job, SurveyRequest.From(p, _data.Anchors)), context.Context);

    private List<CallTreeNode> CallerNodes(MethodBase method, int depth, HashSet<(Guid, int)> expanded, TreeBudget budget, CancellationToken cancellation)
    {
        var nodes = new List<CallTreeNode>();
        foreach (var xref in _code.Xrefs.Referencing(method, XrefKind.Call, cancellation))
        {
            if (!budget.Take())
            {
                break;
            }

            var node = new CallTreeNode { Method = AnchorWriter.ForMember(xref.Method), Opcode = xref.Opcode, Offset = xref.Offset };
            if (depth > 1 && expanded.Add(XrefScanner.Key(xref.Method)))
            {
                node.Children = CallerNodes(xref.Method, depth - 1, expanded, budget, cancellation);
            }

            nodes.Add(node);
        }

        return nodes;
    }

    private static List<CallTreeNode> CalleeNodes(MethodBase method, int depth, HashSet<(Guid, int)> expanded, TreeBudget budget, CancellationToken cancellation)
    {
        var nodes = new List<CallTreeNode>();
        foreach (var xref in XrefIndex.In(method).Where(x => x.Kind == XrefKind.Call))
        {
            cancellation.ThrowIfCancellationRequested();
            var callee = (MethodBase)xref.Target!;
            if (callee.DeclaringType is { } declaring && !AnchorWriter.CanAnchor(declaring))
            {
                budget.ArrayMethods++;
                continue;
            }

            if (!budget.Take())
            {
                break;
            }

            var node = new CallTreeNode { Method = AnchorWriter.ForMember(callee), Opcode = xref.Opcode, Offset = xref.Offset };
            if (depth > 1 && expanded.Add(XrefScanner.Key(callee)))
            {
                node.Children = CalleeNodes(callee, depth - 1, expanded, budget, cancellation);
            }

            nodes.Add(node);
        }

        return nodes;
    }

    private MethodBase Method(Anchor anchor, string param) =>
        _data.Anchors.ResolveMember(anchor, param) as MethodBase ?? throw ProtocolException.InvalidParams(param, $"{param} must be a method or constructor.");

    private List<Type> FilterTypes(CodeTypesParams p, CancellationToken cancellation)
    {
        var baseType = p.BaseType is null ? null : TypeIndex.Key(_data.Anchors.ResolveType(p.BaseType, "params.baseType"));
        var implements = p.Implements is null ? null : TypeIndex.Key(_data.Anchors.ResolveType(p.Implements, "params.implements"));
        Regex? name;
        try
        {
            name = p.NameRegex is null ? null : new Regex(p.NameRegex, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException e)
        {
            throw ProtocolException.InvalidParams("params.nameRegex", "params.nameRegex isn't a valid regular expression: " + e.Message);
        }

        var matching = new List<Type>();
        foreach (var type in _code.Types.Types.OrderBy(t => t.Assembly.GetName().Name, StringComparer.Ordinal).ThenBy(t => t.MetadataToken))
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                if ((p.Assembly is null || type.Assembly.GetName().Name == p.Assembly)
                    && (p.Namespace is null || type.Namespace == p.Namespace)
                    && (name is null || name.IsMatch(type.FullName ?? type.Name))
                    && (p.Kind is null || Describe.TypeKind(type) == p.Kind)
                    && (baseType is null || Bases(type).Any(b => TypeIndex.Key(b) == baseType))
                    && (implements is null || type.GetInterfaces().Any(i => TypeIndex.Key(i) == implements))
                    && (p.HasAttribute is null || Describe.HasAttribute(type, p.HasAttribute))
                    && (p.IsUnityComponent is null || UnityRules.IsComponent(type) == p.IsUnityComponent))
                {
                    matching.Add(type);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // a type whose base or interfaces can't load doesn't match
            }
        }

        return matching;
    }

    private static IEnumerable<Type> Bases(Type type)
    {
        for (var t = type.BaseType; t is not null; t = t.BaseType)
        {
            yield return t;
        }
    }

    private static TypeSummary Summary(Type type) => new()
    {
        Anchor = AnchorWriter.ForType(type),
        FullName = AnchorWriter.TypeName(type),
        Kind = Describe.TypeKind(type),
        Base = type.BaseType is { } b && AnchorWriter.CanAnchor(b) ? AnchorWriter.ForType(b) : null,
        IsGeneric = type.IsGenericTypeDefinition,
        IsAbstract = type.IsAbstract,
        IsSealed = type.IsSealed,
        Attributes = Describe.AttributeNames(type),
    };

    private static MemberSummary MemberSummary(MemberInfo member) => new()
    {
        Anchor = AnchorWriter.ForMember(member),
        Kind = Describe.MemberKind(member),
        Name = member.Name,
        Signature = Describe.Signature(member),
        Static = Describe.IsStatic(member),
        Visibility = Describe.Visibility(member),
        Attributes = Describe.AttributeNames(member),
    };

    private static List<AttributeInfo> Attributes(object target) =>
        Describe.AttributeData(target).Select(a => AttributeInfo.Read(Describe.Attribute(a), "attribute")).ToList();

    private static ProtocolParameterInfo Parameter(System.Reflection.ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        var byRef = !type.IsByRef ? "none" : parameter.IsOut ? "out" : parameter.IsIn ? "in" : "ref";
        var result = new ProtocolParameterInfo { Name = parameter.Name ?? $"arg{parameter.Position}", Type = AnchorWriter.TypeName(type.IsByRef ? type.GetElementType()! : type), ByRef = byRef };
        var hasDefault = Safe(() => parameter.HasDefaultValue, false);
        result.HasDefault = hasDefault;
        if (hasDefault)
        {
            result.DefaultValue = Constant(Safe(() => parameter.RawDefaultValue, null));
        }

        return result;
    }

    private static JsonValue Constant(object? value) => value switch
    {
        null or DBNull => JsonNull.Instance,
        string s => JsonValue.From(s),
        bool b => JsonValue.From(b),
        char c => JsonValue.From(c.ToString()),
        float f => float.IsNaN(f) || float.IsInfinity(f) ? JsonValue.From(f.ToString(System.Globalization.CultureInfo.InvariantCulture)) : JsonNumber.FromRawText(f.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
        double d => double.IsNaN(d) || double.IsInfinity(d) ? JsonValue.From(d.ToString(System.Globalization.CultureInfo.InvariantCulture)) : new JsonNumber(d),
        ulong u => new JsonNumber(u),
        decimal m => new JsonNumber(m),
        IConvertible number => new JsonNumber(Convert.ToInt64(number, System.Globalization.CultureInfo.InvariantCulture)),
        var other => JsonValue.From(other.ToString()),
    };

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

    private sealed record TypesPage(List<Type> Types, int Offset);

    private sealed class TreeBudget
    {
        public int Nodes { get; private set; }

        public bool Truncated { get; private set; }

        public int ArrayMethods { get; set; }

        public bool Take()
        {
            if (Nodes >= MaxTreeNodes)
            {
                Truncated = true;
                return false;
            }

            Nodes++;
            return true;
        }

        public List<string> Limits()
        {
            var limits = XrefIndex.Limits.ToList();
            if (Truncated)
            {
                limits.Add($"The tree was cut at {MaxTreeNodes} nodes: ask for a smaller depth.");
            }

            if (ArrayMethods > 0)
            {
                limits.Add($"{ArrayMethods} call(s) to runtime-provided array methods have no definition and aren't listed.");
            }

            return limits;
        }
    }
}
