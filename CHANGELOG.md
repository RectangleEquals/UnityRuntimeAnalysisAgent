# Changelog

## 0.1.0-dev (unreleased)
- Repository and toolchain scaffolding: solution, projects, central package management.
- Shared protocol consumed through the `external/protocol` submodule (now `protocol-v0.1.0-dev.7`).
- Transport, handshake and discovery: framed connections over a named pipe (restricted to the current user where the
  runtime allows) or a loopback TCP fallback, the token-authenticated `hello` handshake, `ping`, `agent.info`,
  `agent.capabilities`, `cancel` and event subscriptions, and an atomically written discovery file.
- `tools/AgentClient` (a small client library) and `tools/AgentConsole` (a developer console for talking to a running
  agent).
- The core runtime: method dispatch with mode and capability checks, timeouts and `cancel`; a main-thread pump that runs
  work within a per-frame budget, with multi-frame routines and a watchdog; background jobs (`job.get`, `job.wait`,
  `job.cancel`, `job.list`, `job.progress` and `job.finished` events) and verified NDJSON output files; batched,
  throttled events with back-pressure that never drops replies; `batch` (optionally within one frame); the activity feed
  and audit log (`activity.list`, `activity.get`); `agent.setMode` (lower only) and `agent.logLevel`; and the settings
  `Pump.FrameBudgetMs`, `Pump.StallMs`, `Jobs.MaxConcurrent` and `Events.MaxQueueBytes`
  ([configuration](docs/configuration.md)).
- The BepInEx 5 plugin: the agent now runs inside games. Unity bindings (a hidden main-thread pump host that survives
  the game destroying it; reflection binders for optional modules), the loader bridge (settings, logs, plugins,
  shortcuts), every setting bound with its default and description, `agent.healthCheck`, and the release package
  (`dist/`: the plugin assemblies, a verified `package.json` and the zip, byte-reproducible).
  Verified with BepInEx 5.4.23.5 in a Unity 6 Mono player ([compatibility](docs/compatibility.md)).
- The data model: exact code references (anchors: module version id + metadata token, the same ones static tools
  read; `code.resolve` for exploratory lookup by name), live object handles (`handles.list`, `handles.release`,
  `handles.releaseAll`; setting `Handles.Max`), variables (`vars.set`, `vars.get`, `vars.list`, `vars.delete`),
  targets and member paths, the value encoding and decoding, redaction stubs whenever a limit leaves a value out
  (`value.expand` reads it; nothing is dropped silently), durable locators (`locator.resolve`), and paging cursors.
  `AgentConsole` gained `expand` and `resolve`.
- Methods that wait (`job.wait`, `batch`) no longer hold a thread while waiting.
- Code introspection: loaded assemblies (with the SHA-256 of their files), types, members, hierarchies, implementations,
  attributes (decoded without running them), IL (`code.il`, JSON or text) and per-method IL hashes, cross-references
  (`code.callers`, `code.callees`, `code.fieldAccess`, `code.strings`, `code.allocations`), the `code.assemblyLoaded`
  event, and two bulk jobs writing NDJSON files: `survey.start` (types, members, Unity messages, serialized fields, custom
  serializer markers, singleton-like statics, instance counts) and `il.index.start` (the cross-reference index), both
  reused when nothing changed. Nothing here runs a static constructor.
- Live state: scenes (`scene.list`, `scene.roots`, the `scene.changed` event), the GameObject hierarchy (`go.tree`,
  `go.find`, `go.get`, `go.path`; same-named siblings are numbered, `Name[2]`), objects (`obj.find` by type and scope,
  `obj.inspect`, `obj.get`, `obj.snapshot` across targets, `obj.describe`), statics and singletons (`static.get`,
  `static.singletons`), declarative queries spread over frames (`obj.query`, or as a job with `obj.query.start`),
  collections (`coll.page`, `coll.count`) and listeners (`event.listeners`, `unityEvent.listeners`). Reads can carry
  expected values and report each match. Statics are read only once the game has initialized their type, unless
  `allowInit` is set (Full mode).
- Changes, in Full mode only, each audited with the locator of what it changed: `obj.set` (structs are written back),
  `obj.invoke` (with generic arguments; out and ref arguments are returned), `obj.create`, `go.create`,
  `go.instantiate`, `go.setActive`, `obj.destroy`, `component.add`, `component.remove`, `coll.add`, `coll.remove`,
  `coll.set` and `event.raise`.
- Content: loaded content with type-specific summaries (`content.list`, `content.summary` with estimated memory),
  Addressables (`addressables.info`, `addressables.keys`, `addressables.locate`, and `addressables.load` /
  `addressables.release` in `ReadOnly+Load`), AssetBundles (`bundles.loaded`, `bundles.load`), `Resources`
  (`resources.load`, `resources.loadAll`), and two jobs: `content.export.start` (PNG of the textures, sprites and render
  textures in use, read back through the GPU when they aren't readable, in strips spread over frames; JSON of objects' data; text assets' raw bytes;
  with a manifest) and `content.scan.start` (an NDJSON inventory of loaded assets, Addressables keys and bundles).
- `code.attributes` no longer fails on a method whose parameter types come from a missing assembly.
- Instrumentation, all of it reversible and owned by the client that created it: method hooks (`hook.add` with phases,
  captures, conditions on arguments or the instance, sampling, `maxHits` and a ring buffer; `hook.hits`, `hook.list`,
  `hook.remove`, `hooks.clear`, batched `hook.hits` events), call traces (`trace.start` as a job with stop conditions,
  an NDJSON trace file, a summary with the ordered sequence, a call tree and per-method counts and durations, live
  `trace.records` events; `trace.stop`), timing (`profile.start`: per-method calls, total, mean, p50, p95, max and by
  thread, plus frame times over the window), patch-point verification (`hook.verify`: arms, triggers, counts hits,
  assesses the chance of inlining and names callers to patch instead), value watches (`watch.add`, `watch.changes`,
  `watch.list`, `watch.remove`, `watch.changes` events), C# and UnityEvent subscriptions (`event.subscribe`,
  `event.unsubscribe`, `event.raised` events), exception monitoring (`exceptions.monitor`: the game's log, and
  first-chance exceptions where the runtime raises them, filtered and rate-limited; `exception` events), and
  `instrumentation.status` / `instrumentation.clear`. A fault in instrumentation turns that instrumentation off with an
  `agent.warning` instead of affecting the game. Settings `Instrumentation.MaxMethods` and
  `Instrumentation.RemoveOnDisconnect`; `agent.info` reports hooks and patched methods.
- Scripting, in Full mode only and audited with each assembly's name and SHA-256 ([scripting](docs/scripting.md)):
  the scripting API (`UnityRuntimeAnalysisAgent.Api`: `IAgentContext` with arguments, variables, session state, log,
  handles, waits, emitted events and hit counters; `AgentApi.Current` for live patches), snippets (`exec.run`, one frame
  or several, with named sessions; `exec.sessions`, `exec.sessionClose`; `exec.emit` events), live Harmony patch sets
  (`patch.apply`, `patch.revert`, `patch.list`, removed when the agent stops), the process's Harmony patches whoever owns
  them (`patch.inspect`, `patches.all`), and mod hot-reload (`mod.reload`, `mod.unload`, `mod.list`). `hook.verify` can
  use a snippet as its trigger. `agent.info` counts the assemblies the agent loaded and the live-patched methods.
- Game control: time (`time.info`; `time.scale`, `time.pause`, `time.resume` and `time.step`, which runs the game for
  exactly N frames and pauses it again; `time.waitFrames` and `time.waitSeconds`, which wait without holding up the
  game), scenes (`scene.load`, `scene.unload`, `scene.setActive`), uGUI and TextMeshPro (`ui.snapshot` with each element's
  kind, text, images, state, options and screen rectangle; `ui.find`; `ui.click` through the EventSystem, `ui.setText`,
  `ui.setValue`, `ui.submit`, `ui.cancel`, `ui.select`, all through the game's own handlers), and the application
  (`app.info`, `app.runInBackground`, `app.quit`). `hook.verify` can use a UI click as its trigger. The agent's own UI
  is never listed or driven. Capabilities report `module:ugui` and `module:tmp`.
- Diagnostics: a unified log of Unity's messages, other plugins' and mods' (through the loader) and the agent's own, with
  a global sequence and capped messages and stacks (`logs.tail` with level/source/channel/regex filters, `logs.search`,
  `logs.mark`, and the `log` event with per-subscription filters; setting `Logs.BufferSize`); exception monitoring now
  reads it. Screenshots for vision (`screenshot.capture`: the finished frame or one camera, super-sampling, downscaling
  to a maximum size, cropping to a rectangle or to a UI element's / renderer's / collider's screen bounds, numbered
  `ui-marks` that map to clickable handles, bursts, PNG or JPEG, inline base64, written atomically with SHA-256;
  `screenshot.camera`). Metrics (`metrics.get`: frame times, fps, managed and Unity memory, collections, object counts,
  working set, private bytes, threads; `metrics.sample.start`: a time series to an NDJSON file). `agent.healthCheck`
  now also resolves an anchor, hooks and unhooks an agent-owned method, and checks the log.
- UI visibility: an element counts as visible only if some of it is on screen, inside every mask and scroll view it
  sits in, and not faded out by a CanvasGroup; `ui.snapshot`/`ui.find` with `onlyVisible` use that. `ui-marks` mark
  only what can be seen (by its visible part), count their limit (now 199) after leaving out what can't, and place each
  number beside its box where it covers no other mark, sized to the image. UI text no longer includes rich-text markup.
- Rules ([rules](docs/rules.md)): "when these things happen, do this". Conditions: scenes, delays (real time, game time,
  frames), values (operators or changes), hooked calls (enter, exit, throw; counted; filtered on arguments), C# events
  and UnityEvents, log lines, logged exceptions, uGUI elements appearing, disappearing or becoming interactable, and
  compiled predicates; combined with `all` (latched or simultaneous), `any`, `seq` (with `withinMs`), `not` (for a
  window) and `count`. Actions, in order on the main thread: pause (optionally audio), wait frames or milliseconds,
  screenshot (every capture option), snapshot, the rule's hook records, log entries, log marker, snippet, UI click,
  method call, resume (after a delay), stay paused, and emit. `pauseImmediately` pauses inside the hooked call.
  Methods `rule.add` (the mode a rule needs is computed from its content), `rule.list`, `rule.get`, `rule.wait`,
  `rule.hold`, `rule.cancel`, `rules.clear`; events `rule.fired` and `rule.progress`. Pauses held by rules are released
  after `Rules.MaxPauseMs` unless held; `Rules.MaxActive`, `Rules.MaxFiresPerMinute` and `Rules.MaxCapturesPerMinute`
  cap them; files go only under the rule's `outDir`. Rules belong to their connection like instrumentation and all end
  with the agent. Actions that change the game are audited with the rule as their source.
- In-game tests ([testing](docs/testing.md)): an authoring API in `UnityRuntimeAnalysisAgent.Api` (Api version 0.2):
  `[GameTestFixture]`, `[GameTest(TimeoutMs, Category, RequiresScene, Order, Skip)]`, per-test and per-fixture setup and
  teardown, and `GameTestContext` with waits (frames, seconds, conditions, scenes, end of frame), assertions, a log,
  scoped call counters and call captures, sampled values, uGUI find/click/type, variables and result attachments.
  `test.list` and `test.run` (a job, Full mode) run them sequentially on the main thread: teardown always runs, timeouts
  are checked at every yield, and each result carries its status (`passed`, `failed`, `error`, `timeout`, `skipped`),
  message, stack, attachments and the log lines written while it ran; events `test.started`, `test.result`,
  `test.finished`.
- Probes ([probes](docs/probes.md)): `probe.run`, `probe.runBatch` (a job, in order, with `stopOnError`, a budget and
  `probe.progress` events), `probe.result` and `probe.cancel`. Kinds `read_statics`, `find_instances`, `read_members`,
  `query`, `content`, `watch`, `hook_verify`, `trace` and `call` (with the user's consent and Full mode), each built on
  the agent's own methods; statuses `ok`, `partial`, `failed`, `needs_trigger` (armed until gameplay happens, cancelled
  or timed out) and `stale`; evidence as locators with short excerpts.
- `locator.resolve` now resolves asset locators (`live://asset/<Type>/<name>#<instanceId>`) while the game runs.
- The complete UI model (protocol `0.1.0-dev.5`): every element reports its `visibility` (visible, partial, clipped by a
  mask or scroll view, off screen, hidden) with the visible part, its `interaction` (clickable, disabled, hover-only,
  display), its scroll container, whether it's selected, its keyboard/gamepad `navigation` and its text with markup
  (`rawText`); elements with only pointer handlers are listed too. `ui.snapshot` pages with a cursor instead of a cap and
  filters by interaction (as does `ui.find`), and lays out a screen opened in the same frame before reading it. New
  actions: `ui.hover` (pointer enter/exit, e.g. to reveal tooltips), `ui.scrollTo` (scroll views move until the element
  is visible) and `ui.navigate` (a move from the selected element). `ui-marks` also mark disabled elements (grey) and
  hover-only ones (amber), and every mark carries its interaction and images. `ui.frameworks` reports which UI
  frameworks the game can use and uses (uGUI canvases and EventSystem, UI Toolkit documents and panels, TextMeshPro,
  IMGUI behaviours), its input handling (Input Manager, Input System, gamepads) and a classification per framework.
- The agent keeps running when a game destroys BepInEx's manager object while loading (some games destroy every
  object); it stops only when the game quits, and its main-thread host comes back within a frame through Unity's own
  per-frame callbacks. Connections log why they closed.
- World-space UI without an assigned camera is placed through the camera that draws its layer (the main camera when it
  does), so UI a game draws into a texture is measured in that texture; world-space UI behind the camera counts as off
  screen. Camera-space canvases without a camera are placed like overlays, as Unity draws them.

- The in-game overlay's model layer (nothing is drawn yet; the renderers follow): the Hidden / Collapsed / Expanded
  states with edge docking, drag-and-snap and the arrow's status; notifications (filtered, coalesced, throttled),
  prompts with timeouts, highlights, the Inspector's selection history and its lock (edits only in Full mode); tab
  view models that read the agent's own methods in-process (the overlay's reads stay out of the activity feed, its
  actions are recorded as `source: overlay`); the Copy report; and E-STOP, which lowers the mode to ReadOnly, reverts
  live patches, removes instrumentation, cancels jobs and rules (releasing rule-held pauses), tells clients
  (`overlay.estop`) and then disconnects them. New settings `Overlay.Renderer`, `Overlay.Theme`, `Overlay.RetroFonts`,
  `Overlay.Effects`, `Overlay.Gamepad`, `Overlay.GamepadToggle` and `Overlay.GamepadPausesGame`
  ([configuration](docs/configuration.md)).
- The overlay's data layer and assets (still not drawn; the renderers follow): view files (panels, text, buttons,
  lists, tables and the other node types, with bindings to the agent's data and commands) validated as they load, with
  anything unknown skipped and reported; themes of tokens and classes with hover/focus/active/disabled/checked
  states; a flexbox layout engine matching UI Toolkit's own layout; keyboard/gamepad focus and button chords; tweens
  over unscaled time; font choice (the pixel font at whole multiples of its size). The package now ships
  `overlay/` next to the plugin: asset bundles for Unity 2021.3–6000.2 and 6000.3+ (fonts, UI Toolkit theme, effect
  shaders), a Phosphor icon atlas, the default theme and the fonts' and icons' licences, all with hashes in
  `package.json`; the build refuses bundles that don't match their manifest. The binaries aren't in git: they're
  published as a GitHub release (`overlay-assets-r<N>`), locked by `assets/overlay/release.json`, and downloaded and
  verified by the build when missing. `tools/OverlayAssets` rebuilds and releases them
  ([README](tools/OverlayAssets/README.md)).
- The overlay is drawn: the arrow on its screen edge (status tint, a badge counting prompts and notifications, drag to
  any edge), the expanded panel with its tabs, E-STOP and the selected tab's view, and notification and prompt cards.
  Three renderers, tried in order (or forced with `Overlay.Renderer`), with the reasons for the choice logged: UI
  Toolkit (Unity 2021.3+, not 2023.2; real UI Toolkit elements and controls with the theme applied inline, which checks
  that the bundle's theme really applies and otherwise hands over to uGUI), styled uGUI (built in code and laid out by
  the overlay's flex engine, with generated rounded sprites and the bundle's fonts), and a small IMGUI emergency box
  (status, why the full overlay isn't available, E-STOP). Rows in lists and tables keep their height, scroll areas clip
  their content, and text colours come from the theme, so the overlay stays legible over any game. The overlay's
  objects are rebuilt if the game destroys them; failures never reach the game. Hotkeys (`Overlay.ToggleKey`,
  `Overlay.HideKey`, `Overlay.PickKey`, `Overlay.EStopKey`), the gamepad chord (`Overlay.GamepadToggle`, Input System
  or Input Manager), `Overlay.ForceCursorWhenExpanded`, `Overlay.BlockUiClicks` (with UI Toolkit, an invisible uGUI
  blocker under the overlay) and `Overlay.BlockWorldInput` (opt-in; the Input Manager's mouse-button queries) now
  work. The arrow's edge and position and whether the panel is docked are saved to the configuration. The UI Toolkit
  renderer ships as its own assembly, `UnityRuntimeAnalysisAgent.Overlay.UIToolkit.dll`, loaded only when it's used.
  The docked panel sits beside the arrow, never over it. Overlay assets `overlay-assets-r2`: fonts are imported with a
  pixel of padding around each glyph, so the pixel font stays crisp in uGUI.
- The package is byte-identical from any checkout: Release builds map source paths to a fixed root (here and in the
  protocol package, now `protocol-v0.1.0-dev.6`), and generated JSON is written with the same line endings as a
  checkout.
- Clients can talk to the player in the game: `overlay.notify` shows a notification, and `overlay.prompt` asks a
  question with answer buttons, answered through the `overlay.promptResult` event. A prompt's `textButton` (protocol
  `0.1.0-dev.7`) opens a text field on its card: the player types the answer (Enter or Send) and the event carries
  the `text`. Prompt cards list their answers one per line at the card's full width, and the typing caret blinks
  beside the text. Notification cards docked at the bottom edge now stack upwards from the arrow (UI Toolkit; they
  used to grow below the screen). The overlay keeps the last 200 notifications, and the expanded panel's Activity
  tab lists them, with every open question answerable there too.
- The overlay's nine tabs have views:
  - **Status:** versions, the mode, connected clients (messages, bytes, dropped events), FPS with a sparkline, memory,
    pump health and active counts. **Copy report** puts the Markdown report on the clipboard; **Run the health
    check**.
  - **Activity:** the request feed (newest first, mutations marked ●, filters all / mutating / errors), jobs with
    progress and Cancel, questions and notifications.
  - **Inspector:** the selection's members, back/forward, the lock, **Send to client** (names it `pickN`), and the
    scenes and their hierarchy (click to select).
  - **Pinned:** variables with live values, watches with Remove.
  - **Instrumentation:** hooks, watches, live patch sets (Revert) and rules (Cancel), and **Clear all
    instrumentation**.
  - **Mods & Tests:** plugins and mods, and the last test run (totals and each result, also in the Copy report).
  - **Logs:** newest first, by level.
  - **Control:** E-STOP, pause/resume, step 1/10 frames, speed presets, audio pause, run in the background, disconnect
    clients, lower the mode. The overlay can only ever lower the mode.
  - **Settings:** every `Overlay.*` setting, toggled, cycled, stepped or edited (by typing) and saved to the
    configuration.

  Tab buttons call agent methods as audited overlay actions; failures show as notifications. The arrow's tint now
  follows the connection: idle, connected, busy while a client is exchanging messages or a question is open, E-STOP.
- Overlay readability:
  - Notifications and questions in the Activity tab wrap onto as many lines as they need instead of being cut off.
    Lists can size rows to their content (`"wrapRows": true` in a view file).
  - A notification stays on screen at least long enough to read it whole, however short a duration the client asks
    for.
  - Buttons with an icon size to the icon and the label together (the icon no longer covers the text), in the UI
    Toolkit and uGUI renderers. Section headings no longer shrink under the lists when the panel is short.
  - Status shows frame times as `mean ms (p95 · max)` instead of the raw value.
