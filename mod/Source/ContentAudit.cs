using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.ObjectSystem;

namespace BannerlordInspector
{
    /// <summary>
    /// Audits the CONTENT a modlist produced, rather than the code that produced it.
    ///
    /// This is the class that exists because of total conversions. When a mod replaces every
    /// culture, troop tree and equipment roster in the game through XML, the failures are not
    /// exceptions - they are absences. An equipment roster whose id nobody matched leaves a lord
    /// standing in his underwear. A culture that ends up owning no settlement makes the engine's
    /// own lord-spawn code throw on the daily tick, days later, with a stack trace that names
    /// nothing to do with the culture.
    ///
    /// Both are invisible from the XML on disk, because the XML is not what runs: a malformed
    /// entry is silently dropped at load and the registry is the only truth. Both are trivially
    /// visible from here.
    ///
    /// Everything is read from the live object registry, so it works on any modlist without
    /// knowing anything about the mods in it.
    /// </summary>
    public static class ContentAudit
    {
        // ------------------------------------------------------------------ equipment

        /// <summary>
        /// Armour slots. A character with an empty Body is the "underwear character" that every
        /// total-conversion tester list opens with - and the reason it is worth a dedicated sweep
        /// is that finding it by eye means opening characters one at a time.
        /// </summary>
        private static readonly EquipmentIndex[] ArmourSlots =
        {
            EquipmentIndex.Head,
            EquipmentIndex.Body,
            EquipmentIndex.Leg,
            EquipmentIndex.Gloves,
            EquipmentIndex.Cape
        };

        private static readonly EquipmentIndex[] WeaponSlots =
        {
            EquipmentIndex.Weapon0,
            EquipmentIndex.Weapon1,
            EquipmentIndex.Weapon2,
            EquipmentIndex.Weapon3
        };

        /// <summary>
        /// Sweeps every registered character for missing equipment.
        ///
        /// Severity is deliberate rather than uniform: a naked body is a visible bug in every
        /// screenshot, an unarmed troop cannot fight, and a missing cape is cosmetic. Reporting
        /// all three at the same volume would bury the first two.
        /// </summary>
        public static object Equipment(string cultureFilter, string idFilter, bool heroesOnly,
            bool includeNonCombatants, int limit)
        {
            if (MBObjectManager.Instance == null)
            {
                return new { error = "no object registry yet - load a campaign first" };
            }

            IEnumerable characters = Registry(typeof(CharacterObject));
            if (characters == null) return new { error = "character registry not readable" };

            // This runs on the main thread and walks every registered character, which on a total
            // conversion is thousands with nine slot reads each. That is a real cost and this
            // project has been burned by it before - the first world census froze frames for 728 ms
            // and corrupted its own measurements. The sweep is kept whole because a sliced version
            // would report a moving target, so instead the cost is MEASURED and reported: an
            // occasional deliberate stutter is fine, an unmeasured one is how you end up chasing a
            // performance ghost that is your own diagnostic.
            var clock = System.Diagnostics.Stopwatch.StartNew();

            var naked = new List<Candidate>();
            var unarmed = new List<Candidate>();
            var partial = new List<Candidate>();
            var noRoster = new List<Candidate>();

            // Counted separately from the lists. The first version derived the counts from the
            // truncated lists, so asking with limit=5 reported "5 naked" and asking with limit=10
            // reported "10" - on the same world. A number that changes with how many rows you asked
            // for is not a number.
            int nakedTotal = 0, unarmedTotal = 0, partialTotal = 0, noRosterTotal = 0;
            int examined = 0, ignoredNonCombatant = 0;

            foreach (object entry in characters)
            {
                if (!(entry is CharacterObject character)) continue;

                string id = Safe(() => character.StringId) ?? "?";
                string culture = Safe(() => character.Culture?.StringId);

                if (!Matches(culture, cultureFilter)) continue;
                if (!Matches(id, idFilter)) continue;
                if (heroesOnly && !Safe(() => character.IsHero)) continue;

                // Non-combatants and templates are where this check drowns in its own output. A
                // villager carries no sword, a tournament template is a slot-filler that gets its
                // kit at runtime, and a goblin slave is meant to be in rags. Reporting them is
                // technically true and practically useless - and a detector people learn to ignore
                // has failed, however correct it is. Pass all=true to see them anyway.
                if (!includeNonCombatants && !IsCombatant(character))
                {
                    ignoredNonCombatant++;
                    continue;
                }

                examined++;

                Equipment battle = Safe(() => character.FirstBattleEquipment);
                if (battle == null)
                {
                    // No roster resolved at all. Distinct from "roster exists but is empty" -
                    // usually a roster id that nothing in the modlist defines.
                    noRosterTotal++;
                    noRoster.Add(Candidate.For(character, id, culture,
                        "no battle equipment roster resolved"));
                    continue;
                }

                bool bodyEmpty = IsEmpty(battle, EquipmentIndex.Body);
                var missingArmour = ArmourSlots.Where(s => IsEmpty(battle, s)).ToArray();
                bool anyWeapon = WeaponSlots.Any(s => !IsEmpty(battle, s));

                if (bodyEmpty)
                {
                    nakedTotal++;
                    naked.Add(Candidate.For(character, id, culture,
                        "no body armour - renders in underwear", missingArmour));
                }
                else if (!anyWeapon && !Safe(() => character.IsHero))
                {
                    // Heroes legitimately show up unarmed in civilian contexts; troops do not.
                    unarmedTotal++;
                    unarmed.Add(Candidate.For(character, id, culture, "no weapon in any slot"));
                }
                else if (missingArmour.Length > 0)
                {
                    partialTotal++;
                    partial.Add(Candidate.For(character, id, culture,
                        "incomplete armour (cosmetic unless it is a full set)", missingArmour));
                }
            }

            clock.Stop();

            return new
            {
                note = "Characters whose battle equipment has holes. Within each list the highest "
                       + "tier comes first, because a tier-6 elite with no armour is a bug and a "
                       + "tier-0 servant usually is not. 'noRoster' means no roster resolved at all - "
                       + "usually an id nothing in the modlist defines. 'partial' is mostly cosmetic "
                       + "and is listed last on purpose.",

                // Reported rather than hidden: this sweep costs a frame, and you should be able to
                // tell a stutter you caused from one the modlist caused.
                elapsedMs = clock.ElapsedMilliseconds,
                cost = clock.ElapsedMilliseconds > 200
                    ? "This sweep stalled the game for " + clock.ElapsedMilliseconds
                      + " ms. That is a visible stutter - expected for a whole-registry walk, but do "
                      + "not run it in a loop, and ignore any frame-time spike measured around now."
                    : null,

                examined,
                skippedAsNonCombatant = ignoredNonCombatant,
                skipNote = includeNonCombatants
                    ? "Showing non-combatants too (all=true)."
                    : "Villagers, townsfolk, notables and tournament templates were skipped - they are "
                      + "unarmoured and unarmed by design. Pass all=true to include them.",

                filters = new { culture = cultureFilter, id = idFilter, heroesOnly, all = includeNonCombatants },

                // Totals are of everything found, not of what fitted in the lists below.
                counts = new
                {
                    naked = nakedTotal,
                    noRoster = noRosterTotal,
                    unarmed = unarmedTotal,
                    partialArmour = partialTotal
                },
                truncated = nakedTotal > limit || unarmedTotal > limit
                            || partialTotal > limit || noRosterTotal > limit,

                naked = Top(naked, limit),
                noRoster = Top(noRoster, limit),
                unarmed = Top(unarmed, limit),
                partialArmour = Top(partial, limit)
            };
        }

        /// <summary>
        /// Kept as a class rather than an anonymous object so the lists can be sorted by tier after
        /// the sweep instead of being truncated in registry order - which is arbitrary, and was
        /// putting tournament templates ahead of a tier-6 elite.
        /// </summary>
        private sealed class Candidate
        {
            public int Tier;
            public object Row;

            public static Candidate For(CharacterObject character, string id, string culture,
                string problem, EquipmentIndex[] missing = null)
            {
                return new Candidate
                {
                    Tier = Safe(() => character.Tier),
                    Row = new
                    {
                        id,
                        name = Safe(() => character.Name?.ToString()),
                        culture,
                        isHero = Safe(() => character.IsHero),
                        tier = Safe(() => character.Tier),
                        occupation = Safe(() => character.Occupation.ToString()),
                        problem,
                        missingSlots = missing?.Select(s => s.ToString()).ToArray()
                    }
                };
            }
        }

        private static object[] Top(List<Candidate> candidates, int limit)
        {
            return candidates
                .OrderByDescending(c => c.Tier)
                .Take(limit > 0 ? limit : 25)
                .Select(c => c.Row)
                .ToArray();
        }

        /// <summary>
        /// Whether this character is supposed to be armed and armoured at all.
        ///
        /// Occupation is the honest signal: a Villager with no sword is correct content, and every
        /// culture has one, so including them means ten guaranteed findings on a healthy install.
        /// Tournament templates come through as NotAssigned and are filler that the tournament
        /// system equips at runtime.
        /// </summary>
        private static bool IsCombatant(CharacterObject character)
        {
            switch (Safe(() => character.Occupation))
            {
                case Occupation.NotAssigned:
                case Occupation.Villager:
                case Occupation.Townsfolk:
                case Occupation.Artisan:
                case Occupation.Merchant:
                case Occupation.Preacher:
                case Occupation.RuralNotable:
                case Occupation.Headman:
                case Occupation.GangLeader:
                    return false;

                // Prison guards stand in a scene doing nothing. Every culture has one, so including
                // them is a guaranteed finding per culture on a perfectly healthy install.
                case Occupation.PrisonGuard:
                    return false;

                default:
                    return Safe(() => character.StringId) is string id && !IsProp(id);
            }
        }

        /// <summary>
        /// Scene props masquerading as troops.
        ///
        /// Training dummies and weapon-practice targets are registered as Soldier characters with
        /// full occupation, so occupation alone cannot tell them apart from real troops - and there
        /// are enough of them to swamp everything else. Naming conventions are the only signal
        /// available, and they are consistent enough across modding to be worth using: this cut the
        /// unarmed count from 294 to the handful that are actually worth looking at.
        ///
        /// It is a heuristic, and a troop legitimately named "dummy" would be hidden by it. That
        /// trade is worth making - a list nobody reads finds nothing at all. Pass all=true to see
        /// everything.
        /// </summary>
        private static bool IsProp(string id)
        {
            return id.IndexOf("dummy", StringComparison.OrdinalIgnoreCase) >= 0
                   || id.IndexOf("practice", StringComparison.OrdinalIgnoreCase) >= 0
                   || id.IndexOf("template", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsEmpty(Equipment equipment, EquipmentIndex slot)
        {
            try
            {
                EquipmentElement element = equipment[slot];
                return element.Item == null;
            }
            catch
            {
                return false;   // unreadable is not the same as empty; do not invent a bug
            }
        }

        // ------------------------------------------------------------------ world integrity

        /// <summary>
        /// The data-shape problems that make the ENGINE throw, days of game time after the mistake
        /// was made.
        ///
        /// The headline one is a culture that owns no settlement. Vanilla's lord-spawn path does an
        /// unguarded "first settlement of this culture" and a culture with none makes it throw on
        /// the daily clan tick - a crash to desktop with no mod frame anywhere in the stack, hours
        /// into a campaign, from a data mistake made before the game ever started. It is exactly
        /// the class of bug that is free to detect and miserable to diagnose.
        /// </summary>
        public static object World()
        {
            if (Campaign.Current == null)
            {
                return new { error = "no campaign loaded - this reads live world state" };
            }

            var findings = new List<object>();

            // --- cultures ---------------------------------------------------
            var settlementsByCulture = new Dictionary<string, int>(StringComparer.Ordinal);
            var noCulture = new List<string>();
            var noOwner = new List<object>();

            foreach (Settlement settlement in Settlement.All ?? Enumerable.Empty<Settlement>())
            {
                string culture = Safe(() => settlement.Culture?.StringId);
                if (string.IsNullOrEmpty(culture))
                {
                    noCulture.Add(Safe(() => settlement.StringId) ?? "?");
                    continue;
                }

                settlementsByCulture.TryGetValue(culture, out int count);
                settlementsByCulture[culture] = count + 1;

                bool isVillage = Safe(() => settlement.IsVillage);
                if (!isVillage && Safe(() => settlement.OwnerClan) == null)
                {
                    // Naming it is the whole difference between "there is one somewhere" and a fix.
                    noOwner.Add(new
                    {
                        settlement = Safe(() => settlement.StringId),
                        name = Safe(() => settlement.Name?.ToString()),
                        culture,
                        type = Safe(() => settlement.IsTown) ? "town"
                             : Safe(() => settlement.IsCastle) ? "castle" : "other"
                    });
                }
            }

            // Who USES each culture matters as much as who owns land in it. A culture with no
            // settlement and no clan is dead weight left over from the base game; the same culture
            // with a clan in it is a live crash waiting for that clan to spawn a lord. Reporting
            // both at one severity was this check's first mistake, on its first real run.
            var clansByCulture = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var heroesByCulture = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (Clan clan in Clan.All ?? Enumerable.Empty<Clan>())
            {
                if (clan == null || Safe(() => clan.IsEliminated)) continue;
                if (Safe(() => clan.IsBanditFaction)) continue;

                string culture = Safe(() => clan.Culture?.StringId);
                if (string.IsNullOrEmpty(culture)) continue;

                if (!clansByCulture.TryGetValue(culture, out List<string> list))
                {
                    list = new List<string>();
                    clansByCulture[culture] = list;
                }

                list.Add(Safe(() => clan.Name?.ToString()) ?? Safe(() => clan.StringId));
            }

            foreach (Hero hero in Hero.AllAliveHeroes ?? Enumerable.Empty<Hero>())
            {
                string culture = Safe(() => hero.Culture?.StringId);
                if (string.IsNullOrEmpty(culture)) continue;

                heroesByCulture.TryGetValue(culture, out int count);
                heroesByCulture[culture] = count + 1;
            }

            var landless = new List<object>();
            var landlessUnused = new List<object>();
            var noBasicTroop = new List<object>();

            IEnumerable cultures = Registry(typeof(CultureObject));
            int cultureCount = 0;

            foreach (object entry in cultures ?? Enumerable.Empty<object>())
            {
                if (!(entry is CultureObject culture)) continue;

                string id = Safe(() => culture.StringId) ?? "?";
                bool bandit = Safe(() => culture.IsBandit);
                cultureCount++;

                settlementsByCulture.TryGetValue(id, out int owned);

                // Bandit cultures legitimately own nothing - they live in hideouts. Flagging them
                // would make this check noise on every install, vanilla included.
                if (owned == 0 && !bandit)
                {
                    clansByCulture.TryGetValue(id, out List<string> users);
                    heroesByCulture.TryGetValue(id, out int heroes);

                    var row = new
                    {
                        culture = id,
                        name = Safe(() => culture.Name?.ToString()),
                        isMainCulture = Safe(() => culture.IsMainCulture),
                        clansUsingIt = users?.Count ?? 0,
                        clans = users?.Take(6).ToArray(),
                        livingHeroes = heroes
                    };

                    // A clan carrying this culture is what turns a leftover into a hazard - and it
                    // is also a content bug in its own right, because that clan recruits and equips
                    // from a culture whose troops belong to a different game.
                    if ((users?.Count ?? 0) > 0) landless.Add(row);
                    else landlessUnused.Add(row);
                }

                if (Safe(() => culture.BasicTroop) == null && !bandit)
                {
                    noBasicTroop.Add(new { culture = id, name = Safe(() => culture.Name?.ToString()) });
                }
            }

            if (landless.Count > 0)
            {
                findings.Add(new
                {
                    severity = "high",
                    what = "culture owns no settlement, and a clan uses it",
                    why = "The engine's lord-spawn path takes the first settlement of a culture "
                          + "without checking there is one, so this can crash on a daily clan tick "
                          + "hours in, with nothing in the stack pointing at the culture. It is also "
                          + "a content bug on its own: the clans listed recruit troops and wear "
                          + "equipment from this culture, and a culture with no land in a converted "
                          + "map is usually a leftover from the base game that a clan was never "
                          + "moved off.",
                    count = landless.Count,
                    items = landless.ToArray()
                });
            }

            if (landlessUnused.Count > 0)
            {
                findings.Add(new
                {
                    severity = "low",
                    what = "culture owns no settlement, and nothing uses it",
                    why = "Dead weight rather than a hazard: no clan carries it, so nothing will "
                          + "try to spawn a lord of it. Normal in a total conversion, where base-game "
                          + "cultures survive in the registry after the map replaced their land.",
                    count = landlessUnused.Count,
                    items = landlessUnused.ToArray()
                });
            }

            if (noBasicTroop.Count > 0)
            {
                findings.Add(new
                {
                    severity = "high",
                    what = "culture has no basic troop",
                    why = "Recruitment resolves the culture's basic troop. Without one, villages of "
                          + "that culture offer nothing and some recruitment paths throw.",
                    count = noBasicTroop.Count,
                    items = noBasicTroop.ToArray()
                });
            }

            // --- clans and kingdoms ------------------------------------------
            var leaderless = new List<object>();
            int activeClans = 0;

            foreach (Clan clan in Clan.All ?? Enumerable.Empty<Clan>())
            {
                if (clan == null || Safe(() => clan.IsEliminated)) continue;
                activeClans++;

                // Bandit factions have no leader and no heroes BY DESIGN - they are spawn templates
                // for looter parties, not families. The first real run of this check reported all
                // ten of them as broken clans, which is exactly how a detector teaches people to
                // ignore it. Whether a finding is true matters less than whether it is worth
                // reading.
                if (Safe(() => clan.IsBanditFaction)) continue;

                if (Safe(() => clan.Leader) == null)
                {
                    leaderless.Add(new
                    {
                        clan = Safe(() => clan.StringId),
                        name = Safe(() => clan.Name?.ToString()),
                        kingdom = Safe(() => clan.Kingdom?.StringId),
                        heroes = Safe(() => clan.Heroes?.Count ?? 0)
                    });
                }
            }

            if (leaderless.Count > 0)
            {
                findings.Add(new
                {
                    severity = "medium",
                    what = "active clan with no leader",
                    why = "A leaderless clan is skipped by most AI decisions and its fiefs stop "
                          + "being governed. Usually the clan's lords all died or never spawned.",
                    count = leaderless.Count,
                    items = leaderless.Take(20).ToArray()
                });
            }

            var emptyKingdoms = new List<object>();
            foreach (Kingdom kingdom in Kingdom.All ?? Enumerable.Empty<Kingdom>())
            {
                if (kingdom == null || Safe(() => kingdom.IsEliminated)) continue;
                int clans = Safe(() => kingdom.Clans?.Count ?? 0);
                if (clans == 0)
                {
                    emptyKingdoms.Add(new
                    {
                        kingdom = Safe(() => kingdom.StringId),
                        name = Safe(() => kingdom.Name?.ToString())
                    });
                }
            }

            if (emptyKingdoms.Count > 0)
            {
                findings.Add(new
                {
                    severity = "medium",
                    what = "kingdom with no clans",
                    why = "It exists on the map and in diplomacy but has nobody in it. Either it was "
                          + "defined and never populated, or every clan left.",
                    count = emptyKingdoms.Count,
                    items = emptyKingdoms.ToArray()
                });
            }

            if (noOwner.Count > 0)
            {
                findings.Add(new
                {
                    severity = "medium",
                    what = "town or castle with no owner clan",
                    why = "Ownerless fiefs break garrison, tax and siege logic.",
                    count = noOwner.Count,
                    items = noOwner.Take(20).ToArray()
                });
            }

            if (noCulture.Count > 0)
            {
                findings.Add(new
                {
                    severity = "high",
                    what = "settlement with no culture",
                    why = "Culture drives troops, notables and equipment for the settlement. A null "
                          + "one throws the first time anything recruits there.",
                    count = noCulture.Count,
                    items = noCulture.Take(20).ToArray()
                });
            }

            return new
            {
                note = findings.Count == 0
                    ? "No structural data problems found. This checks shape, not balance - it cannot "
                      + "tell you a troop is too strong, only that something the engine will "
                      + "dereference is missing."
                    : "Data-shape problems, worst first. These are the ones that make the ENGINE "
                      + "throw, often long after the mistake and with nothing in the stack pointing "
                      + "at the cause.",
                world = new
                {
                    settlements = Settlement.All?.Count ?? 0,
                    cultures = cultureCount,
                    activeClans,
                    kingdoms = Kingdom.All?.Count ?? 0,
                    livingHeroes = Hero.AllAliveHeroes?.Count ?? 0
                },
                findingCount = findings.Count,
                findings = findings.ToArray()
            };
        }

        // ------------------------------------------------------------------ shared

        private static IEnumerable Registry(Type type)
        {
            try
            {
                if (MBObjectManager.Instance == null) return null;

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

        private static bool Matches(string value, string filter)
        {
            if (string.IsNullOrEmpty(filter)) return true;
            return value != null && value.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static T Safe<T>(Func<T> read)
        {
            try { return read(); }
            catch { return default; }
        }
    }
}
