using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.ObjectSystem;

namespace BannerlordInspector
{
    /// <summary>
    /// Browses the game's object registry - troops, items, cultures, factions, anything registered
    /// as an MBObjectBase.
    ///
    /// The other endpoints answer "what is this mod DOING". This one answers "what did it ADD",
    /// which is a different and equally common question. Retinues mints a thousand troop stubs;
    /// Realm of Thrones replaces every culture; a dozen mods add items. Counting them from the XML
    /// on disk is guesswork, because what actually made it into the registry is the only truth that
    /// matters - a malformed entry is simply absent, with no error anywhere.
    ///
    /// Filtering is by StringId and by name, so "how many retinues_custom troops exist and how many
    /// are actually in use" is one call.
    /// </summary>
    public static class ObjectBrowser
    {
        /// <summary>Common registries, so the caller does not have to know the exact class name.</summary>
        private static readonly Dictionary<string, string> Aliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "troop", "TaleWorlds.CampaignSystem.CharacterObject" },
                { "troops", "TaleWorlds.CampaignSystem.CharacterObject" },
                { "character", "TaleWorlds.CampaignSystem.CharacterObject" },
                { "item", "TaleWorlds.Core.ItemObject" },
                { "items", "TaleWorlds.Core.ItemObject" },
                { "culture", "TaleWorlds.CampaignSystem.CultureObject" },
                { "cultures", "TaleWorlds.CampaignSystem.CultureObject" },
                { "settlement", "TaleWorlds.CampaignSystem.Settlements.Settlement" },
                { "clan", "TaleWorlds.CampaignSystem.Clan" },
                { "kingdom", "TaleWorlds.CampaignSystem.Kingdom" },
                { "hero", "TaleWorlds.CampaignSystem.Hero" },
                { "perk", "TaleWorlds.CampaignSystem.CharacterDevelopment.PerkObject" },
                { "trait", "TaleWorlds.CampaignSystem.CharacterDevelopment.TraitObject" },
                { "skill", "TaleWorlds.Core.SkillObject" },
                { "equipment", "TaleWorlds.Core.MBEquipmentRoster" },
                { "banner", "TaleWorlds.Core.BasicCultureObject" }
            };

        public static object Known() => new
        {
            note = "Pass any of these to type=, or a full class name such as "
                   + "TaleWorlds.CampaignSystem.CharacterObject.",
            aliases = Aliases.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray()
        };

        public static object Browse(string typeName, string query, int limit, bool countOnly)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                return new { error = "give a type", options = Known() };
            }

            if (Aliases.TryGetValue(typeName, out string mapped)) typeName = mapped;

            Type type = AccessTools.TypeByName(typeName);
            if (type == null)
            {
                return new
                {
                    error = "type not found",
                    typeName,
                    hint = "Use an alias, or find the exact name with /types?q=",
                    options = Known()
                };
            }

            if (!typeof(MBObjectBase).IsAssignableFrom(type))
            {
                return new
                {
                    error = "not a registered game-object type",
                    typeName = type.FullName,
                    why = "Only MBObjectBase subclasses live in the object registry. For anything "
                          + "else, read it with /eval."
                };
            }

            IEnumerable list;
            try
            {
                list = GetRegistry(type);
            }
            catch (Exception ex)
            {
                return new { error = "could not read the registry: " + ex.Message, typeName = type.FullName };
            }

            if (list == null)
            {
                return new
                {
                    error = "the registry for that type is empty or not exposed",
                    typeName = type.FullName
                };
            }

            int total = 0;
            int matched = 0;
            var rows = new List<object>();

            foreach (object item in list)
            {
                if (!(item is MBObjectBase obj)) continue;
                total++;

                string stringId = SafeId(obj);
                string name = SafeName(obj);

                if (!string.IsNullOrEmpty(query))
                {
                    bool hit = (stringId != null && stringId.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                               || (name != null && name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!hit) continue;
                }

                matched++;
                if (countOnly || rows.Count >= limit) continue;

                rows.Add(new { stringId, name, type = obj.GetType().Name });
            }

            return new
            {
                type = type.FullName,
                totalRegistered = total,
                matched,
                returned = rows.Count,
                truncated = !countOnly && matched > rows.Count,
                objects = countOnly ? null : rows.ToArray()
            };
        }

        /// <summary>
        /// MBObjectManager.GetObjectTypeList&lt;T&gt;() is generic, so it is found by shape rather
        /// than by a typed lookup.
        /// </summary>
        private static IEnumerable GetRegistry(Type type)
        {
            if (MBObjectManager.Instance == null) return null;

            MethodInfo generic = typeof(MBObjectManager)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "GetObjectTypeList"
                                     && m.IsGenericMethodDefinition
                                     && m.GetParameters().Length == 0);

            if (generic == null) return null;

            return generic.MakeGenericMethod(type).Invoke(MBObjectManager.Instance, null) as IEnumerable;
        }

        private static string SafeId(MBObjectBase obj)
        {
            try { return obj.StringId; }
            catch { return null; }
        }

        private static string SafeName(MBObjectBase obj)
        {
            try
            {
                object name = AccessTools.Property(obj.GetType(), "Name")?.GetValue(obj);
                return name?.ToString();
            }
            catch
            {
                return null;
            }
        }
    }
}
