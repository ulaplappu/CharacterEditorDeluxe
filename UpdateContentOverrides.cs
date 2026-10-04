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
        private static readonly int[] Maxima = { 100, 200, 400, 1000, 9999 };
        private static readonly string[] Labels = { "100", "200", "400", "1000", "9999" };
        private static UpdateContentOverrides active;
        internal readonly ConfigEntry<bool> Enabled;
        internal readonly ConfigEntry<int> Maximum;
        internal readonly ConfigEntry<int>[] Percent = new ConfigEntry<int>[8];
        private readonly ManualLogSource log;
        private Harmony harmony;
        private Menu_Dev_Update menu;
        private Vector2 scroll;
        private static readonly FieldInfo SelectionField = AccessTools.Field(typeof(Menu_Dev_Update), "buttonAdds");
        private static bool[] Selections(Menu_Dev_Update source) { return (bool[])SelectionField.GetValue(source); }

        internal UpdateContentOverrides(ConfigFile config, ManualLogSource log)
        {
            this.log = log;
            Enabled = config.Bind("Game Update", "Enabled", false, "Scale real update content contributions. Disabled uses vanilla calculations.");
            Maximum = config.Bind("Game Update", "Maximum", 100, "Update % Max: 100, 200, 400, 1000, 9999.");
            if (Array.IndexOf(Maxima, Maximum.Value) < 0) Maximum.Value = 100;
            for (int i = 0; i < Percent.Length; i++)
                Percent[i] = config.Bind("Game Update", "Content" + i, 2, "Actual percentage of category points added by this item. Vanilla is 2% (0-9999).");
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
            harmony.Patch(AccessTools.Method(typeof(taskUpdate), "Complete"), prefix: new HarmonyMethod(typeof(UpdateContentOverrides), nameof(BeforeComplete)), postfix: new HarmonyMethod(typeof(UpdateContentOverrides), nameof(AfterComplete)));
            log.LogInfo("Update content patches installed: per-item production points, quality and workload. Costs remain vanilla.");
        }

        internal void Uninstall() { harmony?.UnpatchSelf(); if (active == this) active = null; }

        internal void DrawOptions()
        {
            Enabled.Value = GUILayout.Toggle(Enabled.Value, "Game Update content percentages");
            GUILayout.BeginHorizontal();
            GUILayout.Label("Update % Max", GUILayout.Width(140));
            int index = GUILayout.SelectionGrid(Math.Max(0, Array.IndexOf(Maxima, Maximum.Value)), Labels, Labels.Length);
            Maximum.Value = Maxima[index];
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
            active.log.LogInfo("Update menu opened content%=" + string.Join("/", Array.ConvertAll(active.Percent, p => p.Value)));
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
            internal float QualityDelta;
            internal float WorkloadDelta;
        }

        private static void BeforeComplete(taskUpdate __instance, out BonusSample __state)
        {
            __state = default;
            if (active == null || __instance.quality <= 1f) return;
            AccessTools.Method(typeof(taskUpdate), "FindMyObject").Invoke(__instance, null);
            var game = (gameScript)AccessTools.Field(typeof(taskUpdate), "gS_").GetValue(__instance);
            var player = UnityEngine.Object.FindObjectOfType<mainScript>();
            if (game == null || player == null || game.developerID != player.myID) return;
            __state = new BonusSample { Game = game, Before = game.bonusSellsUpdates, Quality = __instance.quality, Count = game.amountUpdates };
        }

        private static void AfterComplete(BonusSample __state)
        {
            if (__state.Game == null) return;
            double value = __state.Before + (double)__state.Quality / (__state.Count + 1);
            if (!double.IsNaN(value) && value >= 0 && value <= float.MaxValue)
                __state.Game.bonusSellsUpdates = (float)value;
        }

        private static void BeforeStart(Menu_Dev_Update __instance, out StartSample __state)
        {
            __state = default;
            if (active == null || !active.Enabled.Value) return;
            var game = (gameScript)AccessTools.Field(typeof(Menu_Dev_Update), "gS_").GetValue(__instance);
            if (game == null) return;
            __state.ExistingTasks = new HashSet<int>(UnityEngine.Object.FindObjectsOfType<taskUpdate>().Select(t => t.GetInstanceID()));
            double qualityDelta = 0, workloadDelta = 0;
            double devPoints = game.GetGesamtDevPoints();
            for (int i = 0; i < 8; i++)
            {
                if (!Selections(__instance)[i]) continue;
                qualityDelta += 0.1d * (Weight(i) - 1d);
                workloadDelta += Math.Round(devPoints * 0.02d * Weight(i), MidpointRounding.ToEven) - Math.Round(devPoints * 0.02d, MidpointRounding.ToEven);
            }
            __state.QualityDelta = (float)qualityDelta;
            __state.WorkloadDelta = (float)workloadDelta;
        }

        private static void AfterStart(StartSample __state)
        {
            if (active == null || __state.ExistingTasks == null) return;
            var task = UnityEngine.Object.FindObjectsOfType<taskUpdate>().FirstOrDefault(t => !__state.ExistingTasks.Contains(t.GetInstanceID()));
            if (task == null) { active.log.LogWarning("No new update task found after BUTTON_Start."); return; }
            double quality = task.quality + __state.QualityDelta;
            double points = task.points + __state.WorkloadDelta;
            if (quality < 0 || quality > float.MaxValue || points < 0 || points > float.MaxValue)
            { active.log.LogWarning("Skipped unsafe update task scaling: " + task.myID); return; }
            task.quality = (float)quality;
            task.points = task.pointsLeft = (float)points;
            active.log.LogInfo("Update task " + task.myID + " content quality=" + task.quality + " work=" + task.points + " gains=" + task.pointsGameplay + "/" + task.pointsGrafik + "/" + task.pointsSound + "/" + task.pointsTechnik);
        }

        private static bool CalculatePoints(Menu_Dev_Update __instance, MethodBase __originalMethod, ref int __result)
        {
            if (active == null || !active.Enabled.Value) return true;
            var game = (gameScript)AccessTools.Field(typeof(Menu_Dev_Update), "gS_").GetValue(__instance);
            if (game == null) return true;
            int category = __originalMethod.Name == "GetP_Gameplay" ? 0 : __originalMethod.Name == "GetP_Grafik" ? 1 : __originalMethod.Name == "GetP_Sound" ? 2 : 3;
            float points = category == 0 ? game.points_gameplay : category == 1 ? game.points_grafik : category == 2 ? game.points_sound : game.points_technik;
            float total = 0;
            for (int i = category * 2; i < category * 2 + 2; i++)
                if (Selections(__instance)[i] && Weight(i) > 0) total += 1f + points * 0.02f * Weight(i);
            __result = (int)Math.Min(int.MaxValue, Math.Max(0d, Math.Round(total, MidpointRounding.ToEven)));
            return false;
        }

    }
}

