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
    /// Which behaviors are actually registered and running.
    ///
    /// A mod can load perfectly, patch nothing, and register no behavior - and look completely fine
    /// from the outside while doing nothing at all. That is the failure mode this answers: a
    /// CampaignBehavior present in the DLL but missing from this list never had AddBehavior called
    /// on it, so none of its events will ever fire.
    ///
    /// Mission behaviors are listed separately and only exist while a mission (a battle, a town
    /// scene, a conversation) is running.
    /// </summary>
    public static class BehaviorsInspector
    {
        public static object CampaignBehaviors(string filter)
        {
            if (Campaign.Current == null) return new { error = "no campaign loaded" };

            var behaviors = new List<object>();

            try
            {
                foreach (CampaignBehaviorBase behavior in EnumerateCampaignBehaviors())
                {
                    if (behavior == null) continue;

                    Type type = behavior.GetType();
                    string assembly = type.Assembly.GetName().Name;
                    string full = type.FullName ?? type.Name;

                    if (!string.IsNullOrEmpty(filter) &&
                        full.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                        assembly.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    behaviors.Add(new
                    {
                        behavior = full,
                        assembly,
                        isVanilla = assembly.StartsWith("TaleWorlds", StringComparison.Ordinal)
                                    || assembly.StartsWith("SandBox", StringComparison.Ordinal)
                                    || assembly.StartsWith("StoryMode", StringComparison.Ordinal)
                    });
                }
            }
            catch (Exception ex)
            {
                return new { error = "could not enumerate campaign behaviors: " + ex.Message };
            }

            var byAssembly = behaviors
                .GroupBy(b => (string)b.GetType().GetProperty("assembly").GetValue(b))
                .Select(g => new { assembly = g.Key, count = g.Count() })
                .OrderByDescending(g => g.count)
                .ToArray();

            return new { count = behaviors.Count, byAssembly, behaviors = behaviors.ToArray() };
        }

        /// <summary>
        /// The behavior list lives in a private collection whose exact shape has moved between game
        /// versions, so this tries the known routes and gives up gracefully rather than guessing.
        /// </summary>
        public static IEnumerable<CampaignBehaviorBase> EnumerateCampaignBehaviors()
        {
            var found = new List<CampaignBehaviorBase>();

            object manager = AccessTools.Property(typeof(Campaign), "CampaignBehaviorManager")
                ?.GetValue(Campaign.Current);

            if (manager == null) return found;

            foreach (string memberName in new[] { "_campaignBehaviors", "CampaignBehaviors", "Behaviors" })
            {
                object collection = null;

                try
                {
                    collection = AccessTools.Field(manager.GetType(), memberName)?.GetValue(manager)
                                 ?? AccessTools.Property(manager.GetType(), memberName)?.GetValue(manager);
                }
                catch
                {
                    // Try the next candidate.
                }

                if (!(collection is IEnumerable items)) continue;

                foreach (object item in items)
                {
                    if (item is CampaignBehaviorBase behavior) found.Add(behavior);
                }

                if (found.Count > 0) break;
            }

            return found;
        }

        /// <summary>Mission behaviors - only meaningful while a battle or scene is running.</summary>
        public static object MissionBehaviors()
        {
            Mission mission = Mission.Current;
            if (mission == null)
            {
                return new
                {
                    inMission = false,
                    note = "No mission is running. Enter a battle, town or conversation and ask again."
                };
            }

            var behaviors = new List<object>();

            try
            {
                foreach (MissionBehavior behavior in mission.MissionBehaviors)
                {
                    if (behavior == null) continue;

                    Type type = behavior.GetType();
                    behaviors.Add(new
                    {
                        behavior = type.FullName,
                        assembly = type.Assembly.GetName().Name
                    });
                }
            }
            catch (Exception ex)
            {
                return new { inMission = true, error = "could not list mission behaviors: " + ex.Message };
            }

            return new
            {
                inMission = true,
                mode = mission.Mode.ToString(),
                sceneName = SafeScene(mission),
                count = behaviors.Count,
                behaviors = behaviors.ToArray()
            };
        }

        private static string SafeScene(Mission mission)
        {
            try { return mission.SceneName; }
            catch { return null; }
        }
    }
}
