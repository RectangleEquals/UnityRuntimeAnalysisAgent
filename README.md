# UnityRuntimeAnalysisAgent

A BepInEx plugin that runs **inside a Unity game** (Mono backend) and lets a local analysis tool observe, and with your permission change, the running game. It's the in-game half of [UnityLudometryMCP](https://github.com/RectangleEquals/UnityLudometryMCP), a local Model Context Protocol (MCP) server that helps you understand Unity games and build mods for them.

> **Status: pre-release (v0.1 in development).** Not usable yet. See [CHANGELOG.md](CHANGELOG.md).

## What it does (planned for v0.1)
- **Introspection:** assemblies, types, members, IL, cross-references, scenes, objects, statics, loaded content.
- **Instrumentation:** method hooks, ordered call traces, timing, value watches, event subscriptions.
- **Actions** (only in the permission mode you grant): set values, invoke methods, click UI, control time, run small scripts, try temporary patches, hot-reload mods under development.
- **Verification:** in-game test runs, screenshots, logs, metrics.
- **In-game overlay:** shows everything the connected tool is doing, with an **E-STOP** button that stops all of it instantly.

It talks only to local tools on your own machine, over an authenticated named pipe. It never uses the network.

## Requirements
- Windows x64, a Unity game using the Mono scripting backend (Unity 2018.1 or newer).
- [BepInEx 5](https://github.com/BepInEx/BepInEx) (5.4.x). UnityLudometryMCP can install it for you, with your consent, and remove it again exactly.

## Building from source
- .NET SDK 10 (see `global.json`).
- `git clone --recursive https://github.com/RectangleEquals/UnityRuntimeAnalysisAgent`
- `dotnet build -c Release`.

A Release build also produces the plugin package in `dist/` ([details](docs/CONTRIBUTING.md#the-package)).

## Documentation
- [Contributing](docs/CONTRIBUTING.md): repository layout, building, and the compatibility rules the code follows.
- [All documentation](docs/README.md). User guides are added as features land.

## About the use of AI in this project
This section is here so you can decide for yourself, with accurate information.

**How this project is being made.** The design and implementation are produced with the help of an AI coding assistant (Anthropic's Claude), working under the direct supervision of a human developer. In practice:
- The human decides what the project is for, sets every requirement and constraint, and chooses between the options the AI proposes.
- The AI drafts code and documentation within those requirements, one small, reviewable step at a time.
- **The human reviews every step** before it becomes part of the project. Commits are made by the human, or by the AI only with the human's explicit permission.
- Behaviour is verified during development, including by running the plugin in real Unity games, not just by trusting generated code.

**How this project uses AI when you run it.** It doesn't, by itself. This plugin contains no AI model and never contacts an AI service. It responds only to requests from a local tool you choose to connect (normally UnityLudometryMCP, which your own AI assistant may drive). And you stay in control:
- It starts in **read-only mode**. Changing anything in your game requires a permission level you explicitly grant.
- Every action taken through it is listed in the in-game overlay, and **E-STOP** stops all automated activity immediately, without needing the tool's cooperation.
- It only writes files where the connected tool tells it to, and removing it is as simple as deleting its folder (or letting UnityLudometryMCP uninstall it exactly).

If you have questions or concerns about any of this, please open an issue.

## License
MIT. See [LICENSE](LICENSE).
