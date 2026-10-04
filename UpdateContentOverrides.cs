using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace CharacterEditorDeluxe
{
    // Vanilla stores content as Boolean selections. The actual weights are 0.1
    // quality and (1 + categoryPoints * 0.02) production points per selection.
    internal sealed class UpdateContentOverrides
    {
        private static readonly int[] Maxima = { 100 };
        private static readonly string[] Labels = { "100" };
        private const float SafeCategoryPointCap = 100f;
        private const float SafeUpdateBonusCap = 5f;
        private static UpdateContentOverrides active;
        private static int loggedClampWarnings;
        internal readonly ConfigEntry<bool> Enabled;
        internal readonly ConfigEntry<int> Maximum;
        internal readonly ConfigEntry<int>[] Percent = new ConfigEntry<int>[8];
        private readonly ManualLogSource log;
        private Harmony harmony;
        private Menu_Dev_Update menu;
        private Vector2 scroll;
        private readonly Dictionary<int, double[]> pendingTaskGains = new Dictionary<int, double[]>();
        private static readonly FieldInfo SelectionField = AccessTools.Field(typeof(Menu_Dev_Update), "buttonAdds");
        private static readonly FieldInfo UpdateGameField = AccessTools.Field(typeof(Menu_Dev_Update), "gS_");
        private static readonly FieldInfo TaskGameField = AccessTools.Field(typeof(taskUpdate), "gS_");
        private static readonly MethodInfo FindTaskGameMethod = AccessTools.Method(typeof(taskUpdate), "FindMyObject");
        private mainScript player;
        private static bool[] Selections(Menu_Dev_Update source) { return (bool[])SelectionField.GetValue(source); }

        internal UpdateContentOverrides(ConfigFile config, ManualLogSource log)
        {
            this.log = log;
            Enabled = config.Bind("Game Update", "Enabled", false, "Scale real update content contributions. Disabled uses vanilla calculations.");
            Maximum = config.Bind("Game Update", "Maximum", 100, "Safe update % maximum: 100.");
            if (Array.IndexOf(Maxima, Maximum.Value) < 0) Maximum.Value = 100;
            for (int i = 0; i < Percent.Length; i++)
            {
                Percent[i] = config.Bind("Game Update", "Content" + i, 2, "Actual percentage of category points added by this item. Safe range is 0-100%.");
                Percent[i].Value = Mathf.Clamp(Percent[i].Value, 0, Maximum.Value);
            }
        }

        internal void Install()
        {
            active = this;
            harmony = new Harmony("com.codex.mgt2.charactereditordeluxe.updatecontent");
            foreach (string name in new[] { "GetP_Gameplay", "GetP_Grafik", "GetP_Sound", "GetP_Technik" })
                harmony.Patch(AccessTools.Method(typeof(Menu_Dev_Update), name), prefix: new HarmonyMethod(typeof(UpdateContentOverrides), nameof(CalculatePoints)));
            harmony.Patch(AccessTools.Method(typeof(Menu_Dev_Update), "BUTTON_Start"), prefix: new HarmonyMethod(typeof(UpdateContentOverrides), nameof(BeforeStart)), postfix: new HarmonyMethod(typeof(UpdateContentOverrides), nameof(AfterStart)));
            harmony.Patch(AccessTools.Method(typeof(Menu_Dev_Update), "Init"), postfix: new HarmonyMethod(typeof(UpdateContentOverrides), nameof(MenuOpened)));
            harmony.Patch(AccessTools.Method(typeof(Menu_Dev_Update), "UpdateGUI"), postfix: new HarmonyMethod(typeof(UpdateContentOverrides), nameof(RefreshLabels)));
            harmony.Patch(AccessTools.Method(typeof(taskUpdate), "Complete"), prefix: new HarmonyMethod(typeof(UpdateContentOverrides), nameof(BeforeComplete)), postfix: new HarmonyMethod(typeof(UpdateContentOverrides), nameof(AfterCompleteAll)));
            harmony.Patch(AccessTools.Method(typeof(taskUpdate), "Abbrechen"), prefix: new HarmonyMethod(typeof(UpdateContentOverrides), nameof(BeforeCancel)));
            var playerInit = AccessTools.Method(typeof(mainScript), "InitNewGame");
            if (playerInit != null)
                harmony.Patch(playerInit, postfix: new HarmonyMethod(typeof(UpdateContentOverrides), nameof(AfterPlayerInitialized)));
            var gameLoad = AccessTools.Method(typeof(savegameScript), "Load");
            if (gameLoad != null)
                harmony.Patch(gameLoad, postfix: new HarmonyMethod(typeof(UpdateContentOverrides), nameof(AfterGameLoaded)));
            log.LogInfo("Update content patches installed: per-item production points, quality and workload. Costs remain vanilla.");
        }

        internal void Uninstall() { harmony?.UnpatchSelf(); if (active == this) active = null; }

        internal void SetPlayer(mainScript source)
        {
            if (source != null) player = source;
        }

        internal void ResetToVanilla()
        {
            Enabled.Value = false;
            Maximum.Value = 100;
            for (int i = 0; i < Percent.Length; i++) Percent[i].Value = 2;
        }

        internal void DrawOptions()
        {
            Enabled.Value = GUILayout.Toggle(Enabled.Value, "Game Update content percentages");
            GUILayout.BeginHorizontal();
            GUILayout.Label("Safe update max", GUILayout.Width(140));
            int index = GUILayout.SelectionGrid(Math.Max(0, Array.IndexOf(Maxima, Maximum.Value)), Labels, Labels.Length);
            Maximum.Value = Maxima[index];
            for (int i = 0; i < Percent.Length; i++) Percent[i].Value = Mathf.Clamp(Percent[i].Value, 0, Maximum.Value);
            GUILayout.EndHorizontal();
            if (!Enabled.Value) return;
            if (menu == null || !menu.isActiveAndEnabled)
            {
                GUILayout.Label("Open Game Update to edit each content item's real contribution.");
                return;
            }
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(150));
            for (int i = 0; i < Percent.Length; i++)
            {
                GUILayout.BeginHorizontal();
                bool selected = GUILayout.Toggle(Selections(menu)[i], ItemName(i), GUILayout.Width(230));
                int value = Mathf.RoundToInt(GUILayout.HorizontalSlider(Percent[i].Value, 0, Maximum.Value));
                string typed = GUILayout.TextField(Percent[i].Value.ToString(CultureInfo.InvariantCulture), GUILayout.Width(65));
                int parsed;
                if (int.TryParse(typed, out parsed) && typed != Percent[i].Value.ToString(CultureInfo.InvariantCulture)) value = parsed;
                value = Mathf.Clamp(value, 0, Maximum.Value);
                GUILayout.Label("%", GUILayout.Width(20));
                GUILayout.EndHorizontal();
                if (value != Percent[i].Value || selected != Selections(menu)[i])
                {
                    Percent[i].Value = value;
                    Selections(menu)[i] = selected;
                    AccessTools.Method(typeof(Menu_Dev_Update), "UpdateGUI").Invoke(menu, null);
                }
            }
            GUILayout.EndScrollView();
        }

        private string ItemName(int index)
        {
            var button = menu.uiObjects[17 + index];
            if (button != null)
                foreach (Text text in button.GetComponentsInChildren<Text>(true))
                    if (!string.IsNullOrWhiteSpace(text.text)) return text.text;
            return "Content " + (index + 1) + " (" + new[] { "Gameplay", "Graphics", "Sound", "Technical" }[index / 2] + ")";
        }

        private static void MenuOpened(Menu_Dev_Update __instance)
        {
            if (active == null) return;
            active.menu = __instance;
        }

        private static void AfterPlayerInitialized(mainScript __instance)
        {
            if (active == null || __instance == null) return;
            active.SetPlayer(__instance);
            Plugin.SetGame(__instance);
        }

        private static void AfterGameLoaded()
        {
            if (active == null) return;
            mainScript player = Plugin.CurrentGame;
            if (player == null) player = UnityEngine.Object.FindObjectOfType<mainScript>();
            if (player == null) return;
            active.SetPlayer(player);
            Plugin.SetGame(player);
        }

        private static float Weight(int item)
        {
            return active != null && active.Enabled.Value ? Mathf.Clamp(active.Percent[item].Value, 0, active.Maximum.Value) / 2f : 1f;
        }

        private static void RefreshLabels(Menu_Dev_Update __instance)
        {
            for (int i = 0; i < 8; i++)
                foreach (Text text in __instance.uiObjects[17 + i].GetComponentsInChildren<Text>(true))
                    if (text.text.Trim().StartsWith("+", StringComparison.Ordinal) && text.text.Trim().EndsWith("%", StringComparison.Ordinal))
                        text.text = "+" + (Weight(i) * 2f).ToString("0", CultureInfo.InvariantCulture) + "%";
        }

        private struct BonusSample
        {
            internal gameScript Game;
            internal float Before;
            internal float Quality;
            internal int Count;
        }
        private struct StartSample
        {
            internal HashSet<int> ExistingTasks;
            internal double QualityDelta;
            internal double WorkloadDelta;
            internal double[] CategoryGains;
        }

        private static void BeforeCancel(taskUpdate __instance)
        {
            if (active != null && __instance != null) active.pendingTaskGains.Remove(__instance.GetInstanceID());
        }

        private static void BeforeComplete(taskUpdate __instance, out BonusSample __state)
        {
            __state = default;
            if (active == null || __instance.quality <= 1f) return;
            FindTaskGameMethod.Invoke(__instance, null);
            var game = (gameScript)TaskGameField.GetValue(__instance);
            if (game == null || active.player == null || game.developerID != active.player.myID) return;
            __state = new BonusSample { Game = game, Before = game.bonusSellsUpdates, Quality = __instance.quality, Count = Math.Max(0, game.amountUpdates) };
        }

        private static void AfterCompleteAll(taskUpdate __instance, BonusSample __state)
        {
            if (__state.Game != null)
            {
                double value = __state.Before + (double)__state.Quality / ((double)__state.Count + 1d);
                if (!double.IsNaN(value) && !double.IsInfinity(value) && value >= 0)
                    __state.Game.bonusSellsUpdates = Mathf.Clamp((float)Math.Min(value, SafeUpdateBonusCap), 0f, SafeUpdateBonusCap);
            }
            if (active == null || __instance == null) return;
            double[] gains;
            if (!active.pendingTaskGains.TryGetValue(__instance.GetInstanceID(), out gains)) return;
            active.pendingTaskGains.Remove(__instance.GetInstanceID());
            gameScript game = (gameScript)TaskGameField.GetValue(__instance);
            if (game == null || gains == null || gains.Length < 4) return;
            game.points_gameplay = AddExactGain(game.points_gameplay, gains[0], __instance.pointsGameplay, "gameplay", __instance.myID);
            game.points_grafik = AddExactGain(game.points_grafik, gains[1], __instance.pointsGrafik, "graphics", __instance.myID);
            game.points_sound = AddExactGain(game.points_sound, gains[2], __instance.pointsSound, "sound", __instance.myID);
            game.points_technik = AddExactGain(game.points_technik, gains[3], __instance.pointsTechnik, "technical", __instance.myID);
            game.bonusSellsUpdates = Mathf.Clamp(game.bonusSellsUpdates, 0f, SafeUpdateBonusCap);
        }

        private static float AddExactGain(float current, double exactGain, int vanillaGain, string field, int taskId)
        {
            if (double.IsNaN(exactGain) || double.IsInfinity(exactGain))
            {
                if (TryMarkClampWarning(field))
                    active.log.LogWarning("Ignored non-finite exact gain for " + field + " task=" + taskId + ".");
                return ClampSafe(current, field, taskId);
            }
            if (float.IsNaN(current) || float.IsInfinity(current))
            {
                if (TryMarkClampWarning(field))
                    active.log.LogWarning("Reset non-finite current value for " + field + " task=" + taskId + ".");
                current = 0f;
            }
            double corrected = (double)current + exactGain - vanillaGain;
            if (corrected <= 0d) return 0f;
            if (corrected >= SafeCategoryPointCap)
            {
                if (TryMarkClampWarning(field))
                    active.log.LogWarning("Clamped final game field " + field + " for task=" + taskId + " to safe cap " + SafeCategoryPointCap.ToString("R", CultureInfo.InvariantCulture));
                return SafeCategoryPointCap;
            }
            return (float)corrected;
        }

        private static void BeforeStart(Menu_Dev_Update __instance, out StartSample __state)
        {
            __state = default;
            if (active == null || !active.Enabled.Value) return;
            var game = (gameScript)UpdateGameField.GetValue(__instance);
            if (game == null) return;
            game.points_gameplay = ClampSafe(game.points_gameplay, "gameplay", game.myID);
            game.points_grafik = ClampSafe(game.points_grafik, "graphics", game.myID);
            game.points_sound = ClampSafe(game.points_sound, "sound", game.myID);
            game.points_technik = ClampSafe(game.points_technik, "technical", game.myID);
            game.bonusSellsUpdates = ClampSafe(game.bonusSellsUpdates, "update bonus", game.myID, SafeUpdateBonusCap);
            __state.ExistingTasks = new HashSet<int>(UnityEngine.Object.FindObjectsOfType<taskUpdate>().Select(t => t.GetInstanceID()));
            double qualityDelta = 0, workloadDelta = 0;
            double devPoints = game.GetGesamtDevPoints();
            if (double.IsNaN(devPoints) || double.IsInfinity(devPoints) || devPoints < 0d)
            {
                active.log.LogWarning("Reset unsafe development points for game=" + game.myID + " before update start.");
                devPoints = 0d;
            }
            for (int i = 0; i < 8; i++)
            {
                if (!Selections(__instance)[i]) continue;
                qualityDelta += 0.1d * (Weight(i) - 1d);
                workloadDelta += Math.Round(devPoints * 0.02d * Weight(i), MidpointRounding.ToEven) - Math.Round(devPoints * 0.02d, MidpointRounding.ToEven);
            }
            if (double.IsNaN(workloadDelta) || double.IsInfinity(workloadDelta))
            {
                active.log.LogWarning("Reset unsafe update workload adjustment for game=" + game.myID + ".");
                workloadDelta = 0d;
            }
            __state.QualityDelta = qualityDelta;
            __state.WorkloadDelta = workloadDelta;
            __state.CategoryGains = new double[4];
            for (int category = 0; category < 4; category++)
            {
                double points = category == 0 ? game.points_gameplay : category == 1 ? game.points_grafik : category == 2 ? game.points_sound : game.points_technik;
                for (int item = category * 2; item < category * 2 + 2; item++)
                    if (Selections(__instance)[item] && Weight(item) > 0f)
                        __state.CategoryGains[category] += 1d + (double)points * 0.02d * Weight(item);
            }
        }

        private static float ClampSafe(float value, string field, int gameId, float limit = SafeCategoryPointCap)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f || value > limit)
            {
                if (TryMarkClampWarning(field))
                    active.log.LogWarning("Clamped unsafe " + field + " for game=" + gameId + " to " + limit.ToString("R", CultureInfo.InvariantCulture));
                return Mathf.Clamp(float.IsNaN(value) || float.IsInfinity(value) ? 0f : value, 0f, limit);
            }
            return value;
        }

        private static bool TryMarkClampWarning(string field)
        {
            int bit = field == "gameplay" ? 1
                : field == "graphics" ? 2
                : field == "sound" ? 4
                : field == "technical" ? 8
                : field == "update bonus" ? 16
                : 0;
            if (bit == 0 || (loggedClampWarnings & bit) != 0) return false;
            loggedClampWarnings |= bit;
            return true;
        }

        private static void AfterStart(StartSample __state)
        {
            if (active == null || __state.ExistingTasks == null) return;
            var task = UnityEngine.Object.FindObjectsOfType<taskUpdate>().FirstOrDefault(t => !__state.ExistingTasks.Contains(t.GetInstanceID()));
            if (task == null) { active.log.LogWarning("No new update task found after BUTTON_Start."); return; }
            double quality = task.quality + __state.QualityDelta;
            double points = task.points + __state.WorkloadDelta;
            if (double.IsNaN(quality) || double.IsInfinity(quality) || double.IsNaN(points) || double.IsInfinity(points) ||
                quality < 0 || quality > float.MaxValue || points < 0 || points > float.MaxValue)
            { active.log.LogWarning("Skipped unsafe update task scaling: " + task.myID); return; }
            task.quality = (float)quality;
            task.points = task.pointsLeft = (float)points;
            if (__state.CategoryGains != null) active.pendingTaskGains[task.GetInstanceID()] = __state.CategoryGains;
        }

        private static bool CalculatePoints(Menu_Dev_Update __instance, MethodBase __originalMethod, ref int __result)
        {
            if (active == null || !active.Enabled.Value) return true;
            var game = (gameScript)UpdateGameField.GetValue(__instance);
            if (game == null) return true;
            int category = __originalMethod.Name == "GetP_Gameplay" ? 0 : __originalMethod.Name == "GetP_Grafik" ? 1 : __originalMethod.Name == "GetP_Sound" ? 2 : 3;
            double points = category == 0 ? game.points_gameplay : category == 1 ? game.points_grafik : category == 2 ? game.points_sound : game.points_technik;
            points = double.IsNaN(points) || double.IsInfinity(points) ? 0d : Math.Max(0d, Math.Min(points, SafeCategoryPointCap));
            double total = 0d;
            for (int i = category * 2; i < category * 2 + 2; i++)
                if (Selections(__instance)[i] && Weight(i) > 0) total += 1d + points * 0.02d * Weight(i);
            double rounded = Math.Round(total, MidpointRounding.ToEven);
            __result = double.IsNaN(rounded) || rounded <= 0d ? 0 : rounded >= int.MaxValue ? int.MaxValue : (int)rounded;
            return false;
        }

    }
}
