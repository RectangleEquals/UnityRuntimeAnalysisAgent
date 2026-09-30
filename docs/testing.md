# In-game tests

The agent runs test suites **inside the live game**: a mod's tests call the game's code, click its UI, count calls of
its methods and check the results, in the game as it really runs. That's how a generated mod is shown to do what was
asked. Running tests needs the `Full` permission mode; listing them works in any mode.

## Writing tests

Tests are C# in their own assembly, built like snippets (see [scripting](scripting.md)): against the game's assemblies,
BepInEx's, and the agent's `UnityRuntimeAnalysisAgent.Api.dll`, with a unique assembly name per build.

```csharp
using System.Collections;
using UnityRuntimeAnalysisAgent.Api;

[GameTestFixture]
public sealed class CraftingTests
{
    private int _planks;

    [GameTestSetUp]
    public IEnumerator SetUp(GameTestContext ctx)
    {
        yield return ctx.Wait.Scene("Main", 60000);
        _planks = Inventory.Count("plank");
    }

    [GameTestTearDown]
    public void TearDown(GameTestContext ctx) => Inventory.Set("plank", _planks);   // undo what the test changed

    [GameTest(TimeoutMs = 30000, Category = "mod")]
    public IEnumerator DoubleYieldMod_DoublesOutput(GameTestContext ctx)
    {
        var completed = ctx.Hooks.Count(typeof(CraftingSystem).GetMethod("Complete"));
        CraftingSystem.Instance.Start("plank_recipe");                        // tests call game code directly
        yield return completed.WaitForHits(1, 20000);
        ctx.Assert.Equal(_planks + 2, Inventory.Count("plank"), "the mod doubles planks");
        ctx.Attach("planks", Inventory.Count("plank"));
    }
}
```

- A test is `void Name(GameTestContext ctx)` (one frame) or `IEnumerator Name(GameTestContext ctx)` (over several frames:
  yield the context's waits, or `null` for one frame).
- **Attributes:** `[GameTestFixture]` on the class (one instance per run, or static methods);
  `[GameTest(TimeoutMs, Category, RequiresScene, Order, Skip)]`; `[GameTestSetUp]` / `[GameTestTearDown]` around each
  test; `[GameTestFixtureSetUp]` / `[GameTestFixtureTearDown]` around the fixture. Setups and teardowns take the same
  signatures as tests.
- **Order:** fixtures and tests in declaration order; `Order` moves a test earlier (lower first).

### The context

| Member | |
|---|---|
| `Wait` | `Frames(n)`, `Seconds(s)` (real time), `Until(condition, timeoutMs)`, `Scene(name, timeoutMs)`, `EndOfFrame` |
| `Assert` | `True`, `False`, `Equal`, `NotEqual`, `Null`, `NotNull`, `Contains`, `Throws<T>`, `Approximately`, `Fail`: a failed assertion ends the test as `failed` |
| `Log` | `Info`, `Warning`, `Error`: the lines are attached to the test's result |
| `Hooks.Count(method)` | counts calls; `WaitForHits(n, timeoutMs)` to wait for them |
| `Hooks.Capture(method)` | records calls (instance, arguments, result or exception); `WaitForCalls(n, timeoutMs)` |
| `Watch(() => value)` | samples a value every frame: `Current`, `Changes` |
| `Ui` | `Find(text, path, kind)`, `Click(element)`, `SetText(element, text, submit)`: uGUI through the game's own handlers |
| `Vars` | the agent's variables (shared with `vars.*`) |
| `Attach(name, value)` | adds a value to the result, encoded like any value the agent returns |
| `Cancellation`, `Fixture`, `Name` | the run's cancellation; which test is running |

Hooks, captures and watches end with the test that created them.

## Running tests

| Method | |
|---|---|
| `test.list {assembly}` | the tests an assembly holds, with their timeout, order, skip reason and required scene |
| `test.run {assembly, filter?, stopOnFail?, defaultTimeoutMs?, timeScale?}` | runs them as a job; `job.wait` / `job.get` give the results |

`assembly` is `{base64}` (the bytes, loaded now) or `{loadedName}` (an assembly already loaded, for example by an
earlier `test.list`: loading the same name twice is refused with `DUPLICATE_ASSEMBLY`). `filter` selects by `fixture`,
`name` (a regular expression over the test's name or `Fixture.Name`) and `category`. `timeScale` runs the tests at that
time scale and restores the previous one after.

**How a run goes:** fixtures one after the other; for each: fixture setup, then per test setup → test → teardown
(teardown always runs, even after a failure or a timeout), then fixture teardown. A test's `TimeoutMs` (or the run's
`defaultTimeoutMs`, 30 s by default) covers its setup and body in real time and is checked whenever it yields; a
one-frame test that runs past it is reported afterwards. Teardown gets a budget of its own.

**Statuses:**

| Status | When |
|---|---|
| `passed` | the setup, test and teardown finished without failures |
| `failed` | an assertion failed |
| `error` | anything else was thrown (by the setup, test or teardown), the fixture couldn't be created or set up, or the method's signature is wrong |
| `timeout` | the test ran out of time, or a wait's own timeout passed |
| `skipped` | `Skip`, not selected by the filter, not run after a failure with `stopOnFail`, or its `RequiresScene` wasn't loaded within the test's timeout |

Each result has the test, its status, duration, message and stack, attachments, and the log lines written while it ran
(its own and anything else the game or its mods logged). The job's result adds the totals, the frames the run spanned,
and the agent's and Unity's versions. Events `test.started`, `test.result` and `test.finished` follow the run as it goes.

## Things to keep in mind

- **Nothing isolates tests.** They run in the live game, one after another. Restore everything a test changes in its
  teardown, or the next test (and the player) sees the change.
- **A one-frame test can't be interrupted.** A `void` test that never returns freezes the game; loops belong in iterator
  tests, which stop at their next yield.
- **Test assemblies stay loaded** until the game restarts, like every assembly the agent loads.
