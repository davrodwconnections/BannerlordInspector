using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace BannerlordInspector
{
    /// <summary>
    /// Searches types and members across every assembly the game has loaded.
    ///
    /// This replaces the whole business of hunting a mod's DLL down on disk and decompiling it. The
    /// loaded assemblies are the truth - they include Steam Workshop mods sitting outside the
    /// Modules folder, and they are the exact build that is running rather than whichever copy
    /// happens to be lying around. It also works on mods whose files cannot be read statically at
    /// all: AI Influence is ConfuserEx-packed and defeats both System.Reflection.Metadata and
    /// Mono.Cecil on disk, yet its types enumerate perfectly here, because by then the runtime has
    /// already unpacked them.
    ///
    /// Read-only: it reports what exists and never touches an instance.
    /// </summary>
    public static class TypeExplorer
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic
                                                | BindingFlags.Instance | BindingFlags.Static
                                                | BindingFlags.DeclaredOnly;

        /// <summary>Every loaded assembly, so you know what there is to search.</summary>
        public static object Assemblies(string filter)
        {
            var rows = new List<object>();

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name;
                try { name = assembly.GetName().Name; }
                catch { continue; }

                if (!string.IsNullOrEmpty(filter) &&
                    name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                int typeCount;
                try { typeCount = SafeTypes(assembly).Length; }
                catch { typeCount = -1; }

                rows.Add(new
                {
                    assembly = name,

                    // Which module folder it was loaded from. Not derivable from the name: a total
                    // conversion bundles dozens of third-party assemblies inside one module and
                    // leaves the folders those libraries would normally occupy as empty stubs.
                    // Without this, "ButterLib is loaded" says nothing about who supplied it -
                    // which is the whole question when more than one module could have.
                    module = ModuleMap.ForAssembly(assembly),

                    version = SafeVersion(assembly),
                    types = typeCount,
                    location = SafeLocation(assembly)
                });
            }

            return new
            {
                count = rows.Count,
                assemblies = rows.OrderBy(r => (string)r.GetType().GetProperty("assembly").GetValue(r),
                    StringComparer.OrdinalIgnoreCase).ToArray()
            };
        }

        /// <summary>Find types by name substring. The starting point for exploring an unfamiliar mod.</summary>
        public static object FindTypes(string query, string assemblyFilter, int limit)
        {
            if (string.IsNullOrWhiteSpace(query) && string.IsNullOrWhiteSpace(assemblyFilter))
            {
                return new { error = "give a query (type name substring) or an assembly filter" };
            }

            var rows = new List<object>();
            int matched = 0;

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string assemblyName;
                try { assemblyName = assembly.GetName().Name; }
                catch { continue; }

                if (!string.IsNullOrEmpty(assemblyFilter) &&
                    assemblyName.IndexOf(assemblyFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                foreach (Type type in SafeTypes(assembly))
                {
                    string full = type.FullName;
                    if (full == null) continue;

                    if (!string.IsNullOrEmpty(query) &&
                        full.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    // Compiler-generated noise helps nobody.
                    if (full.Contains("<") || full.Contains("+<")) continue;

                    matched++;
                    if (rows.Count >= limit) continue;

                    rows.Add(new
                    {
                        type = full,
                        assembly = assemblyName,
                        baseType = type.BaseType?.FullName,
                        isStatic = type.IsAbstract && type.IsSealed,
                        isPublic = type.IsPublic
                    });
                }
            }

            return new { matched, returned = rows.Count, truncated = matched > rows.Count, types = rows.ToArray() };
        }

        /// <summary>
        /// The full member surface of one type, including non-public. This is what makes a closed
        /// mod explorable: find the type, read its members, then read live values off it with
        /// /eval or call a query method with /call.
        /// </summary>
        public static object Members(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return new { error = "give a type name" };

            Type type = HarmonyLib.AccessTools.TypeByName(typeName);
            if (type == null)
            {
                return new
                {
                    error = "type not found",
                    typeName,
                    hint = "Use /types?q= to find the exact full name first."
                };
            }

            var properties = new List<object>();
            var fields = new List<object>();
            var methods = new List<object>();

            try
            {
                foreach (PropertyInfo p in type.GetProperties(AllMembers))
                {
                    properties.Add(new
                    {
                        name = p.Name,
                        type = p.PropertyType.Name,
                        isStatic = p.GetGetMethod(true)?.IsStatic ?? false,
                        canRead = p.CanRead
                    });
                }

                foreach (FieldInfo f in type.GetFields(AllMembers))
                {
                    if (f.Name.Contains("<")) continue;
                    fields.Add(new { name = f.Name, type = f.FieldType.Name, isStatic = f.IsStatic });
                }

                foreach (MethodInfo m in type.GetMethods(AllMembers))
                {
                    if (m.IsSpecialName) continue;   // property accessors, operators
                    methods.Add(new
                    {
                        name = m.Name,
                        returns = m.ReturnType.Name,
                        isStatic = m.IsStatic,
                        parameters = m.GetParameters()
                            .Select(p => p.ParameterType.Name + " " + p.Name).ToArray(),
                        queryable = QueryInvoker.LooksQueryable(m)
                    });
                }
            }
            catch (Exception ex)
            {
                return new { error = "could not read members: " + ex.Message, type = type.FullName };
            }

            return new
            {
                type = type.FullName,
                assembly = type.Assembly.GetName().Name,
                baseType = type.BaseType?.FullName,
                interfaces = SafeInterfaces(type),
                counts = new { properties = properties.Count, fields = fields.Count, methods = methods.Count },
                properties = properties.ToArray(),
                fields = fields.ToArray(),
                methods = methods.ToArray()
            };
        }

        public static Type[] SafeTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                // Expected on packed or partially-resolvable assemblies. Whatever did load is
                // still worth searching.
                return ex.Types?.Where(t => t != null).ToArray() ?? new Type[0];
            }
            catch
            {
                return new Type[0];
            }
        }

        private static string[] SafeInterfaces(Type type)
        {
            try { return type.GetInterfaces().Select(i => i.Name).Take(12).ToArray(); }
            catch { return new string[0]; }
        }

        private static string SafeVersion(Assembly assembly)
        {
            try { return assembly.GetName().Version?.ToString(); }
            catch { return null; }
        }

        private static string SafeLocation(Assembly assembly)
        {
            try { return assembly.IsDynamic ? "(dynamic)" : assembly.Location; }
            catch { return null; }
        }
    }
}
