# Bannerlord Inspector

A read-only window into a **running** Mount & Blade II: Bannerlord campaign, exposed to Claude as
MCP tools.

It exists because static analysis kept running out of road. In a heavily modded install you cannot
tell from the DLLs which diplomacy model actually wins when three mods subclass the same one,
whether a given hero really has no Banner Kings faith, or whether a mod that loaded fine is doing
anything at all. Those are runtime facts.

It has already earned itself once: it proved that the diplomacy model in this install belongs to
**Fourberie**, not Banner Kings — the opposite of what reading the DLLs suggested.

## Two pieces

| | Where | What |
|---|---|---|
| Game module | `mod/` | Serves JSON on `127.0.0.1:8420` from inside the game |
| MCP bridge | `mcp/` | Exposes that to Claude as 32 tools |

## Read-only, and how far that goes

- **The server speaks `GET` and nothing else.** Any other verb gets a 405. There is no HTTP method
  through which the game could be asked to change.
- **The path evaluator never invokes a method.** It reads properties and fields only.
- **Loopback only.** Bound to `127.0.0.1`.
- **One deliberate exception: `/call`.** See below — it is fenced, logged, and can be switched off.

## The part that matters most: threading

Bannerlord's campaign objects are **not thread-safe**. The HTTP server answers on background
threads, and reading `Campaign.Current` from one of those corrupts a save or crashes the process.

So no request ever touches the game directly. Every request enqueues work and blocks; the game's own
`OnApplicationTick` runs it on the real main thread and signals back. The game never waits on the
network; the network waits on the game. Timeout is 5 s (20–25 s for the sweeps), capped at 8 items
per frame so a burst cannot stall a frame.

---

## Tools

### Knowing where you are

| Tool | Answers |
|---|---|
| `bannerlord_status` | is a campaign loaded, what date, which campaign id |
| `bannerlord_player` | hero, clan, kingdom, gold, party, position, **captivity state** |
| `bannerlord_heroes` | search living heroes |
| `bannerlord_settlements` | search settlements |

### Catching what went wrong

**`bannerlord_errors`** — the tool for working on a mod rather than merely looking at one.

Every other tool here photographs state. This one records exceptions **at the moment they are
thrown**, which is a strictly larger set than anything a log can show you: a mod that wraps a
campaign tick in `try/catch` and swallows the exception produces no crash, no log line, and a bug
report that reads "the feature does nothing". Those show up here.

The loop it exists for:

```
action=clear          forget everything
                      → load the save / start the battle / recruit the troop
(read)                → whatever comes back was caused by what you just did
```

Identical exceptions collapse into one group with a count, and each group is blamed on the first
non-engine assembly on its stack — so the answer is "TAOM threw this once, just now", not "an
exception happened". Filters: `since=60` for the last minute only, `blame=TAOM` for one mod,
`full=true` for the whole trace.

The cost is bounded by construction, because this handler runs inside the throw path on whatever
thread threw: a stack is formatted **once per distinct exception** and never again, there is a
ceiling of 200 examined per second past which occurrences are only counted, and the group table is
capped at 200 with oldest-evicted. It observes and never handles, so the game behaves exactly as it
would without the module.

Not every exception is a bug — the engine throws during normal operation. Read `blame` and `count`
first.

### Auditing the content a modlist produced

The routes above inspect *code*. These inspect what the code and the XML actually produced, which is
where a total conversion goes wrong: its failures are not exceptions, they are **absences**. The XML
on disk is not what runs — a malformed entry is dropped silently at load — so the registry is the
only truth, and it is trivially readable from here.

| Tool | Answers |
|---|---|
| `bannerlord_equipment` | characters with missing armour or weapons — the "underwear character" sweep |
| `bannerlord_world` | data-shape problems that make the **engine** throw: landless cultures, cultures with no basic troop, leaderless clans, ownerless fiefs |
| `bannerlord_groupby` | group any collection by any property, with min/max/average of a numeric one |

**`bannerlord_world`**'s headline finding is a culture that owns no settlement. Vanilla's lord-spawn
path takes the first settlement of a culture without checking there is one, so a landless culture
crashes the game on a daily tick — hours into a campaign, with nothing in the stack pointing at the
cause. Free to detect, miserable to diagnose.

**`bannerlord_groupby`** is the generic answer to a very specific family of questions. A conversion
adds races, and its test plan then says "elves should be effectively immortal, dwarves die around
250". Verifying that by hand means fast-forwarding for hours:

```
over=heroes by=Race stat=Age
  -> Race=Elf    n=214  Age min 30  max 9412  avg 1180
     Race=Dwarf  n=96   Age min 24  max  247  avg  138
```

Nothing in it knows what a race is — it reads whatever property you name, which is why it works on a
mod this tool has never heard of. Name a property that does not exist and it lists the ones that do,
which is usually the fastest way to find what a mod called its field.

### Reading other mods' logs

**`bannerlord_modlog`** — list the log files under `Modules`, then read the tail of one.

Worth pairing with `bannerlord_errors`: that says what was thrown, this says what the mod believed it
was doing at the time. Separately, two puzzles. Confined to the `Modules` folder — every path is
resolved and rejected if it escapes that root, because "loopback-only" is a reason not to worry about
strangers, not a reason to let a GET read the whole disk.

### Before and after

**`bannerlord_snapshot`** — photograph the campaign, then ask what moved.

```
action=save name=before     → quit, apply the fix, relaunch
action=compare name=before  → settlements that changed hands, wars declared, heroes who died
```

This answers what no other route can: *what changed besides the thing I was trying to change*. A fix
that works and quietly kills three lords is not a fix, and nothing about playing for ten minutes
makes that visible — the world is too big to hold in your head between two sessions. Snapshots go to
disk, because the workflow that matters spans a restart.

### Running a whole test plan

**`bannerlord_checklist`** — a tester checklist as data, with pass/fail per line.

```
mcp/checks/install.json    is this install even sane
mcp/checks/taom.json       assertions from TAOM's own tester checklist
```

A check is: call a route, walk a dotted path into the JSON, apply an operator (`eq`, `gt`,
`contains`, `notEmpty`, …). Adding one is a line of JSON and needs no code. Deliberately not a
scripting language — the moment checks can compute, they can be wrong in ways that need debugging,
and a harness nobody trusts is worse than none.

A route that cannot be reached reports **could not run**, never `FAIL`. A closed game must not look
like a broken mod.

### Diagnosing the modlist

**`bannerlord_doctor`** — start here when something is wrong and you do not know where. One sweep,
ranked by severity:

- **shadowed models** — two mods replaced the same game model; only the last registered is live, so
  the other's work is simply gone. This is how a whole feature vanishes with no error.
- **skipping-prefix conflicts** — two mods patched one method and a prefix there can return `false`,
  which skips the original *and every prefix behind it*. The classic silent breakage.
- **inert mods** — loaded, but patched nothing and registered no behaviour.

| Tool | Answers |
|---|---|
| `bannerlord_conflicts` | methods patched by 2+ mods, riskiest first |
| `bannerlord_patches` | the full Harmony inventory, filterable by owner or target |
| `bannerlord_mod` | **dossier**: one mod's patches, behaviours, and models won *and lost* |
| `bannerlord_behaviors` | registered campaign behaviours (a missing one never fires) |
| `bannerlord_mcm` | other mods' MCM settings with their live values |

`bannerlord_mod`'s **`modelsLost`** is the sharpest line in the whole tool: a model this mod
implements but does not own, because someone registered later. Invisible in game, and the reason
behind "I installed it and nothing happened".

### Exploring code

| Tool | Answers |
|---|---|
| `bannerlord_assemblies` | every loaded assembly, version, location |
| `bannerlord_types` | find types by name across all loaded mods |
| `bannerlord_members` | full member surface of a type, including non-public |

This replaces hunting DLLs on disk. The loaded assemblies are the exact build that is running, they
include Workshop mods outside `Modules`, and they work on mods that **cannot be read statically at
all** — AI Influence is ConfuserEx-packed and defeats both `System.Reflection.Metadata` and
Mono.Cecil on disk, yet enumerates perfectly here, because by then the runtime has unpacked it.

### Reading live values

**`bannerlord_eval`** — the workhorse.

```
Campaign.Current.Models.DiplomacyModel.$type
  -> "Fourberie.FModelDiplo"
```

Roots: `Campaign.Current`, `Hero.MainHero`, `Clan.PlayerClan`, `MobileParty.MainParty`,
`Settlement.All`, `Hero.AllAliveHeroes`, `Kingdom.All`, `Clan.All`, or `type:Full.Type.Name`.

| | |
|---|---|
| `.$type` | concrete runtime type |
| `.$members` | everything readable here — explore without documentation |
| `.$count` | element count |
| `[n]` | index into a list |

`type:` paths resolve greedily, so `type:BannerKings.BannerKingsConfig.Instance` correctly finds the
type `BannerKings.BannerKingsConfig` and then the member `Instance`.

**`bannerlord_call`** — ask a question-shaped method, because half of what a mod knows lives behind
a getter. `GetHeroReligion(hero)` has no field to read.

This is the one place where read-only is enforced by judgement rather than construction, so it is
fenced:

- the name must **start** with `Get`/`Is`/`Has`/`Can`/`Find`/`Count`/…
- no **whole word** in the name may be a mutating verb (`Create`, `Set`, `Apply`, `Ensure`, …).
  Whole words, not substrings — otherwise `GetSettlement` gets rejected for containing "Set", which
  is a real bug this had until it was tested.
- at most 3 simple parameters; game objects are passed by `StringId`
- no `ref`/`out`, no generics
- **every call is logged**
- `allowQueryMethods=false` in `config.txt` forbids it entirely

Honest caveat: a getter can still compute or cache. "Question-shaped" means *asks a question*, not
*provably has no effect*.

### Watching change over time

**`bannerlord_watch`** — every other tool is a photograph. Some questions are not answerable from a
photograph: *did this counter move during the battle? did his relation drop when I hit him, or when
he died?*

```
action=add     start watching a path (seconds = interval, default 2)
action=read    read the history, with a per-watch 'changes' count
action=remove  stop watching one path
action=clear   stop watching everything
```

The game samples on its own tick, so it is as safe as any other read. Bounded on purpose: 8 watches,
240 samples each, minimum 0.5 s interval. It is a diagnostic aid, not telemetry, and must never be
why a frame got slower.

---

## Setup

**1. Build and deploy** (close the launcher first — it locks the DLL):

```
dotnet build mod\BannerlordInspector.csproj -c Release -p:Deploy=true
```

Enable **Bannerlord Inspector (read-only)** in the launcher, and put it **last** in the load order —
after the mods you intend to inspect.

### Total-conversion installs (TAOM and friends)

A conversion install is not a vanilla install with mods on top; two of the things this module used to
take for granted are simply absent, and both were hard failures rather than degraded behaviour:

| Assumption | Reality in a TAOM install | What was done |
|---|---|---|
| `StoryMode` is present | Not installed at all, and enabling it would drag the vanilla main quest into a Middle-earth campaign | Dropped as a hard dependency. It was never used for types — only matched as a string when classifying assemblies |
| `Bannerlord.Harmony` ships `0Harmony.dll` | It is an **empty stub** — no `bin\` at all. HarmonyLib comes from `TAOM.Dependencies` | The build probes `Bannerlord.Harmony`, then `TAOM.Dependencies`, then an archived modlist, and prints which one it picked |

The remaining hard dependencies are `Native`, `SandBoxCore` and `Sandbox` — the three that exist in
every install. Harmony is required collectively but not individually: whichever module provides it is
declared optional and load-before, so the same build loads in a vanilla install and in a converted
one.

**2. Install the bridge's dependency:**

```
cd mcp
npm install
```

**3. Check the game side alone** — with Bannerlord running and a campaign loaded:

```
node mcp\server.js --selftest
```

**4. Register with Claude:**

```json
{
  "mcpServers": {
    "bannerlord": {
      "command": "node",
      "args": ["D:\\Proyectos\\BannerlordInspector\\mcp\\server.js"]
    }
  }
}
```

Port `8420`; override with `BANNERLORD_INSPECTOR_PORT` (and `config.txt` on the game side).

## Checking the bridge without the game

```
node mcp\smoketest.js
```

Handshake plus tool list. It checks that the core tools are present, that no two share a name and
that every schema is well formed - deliberately not an exact count, which is a number somebody has to
remember to edit and therefore a test that fails silently. Separates "the bridge is broken" from
"the game is not running".

## Configuration

`Modules/BannerlordInspector/config.txt`, read at load:

| Key | Default | Meaning |
|---|---|---|
| `enabled` | `true` | run the server at all |
| `port` | `8420` | loopback port |
| `allowQueryMethods` | `true` | permit `/call`; `false` makes read-only absolute |
| `measureCampaignPhases` | `true` | time the campaign tick dispatchers so `/perf` can name the phase eating the frame |
| `collectErrors` | `true` | record thrown exceptions for `/errors`. On by default because a recorder you have to remember to switch on is off exactly when the interesting failure happens |

## Limits worth knowing

- **The game must be running**, with a campaign loaded for most tools.
- **Property getters can compute.** Reading is not always literally free.
- **Results are a snapshot** from one tick. Two calls are two moments — which is exactly what
  `bannerlord_watch` exists to work around.
- **No writes.** Adding actions would be a separate decision with real risk to a save, and would go
  behind an explicit switch.

## Logs

`Modules/BannerlordInspector/logs/inspector.log` — startup, the bound port, every `/call`, and any
failed request.
