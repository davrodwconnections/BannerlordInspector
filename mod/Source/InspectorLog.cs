using System;
using System.IO;
using TaleWorlds.Library;

namespace BannerlordInspector
{
    /// <summary>Append-only log under the module folder. Never allowed to throw.</summary>
    public static class InspectorLog
    {
        private const long MaxBytes = 2 * 1024 * 1024;

        private static readonly object Sync = new object();
        private static string _path;

        private static string Path_
        {
            get
            {
                if (_path == null)
                {
                    _path = System.IO.Path.Combine(
                        BasePath.Name + "Modules", "BannerlordInspector", "logs", "inspector.log");
                }
                return _path;
            }
        }

        public static void Info(string message) => Write("INFO ", message);
        public static void Warn(string message) => Write("WARN ", message);

        public static void Error(string message, Exception ex = null) =>
            Write("ERROR", ex == null ? message : message + " :: " + ex);

        private static void Write(string level, string message)
        {
            try
            {
                lock (Sync)
                {
                    string path = Path_;
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));

                    if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    {
                        File.WriteAllText(path, string.Empty);
                    }

                    File.AppendAllText(path,
                        "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + level + " " + message + "\n");
                }
            }
            catch
            {
                // Intentionally silent.
            }
        }
    }
}
