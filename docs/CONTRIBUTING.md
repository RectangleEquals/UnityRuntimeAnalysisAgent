# Contributing to UnityRuntimeAnalysisAgent

Thanks for your interest! This guide explains how the repository is laid out, how to build and test it, and the
rules that keep the plugin working across many Unity games. The project is pre-release, so expect things to move.

## Repository layout

| Path | What it is |
|---|---|
| `src/UnityRuntimeAnalysisAgent.Core` | The agent itself: protocol, request handling, analysis and instrumentation. Knows nothing about Unity or the loader. |
| `src/UnityRuntimeAnalysisAgent.Unity` | Unity bindings: everything that touches `UnityEngine`. |
| `src/UnityRuntimeAnalysisAgent.Overlay` | The in-game overlay (IMGUI). |
| `src/UnityRuntimeAnalysisAgent.Api` | The small public API that scripts and mods under test compile against. |
| `src/UnityRuntimeAnalysisAgent.BepInEx5` | The BepInEx 5 plugin entry point that wires everything together. |
| `tests/*.Core.Tests`, `*.Api.Tests`, `*.Protocol.Tests` | Unit tests. Run anywhere, no game needed. |
| `tests/*.Integration` | Tests against a real Unity player. Run locally only (see below). |
| `tools/AgentConsole` | A developer console for talking to a running agent. |
| `external/protocol` | Git submodule with the shared protocol (schemas, fixtures, the `UnityLudometry.Protocol` package). See [its README](../external/README.md). |

## Building and testing

Requirements: the .NET SDK pinned in `global.json` (10.0.x). Nothing else is needed for the build and unit tests.

```
git clone --recursive https://github.com/RectangleEquals/UnityRuntimeAnalysisAgent
dotnet build -c Release
dotnet test -c Release
```

- Warnings are errors. A pull request must build with 0 warnings.
- Machine-specific paths go in a git-ignored `Directory.Build.local.props` at the repo root. Copy
  `build/Local.props.example` to start. None of these paths are needed for the build or unit tests.

### Integration tests

Integration tests skip themselves unless the environment variable `URAA_INTEGRATION_SETTINGS` points at a JSON
settings file on your machine. Keep that file outside the repository. Its keys:

| Key | Meaning |
|---|---|
| `unityEditorPath` | Unity editor used to build the fixture game |
| `fixtureBuildDir` | Where the built fixture player goes |
| `gameDir` | A Unity game install for read-only scenarios (optional) |
| `providersDir` | Directory for agent discovery files; `null` uses a fresh temp directory per run |
| `bepInExZip`, `bepInExZipSha256` | The BepInEx 5 archive to install into test players, and its expected SHA-256 |

## The protocol submodule

The wire protocol (JSON Schemas, golden fixtures and the `UnityLudometry.Protocol` package) comes from the
UnityLudometryMCP repository through the `external/protocol` submodule, pinned to a protocol tag.

- **The protocol is never changed here.** If agent work needs a protocol change, open an issue or a pull request in
  the UnityLudometryMCP repository; once a new protocol tag exists, bump the pin (see `external/README.md`).
- `tests/UnityRuntimeAnalysisAgent.Protocol.Tests` replays every fixture of the pinned protocol, and grows handler
  conformance checks as features land.
- A build without the submodule stops with a message telling you to run `git submodule update --init --recursive`.

## Compatibility rules

The plugin has to load in games built with many Unity versions (2018.1 onwards, Mono backend) next to whatever
else the player has installed. These rules make that work. The tests enforce the ones marked ✔.

- **Shipped assemblies target `netstandard2.0`.** Modern C# syntax is fine: PolySharp supplies the polyfills at
  compile time, without runtime dependencies.
- **No third-party DLL ships with the plugin.** BepInEx, HarmonyX and UnityEngine are provided by the game and the
  loader, so they are referenced compile-only (`PrivateAssets="all" ExcludeAssets="runtime"`). The only assembly the
  plugin ships besides its own is `UnityLudometry.Protocol.dll`, the shared protocol package, which has no
  dependencies. Check the `BepInEx5` project's output after changing references.
- ✔ **Layering.** `Core` must not reference `UnityEngine*`, `BepInEx*` or any other loader (it may use
  `UnityLudometry.Protocol`). `Api` and `UnityLudometry.Protocol` reference only the framework. Unity code goes in `Unity` or `Overlay`, loader code in the loader project.
- **Unity API baseline 2018.1.0.** `Unity` and `Overlay` compile against `UnityEngine.Modules` 2018.1.0, the oldest
  API surface that has everything the agent needs. Code compiled against it binds by name on newer players. Newer
  APIs must be reached through reflection and must degrade gracefully when missing.
- ✔ **Never use `UnityEngine.Input` directly.** It moved from `CoreModule` to `InputLegacyModule` in Unity 2019.1, so
  a direct reference compiled against 2018.1 fails with a `TypeLoadException` on newer players. Read input through
  the reflection-bound accessor.
- **HarmonyX stays on 2.9.0**, the version of `0Harmony.dll` that ships with BepInEx 5.4.23.5. Compiling against a
  newer HarmonyX can bind to members the loader's copy doesn't have. Only test projects may override it.
- **Package versions are central.** Add or change versions only in `Directory.Packages.props`; project files carry
  no versions.

## Code style

`.editorconfig` is the reference: 4-space indentation, file-scoped namespaces, `_camelCase` private fields, and
nullable reference types enabled everywhere. Public members of `Api` need XML documentation.

## Pull requests

- Keep each pull request focused, with tests for new behaviour. CI (Windows) must be green.
- Update the docs your change affects (this guide, the user docs, `CHANGELOG.md`).
- Don't commit personal information or machine-specific paths: no user names, absolute paths or local settings
  files. CI rejects known personal terms.
- Found a bug or have an idea? Open an issue first for anything bigger than a small fix.

By contributing, you agree that your contributions are licensed under the [MIT License](../LICENSE).
