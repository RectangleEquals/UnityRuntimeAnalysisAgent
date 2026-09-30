# Rules: "when this happens, do that"

A rule watches the game for a combination of conditions and, when they are met, runs a list of actions in order on the
game's main thread. The typical use is precisely timed evidence: pause the game at the moment something happens, take a
screenshot with numbered UI marks, read some values, and let the game run again.

```json
{ "method": "rule.add", "params": {
    "when": { "seq": [ { "scene": { "loaded": "Crafting" } },
                       { "hook": { "method": <anchor of CraftingSystem.Complete>, "phase": "exit" } } ] },
    "then": [ { "pause": {} }, { "waitFrames": 1 },
              { "screenshot": { "annotate": "ui-marks", "maxWidth": 1568 } },
              { "snapshot": { "targets": [ { "var": "inventory" } ], "paths": [ [ { "name": "count" } ] ] } },
              { "resume": { "afterMs": 250 } } ],
    "outDir": "X:/Example/Captures" } }
```

`rule.add` answers with the rule's id and the permission mode it needs. `rule.wait { ruleId, timeoutMs }` then waits for
the rule to fire and returns the firing: when it happened, what triggered it, and every action's results (screenshot
paths with their marks and SHA-256, the snapshot, hook records, log entries, snippet results). Every firing is also sent
as a `rule.fired` event.

## Conditions

Each condition object has exactly one kind or combinator.

| Condition | Occurs when |
|---|---|
| `scene {loaded \| unloaded \| active}` | a scene whose name (or path) matches loads or unloads; `active` holds while the active scene matches |
| `delay {realtimeMs \| gameTimeMs \| frames}` | that much time has passed since the rule was armed (inside `seq`: since the previous step) |
| `value {target, path, op, value}` | the value at the path meets the operator (`eq`, `lt`, `regex`, `contains`, …); polled every `everyFrames` (default 5) |
| `value {target, path, changed: true}` | the value differs from the previous poll |
| `hook {method, phase, count, where}` | the method is called (`enter`, the default), returns (`exit`) or throws (`throw`), `count` times; `where` filters on arguments or the instance |
| `event {target, event}` | a C# event is raised, or a UnityEvent field or property is invoked |
| `log {regex, minLevel, source}` | a line in the agent's unified log matches |
| `exception {typeRegex, messageRegex}` | Unity logs a matching exception |
| `ui {text, path, kind, state}` | a visible uGUI element `appears`, `disappears` (after having been seen) or becomes `interactable`; polled every 5 frames |
| `predicate {assembly, entryType, method, everyFrames}` | a compiled `static bool Check(IAgentContext)` returns true (Full mode) |
| `prompt`, `pick` | the user answers a prompt or picks an object in the in-game overlay |

States (`value`, `ui`, `predicate`, `scene active`) occur when they *become* true, including when they are already true
at the moment the rule starts watching.

**Combinators:**
- `all: [...]`: every condition has occurred at some point (each is remembered once it has); with `simultaneous: true`,
  all of them hold in the same frame.
- `any: [...]`: the first one to occur.
- `seq: [...]`: in order, each one only watched after the previous one occurred; with `withinMs`, a step that takes
  longer than that starts the sequence over.
- `not: <condition>, forMs`: the condition doesn't occur for `forMs` (each occurrence restarts the wait).
- `count: <condition>, n`: the condition occurs `n` times.

`rule.list` shows each rule's progress per condition, and `rule.progress` events report each sub-condition as it is
met, with paths like `when.seq[0].scene`.

## Actions

| Action | Does | Mode |
|---|---|---|
| `pause {audio}` | sets the time scale to 0, remembering the previous one; `audio: true` also pauses the game's audio | Full |
| `waitFrames`, `waitMs` | lets things settle (for example one frame after pausing, so the paused frame renders) | ReadOnly |
| `screenshot {…}` | any `screenshot.capture` option; written under the rule's `outDir` | ReadOnly |
| `snapshot {targets, paths, view}` | an `obj.snapshot` of the values | ReadOnly |
| `hits {since}` | the calls the rule's hook conditions saw, since it was armed or since its last firing (the default) | ReadOnly |
| `logs {since, minLevel}` | log entries since the rule was armed or since its last firing (the default) | ReadOnly |
| `mark {label}` | a marker in the log | ReadOnly |
| `emit {kind, payload}` | an `exec.emit` event (and the item in the firing's results) | ReadOnly |
| `exec {assembly, entryType, entryMethod, args}` | runs a snippet (see [scripting](scripting.md)); it can wait over frames | Full |
| `uiClick {target}`, `invoke {target, method, args}` | clicks a UI element, calls a method | Full |
| `resume {afterMs}` | restores the time scale the game had (and its audio) | Full |
| `stayPaused {}` | keeps the game paused after the actions end | Full |
| `notify`, `highlight` | a toast or outline in the in-game overlay | ReadOnly |

A rule needs the highest mode any of its conditions or actions needs, checked when it is added: a screenshot-only rule
works in ReadOnly, a rule that pauses needs Full. A failing action is reported as an `agent.warning` and the next action
runs. Actions that change the game appear in the activity feed with the rule as their source.

**Timing.** Polled conditions are evaluated at the start of each frame. When a condition is met by something that
happened on the main thread (a hooked call, an event, a scene load), the actions start at the end of that same frame;
otherwise at the start of the next one. With `pauseImmediately: true` and a `pause` action, a rule completed by a hook
on the main thread pauses the game inside the hooked call itself; that frame still finishes, so a screenshot shows the
state right after the call.

## Firing

`fire: { mode: "once" | "repeat", maxFires, cooldownMs }` (default: once). A repeating rule starts watching again after
its actions end and the cooldown has passed. `expiresMs` ends a rule that hasn't fired by then.

## Safety

- **Pauses are bounded.** A pause held by a rule ends with its actions (unless `stayPaused`), and in any case after
  `Rules.MaxPauseMs` (default 30 s) unless the client keeps it with `rule.hold {ruleId, extendMs}`. `rule.cancel`
  releases a pause the rule holds. If something else already resumed the game, a rule never pauses it again on release.
- **Caps** ([configuration](configuration.md)): `Rules.MaxActive` rules at once (more are refused with `BUSY`),
  `Rules.MaxFiresPerMinute` firings and `Rules.MaxCapturesPerMinute` screenshots per minute across all rules (a rule
  that goes over is suspended, with an `agent.warning`).
- **Files** are written only under the rule's `outDir` (required when an action writes files), each listed with its
  SHA-256 in the firing.
- **Ownership.** Rules belong to the connection that added them and end when it disconnects, unless `persistent`
  (`Instrumentation.RemoveOnDisconnect` applies to them too). `rules.clear` cancels this connection's rules, or all with
  `owner: "all"`. Every rule ends, and every pause is released, when the agent stops.

## Methods

| Method | |
|---|---|
| `rule.add` | arms a rule → `{ruleId, requiredMode, armedAtFrame}` |
| `rule.list` | every rule with its state (`armed`, `fired`, `expired`, `cancelled`, `suspended`), fires and progress |
| `rule.get {ruleId}` | the rule, its summary and its firings (the last 100) |
| `rule.wait {ruleId, timeoutMs}` | the next completed firing, or `null` on timeout; a rule that has ended answers at once with its last firing |
| `rule.hold {ruleId, extendMs}` | keeps the rule's pause past the watchdog (Full) |
| `rule.cancel {ruleId}` | ends the rule and releases its pause |
| `rules.clear {owner}` | cancels this connection's rules, or all |
