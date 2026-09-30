using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Api;
using UnityRuntimeAnalysisAgent.Core.Abstractions;
using UnityRuntimeAnalysisAgent.Core.Data;
using UnityRuntimeAnalysisAgent.Core.Diagnostics;
using UnityRuntimeAnalysisAgent.Core.Execution;
using UnityRuntimeAnalysisAgent.Core.Hosting;
using UnityRuntimeAnalysisAgent.Core.Instrumentation;
using UnityRuntimeAnalysisAgent.Core.Runtime;

namespace UnityRuntimeAnalysisAgent.Core.Testing;

/// <summary>A test fixture found in an assembly.</summary>
internal sealed class GameTestFixture
{
    public GameTestFixture(Type type, List<GameTestCase> tests, List<MethodInfo> setUps, List<MethodInfo> tearDowns, List<MethodInfo> fixtureSetUps, List<MethodInfo> fixtureTearDowns)
    {
        Type = type;
        Tests = tests;
        SetUps = setUps;
        TearDowns = tearDowns;
        FixtureSetUps = fixtureSetUps;
        FixtureTearDowns = fixtureTearDowns;
    }

    public Type Type { get; }

    public string Name => Type.Name;

    public List<GameTestCase> Tests { get; }

    public List<MethodInfo> SetUps { get; }

    public List<MethodInfo> TearDowns { get; }

    public List<MethodInfo> FixtureSetUps { get; }

    public List<MethodInfo> FixtureTearDowns { get; }
}

/// <summary>One test: its method and attribute.</summary>
internal sealed class GameTestCase
{
    public GameTestCase(GameTestFixture? fixture, MethodInfo method, GameTestAttribute attribute)
    {
        Fixture = fixture;
        Method = method;
        Attribute = attribute;
    }

    public GameTestFixture? Fixture { get; set; }

    public MethodInfo Method { get; }

    public GameTestAttribute Attribute { get; }

    public string Name => Method.Name;

    public TestId Id => new() { Fixture = Fixture?.Name ?? Method.DeclaringType?.Name ?? string.Empty, Name = Name, Category = Attribute.Category };
}

/// <summary>Finds fixtures and tests by their attributes: types and methods in declaration order, tests then by
/// <see cref="GameTestAttribute.Order"/>.</summary>
internal static class GameTestDiscovery
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    public static List<GameTestFixture> Discover(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            types = e.Types.Where(t => t is not null).ToArray()!;
        }

        var fixtures = new List<GameTestFixture>();
        foreach (var type in types.Where(t => t.IsDefined(typeof(GameTestFixtureAttribute), false)).OrderBy(t => t.MetadataToken))
        {
            var methods = type.GetMethods(Declared).OrderBy(m => m.MetadataToken).ToList();
            List<MethodInfo> With<T>() where T : Attribute => methods.Where(m => m.IsDefined(typeof(T), false)).ToList();
            var tests = methods.Select(m => (Method: m, Attribute: m.GetCustomAttribute<GameTestAttribute>(false)))
                .Where(t => t.Attribute is not null)
                .Select((t, i) => (Case: new GameTestCase(null, t.Method, t.Attribute!), Index: i))
                .OrderBy(t => t.Case.Attribute.Order).ThenBy(t => t.Index)
                .Select(t => t.Case).ToList();
            var fixture = new GameTestFixture(type, tests, With<GameTestSetUpAttribute>(), With<GameTestTearDownAttribute>(),
                With<GameTestFixtureSetUpAttribute>(), With<GameTestFixtureTearDownAttribute>());
            foreach (var test in tests)
            {
                test.Fixture = fixture;
            }

            fixtures.Add(fixture);
        }

        return fixtures;
    }

    /// <summary>Why a test (or setup/teardown) method can't be run, or null.</summary>
    public static string? SignatureProblem(MethodInfo method)
    {
        var parameters = method.GetParameters();
        if (parameters.Length != 1 || parameters[0].ParameterType != typeof(GameTestContext))
        {
            return $"{method.DeclaringType?.Name}.{method.Name} must take one GameTestContext.";
        }

        if (method.ReturnType != typeof(void) && method.ReturnType != typeof(IEnumerator))
        {
            return $"{method.DeclaringType?.Name}.{method.Name} must return void or IEnumerator.";
        }

        return method.ContainsGenericParameters ? $"{method.DeclaringType?.Name}.{method.Name} can't be generic." : null;
    }
}

/// <summary>How a phase (setup, body, teardown) ended: null status means it passed.</summary>
internal sealed class PhaseOutcome
{
    public string? Status { get; private set; }

    public string? Message { get; private set; }

    public string? Stack { get; private set; }

    public bool Passed => Status is null;

    public void Set(string status, string message, string? stack = null)
    {
        if (Status is null)
        {
            (Status, Message, Stack) = (status, message, stack);
        }
    }

    public void From(Exception error)
    {
        var e = error is TargetInvocationException { InnerException: { } inner } ? inner : error;
        var stack = e.StackTrace is { } s && s.Length > LogBuffer.MaxStackBytes ? s.Substring(0, LogBuffer.MaxStackBytes) : e.StackTrace;
        switch (e)
        {
            case GameTestAssertionException:
                Set("failed", e.Message, stack);
                break;
            case ProtocolException { Code: ErrorCodes.Timeout }:
                Set("timeout", e.Message, stack);
                break;
            default:
                Set("error", $"{e.GetType().Name}: {e.Message}", stack);
                break;
        }
    }
}

/// <summary>
/// Runs a plan of tests as one main-thread routine: fixture setup, then per test setup → body → teardown (teardown always
/// runs), then fixture teardown. Test iterators are stepped here, not by the pump, so a test's timeout is checked at each
/// of its yields and a failing or timed-out test still gets its teardown.
/// </summary>
internal sealed class GameTestRunner
{
    public const int MaxLogsPerTest = 1000;

    private readonly ContextServices _services;
    private readonly LogBuffer _logs;
    private readonly IUnityApi _unity;
    private readonly Action<string, ProtocolMessage> _publish;

    public GameTestRunner(ContextServices services, LogBuffer logs, IUnityApi unity, Action<string, ProtocolMessage> publish)
    {
        _services = services;
        _logs = logs;
        _unity = unity;
        _publish = publish;
    }

    /// <summary>The results so far, in run order.</summary>
    public List<TestResult> Results { get; } = new();

    public long FirstFrame { get; private set; }

    public long LastFrame { get; private set; }

    /// <summary>The run (main thread). <paramref name="selected"/> says which tests run (the others are reported
    /// skipped); <paramref name="progress"/> gets (done, total, test).</summary>
    public IEnumerable<object?> Run(string jobId, List<GameTestFixture> fixtures, Func<GameTestCase, bool> selected, bool stopOnFail, int defaultTimeoutMs,
        double? timeScale, CancellationToken cancellation, Action<long, long, string> progress)
    {
        FirstFrame = _services.Pump.Clock.FrameCount;
        var total = fixtures.Sum(f => f.Tests.Count);
        var control = _unity.Control;
        double? previousScale = null;
        if (timeScale is { } scale && control is not null)
        {
            previousScale = control.TimeScale;
            control.TimeScale = scale;
        }

        var stopped = false;
        try
        {
            foreach (var fixture in fixtures)
            {
                var chosen = fixture.Tests.Where(t => !stopped && selected(t)).ToList();
                if (chosen.Count == 0)
                {
                    foreach (var test in fixture.Tests)
                    {
                        Report(jobId, test, "skipped", stopped ? "Not run: an earlier test failed (stopOnFail)." : "Not selected by the run's filter.", 0, null, null);
                    }

                    continue;
                }

                // One instance for the fixture's tests (static fixtures have none).
                object? instance = null;
                string? fixtureProblem = null;
                var created = true;
                if (!(fixture.Type.IsAbstract && fixture.Type.IsSealed))
                {
                    try
                    {
                        instance = Activator.CreateInstance(fixture.Type, nonPublic: true);
                    }
                    catch (Exception e)
                    {
                        var inner = e is TargetInvocationException { InnerException: { } i } ? i : e;
                        fixtureProblem = $"The fixture couldn't be created: {inner.GetType().Name}: {inner.Message}";
                        created = false;
                    }
                }

                var fixtureContext = new GameTestContextImpl(_services, _unity, fixture.Name, "(fixture setup)", cancellation);
                if (fixtureProblem is null)
                {
                    foreach (var method in fixture.FixtureSetUps)
                    {
                        var outcome = new PhaseOutcome();
                        foreach (var step in Phase(method, instance, fixtureContext, Budget(defaultTimeoutMs), cancellation, outcome))
                        {
                            yield return step;
                        }

                        if (!outcome.Passed)
                        {
                            fixtureProblem = $"Fixture setup {method.Name} {outcome.Status}: {outcome.Message}";
                            break;
                        }
                    }
                }

                foreach (var test in fixture.Tests)
                {
                    progress(Results.Count, total, $"{fixture.Name}.{test.Name}");
                    if (!chosen.Contains(test) || stopped)
                    {
                        Report(jobId, test, "skipped", stopped ? "Not run: an earlier test failed (stopOnFail)." : "Not selected by the run's filter.", 0, null, null);
                        continue;
                    }

                    if (cancellation.IsCancellationRequested)
                    {
                        break;
                    }

                    foreach (var step in RunTest(jobId, fixture, test, instance, fixtureProblem, defaultTimeoutMs, cancellation))
                    {
                        yield return step;
                    }

                    if (stopOnFail && Results[Results.Count - 1].Status is "failed" or "error" or "timeout")
                    {
                        stopped = true;
                    }
                }

                if (created)
                {
                    foreach (var method in fixture.FixtureTearDowns)
                    {
                        var outcome = new PhaseOutcome();
                        foreach (var step in Phase(method, instance, fixtureContext, Budget(defaultTimeoutMs), CancellationToken.None, outcome))
                        {
                            yield return step;
                        }

                        if (!outcome.Passed)
                        {
                            _services.Log.Error($"[test] Fixture teardown {fixture.Name}.{method.Name} {outcome.Status}: {outcome.Message}");
                        }
                    }
                }

                fixtureContext.End();
                if (cancellation.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            if (previousScale is { } restore && control is not null)
            {
                control.TimeScale = restore;
            }

            LastFrame = _services.Pump.Clock.FrameCount;
        }
    }

    /// <summary>Totals of <see cref="Results"/>.</summary>
    public TestTotals Totals() => new()
    {
        Passed = Results.Count(r => r.Status == "passed"),
        Failed = Results.Count(r => r.Status == "failed"),
        Error = Results.Count(r => r.Status == "error"),
        Timeout = Results.Count(r => r.Status == "timeout"),
        Skipped = Results.Count(r => r.Status == "skipped"),
    };

    private IEnumerable<object?> RunTest(string jobId, GameTestFixture fixture, GameTestCase test, object? instance, string? fixtureProblem, int defaultTimeoutMs,
        CancellationToken cancellation)
    {
        var clock = Stopwatch.StartNew();
        if (test.Attribute.Skip is { } skip)
        {
            Report(jobId, test, "skipped", skip, 0, null, null);
            yield break;
        }

        _publish(EventKinds.TestStarted, new TestStartedEventParams { JobId = jobId, Test = test.Id });
        var fromSeq = _logs.NextSeq - 1;
        var timeoutMs = test.Attribute.TimeoutMs > 0 ? test.Attribute.TimeoutMs : defaultTimeoutMs;
        var context = new GameTestContextImpl(_services, _unity, fixture.Name, test.Name, cancellation);
        var outcome = new PhaseOutcome();
        if (fixtureProblem is not null)
        {
            outcome.Set("error", fixtureProblem);
        }
        else if (GameTestDiscovery.SignatureProblem(test.Method) is { } signature)
        {
            outcome.Set("error", signature);
        }
        else if (test.Attribute.RequiresScene is { } scene && !SceneLoaded(scene))
        {
            var waited = new PhaseOutcome();
            foreach (var step in Drive(Enumerate(PumpWait.Until(() => SceneLoaded(scene), timeoutMs, $"Scene '{scene}' wasn't loaded")), Budget(timeoutMs), cancellation, waited))
            {
                yield return step;
            }

            if (!waited.Passed)
            {
                context.End();
                Report(jobId, test, "skipped", $"Scene '{scene}' wasn't loaded within {timeoutMs} ms.", clock.ElapsedMilliseconds, Logs(fromSeq), null);
                yield break;
            }
        }

        if (outcome.Passed)
        {
            var deadline = Budget(timeoutMs);
            foreach (var method in fixture.SetUps)
            {
                foreach (var step in Phase(method, instance, context, deadline, cancellation, outcome))
                {
                    yield return step;
                }

                if (!outcome.Passed)
                {
                    break;
                }
            }

            if (outcome.Passed)
            {
                foreach (var step in Phase(test.Method, instance, context, deadline, cancellation, outcome))
                {
                    yield return step;
                }
            }

            // Teardown always runs, with a budget of its own.
            var teardownDeadline = Budget(timeoutMs);
            foreach (var method in fixture.TearDowns)
            {
                var teardown = new PhaseOutcome();
                foreach (var step in Phase(method, instance, context, teardownDeadline, CancellationToken.None, teardown))
                {
                    yield return step;
                }

                if (!teardown.Passed)
                {
                    if (outcome.Passed)
                    {
                        outcome.Set("error", $"Teardown {method.Name} {teardown.Status}: {teardown.Message}", teardown.Stack);
                    }
                    else
                    {
                        _services.Log.Error($"[test] Teardown {fixture.Name}.{method.Name} {teardown.Status}: {teardown.Message}");
                    }
                }
            }
        }

        context.End();
        Report(jobId, test, outcome.Status ?? "passed", outcome.Message, clock.ElapsedMilliseconds, Logs(fromSeq), context.Attachments, outcome.Stack);
    }

    // Invokes a test, setup or teardown method and drives it if it's an iterator.
    private IEnumerable<object?> Phase(MethodInfo method, object? instance, GameTestContextImpl context, Deadline deadline, CancellationToken cancellation, PhaseOutcome outcome)
    {
        if (GameTestDiscovery.SignatureProblem(method) is { } problem)
        {
            outcome.Set("error", problem);
            yield break;
        }

        if (!method.IsStatic && instance is null)
        {
            outcome.Set("error", $"{method.Name} is an instance method of a fixture that has no instance.");
            yield break;
        }

        object? returned;
        try
        {
            returned = method.Invoke(method.IsStatic ? null : instance, new object[] { context });
        }
        catch (Exception e)
        {
            outcome.From(e);
            yield break;
        }

        if (returned is IEnumerator iterator)
        {
            foreach (var step in Drive(iterator, deadline, cancellation, outcome))
            {
                yield return step;
            }
        }
        else if (deadline.Passed)
        {
            outcome.Set("timeout", $"{method.Name} took {deadline.ElapsedMs} ms (the timeout is {deadline.BudgetMs} ms).");
        }
    }

    // Steps a test iterator: its waits become frame-by-frame waits here, so the timeout and cancellation are checked at
    // every frame; its exceptions and wait timeouts become the outcome.
    private IEnumerable<object?> Drive(IEnumerator iterator, Deadline deadline, CancellationToken cancellation, PhaseOutcome outcome)
    {
        bool Expired()
        {
            if (cancellation.IsCancellationRequested)
            {
                outcome.Set("error", "The run was cancelled.");
                return true;
            }

            if (deadline.Passed)
            {
                outcome.Set("timeout", $"Timed out after {deadline.BudgetMs} ms.");
                return true;
            }

            return false;
        }

        try
        {
            while (true)
            {
                if (Expired())
                {
                    yield break;
                }

                object? current;
                try
                {
                    if (!iterator.MoveNext())
                    {
                        yield break;
                    }

                    current = iterator.Current;
                }
                catch (Exception e)
                {
                    outcome.From(e);
                    yield break;
                }

                switch (current)
                {
                    case null:
                        yield return PumpWait.NextFrame;
                        break;
                    case PumpWait.EndOfFrameWait:
                        yield return PumpWait.EndOfFrame;
                        break;
                    case PumpWait.FramesWait frames:
                        for (var i = 0; i < frames.Count; i++)
                        {
                            if (Expired())
                            {
                                yield break;
                            }

                            yield return PumpWait.NextFrame;
                        }

                        break;
                    case PumpWait.RealtimeWait realtime:
                        var waited = Stopwatch.StartNew();
                        while (waited.ElapsedMilliseconds < realtime.Milliseconds)
                        {
                            if (Expired())
                            {
                                yield break;
                            }

                            yield return PumpWait.NextFrame;
                        }

                        break;
                    case PumpWait.UntilWait until:
                        var since = Stopwatch.StartNew();
                        while (true)
                        {
                            bool met;
                            try
                            {
                                met = until.Predicate();
                            }
                            catch (Exception e)
                            {
                                outcome.From(e);
                                yield break;
                            }

                            if (met)
                            {
                                break;
                            }

                            if (since.ElapsedMilliseconds > until.TimeoutMs)
                            {
                                outcome.Set("timeout", until.TimeoutMessage);
                                yield break;
                            }

                            if (Expired())
                            {
                                yield break;
                            }

                            yield return PumpWait.NextFrame;
                        }

                        break;
                    default:
                        outcome.Set("error", $"The test yielded a {current.GetType().Name}; yield ctx.Wait.Frames/Seconds/Until/Scene/EndOfFrame, or null for one frame.");
                        yield break;
                }
            }
        }
        finally
        {
            (iterator as IDisposable)?.Dispose();
        }
    }

    private void Report(string jobId, GameTestCase test, string status, string? message, long durationMs, List<LogEntry>? logs, JsonObject? attachments, string? stack = null)
    {
        var result = new TestResult
        {
            Test = test.Id,
            Status = status,
            DurationMs = durationMs,
            Message = message,
            Stack = stack,
            Attachments = attachments is { Count: > 0 } ? attachments : null,
            Logs = logs,
        };
        Results.Add(result);
        _publish(EventKinds.TestResult, new TestResultEventParams { JobId = jobId, Result = result });
    }

    private List<LogEntry> Logs(long fromSeq) => _logs.Tail(fromSeq, MaxLogsPerTest, 0, null, null, null).Items;

    private bool SceneLoaded(string name) => _unity.Scenes().Any(s => s.IsLoaded && (s.Name == name || s.Path == name));

    private static IEnumerator Enumerate(object wait)
    {
        yield return wait;
    }

    private static Deadline Budget(int ms) => new(ms);

    /// <summary>A time budget in real time, started now.</summary>
    private sealed class Deadline
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public Deadline(long budgetMs) => BudgetMs = budgetMs;

        public long BudgetMs { get; }

        public long ElapsedMs => _clock.ElapsedMilliseconds;

        public bool Passed => ElapsedMs > BudgetMs;
    }
}

/// <summary>The context a test gets; its instrumentation and watches end with it.</summary>
internal sealed class GameTestContextImpl : GameTestContext, IGameTestWait, IAgentLog, IGameTestHooks, IGameTestUi
{
    public const int MaxCalls = 1000;
    public const int MaxSamples = 10_000;

    private readonly ContextServices _services;
    private readonly IUnityApi _unity;
    private readonly List<IDisposable> _scoped = new();

    public GameTestContextImpl(ContextServices services, IUnityApi unity, string fixture, string name, CancellationToken cancellation)
    {
        _services = services;
        _unity = unity;
        Fixture = fixture;
        Name = name;
        Cancellation = cancellation;
        Vars = new AgentVars(services.Data, () => services.Pump.Clock.FrameCount);
    }

    public override string Fixture { get; }

    public override string Name { get; }

    public override IGameTestWait Wait => this;

    public override IAgentLog Log => this;

    public override IGameTestHooks Hooks => this;

    public override IGameTestUi Ui => this;

    public override IAgentVars Vars { get; }

    public override CancellationToken Cancellation { get; }

    /// <summary>The attached values.</summary>
    public JsonObject Attachments { get; } = new();

    object IAgentWait.EndOfFrame => PumpWait.EndOfFrame;

    public override void Attach(string name, object? value)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("An attachment needs a name.", nameof(name));
        }

        Attachments.Set(name, _services.Data.Writer(ViewOptions.From(null), _services.Pump.Clock.FrameCount).Write(value, new Place()));
    }

    public override IGameTestWatch Watch(Func<object?> read)
    {
        var watch = new ValueWatch(read ?? throw new ArgumentNullException(nameof(read)), _services.Pump);
        Scope(watch);
        return watch;
    }

    /// <summary>Removes the test's instrumentation and watches.</summary>
    public void End()
    {
        List<IDisposable> scoped;
        lock (_scoped)
        {
            scoped = new List<IDisposable>(_scoped);
            _scoped.Clear();
        }

        foreach (var item in scoped)
        {
            item.Dispose();
        }
    }

    // ---- waits --------------------------------------------------------------------------------------------------------------

    object IAgentWait.Frames(int count) => PumpWait.Frames(count);

    object IAgentWait.Seconds(double seconds) => PumpWait.Realtime(seconds * 1000.0);

    object IAgentWait.Until(Func<bool> condition, int timeoutMs) => PumpWait.Until(condition, Math.Max(1, timeoutMs));

    object IGameTestWait.Scene(string name, int timeoutMs) =>
        PumpWait.Until(() => _unity.Scenes().Any(s => s.IsLoaded && (s.Name == name || s.Path == name)), Math.Max(1, timeoutMs), $"Scene '{name}' wasn't loaded");

    // ---- log ----------------------------------------------------------------------------------------------------------------

    void IAgentLog.Info(string message) => _services.Log.Info(Prefix + message);

    void IAgentLog.Warning(string message) => _services.Log.Warning(Prefix + message);

    void IAgentLog.Error(string message) => _services.Log.Error(Prefix + message);

    private string Prefix => $"[test {Fixture}.{Name}] ";

    // ---- hooks --------------------------------------------------------------------------------------------------------------

    IAgentHitCounter IGameTestHooks.Count(MethodBase method)
    {
        var counter = new Counter(method ?? throw new ArgumentNullException(nameof(method)), _services.Instrumenter);
        Attach(method, counter);
        Scope(counter);
        return counter;
    }

    IGameTestCapture IGameTestHooks.Capture(MethodBase method)
    {
        var capture = new CallCapture(method ?? throw new ArgumentNullException(nameof(method)), _services.Instrumenter);
        Attach(method, capture);
        Scope(capture);
        return capture;
    }

    // ---- UI -------------------------------------------------------------------------------------------------------------------

    IReadOnlyList<GameTestUiElement> IGameTestUi.Find(string? text, string? path, string? kind)
    {
        var regex = text is null ? null : new Regex(text, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return UiApi.Snapshot(onlyInteractable: false, onlyVisible: true, includeText: true, 5000)
            .Select(e => (Element: e, Path: _unity.Locate(e.GameObject)?.Path ?? string.Empty))
            .Where(e => (regex is null || (e.Element.Text is not null && regex.IsMatch(e.Element.Text)))
                && (path is null || e.Path == path || e.Path.EndsWith("/" + path, StringComparison.Ordinal))
                && (kind is null || e.Element.Kind == kind))
            .Select(e => new GameTestUiElement(e.Element.GameObject, e.Path, e.Element.Kind, e.Element.Text, e.Element.Interactable))
            .ToList();
    }

    bool IGameTestUi.Click(object target) => UiApi.Click(UiTarget(target)).Handled;

    bool IGameTestUi.SetText(object target, string text, bool submit) => UiApi.SetText(UiTarget(target), text, submit).Handled;

    private IUiApi UiApi => _unity.Ui is { UguiStatus.Available: true } ui ? ui : throw new InvalidOperationException("This game has no uGUI to drive.");

    private object UiTarget(object target)
    {
        var value = target is GameTestUiElement element ? element.GameObject : target ?? throw new ArgumentNullException(nameof(target));
        return UiApi.IsAgentOwned(value) ? throw new ArgumentException("The target is part of the agent's own UI.", nameof(target)) : value;
    }

    private void Attach(MethodBase method, IMethodSink sink)
    {
        try
        {
            _services.Instrumenter.Attach(method, sink, force: false);
        }
        catch (ProtocolException e)
        {
            throw new InvalidOperationException(e.Message);
        }
    }

    private void Scope(IDisposable item)
    {
        lock (_scoped)
        {
            _scoped.Add(item);
        }
    }

    private sealed class Counter : IAgentHitCounter, IMethodSink
    {
        private readonly Instrumenter _instrumenter;
        private long _hits;
        private int _disposed;

        public Counter(MethodBase method, Instrumenter instrumenter)
        {
            Method = method;
            _instrumenter = instrumenter;
        }

        public MethodBase Method { get; }

        public long Hits => Interlocked.Read(ref _hits);

        public object WaitForHits(long count, int timeoutMs) =>
            PumpWait.Until(() => Hits >= count, Math.Max(1, timeoutMs), $"{Method.Name} wasn't called {count} time(s) (called {Hits})");

        public object? Enter(CallContext call)
        {
            Interlocked.Increment(ref _hits);
            return null;
        }

        public void Exit(CallContext call, object token, object? result, bool hasResult, long elapsedTicks)
        {
        }

        public void Throw(CallContext call, object token, Exception exception, long elapsedTicks)
        {
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _instrumenter.Detach(Method, this);
            }
        }
    }

    private sealed class CallCapture : IGameTestCapture, IMethodSink
    {
        private readonly Instrumenter _instrumenter;
        private readonly List<GameTestCall> _calls = new();
        private int _disposed;

        public CallCapture(MethodBase method, Instrumenter instrumenter)
        {
            Method = method;
            _instrumenter = instrumenter;
        }

        public MethodBase Method { get; }

        public IReadOnlyList<GameTestCall> Calls
        {
            get
            {
                lock (_calls)
                {
                    return _calls.ToList();
                }
            }
        }

        public object WaitForCalls(int count, int timeoutMs) =>
            PumpWait.Until(() => Calls.Count >= count, Math.Max(1, timeoutMs), $"{Method.Name} didn't finish {count} call(s)");

        public object? Enter(CallContext call) => new Started(call.Instance, call.Args?.ToArray() ?? Array.Empty<object?>());

        public void Exit(CallContext call, object token, object? result, bool hasResult, long elapsedTicks) => Add((Started)token, result, null, call.Frame);

        public void Throw(CallContext call, object token, Exception exception, long elapsedTicks) => Add((Started)token, null, exception, call.Frame);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _instrumenter.Detach(Method, this);
            }
        }

        private void Add(Started started, object? result, Exception? exception, long frame)
        {
            lock (_calls)
            {
                if (_calls.Count < MaxCalls)
                {
                    _calls.Add(new GameTestCall(started.Instance, started.Args, result, exception, frame));
                }
            }
        }

        private sealed class Started
        {
            public Started(object? instance, object?[] args)
            {
                Instance = instance;
                Args = args;
            }

            public object? Instance { get; }

            public object?[] Args { get; }
        }
    }

    private sealed class ValueWatch : IGameTestWatch
    {
        private readonly Func<object?> _read;
        private readonly MainThreadPump _pump;
        private readonly List<GameTestSample> _changes = new();
        private bool _started;

        public ValueWatch(Func<object?> read, MainThreadPump pump)
        {
            _read = read;
            _pump = pump;
            Sample(pump.Clock);
            pump.Ticked += Sample;
        }

        public object? Current { get; private set; }

        public IReadOnlyList<GameTestSample> Changes
        {
            get
            {
                lock (_changes)
                {
                    return _changes.ToList();
                }
            }
        }

        public Exception? Error { get; private set; }

        public void Dispose() => _pump.Ticked -= Sample;

        private void Sample(FrameTime clock)
        {
            if (Error is not null)
            {
                return;
            }

            object? value;
            try
            {
                value = _read();
            }
            catch (Exception e)
            {
                Error = e;
                _pump.Ticked -= Sample;
                return;
            }

            if (_started && Equals(value, Current))
            {
                return;
            }

            _started = true;
            Current = value;
            lock (_changes)
            {
                if (_changes.Count < MaxSamples)
                {
                    _changes.Add(new GameTestSample(clock.FrameCount, value));
                }
            }
        }
    }
}
