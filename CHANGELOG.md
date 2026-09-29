# Changelog

## 0.1.0-dev (unreleased)
- Repository and toolchain scaffolding: solution, projects, central package management.
- Shared protocol consumed through the `external/protocol` submodule (now `protocol-v0.1.0-dev.2`).
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
  shortcuts), every setting bound with its default and description, `agent.selfTest`, and the release package
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
