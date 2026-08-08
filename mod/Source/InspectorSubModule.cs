using System;
using System.IO;
using HarmonyLib;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordInspector
{
    /// <summary>
    /// Entry point.
    ///
    /// A read-only window into a running campaign, served over loopback so an external tool can ask
    /// the game questions that static analysis cannot answer: which model class actually wins when
    /// three mods subclass the same one, whether a given hero really has no faith, whether the
    /// player is captive in vanilla terms.
    ///
    /// Two safety properties hold by construction rather than by discipline:
    ///   - the server speaks GET and nothing else, so there is no verb that could change the game;
    ///   - the path evaluator reads properties and fields but never invokes a method.
    ///
    /// And the important one: nothing touches campaign state off the main thread. Requests queue,
    /// and <see cref="OnApplicationTick"/> runs them where it is safe to.
    /// </summary>
    public class InspectorSubModule : MBSubModuleBase
    {
        private const string HarmonyId = "oai.bannerlord.inspector";

        private HttpServer _server;
        private Harmony _harmony;
        private bool _started;

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();

            try
            {
                InspectorLog.Info("=== Bannerlord Inspector loading ===");
                InspectorConfig.Reload();

                if (!InspectorConfig.Enabled)
                {
                    InspectorLog.Info("Disabled in config.txt - not starting the server.");
                    return;
                }

                _server = new HttpServer(InspectorConfig.Port, Router.Handle);
                _server.Start();
                _started = true;

                // Armed BEFORE anything else runs, and deliberately not behind a "start recording"
                // command. The exceptions worth catching are the ones thrown during load and during
                // the first minute of a campaign, and a recorder you have to remember to switch on
                // is switched off exactly when the interesting failure happens.
                if (InspectorConfig.CollectErrors)
                {
                    ErrorCollector.Arm();
                }

                // Phase timing is attached HERE, at load, before any campaign exists - never later.
                // Patching a live method mid-session re-JITs it underneath whatever is calling it,
                // and a diagnostic is not worth that risk.
                if (InspectorConfig.MeasureCampaignPhases)
                {
                    _harmony = new Harmony(HarmonyId);
                    CampaignPhasePatches.Apply(_harmony);
                }
            }
            catch (Exception ex)
            {
                // A failure here must never stop the game from loading.
                InspectorLog.Error("Could not start the inspector. The game is unaffected.", ex);
            }
        }

        /// <summary>
        /// The main thread. Every queued request runs here, which is the whole reason this class
        /// exists - see <see cref="MainThreadDispatcher"/>.
        /// </summary>
        protected override void OnApplicationTick(float dt)
        {
            base.OnApplicationTick(dt);

            if (!_started) return;

            // The heartbeat goes FIRST and unconditionally. It is what proves the main thread is
            // alive, so it must not sit behind anything that could throw or block - otherwise a
            // hang would look identical to the inspector simply having stopped.
            Heartbeat.Beat();

            // Frame timing rides with the heartbeat: same call site, same thread, and it is the
            // only place that sees every frame. A single reading of "time since last tick" turned
            // out to be worthless as an FPS measure - 5 ms and 17 ms seconds apart - so this keeps
            // the whole distribution instead.
            PerformanceMonitor.RecordFrame();

            MainThreadDispatcher.Pump();

            // Watches sample here too - same thread, same safety, and it costs nothing when no
            // watch is registered.
            Watcher.Tick(dt);

            // The world census walks a bounded slice per frame. It used to run whole inside a
            // request and produced the worst frames in the session - 728 ms - which corrupted its
            // own measurements. Spread out, it disappears into the frame budget.
            WorldCensus.Tick();
        }

        protected override void OnSubModuleUnloaded()
        {
            base.OnSubModuleUnloaded();

            try
            {
                ErrorCollector.Disarm();

                _server?.Dispose();
                _server = null;
                _started = false;

                if (_harmony != null)
                {
                    _harmony.UnpatchAll(HarmonyId);
                    _harmony = null;
                }
            }
            catch (Exception ex)
            {
                InspectorLog.Error("Shutdown failed.", ex);
            }
        }
    }

    /// <summary>Plain key=value config, read once at load.</summary>
    public static class InspectorConfig
    {
        public static bool Enabled = true;
        public static int Port = 8420;

        /// <summary>
        /// Allow /call to invoke question-shaped methods (Get/Is/Has/Can/Find...). Off makes the
        /// read-only guarantee absolute; on makes half of what a mod knows about itself reachable.
        /// See QueryInvoker for exactly what is and is not permitted.
        /// </summary>
        public static bool AllowQueryMethods = true;

        /// <summary>
        /// Time the campaign's tick dispatchers, so /perf can say which phase eats the frame.
        /// Costs a Stopwatch timestamp pair per dispatched tick. On by default because a profiler
        /// you have to remember to switch on is off exactly when you need it.
        /// </summary>
        public static bool MeasureCampaignPhases = true;

        /// <summary>
        /// Record every exception the process throws, grouped, so /errors can answer "did anything
        /// go wrong when I did that". Costs a dictionary lookup per throw and formats a stack once
        /// per distinct exception. See <see cref="ErrorCollector"/> for the ceilings.
        /// </summary>
        public static bool CollectErrors = true;

        public static void Reload()
        {
            string path = Path.Combine(BasePath.Name + "Modules", "BannerlordInspector", "config.txt");

            try
            {
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, string.Join("\n", new[]
                    {
                        "# Bannerlord Inspector - a read-only window into the running campaign.",
                        "# Serves on 127.0.0.1 only, accepts GET only, and never invokes game methods.",
                        "",
                        "enabled=true",
                        "port=8420",
                        "",
                        "# Allow /call to invoke question-shaped methods (Get/Is/Has/Can/Find...).",
                        "# Names containing a mutating verb (Create, Set, Apply, Ensure...) are always",
                        "# refused, as are complex arguments. Set false to forbid all method calls.",
                        "allowQueryMethods=true",
                        "",
                        "# Time the campaign's tick dispatchers so /perf can say which phase eats the",
                        "# frame. Costs a timestamp pair per dispatched tick. Patched at load only,",
                        "# never while the game is running.",
                        "measureCampaignPhases=true",
                        "",
                        "# Record exceptions as they are thrown - including the ones a mod catches",
                        "# and swallows, which are invisible in the game's own log. Grouped by kind,",
                        "# capped, and read through /errors. Set false to record nothing.",
                        "collectErrors=true",
                        ""
                    }));
                    InspectorLog.Info("config.txt created with defaults.");
                    return;
                }

                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();

                    if (key == "enabled" && bool.TryParse(value, out bool enabled)) Enabled = enabled;
                    if (key == "port" && int.TryParse(value, out int port)) Port = port;
                    if (key == "allowQueryMethods" && bool.TryParse(value, out bool allow)) AllowQueryMethods = allow;
                    if (key == "measureCampaignPhases" && bool.TryParse(value, out bool measure)) MeasureCampaignPhases = measure;
                    if (key == "collectErrors" && bool.TryParse(value, out bool collect)) CollectErrors = collect;
                }
            }
            catch (Exception ex)
            {
                InspectorLog.Error("Could not read config.txt; using defaults.", ex);
            }
        }
    }
}
