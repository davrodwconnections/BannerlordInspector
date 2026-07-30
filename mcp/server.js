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
];

const ROUTES = {
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
