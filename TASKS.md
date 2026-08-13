# Tasks

Priorities: **P1** wrong or misleading now, **P2** costs time, **P3** tidiness.

## Open

- **[P1] The README undercounts and under-documents the tools.** It says "32 tools";
  `mcp/server.js` declares **39**. Fourteen have no entry in the README's tables at all:
  `battle`, `breadcrumbs`, `crashes`, `events`, `hang`, `modules`, `objects`, `parties`, `perf`,
  `save`, `scale`, `screens`, `settlement_breakdown`, `text`, `tick_subscribers`. Several of them
  are backed by substantial source files (`BattleInspector.cs`, `CrashReports.cs`, `SaveAudit.cs`,
  `PerformanceMonitor.cs`, `ThreadInspector.cs`, `TextAudit.cs`, `TickSubscribers.cs`), so this is
  undocumented capability rather than dead code. Someone reading the README does not know these
  exist.
  Note `config.txt`'s `measureCampaignPhases` already refers to `/perf`, which the tool tables
  never introduce.
- **[P2] No CI.** `smoketest.js` checks the bridge without the game and is the one thing that could
  run automatically on every push; nothing runs it.
- **[P3] The tool count is asserted in prose.** Since it has already drifted once, either derive it
  or drop the number. `smoketest.js` deliberately avoids asserting an exact count for exactly this
  reason.
- **[P3] `.mcp.json` uses a relative path** (`mcp/server.js`) while the README's registration
  example uses an absolute one. Both work in their own context; worth a sentence saying which is
  which.
- **[P3] `mcp/checks/` holds two checklists** (`install.json`, `taom.json`). Adding a check is a
  line of JSON, which is the design's main virtue — but nothing documents the operator vocabulary
  in one place, so writing a new check means reading an existing one.

## Known limits (by design)

- **The game must be running**, with a campaign loaded for most tools.
- **No writes.** Adding actions would be a separate decision with real risk to a save, and would go
  behind an explicit switch.
- **Results are a snapshot from one tick.** Two calls are two moments — `bannerlord_watch` exists
  precisely to work around this.
- **Property getters can compute**, so reading is not always literally free.
- **`/call` is fenced by judgement, not construction.** A getter can still compute or cache;
  "question-shaped" means *asks a question*, not *provably has no effect*. `allowQueryMethods=false`
  makes read-only absolute.
- **Requests block on the game's tick.** That is the safety mechanism, not a performance defect.
- **The module must load last**, after whatever it is meant to inspect.

## Technical debt

- **The bridge is a single 47 KB `server.js`** carrying all 39 tool definitions. It works, but a
  tool is defined in one file and implemented in another repository half, and nothing checks the
  two agree beyond the smoke test's schema validation.
- **37 source files with no test coverage** on the game side. Verification is `--selftest` plus
  using the tools against a live campaign — reasonable given the constraint, but it means a
  regression in a rarely used route surfaces only when someone calls it.
- **The build probes three locations for Harmony.** Correct and necessary, but it means a build can
  succeed against a different HarmonyLib than the game will load; the printed choice is the only
  signal.

## Completed

- Read-only HTTP surface inside the running game, with the main-thread dispatcher that makes it
  safe — the design constraint everything else is shaped around.
- 39 MCP tools across seven families: orientation, failure capture, content audit, mod-conflict
  diagnosis, code exploration, change over time, and performance.
- `bannerlord_errors`, recording exceptions **at the moment they are thrown** — including the ones
  a mod swallows in a `try/catch`, which appear in no log anywhere.
- `bannerlord_doctor`: shadowed models, skipping-prefix conflicts and inert mods in one ranked
  sweep.
- `bannerlord_snapshot`: photograph, restart, compare — the only way to answer "what changed
  besides the thing I was trying to change".
- Support for total-conversion installs (`f6aa256` onward): `StoryMode` dropped as a hard
  dependency, Harmony resolved by probing whichever module actually ships it.
- **The founding result**: proved the live diplomacy model is `Fourberie.FModelDiplo`, not Banner
  Kings.
- `13d0e58` — name the dead in a snapshot comparison.
- `bb11a77` — stop counting tavernkeepers as troops with no weapon.
- `8f92f51` — stop two checks claiming to know things they do not.
- `92bb34a` — stop the battle check calling a tavern visit a bug.
- `73b594c` — stop the inspector reporting itself, and fix what the first real session broke.
- `cd8301f` — answer "my feature never fires" and "can this modlist share a save".
- `dc99663` — make the repository usable by someone who is not its author.
- Standardised project documentation (2026-08-13).
