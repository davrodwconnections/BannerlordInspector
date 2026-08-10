using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace BannerlordInspector
{
    /// <summary>
    /// The battle you are standing in: who is on which team, in which formation, and where you are
    /// among them.
    ///
    /// Everything else here inspects the campaign, which is where most questions live - but not all
    /// of them. "The enlisted soldier stands in a formation on his own one-man team rather than the
    /// lord's line" is a real open bug in TAOM, and it is unanswerable from the campaign layer and
    /// miserable to judge by eye: from inside a battle, a soldier standing near a line looks exactly
    /// like a soldier standing in it. The distinction is whether he shares the commander's Team and
    /// Formation, which is two integers nobody can see.
    ///
    /// So this reports the structure rather than the spectacle. It deliberately does not enumerate
    /// agents - a field battle has thousands, and a list of thousands answers nothing. It reports
    /// the teams, the formations inside them, and exactly where the player sits, which is the shape
    /// every "am I actually part of this army" question reduces to.
    ///
    /// Read-only like everything else: it reads properties and never issues an order.
    /// </summary>
    public static class BattleInspector
    {
        public static object Current()
        {
            Mission mission = Mission.Current;

            if (mission == null)
            {
                return new
                {
                    inBattle = false,
                    note = "No mission is running. This route reports a live battle, siege, arena "
                           + "fight or town scene - anything that is not the campaign map."
                };
            }

            var teams = new List<object>();
            object playerView = null;

            try
            {
                Agent player = mission.MainAgent;
                Team playerTeam = SafeGet(() => player?.Team);
                Formation playerFormation = SafeGet(() => player?.Formation);

                foreach (Team team in mission.Teams ?? Enumerable.Empty<Team>())
                {
                    if (team == null) continue;

                    var formations = new List<object>();

                    foreach (Formation formation in SafeGet(() => team.FormationsIncludingEmpty)
                                                    ?? Enumerable.Empty<Formation>())
                    {
                        if (formation == null) continue;

                        int units = SafeGet(() => formation.CountOfUnits);
                        if (units == 0 && !ReferenceEquals(formation, playerFormation)) continue;

                        formations.Add(new
                        {
                            index = SafeGet(() => (int)formation.FormationIndex),
                            // FormationIndex is the class: Infantry, Ranged, Cavalry, HorseArcher.
                            @class = SafeGet(() => formation.FormationIndex.ToString()),
                            units,
                            hasPlayer = ReferenceEquals(formation, playerFormation),
                            captain = SafeGet(() => formation.Captain?.Name?.ToString()),
                            playerIsCaptain = SafeGet(() => formation.Captain != null
                                                            && formation.Captain == player),
                            underPlayerCommand = SafeGet(() => formation.PlayerOwner != null)
                        });
                    }

                    teams.Add(new
                    {
                        side = SafeGet(() => team.Side.ToString()),
                        isPlayerTeam = ReferenceEquals(team, playerTeam),
                        isPlayerAlly = SafeGet(() => playerTeam != null && team.IsPlayerAlly),
                        activeAgents = SafeGet(() => team.ActiveAgents?.Count ?? 0),
                        formationsInUse = formations.Count,
                        formations = formations.ToArray()
                    });
                }

                playerView = PlayerView(mission, player, playerTeam, playerFormation);
            }
            catch (Exception ex)
            {
                return new { inBattle = true, error = "could not read the battle: " + ex.Message };
            }

            return new
            {
                inBattle = true,
                scene = SafeGet(() => mission.SceneName),
                mode = SafeGet(() => mission.Mode.ToString()),
                elapsedSeconds = Math.Round(SafeGet(() => (double)mission.CurrentTime), 1),
                isSiege = SafeGet(() => mission.IsSiegeBattle),
                isFieldBattle = SafeGet(() => mission.IsFieldBattle),
                teamCount = teams.Count,
                teams = teams.ToArray(),
                player = playerView
            };
        }

        /// <summary>
        /// Where the player actually sits in the structure, with the conclusion drawn rather than
        /// left as an exercise. A one-man formation and a shared formation are indistinguishable on
        /// screen and completely different bugs.
        /// </summary>
        private static object PlayerView(Mission mission, Agent player, Team team, Formation formation)
        {
            if (player == null)
            {
                return new
                {
                    present = false,
                    note = "No main agent. You are spectating, dead, or the mission has no player body."
                };
            }

            int formationUnits = SafeGet(() => formation?.CountOfUnits ?? 0);
            int teamAgents = SafeGet(() => team?.ActiveAgents?.Count ?? 0);

            var flags = new List<string>();

            // Every flag below assumes an army is supposed to be standing around the player. In a
            // town, an arena, a hideout conversation or any walk-around scene, having no formation
            // and no team is simply what being there looks like - and raising three anomalies for
            // walking into a tavern is how a check gets ignored on the day it is right.
            //
            // This route was written for a field-battle question and would have shipped flagging
            // every town visit as a bug. Caught by re-reading it rather than by running it, which
            // is luckier than it sounds: nobody would have believed it afterwards.
            bool armyExpected = SafeGet(() => mission.IsFieldBattle) || SafeGet(() => mission.IsSiegeBattle);

            if (!armyExpected)
            {
                flags.Add("Not a field battle or siege, so formation and team are not expected here - "
                          + "no conclusions drawn.");
            }
            else
            {
                if (formation == null)
                {
                    flags.Add("The player is in NO formation. He will not receive formation orders and "
                              + "will not move with any line.");
                }
                else if (formationUnits <= 1)
                {
                    flags.Add("The player is ALONE in his formation (" + formationUnits + " unit). He is "
                              + "not standing in anyone's line even if he looks like he is - this is the "
                              + "shape of the 'one-man formation' bug.");
                }

                if (team != null && teamAgents <= 1)
                {
                    flags.Add("The player's team has " + teamAgents + " active agent(s). He is on a team "
                              + "of his own rather than the commander's.");
                }
            }

            return new
            {
                present = true,
                name = SafeGet(() => player.Name),
                health = SafeGet(() => Math.Round((double)player.Health)),
                mounted = SafeGet(() => player.HasMount),
                team = new
                {
                    side = SafeGet(() => team?.Side.ToString()),
                    activeAgents = teamAgents,
                    isAttacker = SafeGet(() => team != null && team.Side == BattleSideEnum.Attacker)
                },
                formation = formation == null ? null : new
                {
                    index = SafeGet(() => (int)formation.FormationIndex),
                    @class = SafeGet(() => formation.FormationIndex.ToString()),
                    units = formationUnits,
                    captain = SafeGet(() => formation.Captain?.Name?.ToString()),
                    underPlayerCommand = SafeGet(() => formation.PlayerOwner != null)
                },
                commandsAnything = SafeGet(() => mission.PlayerTeam != null
                                                 && mission.PlayerTeam.PlayerOrderController != null
                                                 && mission.PlayerTeam.PlayerOrderController
                                                     .SelectedFormations.Count > 0),
                flags = flags.ToArray(),
                verdict = !armyExpected
                    ? "Not a battle - nothing to judge about placement here."
                    : flags.Count == 0
                        ? "The player is embedded in a formation with other troops - the normal case."
                        : "Something about the player's placement is irregular; see flags."
            };
        }

        private static T SafeGet<T>(Func<T> read)
        {
            try { return read(); }
            catch { return default; }
        }
    }
}
