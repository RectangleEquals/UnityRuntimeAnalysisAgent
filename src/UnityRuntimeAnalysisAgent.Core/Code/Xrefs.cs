using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using System.Threading;
using UnityRuntimeAnalysisAgent.Core.Data;

namespace UnityRuntimeAnalysisAgent.Core.Code;

/// <summary>What a cross-reference is.</summary>
public enum XrefKind
{
    /// <summary><c>call</c>, <c>callvirt</c>, <c>newobj</c>, <c>ldftn</c>, <c>ldvirtftn</c>.</summary>
    Call,

    /// <summary>Field read, write or address.</summary>
    Field,

    /// <summary><c>ldstr</c>.</summary>
    String,

    /// <summary><c>newobj</c>, <c>newarr</c>, <c>box</c>, <c>initobj</c>.</summary>
    Alloc,

    /// <summary><c>castclass</c>, <c>isinst</c>, <c>ldtoken</c>, <c>sizeof</c>.</summary>
    TypeRef,
}

/// <summary>One reference found in a method body.</summary>
public sealed class Xref
{
    internal Xref(XrefKind kind, MethodBase method, int offset, string opcode, int token)
    {
        Kind = kind;
        Method = method;
        Offset = offset;
        Opcode = opcode;
        Token = token;
    }

    /// <summary>What it is.</summary>
    public XrefKind Kind { get; }

    /// <summary>The method whose body has it.</summary>
    public MethodBase Method { get; }

    /// <summary>IL offset.</summary>
    public int Offset { get; }

    /// <summary>The opcode name.</summary>
    public string Opcode { get; }

    /// <summary>The raw metadata token of the operand.</summary>
    public int Token { get; }

    /// <summary>The callee, field or type.</summary>
    public MemberInfo? Target { get; internal set; }

    /// <summary>The string literal.</summary>
    public string? Literal { get; internal set; }

    /// <summary>Field access: <c>read</c>, <c>write</c> or <c>address</c>; type ref: the usage.</summary>
    public string? Access { get; internal set; }

    /// <summary>Whether the field is static.</summary>
    public bool Static { get; internal set; }
}

/// <summary>Finds the references in method bodies.</summary>
public static class XrefScanner
{
    /// <summary>Every method and constructor with a body in a module's loadable types, by type token then method token.</summary>
    public static IEnumerable<MethodBase> Methods(Module module) => AnchorResolver.LoadableTypes(module)
        .OrderBy(t => t.MetadataToken)
        .SelectMany(t => SafeMethods(t));

    /// <summary>Scans one method body: references go to <paramref name="found"/>, problems to <paramref name="error"/>.</summary>
    public static void Scan(MethodBase method, Action<Xref> found, Action<string> error)
    {
        var il = IlReader.Body(method);
        if (il is null)
        {
            return;
        }

        List<IlInstruction> instructions;
        try
        {
            instructions = IlReader.Decode(il);
        }
        catch (Exception e)
        {
            error("body unreadable: " + e.Message);
            return;
        }

        foreach (var instruction in instructions)
        {
            if (!instruction.HasToken)
            {
                continue;
            }

            var name = instruction.OpCode.Name!;
            var kind = Classify(name, instruction.OpCode.OperandType);
            if (kind is null)
            {
                continue;
            }

            object resolved;
            try
            {
                resolved = IlReader.Resolve(instruction, method);
            }
            catch (Exception e)
            {
                error($"operand unresolved at IL_{instruction.Offset:x4} ({name} 0x{(int)instruction.Operand!:x8}): {e.GetBaseException().Message}");
                continue;
            }

            var xref = new Xref(kind.Value, method, instruction.Offset, name, (int)instruction.Operand!);
            switch (kind.Value)
            {
                case XrefKind.Call when resolved is MethodBase callee:
                    xref.Target = callee;
                    found(xref);
                    if (name == "newobj" && callee.DeclaringType is { } created)
                    {
                        found(new Xref(XrefKind.Alloc, method, instruction.Offset, name, xref.Token) { Target = created });
                    }

                    break;
                case XrefKind.Field when resolved is FieldInfo field:
                    xref.Target = field;
                    xref.Access = name.EndsWith("a", StringComparison.Ordinal) ? "address" : name.StartsWith("st", StringComparison.Ordinal) ? "write" : "read";
                    xref.Static = name.Contains("sfld");
                    found(xref);
                    break;
                case XrefKind.String when resolved is string literal:
                    xref.Literal = literal;
                    found(xref);
                    break;
                case XrefKind.Alloc when resolved is Type allocated:
                    xref.Target = allocated;
                    found(xref);
                    break;
                case XrefKind.TypeRef when resolved is Type referenced:
                    xref.Target = referenced;
                    xref.Access = name;
                    found(xref);
                    break;
            }
        }
    }

    /// <summary>The identity of a member across generic instantiations: its module and definition token.</summary>
    public static (Guid Module, int Token) Key(MemberInfo member)
    {
        var definition = member is Type t ? TypeIndex.Key(t) : member;
        return (definition.Module.ModuleVersionId, definition.MetadataToken);
    }

    private static XrefKind? Classify(string opcode, OperandType operandType) => opcode switch
    {
        "call" or "callvirt" or "newobj" or "ldftn" or "ldvirtftn" => XrefKind.Call,
        "ldfld" or "ldsfld" or "stfld" or "stsfld" or "ldflda" or "ldsflda" => XrefKind.Field,
        "ldstr" => XrefKind.String,
        "newarr" or "box" or "initobj" => XrefKind.Alloc,
        "castclass" or "isinst" or "sizeof" => XrefKind.TypeRef,
        "ldtoken" when operandType == OperandType.InlineTok => XrefKind.TypeRef,
        _ => null,
    };

    private static IEnumerable<MethodBase> SafeMethods(Type type)
    {
        try
        {
            return type.GetMethods(Describe.Declared).Cast<MethodBase>().Concat(type.GetConstructors(Describe.Declared))
                .OrderBy(m => m.MetadataToken).ToList();
        }
        catch (Exception)
        {
            return Array.Empty<MethodBase>();
        }
    }
}

/// <summary>
/// Interactive cross-reference queries, from per-module indexes built on first use (on the calling worker thread,
/// cancellable) and kept. A reference to a member can only come from its own module or from assemblies that reference
/// its assembly, so only those are indexed for a query.
/// </summary>
public sealed class XrefIndex
{
    /// <summary>What runtime cross-references can't show (reported with results).</summary>
    public static readonly IReadOnlyList<string> Limits = new[]
    {
        "Virtual and interface calls are listed with their declared target (code.implementations finds the overrides).",
        "Calls made through reflection, delegates built at runtime and visual-scripting graphs aren't visible in IL.",
        "Only loaded assemblies are indexed; methods of dynamic assemblies may have no body.",
    };

    private readonly ModuleMap _modules;
    private readonly ConcurrentDictionary<Module, List<Xref>> _built = new();
    private readonly ConcurrentDictionary<Module, object> _building = new();

    /// <summary>Creates the index over the module map.</summary>
    public XrefIndex(ModuleMap modules) => _modules = modules;

    /// <summary>References in the modules that can refer to <paramref name="target"/>.</summary>
    public IEnumerable<Xref> Referencing(MemberInfo target, XrefKind kind, CancellationToken cancellation)
    {
        var key = XrefScanner.Key(target);
        var assembly = (target is Type t ? TypeIndex.Key(t) : target).Module.Assembly;
        var name = assembly.GetName().Name;
        foreach (var module in _modules.Modules.Where(m => m.Assembly == assembly || References(m.Assembly, name)).OrderBy(m => m.Assembly.GetName().Name, StringComparer.Ordinal))
        {
            foreach (var xref in For(module, cancellation))
            {
                if (xref.Kind == kind && xref.Target is not null && XrefScanner.Key(xref.Target) == key)
                {
                    yield return xref;
                }
            }
        }
    }

    /// <summary>String references matching a pattern, in every module the filter includes.</summary>
    public IEnumerable<Xref> Strings(Regex pattern, AssemblyFilter filter, CancellationToken cancellation)
    {
        foreach (var module in _modules.Modules.Where(m => filter.Includes(m.Assembly.GetName().Name ?? "?")).OrderBy(m => m.Assembly.GetName().Name, StringComparer.Ordinal))
        {
            foreach (var xref in For(module, cancellation))
            {
                if (xref.Kind == XrefKind.String && pattern.IsMatch(xref.Literal!))
                {
                    yield return xref;
                }
            }
        }
    }

    /// <summary>The references in one method's body (not cached: bodies are read directly).</summary>
    public static List<Xref> In(MethodBase method)
    {
        var found = new List<Xref>();
        XrefScanner.Scan(method, found.Add, _ => { });
        return found;
    }

    private List<Xref> For(Module module, CancellationToken cancellation)
    {
        if (_built.TryGetValue(module, out var ready))
        {
            return ready;
        }

        lock (_building.GetOrAdd(module, _ => new object()))
        {
            if (_built.TryGetValue(module, out ready))
            {
                return ready;
            }

            var found = new List<Xref>();
            foreach (var method in XrefScanner.Methods(module))
            {
                cancellation.ThrowIfCancellationRequested();
                XrefScanner.Scan(method, found.Add, _ => { });
            }

            _built[module] = found;
            return found;
        }
    }

    private static bool References(Assembly assembly, string? name)
    {
        try
        {
            return assembly.GetReferencedAssemblies().Any(r => r.Name == name);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
