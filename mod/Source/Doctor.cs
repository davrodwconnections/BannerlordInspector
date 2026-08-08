using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.MountAndBlade;

namespace BannerlordInspector
{
    /// <summary>
    /// One call that sweeps the install and reports what is actually worth looking at.
    ///
    /// The first version of this returned 150 findings on this machine, of which roughly ten
    /// mattered. That is worse than useless: a diagnostic that hands back a haystack has moved the
    /// work rather than done it. Every rule below exists because the previous one was too loose, and
    /// the notes say what it used to get wrong.
    ///
    /// What it checks:
    ///
    ///   FAITHLESS HEROES   Heroes with no Banner Kings religion. Its religion selector throws a
    ///                      NullReferenceException on a hero that has none, which is a hard crash on
    ///                      the clan screen. Counted exactly, not estimated.
    ///   DUELLING PREFIXES  TWO OR MORE prefixes on one method that can each return false. They can
    ///                      cut each other off, and the loser is decided by Harmony priority.
    ///                      A *single* skipping prefix is just a mod overriding vanilla - normal, and
    ///                      previously reported as "high" 49 times, which buried the real ones.
    ///   SHADOWED MODEL     Several mods implement one game model; only the last registered runs.
    ///                      Now names the winner and the losers, because "3 mods also implement this"
    ///                      without saying who won answers nothing.
    ///   INERT MOD          A mod that patched nothing and registered no behaviour. Now only counts
    ///                      assemblies that actually declare an MBSubModuleBase - i.e. real mod entry
    ///                      points. The old name-based guess flagged Mono.Cecil, Serilog, YamlDotNet,
    ///                      every SandBox UI satellite, and the inspector itself: 48 findings, all
    ///                      noise.
    ///
    /// Findings are capped per kind. This is a shortlist that points at the detailed tools, not a
    /// dump of everything the install contains.
    /// </summary>
    public static class Doctor
    {
        /// <summary>Per-kind cap. Beyond this the count is reported and the rows are not.</summary>
        private const int PerKindCap = 12;

        public static object Diagnose()
        {
            var findings = new List<Finding>();
            Scan scan = null;

            try { scan = WalkModAssembliesOnce(); }
            catch (Exception ex) { findings.Add(Error("assembly scan failed", ex)); }

            try { CheckFaithlessHeroes(findings); }
            catch (Exception ex) { findings.Add(Error("faith coverage check failed", ex)); }

            try { CheckDuellingPrefixes(findings); }
            catch (Exception ex) { findings.Add(Error("harmony check failed", ex)); }

            if (scan != null)
            {
                try { CheckShadowedModels(findings, scan); }
                catch (Exception ex) { findings.Add(Error("model check failed", ex)); }

                try { CheckInertMods(findings, scan); }
                catch (Exception ex) { findings.Add(Error("inert-mod check failed", ex)); }
            }

            // Cap per kind so one noisy category cannot drown the rest.
            var kept = new List<Finding>();
            var omitted = new Dictionary<string, int>();

            foreach (var group in findings.GroupBy(f => f.kind))
            {
                var ordered = group.OrderBy(f => Rank(f.severity))
                    .ThenBy(f => f.title, StringComparer.OrdinalIgnoreCase).ToList();

                kept.AddRange(ordered.Take(PerKindCap));
                if (ordered.Count > PerKindCap) omitted[group.Key] = ordered.Count - PerKindCap;
            }

            var results = kept.OrderBy(f => Rank(f.severity))
                .ThenBy(f => f.title, StringComparer.OrdinalIgnoreCase).ToArray();

            return new
            {
                campaignLoaded = Campaign.Current != null,
                totalFindings = findings.Count,
                shown = results.Length,
                bySeverity = findings.GroupBy(f => f.severity)
                    .Select(g => new { severity = g.Key, count = g.Count() })
                    .OrderBy(g => Rank(g.severity)).ToArray(),
                omittedByKind = omitted.Select(kv => new { kind = kv.Key, omitted = kv.Value }).ToArray(),
                note = "A shortlist, not a verdict - some of these are deliberate. For the full lists "
                       + "use /conflicts, /patches or /mod.",
                results
            };
        }

        // ------------------------------------------------------------------ one shared scan

        private sealed class Scan
        {
            /// <summary>Assemblies that declare an MBSubModuleBase - i.e. are really mods.</summary>
            public readonly HashSet<string> ModAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>Model base type -> "assembly|TypeFullName" of everything implementing it.</summary>
            public readonly Dictionary<Type, HashSet<string>> ImplementorsByBase =
                new Dictionary<Type, HashSet<string>>();
        }

        /// <summary>
        /// Walks every non-engine assembly exactly once, collecting what both checks need.
        ///
        /// The shape matters: doing this per model - re-scanning all assemblies for each of ~124
        /// models - is millions of assignability tests on the main thread, which freezes the game
        /// visibly. Walking each type's base chain once keeps it to a few thousand dictionary hits.
        /// </summary>
        private static Scan WalkModAssembliesOnce()
        {
            var scan = new Scan();

            var modelBaseTypes = new HashSet<Type>();
            if (Campaign.Current?.Models != null)
            {
                foreach (PropertyInfo property in Campaign.Current.Models.GetType()
                             .GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (property.GetIndexParameters().Length == 0) modelBaseTypes.Add(property.PropertyType);
                }
            }

            Type subModuleBase = typeof(MBSubModuleBase);

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name;
                try
                {
                    if (assembly.IsDynamic) continue;
                    name = assembly.GetName().Name;
                }
                catch { continue; }

                if (IsEngineOrLibrary(name)) continue;

                foreach (Type type in TypeExplorer.SafeTypes(assembly))
                {
                    if (type == null || type.IsInterface) continue;

                    // Is this assembly a real mod? Only a mod declares a submodule entry point.
                    if (!type.IsAbstract && subModuleBase.IsAssignableFrom(type)) scan.ModAssemblies.Add(name);

                    if (type.IsAbstract || modelBaseTypes.Count == 0) continue;

                    for (Type baseType = type.BaseType; baseType != null; baseType = baseType.BaseType)
                    {
                        if (!modelBaseTypes.Contains(baseType)) continue;

                        if (!scan.ImplementorsByBase.TryGetValue(baseType, out HashSet<string> set))
                        {
                            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            scan.ImplementorsByBase[baseType] = set;
                        }
                        set.Add(name + "|" + type.FullName);
                        break;
                    }
                }
            }

            return scan;
        }

        // ------------------------------------------------------------------ checks

        /// <summary>
        /// Heroes with no Banner Kings faith.
        ///
        /// This is the check that came out of a real crash: BK's religion selector calls
        /// SelectorVM.set_SelectedIndex for the hero's faith, and a hero who has none produces a
        /// NullReferenceException that takes the game down when the clan screen's religion tab opens.
        ///
        /// Counted by walking the cache's keys and testing living heroes against it, rather than
        /// subtracting two totals - the cache also holds the dead, so subtraction gives a number that
        /// looks precise and is wrong.
        /// </summary>
        private static void CheckFaithlessHeroes(List<Finding> findings)
        {
            Type configType = AccessTools.TypeByName("BannerKings.BannerKingsConfig");
            if (configType == null) return;   // Banner Kings not installed; nothing to check.

            object instance = AccessTools.Property(configType, "Instance")?.GetValue(null);
            object manager = instance == null ? null : AccessTools.Property(instance.GetType(), "ReligionsManager")?.GetValue(instance);
            if (manager == null) return;

            if (!(AccessTools.Property(manager.GetType(), "HeroesCache")?.GetValue(manager) is IDictionary cache)) return;

            var withFaith = new HashSet<Hero>();
            foreach (DictionaryEntry entry in cache)
            {
                if (entry.Key is Hero hero) withFaith.Add(hero);
            }

            var faithless = new List<Hero>();
            foreach (Hero hero in Hero.AllAliveHeroes ?? Enumerable.Empty<Hero>())
            {
                if (hero != null && !withFaith.Contains(hero)) faithless.Add(hero);
            }

            if (faithless.Count == 0) return;

            bool playerAffected = Hero.MainHero != null && !withFaith.Contains(Hero.MainHero);

            string sample = string.Join(", ", faithless.Take(8)
                .Select(h => (h.Name?.ToString() ?? h.StringId)));

            findings.Add(new Finding
            {
                severity = "high",
                kind = "faithless-heroes",
                title = $"{faithless.Count} living heroes have no Banner Kings religion"
                        + (playerAffected ? " - INCLUDING YOU" : string.Empty),
                detail = $"Banner Kings has a faith for {withFaith.Count} heroes, but {faithless.Count} "
                         + "living heroes are missing from it. Its religion selector throws on a hero "
                         + "with no faith, which is a hard crash when the clan screen's religion tab "
                         + "opens on one of them. Examples: " + sample + ".",
                whatToDo = playerAffected
                    ? "Your own hero has no faith - opening the religion tab is likely to crash. Seed "
                      + "faiths before using that screen."
                    : "Banner Kings only seeds faith at campaign start, so heroes created later by "
                      + "other mods never get one. Seeding them removes the crash."
            });
        }

        /// <summary>
        /// Two or more prefixes on one method that can each return false.
        ///
        /// Deliberately stricter than before. One skipping prefix is how a mod legitimately replaces
        /// vanilla behaviour, and flagging that produced 49 "high" findings here - so many that the
        /// genuinely dangerous ones were invisible. Two mods that can *each* cut the chain is the
        /// shape where one silently disables the other.
        /// </summary>
        private static void CheckDuellingPrefixes(List<Finding> findings)
        {
            foreach (MethodBase method in Harmony.GetAllPatchedMethods().Where(m => m != null))
            {
                Patches info;
                try { info = Harmony.GetPatchInfo(method); }
                catch { continue; }
                if (info == null) continue;

                Patch[] skipping = info.Prefixes
                    .Where(p => p?.PatchMethod?.ReturnType == typeof(bool))
                    .ToArray();

                // Distinct owners, because one mod with two skipping prefixes is its own business.
                string[] skippingOwners = skipping.Select(p => p.owner)
                    .Where(o => o != null)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (skippingOwners.Length < 2) continue;

                // Harmony runs prefixes by descending priority; the first to return false wins.
                string order = string.Join("  >  ", skipping
                    .OrderByDescending(p => p.priority)
                    .Select(p => $"{p.owner} (priority {p.priority})"));

                findings.Add(new Finding
                {
                    severity = "high",
                    kind = "duelling-prefixes",
                    title = Describe(method) + $": {skippingOwners.Length} mods can each skip this method",
                    detail = "Run order, highest priority first: " + order
                             + ". The first one that returns false stops the original method and every "
                             + "prefix behind it, so the ones later in that list may never run.",
                    whatToDo = "If one of these mods' effect is missing here, it is being cut off by the "
                               + "one ahead of it."
                });
            }
        }

        /// <summary>Several mods implement one model; only the last registered is live.</summary>
        private static void CheckShadowedModels(List<Finding> findings, Scan scan)
        {
            if (Campaign.Current?.Models == null) return;

            foreach (PropertyInfo property in Campaign.Current.Models.GetType()
                         .GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0) continue;

                object live;
                try { live = property.GetValue(Campaign.Current.Models); }
                catch { continue; }
                if (live == null) continue;

                if (!scan.ImplementorsByBase.TryGetValue(property.PropertyType, out HashSet<string> implementors)) continue;

                Type liveType = live.GetType();
                string winner = liveType.Assembly.GetName().Name;

                string[] losers = implementors
                    .Where(entry => entry.Split('|')[1] != liveType.FullName)
                    .Select(entry => entry.Split('|')[0])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(a => !string.Equals(a, winner, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(a => a)
                    .ToArray();

                if (losers.Length == 0) continue;

                findings.Add(new Finding
                {
                    severity = "warning",
                    kind = "shadowed-model",
                    title = $"{property.Name}: {winner} is live, {string.Join(" + ", losers)} shadowed",
                    detail = $"Running: {liveType.FullName}. Also implemented by {string.Join(", ", losers)}, "
                             + "whose versions never run - only the last registration wins. Note that a mod "
                             + "may define a model without ever registering it, in which case this is "
                             + "expected rather than a problem.",
                    whatToDo = "If a feature from one of the shadowed mods is missing, this is why. "
                               + "Load order decides the winner."
                });
            }
        }

        /// <summary>
        /// A real mod that patched nothing and registered no behaviour.
        ///
        /// Now gated on the assembly declaring an MBSubModuleBase, which is what makes something a
        /// mod rather than a library it ships alongside. Severity is 'info' on purpose: plenty of
        /// legitimate mods are pure XML, assets or scenes and touch no code at all.
        /// </summary>
        private static void CheckInertMods(List<Finding> findings, Scan scan)
        {
            var patchingAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (MethodBase method in Harmony.GetAllPatchedMethods().Where(m => m != null))
            {
                Patches info;
                try { info = Harmony.GetPatchInfo(method); }
                catch { continue; }
                if (info == null) continue;

                foreach (Patch p in info.Prefixes.Concat(info.Postfixes)
                             .Concat(info.Transpilers).Concat(info.Finalizers))
                {
                    string assembly = p?.PatchMethod?.DeclaringType?.Assembly?.GetName()?.Name;
                    if (assembly != null) patchingAssemblies.Add(assembly);
                }
            }

            var behaviourAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Campaign.Current != null)
            {
                foreach (CampaignBehaviorBase behavior in BehaviorsInspector.EnumerateCampaignBehaviors())
                {
                    string assembly = behavior?.GetType().Assembly.GetName().Name;
                    if (assembly != null) behaviourAssemblies.Add(assembly);
                }
            }

            foreach (string name in scan.ModAssemblies.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                if (patchingAssemblies.Contains(name) || behaviourAssemblies.Contains(name)) continue;

                // This inspector patches nothing by design; reporting itself is just noise.
                if (string.Equals(name, "BannerlordInspector", StringComparison.OrdinalIgnoreCase)) continue;

                findings.Add(new Finding
                {
                    severity = "info",
                    kind = "inert-mod",
                    title = name + " patched nothing and registered no behaviour",
                    detail = "It has a submodule, so it is a real mod, but it is not touching code. "
                             + "Often correct - data, XML, scene or asset mods work entirely without it.",
                    whatToDo = "Only worth chasing if you expected this mod to change behaviour."
                });
            }
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>
        /// Engine assemblies and the libraries mods ship with. These are not mods, and treating them
        /// as such is what produced most of the old noise.
        /// </summary>
        private static bool IsEngineOrLibrary(string name)
        {
            string[] prefixes =
            {
                "TaleWorlds", "SandBox", "StoryMode", "System", "Microsoft", "mscorlib", "netstandard",
                "Newtonsoft", "0Harmony", "HarmonyLib", "Mono.", "MonoMod", "Serilog", "YamlDotNet",
                "BUTR", "MCM", "Bannerlord.BUTR", "Bannerlord.ModuleManager", "Ionic", "NLog"
            };

            foreach (string prefix in prefixes)
            {
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static int Rank(string severity)
        {
            switch (severity)
            {
                case "high": return 0;
                case "warning": return 1;
                case "info": return 2;
                default: return 3;
            }
        }

        private static Finding Error(string what, Exception ex) => new Finding
        {
            severity = "warning",
            kind = "check-failed",
            title = what,
            detail = ex.Message,
            whatToDo = "See inspector.log."
        };

        private static string Describe(MethodBase method)
        {
            try { return (method.DeclaringType?.Name ?? "?") + "." + method.Name; }
            catch { return "?"; }
        }

        private sealed class Finding
        {
            public string severity { get; set; }
            public string kind { get; set; }
            public string title { get; set; }
            public string detail { get; set; }
            public string whatToDo { get; set; }
        }
    }
}
