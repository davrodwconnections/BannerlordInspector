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
            switch (path.TrimEnd('/').ToLowerInvariant())
            {
                case "":
                case "/health":
                    return Health();

                case "/routes":
                    return Routes();

                case "/status":
                    return OnMainThread(Status);

                case "/player":
                    return OnMainThread(Player);

                case "/models":
                    return OnMainThread(Models);

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

                case "/assemblies":
                    return OnMainThread(() => TypeExplorer.Assemblies(Str(query, "filter")));

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

        private static object OnMainThread(Func<object> work) =>
            MainThreadDispatcher.Run(work, DefaultTimeoutMs);

        /// <summary>
        /// Sweeps that walk every loaded assembly (doctor, patch inventory, type search) legitimately
        /// take longer than a normal read, so they get their own budget instead of tripping the
        /// default timeout and looking like a hung game.
        /// </summary>
        private static object OnMainThread(Func<object> work, int timeoutMs) =>
            MainThreadDispatcher.Run(work, timeoutMs);

        private static string Str(IDictionary<string, string> query, string key) =>
            query.TryGetValue(key, out string value) ? value : null;

        private static bool Flag(IDictionary<string, string> query, string key)
        {
            if (!query.TryGetValue(key, out string raw)) return false;
            if (string.IsNullOrEmpty(raw)) return true;      // bare ?values counts as true
            return !string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase) && raw != "0";
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
            timedOut = MainThreadDispatcher.TimedOut
        };

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

                new { path = "/doctor", what = "SWEEP: shadowed models, prefix conflicts, inert mods - ranked" },
                new { path = "/conflicts", what = "methods patched by more than one mod, riskiest first" },
                new { path = "/patches?owner=&target=", what = "full Harmony patch inventory" },
                new { path = "/patchowners", what = "how many methods each mod has patched" },
                new { path = "/mod?name=", what = "DOSSIER: one mod's patches, behaviours, models won and lost" },
                new { path = "/behaviors?filter=", what = "registered campaign behaviours (a missing one never fires)" },
                new { path = "/mission", what = "mission behaviours, while a battle or scene is running" },
                new { path = "/assemblies?filter=", what = "every loaded assembly" },
                new { path = "/types?q=&assembly=", what = "find types by name across all loaded mods" },
                new { path = "/members?type=", what = "full member surface of a type, including non-public" },
                new { path = "/call?path=&method=&args=a|b", what = "call a question-shaped method (Get/Is/Has...)" },
                new { path = "/mcm?filter=&values=true", what = "other mods' MCM settings with current values" },
                new { path = "/objects?type=troop&q=&count=", what = "what mods ADDED: troops, items, cultures in the registry" },
                new { path = "/objects/types", what = "the type aliases /objects understands" },

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
