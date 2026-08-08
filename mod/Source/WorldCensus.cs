using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace BannerlordInspector
{
    /// <summary>
    /// Counts the world a slice at a time, so asking never costs a frame.
    ///
    /// WHY THIS REPLACED THE DIRECT SCAN. The first version walked every party, settlement and hero
    /// inside one request, on the main thread. On this install that is over 11,000 objects with a
    /// property read or two each, and it showed up in its own measurements as the worst frames in
    /// the session - up to 728 ms. A diagnostic that causes a three-quarter-second freeze is worse
    /// than no diagnostic: it corrupts the very numbers it was built to report, and it did, twice.
    ///
    /// So the census now runs as a background chore on the main thread: a bounded slice per frame,
    /// publishing a complete snapshot when a full pass finishes. Reading it is then free - no
    /// iteration, no dispatcher, no waiting - which also means these routes keep answering while the
    /// game is busy.
    ///
    /// The trade is that numbers are a few seconds old, and every answer says how old. For a census
    /// of a world with five thousand parties, that is the right trade: nobody needs to know the
    /// party count to the exact frame.
    /// </summary>
    public static class WorldCensus
    {
        /// <summary>Objects examined per frame. Small enough to disappear into the frame budget.</summary>
        private const int SliceSize = 350;

        private sealed class Counts
        {
            public int Parties;
            public int Settlements;
            public int LivingHeroes;
            public int ActiveClans;
            public int ActiveKingdoms;

            public readonly Dictionary<string, int> PartyKinds = new Dictionary<string, int>(StringComparer.Ordinal);
            public readonly Dictionary<string, int> PartyFactions = new Dictionary<string, int>(StringComparer.Ordinal);

            public int Towns, Castles, Villages, Hideouts, OtherSettlements;
            public readonly Dictionary<string, int> HideoutFactions = new Dictionary<string, int>(StringComparer.Ordinal);

            public DateTime CompletedAt;
            public double PassMs;
        }

        // The last COMPLETE pass. Readers only ever see a finished snapshot, never a half-counted one.
        private static volatile Counts _published;

        // The pass being built right now.
        private static Counts _building;
        private static int _partyCursor, _settlementCursor;
        private static List<MobileParty> _partySnapshot;
        private static List<Settlement> _settlementSnapshot;
        private static DateTime _passStarted;

        private static long _passes;

        /// <summary>Called once per frame from the main thread. Does a bounded slice and returns.</summary>
        public static void Tick()
        {
            try
            {
                if (Campaign.Current == null)
                {
                    _building = null;
                    return;
                }

                if (_building == null) BeginPass();
                if (_building == null) return;

                int budget = SliceSize;

                // Parties first - the expensive part, and the one that matters most.
                while (budget > 0 && _partyCursor < _partySnapshot.Count)
                {
                    CountParty(_partySnapshot[_partyCursor++]);
                    budget--;
                }

                while (budget > 0 && _settlementCursor < _settlementSnapshot.Count)
                {
                    CountSettlement(_settlementSnapshot[_settlementCursor++]);
                    budget--;
                }

                if (_partyCursor >= _partySnapshot.Count && _settlementCursor >= _settlementSnapshot.Count)
                {
                    FinishPass();
                }
            }
            catch (Exception ex)
            {
                // A census must never be able to break a frame. Drop the pass and start over.
                InspectorLog.Error("World census slice failed; restarting the pass.", ex);
                _building = null;
            }
        }

        private static void BeginPass()
        {
            _partySnapshot = MobileParty.All?.ToList() ?? new List<MobileParty>();
            _settlementSnapshot = Settlement.All?.ToList() ?? new List<Settlement>();
            _partyCursor = 0;
            _settlementCursor = 0;
            _passStarted = DateTime.UtcNow;

            _building = new Counts
            {
                Parties = _partySnapshot.Count,
                Settlements = _settlementSnapshot.Count,

                // These are plain list counts, cheap enough to take up front.
                LivingHeroes = Hero.AllAliveHeroes?.Count ?? 0,
                ActiveClans = Clan.All?.Count(c => c != null && !c.IsEliminated) ?? 0,
                ActiveKingdoms = Kingdom.All?.Count(k => k != null && !k.IsEliminated) ?? 0
            };
        }

        private static void FinishPass()
        {
            _building.CompletedAt = DateTime.UtcNow;
            _building.PassMs = (DateTime.UtcNow - _passStarted).TotalMilliseconds;

            _published = _building;
            _building = null;
            _passes++;
        }

        private static void CountParty(MobileParty party)
        {
            if (party == null) return;

            string kind;
            try
            {
                if (party.IsMainParty) kind = "the player";
                else if (party.IsGarrison) kind = "garrison";
                else if (party.IsMilitia) kind = "militia";
                else if (party.IsCaravan) kind = "caravan";
                else if (party.IsVillager) kind = "villager";
                else if (party.IsBandit) kind = "bandit";
                else if (party.IsLordParty) kind = "lord party";
                else if (party.IsCustomParty) kind = "custom (mod-made)";
                else kind = "other";
            }
            catch
            {
                kind = "unreadable";
            }

            Bump(_building.PartyKinds, kind);

            try
            {
                string faction = party.MapFaction?.Name?.ToString();
                if (faction != null) Bump(_building.PartyFactions, faction);
            }
            catch
            {
                // Skip the faction for this one rather than losing the party.
            }
        }

        private static void CountSettlement(Settlement settlement)
        {
            if (settlement == null) return;

            try
            {
                if (settlement.IsTown) { _building.Towns++; return; }
                if (settlement.IsCastle) { _building.Castles++; return; }
                if (settlement.IsVillage) { _building.Villages++; return; }

                if (settlement.IsHideout)
                {
                    _building.Hideouts++;
                    Bump(_building.HideoutFactions, settlement.MapFaction?.Name?.ToString() ?? "(none)");
                    return;
                }

                _building.OtherSettlements++;
            }
            catch
            {
                _building.OtherSettlements++;
            }
        }

        private static void Bump(Dictionary<string, int> map, string key)
        {
            map.TryGetValue(key, out int n);
            map[key] = n + 1;
        }

        // ------------------------------------------------------------------ reading

        private static object NotReady()
        {
            int done = _partyCursor + _settlementCursor;
            int total = (_partySnapshot?.Count ?? 0) + (_settlementSnapshot?.Count ?? 0);

            return new
            {
                ready = false,
                note = "The first census pass has not finished yet. It runs a slice per frame so it "
                       + "never costs a frame - give it a couple of seconds and ask again.",
                progress = total == 0 ? "starting" : done + " / " + total
            };
        }

        private static object Age(Counts c) => new
        {
            secondsOld = Math.Round((DateTime.UtcNow - c.CompletedAt).TotalSeconds, 1),
            passTookMs = Math.Round(c.PassMs, 0),
            passes = _passes
        };

        public static object Scale()
        {
            Counts c = _published;
            if (c == null) return NotReady();

            return new
            {
                asOf = Age(c),
                parties = c.Parties,
                settlements = c.Settlements,
                livingHeroes = c.LivingHeroes,
                activeClans = c.ActiveClans,
                activeKingdoms = c.ActiveKingdoms,
                vanilla = new { parties = "600-800", settlements = "250-300", livingHeroes = "600-900" },
                note = "Campaign-map cost scales with parties above all else. The map runs on "
                       + "essentially one thread, so extra CPU cores do not rescue it."
            };
        }

        public static object Parties()
        {
            Counts c = _published;
            if (c == null) return NotReady();

            return new
            {
                asOf = Age(c),
                total = c.Parties,
                vanillaRoughly = "600-800",
                multipleOfVanilla = Math.Round(c.Parties / 700.0, 1) + "x",
                byKind = c.PartyKinds.OrderByDescending(kv => kv.Value)
                    .Select(kv => new { kind = kv.Key, count = kv.Value }).ToArray(),
                topFactions = c.PartyFactions.OrderByDescending(kv => kv.Value).Take(15)
                    .Select(kv => new { faction = kv.Key, parties = kv.Value }).ToArray(),
                note = "Every one of these is simulated and drawn every frame on the campaign map."
            };
        }

        public static object Settlements()
        {
            Counts c = _published;
            if (c == null) return NotReady();

            return new
            {
                asOf = Age(c),
                total = c.Settlements,
                vanillaRoughly = "250-300 plus about 30-40 hideouts",
                towns = c.Towns,
                castles = c.Castles,
                villages = c.Villages,
                hideouts = c.Hideouts,
                other = c.OtherSettlements,
                hideoutsByFaction = c.HideoutFactions.OrderByDescending(kv => kv.Value)
                    .Select(kv => new { faction = kv.Key, hideouts = kv.Value }).ToArray(),
                hideoutNote = "Hideouts matter beyond their number: each keeps respawning bandit "
                              + "parties, so they turn into a party count that comes back."
            };
        }
    }
}
