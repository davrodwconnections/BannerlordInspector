using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TaleWorlds.Library;

namespace BannerlordInspector
{
    /// <summary>
    /// Answers "which module does this assembly come from", and the reverse.
    ///
    /// Everything else here reasons in assemblies, which was fine while a mod meant one DLL in one
    /// folder. Total conversions broke that assumption: TAOM ships FORTY assemblies inside a single
    /// module - Harmony, ButterLib, MCM, UIExtenderEx, Serilog, the whole BUTR stack - while the
    /// module folders those libraries would normally live in are left as empty stubs, present so
    /// the launcher can satisfy other mods' dependencies but containing no binary at all.
    ///
    /// So on such an install "which mod owns this patch" and "what does this module actually bring"
    /// stop being the same question, and the second one had no answer at all. Asking for a dossier
    /// on TAOM.Dependencies reported only its own DLL, as if the other thirty-nine were not there.
    ///
    /// The mapping is by file path rather than by name, because names lie in both directions: the
    /// assembly inside Modules\Foo\ is frequently not called Foo, and an assembly called
    /// Bannerlord.ButterLib may be sitting inside a module called something else entirely - which is
    /// exactly the case this class exists for.
    /// </summary>
    public static class ModuleMap
    {
        private static readonly object Gate = new object();
        private static Dictionary<string, string> _assemblyToModule;   // assembly name -> module folder
        private static string _modulesRoot;

        private static string ModulesRoot
        {
            get
            {
                if (_modulesRoot == null)
                {
                    try { _modulesRoot = Path.GetFullPath(Path.Combine(BasePath.Name, "Modules")); }
                    catch { _modulesRoot = string.Empty; }
                }
                return _modulesRoot;
            }
        }

        /// <summary>
        /// The module folder an assembly was loaded from, or null when it did not come from one -
        /// engine assemblies, the framework, and anything loaded from outside Modules\.
        ///
        /// Rebuilt on demand rather than cached forever: modules can load assemblies late, and a
        /// map that was correct at startup would quietly go stale exactly when a late-loading mod
        /// is what you are looking for.
        /// </summary>
        public static string ForAssembly(Assembly assembly)
        {
            if (assembly == null) return null;

            string name;
            try { name = assembly.GetName().Name; }
            catch { return null; }

            Map().TryGetValue(name, out string module);
            return module;
        }

        public static string ForAssemblyName(string assemblyName)
        {
            if (string.IsNullOrEmpty(assemblyName)) return null;
            Map().TryGetValue(assemblyName, out string module);
            return module;
        }

        /// <summary>Every loaded assembly that came from the given module folder.</summary>
        public static List<Assembly> AssembliesOf(string module)
        {
            var result = new List<Assembly>();
            if (string.IsNullOrWhiteSpace(module)) return result;

            foreach (Assembly assembly in Loaded())
            {
                string owner = ForAssembly(assembly);
                if (owner != null && owner.Equals(module, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(assembly);
                }
            }

            return result;
        }

        /// <summary>
        /// Module folder names that currently have at least one assembly loaded, with the count.
        /// A module bringing many assemblies is the signature of a bundled dependency stack.
        /// </summary>
        public static List<KeyValuePair<string, int>> Modules()
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (string module in Map().Values)
            {
                counts.TryGetValue(module, out int n);
                counts[module] = n + 1;
            }

            return counts.OrderByDescending(p => p.Value)
                         .ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                         .ToList();
        }

        /// <summary>
        /// Resolves a name the caller typed to a module folder, tolerating case and partial matches
        /// so "taom.dep" finds TAOM.Dependencies. Returns null when nothing matches.
        /// </summary>
        public static string ResolveModule(string typed)
        {
            if (string.IsNullOrWhiteSpace(typed)) return null;

            var known = Map().Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            string exact = known.FirstOrDefault(m => m.Equals(typed, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;

            var partial = known
                .Where(m => m.IndexOf(typed, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(m => m.Length)
                .ToList();

            // One partial hit is a helpful shorthand; several is a guess, and guessing which module
            // the caller meant is how you answer a question nobody asked.
            return partial.Count == 1 ? partial[0] : null;
        }

        // ------------------------------------------------------------------ building

        private static Dictionary<string, string> Map()
        {
            lock (Gate)
            {
                // Cheap staleness check: assembly count is monotonic within a session, so a change
                // means something loaded since the last build.
                if (_assemblyToModule != null && _assemblyToModule.Count > 0)
                {
                    int loaded = Loaded().Count;
                    if (loaded == _lastAssemblyCount) return _assemblyToModule;
                }

                Build();
                return _assemblyToModule;
            }
        }

        private static int _lastAssemblyCount;

        private static void Build()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            List<Assembly> loaded = Loaded();

            string root = ModulesRoot;

            foreach (Assembly assembly in loaded)
            {
                string name, location;

                try
                {
                    name = assembly.GetName().Name;
                    location = assembly.IsDynamic ? null : assembly.Location;
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(location)) continue;

                string module = ModuleFromPath(location, root);
                if (module != null) map[name] = module;
            }

            _assemblyToModule = map;
            _lastAssemblyCount = loaded.Count;
        }

        /// <summary>
        /// Extracts the module folder from a full path under Modules\. Deliberately string-based:
        /// the engine offers no reverse lookup from a loaded assembly to its module, and every
        /// alternative (matching by name, asking ModuleHelper) is wrong on precisely the bundled
        /// case this exists to handle.
        /// </summary>
        private static string ModuleFromPath(string location, string root)
        {
            if (string.IsNullOrEmpty(root)) return null;

            string full;
            try { full = Path.GetFullPath(location); }
            catch { return null; }

            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;

            string relative = full.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);

            int slash = relative.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
            if (slash <= 0) return null;

            return relative.Substring(0, slash);
        }

        private static List<Assembly> Loaded()
        {
            try { return AppDomain.CurrentDomain.GetAssemblies().Where(a => a != null).ToList(); }
            catch { return new List<Assembly>(); }
        }
    }
}
