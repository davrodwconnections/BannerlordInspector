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

            // A module is tried FIRST, because on a total conversion the name a person types is
            // almost always a module and almost never an assembly - and a module can carry dozens of
            // assemblies that the assembly-name search would silently miss. Only if the module
            // brings more than its own DLL is the module view actually more informative, so a
            // one-assembly module falls through to the plain dossier below.
            string module = ModuleMap.ResolveModule(modName);
            if (module != null)
            {
                List<Assembly> carried = ModuleMap.AssembliesOf(module);
                if (carried.Count > 1) return ModuleDossier(module, carried);
            }

            Assembly assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => SafeName(a).IndexOf(modName, StringComparison.OrdinalIgnoreCase) >= 0);

            if (assembly == null)
            {
                return new
                {
                    error = "no loaded assembly or module matches that name",
                    modName,
                    hint = "Use /assemblies to see what is loaded, or /modules for the module view. "
                           + "Remember the assembly name can differ from the module folder name."
                };
            }

            string name = SafeName(assembly);
            Type[] types = TypeExplorer.SafeTypes(assembly);

            return new
            {
                assembly = name,
                module = ModuleMap.ForAssembly(assembly),
                version = SafeVersion(assembly),
                location = SafeLocation(assembly),
                types = types.Length,
                harmony = HarmonyFor(name, assembly),
                behaviors = BehaviorsFor(assembly),
                models = ModelsFor(assembly, types),
                notableTypes = Notable(types)
            };
        }

        /// <summary>
        /// A module that ships a whole dependency stack, seen as one thing.
        ///
        /// The per-assembly detail is kept deliberately shallow - what it is, what it patched, what
        /// it registered - because the question this answers is "what is in here and which parts are
        /// doing something", not "explain every one of these forty DLLs". Ask for any single one by
        /// name to get the full dossier.
        /// </summary>
        private static object ModuleDossier(string module, List<Assembly> carried)
        {
            var rows = new List<object>();
            int active = 0;

            foreach (Assembly assembly in carried.OrderBy(SafeName, StringComparer.OrdinalIgnoreCase))
            {
                string name = SafeName(assembly);
                Type[] types = TypeExplorer.SafeTypes(assembly);

                int patched = CountFrom(HarmonyFor(name, assembly), "methodsPatched");
                int behaviourCount = CountFrom(BehaviorsFor(assembly), "registered");

                bool doingSomething = patched > 0 || behaviourCount > 0;
                if (doingSomething) active++;

                rows.Add(new
                {
                    assembly = name,
                    version = SafeVersion(assembly),
                    types = types.Length,
                    patchedMethods = patched,
                    behaviors = behaviourCount,
                    active = doingSomething
                });
            }

            return new
            {
                note = "This is a MODULE, and it carries more than one assembly - the pattern total "
                       + "conversions use to bundle their dependencies. Listed below is everything it "
                       + "brought and which parts are actually doing something. Ask for any single "
                       + "assembly by name for its full dossier.",
                module,
                assembliesCarried = carried.Count,
                activeAssemblies = active,
                inertAssemblies = carried.Count - active,
                inertNote = "Inert here is usually correct: a bundled library only runs when the code "
                            + "that uses it asks. It is only worth a look when a library you expect to "
                            + "be working shows nothing at all.",
                assemblies = rows.ToArray()
            };
        }

        /// <summary>
        /// Reads a count back out of the anonymous objects the per-assembly helpers produce.
        ///
        /// Both can also return an error object with no such property - a type that fails to
        /// enumerate is normal for a packed or partially-loaded assembly - and that reads as zero,
        /// which is the honest answer: nothing observed.
        /// </summary>
        private static int CountFrom(object source, string property)
        {
            try
            {
                object value = source?.GetType().GetProperty(property)?.GetValue(source);
                if (value is int count) return count;
                if (value is Array array) return array.Length;
                if (value is System.Collections.ICollection collection) return collection.Count;
            }
            catch
            {
                // A dossier is worth more with an unknown count than not at all.
            }
            return 0;
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
