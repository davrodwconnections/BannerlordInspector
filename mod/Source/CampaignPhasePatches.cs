using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

namespace BannerlordInspector
{
    /// <summary>
    /// Times the campaign's own tick dispatchers, so "the campaign map is slow" becomes a number
    /// against a named phase.
    ///
    /// WHY THE DISPATCHERS AND NOT EACH MOD. Every campaign behaviour in the game - vanilla and
    /// modded alike - is called through CampaignEventDispatcher. Timing those few methods costs
    /// almost nothing and covers everything; timing each behaviour individually would mean hundreds
    /// of patches for a diagnostic. This does not attribute cost to a specific mod, but it does say
    /// which phase is eating the frame, and the per-party phases are the ones that multiply.
    ///
    /// APPLIED AT LOAD, NEVER LATER. Patching a method while the game runs re-JITs it underneath
    /// whatever is calling it. These go on once, before a campaign exists, and are never touched
    /// again.
    ///
    /// COST. A Stopwatch timestamp pair per call. On AiHourlyTick with 5000 parties that is 5000
    /// timestamp pairs an in-game hour - tens of microseconds total, against a phase that takes
    /// milliseconds. If that ever stopped being true, the honest thing would be to make it opt-in;
    /// it is measured and it is not.
    /// </summary>
    public static class CampaignPhasePatches
    {
        /// <summary>
        /// The methods worth timing. Per-party and per-settlement ones matter most: they are called
        /// once for every party or settlement in the world, so a heavy modlist multiplies them.
        /// </summary>
        private static readonly string[] DispatcherMethods =
        {
            "AiHourlyTick",            // per party, the AI think - usually the expensive one
            "HourlyTickParty",         // per party
            "HourlyTickSettlement",    // per settlement
            "HourlyTickClan",
            "HourlyTick",
            "QuarterHourlyTick",
            "DailyTickParty",
            "DailyTickSettlement",
            "DailyTickClan",
            "DailyTick",
            "Tick",                    // the per-frame campaign tick
            "TickPartialHourlyAi"
        };

        private static int _patched;
        public static int PatchedCount => _patched;

        public static void Apply(Harmony harmony)
        {
            Type dispatcher = typeof(CampaignEventDispatcher);

            foreach (string name in DispatcherMethods)
            {
                try
                {
                    MethodInfo target = AccessTools.Method(dispatcher, name);
                    if (target == null) continue;

                    harmony.Patch(
                        target,
                        prefix: new HarmonyMethod(AccessTools.Method(typeof(CampaignPhasePatches), nameof(Before))),
                        finalizer: new HarmonyMethod(AccessTools.Method(typeof(CampaignPhasePatches), nameof(After))));

                    _patched++;
                }
                catch (Exception ex)
                {
                    // One unpatchable phase must not cost us the rest.
                    InspectorLog.Warn($"Could not time CampaignEventDispatcher.{name}: {ex.Message}");
                }
            }

            InspectorLog.Info($"Campaign phase timing attached to {_patched} dispatcher method(s).");
        }

        /// <summary>
        /// Nested calls happen - a daily tick can drive an hourly one - so each invocation carries
        /// its own start time rather than sharing one field.
        /// </summary>
        public static void Before(out long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        /// <summary>
        /// A finalizer rather than a postfix, so the timing is still recorded when the original
        /// throws - and an exception thrown inside a tick is exactly the case worth seeing.
        /// </summary>
        public static void After(long __state, MethodBase __originalMethod)
        {
            try
            {
                double ms = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
                PerformanceMonitor.RecordPhase(__originalMethod?.Name ?? "unknown", ms);
            }
            catch
            {
                // Never throw out of a finalizer - it would replace the original exception.
            }
        }
    }
}
