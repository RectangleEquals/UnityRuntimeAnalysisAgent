# Configuration

The agent reads its settings when the game starts, from the loader's config file (for BepInEx, the plugin's `.cfg` in
`BepInEx\config`; the BepInEx integration is still in progress). The orchestrator writes the settings it needs there;
you can also edit the file yourself while the game isn't running. An invalid value falls back to its default, and the
agent logs a warning saying so.

| Key | Default | What it does |
|---|---|---|
| `Discovery.ProvidersDir` | *(none)* | Folder where the agent writes its discovery file (`agent-<pid>.json`), so clients can find and authenticate to it. Without it the agent still listens, but no client can find it. |
| `Security.Mode` | `ReadOnly` | What clients may do: `ReadOnly` (observe only), `ReadOnly+Load` (also load content, which changes memory but not game logic), or `Full` (also change game state and run code). Clients can lower the mode while the game runs, never raise it. |
| `Transport.Mode` | `auto` | `auto` (a named pipe, falling back to TCP on 127.0.0.1 if the pipe can't be created), `pipe` or `tcp`. |
| `Transport.MaxFrameBytes` | `16777216` | Largest message accepted or sent, in bytes. |
| `Pump.FrameBudgetMs` | `4` | Main-thread time per frame the agent may use for its work, in milliseconds. Work that doesn't fit continues in the next frame. |
| `Pump.StallMs` | `3000` | After this long without a frame (a blocking load, a hang), requests that need the main thread fail with `MAIN_THREAD_UNAVAILABLE` instead of waiting. |
| `Jobs.MaxConcurrent` | `2` | Long-running jobs (surveys, indexes, exports) running at once; more wait in a queue. |
| `Events.MaxQueueBytes` | `8388608` | Unsent events per client, in bytes. When a client falls behind, its oldest events are dropped and counted, never replies to its requests. |
| `Agent.LogLevel` | `Info` | `Debug`, `Info`, `Warning` or `Error`. Clients can change it while the game runs (`agent.logLevel`). |

`agent.info` reports the effective limits under `limits`.
