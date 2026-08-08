using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TaleWorlds.Library;

namespace BannerlordInspector
{
    /// <summary>
    /// Reads the log files other mods write, from inside the game.
    ///
    /// The point is pairing. <see cref="ErrorCollector"/> says what was thrown; a mod's own log says
    /// what it believed it was doing at that moment. Separately they are two puzzles. Together they
    /// are usually the answer, and getting them together today means alt-tabbing out, hunting for a
    /// log path, and opening a file that may be 40 MB.
    ///
    /// Reads only, and only under the game's Modules folder: every path is resolved to a full path
    /// and rejected if it escapes that root. The server is loopback-only, but "loopback-only" is a
    /// reason not to worry about strangers, not a reason to let a GET read anywhere on the disk.
    /// </summary>
    public static class ModLogReader
    {
        private const int MaxTail = 500;
        private const int DefaultTail = 80;

        /// <summary>Files big enough that reading them whole would be a mistake.</summary>
        private const long StreamThreshold = 4L * 1024 * 1024;

        private static string ModulesRoot
        {
            get
            {
                try { return Path.GetFullPath(Path.Combine(BasePath.Name, "Modules")); }
                catch { return null; }
            }
        }

        /// <summary>Every log file under Modules, newest first - so "which one is live" is obvious.</summary>
        public static object List(string modFilter)
        {
            string root = ModulesRoot;
            if (root == null || !Directory.Exists(root))
            {
                return new { error = "cannot locate the Modules folder" };
            }

            var rows = new List<object>();

            try
            {
                foreach (string moduleDir in Directory.GetDirectories(root))
                {
                    string module = Path.GetFileName(moduleDir);

                    if (!string.IsNullOrEmpty(modFilter)
                        && module.IndexOf(modFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    foreach (string file in Files(moduleDir))
                    {
                        FileInfo info;
                        try { info = new FileInfo(file); }
                        catch { continue; }

                        rows.Add(new
                        {
                            module,
                            path = Relative(root, file),
                            sizeKb = Math.Round(info.Length / 1024.0, 1),
                            modified = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                            minutesAgo = (int)(DateTime.Now - info.LastWriteTime).TotalMinutes
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                return new { error = "could not enumerate module logs: " + ex.Message };
            }

            return new
            {
                note = "Log files under the Modules folder, most recently written first. Read one "
                       + "with file=<path>. A file written seconds ago is the one the running game "
                       + "is using.",
                count = rows.Count,
                files = rows
                    .OrderBy(r => (int)r.GetType().GetProperty("minutesAgo").GetValue(r))
                    .Take(60)
                    .ToArray()
            };
        }

        /// <summary>
        /// The tail of one file, optionally filtered. Tail rather than head because the interesting
        /// end of a log is always the recent end.
        /// </summary>
        public static object Read(string relativePath, int tail, string contains)
        {
            string root = ModulesRoot;
            if (root == null) return new { error = "cannot locate the Modules folder" };

            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return new { error = "give file=<path>", hint = "list them with /modlog" };
            }

            string full;
            try
            {
                full = Path.GetFullPath(Path.Combine(root, relativePath));
            }
            catch (Exception ex)
            {
                return new { error = "bad path: " + ex.Message };
            }

            // Containment check on the RESOLVED path, which is the only form that cannot be
            // tricked by ..\ or by a symlinked module folder.
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return new
                {
                    error = "refused - that path is outside the Modules folder",
                    why = "This route reads mod logs, not the disk."
                };
            }

            if (!File.Exists(full)) return new { error = "no such file", path = relativePath };

            int wanted = tail <= 0 ? DefaultTail : Math.Min(tail, MaxTail);

            try
            {
                var lines = TailLines(full, wanted, contains, out int scanned, out bool partial);

                var info = new FileInfo(full);

                return new
                {
                    note = contains == null
                        ? "Last lines of the file, oldest first."
                        : "Last matching lines, oldest first. Non-matching lines are not counted "
                          + "towards the limit.",
                    module = Module(root, full),
                    path = Relative(root, full),
                    sizeKb = Math.Round(info.Length / 1024.0, 1),
                    modified = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    filter = contains,
                    linesScanned = scanned,
                    partialScan = partial,
                    returned = lines.Count,
                    lines = lines.ToArray()
                };
            }
            catch (Exception ex)
            {
                return new { error = "could not read it: " + ex.Message, path = relativePath };
            }
        }

        /// <summary>
        /// Keeps only the last N matching lines in a ring, so memory is bounded by N and not by the
        /// file. Large files are read from near the end instead of from the start - a 40 MB log is
        /// normal for a chatty mod and reading all of it to show 80 lines would be absurd.
        /// </summary>
        private static List<string> TailLines(string path, int wanted, string contains,
            out int scanned, out bool partial)
        {
            scanned = 0;
            partial = false;

            var ring = new LinkedList<string>();

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (stream.Length > StreamThreshold)
                {
                    // Seek to a window near the end. The first line after a seek is usually a
                    // fragment, so it is dropped.
                    stream.Seek(-StreamThreshold, SeekOrigin.End);
                    partial = true;
                }

                using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    if (partial) reader.ReadLine();

                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        scanned++;

                        if (!string.IsNullOrEmpty(contains)
                            && line.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0) continue;

                        ring.AddLast(line.Length > 2000 ? line.Substring(0, 2000) + "...(truncated)" : line);
                        if (ring.Count > wanted) ring.RemoveFirst();
                    }
                }
            }

            return ring.ToList();
        }

        private static IEnumerable<string> Files(string moduleDir)
        {
            var found = new List<string>();

            foreach (string pattern in new[] { "*.log", "*.txt" })
            {
                try
                {
                    // Two levels is enough for every convention in the wild - <mod>\foo.log and
                    // <mod>\logs\foo.log - and stops this from walking a mod's whole asset tree.
                    found.AddRange(Directory.GetFiles(moduleDir, pattern, SearchOption.TopDirectoryOnly));

                    foreach (string sub in Directory.GetDirectories(moduleDir))
                    {
                        string name = Path.GetFileName(sub);
                        if (name.IndexOf("log", StringComparison.OrdinalIgnoreCase) < 0) continue;

                        found.AddRange(Directory.GetFiles(sub, pattern, SearchOption.TopDirectoryOnly));
                    }
                }
                catch
                {
                    // An unreadable module folder is not worth failing the whole listing over.
                }
            }

            return found;
        }

        private static string Relative(string root, string full)
        {
            return full.Length > root.Length + 1 ? full.Substring(root.Length + 1) : full;
        }

        private static string Module(string root, string full)
        {
            string relative = Relative(root, full);
            int slash = relative.IndexOf(Path.DirectorySeparatorChar);
            return slash > 0 ? relative.Substring(0, slash) : relative;
        }
    }
}
