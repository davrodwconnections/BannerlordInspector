using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace BannerlordInspector
{
    /// <summary>
    /// Reads other mods' Mod Configuration Menu settings, with their current values.
    ///
    /// Most of this modlist is configured through MCM, and a surprising number of "why is this mod
    /// not doing anything" answers turn out to be a toggle that is off. Opening the menu to check
    /// means leaving whatever you were doing; worse, MCM settings are per-save or per-global
    /// depending on the mod, so the file on disk is not always what is live. This reads what the
    /// game is actually holding.
    ///
    /// Found by reflection rather than by asking MCM: every settings class derives from MCM's
    /// BaseSettings and exposes a static Instance, which is enough to enumerate them without
    /// binding to MCM's own API surface - and it keeps working if MCM's internals move.
    /// </summary>
    public static class McmInspector
    {
        private const string BaseSettingsTypeName = "MCM.Abstractions.Base.BaseSettings";

        public static object List(string filter, bool includeValues)
        {
            Type baseSettings = AccessTools.TypeByName(BaseSettingsTypeName);
            if (baseSettings == null)
            {
                return new
                {
                    error = "MCM is not loaded",
                    hint = "Bannerlord.MBOptionScreen provides it. Without MCM there are no settings to read."
                };
            }

            var mods = new List<object>();

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string assemblyName;
                try { assemblyName = assembly.GetName().Name; }
                catch { continue; }

                foreach (Type type in TypeExplorer.SafeTypes(assembly))
                {
                    if (type == null || type.IsAbstract) continue;
                    if (!baseSettings.IsAssignableFrom(type)) continue;
                    if (type.FullName == null || type.FullName.Contains("<")) continue;

                    if (!string.IsNullOrEmpty(filter)
                        && type.FullName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0
                        && assemblyName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    mods.Add(Describe(type, assemblyName, includeValues));
                }
            }

            return new
            {
                count = mods.Count,
                note = includeValues
                    ? "Values are read from the live instance, which is what the game is using now."
                    : "Add values=true to read the current value of every setting.",
                settings = mods.ToArray()
            };
        }

        private static object Describe(Type type, string assemblyName, bool includeValues)
        {
            object instance = null;
            string instanceError = null;

            try
            {
                // AttributeGlobalSettings<T> and friends all expose a static Instance.
                instance = AccessTools.Property(type, "Instance")?.GetValue(null);
            }
            catch (Exception ex)
            {
                instanceError = ex.InnerException?.Message ?? ex.Message;
            }

            string displayName = null;
            string id = null;

            if (instance != null)
            {
                displayName = SafeRead(instance, "DisplayName") as string;
                id = SafeRead(instance, "Id") as string;
            }

            object[] values = null;
            if (includeValues && instance != null)
            {
                values = ReadValues(type, instance);
            }

            return new
            {
                settingsClass = type.FullName,
                assembly = assemblyName,
                id,
                displayName,
                live = instance != null,
                problem = instanceError,
                settingCount = values?.Length,
                values
            };
        }

        /// <summary>
        /// Only properties that MCM itself would show: public, readable, and simple enough to render.
        /// Anything exotic is reported by type rather than value, so one odd setting cannot break
        /// the whole listing.
        /// </summary>
        private static object[] ReadValues(Type type, object instance)
        {
            var rows = new List<object>();

            PropertyInfo[] properties;
            try
            {
                properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            }
            catch
            {
                return new object[0];
            }

            foreach (PropertyInfo property in properties)
            {
                if (property.GetIndexParameters().Length > 0) continue;
                if (!property.CanRead) continue;

                // MCM plumbing, not user settings.
                if (property.Name == "Id" || property.Name == "DisplayName"
                    || property.Name == "FolderName" || property.Name == "FormatType"
                    || property.Name == "SubFolder" || property.Name == "UIVersion") continue;

                object value;
                try { value = property.GetValue(instance); }
                catch (Exception ex)
                {
                    rows.Add(new { setting = property.Name, type = property.PropertyType.Name, error = ex.Message });
                    continue;
                }

                rows.Add(new
                {
                    setting = property.Name,
                    type = property.PropertyType.Name,
                    value = Simplify(value)
                });
            }

            return rows.OrderBy(r => (string)r.GetType().GetProperty("setting").GetValue(r),
                StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static object Simplify(object value)
        {
            if (value == null) return null;
            if (value is string || value.GetType().IsPrimitive || value.GetType().IsEnum) return value;

            // MCM dropdowns carry a selection plus the list of options.
            object selected = SafeRead(value, "SelectedValue");
            if (selected != null) return new { selected = selected.ToString(), kind = "dropdown" };

            if (value is IEnumerable items && !(value is string))
            {
                var preview = new List<string>();
                foreach (object item in items)
                {
                    preview.Add(item?.ToString());
                    if (preview.Count >= 10) break;
                }
                return new { kind = "list", items = preview.ToArray() };
            }

            return value.ToString();
        }

        private static object SafeRead(object instance, string member)
        {
            try
            {
                return AccessTools.Property(instance.GetType(), member)?.GetValue(instance);
            }
            catch
            {
                return null;
            }
        }
    }
}
