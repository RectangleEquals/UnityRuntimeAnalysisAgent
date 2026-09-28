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
