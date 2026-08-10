using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace BannerlordInspector
{
    /// <summary>
    /// Whether this modlist can safely share a save file.
    ///
    /// Mods that persist their own data declare a SaveableTypeDefiner carrying a numeric base id,
    /// and every type and member they save is numbered relative to it. The engine does not police
    /// those ids. Two mods that pick overlapping ranges - and a great many pick round numbers like
    /// 1000 or 12345 - write different objects under the same key, and the save silently becomes
    /// something neither of them can read back correctly.
    ///
    /// The failure arrives later and somewhere else: a campaign that loads with a mod's data
    /// scrambled or missing, or a load that dies with a type-resolution error naming a mod that did
    /// nothing wrong. Nobody suspects the numbering, because nothing in the game ever mentions it.
    ///
    /// This is cheap to check and checked by nothing else, which is a good reason for it to exist.
    /// A collision here is not proof of corruption - ranges can overlap without both mods using the
    /// contested numbers - but it is the first thing worth ruling out when saves misbehave in a
    /// heavy modlist.
    /// </summary>
    public static class SaveAudit
    {
        /// <summary>
        /// How much room each definer is assumed to occupy. TaleWorlds' own guidance is to leave
        /// generous gaps; this window is what makes "adjacent" readable as a warning rather than
        /// pretending exact equality is the only hazard.
        /// </summary>
        private const int AssumedSpan = 100;

        public static object Run()
        {
            Type definerBase = AccessTools.TypeByName("TaleWorlds.SaveSystem.Definition.SaveableTypeDefiner");

            if (definerBase == null)
            {
                return new
                {
                    error = "SaveableTypeDefiner not found",
                    hint = "The save API's type name moved. Nothing here is reliable on this version."
                };
            }

            var definers = new List<object>();
            var byId = new Dictionary<int, List<string>>();
            int unreadable = 0;

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types = TypeExplorer.SafeTypes(assembly);

                foreach (Type type in types)
                {
                    if (type == null || type.IsAbstract) continue;
                    if (!definerBase.IsAssignableFrom(type)) continue;

                    string owner = SafeName(assembly);
                    int? id = ReadBaseId(type, definerBase);

                    if (id == null)
                    {
                        unreadable++;
                        definers.Add(new
                        {
                            definer = type.FullName,
                            mod = owner,
                            module = ModuleMap.ForAssembly(assembly),
                            saveBaseId = (int?)null,
                            note = "base id not readable on this version"
                        });
                        continue;
                    }

                    definers.Add(new
                    {
                        definer = type.FullName,
                        mod = owner,
                        module = ModuleMap.ForAssembly(assembly),
                        saveBaseId = id,
                        note = (string)null
                    });

                    if (!byId.TryGetValue(id.Value, out List<string> owners))
                    {
                        owners = new List<string>();
                        byId[id.Value] = owners;
                    }

                    owners.Add(owner + " (" + type.Name + ")");
                }
            }

            // --- exact collisions: two definers claiming the same number ---
            var collisions = byId
                .Where(p => p.Value.Select(v => v).Distinct().Count() > 1)
                .OrderBy(p => p.Key)
                .Select(p => (object)new { saveBaseId = p.Key, claimedBy = p.Value.ToArray() })
                .ToArray();

            // --- near misses: distinct ids close enough that their ranges plausibly overlap ---
            var ordered = byId.Keys.OrderBy(k => k).ToList();
            var tooClose = new List<object>();

            for (int i = 1; i < ordered.Count; i++)
            {
                int gap = ordered[i] - ordered[i - 1];
                if (gap <= 0 || gap >= AssumedSpan) continue;

                tooClose.Add(new
                {
                    lower = ordered[i - 1],
                    upper = ordered[i],
                    gap,
                    between = byId[ordered[i - 1]].Concat(byId[ordered[i]]).ToArray()
                });
            }

            return new
            {
                note = collisions.Length == 0 && tooClose.Count == 0
                    ? "No save-id collisions. Every mod that persists data claims a distinct, "
                      + "well-spaced base id - which is what a save shared between mods needs."
                    : "Save-id problems. Two mods numbering their saved types into the same range "
                      + "write different objects under the same key, and the save becomes something "
                      + "neither reads back correctly. The failure shows up later, elsewhere, and "
                      + "usually blames the wrong mod.",

                definersFound = definers.Count,
                unreadableIds = unreadable,

                exactCollisions = collisions.Length,
                collisions,

                suspiciouslyClose = tooClose.Count,
                closeRanges = tooClose.Take(20).ToArray(),
                closeNote = "Ids less than " + AssumedSpan + " apart. Not proof of anything - a mod "
                            + "saving three types needs three numbers - but it is where to look "
                            + "first if a save is misbehaving.",

                definers = definers
                    .OrderBy(d => (int?)d.GetType().GetProperty("saveBaseId").GetValue(d) ?? int.MaxValue)
                    .ToArray()
            };
        }

        /// <summary>
        /// Reads the definer's base id. It is set through a protected constructor and kept in a
        /// private field whose name has varied, so several are tried before giving up - and giving
        /// up is reported rather than guessed at, because a wrong id here would invent a collision.
        /// </summary>
        private static int? ReadBaseId(Type definerType, Type definerBase)
        {
            string[] candidates = { "_saveBaseId", "saveBaseId", "SaveBaseId", "_baseId" };

            object instance = null;
            try
            {
                // Definers are constructed by the save system with no arguments available to us;
                // an uninitialized instance is enough to read a field the constructor sets, and
                // several definers do set it inline.
                instance = System.Runtime.Serialization.FormatterServices
                    .GetUninitializedObject(definerType);
            }
            catch
            {
                return null;
            }

            foreach (string name in candidates)
            {
                try
                {
                    FieldInfo field = AccessTools.Field(definerType, name)
                                      ?? AccessTools.Field(definerBase, name);

                    if (field == null) continue;

                    object value = field.GetValue(instance);
                    if (value is int id && id != 0) return id;
                }
                catch
                {
                    // Try the next candidate name.
                }
            }

            return null;
        }

        private static string SafeName(Assembly assembly)
        {
            try { return assembly.GetName().Name; }
            catch { return "?"; }
        }
    }
}
