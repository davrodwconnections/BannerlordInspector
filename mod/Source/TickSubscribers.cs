using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

namespace BannerlordInspector
{
    /// <summary>
    /// Who is subscribed to the campaign's tick events, and therefore who to blame.
    ///
    /// WHY THIS EXISTS. Phase timing said the daily tick costs 604 ms in a single call, which names
    /// the moment but not the culprit: every mod's daily work runs inside that one call. The
    /// dispatcher fans out through MbEvent, and each subscriber is a delegate held in a linked list
    /// of EventHandlerRec nodes - each node carrying the Action and the Owner object. Walking that
    /// list turns "the daily tick is slow" into a list of named methods with the mod they came from.
    ///
    /// This walk is READ-ONLY and cheap: a handful of pointer hops per event, done on demand rather
    /// than every frame. It patches nothing, so it cannot break the ordering or the behaviour of the
    /// events it inspects - which matters, because tick order between mods is load-bearing and a
    /// diagnostic has no business changing it.
    ///
    /// It reports who subscribes, not how long each one takes. That is a deliberate stopping point:
    /// timing them individually means patching every subscriber, and the honest first step is to see
    /// how many there are and whose they are. Often that alone is the answer - one mod with forty
    /// daily subscribers on a five-thousand-party world does not need a stopwatch to be suspicious.
    /// </summary>
    public static class TickSubscribers
    {
        /// <summary>The events worth asking about, in the order they matter for a slow campaign.</summary>
        private static readonly string[] InterestingEvents =
        {
            "DailyTickEvent",
            "DailyTickSettlementEvent",
            "DailyTickClanEvent",
            "DailyTickPartyEvent",
            "DailyTickHeroEvent",
            "DailyTickTownEvent",
            "HourlyTickEvent",
            "HourlyTickPartyEvent",
            "HourlyTickSettlementEvent",
            "HourlyTickClanEvent",
            "TickEvent",
            "AiHourlyTickEvent"
        };

        public static object Report(string eventFilter)
        {
            if (Campaign.Current == null) return new { error = "no campaign loaded" };

            var results = new List<object>();

            foreach (string eventName in InterestingEvents)
            {
                if (!string.IsNullOrEmpty(eventFilter) &&
                    eventName.IndexOf(eventFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                object mbEvent = ReadEvent(eventName);
                if (mbEvent == null) continue;

                List<Subscriber> subscribers = WalkListeners(mbEvent);

                var byAssembly = subscribers
                    .GroupBy(s => s.Assembly)
                    .Select(g => new { mod = g.Key, subscribers = g.Count() })
                    .OrderByDescending(g => g.subscribers)
                    .ToArray();

                results.Add(new
                {
                    campaignEvent = eventName,
                    subscriberCount = subscribers.Count,
                    byMod = byAssembly,
                    subscribers = subscribers
                        .OrderBy(s => s.Assembly, StringComparer.OrdinalIgnoreCase)
                        .Select(s => new { mod = s.Assembly, handler = s.Method })
                        .ToArray()
                });
            }

            return new
            {
                note = "Every subscriber below runs inside that one tick. For the per-party and "
                       + "per-settlement events, multiply by the party or settlement count - that is "
                       + "where a heavy modlist turns cheap handlers into a stall.",
                caveat = "This says WHO runs, not how long each takes. A mod with many subscribers is "
                         + "a suspect, not a verdict.",
                events = results
            };
        }

        private sealed class Subscriber
        {
            public string Assembly;
            public string Method;
        }

        /// <summary>Reads CampaignEvents.SomeEvent, which is a static property returning the MbEvent.</summary>
        private static object ReadEvent(string propertyName)
        {
            try
            {
                PropertyInfo property = AccessTools.Property(typeof(CampaignEvents), propertyName);
                return property?.GetValue(null);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Walks the EventHandlerRec chain. The list and its node type are private and generic
        /// arities differ per event, so everything here is by name and defensive: an event whose
        /// shape has moved is skipped, not fatal.
        /// </summary>
        private static List<Subscriber> WalkListeners(object mbEvent)
        {
            var found = new List<Subscriber>();

            try
            {
                Type eventType = mbEvent.GetType();

                FieldInfo listField = AccessTools.Field(eventType, "_nonSerializedListenerList")
                                      ?? AccessTools.Field(eventType, "_listenerList");
                if (listField == null) return found;

                object node = listField.GetValue(mbEvent);

                int guard = 0;
                while (node != null && guard++ < 500)
                {
                    Type nodeType = node.GetType();

                    object action = AccessTools.Property(nodeType, "Action")?.GetValue(node)
                                    ?? AccessTools.Field(nodeType, "Action")?.GetValue(node);

                    if (action is Delegate del)
                    {
                        MethodInfo method = del.Method;
                        Type owner = method?.DeclaringType;

                        found.Add(new Subscriber
                        {
                            Assembly = owner?.Assembly.GetName().Name ?? "(unknown)",
                            Method = (owner?.FullName ?? "?") + "." + (method?.Name ?? "?")
                        });
                    }

                    node = AccessTools.Property(nodeType, "Next")?.GetValue(node)
                           ?? AccessTools.Field(nodeType, "Next")?.GetValue(node);
                }
            }
            catch (Exception ex)
            {
                InspectorLog.Error("Could not walk an event's listener list.", ex);
            }

            return found;
        }
    }
}
