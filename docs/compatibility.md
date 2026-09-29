# Compatibility

What the agent has been verified with, and known limitations. This page grows as the agent is verified with more games.

## Verified

| | Version |
|---|---|
| Loader | BepInEx 5.4.23.5 (Windows x64) |
| Unity | 6000.3 (Unity 6), Mono scripting backend, Windows x64 player |
| Games | A purpose-built Unity player, and a released Unity 6 Mono game |
| API baseline | The agent is built against the Unity 2018.1 API, so it's meant to load in games from Unity 2018.1 on (Mono). Versions other than the ones above haven't been verified yet. |

IL2CPP games aren't supported in this version.

## Known limitations

- **The connection pipe isn't restricted to your user account inside Unity games.** On Windows the agent listens on a
  named pipe. Where the runtime allows it, the pipe only accepts your own user account; the Mono runtime that Unity
  games use doesn't support that, so there the pipe uses Windows' default security. Every client must still present the
  session token, a random secret the agent writes only into its discovery file in your own profile folder, so other
  accounts on the machine can't use the agent without being able to read your files.
- **BepInEx's `LogOutput.log` can miss other plugins' last lines** when a game quits: BepInEx 5 writes that file on a
  timer. The agent flushes it as it stops, so its own lines are complete; the game's own player log (`Player.log`) has
  every line.
- **Engine internal calls are listed without their parameters.** Reading the parameters of some engine functions can
  crash the game's runtime, so for internal calls (functions implemented inside the engine) the agent never asks: their
  names and signatures end in `(?)`. Everything else about them (their exact code reference, their IL callers) is there.
- **Engine internal calls aren't invoked.** For the same reason, `obj.invoke` refuses them (`UNSUPPORTED`); call the
  game's own code that uses them instead.
- **Only Unity objects are found by type.** `obj.find` and `obj.query` with a type search the objects Unity tracks
  (GameObjects, components, assets); the agent doesn't scan the managed heap, so plain C# objects are reached from a
  target, a static or a collection instead.
- **Content export covers what only the running game holds.** `content.export.start` writes textures, sprites and
  render textures as PNG (the pixels in use), objects' data as JSON and text assets' raw bytes. Audio, meshes and fonts
  don't change at runtime, so they're skipped with the warning `STATIC_ASSET`: extract them from the game's files with a
  static tool. Textures that aren't readable are read back through the GPU, so a player started without graphics
  (`-nographics`) can only export readable ones (the others get `IMAGE_NOT_READABLE`).
- **Addressables labels can stand for thousands of locations.** In `content.scan.start` each key's record keeps its
  first 200 locations (and each location its first 64 dependencies); the footer's `redactions` counts what was left out.
  `addressables.locate` on such a key returns every location, which can exceed the maximum message size.
- **Hooks on very small methods may never fire.** The runtime can copy (inline) a tiny method, such as a one-line
  property getter, into the methods that call it; a patch on the tiny method is then applied without error but never
  runs, and traces show only the calls that really happened. `hook.verify` tells you whether a method fires, how likely
  it is to be inlined, and which callers to hook instead.
- **A synchronous snippet can't be interrupted.** The game's runtime has no safe way to stop running code, so a
  snippet in the one-frame form that never returns freezes the game. Snippets in the iterator form stop at their next
  yield when cancelled or timed out ([scripting](scripting.md)).
- **Loaded assemblies stay until the game restarts.** The game's runtime can't unload assemblies, so every snippet,
  patch set and mod build the agent loads stays in memory; `agent.info` counts them and the agent warns when the count
  gets high.
- **Games can pause their main thread while loading.** After `Pump.StallMs` (3 s by default) without a frame, the agent
  logs a warning, and requests that need the main thread fail at once with `MAIN_THREAD_UNAVAILABLE` instead of hanging;
  they work again as soon as the game renders frames. Raise the setting if a game's loads regularly take longer.
