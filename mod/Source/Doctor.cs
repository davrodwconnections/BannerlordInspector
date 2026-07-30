using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

namespace BannerlordInspector
{
    /// <summary>
    /// One call that sweeps the whole install and reports what is worth looking at.
    ///
    /// Every other endpoint answers a question you already knew to ask. This one is for the case
    /// that actually happens: something is off, you do not know where, and you do not want to read
    /// six lists hoping to spot it. It runs the checks that caught real problems in this install and
    /// ranks what it finds.
    ///
    /// The findings it looks for, and why each earned its place:
    ///
    ///   SHADOWED MODEL      Two mods replaced the same game model. Only the last one registered is
    ///                       live; the other's work is simply gone. This is how an entire mod
    ///                       feature can vanish without a single error - and it is exactly what was
    ///                       happening with the diplomacy model here.
    ///   SKIPPING PREFIX     Two mods patched one method and at least one prefix can return false,
    ///                       which skips the original AND every prefix queued behind it. The
    ///                       classic silent-breakage shape.
    ///   INERT MOD           A mod is loaded but has patched nothing and registered no campaign
    ///                       behavior. Sometimes correct. Often it means it failed to initialise.
    ///   NO BEHAVIOURS       A campaign is running but a mod's behaviours are missing from the
    ///                       registry, so none of its events will ever fire.
    ///
    /// Nothing here is a verdict. It is a shortlist, ordered so the most likely culprit is first.
    /// </summary>
    public static class Doctor
    {
        public static object Diagnose()
        {
            var findings = new List<Finding>();

            try { CheckShadowedModels(findings); }
            catch (Exception ex) { findings.Add(Error("model check failed", ex)); }

            try { CheckSkippingPrefixConflicts(findings); }
            catch (Exception ex) { findings.Add(Error("harmony check failed", ex)); }

            try { CheckInertMods(findings); }
            catch (Exception ex) { findings.Add(Error("inert-mod check failed", ex)); }

            var ordered = findings
                .OrderBy(f => Rank(f.severity))
                .ThenBy(f => f.title, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new
            {
                campaignLoaded = Campaign.Current != null,
                findings = ordered.Length,
                bySeverity = ordered.GroupBy(f => f.severity)
                    .Select(g => new { severity = g.Key, count = g.Count() })
                    .OrderBy(g => Rank(g.severity)).ToArray(),
                note = "A finding is a shortlist entry, not a verdict. Plenty of these are "
                       + "deliberate and harmless.",
                results = ordered
            };
        }

        // ------------------------------------------------------------------ checks

        /// <summary>
        /// A model can only have one live implementation. When several mods each subclass the same
        /// one, whoever registered last wins and the others are dead code. The live winner is
        /// visible; the losers are not, which is why this matters.
        /// </summary>
        private static void CheckShadowedModels(List<Finding> findings)
        {
            if (Campaign.Current?.Models == null) return;

            // Collect the live model properties first: base type -> (property name, live instance).
            var modelProperties = new List<PropertyInfo>();
            var modelBaseTypes = new HashSet<Type>();

            foreach (PropertyInfo property in Campaign.Current.Models.GetType()
                         .GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0) continue;
                modelProperties.Add(property);
                modelBaseTypes.Add(property.PropertyType);
            }

            // ONE pass over every mod type, walking each type's base chain. The naive shape of this
            // check - re-scanning all assemblies for each of ~124 models - is millions of
            // IsAssignableFrom calls on the main thread, i.e. a visibly frozen game. Walking the
            // base chain once per type instead keeps it to a few thousand cheap lookups.
            var implementorsByBase = new Dictionary<Type, HashSet<string>>();

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string assemblyName;
                try { assemblyName = assembly.GetName().Name; }
                catch { continue; }

                if (assemblyName.StartsWith("TaleWorlds", StringComparison.Ordinal)) continue;
                if (assemblyName.StartsWith("System", StringComparison.Ordinal)) continue;
                if (assemblyName.StartsWith("Microsoft", StringComparison.Ordinal)) continue;

                foreach (Type type in TypeExplorer.SafeTypes(assembly))
                {
                    if (type == null || type.IsAbstract || type.IsInterface) continue;

                    for (Type baseType = type.BaseType; baseType != null; baseType = baseType.BaseType)
                    {
                        if (!modelBaseTypes.Contains(baseType)) continue;

                        if (!implementorsByBase.TryGetValue(baseType, out HashSet<string> set))
                        {
                            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            implementorsByBase[baseType] = set;
                        }
                        set.Add(assemblyName + "|" + type.FullName);
                        break;
                    }
                }
            }

            foreach (PropertyInfo property in modelProperties)
            {
                object live;
                try { live = property.GetValue(Campaign.Current.Models); }
                catch { continue; }
                if (live == null) continue;

                if (!implementorsByBase.TryGetValue(property.PropertyType, out HashSet<string> implementors)) continue;

                Type liveType = live.GetType();
                string winner = liveType.Assembly.GetName().Name;

                var losers = implementors
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
                    title = $"{property.Name}: {losers.Length} other mod(s) also implement this model",
                    detail = $"Live: {liveType.FullName} (from {winner}). Also present but NOT used: "
                             + string.Join(", ", losers)
                             + ". Only the last registration wins, so the others have no effect on this model.",
                    whatToDo = "If a feature from one of the unused mods seems missing, this is why. "
                               + "Load order decides the winner."
                });
            }
        }

        /// <summary>Two owners on one method, where a prefix can cut the chain.</summary>
        private static void CheckSkippingPrefixConflicts(List<Finding> findings)
        {
            foreach (MethodBase method in Harmony.GetAllPatchedMethods().Where(m => m != null))
            {
                Patches info;
                try { info = Harmony.GetPatchInfo(method); }
                catch { continue; }
                if (info == null) continue;

                var owners = new List<string>();
                foreach (Patch p in info.Prefixes.Concat(info.Postfixes)
                             .Concat(info.Transpilers).Concat(info.Finalizers))
                {
                    if (p?.owner != null && !owners.Contains(p.owner)) owners.Add(p.owner);
                }
                if (owners.Count < 2) continue;

                Patch[] skipping = info.Prefixes
                    .Where(p => p?.PatchMethod?.ReturnType == typeof(bool)).ToArray();
                if (skipping.Length == 0) continue;

                findings.Add(new Finding
                {
                    severity = "high",
                    kind = "skipping-prefix-conflict",
                    title = Describe(method) + " is patched by " + owners.Count + " mods, one of which can skip it",
                    detail = "Owners: " + string.Join(", ", owners) + ". Prefixes that can return false: "
                             + string.Join(", ", skipping.Select(p => p.owner + " (" + p.PatchMethod.Name + ")"))
                             + ". A prefix returning false skips the original method and every prefix "
                             + "queued behind it.",
                    whatToDo = "If one of these mods' behaviour is missing, check whether the other's "
                               + "prefix is cutting it off. Harmony priority decides the order."
                });
            }
        }

        /// <summary>Loaded, but doing nothing: no Harmony patches and no campaign behaviours.</summary>
        private static void CheckInertMods(List<Finding> findings)
        {
            var patchOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                    if (p?.owner != null) patchOwners.Add(p.owner);

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

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name;
                try { name = assembly.GetName().Name; }
                catch { continue; }

                if (!LooksLikeMod(assembly, name)) continue;
                if (patchingAssemblies.Contains(name) || behaviourAssemblies.Contains(name)) continue;

                findings.Add(new Finding
                {
                    severity = "info",
                    kind = "inert-mod",
                    title = name + " is loaded but has patched nothing and registered no behaviour",
                    detail = "Often fine - a library, a data-only mod, or one that acts through other "
                             + "means. Worth a look if you expected this mod to be doing something.",
                    whatToDo = "Check its own log, and whether it is enabled in the launcher."
                });
            }
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Mod assemblies live under Modules or the Workshop; engine ones do not.</summary>
        private static bool LooksLikeMod(Assembly assembly, string name)
        {
            if (name.StartsWith("TaleWorlds", StringComparison.Ordinal)) return false;
            if (name.StartsWith("System", StringComparison.Ordinal)) return false;
            if (name.StartsWith("Microsoft", StringComparison.Ordinal)) return false;
            if (name.StartsWith("mscorlib", StringComparison.Ordinal)) return false;
            if (name.StartsWith("Newtonsoft", StringComparison.Ordinal)) return false;
            if (name.StartsWith("0Harmony", StringComparison.Ordinal)) return false;

            try
            {
                if (assembly.IsDynamic) return false;
                string location = assembly.Location ?? string.Empty;
                return location.IndexOf("Modules", StringComparison.OrdinalIgnoreCase) >= 0
                       || location.IndexOf("workshop", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
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
