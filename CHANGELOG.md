# Changelog

## 0.1.0-dev (unreleased)
- Repository and toolchain scaffolding: solution, projects, central package management.
- Shared protocol consumed through the `external/protocol` submodule (now `protocol-v0.1.0-dev.4`).
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
  (`dist/`: the six plugin assemblies, a verified `package.json` and the zip, byte-reproducible).
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
