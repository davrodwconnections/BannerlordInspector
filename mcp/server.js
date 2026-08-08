#!/usr/bin/env node
/**
 * MCP bridge to a running Bannerlord campaign.
 *
 * The game side (the BannerlordInspector module) serves read-only JSON on loopback. This process
 * exposes that as MCP tools. It holds no state and caches nothing: every tool call is a fresh HTTP
 * GET, because the whole point is to see what the game looks like *now*.
 *
 * The game server accepts GET and nothing else, so there is no verb this bridge could use to change
 * a campaign even if it tried. The single exception is bannerlord_call, which invokes
 * question-shaped methods only and is fenced on the game side - see its description.
 */

import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
  CallToolRequestSchema,
  ListToolsRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const PORT = process.env.BANNERLORD_INSPECTOR_PORT || "8420";
const BASE = `http://127.0.0.1:${PORT}`;

/** Sweeps that walk every loaded assembly need longer than a plain read. */
const TIMEOUT_MS = 8000;
const SLOW_TIMEOUT_MS = 25000;

async function ask(path, params = {}, timeoutMs = TIMEOUT_MS) {
  const url = new URL(path, BASE);
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== null && value !== "") {
      url.searchParams.set(key, String(value));
    }
  }

  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);

  try {
    const response = await fetch(url, { signal: controller.signal });
    const text = await response.text();

    let body;
    try {
      body = JSON.parse(text);
    } catch {
      return {
        error: `The game returned something that is not JSON (HTTP ${response.status}).`,
        raw: text.slice(0, 400),
      };
    }

    if (response.status === 504) {
      return {
        error: "The game did not tick in time.",
        likelyCause:
          "It is loading, sitting on a blocking dialog, or minimised. Try again once it is running.",
        detail: body,
      };
    }
    return body;
  } catch (err) {
    if (err.name === "AbortError") {
      return {
        error: `No answer within ${timeoutMs} ms.`,
        likelyCause: "The game is frozen, or this sweep is unusually large.",
      };
    }
    return {
      error: "Could not reach the game.",
      likelyCause:
        "Bannerlord is not running, the BannerlordInspector module is not enabled in the launcher, " +
        `or it is on a different port (expected ${PORT}). Check Modules/BannerlordInspector/logs/inspector.log.`,
      detail: String(err.message || err),
    };
  } finally {
    clearTimeout(timer);
  }
}

const TOOLS = [
  // ---------------------------------------------------------------- state
  {
    name: "bannerlord_status",
    description:
      "Is a campaign loaded, and what is the in-game date and campaign id. Start here - most other " +
      "tools need a loaded campaign. Also confirms the inspector is reachable at all.",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "bannerlord_player",
    description:
      "The player: hero, clan, kingdom, gold, party size, current settlement, map position, and " +
      "captivity state (both the vanilla IsPrisoner flag and PlayerCaptivity.IsCaptive, which mods " +
      "like Consequences of Crime drive separately).",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "bannerlord_heroes",
    description: "Search living heroes by name. Returns id, clan, faction, occupation, prisoner state.",
    inputSchema: {
      type: "object",
      properties: {
        name: { type: "string", description: "Substring of the hero name, case-insensitive." },
        limit: { type: "number", description: "Max results, 1-200. Default 25." },
      },
    },
  },
  {
    name: "bannerlord_settlements",
    description: "Search settlements by name. Returns id, owner clan, faction and type.",
    inputSchema: {
      type: "object",
      properties: {
        name: { type: "string", description: "Substring of the settlement name." },
        limit: { type: "number", description: "Max results, 1-200. Default 25." },
      },
    },
  },

  // ---------------------------------------------------------------- diagnosing the modlist
  {
    name: "bannerlord_doctor",
    description:
      "START HERE WHEN SOMETHING IS WRONG BUT YOU DO NOT KNOW WHERE. One sweep of the whole install, " +
      "ranked by severity:\n" +
      "  - shadowed models: two mods replaced the same game model, so one of them silently does nothing\n" +
      "  - skipping-prefix conflicts: two mods patched a method and one can skip the original\n" +
      "  - inert mods: loaded but patched nothing and registered no behaviour\n\n" +
      "Findings are a shortlist, not a verdict - many are deliberate. Takes a few seconds.",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "bannerlord_conflicts",
    description:
      "Methods patched by more than one mod, riskiest first. 'High' risk means a prefix there can " +
      "return false, which skips the original method AND every prefix behind it - the usual cause of " +
      "one mod silently disabling another.",
    inputSchema: {
      type: "object",
      properties: { limit: { type: "number", description: "Max rows. Default 60." } },
    },
  },
  {
    name: "bannerlord_patches",
    description:
      "The Harmony patch inventory: which methods are patched, by whom, and with what kind of patch. " +
      "Filter by owner (a mod's harmony id) or by target (a type or method name). With no filter, " +
      "returns a capped sample - prefer bannerlord_mod for one mod, or bannerlord_conflicts for trouble.",
    inputSchema: {
      type: "object",
      properties: {
        owner: { type: "string", description: "Harmony id substring, e.g. 'bannerkings'." },
        target: { type: "string", description: "Patched type/method substring, e.g. 'DiplomacyModel'." },
        limit: { type: "number", description: "Max rows. Default 60." },
      },
    },
  },
  {
    name: "bannerlord_mod",
    description:
      "DOSSIER on one mod: its assembly, everything it patched, which campaign behaviours it " +
      "registered (and which it declared but never registered, so they never fire), and which game " +
      "models it owns or LOST to another mod. 'modelsLost' is the interesting one - it means this " +
      "mod's version of a model never runs because someone registered later.",
    inputSchema: {
      type: "object",
      properties: {
        name: { type: "string", description: "Mod or assembly name substring, e.g. 'BannerKings'." },
      },
      required: ["name"],
    },
  },
  {
    name: "bannerlord_behaviors",
    description:
      "Registered campaign behaviours, grouped by mod. A behaviour that a mod defines but that is " +
      "missing from this list never had AddBehavior called on it, so none of its events will ever " +
      "fire - a mod can look loaded and healthy while doing nothing. Pass mission=true instead to " +
      "list mission behaviours (only meaningful during a battle or scene).",
    inputSchema: {
      type: "object",
      properties: {
        filter: { type: "string", description: "Behaviour or assembly name substring." },
        mission: { type: "boolean", description: "List mission behaviours instead of campaign ones." },
      },
    },
  },
  {
    name: "bannerlord_mcm",
    description:
      "Other mods' Mod Configuration Menu settings, read from the live instance rather than from " +
      "disk. Useful because 'why is this mod not doing anything' is often a toggle that is off. Pass " +
      "values=true to include the current value of every setting.",
    inputSchema: {
      type: "object",
      properties: {
        filter: { type: "string", description: "Settings class or assembly substring." },
        values: { type: "boolean", description: "Include current values. Default false." },
      },
    },
  },

  {
    name: "bannerlord_objects",
    description:
      "What mods ADDED, rather than what they do: the game's object registry - troops, items, " +
      "cultures, clans, kingdoms, perks. Counting these from XML on disk is guesswork, because a " +
      "malformed entry is simply absent from the registry with no error anywhere; this is what the " +
      "game actually holds.\n\n" +
      "type accepts an alias (troop, item, culture, clan, kingdom, hero, perk, trait, skill) or a " +
      "full class name. Filter with q= on StringId or name. Pass count=true for just the numbers.\n" +
      "Example: type='troop', q='retinues_custom' answers how many Retinues stubs exist.",
    inputSchema: {
      type: "object",
      properties: {
        type: { type: "string", description: "Alias or full class name. Omit to list the aliases." },
        q: { type: "string", description: "Substring of StringId or name." },
        limit: { type: "number", description: "Max rows. Default 50." },
        count: { type: "boolean", description: "Return counts only, no rows." },
      },
    },
  },

  // ---------------------------------------------------------------- exploring code
  {
    name: "bannerlord_types",
    description:
      "Find types by name across every loaded assembly. This replaces hunting a mod's DLL down on " +
      "disk: the loaded assemblies are the exact build that is running, they include Workshop mods " +
      "outside the Modules folder, and they work even on mods that cannot be read statically at all " +
      "(AI Influence is packed and defeats both Reflection.Metadata and Mono.Cecil on disk, yet " +
      "enumerates fine here). Pass assembly=... alone to list a whole mod's types.",
    inputSchema: {
      type: "object",
      properties: {
        q: { type: "string", description: "Type name substring." },
        assembly: { type: "string", description: "Restrict to one assembly." },
        limit: { type: "number", description: "Max rows. Default 60." },
      },
    },
  },
  {
    name: "bannerlord_members",
    description:
      "The full member surface of a type - properties, fields and methods, including non-public. " +
      "Methods are flagged 'queryable' when bannerlord_call is allowed to invoke them. This is how " +
      "you learn a closed mod's API: find the type, read its members, then read live values with " +
      "bannerlord_eval or ask a question with bannerlord_call.",
    inputSchema: {
      type: "object",
      properties: { type: { type: "string", description: "Full type name." } },
      required: ["type"],
    },
  },
  {
    name: "bannerlord_assemblies",
    description: "Every loaded assembly with its version, type count and file location.",
    inputSchema: {
      type: "object",
      properties: { filter: { type: "string", description: "Assembly name substring." } },
    },
  },

  // ---------------------------------------------------------------- reading live values
  {
    name: "bannerlord_eval",
    description:
      "Read any live value by dotted path. THE WORKHORSE.\n\n" +
      "Roots: Campaign.Current, Hero.MainHero, Clan.PlayerClan, MobileParty.MainParty, " +
      "Settlement.All, Hero.AllAliveHeroes, Kingdom.All, Clan.All, or type:Full.Type.Name for statics.\n\n" +
      "Pseudo-members (this reads; it never invokes a method):\n" +
      "  .$type     the concrete runtime type\n" +
      "  .$members  what can be read from here, for exploring without documentation\n" +
      "  .$count    element count of a collection\n" +
      "  [n]        index into a list\n\n" +
      "Examples:\n" +
      "  Campaign.Current.Models.DiplomacyModel.$type\n" +
      "  type:BannerKings.BannerKingsConfig.Instance.ReligionsManager.$members\n\n" +
      "Non-public members are readable, which matters because most interesting mod state is private.",
    inputSchema: {
      type: "object",
      properties: { path: { type: "string", description: "Dotted path." } },
      required: ["path"],
    },
  },
  {
    name: "bannerlord_call",
    description:
      "Call a QUESTION-SHAPED method and get its answer. Needed because half of what a mod knows is " +
      "behind a getter: 'does this hero have a Banner Kings faith?' lives in " +
      "ReligionsManager.GetHeroReligion(hero), and there is no field to read.\n\n" +
      "Fenced on the game side: the name must start with Get/Is/Has/Can/Find/Count..., must NOT " +
      "contain a mutating verb (Create/Set/Apply/Ensure... - this is what blocks GetOrCreateX), at " +
      "most 3 simple parameters, no ref/out, no generics. Every call is logged. Anything else is " +
      "refused with an explanation.\n\n" +
      "Game objects are passed as their StringId. Separate multiple args with a pipe.\n" +
      "Example: path='type:BannerKings.BannerKingsConfig.Instance.ReligionsManager', " +
      "method='GetHeroReligion', args='main_hero'.\n\n" +
      "Caveat worth keeping in mind: a getter can still compute or cache. 'Question-shaped' means " +
      "'asks a question', not 'provably has no effect'.",
    inputSchema: {
      type: "object",
      properties: {
        path: {
          type: "string",
          description: "Path to the instance, or type:Full.Type.Name for a static method.",
        },
        method: { type: "string", description: "Method name." },
        args: { type: "string", description: "Pipe-separated arguments, e.g. 'main_hero|3'." },
      },
      required: ["method"],
    },
  },

  // ---------------------------------------------------------------- change over time
  {
    name: "bannerlord_watch",
    description:
      "Sample a value repeatedly, to answer questions about CHANGE that a single reading cannot: " +
      "'did this counter move during the battle?', 'did his relation drop when I hit him or when he " +
      "died?'. The game samples on its own tick, so it is as safe as any other read.\n\n" +
      "  action=add    start watching a path (seconds = interval, default 2)\n" +
      "  action=read   read the history, with a 'changes' count per watch\n" +
      "  action=remove stop watching one path\n" +
      "  action=clear  stop watching everything\n\n" +
      "Bounded on purpose: 8 watches, 240 samples each. Add a watch, play, then read it back.",
    inputSchema: {
      type: "object",
      properties: {
        action: { type: "string", enum: ["add", "read", "remove", "clear"] },
        path: { type: "string", description: "Dotted path, same syntax as bannerlord_eval." },
        seconds: { type: "number", description: "Sampling interval for add. Default 2, min 0.5." },
      },
      required: ["action"],
    },
  },

  // ---------------------------------------------------------------- auditing content
  {
    name: "bannerlord_equipment",
    description:
      "THE UNDERWEAR SWEEP. Every registered character whose battle equipment has holes, worst " +
      "first:\n" +
      "  naked      - no body armour, renders in underwear. Visible in every screenshot\n" +
      "  noRoster   - no equipment roster resolved at all, usually a roster id nothing defines\n" +
      "  unarmed    - a non-hero troop with no weapon in any slot\n" +
      "  partial    - missing a cape or gloves; mostly cosmetic, listed last on purpose\n\n" +
      "This is the check every total-conversion test plan opens with, and doing it by eye means " +
      "opening characters one at a time. Filter with culture= to test one faction after editing " +
      "its rosters. Note that in a running game a reference to an item that does not exist looks " +
      "identical to an empty slot - the engine drops bad entries silently at load - so this catches " +
      "both.",
    inputSchema: {
      type: "object",
      properties: {
        culture: { type: "string", description: "Only characters of cultures matching this, e.g. 'gondor'." },
        id: { type: "string", description: "Only characters whose id matches this." },
        heroesOnly: { type: "boolean", description: "Skip regular troops." },
        all: {
          type: "boolean",
          description:
            "Include non-combatants: villagers, townsfolk, notables and tournament templates. They " +
            "are unarmoured and unarmed by design, so they are skipped by default - every culture " +
            "has a villager, and including them means guaranteed findings on a healthy install.",
        },
        limit: { type: "number", description: "Max rows per category. Default 25." },
      },
    },
  },
  {
    name: "bannerlord_world",
    description:
      "DATA-SHAPE PROBLEMS THAT MAKE THE ENGINE THROW, days of game time after the mistake.\n\n" +
      "The headline one: a culture that owns no settlement. Vanilla's lord-spawn code takes the " +
      "first settlement of a culture without checking there is one, so a landless culture crashes " +
      "the game on a daily tick, hours into a campaign, with nothing in the stack pointing at the " +
      "cause. Also finds cultures with no basic troop (breaks recruitment), leaderless clans, " +
      "kingdoms with no clans, and settlements with no owner or no culture.\n\n" +
      "Checks shape, not balance. It cannot tell you a troop is too strong - only that something " +
      "the engine will dereference is missing. Run it after any change to cultures, clans or the map.",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "bannerlord_groupby",
    description:
      "Group any collection by any property, with min/max/average of a numeric one.\n\n" +
      "Built for the questions a test plan asks about a system this tool has never heard of:\n" +
      "  over=heroes by=Race stat=Age        -> is each race's lifespan what it should be?\n" +
      "  over=troops by=Culture.StringId     -> how many troops did each culture actually get?\n" +
      "  over=settlements by=Culture.StringId -> who owns the map\n\n" +
      "Dotted paths work. If the property name is wrong it lists the ones that exist on that type, " +
      "which is usually the fastest way to find what a mod called its field.",
    inputSchema: {
      type: "object",
      properties: {
        over: {
          type: "string",
          description: "heroes (default), troops, settlements, clans, kingdoms, parties.",
        },
        by: { type: "string", description: "Property to group by. Dotted paths allowed: Culture.StringId." },
        stat: { type: "string", description: "Numeric property to summarise per group, e.g. Age." },
        limit: { type: "number", description: "Max groups. Default 40." },
      },
      required: ["by"],
    },
  },
  {
    name: "bannerlord_modlog",
    description:
      "Read the log files other mods write, from inside the game. Call with no arguments to list " +
      "them (newest first - the one written seconds ago is the live one), then with file= to read " +
      "its tail.\n\n" +
      "Worth pairing with bannerlord_errors: that says what was thrown, this says what the mod " +
      "believed it was doing at the time. Separately two puzzles, together usually the answer.\n\n" +
      "Confined to the game's Modules folder, and reads only.",
    inputSchema: {
      type: "object",
      properties: {
        mod: { type: "string", description: "Only list logs of modules matching this." },
        file: { type: "string", description: "Path from the listing. Reads its tail." },
        tail: { type: "number", description: "How many lines. Default 80, max 500." },
        q: { type: "string", description: "Only lines containing this (case-insensitive)." },
      },
    },
  },
  {
    name: "bannerlord_snapshot",
    description:
      "WHAT CHANGED BESIDES THE THING YOU WANTED. Photograph the campaign, then compare later.\n\n" +
      "  action=save name=before     take the photograph (survives a game restart)\n" +
      "  action=compare name=before  what moved since\n" +
      "  action=list / action=drop\n\n" +
      "Records who owns which settlement, which kingdoms are at war, who is alive, and the headline " +
      "counts. The comparison then names settlements that changed hands, wars declared and ended, " +
      "and heroes who died or appeared.\n\n" +
      "The workflow it exists for spans a restart: snapshot, quit, apply the fix, relaunch, compare. " +
      "A fix that works and quietly kills three lords is not a fix, and nothing about playing for ten " +
      "minutes makes that visible.",
    inputSchema: {
      type: "object",
      properties: {
        action: { type: "string", enum: ["save", "compare", "list", "drop"] },
        name: { type: "string", description: "Snapshot name: letters, digits, dash, underscore." },
      },
    },
  },
  {
    name: "bannerlord_checklist",
    description:
      "RUN A WHOLE TEST PLAN AT ONCE and get pass/fail per line.\n\n" +
      "Reads a checklist file (mcp/checks/*.json), calls the inspector once per check, and asserts " +
      "on the answer. Call with no arguments to see which checklists exist.\n\n" +
      "This is what turns a page of manual tester instructions into something you run after every " +
      "build. Each failure names the check, what it expected, and what it actually got, so a failing " +
      "line is a lead rather than a mystery.\n\n" +
      "Checks are plain JSON - route, params, a dotted path into the response, an operator and a " +
      "value - so adding one takes a line and needs no code.",
    inputSchema: {
      type: "object",
      properties: {
        name: { type: "string", description: "Checklist file name without .json. Omit to list them." },
        verbose: { type: "boolean", description: "Include passing checks too, not just failures." },
      },
    },
  },

  // ---------------------------------------------------------------- what went wrong
  {
    name: "bannerlord_errors",
    description:
      "DID ANYTHING GO WRONG? Exceptions the game threw while you were playing, grouped by kind, " +
      "newest first, each blamed on the first non-engine assembly on its stack.\n\n" +
      "This sees more than the game's log does. It records exceptions at the moment they are THROWN, " +
      "so it also catches the ones a mod catches and swallows - the failures whose only symptom is " +
      "'the feature does nothing', with no crash and nothing to grep for.\n\n" +
      "The testing loop this is built for:\n" +
      "  1. action=clear\n" +
      "  2. do the thing in game (load the save, start the battle, recruit the troop)\n" +
      "  3. call again - whatever comes back was caused by what you just did\n\n" +
      "Use since=60 to mean 'only the last minute'. Use blame='TAOM' to see only one mod's throws. " +
      "Use full=true when you need the whole stack rather than the first lines.\n\n" +
      "Not every exception is a bug - the engine throws during normal operation. Read 'blame' and " +
      "'count' first: something thrown once by a mod right after you did the thing is the lead.\n\n" +
      "Does not wait for the game's tick, so it answers while the game is hung or dying.",
    inputSchema: {
      type: "object",
      properties: {
        action: {
          type: "string",
          enum: ["read", "clear", "arm", "disarm"],
          description: "Default read. 'clear' forgets everything so far - do that BEFORE reproducing.",
        },
        since: { type: "number", description: "Only what was last thrown within N seconds." },
        blame: { type: "string", description: "Only groups blamed on an assembly matching this, e.g. 'TAOM'." },
        q: { type: "string", description: "Only groups whose exception type or message contains this." },
        limit: { type: "number", description: "Max groups shown. Default 25." },
        full: { type: "boolean", description: "Full stack traces instead of the first lines." },
      },
    },
  },

  // ---------------------------------------------------------------- when the game is frozen
  {
    name: "bannerlord_hang",
    description:
      "USE THIS WHEN THE GAME IS FROZEN. Unlike every other tool, it does NOT wait for the game's " +
      "tick, so it still answers while the main thread is stuck - which is exactly when everything " +
      "else times out and goes silent.\n\n" +
      "Reports: how long since the last tick, WHAT THE MAIN THREAD WAS DOING when it stopped (a " +
      "breadcrumb trail written while the game was still healthy), where the player was, and " +
      "whether the process is deadlocked (nothing running, blocked on a lock) or spinning (a thread " +
      "burning CPU in a loop). Those two look identical from outside and have opposite causes.\n\n" +
      "Also worth calling when the game feels slow but is NOT hung - it will say so, and the " +
      "breadcrumbs show what took the time.",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "bannerlord_threads",
    description:
      "OS-level thread states for the game process: how many are running vs waiting, which burned " +
      "the most CPU, and an interpretation of whether a stall looks like a deadlock or an infinite " +
      "loop. Read-only, needs no cooperation from the game, safe at any time.",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "bannerlord_breadcrumbs",
    description:
      "The last things the main thread did, newest first, with how long ago. Written while the game " +
      "was healthy, readable while it is not. If the game hung, the newest entry is where it " +
      "stopped. Does not wait for the game.",
    inputSchema: { type: "object", properties: {} },
  },

  // ---------------------------------------------------------------- performance
  {
    name: "bannerlord_perf",
    description:
      "WHY THE GAME IS SLOW, measured rather than guessed.\n\n" +
      "Frame times over a rolling window - average, median, p95, p99 and the worst frames with what " +
      "the game was doing at the time. Read the p95/p99, not the average: 60 FPS average with a 90 ms " +
      "p99 feels far worse than a steady 45.\n\n" +
      "Also breaks down time spent inside the campaign's own tick dispatchers (per-party AI think, " +
      "hourly ticks, daily ticks). 'calls x avgMs' is where the cost really is - a cheap method " +
      "called 5000 times an hour beats an expensive one called twice.\n\n" +
      "Pass reset=true to clear the counters, then play for a minute and ask again - that gives a " +
      "clean window instead of everything since launch.",
    inputSchema: {
      type: "object",
      properties: {
        reset: { type: "boolean", description: "Clear counters and start a fresh measuring window." },
      },
    },
  },
  {
    name: "bannerlord_scale",
    description:
      "How big this world is compared to vanilla: parties, settlements, living heroes, clans, " +
      "kingdoms. Campaign-map cost scales with party count above all else, and the map runs on " +
      "essentially one thread - so a fast many-core CPU does not rescue it. Start here when the " +
      "campaign map is slow but battles are fine.",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "bannerlord_parties",
    description:
      "Breaks the party count down BY KIND - bandits, caravans, villagers, lord parties, garrisons, " +
      "militias, mod-made - and by faction. This is what turns 'too many parties' into a setting you " +
      "can actually change, because each kind is controlled somewhere different.",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "bannerlord_tick_subscribers",
    description:
      "WHO runs inside each campaign tick, and which mod they came from. Phase timing says WHEN the " +
      "campaign is slow (e.g. 'the daily tick costs 604 ms'); this says who is inside that call.\n\n" +
      "Walks the event's real subscriber list, so it reflects what is actually wired up rather than " +
      "what a mod claims. For per-party and per-settlement events, multiply the subscriber count by " +
      "the party or settlement count - that is where cheap handlers become a stall.\n\n" +
      "Reports who runs, not how long each takes: a mod with many subscribers is a suspect, not a " +
      "verdict. Patches nothing, so it cannot disturb tick order.",
    inputSchema: {
      type: "object",
      properties: {
        event: {
          type: "string",
          description: "Filter to one event, e.g. 'DailyTick' or 'HourlyTickParty'. Omit for all.",
        },
      },
    },
  },
  {
    name: "bannerlord_settlement_breakdown",
    description:
      "Settlements by type - towns, castles, villages and HIDEOUTS, with hideouts also broken down " +
      "by faction. Hideouts matter out of proportion to their number: each one keeps respawning " +
      "bandit parties, so a high hideout count becomes a high party count that returns even after " +
      "you clear them.",
    inputSchema: { type: "object", properties: {} },
  },
];

/**
 * Checklists: a test plan as data.
 *
 * A tester checklist is a page of prose that somebody has to work through by hand every release.
 * Most of its lines are assertions about state the inspector can already read - "no character is in
 * underwear", "no culture is landless", "elves live past 1000". Written as data they run in seconds
 * and produce the same answer every time, which is the difference between a test plan and a wish.
 *
 * A check is: call a route, walk a dotted path into the JSON, apply an operator. Deliberately not a
 * scripting language - the moment checks can compute, they can be wrong in ways that need debugging,
 * and a test harness nobody trusts is worse than none.
 */
const CHECKS_DIR = path.join(path.dirname(fileURLToPath(import.meta.url)), "checks");

function dig(value, dotted) {
  if (!dotted) return value;

  let current = value;
  for (const step of String(dotted).split(".")) {
    if (current === null || current === undefined) return undefined;
    current = Array.isArray(current) && /^\d+$/.test(step) ? current[Number(step)] : current[step];
  }
  return current;
}

const OPERATORS = {
  eq: (a, b) => a === b,
  ne: (a, b) => a !== b,
  lt: (a, b) => Number(a) < Number(b),
  lte: (a, b) => Number(a) <= Number(b),
  gt: (a, b) => Number(a) > Number(b),
  gte: (a, b) => Number(a) >= Number(b),
  empty: (a) => a === null || a === undefined || (Array.isArray(a) ? a.length === 0 : a === ""),
  notEmpty: (a) => !(a === null || a === undefined || (Array.isArray(a) ? a.length === 0 : a === "")),
  contains: (a, b) => JSON.stringify(a ?? null).toLowerCase().includes(String(b).toLowerCase()),
  notContains: (a, b) => !JSON.stringify(a ?? null).toLowerCase().includes(String(b).toLowerCase()),
};

async function runChecklist(name, verbose) {
  let available = [];
  try {
    available = fs
      .readdirSync(CHECKS_DIR)
      .filter((f) => f.endsWith(".json"))
      .map((f) => f.replace(/\.json$/, ""));
  } catch {
    return { error: `No checks folder at ${CHECKS_DIR}.` };
  }

  if (!name) {
    return {
      note: "Pass name= to run one. Checklists are plain JSON in mcp/checks/ - adding a check is a line.",
      available,
    };
  }

  if (!available.includes(name)) {
    return { error: `No checklist called '${name}'.`, available };
  }

  let plan;
  try {
    plan = JSON.parse(fs.readFileSync(path.join(CHECKS_DIR, `${name}.json`), "utf8"));
  } catch (err) {
    return { error: `Checklist '${name}' is not readable JSON.`, detail: String(err.message || err) };
  }

  const results = [];
  let passed = 0;
  let failed = 0;
  let errored = 0;

  for (const check of plan.checks || []) {
    const body = await ask(check.route, check.params || {}, SLOW_TIMEOUT_MS);

    // A route that could not be reached is not a failing test - it is an unrun one, and calling it
    // a failure would mean a closed game reports the mod as broken.
    if (body && body.error && check.path !== "error") {
      errored++;
      results.push({
        status: "ERROR",
        what: check.what,
        route: check.route,
        detail: body.error,
      });
      continue;
    }

    const actual = dig(body, check.path);
    const operator = OPERATORS[check.op || "eq"];

    if (!operator) {
      errored++;
      results.push({ status: "ERROR", what: check.what, detail: `unknown operator '${check.op}'` });
      continue;
    }

    const ok = operator(actual, check.value);
    if (ok) passed++;
    else failed++;

    if (!ok || verbose) {
      results.push({
        status: ok ? "PASS" : "FAIL",
        what: check.what,
        route: check.route,
        expected: check.op === "empty" || check.op === "notEmpty"
          ? check.op
          : `${check.path} ${check.op || "eq"} ${JSON.stringify(check.value)}`,
        actual: actual === undefined ? "(path not present in the response)" : actual,
        why: ok ? undefined : check.why,
      });
    }
  }

  return {
    checklist: name,
    description: plan.description,
    summary: `${passed} passed, ${failed} failed, ${errored} could not run`,
    passed,
    failed,
    errored,
    note:
      errored > 0
        ? "Checks that could not run usually mean the game is closed or no campaign is loaded - " +
          "that is not the same as failing."
        : failed === 0
        ? "Everything asserted here holds. It asserts what is in the file and nothing more."
        : "Each failure names what it expected and what it got.",
    results,
  };
}

const ROUTES = {
  bannerlord_hang: () => ask("/hang"),
  bannerlord_threads: () => ask("/threads"),
  bannerlord_breadcrumbs: () => ask("/breadcrumbs"),
  bannerlord_perf: (a) => ask("/perf", { reset: a.reset ? "true" : undefined }),
  bannerlord_scale: () => ask("/scale"),
  bannerlord_parties: () => ask("/parties", {}, SLOW_TIMEOUT_MS),
  bannerlord_settlement_breakdown: () => ask("/settlementbreakdown", {}, SLOW_TIMEOUT_MS),
  bannerlord_tick_subscribers: (a) => ask("/ticksubscribers", { event: a.event }, SLOW_TIMEOUT_MS),
  bannerlord_status: () => ask("/status"),
  bannerlord_player: () => ask("/player"),
  bannerlord_heroes: (a) => ask("/heroes", { name: a.name, limit: a.limit }),
  bannerlord_settlements: (a) => ask("/settlements", { name: a.name, limit: a.limit }),

  bannerlord_doctor: () => ask("/doctor", {}, SLOW_TIMEOUT_MS),
  bannerlord_conflicts: (a) => ask("/conflicts", { limit: a.limit }, SLOW_TIMEOUT_MS),
  bannerlord_patches: (a) =>
    ask("/patches", { owner: a.owner, target: a.target, limit: a.limit }, SLOW_TIMEOUT_MS),
  bannerlord_mod: (a) => ask("/mod", { name: a.name }, SLOW_TIMEOUT_MS),
  bannerlord_behaviors: (a) =>
    a.mission ? ask("/mission") : ask("/behaviors", { filter: a.filter }),
  bannerlord_mcm: (a) => ask("/mcm", { filter: a.filter, values: a.values }, SLOW_TIMEOUT_MS),

  bannerlord_objects: (a) =>
    a.type
      ? ask("/objects", { type: a.type, q: a.q, limit: a.limit, count: a.count }, SLOW_TIMEOUT_MS)
      : ask("/objects/types"),

  bannerlord_types: (a) => ask("/types", { q: a.q, assembly: a.assembly, limit: a.limit }, SLOW_TIMEOUT_MS),
  bannerlord_members: (a) => ask("/members", { type: a.type }),
  bannerlord_assemblies: (a) => ask("/assemblies", { filter: a.filter }),

  bannerlord_eval: (a) => ask("/eval", { path: a.path }),
  bannerlord_call: (a) => ask("/call", { path: a.path, method: a.method, args: a.args }),

  bannerlord_equipment: (a) =>
    ask("/equipment",
      { culture: a.culture, id: a.id, heroesOnly: a.heroesOnly, all: a.all, limit: a.limit },
      SLOW_TIMEOUT_MS),
  bannerlord_world: () => ask("/world", {}, SLOW_TIMEOUT_MS),
  bannerlord_groupby: (a) =>
    ask("/groupby", { over: a.over, by: a.by, stat: a.stat, limit: a.limit }, SLOW_TIMEOUT_MS),
  bannerlord_modlog: (a) => ask("/modlog", { mod: a.mod, file: a.file, tail: a.tail, q: a.q }),
  bannerlord_snapshot: (a) =>
    ask("/snapshot", { action: a.action, name: a.name }, SLOW_TIMEOUT_MS),
  bannerlord_checklist: (a) => runChecklist(a.name, a.verbose),

  bannerlord_errors: (a) =>
    a.action && a.action !== "read"
      ? ask("/errors", { action: a.action })
      : ask("/errors", { since: a.since, blame: a.blame, q: a.q, limit: a.limit, full: a.full }),

  bannerlord_watch: (a) => {
    switch (a.action) {
      case "add":
        return ask("/watch/add", { path: a.path, seconds: a.seconds });
      case "remove":
        return ask("/watch/remove", { path: a.path });
      case "clear":
        return ask("/watch/clear");
      default:
        return ask("/watch", { path: a.path });
    }
  },
};

const server = new Server(
  { name: "bannerlord-inspector", version: "2.0.0" },
  { capabilities: { tools: {} } }
);

server.setRequestHandler(ListToolsRequestSchema, async () => ({ tools: TOOLS }));

server.setRequestHandler(CallToolRequestSchema, async (request) => {
  const { name, arguments: args = {} } = request.params;
  const route = ROUTES[name];

  if (!route) {
    return {
      content: [{ type: "text", text: JSON.stringify({ error: `unknown tool '${name}'` }) }],
      isError: true,
    };
  }

  const result = await route(args);
  return {
    content: [{ type: "text", text: JSON.stringify(result, null, 2) }],
    isError: Boolean(result && result.error),
  };
});

// `node server.js --selftest` checks the game connection without involving MCP at all,
// which makes "is the game side working?" answerable on its own.
if (process.argv.includes("--selftest")) {
  const health = await ask("/health");
  console.log(JSON.stringify(health, null, 2));
  process.exit(health && health.ok ? 0 : 1);
}

await server.connect(new StdioServerTransport());
