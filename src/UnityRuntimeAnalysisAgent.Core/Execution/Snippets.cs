using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Api;
using UnityRuntimeAnalysisAgent.Core.Data;
using UnityRuntimeAnalysisAgent.Core.Dispatch;
using UnityRuntimeAnalysisAgent.Core.Runtime;

namespace UnityRuntimeAnalysisAgent.Core.Execution;

/// <summary>A snippet's entry point, bound and ready to run.</summary>
internal sealed class SnippetEntry
{
    public SnippetEntry(LoadedAssembly assembly, MethodInfo method)
    {
        Assembly = assembly;
        Method = method;
    }

    public LoadedAssembly Assembly { get; }

    public MethodInfo Method { get; }

    /// <summary>Whether it runs over several frames (it returns an <see cref="IEnumerator"/>).</summary>
    public bool IsIterator => typeof(IEnumerator).IsAssignableFrom(Method.ReturnType);
}

/// <summary>
/// Runs snippets: <c>static object Run(IAgentContext)</c> within one frame, or <c>static IEnumerator Run(IAgentContext)</c>
/// over several (yielding the context's waits, or null for one frame), always on the main thread. Named sessions keep
/// their state between runs. A synchronous snippet can't be interrupted; an iterator one stops at its next yield when
/// the request is cancelled or its <c>timeoutMs</c> passes.
/// </summary>
internal sealed class SnippetRunner
{
    public const string DefaultEntryType = "Snippet";
    public const string DefaultEntryMethod = "Run";

    private readonly ContextServices _services;
    private readonly AssemblyLoader _loader;
    private readonly Dictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);

    public SnippetRunner(ContextServices services, AssemblyLoader loader)
    {
        _services = services;
        _loader = loader;
    }

    /// <summary>Loads the assembly and binds its entry point (<c>EXEC_FAILED</c> with phase <c>load</c> or <c>bind</c>).</summary>
    public SnippetEntry Bind(byte[] bytes, string? entryType, string? entryMethod)
    {
        var assembly = _loader.Load(bytes);
        var typeName = string.IsNullOrEmpty(entryType) ? DefaultEntryType : entryType!;
        var methodName = string.IsNullOrEmpty(entryMethod) ? DefaultEntryMethod : entryMethod!;
        Type? type;
        try
        {
            type = assembly.Assembly.GetType(typeName, throwOnError: false)
                ?? LoadableTypes(assembly.Assembly).FirstOrDefault(t => t.Name == typeName);
        }
        catch (Exception e)
        {
            throw AgentErrors.ExecFailed("bind", $"The types of {assembly.Name} can't be read: {e.Message}", e);
        }

        if (type is null)
        {
            throw AgentErrors.ExecFailed("bind", $"{assembly.Name} has no type {typeName}.");
        }

        var method = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == methodName && m.GetParameters() is { Length: 1 } ps && ps[0].ParameterType == typeof(IAgentContext));
        if (method is null)
        {
            throw AgentErrors.ExecFailed("bind",
                $"{type.FullName} has no static {methodName}(IAgentContext): use 'static object {methodName}(IAgentContext ctx)' or 'static IEnumerator {methodName}(IAgentContext ctx)'.");
        }

        if (method.ContainsGenericParameters)
        {
            throw AgentErrors.ExecFailed("bind", $"{type.FullName}.{methodName} can't be generic.");
        }

        return new SnippetEntry(assembly, method);
    }

    /// <summary>Runs the snippet on the main thread; the deferred completes with its <see cref="ExecRunResult"/>.</summary>
    public Deferred Run(SnippetEntry entry, ExecRunParams p, CancellationToken cancellation)
    {
        var deferred = new Deferred();
        var timeout = p.TimeoutMs is { } ms ? new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Min(ms, int.MaxValue))) : null;
        var linked = timeout is null ? CancellationTokenSource.CreateLinkedTokenSource(cancellation) : CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeout.Token);
        var clock = Stopwatch.StartNew();
        AgentContext? context = null;
        long startFrame = 0;

        void Finish()
        {
            context?.End();
            linked.Dispose();
            timeout?.Dispose();
        }

        _services.Pump.Enqueue(new PumpWork(
            () =>
            {
                startFrame = _services.Pump.Clock.FrameCount;
                context = new AgentContext(_services, p.Args, p.Session, SessionFor(p.Session), linked.Token, p.OutDir, collect: true);
                var returned = Invoke(entry, context);
                return entry.IsIterator && returned is IEnumerator iterator ? Drive(iterator) : new Holder(returned);
            },
            result =>
            {
                try
                {
                    var value = result is Holder holder ? holder.Value : context!.Returned;
                    var writer = _services.Data.Writer(ViewOptions.From(p.View), _services.Pump.Clock.FrameCount);
                    deferred.Complete(new ExecRunResult
                    {
                        Value = writer.Write(value, new Place()),
                        Logs = context!.Logs,
                        Emitted = context.Emitted,
                        DurationMs = clock.ElapsedMilliseconds,
                        Frames = Math.Max(0, _services.Pump.Clock.FrameCount - startFrame),
                    });
                }
                catch (Exception e)
                {
                    deferred.Fail(e);
                }
                finally
                {
                    Finish();
                }
            },
            error =>
            {
                deferred.Fail(error is OperationCanceledException && timeout is { IsCancellationRequested: true } && !cancellation.IsCancellationRequested
                    ? new ProtocolException(ErrorCodes.Timeout, $"The snippet didn't finish within its timeoutMs ({p.TimeoutMs} ms).")
                    : error);
                Finish();
            },
            linked.Token));
        return deferred;
    }

    /// <summary>Starts a snippet without waiting for it (hook.verify's <c>exec</c> trigger); main thread only. Returns a note
    /// if it failed synchronously.</summary>
    public string? Start(SnippetEntry entry)
    {
        var context = new AgentContext(_services, null, null, new Dictionary<string, object?>(), CancellationToken.None, null, collect: true);
        object? returned;
        try
        {
            returned = Invoke(entry, context);
        }
        catch (ProtocolException e)
        {
            context.End();
            return $"the trigger snippet failed: {e.Message}";
        }

        if (entry.IsIterator && returned is IEnumerator iterator)
        {
            _services.Pump.Enqueue(new PumpWork(() => Drive(iterator), _ => context.End(), _ => context.End()));
        }
        else
        {
            context.End();
        }

        return null;
    }

    public List<ExecSession> Sessions()
    {
        lock (_sessions)
        {
            return _sessions.Select(s => new ExecSession
            {
                Name = s.Key,
                Variables = s.Value.State.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(),
                CreatedAt = s.Value.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                Runs = s.Value.Runs,
            }).OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
        }
    }

    public bool Close(string session)
    {
        lock (_sessions)
        {
            return _sessions.Remove(session);
        }
    }

    public void CloseAll()
    {
        lock (_sessions)
        {
            _sessions.Clear();
        }
    }

    private IDictionary<string, object?> SessionFor(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        lock (_sessions)
        {
            if (!_sessions.TryGetValue(name!, out var state))
            {
                _sessions[name!] = state = new SessionState();
            }

            state.Runs++;
            return state.State;
        }
    }

    private static object? Invoke(SnippetEntry entry, AgentContext context)
    {
        try
        {
            return entry.Method.Invoke(null, new object[] { context });
        }
        catch (TargetInvocationException e)
        {
            var inner = e.InnerException ?? e;
            throw AgentErrors.ExecFailed("run", $"The snippet threw {inner.GetType().Name}: {inner.Message}", inner);
        }
    }

    // Steps the snippet's iterator for the pump: its waits pass through, null waits a frame, and its exceptions are
    // reported as EXEC_FAILED (run). The pump completes the routine with null; the result is the context's Return value.
    private static IEnumerator Drive(IEnumerator snippet)
    {
        try
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = snippet.MoveNext();
                }
                catch (Exception e)
                {
                    throw AgentErrors.ExecFailed("run", $"The snippet threw {e.GetType().Name}: {e.Message}", e);
                }

                if (!moved)
                {
                    yield break;
                }

                switch (snippet.Current)
                {
                    case null:
                        yield return PumpWait.NextFrame;
                        break;
                    case PumpWait wait:
                        yield return wait;
                        break;
                    case var other:
                        throw AgentErrors.ExecFailed("run",
                            $"The snippet yielded a {other.GetType().Name}; yield ctx.Wait.Frames/Seconds/Until/EndOfFrame, or null for one frame, and set the result with ctx.Return.");
                }
            }
        }
        finally
        {
            (snippet as IDisposable)?.Dispose();
        }
    }

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.Where(t => t is not null)!;
        }
    }

    // Keeps a synchronous result (even an enumerable one) from being taken for a routine by the pump.
    private sealed class Holder
    {
        public Holder(object? value) => Value = value;

        public object? Value { get; }
    }

    private sealed class SessionState
    {
        public Dictionary<string, object?> State { get; } = new(StringComparer.Ordinal);

        public DateTime CreatedAt { get; } = DateTime.UtcNow;

        public long Runs { get; set; }
    }
}
