# Configuration

The agent reads its settings when the game starts, from the loader's config file: for BepInEx,
`BepInEx\config\com.github.rectangleequals.unityruntimeanalysisagent.cfg`, which the plugin creates with every setting
and its description on first start. The orchestrator writes the settings it needs there; you can also edit the file
yourself while the game isn't running. An invalid value falls back to its default, and the
agent logs a warning saying so.

| Key | Default | What it does |
|---|---|---|
| `Discovery.ProvidersDir` | *(none)* | Folder for the discovery file (agent-<pid>.json) that lets clients find and authenticate to the agent. Set by the orchestrator; without it no client can connect. |
| `Security.Mode` | `ReadOnly` | What clients may do: ReadOnly (observe), ReadOnly+Load (also load content) or Full (also change the game and run code). Clients can lower it at runtime, never raise it. Values: `ReadOnly`, `ReadOnly+Load`, `Full`. |
| `Transport.Mode` | `auto` | auto: a named pipe, falling back to TCP on 127.0.0.1. pipe or tcp: only that. Values: `auto`, `pipe`, `tcp`. |
| `Transport.MaxFrameBytes` | `16777216` | Largest message accepted or sent, in bytes. |
| `Pump.FrameBudgetMs` | `4` | Main-thread time per frame for the agent's work, in milliseconds. |
| `Pump.StallMs` | `3000` | After this long without a frame, main-thread requests fail with MAIN_THREAD_UNAVAILABLE instead of waiting. |
| `Jobs.MaxConcurrent` | `2` | Long-running jobs (surveys, indexes, exports) at once; more wait in a queue. |
| `Handles.Max` | `20000` | Live object handles kept (least recently used ones are released). |
| `Instrumentation.MaxMethods` | `2000` | Methods instrumented at once. |
| `Instrumentation.RemoveOnDisconnect` | `true` | Remove a client's non-persistent instrumentation when it disconnects. |
| `Logs.BufferSize` | `10000` | Log lines kept in memory. |
| `Events.MaxQueueBytes` | `8388608` | Unsent events per client, in bytes; over it the oldest events are dropped and counted (never replies). |
| `Agent.LogLevel` | `Info` | The agent's own log verbosity. Values: `Debug`, `Info`, `Warning`, `Error`. |
| `Rules.MaxActive` | `32` | Automation rules active at once. |
| `Rules.MaxFiresPerMinute` | `60` | Rule firings per minute before rules are suspended. |
| `Rules.MaxCapturesPerMinute` | `30` | Screenshots taken by rules per minute. |
| `Rules.MaxPauseMs` | `30000` | Longest a rule may keep the game paused, in milliseconds. |
| `Overlay.Enabled` | `true` | Show the in-game overlay. |
| `Overlay.ToggleKey` | `F9 + LeftControl` | Expand or collapse the overlay. |
| `Overlay.HideKey` | `F9 + LeftControl + LeftShift` | Hide the overlay completely, or show it again. |
| `Overlay.PickKey` | `F8 + LeftControl` | Pick an object in the game. |
| `Overlay.EStopKey` | *(none)* | E-STOP: stop all automated activity at once (no shortcut by default). |
| `Overlay.StartState` | `Collapsed` | How the overlay starts. Values: `Hidden`, `Collapsed`, `Expanded`. |
| `Overlay.Edge` | `Right` | The screen edge the overlay is docked to. Values: `Left`, `Right`, `Top`, `Bottom`. |
| `Overlay.EdgeOffset` | `0.5` | Position along that edge, from 0 to 1. |
| `Overlay.Docked` | `true` | Keep the overlay docked to its edge. |
| `Overlay.PanelSize` | `auto` | Panel size (auto or width x height). |
| `Overlay.Scale` | `auto` | UI scale (auto follows the screen height). |
| `Overlay.Opacity` | `0.92` | Panel opacity. |
| `Overlay.FontSize` | `13` | Font size. |
| `Overlay.RefreshHz` | `4` | How often the panel's contents refresh, per second. |
| `Overlay.Toasts` | `Important` | Which notifications are shown next to the overlay. |
| `Overlay.BlockUiClicks` | `true` | Keep clicks on the overlay from reaching the game's UI. |
| `Overlay.BlockWorldInput` | `false` | Keep input over the overlay from reaching the game world. |
| `Overlay.ForceCursorWhenExpanded` | `true` | Show and free the mouse cursor while the overlay is expanded. |
| `Overlay.InspectorStartsLocked` | `true` | The inspector starts locked (no edits until unlocked). |
| `Overlay.LocalTimeControl` | `true` | Allow pausing and stepping the game from the overlay. |
| `Overlay.EStopPauses` | `false` | E-STOP also pauses the game. |
| `Overlay.EStopDisconnects` | `true` | E-STOP also disconnects clients. |
| `Overlay.ExcludeFromScreenshots` | `true` | Hide the overlay in screenshots the agent takes. |
| `Overlay.VisibleTabs` | `all` | The overlay tabs to show (all, or a comma-separated list). |
| `Overlay.FollowStaticToolSelection` | `false` | Follow selections made in a static analysis tool, when the orchestrator relays them. |

Settings for features that aren't finished yet (instrumentation, logs, rules, the overlay) are already in the file, so it
lists everything from the start; they take effect as those features arrive. Shortcuts use BepInEx notation (for example
`F9 + LeftControl`). `agent.info` reports the effective limits under `limits`.
