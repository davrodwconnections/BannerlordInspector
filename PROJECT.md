# Bannerlord Inspector

A **read-only window into a running** Mount & Blade II: Bannerlord campaign, exposed to Claude as
MCP tools.

It exists because static analysis kept running out of road. In a heavily modded install you cannot
tell from the DLLs which diplomacy model actually wins when three mods subclass the same one,
whether a hero really has no Banner Kings faith, or whether a mod that loaded fine is doing
anything at all. Those are runtime facts.

**It has already earned itself once:** it proved the diplomacy model in this install belongs to
*Fourberie*, not Banner Kings — the opposite of what reading the DLLs suggested.

## Status

| | |
|---|---|
| **State** | In active use as the owner's test bench for TAOM |
| **Tools** | **39 declared in `mcp/server.js`** (the README says 32 and documents ~25 — see `TASKS.md`) |
| **Game module** | 37 C# source files under `mod/Source/` |
| **Bridge** | `mcp/server.js`, ~47 KB, one dependency |
| **Tests** | `mcp/smoketest.js` (bridge only) and `--selftest` (game side) |
| **Licence** | MIT |

## The two pieces

| | Where | What |
|---|---|---|
| Game module | `mod/` | Serves JSON on `127.0.0.1:8420` from inside the running game |
| MCP bridge | `mcp/` | Exposes that HTTP surface to Claude as tools |

## How read-only it actually is

- **The server speaks `GET` and nothing else.** Any other verb returns 405. There is no HTTP
  method through which the game could be asked to change.
- **The path evaluator never invokes a method.** Properties and fields only.
- **Loopback only** — bound to `127.0.0.1`.
- **One deliberate exception, `/call`**, fenced by name rules, parameter limits and logging, and
  disableable with `allowQueryMethods=false`.

## Setup

**1. Build and deploy** — close the launcher first, it locks the DLL:

```
dotnet build mod\BannerlordInspector.csproj -c Release -p:Deploy=true
```

Enable **Bannerlord Inspector (read-only)** in the launcher and put it **last** in the load order,
after the mods you intend to inspect.

**2. Install the bridge dependency:**

```
cd mcp && npm install
```

**3. Verify the game side** — with Bannerlord running and a campaign loaded:

```
node mcp\server.js --selftest
```

**4. Register with Claude** (see `.mcp.json`, or point at the absolute path).

Port `8420`; override with `BANNERLORD_INSPECTOR_PORT` and `config.txt` on the game side.

To build without touching the game, omit `-p:Deploy=true` — it stages under `mod\dist\`. Point at a
non-standard install with `BANNERLORD_GAME_DIR`.

## Configuration

`Modules/BannerlordInspector/config.txt`, read at load:

| Key | Default | Meaning |
|---|---|---|
| `enabled` | `true` | Run the server at all |
| `port` | `8420` | Loopback port |
| `allowQueryMethods` | `true` | Permit `/call`; `false` makes read-only absolute |
| `measureCampaignPhases` | `true` | Time the campaign tick dispatchers, so `/perf` can name the phase eating the frame |
| `collectErrors` | `true` | Record thrown exceptions. On by default because a recorder you must remember to switch on is off exactly when the interesting failure happens |

## What the tools cover

Roughly seven families. The README documents the first five in detail.

| Family | Tools |
|---|---|
| **Where you are** | `status`, `player`, `heroes`, `settlements`, `parties`, `objects` |
| **What went wrong** | `errors`, `crashes`, `modlog`, `breadcrumbs`, `hang` |
| **What the modlist produced** | `equipment`, `world`, `groupby`, `text`, `settlement_breakdown` |
| **Which mod is really winning** | `doctor`, `conflicts`, `patches`, `mod`, `behaviors`, `mcm`, `modules` |
| **Code exploration** | `assemblies`, `types`, `members`, `eval`, `call` |
| **Change over time** | `snapshot`, `watch`, `events`, `tick_subscribers` |
| **Performance & runtime** | `perf`, `threads`, `scale`, `battle`, `screens`, `save`, `checklist` |

The sharpest ones, per the README: `bannerlord_doctor` (start here when something is wrong and you
do not know where), `bannerlord_mod`'s **`modelsLost`** — a model a mod implements but does not own
because someone registered later, invisible in game and the reason behind "I installed it and
nothing happened" — and `bannerlord_errors`, which records exceptions **as they are thrown**,
including the ones a mod swallows in a `try/catch` and which therefore appear in no log at all.

## Total-conversion installs

A conversion install is not a vanilla install with mods on top. Two assumptions failed hard against
TAOM:

| Assumption | Reality | Response |
|---|---|---|
| `StoryMode` is present | Not installed, and enabling it would drag the vanilla main quest into a Middle-earth campaign | Dropped as a hard dependency; it was only ever matched as a string when classifying assemblies |
| `Bannerlord.Harmony` ships `0Harmony.dll` | It is an **empty stub**; HarmonyLib comes from `TAOM.Dependencies` | The build probes `Bannerlord.Harmony`, then `TAOM.Dependencies`, then an archived modlist, and prints which it picked |

Remaining hard dependencies: `Native`, `SandBoxCore`, `Sandbox`.

## Technologies

C# / .NET Framework (game module) · a hand-rolled HTTP server and JSON writer, no dependencies ·
HarmonyLib for phase timing · Node.js MCP bridge over stdio · JSON check files.

## Structure

```
BannerlordInspector/
├─ mod/
│  ├─ BannerlordInspector.csproj   Probing build: finds the game and whoever ships Harmony
│  ├─ SubModule.xml
│  └─ Source/                      37 files: HttpServer, Router, MainThreadDispatcher,
│                                  PathEvaluator, QueryInvoker, Doctor, ErrorCollector,
│                                  Snapshot, Watcher, and one inspector per subject
├─ mcp/
│  ├─ server.js                    The bridge: 39 tools over stdio
│  ├─ smoketest.js                 Bridge-only check, no game required
│  └─ checks/                      install.json, taom.json — checklists as data
└─ .mcp.json                       Registration for this repository
```

## Roadmap

None scheduled. **No writes** — adding actions would be a separate decision with real risk to a
save, and would go behind an explicit switch.

## Limits worth knowing

- The game must be running, with a campaign loaded for most tools.
- Property getters can compute; reading is not always literally free.
- Results are a snapshot from one tick. Two calls are two moments — which is what
  `bannerlord_watch` exists to work around.

## Documentation

- [`README.md`](README.md) — the detailed tool-by-tool guide and the reasoning behind each
- [`ARCHITECTURE.md`](ARCHITECTURE.md) — how a request reaches the game safely
- [`AI_CONTEXT.md`](AI_CONTEXT.md) — **read before changing anything**; the threading rule above all
- [`TASKS.md`](TASKS.md) — open work and technical debt
- [`CHANGELOG.md`](CHANGELOG.md) — notable changes

Not affiliated with TaleWorlds Entertainment. This reads a running copy of their game and modifies
nothing.
