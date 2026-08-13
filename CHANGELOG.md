# Changelog

Notable changes. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added
- Standardised project documentation: `PROJECT.md`, `ARCHITECTURE.md`, `AI_CONTEXT.md`,
  `TASKS.md` and this changelog. `README.md` was left unchanged. — 2026-08-13

### Documented (no code change)
- **`mcp/server.js` declares 39 tools; the README says 32 and documents about 25.** Fourteen are
  undocumented — `battle`, `breadcrumbs`, `crashes`, `events`, `hang`, `modules`, `objects`,
  `parties`, `perf`, `save`, `scale`, `screens`, `settlement_breakdown`, `text`,
  `tick_subscribers` — several backed by substantial source files, so this is undocumented
  capability rather than dead code.

## 2026-08-13 — `9d04304`

- `CLAUDE.md` added, pointing at the project documentation set.

## 2026-08-10

- **`fe877d5`** — keep a `local/` folder out of the repository.
- **`13d0e58`** — name the dead in a snapshot comparison, so "what changed" says *who*.
- **`bb11a77`** — stop counting tavernkeepers as troops with no weapon. The equipment sweep was
  reporting non-combatants as broken content.

## 2026-08-09

- **`8f92f51`** — stop two checks claiming to know things they do not.
- **`92bb34a`** — stop the battle check calling a tavern visit a bug.
- **`73b594c`** — stop the inspector reporting itself, and fix what the first real session broke.
  The self-report problem is the classic one for a diagnostic: an instrument that records its own
  activity manufactures the symptom it is looking for.
- **`cd8301f`** — answer "my feature never fires" and "can this modlist share a save". The first
  is the `doctor`/`mod` path — shadowed models and skipping prefixes; the second is the save audit.
- **`dc99663`** — make the repository usable by someone who is not its author.
- **`f6aa256`** — see what the player sees, not just what the campaign holds.

### Support for total-conversion installs

Two assumptions failed hard against a TAOM install, both as outright failures rather than degraded
behaviour:

- **`StoryMode` is not present** in a conversion, and enabling it would drag the vanilla main quest
  into a Middle-earth campaign. Dropped as a hard dependency — it had only ever been matched as a
  string when classifying assemblies.
- **`Bannerlord.Harmony` is an empty stub** in such an install, with no `bin\` at all; HarmonyLib
  comes from `TAOM.Dependencies`. The build now probes `Bannerlord.Harmony`, then
  `TAOM.Dependencies`, then an archived modlist, and prints which one it picked. Harmony became a
  collective rather than individual dependency, so one build loads in both a vanilla and a
  converted install.

## Earlier — the foundation

- **Read-only HTTP server inside the running game** on `127.0.0.1:8420`, answering `GET` and
  returning 405 for everything else.
- **`MainThreadDispatcher`** — the design constraint the whole project is shaped around. Bannerlord's
  campaign objects are not thread-safe, so no request ever touches the game directly: it enqueues
  and blocks while the game's own `OnApplicationTick` does the work and signals back. Capped at 8
  items per frame so a burst cannot stall a frame.
- **`PathEvaluator`** with `$type`, `$members`, `$count` and indexing, reading properties and fields
  and never invoking.
- **`QueryInvoker` / `/call`** — the single deliberate exception to read-only, fenced by name rules
  (whole-word mutating-verb rejection, after substring matching was found to reject
  `GetSettlement`), a 3-parameter limit, no `ref`/`out`/generics, mandatory logging, and a config
  switch that disables it entirely.
- **`ErrorCollector`** — records exceptions as they are thrown, groups identical ones with a count,
  and blames the first non-engine assembly on the stack. Bounded: one stack format per distinct
  exception, 200 examined per second, 200 groups with oldest evicted. Observes and never handles.
- **`Doctor`**, `conflicts`, `patches`, `mod` (with `modelsLost`), `behaviors`, `mcm`.
- **Content audits** reading the loaded registry rather than the XML, because a malformed entry is
  dropped silently at load and a conversion's failures are absences, not exceptions.
- **`Snapshot`** and **`Watcher`**, for questions a single photograph cannot answer.
- **`checklist`** — checks as data, deliberately not a scripting language, reporting "could not
  run" rather than `FAIL` when a route is unreachable.
- **The founding result**: proved that the live diplomacy model is `Fourberie.FModelDiplo`, not
  Banner Kings — the opposite of what reading the DLLs suggested.
