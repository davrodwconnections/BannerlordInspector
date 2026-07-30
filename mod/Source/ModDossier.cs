using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

namespace BannerlordInspector
{
    /// <summary>
    /// Everything about one mod, in a single answer.
    ///
    /// The information already exists across the other endpoints, but reconstructing "what is this
    /// mod actually doing?" meant four calls and joining them by hand every time. This does the
    /// join: the assembly, what it patched, which behaviours it registered, which game models it
    /// won or lost, and roughly what its code is made of.
    ///
    /// The most useful line is usually "modelsLost" - a model this mod implements but does not own,
    /// because another mod registered later. That is silent, invisible in game, and the cause of
    /// "I installed it and nothing happened".
    /// </summary>
    public static class ModDossier
    {
        public static object Build(string modName)
        {
            if (string.IsNullOrWhiteSpace(modName)) return new { error = "give a mod or assembly name" };

            Assembly assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => SafeName(a).IndexOf(modName, StringComparison.OrdinalIgnoreCase) >= 0);

            if (assembly == null)
            {
                return new
                {
                    error = "no loaded assembly matches that name",
                    modName,
                    hint = "Use /assemblies to see what is loaded. Remember the assembly name can "
                           + "differ from the module folder name."
                };
            }

            string name = SafeName(assembly);
            Type[] types = TypeExplorer.SafeTypes(assembly);

            return new
            {
                assembly = name,
                version = SafeVersion(assembly),
                location = SafeLocation(assembly),
                types = types.Length,
                harmony = HarmonyFor(name, assembly),
                behaviors = BehaviorsFor(assembly),
                models = ModelsFor(assembly, types),
                notableTypes = Notable(types)
            };
        }

        private static object HarmonyFor(string name, Assembly assembly)
        {
            var patched = new List<string>();
            var owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (MethodBase method in Harmony.GetAllPatchedMethods().Where(m => m != null))
                {
                    Patches info;
                    try { info = Harmony.GetPatchInfo(method); }
                    catch { continue; }
                    if (info == null) continue;

                    bool mine = false;
                    foreach (Patch p in info.Prefixes.Concat(info.Postfixes)
                                 .Concat(info.Transpilers).Concat(info.Finalizers))
                    {
                        Assembly patchAssembly = p?.PatchMethod?.DeclaringType?.Assembly;
                        if (patchAssembly != assembly) continue;

                        mine = true;
                        if (p.owner != null) owners.Add(p.owner);
                    }

                    if (mine) patched.Add(Describe(method));
                }
            }
            catch (Exception ex)
            {
                return new { error = ex.Message };
            }

            return new
            {
                harmonyIds = owners.ToArray(),
                methodsPatched = patched.Count,
                targets = patched.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).Take(60).ToArray(),
                truncated = patched.Count > 60
            };
        }

        private static object BehaviorsFor(Assembly assembly)
        {
            if (Campaign.Current == null) return new { registered = 0, note = "no campaign loaded" };

            var registered = new List<string>();
            try
            {
                foreach (CampaignBehaviorBase behavior in BehaviorsInspector.EnumerateCampaignBehaviors())
                {
                    if (behavior?.GetType().Assembly == assembly) registered.Add(behavior.GetType().FullName);
                }
            }
            catch
            {
                // Leave the list as far as it got.
            }

            // Behaviours the mod defines but that are not in the live registry: declared and never
            // added, which means none of their events will ever fire.
            var declared = TypeExplorer.SafeTypes(assembly)
                .Where(t => t != null && !t.IsAbstract && typeof(CampaignBehaviorBase).IsAssignableFrom(t))
                .Select(t => t.FullName)
                .ToArray();

            string[] notRegistered = declared.Where(d => !registered.Contains(d)).ToArray();

            return new
            {
                registered = registered.Count,
                running = registered.OrderBy(b => b).ToArray(),
                declaredButNotRegistered = notRegistered.OrderBy(b => b).ToArray(),
                note = notRegistered.Length > 0
                    ? "Declared-but-not-registered behaviours never receive events. Sometimes "
                      + "deliberate (conditional registration), sometimes a failed init."
                    : null
            };
        }

        private static object ModelsFor(Assembly assembly, Type[] types)
        {
            if (Campaign.Current?.Models == null) return new { note = "no campaign loaded" };

            var won = new List<string>();
            var lost = new List<object>();

            foreach (PropertyInfo property in Campaign.Current.Models.GetType()
                         .GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0) continue;

                object live;
                try { live = property.GetValue(Campaign.Current.Models); }
                catch { continue; }
                if (live == null) continue;

                Type liveType = live.GetType();

                if (liveType.Assembly == assembly)
                {
                    won.Add(property.Name + " -> " + liveType.FullName);
                    continue;
                }

                // Does this mod implement that model anyway? Then it lost the slot.
                Type mine = types.FirstOrDefault(t =>
                    t != null && !t.IsAbstract && property.PropertyType.IsAssignableFrom(t));

                if (mine != null)
                {
                    lost.Add(new
                    {
                        model = property.Name,
                        yours = mine.FullName,
                        liveInstead = liveType.FullName,
                        winner = liveType.Assembly.GetName().Name
                    });
                }
            }

            return new
            {
                modelsOwned = won.Count,
                owns = won.OrderBy(w => w).ToArray(),
                modelsLost = lost.Count,
                lost = lost.ToArray(),
                note = lost.Count > 0
                    ? "A lost model means this mod implements it but another mod registered later "
                      + "and won the slot. Its version never runs."
                    : null
            };
        }

        /// <summary>The types most likely to be worth exploring next.</summary>
        private static object[] Notable(Type[] types)
        {
            return types
                .Where(t => t?.FullName != null && !t.FullName.Contains("<"))
                .Where(t => t.Name.EndsWith("Manager", StringComparison.Ordinal)
                            || t.Name.EndsWith("Behavior", StringComparison.Ordinal)
                            || t.Name.EndsWith("Behaviour", StringComparison.Ordinal)
                            || t.Name.EndsWith("Model", StringComparison.Ordinal)
                            || t.Name.EndsWith("Settings", StringComparison.Ordinal)
                            || t.Name.EndsWith("Config", StringComparison.Ordinal)
                            || t.Name.EndsWith("Api", StringComparison.Ordinal))
                .Select(t => (object)new { type = t.FullName, kind = Kind(t) })
                .Take(60)
                .ToArray();
        }

        private static string Kind(Type type)
        {
            if (type.Name.EndsWith("Manager", StringComparison.Ordinal)) return "manager - usually holds the mod's state";
            if (type.Name.EndsWith("Model", StringComparison.Ordinal)) return "game model override";
            if (type.Name.EndsWith("Settings", StringComparison.Ordinal)
                || type.Name.EndsWith("Config", StringComparison.Ordinal)) return "settings";
            if (type.Name.EndsWith("Api", StringComparison.Ordinal)) return "public API for other mods";
            return "campaign behaviour";
        }

        private static string Describe(MethodBase method)
        {
            try { return (method.DeclaringType?.FullName ?? "?") + "." + method.Name; }
            catch { return "?"; }
        }

        private static string SafeName(Assembly assembly)
        {
            try { return assembly.GetName().Name ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static string SafeVersion(Assembly assembly)
        {
            try { return assembly.GetName().Version?.ToString(); }
            catch { return null; }
        }

        private static string SafeLocation(Assembly assembly)
        {
            try { return assembly.IsDynamic ? "(dynamic)" : assembly.Location; }
            catch { return null; }
        }
    }
}
