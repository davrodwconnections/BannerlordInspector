using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;
using TaleWorlds.ModuleManager;

namespace BannerlordInspector
{
    /// <summary>
    /// Maps URLs to work. Every handler that touches the campaign is wrapped in
    /// <see cref="MainThreadDispatcher"/>; only /health and /routes answer off-thread, because
    /// they read nothing from the game.
    /// </summary>
    public static class Router
    {
        private const int DefaultTimeoutMs = 5000;

        public static object Handle(string path, IDictionary<string, string> query)
        {
            // Remember the route so the breadcrumb the main thread leaves names the actual query,
            // not just "a request". When a hang is attributed to a breadcrumb, this is the detail
            // that makes it actionable.
            _currentRoute = path;

            switch (path.TrimEnd('/').ToLowerInvariant())
            {
                case "":
                case "/health":
                    return Health();

                case "/routes":
                    return Routes();

                // --- these three deliberately DO NOT wait for the game -------------
                // They are the ones that still answer when the main thread is hung, which is
                // exactly when every other route times out and goes silent.
                case "/hang":
                    return Hang();

                case "/threads":
                    return ThreadInspector.Snapshot();

                // Frame timings are recorded on the main thread but READ from plain arrays, so this
                // answers even while the game is stuttering badly - which is when it is asked.
                case "/perf":
                    if (query.ContainsKey("reset"))
                    {
                        PerformanceMonitor.Reset();
                        return new { reset = true, note = "Counters cleared. Play for a while, then ask again." };
                    }
                    return PerformanceMonitor.Report();

                // Off-thread on purpose. Exceptions are recorded from whatever thread threw them
                // into a plain dictionary, so this answers even when the game is hung or dying -
                // which is precisely the moment you want to know what was thrown.
                case "/errors":
                {
                    string action = Str(query, "action");

                    if (string.Equals(action, "clear", StringComparison.OrdinalIgnoreCase))
                    {
                        ErrorCollector.Clear();
                        return new { cleared = true, note = "Now go and do the thing in game, then read /errors again." };
                    }

                    if (string.Equals(action, "arm", StringComparison.OrdinalIgnoreCase))
                    {
                        ErrorCollector.Arm();
                        return new { armed = true };
                    }

                    if (string.Equals(action, "disarm", StringComparison.OrdinalIgnoreCase))
                    {
                        ErrorCollector.Disarm();
                        return new { armed = false };
                    }

                    return ErrorCollector.Report(
                        Str(query, "blame"), Str(query, "q"),
                        Since(query), Limit(query, 25), Flag(query, "full"));
                }

                case "/breadcrumbs":
                    return new
                    {
                        note = "What the main thread was doing, newest first. If the game is hung, "
                               + "the newest entry is where it stopped.",
                        lastPhase = Heartbeat.LastPhase,
                        lastContext = Heartbeat.LastContext,
                        trail = Heartbeat.Breadcrumbs()
                    };

                case "/status":
                    return OnMainThread(Status);

                case "/player":
                    return OnMainThread(Player);

                case "/models":
                    return OnMainThread(Models);

                case "/ticksubscribers":
                    return OnMainThread(() => TickSubscribers.Report(Str(query, "event")), 15000);

                // These read a snapshot the census already built, so they neither iterate nor wait
                // for the tick. That is deliberate: walking 11,000 objects inside a request was
                // producing 700 ms freezes and poisoning the performance numbers.
                case "/scale":
                    return WorldCensus.Scale();

                case "/parties":
                    return WorldCensus.Parties();

                case "/settlementbreakdown":
                    return WorldCensus.Settlements();

                case "/mods":
                    return OnMainThread(Mods);

                case "/heroes":
                    return OnMainThread(() => Heroes(query));

                case "/settlements":
                    return OnMainThread(() => Settlements(query));

                case "/eval":
                    query.TryGetValue("path", out string expr);
                    return OnMainThread(() => PathEvaluator.Evaluate(expr));

                // --- inspecting the mods themselves -------------------------
                case "/doctor":
                    return OnMainThread(Doctor.Diagnose, 20000);

                case "/patches":
                    return OnMainThread(() => HarmonyInspector.AllPatches(
                        Str(query, "owner"), Str(query, "target"), Limit(query, 60)), 15000);

                case "/conflicts":
                    return OnMainThread(() => HarmonyInspector.Conflicts(Limit(query, 60)), 15000);

                case "/patchowners":
                    return OnMainThread(HarmonyInspector.Owners, 15000);

                case "/mod":
                    return OnMainThread(() => ModDossier.Build(Str(query, "name")), 20000);

                case "/behaviors":
                case "/behaviours":
                    return OnMainThread(() => BehaviorsInspector.CampaignBehaviors(Str(query, "filter")));

                case "/mission":
                    return OnMainThread(BehaviorsInspector.MissionBehaviors);

                // The battle itself, rather than the code running it: teams, formations, and where
                // the player actually sits among them.
                case "/battle":
                    return OnMainThread(BattleInspector.Current);

                // What owns the input, and whether it can be clicked at all.
                case "/screens":
                case "/screen":
                    return OnMainThread(ScreenInspector.Current);

                // Who listens to what. "My feature never fires" is usually answered here.
                case "/events":
                    return OnMainThread(() => CampaignEventInspector.Run(
                        Str(query, "q"), Str(query, "mod"),
                        Flag(query, "subscribedonly"), Limit(query, 40)), 25000);

                // Whether this modlist can safely share a save file.
                case "/save":
                    return OnMainThread(SaveAudit.Run, 25000);

                // Names the player would see wrong. Walks every registry, so it gets the long budget.
                case "/text":
                    return OnMainThread(() => TextAudit.Run(Str(query, "q"), Limit(query, 25)), 25000);

                // Other mods' crash bundles. Files, not game state - answers while the game is hung.
                case "/crashes":
                    return string.IsNullOrWhiteSpace(Str(query, "name"))
                        ? CrashReports.List()
                        : CrashReports.Read(Str(query, "name"), Str(query, "entry"),
                                            Str(query, "q"), Limit(query, 120));

                case "/assemblies":
                    return OnMainThread(() => TypeExplorer.Assemblies(Str(query, "filter")));

                // Which module supplied what. On a total conversion one module carries the whole
                // dependency stack, so "assembly" and "mod" stop being the same unit.
                case "/modules":
                    return OnMainThread(Modules);

                case "/types":
                    return OnMainThread(() => TypeExplorer.FindTypes(
                        Str(query, "q"), Str(query, "assembly"), Limit(query, 60)), 15000);

                case "/members":
                    return OnMainThread(() => TypeExplorer.Members(Str(query, "type")));

                case "/call":
                    return OnMainThread(() => QueryInvoker.Call(
                        Str(query, "path"), Str(query, "method"), Str(query, "args")));

                case "/mcm":
                    return OnMainThread(() => McmInspector.List(
                        Str(query, "filter"), Flag(query, "values")), 15000);

                case "/objects":
                    return OnMainThread(() => ObjectBrowser.Browse(
                        Str(query, "type"), Str(query, "q"), Limit(query, 50), Flag(query, "count")), 15000);

                case "/objects/types":
                    return ObjectBrowser.Known();

                // --- auditing the content a modlist produced -----------------
                // Both sweep every registered object, so they get the long budget: they are asked
                // rarely and deliberately, unlike the state routes.
                case "/equipment":
                    return OnMainThread(() => ContentAudit.Equipment(
                        Str(query, "culture"), Str(query, "id"),
                        Flag(query, "heroesonly"), Flag(query, "all"), Limit(query, 25)), 25000);

                case "/world":
                    return OnMainThread(ContentAudit.World, 25000);

                case "/groupby":
                    return OnMainThread(() => GroupBy.Run(
                        Str(query, "over"), Str(query, "by"), Str(query, "stat"), Limit(query, 40)), 25000);

                // --- other mods' logs ---------------------------------------
                // Files, not game state: no dispatcher, and it answers while the game is hung.
                case "/modlog":
                    return string.IsNullOrWhiteSpace(Str(query, "file"))
                        ? ModLogReader.List(Str(query, "mod"))
                        : ModLogReader.Read(Str(query, "file"), Limit(query, 80), Str(query, "q"));

                // --- before and after ---------------------------------------
                case "/snapshot":
                {
                    string action = (Str(query, "action") ?? "list").ToLowerInvariant();
                    string name = Str(query, "name");

                    switch (action)
                    {
                        case "save":
                            return OnMainThread(() => Snapshot.Save(name), 25000);
                        case "compare":
                        case "diff":
                            return OnMainThread(() => Snapshot.Compare(name), 25000);
                        case "drop":
                        case "delete":
                            return Snapshot.Drop(name);
                        default:
                            return Snapshot.List();
                    }
                }

                // --- watching values change ---------------------------------
                case "/watch":
                    return Watcher.Report(Str(query, "path"));

                case "/watch/add":
                    return Watcher.Add(Str(query, "path"), Seconds(query, 2.0));

                case "/watch/remove":
                    return Watcher.Remove(Str(query, "path"));

                case "/watch/clear":
                    return Watcher.Clear();

                default:
                    return new { error = "unknown route", path, see = "/routes" };
            }
        }

        /// <summary>
        /// The route being served on this thread, used to label the breadcrumb the main thread
        /// leaves before running the work. Thread-static because each request has its own thread,
        /// which makes this cheaper and safer than threading a label through twenty call sites.
        /// </summary>
        [ThreadStatic] private static string _currentRoute;

        private static object OnMainThread(Func<object> work) =>
            MainThreadDispatcher.Run(work, DefaultTimeoutMs, _currentRoute);

        /// <summary>
        /// Sweeps that walk every loaded assembly (doctor, patch inventory, type search) legitimately
        /// take longer than a normal read, so they get their own budget instead of tripping the
        /// default timeout and looking like a hung game.
        /// </summary>
        private static object OnMainThread(Func<object> work, int timeoutMs) =>
            MainThreadDispatcher.Run(work, timeoutMs, _currentRoute);

        private static string Str(IDictionary<string, string> query, string key) =>
            query.TryGetValue(key, out string value) ? value : null;

        private static bool Flag(IDictionary<string, string> query, string key)
        {
            if (!query.TryGetValue(key, out string raw)) return false;
            if (string.IsNullOrEmpty(raw)) return true;      // bare ?values counts as true
            return !string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase) && raw != "0";
        }

        /// <summary>
        /// "Only what happened in the last N seconds" - the filter that turns a pile of exceptions
        /// into an answer about the thing you just did. 0 means no time filter.
        /// </summary>
        private static int Since(IDictionary<string, string> query)
        {
            if (query.TryGetValue("since", out string raw) && int.TryParse(raw, out int parsed) && parsed > 0)
            {
                return parsed;
            }
            return 0;
        }

        private static double Seconds(IDictionary<string, string> query, double fallback)
        {
            if (query.TryGetValue("seconds", out string raw)
                && double.TryParse(raw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double parsed))
            {
                return Math.Max(0.5, Math.Min(parsed, 600));
            }
            return fallback;
        }

        // ------------------------------------------------------------------ off-thread

        private static object Health() => new
        {
            ok = true,
            mod = "BannerlordInspector",
            readOnly = true,
            served = MainThreadDispatcher.Served,
            pending = MainThreadDispatcher.Pending,
            timedOut = MainThreadDispatcher.TimedOut,

            // The heartbeat: read without touching the game, so this stays truthful during a hang.
            gameTicking = !Heartbeat.LooksHung,
            msSinceLastTick = Heartbeat.MillisecondsSinceLastTick,
            ticks = Heartbeat.TickCount,

            // Surfaced here so a caller who only ever checks health still finds out that something
            // is throwing. The detail is at /errors.
            errors = ErrorCollector.Summary()
        };

        /// <summary>
        /// The one-call answer for "it froze". Combines the heartbeat, what the main thread was last
        /// doing, and whether the process is deadlocked or spinning - none of which needs the game
        /// to cooperate.
        /// </summary>
        private static object Hang()
        {
            long since = Heartbeat.MillisecondsSinceLastTick;

            string state = Heartbeat.State;

            // Loading blocks the tick for a long time on a heavy modlist, and calling that a freeze
            // makes the whole tool untrustworthy on the day something really does hang. Say which
            // of the two it is instead of reporting silence as failure.
            if (state == "loading" || state == "starting")
            {
                return new
                {
                    hung = false,
                    state,
                    msSinceLastTick = since,
                    ticks = Heartbeat.TickCount,
                    context = Heartbeat.LastContext,
                    note = "No campaign is up and the tick is blocked - this is a loading screen, not "
                           + "a freeze. A worker thread burning CPU here is exactly what loading looks "
                           + "like. Ask again once you are on the map."
                };
            }

            if (state == "healthy")
            {
                return new
                {
                    hung = false,
                    state,
                    msSinceLastTick = since,
                    ticks = Heartbeat.TickCount,
                    context = Heartbeat.LastContext,
                    note = "The game is ticking. If it FELT frozen, it was a long single operation "
                           + "rather than a hang - check /breadcrumbs for what took the time."
                };
            }

            return new
            {
                hung = true,
                state,
                msSinceLastTick = since,
                ticks = Heartbeat.TickCount,
                lastPhase = Heartbeat.LastPhase,
                lastContext = Heartbeat.LastContext,
                threads = ThreadInspector.Snapshot(),
                trail = Heartbeat.Breadcrumbs(),
                whatThisMeans =
                    "The main thread stopped after the newest breadcrumb below. Read 'interpretation' "
                    + "under threads to tell a deadlock (nothing running, waiting on a lock) from an "
                    + "infinite loop (a thread burning CPU)."
            };
        }

        private static object Routes() => new
        {
            routes = new object[]
            {
                new { path = "/health", what = "is the server up (does not touch the game)" },
                new { path = "/status", what = "game state, campaign id, in-game date" },
                new { path = "/player", what = "player hero, clan, kingdom, party, captivity" },
                new { path = "/models", what = "the concrete game model classes actually in use" },
                new { path = "/mods", what = "loaded modules and versions" },
                new { path = "/heroes?name=&limit=", what = "search living heroes by name" },
                new { path = "/settlements?name=&limit=", what = "search settlements" },
                new { path = "/eval?path=", what = "read any live value, e.g. Campaign.Current.Models.DiplomacyModel.$type" },

                new { path = "/errors?since=&blame=&q=&full=", what = "ERRORS: exceptions thrown while playing, grouped and blamed on an assembly - including ones a mod swallowed" },
                new { path = "/errors?action=clear", what = "forget recorded errors, so the next read is caused by what you do next" },

                new { path = "/doctor", what = "SWEEP: shadowed models, prefix conflicts, inert mods - ranked" },
                new { path = "/conflicts", what = "methods patched by more than one mod, riskiest first" },
                new { path = "/patches?owner=&target=", what = "full Harmony patch inventory" },
                new { path = "/patchowners", what = "how many methods each mod has patched" },
                new { path = "/mod?name=", what = "DOSSIER: one mod's patches, behaviours, models won and lost" },
                new { path = "/behaviors?filter=", what = "registered campaign behaviours (a missing one never fires)" },
                new { path = "/mission", what = "mission behaviours, while a battle or scene is running" },
                new { path = "/battle", what = "IN BATTLE: teams, formations, and where the player actually sits" },
                new { path = "/events?mod=&q=", what = "who subscribes to which campaign event - answers 'my feature never fires'" },
                new { path = "/save", what = "save-id collisions between mods, the silent save-corruption cause" },
                new { path = "/text?q=", what = "names the player sees wrong: raw {=keys}, blanks, ids used as names" },
                new { path = "/crashes", what = "list other mods' crash bundles" },
                new { path = "/crashes?name=&entry=&q=", what = "read inside one - rgl_log is where silent failures live" },
                new { path = "/screens", what = "FROZEN UI: what owns the input, whether it has a cursor, and the active campaign menu" },
                new { path = "/assemblies?filter=", what = "every loaded assembly, with the module that supplied it" },
                new { path = "/modules", what = "which module brought which assemblies - the bundled-dependency view" },
                new { path = "/types?q=&assembly=", what = "find types by name across all loaded mods" },
                new { path = "/members?type=", what = "full member surface of a type, including non-public" },
                new { path = "/call?path=&method=&args=a|b", what = "call a question-shaped method (Get/Is/Has...)" },
                new { path = "/mcm?filter=&values=true", what = "other mods' MCM settings with current values" },
                new { path = "/objects?type=troop&q=&count=", what = "what mods ADDED: troops, items, cultures in the registry" },
                new { path = "/objects/types", what = "the type aliases /objects understands" },

                new { path = "/equipment?culture=&id=&heroesOnly=", what = "CONTENT: characters with missing armour or weapons - the 'underwear character' sweep" },
                new { path = "/world", what = "CONTENT: data-shape problems that make the engine throw - landless cultures, leaderless clans, ownerless fiefs" },
                new { path = "/groupby?over=heroes&by=Race&stat=Age", what = "group any collection by any property, with min/max/avg of a numeric one" },
                new { path = "/modlog?mod=", what = "list log files other mods write" },
                new { path = "/modlog?file=&tail=&q=", what = "read the tail of one mod's log, filtered" },
                new { path = "/snapshot?action=save&name=", what = "BEFORE/AFTER: photograph the world (survives a restart)" },
                new { path = "/snapshot?action=compare&name=", what = "what moved since that snapshot - owners, wars, deaths" },

                new { path = "/watch", what = "read sampled history" },
                new { path = "/watch/add?path=&seconds=2", what = "WATCH: sample a value over time to see change" },
                new { path = "/watch/remove?path=", what = "stop watching one path" },
                new { path = "/watch/clear", what = "stop watching everything" }
            }
        };

        // ------------------------------------------------------------------ main thread

        private static object Status()
        {
            Campaign c = Campaign.Current;
            if (c == null) return new { campaignLoaded = false, note = "No campaign is loaded - you are in a menu." };

            return new
            {
                campaignLoaded = true,
                campaignId = c.UniqueGameId,
                date = CampaignTime.Now.ToString(),
                day = (int)CampaignTime.Now.ToDays,
                isMainPartyActive = MobileParty.MainParty != null
            };
        }

        private static object Player()
        {
            Hero h = Hero.MainHero;
            if (h == null) return new { error = "no player hero - no campaign loaded" };

            MobileParty party = MobileParty.MainParty;

            return new
            {
                hero = new { h.StringId, name = h.Name?.ToString(), age = (int)h.Age, gold = h.Gold },
                clan = h.Clan == null ? null : new { h.Clan.StringId, name = h.Clan.Name?.ToString(), tier = h.Clan.Tier, renown = h.Clan.Renown },
                kingdom = h.MapFaction == null ? null : new { id = h.MapFaction.StringId, name = h.MapFaction.Name?.ToString() },
                captivity = new
                {
                    isPrisoner = h.IsPrisoner,
                    playerCaptivityIsCaptive = PlayerCaptivity.IsCaptive,
                    captorParty = PlayerCaptivity.CaptorParty?.Name?.ToString(),
                    captorHero = (PlayerCaptivity.CaptorParty?.Owner ?? PlayerCaptivity.CaptorParty?.LeaderHero)?.Name?.ToString()
                },
                party = party == null ? null : new
                {
                    name = party.Name?.ToString(),
                    members = party.MemberRoster?.TotalManCount ?? 0,
                    prisoners = party.PrisonRoster?.TotalManCount ?? 0,
                    settlement = party.CurrentSettlement?.Name?.ToString()
                },
                position = party == null ? null : new { x = party.GetPosition2D.x, y = party.GetPosition2D.y }
            };
        }

        /// <summary>
        /// Which model classes actually win at runtime. This is the question static analysis cannot
        /// answer in a modded install: several mods subclass the same model and only one instance
        /// is live.
        /// </summary>
        private static object Models()
        {
            Campaign c = Campaign.Current;
            if (c?.Models == null) return new { error = "no campaign models - no campaign loaded" };

            var models = new List<ModelInfo>();

            foreach (var property in c.Models.GetType()
                         .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0) continue;

                object value;
                try { value = property.GetValue(c.Models); }
                catch { continue; }

                if (value == null) continue;

                Type actual = value.GetType();
                string assembly = actual.Assembly.GetName().Name;

                models.Add(new ModelInfo
                {
                    model = property.Name,
                    implementation = actual.FullName,
                    fromAssembly = assembly,
                    isVanilla = assembly.StartsWith("TaleWorlds", StringComparison.Ordinal)
                });
            }

            return new
            {
                count = models.Count,
                overriddenByMods = models.Count(m => !m.isVanilla),
                models = models.OrderBy(m => m.model, StringComparer.OrdinalIgnoreCase).ToArray()
            };
        }

        /// <summary>Concrete rather than anonymous: net48 has no Microsoft.CSharp here, so `dynamic`
        /// is unavailable and sorting an anonymous type needs a real property to read.</summary>
        private sealed class ModelInfo
        {
            public string model { get; set; }
            public string implementation { get; set; }
            public string fromAssembly { get; set; }
            public bool isVanilla { get; set; }
        }

        /// <summary>
        /// Modules by how many assemblies each one actually brought into the process.
        ///
        /// /mods lists what the launcher knows about; this lists what each of those is really
        /// carrying. The difference is the point: a module supplying twenty assemblies is a bundled
        /// dependency stack, and a module supplying none is either a stub folder that exists purely
        /// to satisfy someone else's dependency, or a data-only mod.
        /// </summary>
        private static object Modules()
        {
            var loaded = ModuleMap.Modules();

            var rows = loaded.Select(pair => (object)new
            {
                module = pair.Key,
                assemblies = pair.Value,
                kind = pair.Value >= 5 ? "bundles a dependency stack"
                     : pair.Value > 1 ? "several assemblies"
                     : "single assembly"
            }).ToArray();

            // Folders the launcher lists that put nothing into the process at all.
            var withCode = new HashSet<string>(loaded.Select(p => p.Key), StringComparer.OrdinalIgnoreCase);
            string[] codeless;

            try
            {
                codeless = ModuleHelper.GetModules()
                    .Where(m => m != null && !withCode.Contains(m.Id))
                    .Select(m => m.Id)
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch
            {
                codeless = new string[0];
            }

            return new
            {
                note = "Modules that put assemblies into the running process, most first. Ask /mod "
                       + "with a module name to see what one is carrying and which parts are live.",
                withAssemblies = rows.Length,
                modules = rows,
                codelessModules = codeless,
                codelessNote = "These loaded no assembly. Normal for data, XML, scene and asset mods, "
                               + "and for stub folders that exist only so the launcher can satisfy "
                               + "another mod's dependency on a library that is bundled elsewhere."
            };
        }

        private static object Mods()
        {
            try
            {
                var mods = ModuleHelper.GetModules()
                    .Where(m => m != null)
                    .Select(m => new { id = m.Id, name = m.Name, version = m.Version.ToString(), official = m.IsOfficial })
                    .OrderBy(m => m.id)
                    .ToArray();

                return new { count = mods.Length, modules = mods };
            }
            catch (Exception ex)
            {
                return new { error = "could not enumerate modules: " + ex.Message };
            }
        }

        private static object Heroes(IDictionary<string, string> query)
        {
            query.TryGetValue("name", out string name);
            int limit = Limit(query, 25);

            IEnumerable<Hero> heroes = Hero.AllAliveHeroes ?? Enumerable.Empty<Hero>();

            if (!string.IsNullOrWhiteSpace(name))
            {
                heroes = heroes.Where(h => (h.Name?.ToString() ?? string.Empty)
                    .IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            var list = heroes.Take(limit).Select(h => new
            {
                h.StringId,
                name = h.Name?.ToString(),
                clan = h.Clan?.Name?.ToString(),
                faction = h.MapFaction?.Name?.ToString(),
                occupation = h.Occupation.ToString(),
                isPrisoner = h.IsPrisoner,
                isAlive = h.IsAlive
            }).ToArray();

            return new { count = list.Length, limit, heroes = list };
        }

        private static object Settlements(IDictionary<string, string> query)
        {
            query.TryGetValue("name", out string name);
            int limit = Limit(query, 25);

            IEnumerable<Settlement> all = Settlement.All ?? Enumerable.Empty<Settlement>();

            if (!string.IsNullOrWhiteSpace(name))
            {
                all = all.Where(s => (s.Name?.ToString() ?? string.Empty)
                    .IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            var list = all.Take(limit).Select(s => new
            {
                s.StringId,
                name = s.Name?.ToString(),
                owner = s.OwnerClan?.Name?.ToString(),
                faction = s.MapFaction?.Name?.ToString(),
                type = s.IsTown ? "town" : s.IsCastle ? "castle" : s.IsVillage ? "village" : "other"
            }).ToArray();

            return new { count = list.Length, limit, settlements = list };
        }

        private static int Limit(IDictionary<string, string> query, int fallback)
        {
            if (query.TryGetValue("limit", out string raw) && int.TryParse(raw, out int parsed))
            {
                return Math.Max(1, Math.Min(parsed, 200));
            }
            return fallback;
        }
    }
}
