using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using TaleWorlds.Library;

namespace BannerlordInspector
{
    /// <summary>
    /// The crash bundles other mods write, read from inside the game.
    ///
    /// TAOM's crash reporter and BUTR's both drop a zip per incident, each holding a JSON report,
    /// the engine's own rgl_log, and the modlist at the time. They are genuinely good reports - and
    /// getting at one means leaving the game, finding the folder, opening an archive and picking
    /// through it, every single time.
    ///
    /// This is deliberately a READER and not a second crash reporter. Reimplementing what TAOM
    /// already does well would mean two systems disagreeing about the same crash, which is worse
    /// than one system in an awkward folder. What it adds is that the report and the live state are
    /// finally reachable through the same interface - and the engine's rgl_log turns out to be where
    /// the answer lives for the whole class of failure that never throws a managed exception.
    /// Missing texture data is the example that cost an evening: not one exception anywhere, and
    /// forty lines of "Unable to find data ..." sitting in a log nobody thought to open.
    ///
    /// Reads only, and only under the game folder.
    /// </summary>
    public static class CrashReports
    {
        private const int MaxLines = 400;

        /// <summary>Folders both reporters are known to use, newest file wins.</summary>
        private static IEnumerable<string> Roots()
        {
            string game;
            try { game = BasePath.Name; }
            catch { yield break; }

            yield return Path.Combine(game, "bin", "Win64_Shipping_Client", "Logs");
            yield return Path.Combine(game, "bin", "Win64_Shipping_Client", "crashes");
            yield return Path.Combine(game, "Modules", "TAOM", "Logs");
        }

        public static object List()
        {
            var rows = new List<object>();

            foreach (string root in Roots())
            {
                if (!Directory.Exists(root)) continue;

                string[] files;
                try { files = Directory.GetFiles(root, "*.zip"); }
                catch { continue; }

                foreach (string file in files)
                {
                    FileInfo info;
                    try { info = new FileInfo(file); }
                    catch { continue; }

                    rows.Add(new
                    {
                        name = Path.GetFileName(file),
                        folder = root,
                        sizeKb = Math.Round(info.Length / 1024.0, 1),
                        written = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        minutesAgo = (int)(DateTime.Now - info.LastWriteTime).TotalMinutes
                    });
                }
            }

            var ordered = rows
                .OrderBy(r => (int)r.GetType().GetProperty("minutesAgo").GetValue(r))
                .Take(30)
                .ToArray();

            return new
            {
                note = ordered.Length == 0
                    ? "No crash bundles found. Either nothing has crashed, or the reporters write "
                      + "somewhere this does not look."
                    : "Crash bundles, newest first. Read one with name=<file>. The rgl_log inside is "
                      + "where failures that never throw a managed exception show up - missing "
                      + "textures, failed asset loads, engine-level complaints.",
                count = ordered.Length,
                bundles = ordered
            };
        }

        /// <summary>
        /// Opens one bundle. Without an entry name it lists what is inside; with one it returns that
        /// entry's tail, filtered.
        /// </summary>
        public static object Read(string bundleName, string entry, string contains, int tail)
        {
            if (string.IsNullOrWhiteSpace(bundleName))
            {
                return new { error = "give name=<bundle>", hint = "list them with /crashes" };
            }

            // Name only, never a path: this route opens crash bundles, not arbitrary archives.
            string safe = Path.GetFileName(bundleName);
            string found = null;

            foreach (string root in Roots())
            {
                string candidate = Path.Combine(root, safe);
                if (File.Exists(candidate)) { found = candidate; break; }
            }

            if (found == null) return new { error = "no such bundle", name = safe };

            try
            {
                using (var archive = ZipFile.OpenRead(found))
                {
                    if (string.IsNullOrWhiteSpace(entry))
                    {
                        var entries = archive.Entries
                            .Select(e => (object)new
                            {
                                entry = e.FullName,
                                sizeKb = Math.Round(e.Length / 1024.0, 1)
                            })
                            .ToArray();

                        return new
                        {
                            bundle = safe,
                            note = "Pass entry=<name> to read one. rgl_log.txt is usually the "
                                   + "informative one for anything that did not throw.",
                            entryCount = entries.Length,
                            entries
                        };
                    }

                    ZipArchiveEntry target = archive.Entries
                        .FirstOrDefault(e => e.FullName.IndexOf(entry, StringComparison.OrdinalIgnoreCase) >= 0);

                    if (target == null)
                    {
                        return new
                        {
                            error = "no entry matching that",
                            bundle = safe,
                            available = archive.Entries.Select(e => e.FullName).Take(40).ToArray()
                        };
                    }

                    var ring = new LinkedList<string>();
                    int scanned = 0;
                    int wanted = tail <= 0 ? 120 : Math.Min(tail, MaxLines);

                    using (var reader = new StreamReader(target.Open(), Encoding.UTF8, true))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            scanned++;

                            if (!string.IsNullOrEmpty(contains)
                                && line.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0) continue;

                            ring.AddLast(line.Length > 1200 ? line.Substring(0, 1200) + "...(cut)" : line);
                            if (ring.Count > wanted) ring.RemoveFirst();
                        }
                    }

                    return new
                    {
                        bundle = safe,
                        entry = target.FullName,
                        filter = contains,
                        linesScanned = scanned,
                        returned = ring.Count,
                        lines = ring.ToArray()
                    };
                }
            }
            catch (Exception ex)
            {
                return new { error = "could not open it: " + ex.Message, bundle = safe };
            }
        }
    }
}
