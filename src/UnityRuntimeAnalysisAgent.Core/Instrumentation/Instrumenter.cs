using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Data;
using UnityRuntimeAnalysisAgent.Core.Runtime;

namespace UnityRuntimeAnalysisAgent.Core.Instrumentation;

/// <summary>What happens around one call of an instrumented method, as seen by a sink.</summary>
public sealed class CallContext
{
    internal CallContext(MethodBase method, object? instance, object?[]? args, int depth, long frame, long realtimeUs)
    {
        Method = method;
        Instance = instance;
        Args = args;
        ThreadId = Environment.CurrentManagedThreadId;
        Depth = depth;
        Frame = frame;
        RealtimeUs = realtimeUs;
    }

    /// <summary>The instrumented method.</summary>
    public MethodBase Method { get; }

    /// <summary>The instance (null for static methods).</summary>
    public object? Instance { get; }

    /// <summary>The arguments (by position).</summary>
    public object?[]? Args { get; }

    /// <summary>The calling thread's managed id.</summary>
    public int ThreadId { get; }

    /// <summary>Nesting among instrumented calls on this thread (0 at the top).</summary>
    public int Depth { get; }

    /// <summary>The frame, from the pump's clock (patch code never calls Unity).</summary>
    public long Frame { get; }

    /// <summary>Microseconds since the game started.</summary>
    public long RealtimeUs { get; }
}

/// <summary>
/// Something that watches calls of a method (a hook, a trace, a profile, a verification). Called on the calling thread
/// with re-entrancy suppressed; an exception disables the sink (fault isolation).
/// </summary>
public interface IMethodSink
{
    /// <summary>A call starts: returns a token to see its end, or null to ignore this call.</summary>
    object? Enter(CallContext call);

    /// <summary>The call returned.</summary>
    void Exit(CallContext call, object token, object? result, bool hasResult, long elapsedTicks);

    /// <summary>The call threw.</summary>
    void Throw(CallContext call, object token, Exception exception, long elapsedTicks);
}

/// <summary>Carried from a call's prefix to its postfix and finalizer.</summary>
public sealed class HookState
{
    internal HookState(MethodInstrumentation instrumentation, IMethodSink[] sinks, int depth)
    {
        Instrumentation = instrumentation;
        Sinks = sinks;
        Tokens = new object?[sinks.Length];
        Depth = depth;
        Start = Stopwatch.GetTimestamp();
    }

    internal MethodInstrumentation Instrumentation { get; }

    internal IMethodSink[] Sinks { get; }

    internal object?[] Tokens { get; }

    internal int Depth { get; }

    internal long Start { get; }
}

/// <summary>The sinks active on one method (copy-on-write, so patch code reads them without locking).</summary>
public sealed class MethodInstrumentation
{
    internal MethodInstrumentation(MethodBase method, Instrumenter owner)
    {
        Method = method;
        Owner = owner;
    }

    /// <summary>The method.</summary>
    public MethodBase Method { get; }

    /// <summary>The instrumenter that patched it.</summary>
    public Instrumenter Owner { get; }

    internal volatile IMethodSink[] Sinks = Array.Empty<IMethodSink>();
}

/// <summary>
/// The patch methods (static, shared by every instrumented method) and the lookup they use. They return at once when
/// nothing is active on the method or when the agent's own code is running on the thread (re-entrancy).
/// </summary>
public static class PatchDispatch
{
    [ThreadStatic]
    private static int t_busy;

    [ThreadStatic]
    private static int t_depth;

    internal static readonly ConcurrentDictionary<MethodBase, MethodInstrumentation> Active = new(MethodComparer.Instance);

    /// <summary>Compares methods by module, token and generic arguments (the runtime hands out several MethodBase objects for one method).</summary>
    public static IEqualityComparer<MethodBase> Comparer => MethodComparer.Instance;

    /// <summary>Whether the agent's own code is running on this thread (instrumentation stays quiet meanwhile).</summary>
    public static bool Busy => t_busy != 0;

    /// <summary>Runs <paramref name="action"/> with instrumentation suppressed on this thread.</summary>
    public static T Quietly<T>(Func<T> action)
    {
        t_busy++;
        try
        {
            return action();
        }
        finally
        {
            t_busy--;
        }
    }

#pragma warning disable SA1313 // Harmony binds these parameters by name
    /// <summary>Prefix of every instrumented method.</summary>
    public static void Prefix(MethodBase __originalMethod, object __instance, object[] __args, out HookState? __state)
    {
        __state = null;
        if (t_busy != 0 || !Active.TryGetValue(__originalMethod, out var instrumentation))
        {
            return;
        }

        var owner = instrumentation.Owner;
        var sinks = instrumentation.Sinks;
        if (sinks.Length == 0)
        {
            return;
        }

        var state = new HookState(instrumentation, sinks, t_depth++);
        t_busy++;
        try
        {
            var call = owner.Call(__originalMethod, __instance, __args, state.Depth);
            for (var i = 0; i < sinks.Length; i++)
            {
                try
                {
                    state.Tokens[i] = sinks[i].Enter(call);
                }
                catch (Exception e)
                {
                    owner.Fault(instrumentation, sinks[i], e);
                }
            }
        }
        finally
        {
            t_busy--;
        }

        __state = state;
    }

    /// <summary>Postfix of instrumented methods that return a value.</summary>
    public static void Postfix(MethodBase __originalMethod, object __instance, object __result, HookState? __state) => Exit(__originalMethod, __instance, __result, true, __state);

    /// <summary>Postfix of instrumented methods that return nothing (and constructors).</summary>
    public static void PostfixVoid(MethodBase __originalMethod, object __instance, HookState? __state) => Exit(__originalMethod, __instance, null, false, __state);

    /// <summary>Finalizer of every instrumented method: records throws and restores the depth. Never changes the outcome.</summary>
    public static void Finalizer(MethodBase __originalMethod, Exception __exception, HookState? __state)
    {
        if (__state is null)
        {
            return;
        }

        t_depth = __state.Depth;
        if (__exception is null)
        {
            return;
        }

        var owner = __state.Instrumentation.Owner;
        var elapsed = Stopwatch.GetTimestamp() - __state.Start;
        t_busy++;
        try
        {
            var call = owner.Call(__originalMethod, null, null, __state.Depth);
            for (var i = 0; i < __state.Sinks.Length; i++)
            {
                if (__state.Tokens[i] is { } token)
                {
                    try
                    {
                        __state.Sinks[i].Throw(call, token, __exception, elapsed);
                    }
                    catch (Exception e)
                    {
                        owner.Fault(__state.Instrumentation, __state.Sinks[i], e);
                    }
                }
            }
        }
        finally
        {
            t_busy--;
        }
    }
#pragma warning restore SA1313

    private static void Exit(MethodBase method, object? instance, object? result, bool hasResult, HookState? state)
    {
        if (state is null)
        {
            return;
        }

        var owner = state.Instrumentation.Owner;
        var elapsed = Stopwatch.GetTimestamp() - state.Start;
        t_busy++;
        try
        {
            var call = owner.Call(method, instance, null, state.Depth);
            for (var i = 0; i < state.Sinks.Length; i++)
            {
                if (state.Tokens[i] is { } token)
                {
                    try
                    {
                        state.Sinks[i].Exit(call, token, result, hasResult, elapsed);
                    }
                    catch (Exception e)
                    {
                        owner.Fault(state.Instrumentation, state.Sinks[i], e);
                    }
                }
            }
        }
        finally
        {
            t_busy--;
        }
    }

    // Harmony hands the patch the runtime's MethodBase; compare by module and token (and generic arguments).
    private sealed class MethodComparer : IEqualityComparer<MethodBase>
    {
        public static readonly MethodComparer Instance = new();

        public bool Equals(MethodBase? x, MethodBase? y) => ReferenceEquals(x, y) || (x is not null && y is not null
            && x.MetadataToken == y.MetadataToken && x.Module == y.Module && GenericArgs(x) == GenericArgs(y) && x.DeclaringType == y.DeclaringType);

        public int GetHashCode(MethodBase obj) => obj.MetadataToken ^ obj.Module.GetHashCode();

        private static string GenericArgs(MethodBase method) =>
            method.IsGenericMethod ? string.Join(",", method.GetGenericArguments().Select(t => t.AssemblyQualifiedName)) : string.Empty;
    }
}

/// <summary>
/// Owns the agent's patches (one Harmony id, so unpatching never touches the game's or other mods' patches): attaches
/// sinks to methods, patching a method when its first sink arrives and unpatching it when its last one leaves; refuses
/// methods it must not touch; isolates faults; keeps the global record sequence and counters.
/// </summary>
public sealed class Instrumenter : IDisposable
{
    /// <summary>The agent's Harmony id.</summary>
    public const string HarmonyId = "ulm.agent.instrumentation";

    private static readonly string[] OwnAssemblies =
    {
        "UnityRuntimeAnalysisAgent.Core", "UnityRuntimeAnalysisAgent.Unity", "UnityRuntimeAnalysisAgent.BepInEx5", "UnityRuntimeAnalysisAgent.Api",
        "UnityRuntimeAnalysisAgent.Overlay", "UnityLudometry.Protocol", "0Harmony", "HarmonyXInterop",
    };

    // Runtime-critical members the game (and the agent) can't live without being slowed or re-entered; allowed with force.
    private static readonly string[] CriticalTypes =
    {
        "System.Object", "System.String", "System.Array", "System.Type", "System.RuntimeType", "System.Delegate", "System.MulticastDelegate",
        "System.Enum", "System.ValueType", "System.GC", "System.Buffer", "System.Math", "System.Exception", "System.Environment",
        "System.AppDomain", "System.Activator", "System.Diagnostics.Stopwatch", "System.Diagnostics.StackTrace",
    };

    private static readonly string[] CriticalNamespaces = { "System.Threading", "System.Runtime", "System.Reflection", "MonoMod", "HarmonyLib" };

    private readonly object _gate = new();
    private readonly Harmony _harmony = new(HarmonyId);
    private readonly MainThreadPump _pump;
    private readonly Action<string, string, JsonObject> _warning;
    private readonly HashSet<IMethodSink> _faulted = new();
    private long _seq;
    private long _records;

    /// <summary>Creates the instrumenter. <paramref name="warning"/> publishes an <c>agent.warning</c> (code, message, data).</summary>
    public Instrumenter(MainThreadPump pump, int maxMethods, Action<string, string, JsonObject> warning)
    {
        _pump = pump;
        MaxMethods = maxMethods;
        _warning = warning;
    }

    /// <summary>The cap on instrumented methods.</summary>
    public int MaxMethods { get; }

    /// <summary>Methods patched now.</summary>
    public int PatchedMethods => PatchDispatch.Active.Values.Count(i => ReferenceEquals(i.Owner, this));

    /// <summary>Records created so far (for rates).</summary>
    public long Records => Interlocked.Read(ref _records);

    /// <summary>Called for each type seen running (its static constructor has run).</summary>
    public Action<Type>? TypeSeen { get; set; }

    /// <summary>The next record sequence number.</summary>
    public long NextSeq()
    {
        Interlocked.Increment(ref _records);
        return Interlocked.Increment(ref _seq);
    }

    /// <summary>Why a method can't be instrumented, or null when it can.</summary>
    public static string? Refusal(MethodBase method, bool force)
    {
        if (method.ContainsGenericParameters)
        {
            return "It's an open generic method or on an open generic type: give the type arguments (an instantiation).";
        }

        if (method.IsAbstract || (method.GetMethodImplementationFlags() & (MethodImplAttributes.InternalCall | MethodImplAttributes.Runtime)) != 0 || SafeBody(method) is null)
        {
            return "It has no IL body (abstract, extern or implemented by the runtime).";
        }

        var assembly = method.Module.Assembly.GetName().Name ?? string.Empty;
        if (OwnAssemblies.Contains(assembly, StringComparer.Ordinal) || assembly.StartsWith("MonoMod", StringComparison.Ordinal))
        {
            return $"It belongs to {assembly}, which the agent never instruments.";
        }

        var type = method.DeclaringType?.FullName ?? string.Empty;
        var ns = method.DeclaringType?.Namespace ?? string.Empty;
        if (!force && (CriticalTypes.Contains(type, StringComparer.Ordinal) || CriticalNamespaces.Any(n => ns == n || ns.StartsWith(n + ".", StringComparison.Ordinal))))
        {
            return $"{type} is runtime-critical; instrumenting it can slow or break everything. Pass force to do it anyway.";
        }

        return null;
    }

    /// <summary>Attaches a sink (patching the method if it's the first). Throws <c>UNSUPPORTED</c> for refused methods,
    /// <c>BUSY</c> at the cap, and <c>PATCH_FAILED</c> when Harmony can't patch.</summary>
    public void Attach(MethodBase method, IMethodSink sink, bool force)
    {
        if (Refusal(method, force) is { } reason)
        {
            throw new ProtocolException(ErrorCodes.Unsupported, $"{AnchorWriter.MemberName(method)} can't be instrumented: {reason}");
        }

        lock (_gate)
        {
            if (!PatchDispatch.Active.TryGetValue(method, out var instrumentation))
            {
                if (PatchedMethods >= MaxMethods)
                {
                    throw new ProtocolException(ErrorCodes.Busy, $"{MaxMethods} methods are instrumented already (Instrumentation.MaxMethods).");
                }

                // Registered only once patched, with its first sink in place: whatever counts instrumented methods
                // (PatchedMethods, instrumentation.status) then only counts methods whose patch is live.
                instrumentation = new MethodInstrumentation(method, this) { Sinks = new[] { sink } };
                try
                {
                    Patch(method);
                }
                catch (Exception e)
                {
                    throw new ProtocolException(ErrorCodes.PatchFailed, $"{AnchorWriter.MemberName(method)} couldn't be patched: {e.GetType().Name}: {e.Message}", null, e);
                }

                PatchDispatch.Active[method] = instrumentation;
                return;
            }

            instrumentation.Sinks = instrumentation.Sinks.Append(sink).ToArray();
        }
    }

    /// <summary>Detaches a sink (unpatching the method when it was the last).</summary>
    public void Detach(MethodBase method, IMethodSink sink)
    {
        lock (_gate)
        {
            if (!PatchDispatch.Active.TryGetValue(method, out var instrumentation))
            {
                return;
            }

            instrumentation.Sinks = instrumentation.Sinks.Where(s => !ReferenceEquals(s, sink)).ToArray();
            if (instrumentation.Sinks.Length == 0)
            {
                PatchDispatch.Active.TryRemove(method, out _);
                Unpatch(method);
            }
        }
    }

    /// <summary>The methods the agent has patched, as Harmony reports them (for checks after cleanup).</summary>
    public static IReadOnlyList<MethodBase> AgentPatchedMethods() =>
        Harmony.GetAllPatchedMethods().Where(m => Harmony.GetPatchInfo(m) is { } info && info.Owners.Contains(HarmonyId)).ToList();

    /// <summary>Removes every agent patch.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var instrumentation in PatchDispatch.Active.Values.Where(i => ReferenceEquals(i.Owner, this)).ToList())
            {
                instrumentation.Sinks = Array.Empty<IMethodSink>();
                PatchDispatch.Active.TryRemove(instrumentation.Method, out _);
                Unpatch(instrumentation.Method);
            }
        }
    }

    internal CallContext Call(MethodBase method, object? instance, object?[]? args, int depth)
    {
        var clock = _pump.Clock;
        var sinceTickMs = Math.Max(0, _pump.NowMs - _pump.LastTickAtMs);
        TypeSeen?.Invoke(method.DeclaringType!);
        return new CallContext(method, instance, args, depth, clock.FrameCount, (long)((clock.Realtime * 1_000_000) + (sinceTickMs * 1000)));
    }

    // A sink that throws is detached (the game call goes on) and reported once.
    internal void Fault(MethodInstrumentation instrumentation, IMethodSink sink, Exception error)
    {
        lock (_gate)
        {
            if (!_faulted.Add(sink))
            {
                return;
            }
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                Detach(instrumentation.Method, sink);
                (sink as IFaultAware)?.Faulted(error);
                _warning("INSTRUMENTATION_FAULT", $"Instrumentation on {AnchorWriter.MemberName(instrumentation.Method)} failed and was removed: {error.GetType().Name}: {error.Message}",
                    new JsonObject { { "method", AnchorWriter.ToJson(AnchorWriter.ForMember(instrumentation.Method)) } });
            }
            catch (Exception)
            {
                // Reporting a fault must never fail the process.
            }
        });
    }

    private void Patch(MethodBase method)
    {
        var dispatch = typeof(PatchDispatch);
        var returnsValue = method is MethodInfo info && info.ReturnType != typeof(void);
        InliningHeuristic.RememberFlags(method);
        _harmony.Patch(
            method,
            prefix: new HarmonyMethod(dispatch.GetMethod(nameof(PatchDispatch.Prefix))),
            postfix: new HarmonyMethod(dispatch.GetMethod(returnsValue ? nameof(PatchDispatch.Postfix) : nameof(PatchDispatch.PostfixVoid))),
            finalizer: new HarmonyMethod(dispatch.GetMethod(nameof(PatchDispatch.Finalizer))));
    }

    private void Unpatch(MethodBase method)
    {
        try
        {
            _harmony.Unpatch(method, HarmonyPatchType.All, HarmonyId);
        }
        catch (Exception e)
        {
            _warning("UNPATCH_FAILED", $"{AnchorWriter.MemberName(method)} couldn't be unpatched: {e.Message}", new JsonObject());
        }
    }

    private static MethodBody? SafeBody(MethodBase method)
    {
        try
        {
            return method.GetMethodBody();
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>A sink that wants to know it was removed after a fault.</summary>
public interface IFaultAware
{
    /// <summary>The sink threw and was detached.</summary>
    void Faulted(Exception error);
}

/// <summary>Builds instrumentation records, capturing values shallowly with the value encoder's safe mode.</summary>
public sealed class RecordBuilder
{
    private readonly DataModel _data;

    /// <summary>Creates the builder.</summary>
    public RecordBuilder(DataModel data) => _data = data;

    /// <summary>A record for one phase of a call.</summary>
    public InstrumentationRecord Build(Instrumenter owner, CallContext call, string phase, CaptureOptions capture, object? result = null, bool hasResult = false,
        Exception? exception = null, long? elapsedTicks = null)
    {
        var record = new InstrumentationRecord
        {
            Seq = owner.NextSeq(),
            Phase = phase,
            Method = AnchorWriter.ForMember(call.Method),
            Frame = call.Frame,
            RealtimeUs = call.RealtimeUs,
            ThreadId = call.ThreadId,
            Depth = call.Depth,
            DurationUs = elapsedTicks is { } ticks ? ticks * 1_000_000 / Stopwatch.Frequency : null,
        };

        if (capture.Instance || capture.Args || capture.Result)
        {
            var writer = _data.Writer(new ViewOptions { Safe = true, Capture = true, Retain = capture.Retain, Depth = capture.Depth, MaxString = 256, MaxItems = 16, MaxMembers = 32 }, call.Frame);
            if (capture.Instance && call.Instance is not null && phase == "enter")
            {
                record.Instance = writer.Write(call.Instance, new Place());
            }

            if (capture.Args && call.Args is not null && phase == "enter")
            {
                record.Args = call.Args.Select(a => writer.Write(a, new Place())).ToList();
            }

            if (capture.Result && hasResult)
            {
                record.Result = writer.Write(result, new Place());
            }
        }

        if (exception is not null)
        {
            record.Exception = new CapturedException { Type = exception.GetType().FullName ?? exception.GetType().Name, Message = exception.Message, Stack = exception.StackTrace };
        }

        if (capture.Stack > 0)
        {
            record.Stack = Stack(capture.Stack);
        }

        return record;
    }

    // The calling frames above the instrumented method (the agent's own frames and Harmony's trampolines skipped).
    private static List<Anchor> Stack(int count)
    {
        var anchors = new List<Anchor>();
        foreach (var frame in new StackTrace(1, false).GetFrames() ?? Array.Empty<StackFrame>())
        {
            var method = frame.GetMethod();
            if (method?.DeclaringType is null || method.DeclaringType.Assembly == typeof(RecordBuilder).Assembly
                || method.DeclaringType.Assembly.GetName().Name is "0Harmony" || method.Name.Contains("_Patch"))
            {
                continue;
            }

            anchors.Add(AnchorWriter.ForMember(method));
            if (anchors.Count >= count)
            {
                break;
            }
        }

        return anchors;
    }
}

/// <summary>What a record captures.</summary>
public sealed class CaptureOptions
{
    /// <summary>Nothing.</summary>
    public static readonly CaptureOptions None = new();

    /// <summary>The instance (at enter).</summary>
    public bool Instance { get; set; }

    /// <summary>The arguments (at enter).</summary>
    public bool Args { get; set; }

    /// <summary>The result (at exit).</summary>
    public bool Result { get; set; }

    /// <summary>Stack frames to capture (0 = none).</summary>
    public int Stack { get; set; }

    /// <summary>Depth of captured values (0–2).</summary>
    public int Depth { get; set; } = 1;

    /// <summary>Keep captured objects alive for later expansion.</summary>
    public bool Retain { get; set; }
}
