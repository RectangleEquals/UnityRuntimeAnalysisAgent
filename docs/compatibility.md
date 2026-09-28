# Compatibility

What the agent has been verified with, and known limitations. This page grows as the agent is tested on more games.

## Verified

| | Version |
|---|---|
| Loader | BepInEx 5.4.23.5 (Windows x64) |
| Unity | 6000.3 (Unity 6), Mono scripting backend, Windows x64 player |
| Games | A test player, and a released Unity 6 Mono game |
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
- **Games can pause their main thread while loading.** After `Pump.StallMs` (3 s by default) without a frame, the agent
  logs a warning, and requests that need the main thread fail at once with `MAIN_THREAD_UNAVAILABLE` instead of hanging;
  they work again as soon as the game renders frames. Raise the setting if a game's loads regularly take longer.
