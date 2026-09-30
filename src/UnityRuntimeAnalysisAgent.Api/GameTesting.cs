using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading;

namespace UnityRuntimeAnalysisAgent.Api;

/// <summary>
/// Marks a class holding in-game tests. The agent creates one instance per run (with its parameterless constructor) for
/// all its tests, or uses its static methods.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class GameTestFixtureAttribute : Attribute
{
}

/// <summary>
/// Marks an in-game test: <c>void Name(GameTestContext ctx)</c> (one frame) or <c>IEnumerator Name(GameTestContext ctx)</c>
/// (over several frames; yield the context's waits, or null for one frame). Tests run in declaration order unless
/// <see cref="Order"/> says otherwise.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class GameTestAttribute : Attribute
{
    /// <summary>How long the test (with its setup) may take, in milliseconds of real time; 0 uses the run's default. It is
    /// checked whenever the test yields.</summary>
    public int TimeoutMs { get; set; }

    /// <summary>A category to filter runs by.</summary>
    public string? Category { get; set; }

    /// <summary>A scene that must be loaded for the test to run: the runner waits for it (up to the test's timeout) and
    /// skips the test if it isn't loaded by then.</summary>
    public string? RequiresScene { get; set; }

    /// <summary>Runs before tests with a higher order (default 0; ties keep declaration order).</summary>
    public int Order { get; set; }

    /// <summary>If set, the test isn't run and is reported as skipped with this reason.</summary>
    public string? Skip { get; set; }
}

/// <summary>Runs before each test of its fixture (same signatures as a test).</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class GameTestSetUpAttribute : Attribute
{
}

/// <summary>Runs after each test of its fixture, whatever the test's outcome (same signatures as a test). Restore the
/// state the test changed here: tests run in the live game, with nothing isolating them.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class GameTestTearDownAttribute : Attribute
{
}

/// <summary>Runs once before the fixture's tests (same signatures as a test).</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class GameTestFixtureSetUpAttribute : Attribute
{
}

/// <summary>Runs once after the fixture's tests, whatever their outcomes (same signatures as a test).</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class GameTestFixtureTearDownAttribute : Attribute
{
}

/// <summary>
/// What an in-game test gets from the agent: waits, assertions, its log, scoped instrumentation (removed when the test
/// ends), sampled values, the game's UI, the agent's variables, cancellation, and values to attach to its result. Used on
/// the game's main thread.
/// </summary>
public abstract class GameTestContext
{
    internal GameTestContext()
    {
    }

    /// <summary>The fixture (class) the running test belongs to.</summary>
    public abstract string Fixture { get; }

    /// <summary>The running test's name (during fixture setup and teardown: the method's).</summary>
    public abstract string Name { get; }

    /// <summary>Waits to yield: <c>yield return ctx.Wait.Frames(2);</c>.</summary>
    public abstract IGameTestWait Wait { get; }

    /// <summary>Assertions: a failed one ends the test as <c>failed</c>.</summary>
    public GameTestAssert Assert { get; } = new();

    /// <summary>Writes to the agent's log; the lines are attached to the test's result.</summary>
    public abstract IAgentLog Log { get; }

    /// <summary>Counts or records calls of game methods until the test ends.</summary>
    public abstract IGameTestHooks Hooks { get; }

    /// <summary>The game's uGUI: find, click, type.</summary>
    public abstract IGameTestUi Ui { get; }

    /// <summary>The agent's variables (shared with the <c>vars.*</c> methods).</summary>
    public abstract IAgentVars Vars { get; }

    /// <summary>Cancelled when the run is cancelled; iterator tests are stopped at their next yield anyway.</summary>
    public abstract CancellationToken Cancellation { get; }

    /// <summary>Samples a value every frame until the test ends (read on the main thread).</summary>
    public abstract IGameTestWatch Watch(Func<object?> read);

    /// <summary>Attaches a value to the test's result, encoded like any value the agent returns.</summary>
    public abstract void Attach(string name, object? value);
}

/// <summary>Waits for tests to yield. A wait that times out ends the test as <c>timeout</c>.</summary>
public interface IGameTestWait : IAgentWait
{
    /// <summary>Resumes once a scene with this name is loaded.</summary>
    object Scene(string name, int timeoutMs);
}

/// <summary>Scoped instrumentation for a test: removed when the test ends.</summary>
public interface IGameTestHooks
{
    /// <summary>Counts the calls of a method.</summary>
    /// <exception cref="InvalidOperationException">The method can't be instrumented (the message says why).</exception>
    IAgentHitCounter Count(MethodBase method);

    /// <summary>Records the calls of a method: instance, arguments, result or exception.</summary>
    /// <exception cref="InvalidOperationException">The method can't be instrumented (the message says why).</exception>
    IGameTestCapture Capture(MethodBase method);
}

/// <summary>The recorded calls of one method (the first 1,000). Disposing it stops recording.</summary>
public interface IGameTestCapture : IDisposable
{
    /// <summary>The method recorded.</summary>
    MethodBase Method { get; }

    /// <summary>The calls that finished so far, oldest first.</summary>
    IReadOnlyList<GameTestCall> Calls { get; }

    /// <summary>A wait (to yield) that resumes once <paramref name="count"/> calls have finished.</summary>
    object WaitForCalls(int count, int timeoutMs);
}

/// <summary>One recorded call.</summary>
public sealed class GameTestCall
{
    internal GameTestCall(object? instance, object?[] args, object? result, Exception? exception, long frame)
    {
        Instance = instance;
        Args = args;
        Result = result;
        Exception = exception;
        Frame = frame;
    }

    /// <summary>The instance (null for static methods).</summary>
    public object? Instance { get; }

    /// <summary>The arguments as passed.</summary>
    public IReadOnlyList<object?> Args { get; }

    /// <summary>The returned value (null for void methods, or when it threw).</summary>
    public object? Result { get; }

    /// <summary>What it threw, if it did.</summary>
    public Exception? Exception { get; }

    /// <summary>The frame of the call.</summary>
    public long Frame { get; }
}

/// <summary>A value sampled every frame. Disposing it stops sampling.</summary>
public interface IGameTestWatch : IDisposable
{
    /// <summary>The latest value.</summary>
    object? Current { get; }

    /// <summary>Each change (the first value included), oldest first; the first 10,000.</summary>
    IReadOnlyList<GameTestSample> Changes { get; }

    /// <summary>What reading the value threw, if it did (sampling stops then).</summary>
    Exception? Error { get; }
}

/// <summary>A sampled value and the frame it was read in.</summary>
public sealed class GameTestSample
{
    internal GameTestSample(long frame, object? value)
    {
        Frame = frame;
        Value = value;
    }

    /// <summary>The frame.</summary>
    public long Frame { get; }

    /// <summary>The value.</summary>
    public object? Value { get; }
}

/// <summary>The game's uGUI (and TextMeshPro), through the game's own handlers.</summary>
public interface IGameTestUi
{
    /// <summary>Visible elements whose text matches the regular expression, whose path is or ends with
    /// <paramref name="path"/>, and whose kind (<c>button</c>, <c>toggle</c>, <c>text</c>, …) is <paramref name="kind"/>
    /// (null matches anything).</summary>
    IReadOnlyList<GameTestUiElement> Find(string? text = null, string? path = null, string? kind = null);

    /// <summary>Clicks an element (a <see cref="GameTestUiElement"/>, GameObject or component); false if nothing handled it.</summary>
    bool Click(object target);

    /// <summary>Sets an input field's text (and submits it); false if nothing handled it.</summary>
    bool SetText(object target, string text, bool submit = false);
}

/// <summary>A UI element as <see cref="IGameTestUi.Find"/> sees it.</summary>
public sealed class GameTestUiElement
{
    internal GameTestUiElement(object gameObject, string path, string kind, string? text, bool interactable)
    {
        GameObject = gameObject;
        Path = path;
        Kind = kind;
        Text = text;
        Interactable = interactable;
    }

    /// <summary>The element's GameObject.</summary>
    public object GameObject { get; }

    /// <summary>Its path in the scene hierarchy.</summary>
    public string Path { get; }

    /// <summary>Its kind: <c>button</c>, <c>toggle</c>, <c>slider</c>, <c>inputField</c>, <c>dropdown</c>, <c>text</c>, …</summary>
    public string Kind { get; }

    /// <summary>Its text, without rich-text markup.</summary>
    public string? Text { get; }

    /// <summary>Whether it can be used now.</summary>
    public bool Interactable { get; }
}

/// <summary>A failed assertion (the test ends as <c>failed</c>).</summary>
public sealed class GameTestAssertionException : Exception
{
    /// <summary>Creates the exception.</summary>
    public GameTestAssertionException(string message) : base(message)
    {
    }
}

/// <summary>Assertions for in-game tests. Each throws <see cref="GameTestAssertionException"/> when it fails.</summary>
public sealed class GameTestAssert
{
    internal GameTestAssert()
    {
    }

    /// <summary>The condition is true.</summary>
    public void True(bool condition, string? message = null)
    {
        if (!condition)
        {
            Throw(message, "Expected true, got false.");
        }
    }

    /// <summary>The condition is false.</summary>
    public void False(bool condition, string? message = null)
    {
        if (condition)
        {
            Throw(message, "Expected false, got true.");
        }
    }

    /// <summary>The values are equal (<see cref="EqualityComparer{T}.Default"/>).</summary>
    public void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            Throw(message, $"Expected {Show(expected)}, got {Show(actual)}.");
        }
    }

    /// <summary>The values differ.</summary>
    public void NotEqual<T>(T notExpected, T actual, string? message = null)
    {
        if (EqualityComparer<T>.Default.Equals(notExpected, actual))
        {
            Throw(message, $"Expected anything but {Show(notExpected)}.");
        }
    }

    /// <summary>The value is null (a destroyed Unity object counts as null).</summary>
    public void Null(object? value, string? message = null)
    {
        if (!IsNull(value))
        {
            Throw(message, $"Expected null, got {Show(value)}.");
        }
    }

    /// <summary>The value isn't null (a destroyed Unity object counts as null).</summary>
    public void NotNull(object? value, string? message = null)
    {
        if (IsNull(value))
        {
            Throw(message, "Expected a value, got null.");
        }
    }

    /// <summary>The collection contains the item.</summary>
    public void Contains<T>(T item, IEnumerable<T> collection, string? message = null)
    {
        foreach (var element in collection)
        {
            if (EqualityComparer<T>.Default.Equals(item, element))
            {
                return;
            }
        }

        Throw(message, $"Expected the collection to contain {Show(item)}.");
    }

    /// <summary>The text contains the substring (ordinal).</summary>
    public void Contains(string substring, string? actual, string? message = null)
    {
        if (actual is null || actual.IndexOf(substring, StringComparison.Ordinal) < 0)
        {
            Throw(message, $"Expected {Show(actual)} to contain {Show(substring)}.");
        }
    }

    /// <summary>The action throws <typeparamref name="TException"/> (or a subclass); returns it.</summary>
    public TException Throws<TException>(Action action, string? message = null)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException e)
        {
            return e;
        }
        catch (Exception e)
        {
            Throw(message, $"Expected {typeof(TException).Name}, got {e.GetType().Name}: {e.Message}");
        }

        Throw(message, $"Expected {typeof(TException).Name}, nothing was thrown.");
        return null!;
    }

    /// <summary>The numbers differ by at most <paramref name="tolerance"/>.</summary>
    public void Approximately(double expected, double actual, double tolerance, string? message = null)
    {
        if (double.IsNaN(actual) || Math.Abs(expected - actual) > tolerance)
        {
            Throw(message, string.Format(CultureInfo.InvariantCulture, "Expected {0} ± {1}, got {2}.", expected, tolerance, actual));
        }
    }

    /// <summary>Fails the test.</summary>
    public void Fail(string message) => throw new GameTestAssertionException(message);

    private static void Throw(string? message, string detail) =>
        throw new GameTestAssertionException(message is null ? detail : message + ": " + detail);

    private static bool IsNull(object? value) => value is null || (value.Equals(null) && !(value is ValueType));

    private static string Show(object? value) => value switch
    {
        null => "null",
        string s => "\"" + s + "\"",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        ICollection c => $"{value.GetType().Name}[{c.Count}]",
        _ => value.ToString() ?? value.GetType().Name,
    };
}
