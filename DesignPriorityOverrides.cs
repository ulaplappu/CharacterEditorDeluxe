using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection.Emit;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace CharacterEditorDeluxe
{
    internal sealed class DesignPriorityOverrides
    {
        private static readonly int[] AllowedMaxima = { 100, 200 };
        private static readonly string[] MaximumLabels = { "100%", "200%" };
        private static readonly string[] CategoryLabels = { "Gameplay", "Graphics", "Sound", "Technical" };
        private static DesignPriorityOverrides active;
        private readonly ManualLogSource log;
        private readonly ConfigEntry<bool> enabled;
        private readonly ConfigEntry<int> maximum;
        private readonly ConfigEntry<bool> allowOver100;
        private Harmony harmony;
        private Menu_DevGame menu;
        private Menu_Dev_ChangeDesignproritaet changeMenu;
        private mainScript player;
        private bool lastEnabled;
        private int lastMaximum;
        private int[] rememberedPriorities;
        private readonly string[] priorityInputs = new string[4];

        internal DesignPriorityOverrides(ManualLogSource log, ConfigEntry<bool> enabled, ConfigEntry<int> maximum, ConfigEntry<bool> allowOver100)
        {
            this.log = log;
            this.enabled = enabled;
            this.maximum = maximum;
            this.allowOver100 = allowOver100;
            maximum.Value = NormalizeMaximum(maximum.Value);
        }

        internal void Install()
        {
            active = this;
            harmony = new Harmony("com.codex.mgt2.charactereditordeluxe.designpriority");
            harmony.Patch(AccessTools.Method(typeof(Menu_DevGame), "BUTTON_Start"),
                transpiler: new HarmonyMethod(typeof(DesignPriorityOverrides), nameof(PatchStartValidation)));
            harmony.Patch(AccessTools.Method(typeof(Menu_DevGame), "UpdateGesamtArbeitsprioritaet"),
                transpiler: new HarmonyMethod(typeof(DesignPriorityOverrides), nameof(PatchTotalIndicator)));
            harmony.Patch(AccessTools.Method(typeof(Menu_DevGame), "CopyDesignSettings"),
                prefix: new HarmonyMethod(typeof(DesignPriorityOverrides), nameof(BeforeCopyDesignSettings)));
            harmony.Patch(AccessTools.Method(typeof(Menu_DevGame), "OnDisable"),
                postfix: new HarmonyMethod(typeof(DesignPriorityOverrides), nameof(AfterMenuClosed)));
            harmony.Patch(AccessTools.Method(typeof(Menu_DevGame), "InitNewGame"),
                postfix: new HarmonyMethod(typeof(DesignPriorityOverrides), nameof(AfterNewGameMenuOpened)));
            harmony.Patch(AccessTools.Method(typeof(Menu_Dev_ChangeDesignproritaet), "Init"),
                prefix: new HarmonyMethod(typeof(DesignPriorityOverrides), nameof(BeforeChangeMenuOpened)));
            harmony.Patch(AccessTools.Method(typeof(Menu_Dev_ChangeDesignproritaet), "BUTTON_OK"),
                transpiler: new HarmonyMethod(typeof(DesignPriorityOverrides), nameof(PatchChangeValidation)));
            harmony.Patch(AccessTools.Method(typeof(Menu_Dev_ChangeDesignproritaet), "UpdateGesamtArbeitsprioritaet"),
                transpiler: new HarmonyMethod(typeof(DesignPriorityOverrides), nameof(PatchTotalIndicator)));
            harmony.Patch(AccessTools.Method(typeof(taskGame), "Work"),
                prefix: new HarmonyMethod(typeof(DesignPriorityOverrides), nameof(BeforeWork)));
            harmony.Patch(AccessTools.Method(typeof(savegameScript), "SaveGames"),
                postfix: new HarmonyMethod(typeof(DesignPriorityOverrides), nameof(AfterSaveGames)));
            harmony.Patch(AccessTools.Method(typeof(savegameScript), "LoadGames"),
                postfix: new HarmonyMethod(typeof(DesignPriorityOverrides), nameof(AfterLoadGames)));
            log.LogInfo("Design priority patches installed: New Game validation, total indicator, and player development work.");
        }

        internal void Uninstall()
        {
            if (harmony != null) harmony.UnpatchSelf();
            if (active == this) active = null;
        }

        internal void ResetToVanilla()
        {
            enabled.Value = false;
            allowOver100.Value = false;
            maximum.Value = 100;
            for (int i = 0; i < priorityInputs.Length; i++) priorityInputs[i] = null;
        }

        internal void DrawOptions()
        {
            enabled.Value = GUILayout.Toggle(enabled.Value, "Uncapped Design Priority");
            GUILayout.BeginHorizontal();
            GUILayout.Label("Safe max [100-200]", GUILayout.Width(180));
            int selected = Array.IndexOf(AllowedMaxima, maximum.Value);
            int next = GUILayout.SelectionGrid(Math.Max(0, selected), MaximumLabels, MaximumLabels.Length);
            if (next >= 0 && next < AllowedMaxima.Length && next != selected) maximum.Value = AllowedMaxima[next];
            GUILayout.EndHorizontal();
            allowOver100.Value = GUILayout.Toggle(allowOver100.Value, "Allow Total >100%");
            if (enabled.Value && menu != null && menu.isActiveAndEnabled)
            {
                GUILayout.BeginHorizontal();
                for (int i = 0; i < priorityInputs.Length; i++)
                {
                    if (priorityInputs[i] == null) priorityInputs[i] = (ReadPriority(menu, i) * 5).ToString(CultureInfo.InvariantCulture);
                    GUILayout.Label(CategoryLabels[i], GUILayout.Width(75));
                    priorityInputs[i] = GUILayout.TextField(priorityInputs[i], GUILayout.Width(75));
                }
                if (GUILayout.Button("Apply Priorities", GUILayout.Width(130))) ApplyPriorityInputs();
                GUILayout.EndHorizontal();
            }
        }

        internal void UpdateMenu()
        {
            if (player == null) player = UnityEngine.Object.FindObjectOfType<mainScript>();
            if (menu == null) menu = UnityEngine.Object.FindObjectOfType<Menu_DevGame>();
            if (changeMenu == null) changeMenu = UnityEngine.Object.FindObjectOfType<Menu_Dev_ChangeDesignproritaet>();
            if (changeMenu != null && changeMenu.isActiveAndEnabled)
            {
                var changeSliders = GetChangeSliders(changeMenu);
                if (changeSliders != null)
                    foreach (Slider slider in changeSliders) slider.maxValue = enabled.Value ? maximum.Value / 5 : 20;
            }
            if (menu == null || !menu.isActiveAndEnabled) return;
            bool nowEnabled = enabled.Value;
            int nowMaximum = NormalizeMaximum(maximum.Value);
            if (maximum.Value != nowMaximum) maximum.Value = nowMaximum;
            if (lastEnabled == nowEnabled && lastMaximum == nowMaximum && SlidersMatch(menu, nowEnabled ? nowMaximum / 5 : 20)) return;
            var sliders = GetSliders(menu);
            if (sliders == null) return;
            int rawMax = nowEnabled ? nowMaximum / 5 : 20;
            foreach (Slider slider in sliders) slider.maxValue = rawMax;
            if (!nowEnabled && lastEnabled && Total(menu) > 100)
            {
                // Restore the four ordinary 25% defaults when disabling the cheat in an open editor.
                foreach (Slider slider in sliders) slider.value = 5f;
                menu.g_GameAP_Gameplay = menu.g_GameAP_Grafik = menu.g_GameAP_Sound = menu.g_GameAP_Technik = 5;
                for (int i = 0; i < priorityInputs.Length; i++) priorityInputs[i] = null;
            }
            lastEnabled = nowEnabled;
            lastMaximum = nowMaximum;
        }

        private static int NormalizeMaximum(int value)
        {
            foreach (int allowed in AllowedMaxima) if (allowed == value) return value;
            return 100;
        }

        private static Slider[] GetSliders(Menu_DevGame source)
        {
            if (source.uiObjects == null || source.uiObjects.Length <= 100) return null;
            var result = new Slider[4];
            for (int i = 0; i < 4; i++)
            {
                if (source.uiObjects[97 + i] == null) return null;
                result[i] = source.uiObjects[97 + i].GetComponent<Slider>();
                if (result[i] == null) return null;
            }
            return result;
        }

        private static Slider[] GetChangeSliders(Menu_Dev_ChangeDesignproritaet source)
        {
            if (source.uiObjects == null || source.uiObjects.Length <= 8) return null;
            var result = new Slider[4];
            for (int i = 0; i < result.Length; i++)
            {
                if (source.uiObjects[5 + i] == null) return null;
                result[i] = source.uiObjects[5 + i].GetComponent<Slider>();
                if (result[i] == null) return null;
            }
            return result;
        }

        private static bool SlidersMatch(Menu_DevGame source, int rawMax)
        {
            var sliders = GetSliders(source);
            if (sliders == null) return false;
            foreach (Slider slider in sliders) if (Math.Abs(slider.maxValue - rawMax) > 0.01f) return false;
            return true;
        }

        private static int Total(Menu_DevGame source)
        {
            return (source.g_GameAP_Gameplay + source.g_GameAP_Grafik + source.g_GameAP_Sound + source.g_GameAP_Technik) * 5;
        }

        private static int ReadPriority(Menu_DevGame source, int category)
        {
            return category == 0 ? source.g_GameAP_Gameplay : category == 1 ? source.g_GameAP_Grafik : category == 2 ? source.g_GameAP_Sound : source.g_GameAP_Technik;
        }

        private void ApplyPriorityInputs()
        {
            var sliders = GetSliders(menu);
            if (sliders == null) return;
            int[] values = new int[4];
            for (int i = 0; i < values.Length; i++)
            {
                if (!int.TryParse(priorityInputs[i], NumberStyles.None, CultureInfo.InvariantCulture, out int percent) || percent < 0 || percent > maximum.Value || percent % 5 != 0)
                {
                    log.LogWarning("Priority input must be a 5% increment from 0 to " + maximum.Value + "%: " + CategoryLabels[i]);
                    return;
                }
                values[i] = percent / 5;
            }
            for (int i = 0; i < values.Length; i++) sliders[i].value = values[i];
            menu.SetAP_Gameplay();
            menu.SetAP_Grafik();
            menu.SetAP_Sound();
            menu.SetAP_Technik();
            AccessTools.Method(typeof(Menu_DevGame), "UpdateGesamtArbeitsprioritaet").Invoke(menu, null);
        }

        private static int TotalLimit()
        {
            return active != null && active.enabled.Value && active.allowOver100.Value ? active.maximum.Value * 4 : 100;
        }

        private static void BeforeCopyDesignSettings(Menu_DevGame __instance)
        {
            if (active == null || !active.enabled.Value) return;
            var sliders = GetSliders(__instance);
            if (sliders == null) return;
            int rawMax = active.maximum.Value / 5;
            foreach (Slider slider in sliders) slider.maxValue = rawMax;
        }

        private static void BeforeChangeMenuOpened(Menu_Dev_ChangeDesignproritaet __instance)
        {
            if (active == null || !active.enabled.Value) return;
            var sliders = GetChangeSliders(__instance);
            if (sliders == null) return;
            foreach (Slider slider in sliders) slider.maxValue = active.maximum.Value / 5;
        }

        private static void AfterMenuClosed(Menu_DevGame __instance)
        {
            if (active == null || !active.enabled.Value || __instance == null) return;
            active.rememberedPriorities = new[] { __instance.g_GameAP_Gameplay, __instance.g_GameAP_Grafik, __instance.g_GameAP_Sound, __instance.g_GameAP_Technik };
            for (int i = 0; i < active.priorityInputs.Length; i++) active.priorityInputs[i] = null;
        }

        private static void AfterNewGameMenuOpened(Menu_DevGame __instance)
        {
            if (active == null || !active.enabled.Value) return;
            for (int i = 0; i < active.priorityInputs.Length; i++) active.priorityInputs[i] = null;
            if (active.rememberedPriorities == null) return;
            var sliders = GetSliders(__instance);
            if (sliders == null) return;
            int rawMax = active.maximum.Value / 5;
            for (int i = 0; i < sliders.Length; i++)
            {
                sliders[i].maxValue = rawMax;
                sliders[i].value = Mathf.Clamp(active.rememberedPriorities[i], 0, rawMax);
                active.priorityInputs[i] = (Mathf.RoundToInt(sliders[i].value) * 5).ToString(CultureInfo.InvariantCulture);
            }
        }

        private static IEnumerable<CodeInstruction> PatchStartValidation(IEnumerable<CodeInstruction> instructions)
        {
            return PatchValidation(instructions, typeof(Menu_DevGame));
        }

        private static IEnumerable<CodeInstruction> PatchChangeValidation(IEnumerable<CodeInstruction> instructions)
        {
            return PatchValidation(instructions, typeof(Menu_Dev_ChangeDesignproritaet));
        }

        private static IEnumerable<CodeInstruction> PatchValidation(IEnumerable<CodeInstruction> instructions, Type menuType)
        {
            var list = new List<CodeInstruction>(instructions);
            var totalMethod = AccessTools.Method(menuType, "UpdateGesamtArbeitsprioritaet");
            int found = 0;
            for (int i = 1; i < list.Count; i++)
                if (list[i - 1].Calls(totalMethod) && list[i].LoadsConstant(100))
                {
                    // The first comparison rejects totals above 100; the second rejects
                    // totals below 100. Only the upper bound changes with this cheat.
                    if (found == 0)
                    {
                        list[i].opcode = OpCodes.Call;
                        list[i].operand = AccessTools.Method(typeof(DesignPriorityOverrides), nameof(TotalLimit));
                    }
                    found++;
                }
            if (found != 2) throw new InvalidOperationException("Expected two New Game priority total checks; found " + found);
            return list;
        }

        private static IEnumerable<CodeInstruction> PatchTotalIndicator(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            int found = 0;
            for (int i = 1; i < list.Count; i++)
                if (list[i - 1].Calls(AccessTools.Method(typeof(Mathf), "RoundToInt", new[] { typeof(float) })) && list[i].LoadsConstant(100))
                {
                    list[i].opcode = OpCodes.Call;
                    list[i].operand = AccessTools.Method(typeof(DesignPriorityOverrides), nameof(TotalLimit));
                    found++;
                }
            if (found != 1) throw new InvalidOperationException("Expected one priority total warning check; found " + found);
            return list;
        }

        private struct WorkSample
        {
            internal gameScript Game;
            internal int Category;
            internal float Before;
            internal float Original;
            internal float Effective;
        }

        private static void BeforeWork(taskGame __instance, ref float __0, int __1, out WorkSample __state)
        {
            __state = default;
            if (active == null || __1 < 0 || __1 > 3 || __instance == null) return;
            gameScript game = __instance.gS_;
            // ownerID may be the external publisher; developerID identifies the studio doing the work.
            if (game == null || active.player == null || game.developerID != active.player.myID) return;
            int raw = ClampStoredPriority(game, __1);
            float original = __0;
            float before = ReadPoints(game, __1);
            if (!active.enabled.Value || raw <= 20) return;
            if (float.IsNaN(original) || float.IsInfinity(original) || float.IsNaN(before) || float.IsInfinity(before))
            {
                active.log.LogWarning("Priority work skipped non-finite source value for game=" + game.myID + " category=" + __1);
                return;
            }
            double boosted = (double)original * raw / 20d;
            if (double.IsNaN(boosted) || double.IsInfinity(boosted) || boosted > (double)float.MaxValue - before || boosted < -(double)float.MaxValue - before)
            {
                active.log.LogWarning("Priority work skipped unsafe production value for game=" + game.myID + " category=" + __1);
                return;
            }
            __0 = (float)boosted;
            __state = new WorkSample { Game = game, Category = __1, Before = before, Original = original, Effective = __0 };
        }

        private static float ReadPoints(gameScript game, int category)
        {
            return category == 0 ? game.points_gameplay : category == 1 ? game.points_grafik : category == 2 ? game.points_sound : game.points_technik;
        }

        private static void AfterSaveGames() { LogStoredPriorities("save"); }

        private static void AfterLoadGames() { LogStoredPriorities("load"); }

        private static void LogStoredPriorities(string operation)
        {
            if (active == null || active.player == null) return;
            foreach (gameScript game in UnityEngine.Object.FindObjectsOfType<gameScript>())
            {
                if (game == null || game.developerID != active.player.myID) continue;
                if (active.enabled.Value) ClampStoredPriorities(game);
                if (game.gameAP_Gameplay <= 20 && game.gameAP_Grafik <= 20 && game.gameAP_Sound <= 20 && game.gameAP_Technik <= 20) continue;
            }
        }

        private static int ClampStoredPriority(gameScript game, int category)
        {
            int raw = category == 0 ? game.gameAP_Gameplay : category == 1 ? game.gameAP_Grafik : category == 2 ? game.gameAP_Sound : game.gameAP_Technik;
            int safe = Mathf.Clamp(raw, 0, active.maximum.Value / 5);
            if (safe != raw)
            {
                if (category == 0) game.gameAP_Gameplay = safe;
                else if (category == 1) game.gameAP_Grafik = safe;
                else if (category == 2) game.gameAP_Sound = safe;
                else game.gameAP_Technik = safe;
                active.log.LogWarning("Clamped stored design priority for game=" + game.myID + " category=" + category + " to " + (safe * 5) + "%. ");
            }
            return safe;
        }

        private static void ClampStoredPriorities(gameScript game)
        {
            for (int category = 0; category < 4; category++) ClampStoredPriority(game, category);
        }
    }
}
