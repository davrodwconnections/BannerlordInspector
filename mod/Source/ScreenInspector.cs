using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.ScreenSystem;

namespace BannerlordInspector
{
    /// <summary>
    /// What is on screen, and whether it can be interacted with.
    ///
    /// This exists because of an evening spent proving, one hand-written query at a time, that a
    /// game the user was certain had frozen was in fact running at 200 FPS with a mod options screen
    /// on top whose mouse visibility was off. The game was fine; the screen was unusable; and from
    /// outside those are the same thing. Six chained lookups to establish it, none of which anyone
    /// should have to remember.
    ///
    /// The two questions worth asking of a UI that is misbehaving are which screen owns the input,
    /// and whether that screen believes it should have a cursor. Both are cheap. Neither was
    /// reachable without knowing the exact type names to walk.
    ///
    /// It also reports the active campaign menu, which is a different layer entirely and the one
    /// behind "a menu keeps popping up on its own" - a real open bug in TAOM's enlistment, where a
    /// failing hourly retry re-opens a menu every game hour.
    /// </summary>
    public static class ScreenInspector
    {
        public static object Current()
        {
            object screens;
            try { screens = Screens(); }
            catch (Exception ex) { screens = new { error = "could not read the screen stack: " + ex.Message }; }

            object menu;
            try { menu = CampaignMenu(); }
            catch (Exception ex) { menu = new { error = "could not read the campaign menu: " + ex.Message }; }

            return new
            {
                note = "The top screen owns the input. If a screen looks frozen, read mouseVisible "
                       + "and inputRestrictions first - a screen that renders but has no cursor is "
                       + "indistinguishable from a hung game, and is not one.",
                screen = screens,
                campaignMenu = menu
            };
        }

        // ------------------------------------------------------------------ screens

        private static object Screens()
        {
            ScreenBase top = ScreenManager.TopScreen;

            if (top == null)
            {
                return new { any = false, note = "No screen is on top. The game is between states." };
            }

            Type type = top.GetType();

            var layers = new List<object>();
            try
            {
                foreach (var layer in top.Layers ?? Enumerable.Empty<ScreenLayer>())
                {
                    if (layer == null) continue;

                    layers.Add(new
                    {
                        type = layer.GetType().Name,
                        isActive = Safe(() => layer.IsActive),
                        isFocusLayer = Safe(() => layer.IsFocusLayer),
                        isHitThisFrame = Safe(() => layer.IsHitThisFrame),
                        activeCursor = Safe(() => layer.ActiveCursor.ToString()),

                        // What the LAYER thinks the mouse should do. When this disagrees with the
                        // screen's own mouseVisible, the screen wins and the cursor is gone -
                        // which is the exact shape of the "frozen menu" that is not frozen.
                        wantsMouseVisible = Safe(() => layer.InputRestrictions?.MouseVisibility)
                    });
                }
            }
            catch
            {
                // A partial layer list beats none.
            }

            bool mouseVisible = Safe(() => top.MouseVisible);
            var flags = new List<string>();

            bool anyLayerWantsMouse = layers.Any(l =>
                Equals(l.GetType().GetProperty("wantsMouseVisible")?.GetValue(l), true));

            // NOT a flag, and the reason is a lesson worth keeping.
            //
            // This started life as a warning: "a layer wants a cursor and the screen says no,
            // therefore the screen is unusable". It matched a real frozen menu once, which was
            // enough to make it look like a diagnosis. Then it fired on CharacterCreationScreen,
            // in the middle of a character creation the user was clicking through perfectly
            // happily.
            //
            // So ScreenBase.MouseVisible is not the switch it appears to be - it sits false on
            // screens that work fine, and the engine gets its cursor from somewhere else. One
            // matching case was correlation, and treating it as a cause produced a confident
            // explanation of a bug that was, as far as this can show, wrong.
            //
            // Reported as an observation instead. Whoever reads it can weigh it; the tool no
            // longer pretends to know what it means.
            var observations = new List<string>();

            if (!mouseVisible && anyLayerWantsMouse)
            {
                observations.Add("A layer asks for a visible mouse while the screen reports "
                                 + "mouseVisible=false. This is COMMON on working screens - it shows "
                                 + "up during character creation, where the mouse is fine - so it is "
                                 + "not evidence of anything on its own.");
            }

            if (Safe(() => top.IsFinalized))
            {
                flags.Add("The top screen is FINALIZED but still on top. It is on its way out and "
                          + "should not be receiving input.");
            }

            return new
            {
                any = true,
                topScreen = type.FullName,
                fromModule = ModuleMap.ForAssembly(type.Assembly),
                isActive = Safe(() => top.IsActive),
                isInitialized = Safe(() => top.IsInitialized),
                isFinalized = Safe(() => top.IsFinalized),
                isPaused = Safe(() => top.IsPaused),
                mouseVisible,
                layerCount = layers.Count,
                layers = layers.ToArray(),
                flags = flags.ToArray(),
                observations = observations.ToArray()
            };
        }

        // ------------------------------------------------------------------ campaign menu

        /// <summary>
        /// The campaign menu currently open, if any.
        ///
        /// Found by reflection rather than by a typed call: the menu manager's surface has moved
        /// between game versions, and a diagnostic that stops working on an engine bump is a
        /// diagnostic that will be missing exactly when the bump breaks something.
        /// </summary>
        private static object CampaignMenu()
        {
            if (Campaign.Current == null)
            {
                return new { open = false, note = "No campaign loaded." };
            }

            object handler = Read(Campaign.Current, "CurrentMenuContext")
                             ?? Read(Campaign.Current, "GameMenuManager");

            if (handler == null)
            {
                return new { open = false, note = "No menu context is active - you are on the map." };
            }

            string menuId = Read(handler, "StringId") as string
                            ?? Read(Read(handler, "GameMenu"), "StringId") as string;

            return new
            {
                open = menuId != null,
                menuId,
                contextType = handler.GetType().FullName,
                fromModule = ModuleMap.ForAssembly(handler.GetType().Assembly),
                note = menuId == null
                    ? "A menu context exists but its id was not readable on this game version."
                    : "Watch this with /watch if a menu is opening on its own - the id tells you "
                      + "which code path is re-opening it."
            };
        }

        private static object Read(object target, string member)
        {
            if (target == null) return null;

            try
            {
                Type type = target.GetType();

                PropertyInfo property = AccessTools.Property(type, member);
                if (property != null && property.CanRead) return property.GetValue(target);

                FieldInfo field = AccessTools.Field(type, member);
                return field?.GetValue(target);
            }
            catch
            {
                return null;
            }
        }

        private static T Safe<T>(Func<T> read)
        {
            try { return read(); }
            catch { return default; }
        }
    }
}
