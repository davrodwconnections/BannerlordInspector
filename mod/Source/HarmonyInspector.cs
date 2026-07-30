using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace BannerlordInspector
{
    /// <summary>
    /// Who has patched what, and where two mods are standing on the same method.
    ///
    /// This is the question a heavily modded install raises constantly and that nothing else can
    /// answer. Reading DLLs off disk tells you a mod *intends* to patch something; it cannot tell
    /// you whether the patch landed, whether another mod patched the same method, or in what order
    /// they run. Harmony knows all of it at runtime, and this exposes it.
    ///
    /// A "conflict" here means: two or more different Harmony ids have patched the same method.
    /// That is not automatically a bug - many are harmless, and some are deliberate cooperation -
    /// but it is exactly the shortlist worth looking at when something behaves strangely, and it is
    /// impossible to produce any other way.
    ///
    /// Prefixes are called out separately because a prefix that returns false skips the original
    /// method *and every later prefix*, which is the mechanism behind most silent mod breakage.
    /// </summary>
    public static class HarmonyInspector
    {
        /// <summary>Every patched method with its owners. Optionally filtered by owner or target.</summary>
        public static object AllPatches(string ownerFilter, string targetFilter, int limit)
        {
            var rows = new List<PatchRow>();
            int total = 0;

            foreach (MethodBase method in SafeGetPatched())
            {
                Patches info;
                try { info = Harmony.GetPatchInfo(method); }
                catch { continue; }
                if (info == null) continue;

                var owners = Owners(info);
                string target = Describe(method);

                if (!string.IsNullOrEmpty(ownerFilter) &&
                    !owners.Any(o => o.IndexOf(ownerFilter, StringComparison.OrdinalIgnoreCase) >= 0)) continue;

                if (!string.IsNullOrEmpty(targetFilter) &&
                    target.IndexOf(targetFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                total++;
                if (rows.Count >= limit) continue;

                rows.Add(new PatchRow
                {
                    target = target,
                    targetAssembly = method.DeclaringType?.Assembly.GetName().Name,
                    owners = owners.ToArray(),
                    prefixes = Names(info.Prefixes),
                    postfixes = Names(info.Postfixes),
                    transpilers = Names(info.Transpilers),
                    finalizers = Names(info.Finalizers)
                });
            }

            return new
            {
                matched = total,
                returned = rows.Count,
                truncated = total > rows.Count,
                patches = rows.OrderBy(r => r.target, StringComparer.OrdinalIgnoreCase).ToArray()
            };
        }

        /// <summary>
        /// Methods patched by more than one mod. The shortlist worth reading when something breaks.
        /// </summary>
        public static object Conflicts(int limit)
        {
            var rows = new List<ConflictRow>();

            foreach (MethodBase method in SafeGetPatched())
            {
                Patches info;
                try { info = Harmony.GetPatchInfo(method); }
                catch { continue; }
                if (info == null) continue;

                List<string> owners = Owners(info);
                if (owners.Count < 2) continue;

                // A prefix that can return false is the dangerous shape: it can skip the original
                // and every prefix queued behind it, which is how one mod silently disables another.
                string[] skippingPrefixes = info.Prefixes
                    .Where(p => ReturnsBool(p))
                    .Select(p => p.owner + " :: " + p.PatchMethod?.Name)
                    .ToArray();

                rows.Add(new ConflictRow
                {
                    target = Describe(method),
                    owners = owners.ToArray(),
                    ownerCount = owners.Count,
                    prefixesThatCanSkipOriginal = skippingPrefixes,
                    risk = skippingPrefixes.Length > 0 ? "high - a prefix here can skip the original"
                                                       : "low - additive patches only"
                });

                if (rows.Count >= limit) break;
            }

            return new
            {
                count = rows.Count,
                note = "Two mods on the same method is not automatically a bug. Look here first when "
                       + "something behaves oddly - especially the 'high' rows.",
                conflicts = rows.OrderByDescending(r => r.ownerCount).ToArray()
            };
        }

        /// <summary>How many methods each mod has patched - the shape of the install at a glance.</summary>
        public static object Owners()
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int patchedMethods = 0;

            foreach (MethodBase method in SafeGetPatched())
            {
                Patches info;
                try { info = Harmony.GetPatchInfo(method); }
                catch { continue; }
                if (info == null) continue;

                patchedMethods++;
                foreach (string owner in Owners(info))
                {
                    counts.TryGetValue(owner, out int n);
                    counts[owner] = n + 1;
                }
            }

            return new
            {
                patchedMethods,
                distinctOwners = counts.Count,
                owners = counts.OrderByDescending(kv => kv.Value)
                    .Select(kv => new { owner = kv.Key, methodsPatched = kv.Value })
                    .ToArray()
            };
        }

        // ------------------------------------------------------------------ helpers

        private static IEnumerable<MethodBase> SafeGetPatched()
        {
            MethodBase[] all;
            try
            {
                all = Harmony.GetAllPatchedMethods()?.ToArray() ?? new MethodBase[0];
            }
            catch (Exception ex)
            {
                InspectorLog.Error("Harmony.GetAllPatchedMethods failed.", ex);
                return new MethodBase[0];
            }
            return all.Where(m => m != null);
        }

        private static List<string> Owners(Patches info)
        {
            var owners = new List<string>();

            foreach (var patch in info.Prefixes.Concat(info.Postfixes)
                         .Concat(info.Transpilers).Concat(info.Finalizers))
            {
                if (patch?.owner == null) continue;
                if (!owners.Contains(patch.owner)) owners.Add(patch.owner);
            }
            return owners;
        }

        private static object[] Names(IEnumerable<Patch> patches)
        {
            return patches.Select(p => (object)new
            {
                owner = p.owner,
                method = p.PatchMethod?.DeclaringType?.FullName + "." + p.PatchMethod?.Name,
                priority = p.priority,
                canSkipOriginal = ReturnsBool(p)
            }).ToArray();
        }

        private static bool ReturnsBool(Patch patch)
        {
            try { return patch?.PatchMethod?.ReturnType == typeof(bool); }
            catch { return false; }
        }

        private static string Describe(MethodBase method)
        {
            try
            {
                string ps = string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name));
                return (method.DeclaringType?.FullName ?? "?") + "." + method.Name + "(" + ps + ")";
            }
            catch
            {
                return method?.Name ?? "?";
            }
        }

        private sealed class PatchRow
        {
            public string target { get; set; }
            public string targetAssembly { get; set; }
            public string[] owners { get; set; }
            public object[] prefixes { get; set; }
            public object[] postfixes { get; set; }
            public object[] transpilers { get; set; }
            public object[] finalizers { get; set; }
        }

        private sealed class ConflictRow
        {
            public string target { get; set; }
            public string[] owners { get; set; }
            public int ownerCount { get; set; }
            public string[] prefixesThatCanSkipOriginal { get; set; }
            public string risk { get; set; }
        }
    }
}
