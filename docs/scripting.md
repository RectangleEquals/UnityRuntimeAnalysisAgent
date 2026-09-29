# Scripting, live patches and mod hot-reload

The agent can run C# inside the game: snippets (short pieces of code, like a REPL), live Harmony patches (to try a
behaviour change before writing a mod), and new builds of a BepInEx mod without restarting the game. All three need
the `Full` permission mode, and every assembly the agent loads is recorded in the audit log with its name and SHA-256.

**The agent never compiles.** Clients (normally the orchestrator) compile the code and send the assembly bytes. Build
against the game's own assemblies (its `*_Data/Managed` folder), BepInEx's (`BepInEx/core`), and the agent's
`UnityRuntimeAnalysisAgent.Api.dll` (next to the plugin). A build against a .NET SDK's own reference assemblies won't
load in the game's runtime.

**Every build needs a unique assembly name** (for example `my_snippet_17`). The game's runtime can't unload assemblies,
and loading a second assembly with a name that is already loaded is refused with `DUPLICATE_ASSEMBLY`. Loaded
assemblies stay in memory until the game restarts; `agent.info` counts them (`assembliesLoadedByAgent`), and the agent
warns once the count gets high.

## Snippets (`exec.run`)

A snippet is a static method that takes the agent's context. The default entry point is `Snippet.Run` (`entryType`
and `entryMethod` choose another).

```csharp
using UnityRuntimeAnalysisAgent.Api;

public static class Snippet
{
    // Runs within one frame, on the game's main thread. Its return value is the result.
    public static object Run(IAgentContext ctx)
    {
        ctx.Log.Info("hello from the game");
        return GameManager.Instance.score;
    }
}
```

```csharp
using System.Collections;
using UnityRuntimeAnalysisAgent.Api;

public static class Snippet
{
    // Runs over several frames: yield the context's waits (null waits one frame), and set the result with Return.
    public static IEnumerator Run(IAgentContext ctx)
    {
        yield return ctx.Wait.Frames(10);
        yield return ctx.Wait.Until(() => Player.Instance != null, 5000);
        ctx.Return(Player.Instance.name);
    }
}
```

- **Prefer the iterator form for anything that may take long.** It stops at its next yield when the request is
  cancelled or its `timeoutMs` passes. A synchronous snippet can't be interrupted: a runaway loop in one freezes the
  game.
- `ctx.Args` holds the request's `args`. `ctx.Vars` are the agent's variables (the same ones `vars.*` reads and writes).
  `ctx.Session` keeps state between the runs of a named `session` (`exec.sessions` lists them, `exec.sessionClose`
  drops one).
- `ctx.Handle(obj)` gives an object a handle that clients can use as a target; `ctx.Resolve` turns a handle or an
  anchor back into an object or member.
- `ctx.Emit(kind, payload)` sends an `exec.emit` event and adds it to the result. `ctx.Hooks.Count(method)` counts a
  method's calls while the snippet runs (`WaitForHits` waits for them).
- Failures come back as `EXEC_FAILED` with `data.phase`: `load` (not an assembly), `bind` (no such entry point) or
  `run` (the snippet threw; the exception and its stack are in `data`).

## Live patches (`patch.*`)

A patch assembly contains ordinary HarmonyX patch classes. `patch.apply` applies every `[HarmonyPatch]` class (or the
`types` named) under its own Harmony id, `ulm.livepatch.<patchSetId>`, so `patch.revert` removes exactly that set.
A class that fails (for example a transpiler that throws) is listed in `errors` with its target and leaves nothing
behind; the rest of the set still applies. Live patches are never saved: they're removed when the agent stops.

Patch code that wants the agent reaches it through `AgentApi.Current` (null when the agent isn't running):

```csharp
[HarmonyPatch(typeof(Shop), nameof(Shop.Price))]
public static class CheaperShop
{
    public static void Postfix(ref int __result)
    {
        __result /= 2;
        AgentApi.Current?.Emit("shop.price", __result);
    }
}
```

`patch.inspect` lists every Harmony patch on a method, whoever owns it (the game's other mods included), and
`patches.all` lists every patched method in the game.

## Mod hot-reload (`mod.*`)

`mod.reload` replaces a running BepInEx plugin with a new build, identified by its plugin GUID. For that to work, the
mod must follow a few rules:

- a stable `[BepInPlugin]` GUID;
- its Harmony instance uses the GUID as its id (`new Harmony(GUID)`);
- it unpatches itself and releases what it holds in `OnDestroy` (`harmony.UnpatchSelf()`);
- it sets up its state, static state included, in `Awake`;
- every build has a unique assembly name.

The agent unpatches the GUID's Harmony id, destroys the running instance (the one BepInEx loaded, or the previous
hot-loaded one), waits one frame so its `OnDestroy` has run, loads the new assemblies (dependencies first) and creates
the plugin on a hidden object, which runs its `Awake`. `mod.unload` destroys a plugin and unpatches its id;
`mod.list` shows the plugins BepInEx loaded and the hot-loaded ones.
