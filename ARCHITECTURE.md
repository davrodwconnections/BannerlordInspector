# Architecture

Two processes, one HTTP hop, and one rule that everything else is shaped around.

```
   Claude
     │ MCP over stdio
     ▼
  mcp/server.js ─────── 39 tools, each mapped to a GET route
     │ HTTP GET, 127.0.0.1:8420
     ▼
┌────────────────────────────── inside the running game ──────────────────────────────┐
│  HttpServer            background threads — accept, parse, respond                   │
│      │                                                                               │
│      │  enqueue + block                                                              │
│      ▼                                                                               │
│  MainThreadDispatcher  ◀── drained by OnApplicationTick, max 8 items per frame       │
│      │                                                                               │
│      ▼                                                                               │
│  Router ──▶ PathEvaluator · QueryInvoker · Doctor · Snapshot · Watcher · ~20 more    │
│      │                                                                               │
│      ▼                                                                               │
│  Json ──▶ response ──▶ signalled back to the waiting HTTP thread                     │
└──────────────────────────────────────────────────────────────────────────────────────┘
```

## The threading rule — the part that matters most

**Bannerlord's campaign objects are not thread-safe.** The HTTP server answers on background
threads, and reading `Campaign.Current` from one of those corrupts a save or crashes the process.

So **no request ever touches the game directly.** Every request enqueues work and blocks; the
game's own `OnApplicationTick` runs it on the real main thread and signals back.

The direction of waiting is the whole design: **the game never waits on the network; the network
waits on the game.** Timeout is 5 s (20–25 s for the sweeps), capped at 8 items per frame so a
burst of requests cannot stall a frame.

Any new route must go through `MainThreadDispatcher`. There is no exception to this.

## Read-only by construction, not by intention

Four layers, in decreasing order of how hard they are to subvert:

1. **The verb.** The server answers `GET`; everything else is 405. There is no HTTP method that
   asks the game to change.
2. **The evaluator.** `PathEvaluator` reads properties and fields. It never invokes a method.
3. **The binding.** `127.0.0.1` only.
4. **`/call`, the one exception** — and the only place where read-only rests on judgement.

### Why `/call` exists and how it is fenced

Half of what a mod knows lives behind a getter: `GetHeroReligion(hero)` has no field to read. So
`QueryInvoker` allows question-shaped methods, fenced by:

- the name must **start** with `Get`/`Is`/`Has`/`Can`/`Find`/`Count`/…;
- no **whole word** in the name may be a mutating verb (`Create`, `Set`, `Apply`, `Ensure`, …).
  Whole words specifically — matching substrings rejected `GetSettlement` for containing "Set",
  which was a real bug until it was tested;
- at most 3 simple parameters, game objects passed by `StringId`;
- no `ref`/`out`, no generics;
- **every call is logged**;
- `allowQueryMethods=false` forbids it entirely.

**Honest caveat, stated in the README:** a getter can still compute or cache. "Question-shaped"
means *asks a question*, not *provably has no effect*.

## Two ideas that recur across the tools

**The registry is the truth, not the XML.** A malformed XML entry is dropped silently at load, so
what is on disk is not what runs. That is why the content-audit tools (`equipment`, `world`,
`text`) read the loaded object registry rather than parsing files — a total conversion's failures
are not exceptions, they are **absences**.

**The loaded assemblies are the exact build that is running.** They include Workshop mods outside
`Modules`, and they work on mods that cannot be read statically at all — AI Influence is
ConfuserEx-packed and defeats both `System.Reflection.Metadata` and Mono.Cecil on disk, yet
enumerates perfectly here, because by then the runtime has unpacked it.

## Costs are bounded by construction

Every observer in here runs inside the game's own frame budget, so each has an explicit ceiling
rather than a hope:

| Component | Bound |
|---|---|
| `MainThreadDispatcher` | 8 items per frame |
| `ErrorCollector` | A stack is formatted **once per distinct exception**; 200 examined per second, past which occurrences are only counted; group table capped at 200, oldest evicted |
| `Watcher` | 8 watches, 240 samples each, minimum 0.5 s interval |
| Requests | 5 s timeout, 20–25 s for sweeps |

`ErrorCollector` **observes and never handles**, so the game behaves exactly as it would without
the module.

## The checklist is data, deliberately not code

A check is: call a route, walk a dotted path into the JSON, apply an operator (`eq`, `gt`,
`contains`, `notEmpty`, …). Adding one is a line of JSON.

It is not a scripting language on purpose — *the moment checks can compute, they can be wrong in
ways that need debugging, and a harness nobody trusts is worse than none.*

A route that cannot be reached reports **could not run**, never `FAIL`. A closed game must not look
like a broken mod.

## Path confinement

`bannerlord_modlog` reads other mods' logs, and every path is resolved and rejected if it escapes
the `Modules` root. "Loopback-only" is a reason not to worry about strangers, not a reason to let a
GET read the whole disk.

## The build probes rather than assumes

`mod/BannerlordInspector.csproj` finds a standard Steam install on its own, honours
`BANNERLORD_GAME_DIR`, reads the game folder for reference assemblies and **never writes to it**.

For Harmony it probes `Bannerlord.Harmony`, then `TAOM.Dependencies`, then an archived modlist —
and prints which one it chose. Harmony is required *collectively* but not individually: whichever
module provides it is declared optional and load-before, so **the same build loads in a vanilla
install and in a total conversion**.
