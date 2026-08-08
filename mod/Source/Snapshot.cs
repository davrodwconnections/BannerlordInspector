using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace BannerlordInspector
{
    /// <summary>
    /// Takes a photograph of the campaign and later tells you what moved.
    ///
    /// This answers the question no other route here can: "what changed BESIDES the thing I was
    /// trying to change". A fix that works and quietly starts a war, kills a lord, or hands three
    /// villages to a different clan is not a fix, and nothing about playing for ten minutes makes
    /// that visible - the world is too big to hold in your head between two sessions.
    ///
    /// Snapshots are written to disk rather than kept in memory, because the workflow that matters
    /// spans a restart: photograph, quit, apply the fix, relaunch, compare.
    ///
    /// What it records is deliberately structural - who owns what, who is at war with whom, who is
    /// alive - and not every field of every object. A diff of everything is not a diff.
    /// </summary>
    public static class Snapshot
    {
        private const int MaxSnapshots = 20;
        private const int MaxListed = 40;

        private sealed class State
        {
            public string Name;
            public string TakenAt;
            public string CampaignId;
            public string GameDate;
            public double GameDays;

            public int Settlements;
            public int LivingHeroes;
            public int ActiveClans;
            public int Kingdoms;
            public int Parties;

            public int PlayerGold;
            public string PlayerClan;
            public string PlayerKingdom;

            /// <summary>settlement id -> owning clan id.</summary>
            public Dictionary<string, string> Owners = new Dictionary<string, string>(StringComparer.Ordinal);

            /// <summary>Sorted "kingdomA|kingdomB" pairs currently at war.</summary>
            public List<string> Wars = new List<string>();

            /// <summary>Living hero ids, so deaths and births are both visible.</summary>
            public List<string> Heroes = new List<string>();
        }

        private static string Folder
        {
            get
            {
                string path = Path.Combine(BasePath.Name + "Modules", "BannerlordInspector", "snapshots");
                Directory.CreateDirectory(path);
                return path;
            }
        }

        // ------------------------------------------------------------------ commands

        public static object Save(string name)
        {
            if (Campaign.Current == null) return new { error = "no campaign loaded" };

            name = Sanitise(name);
            if (name == null)
            {
                return new { error = "give a name", hint = "letters, digits, dash and underscore" };
            }

            State state;
            try { state = Capture(name); }
            catch (Exception ex) { return new { error = "could not capture: " + ex.Message }; }

            try
            {
                Prune();
                File.WriteAllText(PathFor(name), JsonConvert.SerializeObject(state, Formatting.Indented));
            }
            catch (Exception ex)
            {
                return new { error = "could not write it: " + ex.Message };
            }

            return new
            {
                saved = name,
                takenAt = state.TakenAt,
                gameDate = state.GameDate,
                contents = new
                {
                    settlements = state.Settlements,
                    livingHeroes = state.LivingHeroes,
                    activeClans = state.ActiveClans,
                    wars = state.Wars.Count
                },
                note = "Now go and do the thing - it survives a restart. Then: action=compare&name=" + name
            };
        }

        public static object List()
        {
            var rows = new List<object>();

            try
            {
                foreach (string file in Directory.GetFiles(Folder, "*.json"))
                {
                    State state = Load(Path.GetFileNameWithoutExtension(file));
                    if (state == null) continue;

                    rows.Add(new
                    {
                        name = state.Name,
                        takenAt = state.TakenAt,
                        gameDate = state.GameDate,
                        campaignId = state.CampaignId,
                        livingHeroes = state.LivingHeroes
                    });
                }
            }
            catch (Exception ex)
            {
                return new { error = "could not list them: " + ex.Message };
            }

            return new { count = rows.Count, snapshots = rows.ToArray() };
        }

        public static object Drop(string name)
        {
            name = Sanitise(name);
            if (name == null) return new { error = "give a name" };

            try
            {
                string path = PathFor(name);
                if (!File.Exists(path)) return new { error = "no such snapshot", name };

                File.Delete(path);
                return new { dropped = name };
            }
            catch (Exception ex)
            {
                return new { error = "could not delete it: " + ex.Message };
            }
        }

        /// <summary>Compares a saved snapshot against the campaign as it is right now.</summary>
        public static object Compare(string name)
        {
            if (Campaign.Current == null) return new { error = "no campaign loaded" };

            name = Sanitise(name);
            if (name == null) return new { error = "give a name" };

            State before = Load(name);
            if (before == null) return new { error = "no such snapshot", name, hint = "action=list" };

            State now;
            try { now = Capture("(now)"); }
            catch (Exception ex) { return new { error = "could not read current state: " + ex.Message }; }

            bool sameCampaign = string.Equals(before.CampaignId, now.CampaignId, StringComparison.Ordinal);

            // --- ownership ---------------------------------------------------
            var changedHands = new List<object>();
            foreach (var pair in before.Owners)
            {
                now.Owners.TryGetValue(pair.Key, out string current);
                if (!string.Equals(pair.Value, current, StringComparison.Ordinal))
                {
                    changedHands.Add(new { settlement = pair.Key, from = pair.Value, to = current ?? "(gone)" });
                }
            }

            // --- wars --------------------------------------------------------
            var beforeWars = new HashSet<string>(before.Wars, StringComparer.Ordinal);
            var nowWars = new HashSet<string>(now.Wars, StringComparer.Ordinal);

            string[] declared = nowWars.Except(beforeWars).ToArray();
            string[] ended = beforeWars.Except(nowWars).ToArray();

            // --- heroes ------------------------------------------------------
            var beforeHeroes = new HashSet<string>(before.Heroes, StringComparer.Ordinal);
            var nowHeroes = new HashSet<string>(now.Heroes, StringComparer.Ordinal);

            string[] died = beforeHeroes.Except(nowHeroes).ToArray();
            string[] appeared = nowHeroes.Except(beforeHeroes).ToArray();

            int changes = changedHands.Count + declared.Length + ended.Length + died.Length + appeared.Length;

            return new
            {
                note = changes == 0
                    ? "Nothing structural moved between the two. Counts below are still worth a look - "
                      + "a count that changed with no named change means something was created or "
                      + "destroyed that this does not track by id."
                    : "What moved between the snapshot and now. Deaths are the line to read first - "
                      + "a fix that kills lords is the classic silent regression.",
                sameCampaign,
                warning = sameCampaign
                    ? null
                    : "DIFFERENT CAMPAIGN. This snapshot came from another save, so the comparison "
                      + "is between two different worlds and means nothing.",

                from = new { before.Name, before.TakenAt, before.GameDate },
                to = new { now.TakenAt, now.GameDate, elapsedGameDays = Math.Round(now.GameDays - before.GameDays, 1) },

                counts = new[]
                {
                    Delta("settlements", before.Settlements, now.Settlements),
                    Delta("livingHeroes", before.LivingHeroes, now.LivingHeroes),
                    Delta("activeClans", before.ActiveClans, now.ActiveClans),
                    Delta("kingdoms", before.Kingdoms, now.Kingdoms),
                    Delta("parties", before.Parties, now.Parties),
                    Delta("playerGold", before.PlayerGold, now.PlayerGold)
                }.Where(d => d != null).ToArray(),

                changeCount = changes,
                settlementsChangedHands = changedHands.Take(MaxListed).ToArray(),
                warsDeclared = declared.Take(MaxListed).ToArray(),
                warsEnded = ended.Take(MaxListed).ToArray(),
                heroesDied = died.Take(MaxListed).ToArray(),
                heroesAppeared = appeared.Take(MaxListed).ToArray()
            };
        }

        // ------------------------------------------------------------------ capture

        private static State Capture(string name)
        {
            var state = new State
            {
                Name = name,
                TakenAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                CampaignId = Safe(() => Campaign.Current?.UniqueGameId),
                GameDate = Safe(() => CampaignTime.Now.ToString()),
                GameDays = Safe(() => CampaignTime.Now.ToDays),

                Settlements = Safe(() => Settlement.All?.Count ?? 0),
                LivingHeroes = Safe(() => Hero.AllAliveHeroes?.Count ?? 0),
                ActiveClans = Safe(() => Clan.All?.Count(c => c != null && !c.IsEliminated) ?? 0),
                Kingdoms = Safe(() => Kingdom.All?.Count(k => k != null && !k.IsEliminated) ?? 0),
                Parties = Safe(() => TaleWorlds.CampaignSystem.Party.MobileParty.All?.Count ?? 0),

                PlayerGold = Safe(() => Hero.MainHero?.Gold ?? 0),
                PlayerClan = Safe(() => Clan.PlayerClan?.StringId),
                PlayerKingdom = Safe(() => Clan.PlayerClan?.Kingdom?.StringId)
            };

            foreach (Settlement settlement in Settlement.All ?? Enumerable.Empty<Settlement>())
            {
                string id = Safe(() => settlement.StringId);
                if (string.IsNullOrEmpty(id)) continue;

                state.Owners[id] = Safe(() => settlement.OwnerClan?.StringId) ?? "(none)";
            }

            var kingdoms = (Kingdom.All ?? Enumerable.Empty<Kingdom>())
                .Where(k => k != null && !Safe(() => k.IsEliminated))
                .ToList();

            for (int i = 0; i < kingdoms.Count; i++)
            {
                for (int j = i + 1; j < kingdoms.Count; j++)
                {
                    bool atWar = Safe(() => kingdoms[i].IsAtWarWith(kingdoms[j]));
                    if (!atWar) continue;

                    string a = Safe(() => kingdoms[i].StringId) ?? "?";
                    string b = Safe(() => kingdoms[j].StringId) ?? "?";

                    // Sorted so the pair has one representation regardless of iteration order -
                    // otherwise every snapshot would "differ" from every other.
                    state.Wars.Add(string.CompareOrdinal(a, b) <= 0 ? a + "|" + b : b + "|" + a);
                }
            }

            state.Wars.Sort(StringComparer.Ordinal);

            foreach (Hero hero in Hero.AllAliveHeroes ?? Enumerable.Empty<Hero>())
            {
                string id = Safe(() => hero.StringId);
                if (!string.IsNullOrEmpty(id)) state.Heroes.Add(id);
            }

            state.Heroes.Sort(StringComparer.Ordinal);

            return state;
        }

        // ------------------------------------------------------------------ plumbing

        private static object Delta(string what, int before, int after)
        {
            if (before == after) return null;
            return new { what, before, after, change = after - before };
        }

        private static State Load(string name)
        {
            try
            {
                string path = PathFor(name);
                if (!File.Exists(path)) return null;

                return JsonConvert.DeserializeObject<State>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                InspectorLog.Error("Could not read snapshot '" + name + "'.", ex);
                return null;
            }
        }

        private static string PathFor(string name) => Path.Combine(Folder, name + ".json");

        /// <summary>
        /// Names become file names, so anything that could escape the folder is refused outright
        /// rather than escaped - there is no legitimate snapshot name that needs a slash.
        /// </summary>
        private static string Sanitise(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            name = name.Trim();
            if (name.Length > 40) name = name.Substring(0, 40);

            return name.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_') ? name : null;
        }

        /// <summary>Keeps the folder from growing without bound; oldest goes first.</summary>
        private static void Prune()
        {
            try
            {
                var files = Directory.GetFiles(Folder, "*.json")
                    .Select(f => new FileInfo(f))
                    .OrderBy(f => f.LastWriteTimeUtc)
                    .ToList();

                while (files.Count >= MaxSnapshots)
                {
                    files[0].Delete();
                    files.RemoveAt(0);
                }
            }
            catch
            {
                // Housekeeping failing must not stop a snapshot being taken.
            }
        }

        private static T Safe<T>(Func<T> read)
        {
            try { return read(); }
            catch { return default; }
        }
    }
}
