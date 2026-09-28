# Changelog

## 0.1.0-dev (unreleased)
- Repository and toolchain scaffolding: solution, projects, central package management, CI.
- Shared protocol consumed through the `external/protocol` submodule (now `protocol-v0.1.0-dev.2`); its fixtures are
  replayed in `Protocol.Tests`.
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
