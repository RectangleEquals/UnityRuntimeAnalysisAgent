# Probes

A probe asks the game one question in a uniform shape, and answers with a status, the underlying result, and
**evidence**: re-resolvable locators with short excerpts. Probes are how a planner (normally the orchestrator) gathers
facts it can record directly. Each kind is built on the agent's own methods, so their parameters, checks, permission
modes and audit apply unchanged.

```json
{ "method": "probe.run", "params": { "probe": {
    "id": "p-118", "kind": "trace",
    "anchors": [ <anchor of Inventory.AddItem>, <anchor of Inventory.Store> ],
    "params": { "windowMs": 500 },
    "limits": { "timeoutMs": 60000 },
    "requiresGameplay": true,
    "outDir": "X:/Example/Output" } } }
```

## Kinds

| Kind | Built on | Anchors | `params` |
|---|---|---|---|
| `read_statics` | `static.get` | a type, or its static fields/properties | those of `static.get` |
| `find_instances` | `obj.find`, then `obj.inspect` of the first 3 | a type | those of `obj.find` |
| `read_members` | `obj.snapshot` over the instances found by type (or the type's statics, for static members) | fields/properties of one type | `targets`, `paths` to override; `limit` (instances, default 20) |
| `query` | `obj.query` | — | those of `obj.query` |
| `content` | `content.list`, or `addressables.keys` with `source: "addressables"` | — | those of the method |
| `watch` | `watch.add` → `watch.changes` → `watch.remove` | a static field/property (or give `target`/`path`) | those of `watch.add`; `windowMs` (default 1000) |
| `hook_verify` | `hook.verify` | a method | those of `hook.verify` |
| `trace` | `trace.start` → `trace.stop` | methods (or give `methods`/`include`) | those of `trace.start`; `windowMs` (default 1000); needs `outDir` |
| `call` | `obj.invoke` | a method | `target` (for instance methods), `args`; needs `consented: true` and Full mode |

A `call` probe runs game code, so it needs the user's approval (`consented: true`) as well as Full mode: `probe.run`
refuses it otherwise; in a batch it becomes that probe's error.

## Results

`{id, status, result, evidence: [{locator, excerpt}], metrics: {durationMs, frames}, error?}`

| Status | When |
|---|---|
| `ok` | it answered |
| `partial` | it answered, but some of it couldn't be read (a member that threw, an instance gone meanwhile), a verified hook didn't fire, or a trace saw nothing |
| `failed` | the underlying method failed (`error` says why), or the probe wasn't run in a batch |
| `needs_trigger` | it needs gameplay and nothing happened yet: it stays armed |
| `stale` | an anchor's module isn't loaded: the game changed since the anchor was made |

**Evidence** lists the anchors' `code://` locators, every locator in the result (instances, statics, assets, hits), a
trace's called methods (with their counts), and `trace://` / `hit://` records. Excerpts are at most 300 characters.
`code://` and `live://` locators resolve with `locator.resolve` (an asset's `live://asset/…#<instanceId>` only while the
game runs: instance ids change between runs).

## Probes that wait for gameplay

With `requiresGameplay: true`, a `watch`, `hook_verify` or `trace` probe that sees nothing during its window answers
`needs_trigger` and stays armed: its watch, hook or trace keeps running. Ask the player to do the action (or do it
yourself), then call `probe.result {probeId}`: it answers `needs_trigger` until something happened, and the full
result after. `limits.timeoutMs` (default 30 s, at most 10 minutes) bounds the wait; when it passes, the probe's last
look is its result. `probe.cancel {probeId}` disarms it early. `probe.result` also returns the result of any recently
finished probe (the last 500).

## Batches

`probe.runBatch {probes, stopOnError?, budgetMs?}` runs probes in order as a job (`job.wait` gives
`{results: [...]}`, in the same order) and sends a `probe.progress` event after each. With `stopOnError`, a probe that
fails or is stale ends the batch; once `budgetMs` is spent, no further probe starts. Probes that didn't run are
reported `failed` with the reason. Probe ids must be unique within a batch.
