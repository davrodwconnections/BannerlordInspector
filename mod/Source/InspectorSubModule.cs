using System;
using System.IO;
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
        private HttpServer _server;
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

            MainThreadDispatcher.Pump();

            // Watches sample here too - same thread, same safety, and it costs nothing when no
            // watch is registered.
            Watcher.Tick(dt);
        }

        protected override void OnSubModuleUnloaded()
        {
            base.OnSubModuleUnloaded();

            try
            {
                _server?.Dispose();
                _server = null;
                _started = false;
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
                }
            }
            catch (Exception ex)
            {
                InspectorLog.Error("Could not read config.txt; using defaults.", ex);
            }
        }
    }
}
