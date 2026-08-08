using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.ObjectSystem;

namespace BannerlordInspector
{
    /// <summary>
    /// Calls question-shaped methods, and refuses everything else.
    ///
    /// The path evaluator deliberately invokes nothing, and that is the right default - but it left
    /// real questions unanswerable. "Does this hero have a Banner Kings faith?" lives behind
    /// ReligionsManager.GetHeroReligion(hero); there is no field to read. Half of what a mod knows
    /// about itself is behind a getter method.
    ///
    /// So this is the one place where the read-only guarantee is enforced by judgement instead of
    /// by construction, and it is fenced accordingly:
    ///
    ///   - the method name must START with a question verb (Get, Is, Has, Can, Find, Count, ...)
    ///   - the name must not contain a mutating fragment (Create, Set, Add, Apply, Ensure, ...),
    ///     which is what stops GetOrCreateX - the genuinely dangerous shape hiding behind a Get
    ///   - at most 3 parameters, all simple: string, number, bool, enum, or a game object looked up
    ///     by its StringId
    ///   - no ref or out parameters, no generics
    ///   - every call is written to the log, so there is always a record of what was invoked
    ///
    /// It can still be switched off entirely with allowQueryMethods=false in config.txt, for anyone
    /// who wants the guarantee to be absolute rather than merely careful.
    ///
    /// Honest caveat: a getter can still compute, cache, or lazily build something. "Query method"
    /// means "asks a question", not "provably has no effect".
    /// </summary>
    public static class QueryInvoker
    {
        private static readonly string[] AllowedPrefixes =
        {
            "Get", "Is", "Has", "Can", "Are", "Find", "Count", "Calculate", "Compute",
            "Describe", "Query", "Lookup", "Contains", "Should", "Will", "Does"
        };

        /// <summary>
        /// Checked against the WHOLE name, not the prefix. "GetOrCreateSettlement" passes the
        /// prefix test and is exactly what must not run.
        /// </summary>
        private static readonly string[] ForbiddenFragments =
        {
            "Create", "Set", "Add", "Remove", "Delete", "Destroy", "Apply", "Execute", "Ensure",
            "Init", "Start", "Stop", "Kill", "Give", "Take", "Change", "Update", "Grant", "Revoke",
            "Clear", "Reset", "Force", "Write", "Save", "Load", "Spawn", "Register", "Consume",
            "Trigger", "Fire", "Invoke", "Build", "Make", "Generate", "Refresh", "Recalculate"
        };

        public static bool LooksQueryable(MethodInfo method)
        {
            try
            {
                if (method == null || method.IsSpecialName) return false;
                if (method.IsGenericMethod || method.ContainsGenericParameters) return false;
                if (method.ReturnType == typeof(void)) return false;

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length > 3) return false;
                if (parameters.Any(p => p.ParameterType.IsByRef || p.IsOut)) return false;
                if (parameters.Any(p => !IsSimple(p.ParameterType))) return false;

                string name = method.Name;
                if (!AllowedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal))) return false;

                // Compare WHOLE PascalCase words, not substrings. A plain substring search rejects
                // "GetSettlement" because "Settlement" contains "Set", and "IsSettlementOwner" for
                // the same reason - both are exactly the kind of harmless question this exists to
                // allow. Splitting on capitals keeps "GetOrCreateX" blocked while letting those through.
                foreach (string word in SplitWords(name))
                {
                    if (ForbiddenFragments.Contains(word, StringComparer.Ordinal)) return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Resolve a target from a path (or a static type), find the named method, coerce the
        /// arguments, invoke, and render the result the same way /eval renders values.
        /// </summary>
        public static object Call(string targetPath, string methodName, string rawArgs)
        {
            if (!InspectorConfig.AllowQueryMethods)
            {
                return new
                {
                    error = "query methods are disabled",
                    hint = "Set allowQueryMethods=true in Modules/BannerlordInspector/config.txt."
                };
            }

            if (string.IsNullOrWhiteSpace(methodName)) return new { error = "give a method name" };

            object target;
            Type type;

            object resolved = PathEvaluator.ResolveTarget(targetPath, out string resolveError);
            if (resolveError != null) return new { error = resolveError, path = targetPath };

            if (resolved is PathEvaluator.StaticTypeHandle handle)
            {
                type = handle.Type;
                target = null;
            }
            else if (resolved == null)
            {
                return new { error = "the path resolved to null, so there is nothing to call on", path = targetPath };
            }
            else
            {
                target = resolved;
                type = resolved.GetType();
            }

            string[] args = string.IsNullOrEmpty(rawArgs)
                ? new string[0]
                : rawArgs.Split('|');

            MethodInfo method = FindMethod(type, methodName, args.Length, out string findError);
            if (method == null)
            {
                return new
                {
                    error = findError,
                    type = type.FullName,
                    method = methodName,
                    hint = "Use /members?type=<full name> and look for methods marked queryable."
                };
            }

            if (!LooksQueryable(method))
            {
                return new
                {
                    error = "refused - this is not a question-shaped method",
                    method = type.FullName + "." + method.Name,
                    why = "Only Get/Is/Has/Can/Find-style methods with simple parameters and no "
                          + "mutating verb in the name may be called. This inspector does not "
                          + "change the game."
                };
            }

            object[] coerced;
            try
            {
                coerced = Coerce(method.GetParameters(), args);
            }
            catch (Exception ex)
            {
                return new { error = "could not read the arguments: " + ex.Message, method = method.Name };
            }

            try
            {
                InspectorLog.Info($"CALL {type.FullName}.{method.Name}({string.Join(", ", args)})");
                object result = method.Invoke(method.IsStatic ? null : target, coerced);

                return new
                {
                    called = type.FullName + "." + method.Name,
                    args,
                    value = PathEvaluator.Render(result)
                };
            }
            catch (TargetInvocationException ex)
            {
                return new
                {
                    error = "the method threw",
                    detail = ex.InnerException?.Message ?? ex.Message,
                    called = type.FullName + "." + method.Name
                };
            }
            catch (Exception ex)
            {
                return new { error = "invocation failed: " + ex.Message };
            }
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>
        /// Splits a PascalCase identifier into its words. "GetOrCreateHero" -> Get, Or, Create,
        /// Hero. Runs of capitals stay together, so "GetNPCData" -> Get, NPC, Data rather than one
        /// letter per word.
        /// </summary>
        private static List<string> SplitWords(string name)
        {
            var words = new List<string>();
            if (string.IsNullOrEmpty(name)) return words;

            var current = new System.Text.StringBuilder();

            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];

                bool startsNewWord = char.IsUpper(c)
                                     && current.Length > 0
                                     && (!char.IsUpper(name[i - 1])
                                         || (i + 1 < name.Length && char.IsLower(name[i + 1])));

                if (startsNewWord)
                {
                    words.Add(current.ToString());
                    current.Clear();
                }

                current.Append(c);
            }

            if (current.Length > 0) words.Add(current.ToString());
            return words;
        }

        private static MethodInfo FindMethod(Type type, string name, int argCount, out string error)
        {
            error = null;

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                                       | BindingFlags.Instance | BindingFlags.Static
                                       | BindingFlags.FlattenHierarchy;

            MethodInfo[] candidates;
            try
            {
                candidates = type.GetMethods(flags)
                    .Where(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }
            catch (Exception ex)
            {
                error = "could not search methods: " + ex.Message;
                return null;
            }

            if (candidates.Length == 0)
            {
                error = "no method by that name";
                return null;
            }

            MethodInfo match = candidates.FirstOrDefault(m => m.GetParameters().Length == argCount);
            if (match == null)
            {
                error = $"no overload of '{name}' takes {argCount} argument(s). Available: "
                        + string.Join(", ", candidates.Select(m => m.GetParameters().Length.ToString()).Distinct());
                return null;
            }

            return match;
        }

        private static bool IsSimple(Type type)
        {
            if (type == typeof(string) || type.IsPrimitive || type.IsEnum) return true;
            return typeof(MBObjectBase).IsAssignableFrom(type);
        }

        /// <summary>
        /// Game objects are passed as their StringId, which is the only stable handle a caller has
        /// over HTTP: "GetHeroReligion" wants a Hero, and the caller writes "main_hero".
        /// </summary>
        private static object[] Coerce(ParameterInfo[] parameters, string[] args)
        {
            var values = new object[parameters.Length];

            for (int i = 0; i < parameters.Length; i++)
            {
                Type type = parameters[i].ParameterType;
                string raw = i < args.Length ? args[i] : null;

                if (raw == null) { values[i] = null; continue; }

                if (type == typeof(string)) { values[i] = raw; continue; }
                if (type.IsEnum) { values[i] = Enum.Parse(type, raw, true); continue; }

                if (typeof(MBObjectBase).IsAssignableFrom(type))
                {
                    object found = FindObject(type, raw);
                    if (found == null)
                    {
                        throw new ArgumentException($"no {type.Name} with StringId '{raw}'");
                    }
                    values[i] = found;
                    continue;
                }

                values[i] = Convert.ChangeType(raw, type, System.Globalization.CultureInfo.InvariantCulture);
            }

            return values;
        }

        /// <summary>
        /// The campaign's own collections, for the types MBObjectManager does not hold.
        ///
        /// Heroes, clans and kingdoms are campaign objects rather than managed objects. Looking them
        /// up through MBObjectManager returns null with no error, which reads as "that id does not
        /// exist" when the id is perfectly valid.
        /// </summary>
        private static object FindCampaignObject(Type type, string stringId)
        {
            try
            {
                bool Match(TaleWorlds.ObjectSystem.MBObjectBase o) =>
                    o != null && string.Equals(o.StringId, stringId, StringComparison.OrdinalIgnoreCase);

                if (typeof(TaleWorlds.CampaignSystem.Hero).IsAssignableFrom(type))
                {
                    foreach (var hero in TaleWorlds.CampaignSystem.Hero.AllAliveHeroes)
                    {
                        if (Match(hero)) return hero;
                    }
                    // Dead heroes still answer questions - a mod may well be asked about one.
                    foreach (var hero in TaleWorlds.CampaignSystem.Hero.DeadOrDisabledHeroes)
                    {
                        if (Match(hero)) return hero;
                    }
                    return null;
                }

                if (typeof(TaleWorlds.CampaignSystem.Clan).IsAssignableFrom(type))
                {
                    foreach (var clan in TaleWorlds.CampaignSystem.Clan.All)
                    {
                        if (Match(clan)) return clan;
                    }
                    return null;
                }

                if (typeof(TaleWorlds.CampaignSystem.Kingdom).IsAssignableFrom(type))
                {
                    foreach (var kingdom in TaleWorlds.CampaignSystem.Kingdom.All)
                    {
                        if (Match(kingdom)) return kingdom;
                    }
                    return null;
                }

                if (typeof(TaleWorlds.CampaignSystem.Settlements.Settlement).IsAssignableFrom(type))
                {
                    foreach (var settlement in TaleWorlds.CampaignSystem.Settlements.Settlement.All)
                    {
                        if (Match(settlement)) return settlement;
                    }
                    return null;
                }

                if (typeof(TaleWorlds.CampaignSystem.Party.MobileParty).IsAssignableFrom(type))
                {
                    foreach (var party in TaleWorlds.CampaignSystem.Party.MobileParty.All)
                    {
                        if (Match(party)) return party;
                    }
                    return null;
                }
            }
            catch
            {
                // No campaign, or a collection not ready yet - fall back to MBObjectManager.
            }

            return null;
        }

        /// <summary>
        /// Looks a game object up by StringId, which is the only stable handle a caller has over
        /// HTTP.
        ///
        /// MBObjectManager.GetObject&lt;T&gt;(string) is generic, so it has to be found by name and
        /// shape - AccessTools.Method with a parameter-type array does not match a generic method
        /// definition. If that route fails, fall back to scanning the type's own object list, which
        /// covers types whose registration the manager does not expose.
        /// </summary>
        private static object FindObject(Type type, string stringId)
        {
            // Campaign objects first. Hero, Clan and Kingdom are NOT registered with
            // MBObjectManager - they live in the campaign's own collections - so the generic
            // GetObject route below silently returns null for them. That is what made
            // GetHeroReligion('main_hero') fail with "no Hero with StringId 'main_hero'" while
            // Hero.MainHero.StringId plainly read back as "main_hero".
            object campaignObject = FindCampaignObject(type, stringId);
            if (campaignObject != null) return campaignObject;

            try
            {
                MethodInfo generic = typeof(MBObjectManager)
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GetObject"
                                         && m.IsGenericMethodDefinition
                                         && m.GetParameters().Length == 1
                                         && m.GetParameters()[0].ParameterType == typeof(string));

                if (generic != null && MBObjectManager.Instance != null)
                {
                    object found = generic.MakeGenericMethod(type)
                        .Invoke(MBObjectManager.Instance, new object[] { stringId });
                    if (found != null) return found;
                }
            }
            catch
            {
                // Fall through to the list scan.
            }

            try
            {
                MethodInfo listMethod = typeof(MBObjectManager)
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GetObjectTypeList"
                                         && m.IsGenericMethodDefinition
                                         && m.GetParameters().Length == 0);

                if (listMethod == null || MBObjectManager.Instance == null) return null;

                if (!(listMethod.MakeGenericMethod(type)
                        .Invoke(MBObjectManager.Instance, null) is IEnumerable<MBObjectBase> list)) return null;

                return list.FirstOrDefault(o =>
                    string.Equals(o?.StringId, stringId, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return null;
            }
        }
    }
}
