using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.ObjectSystem;

namespace BannerlordInspector
{
    /// <summary>
    /// Walks a dotted path into live game state, e.g.
    ///
    ///   Campaign.Current.Models.DiplomacyModel.$type
    ///     -> "BannerKings.Models.Vanilla.BKDiplomacyModel"
    ///
    /// This is the part that turns guesswork into fact. Static analysis could not tell us which
    /// diplomacy model actually wins at runtime, whether a hero really has no Banner Kings faith,
    /// or whether the player counts as captive in vanilla terms. One path each, answered.
    ///
    /// READ-ONLY BY CONSTRUCTION. It reads properties and fields, and it will not invoke a method -
    /// not even a harmless-looking one - because "call this and show me" is exactly how an inspector
    /// turns into a way to change the game by accident. Where a method would normally be needed,
    /// there are pseudo-members instead:
    ///
    ///   .$type      the concrete runtime type (replaces GetType().FullName)
    ///   .$members   what can be read from here, so the path can be explored without documentation
    ///   .$count     element count of a collection
    ///   [n]         index into a list or array
    ///
    /// Non-public members are reachable: half of what is worth inspecting in a modded install is
    /// private, and this only ever reads.
    /// </summary>
    public static class PathEvaluator
    {
        private const int MaxSteps = 24;
        private const int MaxListPreview = 25;

        public static object Evaluate(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return new { error = "empty path", roots = Roots() };
            }

            List<string> steps = Tokenize(path);
            if (steps.Count == 0) return new { error = "could not parse path", path };
            if (steps.Count > MaxSteps) return new { error = "path too long", limit = MaxSteps };

            object current;
            string consumed;

            if (!TryResolveRoot(steps, out current, out int startIndex, out consumed))
            {
                return new
                {
                    error = "unknown root",
                    path,
                    hint = "Start from one of the known roots, or use type:Some.Type.Name for statics.",
                    roots = Roots()
                };
            }

            for (int i = startIndex; i < steps.Count; i++)
            {
                string step = steps[i];

                if (current == null)
                {
                    return new { path = consumed, value = (object)null, note = "null before '" + step + "'" };
                }

                // Pseudo-members: the read-only stand-ins for the method calls we refuse to make.
                if (step == "$type") return new { path = consumed + ".$type", value = current.GetType().FullName };
                if (step == "$members") return new { path = consumed + ".$members", members = Members(current) };
                if (step == "$count")
                {
                    int? count = CountOf(current);
                    return count.HasValue
                        ? (object)new { path = consumed + ".$count", value = count.Value }
                        : new { error = "not a collection", path = consumed, type = current.GetType().FullName };
                }

                if (step.StartsWith("[") && step.EndsWith("]"))
                {
                    if (!TryIndex(current, step, out object indexed, out string indexError))
                    {
                        return new { error = indexError, path = consumed };
                    }
                    current = indexed;
                    consumed += step;
                    continue;
                }

                if (!TryRead(current, step, out object next, out string error))
                {
                    return new
                    {
                        error,
                        path = consumed,
                        type = current.GetType().FullName,
                        available = Members(current)
                    };
                }

                current = next;
                consumed += "." + step;
            }

            return new { path = consumed, value = Render(current) };
        }

        // ------------------------------------------------------------------ roots

        private static string[] Roots() => new[]
        {
            "Campaign.Current", "Hero.MainHero", "Clan.PlayerClan", "MobileParty.MainParty",
            "Settlement.All", "Hero.AllAliveHeroes", "Kingdom.All", "Clan.All",
            "type:<Full.Type.Name> (for static members)"
        };

        private static bool TryResolveRoot(List<string> steps, out object root, out int nextIndex, out string consumed)
        {
            root = null;
            nextIndex = 0;
            consumed = string.Empty;

            string first = steps[0];

            // type:Namespace.Type -> the Type itself, so statics can be read off it.
            //
            // The type name contains dots, and so does the member path that follows it, so there is
            // no separator to split on. Resolve greedily instead: try ever-longer prefixes and keep
            // the longest that is a real type. "type:BannerKings.BannerKingsConfig.Instance" then
            // correctly yields the type BannerKings.BannerKingsConfig plus the member Instance,
            // rather than looking for a type called "BannerKings".
            if (first.StartsWith("type:", StringComparison.OrdinalIgnoreCase))
            {
                string candidate = first.Substring(5);
                Type best = AccessTools.TypeByName(candidate);
                int bestCount = 1;

                for (int i = 1; i < steps.Count; i++)
                {
                    // Stop widening at anything that cannot be part of a type name.
                    if (steps[i].StartsWith("[") || steps[i].StartsWith("$")) break;

                    candidate += "." + steps[i];
                    Type longer = AccessTools.TypeByName(candidate);
                    if (longer == null) continue;

                    best = longer;
                    bestCount = i + 1;
                }

                if (best == null) return false;

                root = new StaticTypeHandle(best);
                nextIndex = bestCount;
                consumed = "type:" + best.FullName;
                return true;
            }

            if (steps.Count >= 2)
            {
                string pair = first + "." + steps[1];
                switch (pair)
                {
                    case "Campaign.Current": root = Campaign.Current; break;
                    case "Hero.MainHero": root = Hero.MainHero; break;
                    case "Hero.AllAliveHeroes": root = Hero.AllAliveHeroes; break;
                    case "Clan.PlayerClan": root = Clan.PlayerClan; break;
                    case "Clan.All": root = Clan.All; break;
                    case "Kingdom.All": root = Kingdom.All; break;
                    case "MobileParty.MainParty": root = MobileParty.MainParty; break;
                    case "MobileParty.All": root = MobileParty.All; break;
                    case "Settlement.All": root = Settlement.All; break;
                    default: return false;
                }

                nextIndex = 2;
                consumed = pair;
                return true;
            }

            return false;
        }

        /// <summary>Marks "this is a Type, read statics off it" without being a Type instance itself.</summary>
        public sealed class StaticTypeHandle
        {
            public readonly Type Type;
            public StaticTypeHandle(Type type) { Type = type; }
        }

        /// <summary>
        /// Walks a path and hands back the LIVE object at the end of it, unrendered.
        ///
        /// <see cref="Evaluate"/> is for looking; this is for handing a real target to
        /// <see cref="QueryInvoker"/> so a question-shaped method can be called on it. An empty
        /// path is legitimate and means "no instance", which is how static calls arrive.
        /// </summary>
        public static object ResolveTarget(string path, out string error)
        {
            error = null;

            if (string.IsNullOrWhiteSpace(path)) return null;

            List<string> steps = Tokenize(path);
            if (steps.Count == 0) { error = "could not parse the path"; return null; }
            if (steps.Count > MaxSteps) { error = "path too long"; return null; }

            if (!TryResolveRoot(steps, out object current, out int startIndex, out _))
            {
                error = "unknown root - start from Campaign.Current, Hero.MainHero, Clan.PlayerClan, "
                        + "MobileParty.MainParty, or type:Full.Type.Name";
                return null;
            }

            for (int i = startIndex; i < steps.Count; i++)
            {
                string step = steps[i];

                if (current == null)
                {
                    error = "the path went null before '" + step + "'";
                    return null;
                }

                if (step.StartsWith("[") && step.EndsWith("]"))
                {
                    if (!TryIndex(current, step, out object indexed, out string indexError))
                    {
                        error = indexError;
                        return null;
                    }
                    current = indexed;
                    continue;
                }

                if (!TryRead(current, step, out object next, out string readError))
                {
                    error = readError;
                    return null;
                }

                current = next;
            }

            return current;
        }

        // ------------------------------------------------------------------ member access

        private static bool TryRead(object target, string name, out object value, out string error)
        {
            value = null;
            error = null;

            Type type;
            object instance;

            if (target is StaticTypeHandle handle)
            {
                type = handle.Type;
                instance = null;
            }
            else
            {
                type = target.GetType();
                instance = target;
            }

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                                       | BindingFlags.Instance | BindingFlags.Static
                                       | BindingFlags.FlattenHierarchy;

            try
            {
                PropertyInfo property = type.GetProperty(name, flags);
                if (property != null && property.CanRead)
                {
                    value = property.GetValue(property.GetGetMethod(true).IsStatic ? null : instance);
                    return true;
                }

                FieldInfo field = type.GetField(name, flags);
                if (field != null)
                {
                    value = field.GetValue(field.IsStatic ? null : instance);
                    return true;
                }

                // A method exists but is deliberately not callable - say so plainly rather than
                // leaving the user to wonder whether the name was wrong.
                if (type.GetMethod(name, flags) != null)
                {
                    error = $"'{name}' is a method. This inspector never invokes methods - it is "
                            + "read-only. Try .$type, .$members or .$count.";
                    return false;
                }

                error = $"no readable member '{name}'";
                return false;
            }
            catch (Exception ex)
            {
                error = $"reading '{name}' threw: {ex.InnerException?.Message ?? ex.Message}";
                return false;
            }
        }

        private static bool TryIndex(object target, string step, out object value, out string error)
        {
            value = null;
            error = null;

            string inner = step.Substring(1, step.Length - 2);
            if (!int.TryParse(inner, out int index))
            {
                error = "only numeric indexes are supported, got '" + inner + "'";
                return false;
            }

            if (target is IList list)
            {
                if (index < 0 || index >= list.Count)
                {
                    error = $"index {index} out of range (count {list.Count})";
                    return false;
                }
                value = list[index];
                return true;
            }

            if (target is IEnumerable enumerable)
            {
                int i = 0;
                foreach (object item in enumerable)
                {
                    if (i++ != index) continue;
                    value = item;
                    return true;
                }
                error = $"index {index} out of range";
                return false;
            }

            error = "not indexable";
            return false;
        }

        // ------------------------------------------------------------------ describing

        private static object[] Members(object target)
        {
            Type type = target is StaticTypeHandle handle ? handle.Type : target.GetType();

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                                       | BindingFlags.Instance | BindingFlags.Static
                                       | BindingFlags.FlattenHierarchy;

            var members = new List<MemberEntry>();

            try
            {
                foreach (PropertyInfo p in type.GetProperties(flags))
                {
                    if (p.GetIndexParameters().Length > 0) continue;   // indexers need arguments
                    members.Add(new MemberEntry { kind = "property", name = p.Name, type = Short(p.PropertyType) });
                }

                foreach (FieldInfo f in type.GetFields(flags))
                {
                    if (f.Name.Contains("<")) continue;                // compiler backing fields
                    members.Add(new MemberEntry { kind = "field", name = f.Name, type = Short(f.FieldType) });
                }
            }
            catch (Exception ex)
            {
                members.Add(new MemberEntry { kind = "error", name = ex.Message, type = string.Empty });
            }

            return members
                .OrderBy(m => m.name, StringComparer.OrdinalIgnoreCase)
                .Cast<object>()
                .ToArray();
        }

        /// <summary>Concrete rather than anonymous - `dynamic` needs Microsoft.CSharp, which is
        /// not referenced here, and sorting needs a real property to read.</summary>
        private sealed class MemberEntry
        {
            public string kind { get; set; }
            public string name { get; set; }
            public string type { get; set; }
        }

        private static int? CountOf(object value)
        {
            if (value is ICollection collection) return collection.Count;
            if (value is IEnumerable enumerable)
            {
                int n = 0;
                foreach (object _ in enumerable) n++;
                return n;
            }
            return null;
        }

        /// <summary>
        /// Turns a live object into something safe to serialize: named game objects become a small
        /// identity record, collections become a count plus a capped preview, everything else
        /// becomes its type and its ToString.
        /// </summary>
        public static object Render(object value)
        {
            if (value == null) return null;

            if (value is string || value.GetType().IsPrimitive || value is decimal) return value;
            if (value is StaticTypeHandle handle) return new { type = handle.Type.FullName, kind = "static type" };

            if (value is MBObjectBase mb)
            {
                return new
                {
                    type = mb.GetType().FullName,
                    stringId = mb.StringId,
                    name = SafeName(mb)
                };
            }

            if (value is IEnumerable enumerable && !(value is string))
            {
                var preview = new List<object>();
                int total = 0;

                foreach (object item in enumerable)
                {
                    total++;
                    if (preview.Count < MaxListPreview) preview.Add(Render(item));
                }

                return new
                {
                    type = value.GetType().Name,
                    count = total,
                    truncated = total > preview.Count,
                    items = preview
                };
            }

            return new { type = value.GetType().FullName, value = SafeToString(value) };
        }

        private static string SafeName(MBObjectBase mb)
        {
            try
            {
                PropertyInfo name = mb.GetType().GetProperty("Name");
                return name?.GetValue(mb)?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static string SafeToString(object value)
        {
            try { return value.ToString(); }
            catch (Exception ex) { return "<ToString threw: " + ex.Message + ">"; }
        }

        private static string Short(Type type) => type?.Name ?? "?";

        // ------------------------------------------------------------------ parsing

        /// <summary>Splits "A.B[2].C" into A, B, [2], C.</summary>
        private static List<string> Tokenize(string path)
        {
            var steps = new List<string>();
            var current = new System.Text.StringBuilder();

            foreach (char c in path.Trim())
            {
                if (c == '.')
                {
                    if (current.Length > 0) { steps.Add(current.ToString()); current.Clear(); }
                }
                else if (c == '[')
                {
                    if (current.Length > 0) { steps.Add(current.ToString()); current.Clear(); }
                    current.Append(c);
                }
                else if (c == ']')
                {
                    current.Append(c);
                    steps.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }

            if (current.Length > 0) steps.Add(current.ToString());
            return steps;
        }
    }
}
