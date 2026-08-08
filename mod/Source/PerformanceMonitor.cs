using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace BannerlordInspector
{
    /// <summary>
    /// Where the frame time actually goes.
    ///
    /// WHY THIS EXISTS. The inspector could already say "the game is ticking", but not how fast, and
    /// the number it did have - time since the last tick - turned out to be useless as a frame-time
    /// measure: two readings seconds apart gave 5 ms and 17 ms, which would be 200 and 59 FPS. A
    /// single sample of a jittery signal is not a measurement. So this keeps a rolling window of
    /// every frame delta and reports the distribution, where the shape of the tail matters more than
    /// the average: 60 FPS average with a 90 ms p99 feels far worse than a steady 45.
    ///
    /// PHASE TIMERS. Knowing the frame is slow does not say what made it slow. The campaign drives
    /// its work through a handful of dispatcher methods - hourly ticks, daily ticks, the per-party AI
    /// think - and <see cref="CampaignPhasePatches"/> times each one into here. That turns "the
    /// campaign map is slow" into "72% of it is the per-party AI think, called 5417 times an hour".
    ///
    /// COST. A Stopwatch timestamp is a few nanoseconds and the buffers are fixed arrays written by
    /// one thread. This is always on because a profiler you have to remember to enable is a profiler
    /// that is off when you need it. Nothing here allocates on the hot path.
    /// </summary>
    public static class PerformanceMonitor
    {
        private const int FrameWindow = 1024;

        private static readonly double[] FrameMs = new double[FrameWindow];
        private static int _frameIndex;
        private static long _framesSeen;

        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static double _lastFrameAt;

        /// <summary>Worst frames seen since the last reset, with what the game was doing.</summary>
        private static readonly List<Spike> Spikes = new List<Spike>();
        private const int MaxSpikes = 20;
        private const double SpikeMs = 50.0;

        private sealed class Spike
        {
            public double Ms;
            public string Context;
            public DateTime At;
        }

        // ------------------------------------------------------------------ phases

        private sealed class Phase
        {
            public long Calls;
            public double TotalMs;
            public double WorstMs;
        }

        private static readonly Dictionary<string, Phase> Phases =
            new Dictionary<string, Phase>(StringComparer.Ordinal);

        private static readonly object PhaseLock = new object();

        private static DateTime _since = DateTime.Now;

        // ------------------------------------------------------------------ recording

        /// <summary>Called once per frame from OnApplicationTick, on the main thread.</summary>
        public static void RecordFrame()
        {
            double now = Clock.Elapsed.TotalMilliseconds;

            if (_lastFrameAt > 0)
            {
                double delta = now - _lastFrameAt;

                FrameMs[_frameIndex] = delta;
                _frameIndex = (_frameIndex + 1) % FrameWindow;
                _framesSeen++;

                if (delta >= SpikeMs) RecordSpike(delta);
            }

            _lastFrameAt = now;
        }

        private static void RecordSpike(double ms)
        {
            try
            {
                lock (PhaseLock)
                {
                    Spikes.Add(new Spike { Ms = ms, Context = Heartbeat.LastPhase, At = DateTime.Now });

                    // Keep only the worst, so a long session does not drown the interesting ones.
                    if (Spikes.Count > MaxSpikes * 2)
                    {
                        var worst = Spikes.OrderByDescending(s => s.Ms).Take(MaxSpikes).ToList();
                        Spikes.Clear();
                        Spikes.AddRange(worst);
                    }
                }
            }
            catch
            {
                // Never let diagnostics break a frame.
            }
        }

        /// <summary>Called by the phase patches. Cheap enough for the per-party AI path.</summary>
        public static void RecordPhase(string name, double ms)
        {
            try
            {
                lock (PhaseLock)
                {
                    if (!Phases.TryGetValue(name, out Phase phase))
                    {
                        phase = new Phase();
                        Phases[name] = phase;
                    }

                    phase.Calls++;
                    phase.TotalMs += ms;
                    if (ms > phase.WorstMs) phase.WorstMs = ms;
                }
            }
            catch
            {
                // Same rule.
            }
        }

        public static void Reset()
        {
            lock (PhaseLock)
            {
                Phases.Clear();
                Spikes.Clear();
                _since = DateTime.Now;
            }

            Array.Clear(FrameMs, 0, FrameMs.Length);
            _frameIndex = 0;
            Interlocked.Exchange(ref _framesSeen, 0);
        }

        // ------------------------------------------------------------------ reporting

        public static object Report()
        {
            double[] samples;
            lock (PhaseLock)
            {
                samples = FrameMs.Where(v => v > 0).OrderBy(v => v).ToArray();
            }

            object frames;
            if (samples.Length < 10)
            {
                frames = new
                {
                    note = "Not enough frames sampled yet - let the game run for a few seconds and ask again.",
                    sampled = samples.Length
                };
            }
            else
            {
                double avg = samples.Average();
                frames = new
                {
                    sampled = samples.Length,
                    fpsAverage = Math.Round(1000.0 / avg, 1),
                    fpsFromMedian = Math.Round(1000.0 / Percentile(samples, 50), 1),
                    msAverage = Math.Round(avg, 2),
                    msBest = Math.Round(samples.First(), 2),
                    msMedian = Math.Round(Percentile(samples, 50), 2),
                    msP95 = Math.Round(Percentile(samples, 95), 2),
                    msP99 = Math.Round(Percentile(samples, 99), 2),
                    msWorst = Math.Round(samples.Last(), 2),
                    note = "p95/p99 are the stutter. An average of 60 FPS with a 90 ms p99 feels worse "
                           + "than a steady 45."
                };
            }

            object[] phases;
            object[] spikes;

            lock (PhaseLock)
            {
                double totalPhaseMs = Phases.Values.Sum(p => p.TotalMs);

                phases = Phases
                    .OrderByDescending(kv => kv.Value.TotalMs)
                    .Select(kv => (object)new
                    {
                        phase = kv.Key,
                        totalMs = Math.Round(kv.Value.TotalMs, 1),
                        shareOfMeasured = totalPhaseMs <= 0
                            ? "0%"
                            : Math.Round(100.0 * kv.Value.TotalMs / totalPhaseMs, 1) + "%",
                        calls = kv.Value.Calls,
                        avgMs = kv.Value.Calls == 0 ? 0 : Math.Round(kv.Value.TotalMs / kv.Value.Calls, 4),
                        worstMs = Math.Round(kv.Value.WorstMs, 2)
                    })
                    .ToArray();

                spikes = Spikes
                    .OrderByDescending(s => s.Ms)
                    .Take(MaxSpikes)
                    .Select(s => (object)new
                    {
                        ms = Math.Round(s.Ms, 1),
                        atLocalTime = s.At.ToString("HH:mm:ss"),
                        doing = s.Context
                    })
                    .ToArray();
            }

            return new
            {
                measuringSince = _since.ToString("HH:mm:ss"),
                context = Heartbeat.LastContext,
                frames,
                campaignPhases = phases,
                phaseNote = phases.Length == 0
                    ? "No campaign phases recorded. Either no campaign is loaded, or the phase patches "
                      + "did not attach - check inspector.log."
                    : "Time inside the campaign's own tick dispatchers. 'calls' times 'avgMs' is where "
                      + "the cost is: a cheap method called 5000 times an hour beats an expensive one "
                      + "called twice.",
                worstFrames = spikes
            };
        }

        private static double Percentile(double[] sorted, int percentile)
        {
            if (sorted.Length == 0) return 0;

            double rank = (percentile / 100.0) * (sorted.Length - 1);
            int low = (int)Math.Floor(rank);
            int high = (int)Math.Ceiling(rank);

            if (low == high) return sorted[low];
            return sorted[low] + (rank - low) * (sorted[high] - sorted[low]);
        }
    }
}
