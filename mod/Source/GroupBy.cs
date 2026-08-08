using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;

namespace BannerlordInspector
{
    /// <summary>
    /// Groups the world by any property a mod added, and gives numeric statistics per group.
    ///
    /// This is the deliberately generic answer to a very specific family of questions. A total
    /// conversion adds races, and its test plan then says things like "elves should be effectively
    /// immortal, dwarves die around 250, orcs around 60". Verifying that by hand means fast
    /// forwarding a campaign for hours and watching who dies.
    ///
    /// It is one call from here:
    ///
    ///     over=heroes by=Race stat=Age
    ///     -> Race=Elf   n=214  Age min 30 max 9412 avg 1180
    ///        Race=Dwarf n=96   Age min 24 max  247 avg  138
    ///
    /// Nothing here knows what a race is. It reads whatever property you name, on whatever objects
    /// you name, which is why it works on a mod this tool has never heard of - and why, when the
    /// property name is wrong, the most useful thing it can do is list the ones that exist.
    /// </summary>
    public static class GroupBy
    {
        private const int MaxGroups = 60;
        private const int MaxSamplesPerGroup = 3;

        public static object Run(string over, string by, string stat, int limit)
        {
            if (string.IsNullOrWhiteSpace(by))
            {
                return new
                {
                    error = "give by=<property>",
                    hint = "e.g. over=heroes&by=Culture.StringId&stat=Age. Omit 'by' and you get "
                           + "nothing to group on.",
                    collections = Collections()
                };
            }

            List<object> items = Collection(over, out string resolved, out string error);
            if (error != null) return new { error, collections = Collections() };

            if (items.Count == 0)
            {
                return new { over = resolved, count = 0, note = "that collection is empty right now" };
            }

            // Probe on a real element: a name that does not resolve is the common mistake, and
            // guessing is worse than showing what is actually there.
            object probe = items.FirstOrDefault(i => i != null);
            if (probe != null && !PathResolves(probe.GetType(), by))
            {
                return new
                {
                    error = "no property or field named '" + by + "' on " + probe.GetType().Name,
                    hint = "Dotted paths work: Culture.StringId. Below is what this type actually "
                           + "exposes - the property you want is probably in there under another name.",
                    type = probe.GetType().FullName,
                    available = Readable(probe.GetType())
                };
            }

            var groups = new Dictionary<string, Bucket>(StringComparer.Ordinal);
            int nulls = 0, failures = 0;

            foreach (object item in items)
            {
                if (item == null) continue;

                object key;
                try { key = Read(item, by); }
                catch { failures++; continue; }

                string label = key == null ? "(null)" : key.ToString();
                if (key == null) nulls++;

                if (!groups.TryGetValue(label, out Bucket bucket))
                {
                    if (groups.Count >= MaxGroups)
                    {
                        // A property with thousands of distinct values is not a grouping - it is an
                        // id. Say so rather than returning a useless wall of one-item groups.
                        return new
                        {
                            error = "'" + by + "' has more than " + MaxGroups + " distinct values",
                            why = "That looks like an identifier rather than a category. Group by "
                                  + "something coarser, or use /objects to list them.",
                            over = resolved,
                            sampleValues = groups.Keys.Take(10).ToArray()
                        };
                    }

                    bucket = new Bucket();
                    groups[label] = bucket;
                }

                bucket.Count++;

                if (bucket.Samples.Count < MaxSamplesPerGroup)
                {
                    bucket.Samples.Add(Describe(item));
                }

                if (!string.IsNullOrWhiteSpace(stat))
                {
                    try
                    {
                        object raw = Read(item, stat);
                        if (raw != null && TryNumber(raw, out double value)) bucket.Add(value);
                    }
                    catch
                    {
                        // A stat that fails on one element should not lose the whole grouping.
                    }
                }
            }

            var rows = groups
                .OrderByDescending(g => g.Value.Count)
                .Take(limit > 0 ? limit : MaxGroups)
                .Select(g => (object)new
                {
                    value = g.Key,
                    count = g.Value.Count,
                    stats = string.IsNullOrWhiteSpace(stat) || g.Value.StatCount == 0
                        ? null
                        : new
                        {
                            of = stat,
                            n = g.Value.StatCount,
                            min = Round(g.Value.Min),
                            max = Round(g.Value.Max),
                            average = Round(g.Value.Sum / g.Value.StatCount)
                        },
                    examples = g.Value.Samples.ToArray()
                })
                .ToArray();

            return new
            {
                note = "Grouped live, from the running campaign. 'stats' is only present when you "
                       + "pass stat= and the value is numeric.",
                over = resolved,
                by,
                stat = string.IsNullOrWhiteSpace(stat) ? null : stat,
                examined = items.Count,
                distinctValues = groups.Count,
                nullValues = nulls,
                unreadable = failures,
                groups = rows
            };
        }

        private sealed class Bucket
        {
            public int Count;
            public int StatCount;
            public double Min = double.MaxValue;
            public double Max = double.MinValue;
            public double Sum;
            public readonly List<string> Samples = new List<string>();

            public void Add(double value)
            {
                StatCount++;
                Sum += value;
                if (value < Min) Min = value;
                if (value > Max) Max = value;
            }
        }

        // ------------------------------------------------------------------ collections

        private static object Collections() => new
        {
            over = new[] { "heroes", "livingheroes", "troops", "settlements", "clans", "kingdoms", "parties" },
            note = "'heroes' means living heroes. 'troops' is every registered CharacterObject, "
                   + "which includes hero templates."
        };

        private static List<object> Collection(string over, out string resolved, out string error)
        {
            error = null;
            resolved = string.IsNullOrWhiteSpace(over) ? "heroes" : over.Trim().ToLowerInvariant();

            if (Campaign.Current == null)
            {
                error = "no campaign loaded";
                return new List<object>();
            }

            switch (resolved)
            {
                case "hero":
                case "heroes":
                case "livingheroes":
                    resolved = "heroes";
                    return Hero.AllAliveHeroes?.Cast<object>().ToList() ?? new List<object>();

                case "troop":
                case "troops":
                case "characters":
                    resolved = "troops";
                    return CharacterObject.All?.Cast<object>().ToList() ?? new List<object>();

                case "settlement":
                case "settlements":
                    resolved = "settlements";
                    return Settlement.All?.Cast<object>().ToList() ?? new List<object>();

                case "clan":
                case "clans":
                    resolved = "clans";
                    return Clan.All?.Cast<object>().ToList() ?? new List<object>();

                case "kingdom":
                case "kingdoms":
                    resolved = "kingdoms";
                    return Kingdom.All?.Cast<object>().ToList() ?? new List<object>();

                case "party":
                case "parties":
                    resolved = "parties";
                    return TaleWorlds.CampaignSystem.Party.MobileParty.All?.Cast<object>().ToList()
                           ?? new List<object>();

                default:
                    error = "unknown collection '" + over + "'";
                    return new List<object>();
            }
        }

        // ------------------------------------------------------------------ reflection

        /// <summary>Reads a dotted path of properties and fields. Never invokes a method.</summary>
        private static object Read(object target, string path)
        {
            object current = target;

            foreach (string step in path.Split('.'))
            {
                if (current == null) return null;

                Type type = current.GetType();

                PropertyInfo property = type.GetProperty(step,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);

                if (property != null && property.CanRead && property.GetIndexParameters().Length == 0)
                {
                    current = property.GetValue(current);
                    continue;
                }

                FieldInfo field = type.GetField(step,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);

                if (field == null) throw new MissingMemberException(type.Name, step);

                current = field.GetValue(current);
            }

            return current;
        }

        private static bool PathResolves(Type type, string path)
        {
            Type current = type;

            foreach (string step in path.Split('.'))
            {
                if (current == null) return false;

                PropertyInfo property = current.GetProperty(step,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);

                if (property != null) { current = property.PropertyType; continue; }

                FieldInfo field = current.GetField(step,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);

                if (field == null) return false;
                current = field.FieldType;
            }

            return true;
        }

        /// <summary>What you could have grouped by. Shown when the given name does not resolve.</summary>
        private static object Readable(Type type)
        {
            var names = new List<string>();

            try
            {
                foreach (PropertyInfo p in type.GetProperties(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy))
                {
                    if (p.GetIndexParameters().Length == 0 && p.CanRead) names.Add(p.Name);
                }

                foreach (FieldInfo f in type.GetFields(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy))
                {
                    names.Add(f.Name);
                }
            }
            catch
            {
                // Partial list beats no list.
            }

            return names.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        }

        private static bool TryNumber(object value, out double result)
        {
            switch (value)
            {
                case int i: result = i; return true;
                case float f: result = f; return true;
                case double d: result = d; return true;
                case long l: result = l; return true;
                case short s: result = s; return true;
                case byte b: result = b; return true;
                case decimal m: result = (double)m; return true;
            }

            return double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        }

        private static double Round(double value) => Math.Round(value, 2);

        private static string Describe(object item)
        {
            try
            {
                switch (item)
                {
                    case Hero hero: return hero.Name?.ToString() + " (" + hero.StringId + ")";
                    case CharacterObject character: return character.StringId;
                    case Settlement settlement: return settlement.Name?.ToString() + " (" + settlement.StringId + ")";
                    case Clan clan: return clan.Name?.ToString();
                    case Kingdom kingdom: return kingdom.Name?.ToString();
                    default: return item.ToString();
                }
            }
            catch
            {
                return "?";
            }
        }
    }
}
