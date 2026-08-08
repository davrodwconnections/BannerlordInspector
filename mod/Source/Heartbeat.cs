using System;
using System.Collections.Generic;
using System.Threading;
using TaleWorlds.CampaignSystem;
using TaleWorlds.MountAndBlade;

namespace BannerlordInspector
{
    /// <summary>
    /// The part of the inspector that still works when the game does not.
    ///
    /// THE PROBLEM THIS SOLVES. Every other query routes through MainThreadDispatcher and waits for
    /// the game's tick, which is correct: campaign objects are not thread-safe. But it means that
    /// when the main thread hangs - a deadlock, an infinite loop - the inspector hangs with it and
    /// answers nothing. It goes silent at the exact moment it is most needed.
    ///
    /// So the main thread leaves a trail while it is still healthy: a timestamp every frame, plus
    /// short breadcrumbs of what it was doing. Both live in plain fields that a background thread
    /// can read without touching a single game object, without a lock, and without waiting for
    /// anything. When the game freezes, the trail is the last thing it managed to say.
    ///
    /// This is what would have answered the Emmon Cuy hang: the last breadcrumb before silence
    /// names the operation that never came back.
    ///
    /// Writes are deliberately trivial - a long, an int, a string reference - because this runs
    /// every frame and a diagnostic must never be the reason a frame got slower.
    /// </summary>
    public static class Heartbeat
    {
        /// <summary>Milliseconds without a tick before the game is considered hung.</summary>
        public const long HungThresholdMs = 4000;

        private const int TrailSize = 48;

        // Written only by the main thread, read by anyone. Volatile so a reader on another core
        // sees the latest value rather than a stale cached one.
        private static volatile int _tickCount;
        private static long _lastTickTicks;          // DateTime.UtcNow.Ticks of the last tick
        private static volatile string _lastPhase = "not started";

        // Ring buffer of breadcrumbs. Only the main thread writes; readers may see a torn view of
        // the newest entry, which is an acceptable trade for never blocking the game.
        private static readonly string[] Trail = new string[TrailSize];
        private static readonly long[] TrailAt = new long[TrailSize];
        private static int _trailIndex;

        // Last known campaign/mission context, refreshed on the tick. Gives a hang a location.
        private static volatile string _lastContext = "unknown";

        public static int TickCount => _tickCount;

        public static long MillisecondsSinceLastTick
        {
            get
            {
                long last = Interlocked.Read(ref _lastTickTicks);
                if (last == 0) return -1;
                return (long)(DateTime.UtcNow - new DateTime(last, DateTimeKind.Utc)).TotalMilliseconds;
            }
        }

        /// <summary>
        /// Loading legitimately blocks the tick for a long time - this modlist takes well over half
        /// a minute - so a plain "no tick for 4 seconds" verdict calls every load a freeze. It did,
        /// and a detector that cries wolf during every load is one you cannot trust on the day
        /// something really hangs.
        ///
        /// A load is recognisable: there is no campaign, and a worker thread is busy reading assets.
        /// So while no campaign is up, allow far longer before calling it hung.
        /// </summary>
        private const long LoadingThresholdMs = 90000;

        /// <summary>"healthy", "loading", or "hung" - the three states worth telling apart.</summary>
        public static string State
        {
            get
            {
                long since = MillisecondsSinceLastTick;
                if (since < 0) return "starting";

                bool inCampaign = false;
                try { inCampaign = Campaign.Current != null; } catch { }

                long threshold = inCampaign ? HungThresholdMs : LoadingThresholdMs;

                if (since <= threshold) return "healthy";
                return inCampaign ? "hung" : "loading";
            }
        }

        /// <summary>
        /// True only for a real freeze. Loading no longer counts - see <see cref="State"/>.
        /// </summary>
        public static bool LooksHung => State == "hung";

        public static string LastPhase => _lastPhase;
        public static string LastContext => _lastContext;

        /// <summary>
        /// Called from OnApplicationTick, every frame, on the main thread. Everything here must stay
        /// cheap: a timestamp, a counter, and - only once a second - a short context refresh.
        /// </summary>
        public static void Beat()
        {
            _tickCount++;
            Interlocked.Exchange(ref _lastTickTicks, DateTime.UtcNow.Ticks);

            // Refreshing the context means touching game objects, so do it sparingly rather than
            // 60 times a second.
            if ((_tickCount & 63) == 0) RefreshContext();
        }

        /// <summary>
        /// Note what the main thread is about to do. If the trail ends here, this is what hung.
        /// Called around each dispatched work item and at interesting lifecycle points.
        /// </summary>
        public static void Mark(string phase)
        {
            try
            {
                _lastPhase = phase;

                int index = _trailIndex++ & (TrailSize - 1);
                Trail[index] = phase;
                TrailAt[index] = DateTime.UtcNow.Ticks;
            }
            catch
            {
                // A diagnostic must never throw into the game's frame.
            }
        }

        /// <summary>Where the player was, as of the last healthy tick.</summary>
        private static void RefreshContext()
        {
            try
            {
                Mission mission = Mission.Current;
                if (mission != null)
                {
                    string scene = null;
                    try { scene = mission.SceneName; } catch { }

                    _lastContext = "mission: " + (scene ?? "?") + " / mode " + SafeMode(mission);
                    return;
                }

                if (Campaign.Current != null)
                {
                    _lastContext = "campaign map, day " + (int)CampaignTime.Now.ToDays;
                    return;
                }

                _lastContext = "menu (no campaign)";
            }
            catch (Exception ex)
            {
                _lastContext = "context unavailable: " + ex.GetType().Name;
            }
        }

        private static string SafeMode(Mission mission)
        {
            try { return mission.Mode.ToString(); }
            catch { return "?"; }
        }

        /// <summary>The trail, newest first, with age in milliseconds. Safe to call while hung.</summary>
        public static object[] Breadcrumbs()
        {
            var result = new List<object>();
            long now = DateTime.UtcNow.Ticks;

            // Walk backwards from the newest slot.
            int start = (_trailIndex - 1) & (TrailSize - 1);

            for (int step = 0; step < TrailSize; step++)
            {
                int index = (start - step) & (TrailSize - 1);
                string phase = Trail[index];
                if (phase == null) continue;

                long at = TrailAt[index];
                result.Add(new
                {
                    phase,
                    msAgo = at == 0 ? -1 : (long)TimeSpan.FromTicks(now - at).TotalMilliseconds
                });
            }

            return result.ToArray();
        }
    }
}
