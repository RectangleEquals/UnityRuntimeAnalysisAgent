using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

namespace UnityRuntimeAnalysisAgent.Api;

/// <summary>
/// What code run by the agent (snippets, live patches) can use from it. A snippet receives its context as the argument
/// of its entry method: <c>static object Run(IAgentContext ctx)</c>, or <c>static IEnumerator Run(IAgentContext ctx)</c> for
/// work over several frames (yield <see cref="Wait"/>'s values, finish with <see cref="Return"/>). Live patches reach the
/// agent through <see cref="AgentApi.Current"/>. Everything here is used on the game's main thread.
/// </summary>
public interface IAgentContext
{
    /// <summary>The request's arguments: numbers as <see cref="long"/> or <see cref="double"/>, strings, booleans, nulls,
    /// arrays as <c>object?[]</c> and objects as dictionaries. Empty for live patches.</summary>
    IReadOnlyDictionary<string, object?> Args { get; }

    /// <summary>The agent's variables, the same ones clients read and write with <c>vars.*</c>.</summary>
    IAgentVars Vars { get; }

    /// <summary>State kept between the runs of a named session, for snippets that build on each other. A run without a
    /// session gets a fresh, empty dictionary.</summary>
    IDictionary<string, object?> Session { get; }

    /// <summary>Writes to the agent's log; a snippet's lines are also returned with its result.</summary>
    IAgentLog Log { get; }

    /// <summary>Registers an object with the agent and returns its handle, which clients can use as a target.</summary>
    long Handle(object value);

    /// <summary>The object behind a handle.</summary>
    /// <exception cref="ArgumentException">The handle is unknown, released, or its Unity object was destroyed.</exception>
    object? Resolve(long handle);

    /// <summary>The type or member an anchor (<c>{"mvid":…,"token":…}</c>, as JSON) refers to.</summary>
    /// <exception cref="ArgumentException">The anchor doesn't resolve in the loaded game.</exception>
    MemberInfo Resolve(string anchorJson);

    /// <summary>Waits to yield from an iterator snippet.</summary>
    IAgentWait Wait { get; }

    /// <summary>Sets the result of an iterator snippet (a synchronous snippet returns its result instead).</summary>
    void Return(object? value);

    /// <summary>Sends a custom event to the client (<c>exec.emit</c>) and adds it to the snippet's result.</summary>
    void Emit(string kind, object? payload = null);

    /// <summary>Counts calls of game methods while the snippet runs.</summary>
    IAgentInstrumentation Hooks { get; }

    /// <summary>Cancelled when the request is cancelled or times out. Iterator snippets are stopped at their next yield
    /// anyway; a synchronous snippet can't be interrupted, so long loops should check it.</summary>
    CancellationToken Cancellation { get; }

    /// <summary>The folder the request allows the snippet to write to, or null if it gave none.</summary>
    string? OutDir { get; }
}

/// <summary>The agent's variables (shared with the <c>vars.*</c> methods).</summary>
public interface IAgentVars
{
    /// <summary>The variables' names.</summary>
    IReadOnlyList<string> Names { get; }

    /// <summary>Reads a variable: the object behind a handle variable, the type of a static variable, or a value
    /// variable's value (decoded like <see cref="IAgentContext.Args"/>).</summary>
    /// <exception cref="KeyNotFoundException">There is no such variable.</exception>
    object? Get(string name);

    /// <summary>Reads a variable if it exists.</summary>
    bool TryGet(string name, out object? value);

    /// <summary>Sets a variable: null, numbers, booleans and strings are stored as values; other objects are registered as
    /// handles and stored as handle variables.</summary>
    void Set(string name, object? value);

    /// <summary>Deletes a variable; false if it didn't exist.</summary>
    bool Delete(string name);
}

/// <summary>Writes to the agent's log.</summary>
public interface IAgentLog
{
    /// <summary>An informational line.</summary>
    void Info(string message);

    /// <summary>A warning.</summary>
    void Warning(string message);

    /// <summary>An error.</summary>
    void Error(string message);
}

/// <summary>Waits for iterator snippets: <c>yield return ctx.Wait.Frames(10);</c>. <c>yield return null</c> waits one
/// frame.</summary>
public interface IAgentWait
{
    /// <summary>Resumes after this many frames (at least one).</summary>
    object Frames(int count);

    /// <summary>Resumes after this much real time, in seconds (independent of the game's time scale).</summary>
    object Seconds(double seconds);

    /// <summary>Resumes as soon as the condition is true (checked every frame); fails the snippet with a timeout if it isn't
    /// within <paramref name="timeoutMs"/>.</summary>
    object Until(Func<bool> condition, int timeoutMs);

    /// <summary>Resumes at the end of the current frame, after rendering.</summary>
    object EndOfFrame { get; }
}

/// <summary>Counts calls of game methods for the duration of a snippet (the counters are removed when it ends).</summary>
public interface IAgentInstrumentation
{
    /// <summary>Starts counting the calls of a method.</summary>
    /// <exception cref="InvalidOperationException">The method can't be instrumented (the message says why).</exception>
    IAgentHitCounter Count(MethodBase method);
}

/// <summary>Counts the calls of one method. Disposing it stops counting.</summary>
public interface IAgentHitCounter : IDisposable
{
    /// <summary>The method counted.</summary>
    MethodBase Method { get; }

    /// <summary>Calls so far.</summary>
    long Hits { get; }

    /// <summary>A wait (to yield) that resumes once <see cref="Hits"/> reaches <paramref name="count"/>, or fails the snippet
    /// with a timeout after <paramref name="timeoutMs"/>.</summary>
    object WaitForHits(long count, int timeoutMs);
}
