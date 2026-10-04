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
        internal readonly ConfigEntry<bool> TraceSales;
        internal static bool IsTracing => active != null && active.TraceSales.Value;
        internal readonly ConfigEntry<int>[] Percent = new ConfigEntry<int>[8];
        private readonly ManualLogSource log;
        private Harmony harmony;
        private Menu_Dev_Update menu;
        private Vector2 scroll;
        private readonly Dictionary<int, double[]> pendingTaskGains = new Dictionary<int, double[]>();
        private static readonly FieldInfo SelectionField = AccessTools.Field(typeof(Menu_Dev_Update), "buttonAdds");
        private static bool[] Selections(Menu_Dev_Update source) { return (bool[])SelectionField.GetValue(source); }

        internal UpdateContentOverrides(ConfigFile config, ManualLogSource log)
        {
            this.log = log;
            Enabled = config.Bind("Game Update", "Enabled", false, "Scale real update content contributions. Disabled uses vanilla calculations.");
            TraceSales = config.Bind("Game Update", "TraceSales", false, "Temporarily log player game sales ticks and update task state for diagnosis.");
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
            harmony.Patch(AccessTools.Method(typeof(taskUpdate), "Complete"), postfix: new HarmonyMethod(typeof(UpdateContentOverrides), nameof(AfterCompleteExact)));
            harmony.Patch(AccessTools.Method(typeof(taskUpdate), "Abbrechen"), prefix: new HarmonyMethod(typeof(UpdateContentOverrides), nameof(BeforeCancel)));
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
            internal double QualityDelta;
            internal double WorkloadDelta;
            internal double[] CategoryGains;
        }

        private struct SalesSample
        {
            internal long Units;
            internal long Revenue;
            internal bool OnMarket;
            internal bool Developing;
            internal float Bonus;
            internal long Cash;
        }

        private static bool ShouldTrace(gameScript game)
        {
            if (active == null || !active.TraceSales.Value || game == null) return false;
            var main = UnityEngine.Object.FindObjectOfType<mainScript>();
            return main != null && game.developerID == main.myID;
        }

        private static int ActiveUpdates(gameScript game)
        {
            if (game == null) return 0;
            return UnityEngine.Object.FindObjectsOfType<taskUpdate>().Count(t => t.targetID == game.myID);
        }

        private static void BeforeSell(gameScript __instance, out SalesSample __state)
        {
            __state = default;
            if (!ShouldTrace(__instance)) return;
            var main = UnityEngine.Object.FindObjectOfType<mainScript>();
            __state = new SalesSample
            {
                Units = __instance.sellsTotal,
                Revenue = __instance.umsatzTotal,
                OnMarket = __instance.isOnMarket,
                Developing = __instance.inDevelopment,
                Bonus = __instance.bonusSellsUpdates,
                Cash = main.money
            };
            active.log.LogInfo("SALE before game=" + __instance.myID + " market=" + __state.OnMarket +
                " developing=" + __state.Developing + " updates=" + ActiveUpdates(__instance) +
                " bonus=" + __state.Bonus + " units=" + __state.Units + " revenue=" + __state.Revenue +
                " cash=" + __state.Cash + " weekly=" + WeeklySales(__instance) +
                " inputs=" + SalesInputs(__instance, main));
        }

        private static void AfterSell(gameScript __instance, SalesSample __state)
        {
            if (!ShouldTrace(__instance)) return;
            var main = UnityEngine.Object.FindObjectOfType<mainScript>();
            active.log.LogInfo("SALE after game=" + __instance.myID + " market=" + __instance.isOnMarket +
                " developing=" + __instance.inDevelopment + " updates=" + ActiveUpdates(__instance) +
                " bonus=" + __instance.bonusSellsUpdates + " unitsDelta=" + (__instance.sellsTotal - __state.Units) +
                " revenueDelta=" + (__instance.umsatzTotal - __state.Revenue) +
                " unitsStored=" + __instance.sellsTotal + " revenueStored=" + __instance.umsatzTotal +
                " cashDelta=" + (main.money - __state.Cash) + " cashStored=" + main.money +
                " weekly=" + WeeklySales(__instance) + " inputs=" + SalesInputs(__instance, main));
        }

        private static string SalesInputs(gameScript game, mainScript main)
        {
            int fans = -1;
            if (game.genres_ != null && game.genres_.genres_FANS != null &&
                game.maingenre >= 0 && game.maingenre < game.genres_.genres_FANS.Length)
                fans = game.genres_.genres_FANS[game.maingenre];
            string numeric = NumericState(game);
            return "week=" + main.week + " age=" + game.weeksOnMarket +
                " review=" + game.reviewTotal + "/" + game.reviewGameplay + "/" +
                game.reviewGrafik + "/" + game.reviewSound + "/" + game.reviewSteuerung +
                " ap=" + game.gameAP_Gameplay + "/" + game.gameAP_Grafik + "/" +
                game.gameAP_Sound + "/" + game.gameAP_Technik +
                " points=" + game.points_gameplay.ToString("R", CultureInfo.InvariantCulture) + "/" +
                game.points_grafik.ToString("R", CultureInfo.InvariantCulture) + "/" +
                game.points_sound.ToString("R", CultureInfo.InvariantCulture) + "/" +
                game.points_technik.ToString("R", CultureInfo.InvariantCulture) +
                " hype=" + game.hype.ToString("R", CultureInfo.InvariantCulture) +
                " genreFans=" + fans + " prices=" + (game.verkaufspreis == null ? "null" :
                    string.Join("/", game.verkaufspreis.Select(x => x.ToString()).ToArray())) +
                " numeric=" + numeric;
        }

        private static string NumericState(gameScript game)
        {
            float[] values = { game.points_gameplay, game.points_grafik, game.points_sound,
                game.points_technik, game.hype, game.bonusSellsUpdates };
            string[] names = { "gameplay", "graphics", "sound", "technical", "hype", "updateBonus" };
            var flags = new List<string>();
            for (int i = 0; i < values.Length; i++)
            {
                float value = values[i];
                if (float.IsNaN(value)) flags.Add(names[i] + ":NaN");
                else if (float.IsInfinity(value)) flags.Add(names[i] + ":Infinity");
                else if (value < 0f) flags.Add(names[i] + ":negative");
                else if (value >= int.MaxValue) flags.Add(names[i] + ":Int32MaxOrAbove");
            }
            if (game.sellsPerWeek != null && game.sellsPerWeek.Any(x => x == int.MaxValue || x == int.MinValue))
                flags.Add("weekly:Int32Boundary");
            return flags.Count == 0 ? "ok" : string.Join(",", flags.ToArray());
        }

        private static string WeeklySales(gameScript game)
        {
            return game.sellsPerWeek == null ? "null" : string.Join("/", game.sellsPerWeek.Select(x => x.ToString()).ToArray());
        }

        private static void TraceBeforeComplete(taskUpdate __instance)
        {
            var game = (gameScript)AccessTools.Field(typeof(taskUpdate), "gS_").GetValue(__instance);
            if (ShouldTrace(game))
                active.log.LogInfo("UPDATE completing task=" + __instance.myID + " game=" + game.myID +
                    " market=" + game.isOnMarket + " developing=" + game.inDevelopment +
                    " quality=" + __instance.quality + " workload=" + __instance.points +
                    " left=" + __instance.pointsLeft + " gains=" + __instance.pointsGameplay + "/" +
                    __instance.pointsGrafik + "/" + __instance.pointsSound + "/" + __instance.pointsTechnik +
                    " bonus=" + game.bonusSellsUpdates);
        }

        private static void TraceAfterComplete(taskUpdate __instance)
        {
            var game = (gameScript)AccessTools.Field(typeof(taskUpdate), "gS_").GetValue(__instance);
            if (ShouldTrace(game))
                active.log.LogInfo("UPDATE completed task=" + __instance.myID + " game=" + game.myID +
                    " market=" + game.isOnMarket + " developing=" + game.inDevelopment +
                    " updates=" + ActiveUpdates(game) + " bonus=" + game.bonusSellsUpdates +
                    " points=" + game.points_gameplay + "/" + game.points_grafik + "/" +
                    game.points_sound + "/" + game.points_technik);
        }

        private static void AfterWork(taskUpdate __instance)
        {
            var game = (gameScript)AccessTools.Field(typeof(taskUpdate), "gS_").GetValue(__instance);
            if (ShouldTrace(game))
                active.log.LogInfo("UPDATE work task=" + __instance.myID + " target=" + __instance.targetID +
                    " pointsLeft=" + __instance.pointsLeft + " quality=" + __instance.quality);
        }

        private static void TraceWorkCaller(characterScript __instance, roomScript __0)
        {
            if (active == null || !active.TraceSales.Value || __0 == null) return;
            var task = __0.GetTaskUpdate();
            if (task == null) return;
            var game = (gameScript)AccessTools.Field(typeof(taskUpdate), "gS_").GetValue(task);
            if (!ShouldTrace(game)) return;
            active.log.LogInfo("UPDATE caller task=" + task.myID + " game=" + game.myID +
                " character=" + __instance.myID + " name=" + __instance.myName +
                " characterRoom=" + __instance.roomID + " assignedRoom=" + (__instance.roomS_ == null ? -1 : __instance.roomS_.myID) +
                " targetRoom=" + __0.myID + " roomTask=" + __0.taskID +
                " usingObject=" + __instance.objectUsingID + " roomPaused=" + __0.pause);
        }

        private static void BeforeCancel(taskUpdate __instance)
        {
            if (active != null && __instance != null) active.pendingTaskGains.Remove(__instance.GetInstanceID());
            var game = (gameScript)AccessTools.Field(typeof(taskUpdate), "gS_").GetValue(__instance);
            if (ShouldTrace(game))
                active.log.LogInfo("UPDATE cancel task=" + __instance.myID + " target=" + __instance.targetID);
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

        private static float AddExactGain(float current, double exactGain, int vanillaGain, string field, int taskId)
        {
            if (double.IsNaN(exactGain) || double.IsInfinity(exactGain) || float.IsNaN(current) || float.IsInfinity(current)) return current;
            double corrected = (double)current + exactGain - vanillaGain;
            if (corrected <= 0d) return 0f;
            if (corrected >= float.MaxValue)
            {
                active.log.LogWarning("Clamped final game field " + field + " for task=" + taskId + " value=" + corrected.ToString("R", CultureInfo.InvariantCulture));
                return float.MaxValue;
            }
            return (float)corrected;
        }

        private static void AfterCompleteExact(taskUpdate __instance)
        {
            if (active == null || __instance == null) return;
            double[] gains;
            if (!active.pendingTaskGains.TryGetValue(__instance.GetInstanceID(), out gains)) return;
            active.pendingTaskGains.Remove(__instance.GetInstanceID());
            gameScript game = (gameScript)AccessTools.Field(typeof(taskUpdate), "gS_").GetValue(__instance);
            if (game == null || gains == null || gains.Length < 4) return;
            game.points_gameplay = AddExactGain(game.points_gameplay, gains[0], __instance.pointsGameplay, "gameplay", __instance.myID);
            game.points_grafik = AddExactGain(game.points_grafik, gains[1], __instance.pointsGrafik, "graphics", __instance.myID);
            game.points_sound = AddExactGain(game.points_sound, gains[2], __instance.pointsSound, "sound", __instance.myID);
            game.points_technik = AddExactGain(game.points_technik, gains[3], __instance.pointsTechnik, "technical", __instance.myID);
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
            if (__state.CategoryGains != null) active.pendingTaskGains[task.GetInstanceID()] = __state.CategoryGains;
            active.log.LogInfo("UPDATE started task=" + task.myID + " game=" + task.targetID +
                " quality=" + task.quality + " workload=" + task.points +
                " gains=" + task.pointsGameplay + "/" + task.pointsGrafik + "/" +
                task.pointsSound + "/" + task.pointsTechnik);
        }

        private static bool CalculatePoints(Menu_Dev_Update __instance, MethodBase __originalMethod, ref int __result)
        {
            if (active == null || !active.Enabled.Value) return true;
            var game = (gameScript)AccessTools.Field(typeof(Menu_Dev_Update), "gS_").GetValue(__instance);
            if (game == null) return true;
            int category = __originalMethod.Name == "GetP_Gameplay" ? 0 : __originalMethod.Name == "GetP_Grafik" ? 1 : __originalMethod.Name == "GetP_Sound" ? 2 : 3;
            double points = category == 0 ? game.points_gameplay : category == 1 ? game.points_grafik : category == 2 ? game.points_sound : game.points_technik;
            double total = 0d;
            for (int i = category * 2; i < category * 2 + 2; i++)
                if (Selections(__instance)[i] && Weight(i) > 0) total += 1d + points * 0.02d * Weight(i);
            double rounded = Math.Round(total, MidpointRounding.ToEven);
            __result = rounded >= int.MaxValue ? int.MaxValue : rounded <= 0d ? 0 : (int)rounded;
            return false;
        }

    }
}

