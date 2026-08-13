# AI context

Read before changing anything. This module runs inside a live game process and reads a live save.

## Purpose

Answer questions about a **running** modded Bannerlord campaign that cannot be answered from the
DLLs on disk: which model actually won, whether a mod is doing anything, what a conversion's XML
really produced, and what a mod threw and swallowed.

## The rule above all others

**Never touch a campaign object from a background thread.** Bannerlord's campaign objects are not
thread-safe; reading `Campaign.Current` off an HTTP thread corrupts a save or crashes the process.

Every request enqueues work into `MainThreadDispatcher` and blocks until the game's own
`OnApplicationTick` runs it. **The game never waits on the network; the network waits on the game.**

Any new route goes through the dispatcher. There is no exception, no fast path, and no
"this one is only a quick read".

## The other rules

1. **Read-only means read-only.** `GET` and nothing else; every other verb is 405. The path
   evaluator reads properties and fields and never invokes. Adding writes would be a separate
   decision with real risk to a save, and would go behind an explicit switch.
2. **`/call` stays fenced.** Name must *start* with a question verb; no **whole word** may be a
   mutating verb — whole words, not substrings, because substring matching rejected
   `GetSettlement` for containing "Set" and that was a real bug; ≤3 simple parameters, no
   `ref`/`out`, no generics; every call logged; `allowQueryMethods=false` disables it entirely.
3. **Bound every cost explicitly.** This code runs inside the game's frame budget. The dispatcher
   caps at 8 items per frame, the error collector formats a stack once per distinct exception with
   a 200/second ceiling and a 200-group cap, the watcher allows 8 watches × 240 samples at ≥0.5 s.
   A new observer without a ceiling does not belong here.
4. **Observe, never handle.** `ErrorCollector` sits in the throw path and must leave the game
   behaving exactly as it would without the module.
5. **Confine every path.** `modlog` resolves and rejects anything escaping the `Modules` root.
   Loopback-only is not a licence to read the disk.
6. **A route that cannot run reports "could not run", never `FAIL`.** A closed game must not look
   like a broken mod.

## Facts that look like defects but are not

| Thing | Why |
|---|---|
| **Requests block, sometimes for seconds** | They are waiting for the game's tick. That is the safety mechanism, not latency to optimise away |
| **`StoryMode` is not a dependency** | It is absent in a total conversion, and enabling it would drag the vanilla main quest into a Middle-earth campaign. It was only ever matched as a string when classifying assemblies |
| **Harmony is an optional, load-before dependency** | In a TAOM install `Bannerlord.Harmony` is an **empty stub** and the real `0Harmony.dll` lives in `TAOM.Dependencies`. Declaring it hard would break the conversion install |
| **`collectErrors` defaults to on** | A recorder you have to remember to switch on is off exactly when the interesting failure happens |
| **The checklist format cannot compute** | Deliberate. Checks that can compute can be wrong in ways that need debugging, and a harness nobody trusts is worse than none |
| **Content audits read the registry, not the XML** | A malformed XML entry is dropped silently at load. What is on disk is not what runs |
| **The build prints which module supplied Harmony** | Because it probes three locations, and knowing which one it picked is the difference between a five-minute and a five-hour diagnosis |

## Findings this tool produced that are worth not re-deriving

- The diplomacy model in this install is **`Fourberie.FModelDiplo`**, not Banner Kings — the
  opposite of what static analysis suggested. This is the tool's founding result.
- **AI Influence is ConfuserEx-packed** and cannot be read from disk by
  `System.Reflection.Metadata` or Mono.Cecil, yet enumerates perfectly at runtime.
- **A landless culture crashes the game on a daily tick**, hours into a campaign, with nothing in
  the stack pointing at the cause — vanilla's lord-spawn path takes the first settlement of a
  culture without checking there is one. `bannerlord_world` detects it for free.
- **`modelsLost`** — a model a mod implements but does not own because someone registered later —
  is the explanation behind most "I installed it and nothing happened".

## Critical dependencies

- **The running game.** Most tools need a campaign loaded. Reference assemblies come from the game
  folder at build time; the build **never writes there**.
- **Whichever module ships `0Harmony.dll`** — probed, not assumed.
- **Node.js** for the bridge, with a single dependency.
- Hard module dependencies: `Native`, `SandBoxCore`, `Sandbox`.

## Constraints

- **Load order matters:** the module must be **last**, after whatever you intend to inspect.
- **Close the launcher before building with `-p:Deploy=true`** — it holds the DLL open.
- Results are a snapshot from one tick; two calls are two moments.
- Property getters can compute, so reading is not always literally free.
- No writes, anywhere.

## Instructions for future AI work here

1. **Use the tools before reading the source.** That is what they are for: `bannerlord_doctor`
   first when something is wrong and you do not know where, then the specific tool.
2. **When adding a route:** dispatcher, explicit cost bound, `GET` only, and a line in the README's
   tool table. Then run `node mcp\smoketest.js` (bridge, no game needed) and
   `node mcp\server.js --selftest` (game side, campaign loaded).
3. **Do not add a tool count to documentation as a hard number** unless you have just counted it —
   the README's "32 tools" is already wrong (there are 39). `smoketest.js` deliberately avoids
   asserting an exact count for the same reason: it is a number somebody has to remember to edit,
   and therefore a test that fails silently.
4. **Never suggest a write, a fix-in-place, or an action tool** without flagging it as a change in
   the project's fundamental guarantee.
5. Report what was verified against a *running* game versus what was only compiled. The whole
   project exists because the two differ.
