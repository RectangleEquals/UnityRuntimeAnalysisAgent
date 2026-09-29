using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Code;

namespace UnityRuntimeAnalysisAgent.Core.Instrumentation;

/// <summary>
/// How likely the Mono JIT is to inline a method (a patch on an inlined method never fires): small, non-virtual, no
/// exception clauses, no loops, not marked <c>NoInlining</c>, not a constructor → likely inlined.
/// </summary>
public static class InliningHeuristic
{
    /// <summary>IL size at or below which the JIT inlines readily.</summary>
    public const int SmallIl = 32;

    // Patching marks a method NoInlining at runtime, so its flags are read before the agent first patches it.
    private static readonly ConcurrentDictionary<MethodBase, MethodImplAttributes> s_originalFlags = new(PatchDispatch.Comparer);

    /// <summary>Keeps the method's implementation flags as they were before any patch of the agent's.</summary>
    internal static void RememberFlags(MethodBase method) => s_originalFlags.GetOrAdd(method, m => m.GetMethodImplementationFlags());

    /// <summary>The method's IL size, the risk (<c>low</c>, <c>medium</c>, <c>high</c>) and the reasons.</summary>
    public static (int IlSize, string Risk, List<string> Reasons) Assess(MethodBase method)
    {
        var reasons = new List<string>();
        var il = IlReader.Body(method) ?? Array.Empty<byte>();
        var blocking = false;
        reasons.Add($"IL body is {il.Length} bytes ({(il.Length <= SmallIl ? "small enough to inline" : "larger than the usual inlining limit of " + SmallIl)})");

        if (method.IsConstructor)
        {
            reasons.Add("it's a constructor (not inlined)");
            blocking = true;
        }

        if (method.IsVirtual && !method.IsFinal)
        {
            reasons.Add("it's virtual (calls go through the vtable)");
            blocking = true;
        }

        var flags = s_originalFlags.TryGetValue(method, out var original) ? original : method.GetMethodImplementationFlags();
        if ((flags & MethodImplAttributes.NoInlining) != 0)
        {
            reasons.Add("it's marked NoInlining");
            blocking = true;
        }

        try
        {
            if (method.GetMethodBody()?.ExceptionHandlingClauses.Count > 0)
            {
                reasons.Add("it has exception handling clauses (not inlined)");
                blocking = true;
            }
        }
        catch (Exception)
        {
            // No readable body: nothing more to learn.
        }

        if (HasBackwardBranch(il))
        {
            reasons.Add("it has a loop (backward branch; not inlined)");
            blocking = true;
        }

        var risk = blocking ? "low" : il.Length <= SmallIl ? "high" : il.Length <= 2 * SmallIl ? "medium" : "low";
        return (il.Length, risk, reasons);
    }

    private static bool HasBackwardBranch(byte[] il)
    {
        try
        {
            return IlReader.Decode(il).Any(i => i.Operand switch
            {
                int target when i.OpCode.OperandType is System.Reflection.Emit.OperandType.InlineBrTarget or System.Reflection.Emit.OperandType.ShortInlineBrTarget => target <= i.Offset,
                int[] targets => targets.Any(t => t <= i.Offset),
                _ => false,
            });
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>A verification's sink: counts calls and keeps the first.</summary>
internal sealed class VerifySink : IMethodSink
{
    private readonly RecordBuilder _records;
    private readonly Instrumenter _instrumenter;
    private long _hits;
    private InstrumentationRecord? _first;

    public VerifySink(RecordBuilder records, Instrumenter instrumenter)
    {
        _records = records;
        _instrumenter = instrumenter;
    }

    public long Hits => Interlocked.Read(ref _hits);

    public InstrumentationRecord? First => Volatile.Read(ref _first);

    public object? Enter(CallContext call)
    {
        if (Interlocked.Increment(ref _hits) == 1)
        {
            Volatile.Write(ref _first, _records.Build(_instrumenter, call, "enter", CaptureOptions.None));
        }

        return null;
    }

    public void Exit(CallContext call, object token, object? result, bool hasResult, long elapsedTicks)
    {
    }

    public void Throw(CallContext call, object token, Exception exception, long elapsedTicks)
    {
    }
}

/// <summary>What makes a verified method run. <c>invoke</c> is built in; <c>exec</c> and <c>ui.click</c> are registered by
/// the milestones that add snippets and UI control; <c>wait</c> leaves it to the user.</summary>
public interface ITrigger
{
    /// <summary>The trigger kind (<c>invoke</c>, <c>exec</c>, <c>ui.click</c>).</summary>
    string Kind { get; }

    /// <summary>Fires the trigger on the main thread; returns a note for the result's reasons (or null).</summary>
    string? Fire(Trigger trigger, string param);
}
