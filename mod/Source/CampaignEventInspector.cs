using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TaleWorlds.CampaignSystem;

namespace BannerlordInspector
{
    /// <summary>
    /// Who is listening to what, across every campaign event rather than the dozen tick ones.
    ///
    /// This answers the most common complaint in modding, and the one with the fewest clues:
    /// "my feature never fires". A behaviour that registered fine but subscribed to nothing is
    /// indistinguishable, from the outside, from one whose logic is wrong - and the two have nothing
    /// in common as problems. Silence is the symptom either way.
    ///
    /// The reverse direction is the more useful one in practice. Asking "what is TAOM's enlistment
    /// actually listening to" turns a vague suspicion into a list, and a missing name in that list
    /// is a finished diagnosis: if nothing subscribes to MapEventEnded, the feature cannot react to
    /// a battle no matter how correct the rest of it is.
    ///
    /// Read-only in the strictest sense: it walks the handler chains and never touches them.
    /// Subscription order between mods is load-bearing, and a diagnostic that perturbs it would be
    /// changing the thing it was asked to explain.
    /// </summary>
    public static class CampaignEventInspector
    {
        public static object Run(string eventFilter, string modFilter, bool onlySubscribed, int limit)
        {
            if (Campaign.Current == null) return new { error = "no campaign loaded" };

            List<string> names = EventNames();
            if (names.Count == 0)
            {
                return new { error = "no campaign events found on CampaignEvents - the API shape moved" };
            }

            var rows = new List<object>();
            var perMod = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            int withSubscribers = 0, examined = 0;

            foreach (string name in names)
            {
                if (!string.IsNullOrEmpty(eventFilter)
                    && name.IndexOf(eventFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                object mbEvent = TickSubscribers.ReadEvent(name);
                if (mbEvent == null) continue;

                examined++;

                List<TickSubscribers.Subscriber> subscribers;
                try { subscribers = TickSubscribers.WalkListeners(mbEvent); }
                catch { continue; }

                if (subscribers.Count > 0) withSubscribers++;

                foreach (var s in subscribers)
                {
                    perMod.TryGetValue(s.Assembly ?? "?", out int n);
                    perMod[s.Assembly ?? "?"] = n + 1;
                }

                // When asking about one mod, an event it does not listen to is not an answer.
                var relevant = string.IsNullOrEmpty(modFilter)
                    ? subscribers
                    : subscribers.Where(s => (s.Assembly ?? "")
                        .IndexOf(modFilter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

                if (!string.IsNullOrEmpty(modFilter) && relevant.Count == 0) continue;
                if (onlySubscribed && relevant.Count == 0) continue;

                if (rows.Count < limit)
                {
                    rows.Add(new
                    {
                        campaignEvent = name,
                        subscriberCount = relevant.Count,
                        totalOnThisEvent = subscribers.Count,
                        subscribers = relevant
                            .OrderBy(s => s.Assembly, StringComparer.OrdinalIgnoreCase)
                            .Select(s => new { mod = s.Assembly, handler = s.Method })
                            .Take(20)
                            .ToArray()
                    });
                }
            }

            var byMod = perMod
                .Where(p => !IsEngine(p.Key))
                .OrderByDescending(p => p.Value)
                .Take(20)
                .Select(p => (object)new { mod = p.Key, subscriptions = p.Value })
                .ToArray();

            return new
            {
                note = string.IsNullOrEmpty(modFilter)
                    ? "Subscribers per campaign event. Filter with mod= to ask the useful question: "
                      + "what is one mod actually listening to? A name missing from that list is a "
                      + "finished diagnosis - code that is not subscribed cannot fire."
                    : "Every event '" + modFilter + "' subscribes to. If an event you expected is "
                      + "absent, that feature cannot react to it, however correct its logic is.",
                eventsKnown = names.Count,
                eventsExamined = examined,
                eventsWithSubscribers = withSubscribers,
                filters = new { campaignEvent = eventFilter, mod = modFilter, onlySubscribed },
                returned = rows.Count,
                truncated = rows.Count >= limit,
                busiestMods = byMod,
                events = rows.ToArray()
            };
        }

        /// <summary>
        /// Every static property on CampaignEvents that hands back an event object.
        ///
        /// Enumerated rather than listed by hand: the set changes with every game version, and a
        /// hard-coded list would quietly stop covering whatever the new engine added - which is
        /// exactly when the coverage is wanted.
        /// </summary>
        private static List<string> EventNames()
        {
            var names = new List<string>();

            try
            {
                foreach (PropertyInfo property in typeof(CampaignEvents)
                    .GetProperties(BindingFlags.Public | BindingFlags.Static))
                {
                    if (property.GetIndexParameters().Length > 0) continue;

                    // MbEvent<T> and friends: matched by name because the arities differ per event
                    // and there is no common base worth binding to.
                    string typeName = property.PropertyType.Name;
                    if (typeName.IndexOf("Event", StringComparison.Ordinal) < 0) continue;

                    names.Add(property.Name);
                }
            }
            catch
            {
                // A partial list still answers most questions.
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        private static bool IsEngine(string assembly)
        {
            if (string.IsNullOrEmpty(assembly)) return true;

            return assembly.StartsWith("TaleWorlds", StringComparison.OrdinalIgnoreCase)
                   || assembly.StartsWith("SandBox", StringComparison.OrdinalIgnoreCase)
                   || assembly.StartsWith("StoryMode", StringComparison.OrdinalIgnoreCase);
        }
    }
}
