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
    /// Names that never resolved - the text bugs a player actually sees.
    ///
    /// A conversion renames everything, and the failures are quiet. A localization key that was
    /// never registered leaves the raw "{=key}" on screen; a name that resolved to nothing leaves a
    /// blank where a faction should be; a name identical to its own id means the display name was
    /// never authored at all and the player is reading "npc_goblin_archer_3".
    ///
    /// None of that throws. None of it appears in a log. It is only visible by playing far enough to
    /// meet the object - which for a wanderer, a minor clan or a late-game troop can be never.
    ///
    /// This is NOT a replacement for checking the XML on disk, which sees keys this cannot: strings
    /// declared but never used. This sees the opposite and more important half - what the RUNNING
    /// game will put in front of a player after every XSLT, merge and language file has had its say.
    /// The two disagree in both directions, and the runtime answer is the one the player lives with.
    /// </summary>
    public static class TextAudit
    {
        /// <summary>Registries worth auditing: everything with a player-visible name.</summary>
        private static readonly string[] Registries =
        {
            "TaleWorlds.CampaignSystem.CultureObject",
            "TaleWorlds.CampaignSystem.Clan",
            "TaleWorlds.CampaignSystem.Kingdom",
            "TaleWorlds.CampaignSystem.CharacterObject",
            "TaleWorlds.CampaignSystem.Settlements.Settlement",
            "TaleWorlds.Core.ItemObject"
        };

        public static object Run(string filter, int limit)
        {
            if (MBObjectManager.Instance == null)
            {
                return new { error = "no object registry yet - load a campaign first" };
            }

            var unresolvedKey = new List<object>();   // still shows {=...}
            var blank = new List<object>();           // resolved to nothing
            var idAsName = new List<object>();        // name is just the id

            int examined = 0;
            int unresolvedTotal = 0, blankTotal = 0, idTotal = 0;

            foreach (string typeName in Registries)
            {
                Type type = AccessTools.TypeByName(typeName);
                if (type == null) continue;

                IEnumerable list = Registry(type);
                if (list == null) continue;

                foreach (object entry in list)
                {
                    if (!(entry is MBObjectBase obj)) continue;

                    string id = Safe(() => obj.StringId);
                    if (id == null) continue;

                    if (!string.IsNullOrEmpty(filter)
                        && id.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    examined++;

                    string name = Safe(() => AccessTools.Property(obj.GetType(), "Name")
                                                        ?.GetValue(obj)?.ToString());

                    // A raw key on screen. The most visible of the three and always a bug.
                    if (name != null && name.IndexOf("{=", StringComparison.Ordinal) >= 0)
                    {
                        unresolvedTotal++;
                        if (unresolvedKey.Count < limit)
                        {
                            unresolvedKey.Add(Row(type, id, name, "localization key never registered"));
                        }
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(name))
                    {
                        blankTotal++;
                        if (blank.Count < limit)
                        {
                            blank.Add(Row(type, id, name, "name resolved to nothing"));
                        }
                        continue;
                    }

                    // Reading an internal id is not as loud as a raw key, but it is the same bug
                    // wearing a hat: nobody wrote a display name.
                    if (string.Equals(name, id, StringComparison.Ordinal))
                    {
                        idTotal++;
                        if (idAsName.Count < limit)
                        {
                            idAsName.Add(Row(type, id, name, "display name is the raw id"));
                        }
                    }
                }
            }

            int problems = unresolvedTotal + blankTotal + idTotal;

            return new
            {
                note = problems == 0
                    ? "Every name resolved. This checks what the RUNNING game would show, after "
                      + "XSLT, merges and language files - not what the XML on disk declares."
                    : "Names the player would see wrong, worst first. 'unresolvedKey' shows a raw "
                      + "{=key} on screen and is always a bug. 'blank' leaves an empty space. "
                      + "'idAsName' means no display name was ever authored.",
                examined,
                filter,
                counts = new
                {
                    unresolvedKey = unresolvedTotal,
                    blank = blankTotal,
                    idAsName = idTotal
                },
                truncated = unresolvedTotal > limit || blankTotal > limit || idTotal > limit,
                unresolvedKey = unresolvedKey.ToArray(),
                blank = blank.ToArray(),
                idAsName = idAsName.ToArray(),
                caveat = "Cannot see strings declared in XML but never used - only the on-disk check "
                         + "sees those. The two answers disagree in both directions and both matter."
            };
        }

        private static object Row(Type type, string id, string name, string problem)
        {
            return new { kind = type.Name, id, shownAs = name, problem };
        }

        private static IEnumerable Registry(Type type)
        {
            try
            {
                MethodInfo generic = typeof(MBObjectManager)
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GetObjectTypeList"
                                         && m.IsGenericMethodDefinition
                                         && m.GetParameters().Length == 0);

                return generic?.MakeGenericMethod(type).Invoke(MBObjectManager.Instance, null) as IEnumerable;
            }
            catch
            {
                return null;
            }
        }

        private static T Safe<T>(Func<T> read)
        {
            try { return read(); }
            catch { return default; }
        }
    }
}
