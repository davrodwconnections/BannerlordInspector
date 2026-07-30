using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;

namespace BannerlordInspector
{
    /// <summary>
    /// Samples values over time, so a question about *change* can be answered.
    ///
    /// Every other endpoint is a photograph. Some questions are not answerable from a photograph:
    /// "does the grudge actually stay personal after a battle?", "is this counter moving at all?",
    /// "did my relation drop when I hit him, or when he died?". Comparing two manual calls minutes
    /// apart is unreliable and easy to get wrong.
    ///
    /// So a watch registers a path, the game's own tick samples it on a fixed interval, and the
    /// history is readable afterwards. Sampling happens on the main thread with everything else, so
    /// it is as safe as any other read here.
    ///
    /// Deliberately modest: a handful of watches, a bounded history, and a floor on the interval.
    /// This is a diagnostic aid, not a telemetry system, and it must never be the reason a frame
    /// got slower.
    /// </summary>
    public static class Watcher
    {
        private const int MaxWatches = 8;
        private const int MaxSamples = 240;
        private const double MinIntervalSeconds = 0.5;

        private sealed class Watch
        {
            public string Path;
            public double IntervalSeconds;
            public double Elapsed;
            public readonly List<Sample> Samples = new List<Sample>();
            public string LastRendered;
        }

        private sealed class Sample
        {
            public string at { get; set; }
            public double campaignDay { get; set; }
            public string value { get; set; }
            public bool changed { get; set; }
        }

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, Watch> Watches =
            new Dictionary<string, Watch>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Start watching a path. Re-registering the same path resets its history.</summary>
        public static object Add(string path, double intervalSeconds)
        {
            if (string.IsNullOrWhiteSpace(path)) return new { error = "give a path to watch" };

            if (intervalSeconds < MinIntervalSeconds) intervalSeconds = MinIntervalSeconds;

            lock (Sync)
            {
                if (!Watches.ContainsKey(path) && Watches.Count >= MaxWatches)
                {
                    return new
                    {
                        error = $"too many watches (max {MaxWatches})",
                        hint = "Remove one with /watch/remove?path=..., or clear them all with /watch/clear."
                    };
                }

                Watches[path] = new Watch { Path = path, IntervalSeconds = intervalSeconds };
            }

            InspectorLog.Info($"WATCH + {path} every {intervalSeconds}s");

            return new
            {
                watching = path,
                everySeconds = intervalSeconds,
                note = "Samples are taken while the game runs. Read them back with /watch."
            };
        }

        public static object Remove(string path)
        {
            lock (Sync)
            {
                bool removed = Watches.Remove(path ?? string.Empty);
                return new { removed, path };
            }
        }

        public static object Clear()
        {
            lock (Sync)
            {
                int n = Watches.Count;
                Watches.Clear();
                return new { cleared = n };
            }
        }

        /// <summary>All watches with their history, newest sample last.</summary>
        public static object Report(string pathFilter)
        {
            lock (Sync)
            {
                var rows = Watches.Values
                    .Where(w => string.IsNullOrEmpty(pathFilter)
                                || w.Path.IndexOf(pathFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(w => new
                    {
                        path = w.Path,
                        everySeconds = w.IntervalSeconds,
                        samples = w.Samples.Count,
                        changes = w.Samples.Count(s => s.changed),
                        current = w.LastRendered,
                        history = w.Samples.ToArray()
                    })
                    .ToArray();

                return new
                {
                    count = rows.Length,
                    note = rows.Length == 0
                        ? "Nothing being watched. Add one with /watch/add?path=...&seconds=2"
                        : "'changes' counts samples whose value differed from the one before it.",
                    watches = rows
                };
            }
        }

        /// <summary>
        /// Called every frame on the main thread. Only paths whose interval has elapsed are read,
        /// and only a changed value costs anything to store beyond the sample itself.
        /// </summary>
        public static void Tick(float dt)
        {
            List<Watch> due = null;

            lock (Sync)
            {
                if (Watches.Count == 0) return;

                foreach (Watch watch in Watches.Values)
                {
                    watch.Elapsed += dt;
                    if (watch.Elapsed < watch.IntervalSeconds) continue;

                    watch.Elapsed = 0;
                    (due ?? (due = new List<Watch>())).Add(watch);
                }
            }

            if (due == null) return;

            foreach (Watch watch in due)
            {
                string rendered;
                try
                {
                    object value = PathEvaluator.ResolveTarget(watch.Path, out string error);
                    rendered = error != null ? "<" + error + ">" : Json.Serialize(PathEvaluator.Render(value));
                }
                catch (Exception ex)
                {
                    rendered = "<threw: " + ex.Message + ">";
                }

                lock (Sync)
                {
                    bool changed = watch.LastRendered != null && watch.LastRendered != rendered;

                    watch.Samples.Add(new Sample
                    {
                        at = DateTime.Now.ToString("HH:mm:ss"),
                        campaignDay = SafeDay(),
                        value = Truncate(rendered),
                        changed = changed
                    });

                    if (watch.Samples.Count > MaxSamples) watch.Samples.RemoveAt(0);
                    watch.LastRendered = rendered;
                }
            }
        }

        private static double SafeDay()
        {
            try { return Campaign.Current == null ? 0 : Math.Round(CampaignTime.Now.ToDays, 2); }
            catch { return 0; }
        }

        private static string Truncate(string value)
        {
            if (value == null) return null;
            return value.Length <= 400 ? value : value.Substring(0, 400) + "...";
        }
    }
}
