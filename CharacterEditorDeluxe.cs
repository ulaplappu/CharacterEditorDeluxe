using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace CharacterEditorDeluxe
{
    [BepInPlugin("com.codex.mgt2.charactereditordeluxe", "MGT2 Character Editor Deluxe", "1.6.1")]
    public sealed class Plugin : BaseUnityPlugin
    {
        private static readonly string[] StatFields = {
            "s_motivation", "s_gamedesign", "s_programmieren", "s_grafik", "s_sound",
            "s_pr", "s_gametests", "s_technik", "s_forschen"
        };
        private static readonly string[] StatLabels = {
            "Motivation", "Game Design", "Programming", "Graphics", "Sound",
            "PR", "Game Testing", "Technology", "Research"
        };
        private static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private ConfigEntry<bool> autoMaxNewEmployees;
        private ConfigEntry<int> configuredCap;
        private ConfigEntry<float> savedWindowX;
        private ConfigEntry<float> savedWindowY;
        private ConfigEntry<float> savedWindowWidth;
        private ConfigEntry<float> savedWindowHeight;
        private mainScript game;
        private mainScript trackedGame;
        private readonly HashSet<characterScript> trackedCharacters = new HashSet<characterScript>();
        private readonly Dictionary<characterScript, float> pendingNewCharacters = new Dictionary<characterScript, float>();
        private bool visible;
        private Vector2 scroll;
        private int selectedIndex;
        private characterScript selected;
        private readonly float[] stagedStats = new float[9];
        private readonly bool[] statDirty = new bool[9];
        private readonly string[] statInputs = new string[9];
        private bool[] stagedPerks = new bool[0];
        private string status = "Select an employee, adjust values, then apply.";
        private float nextLookup;
        private int cap;
        private bool uiErrorLogged;
        private Rect windowRect;
        private bool resizing;
        private Vector2 resizeStartMouse;
        private Vector2 resizeStartSize;
        private bool geometryDirty;
        private GUI_Main inputGuardGui;
        private bool inputGuardPreviousMenuOpen;
        private bool inputGuardPreviousSelectInputField;
        private bool inputGuardApplied;
        private PerkInfo[] perkCatalog = new PerkInfo[0];
        private GameObject[] perkCatalogSource;
        private textScript perkTextSource;
        private GUIStyle perkTooltipStyle;
        private StatOverrides overrides;
        private DesignPriorityOverrides designPriorities;
        private UpdateContentOverrides updateContent;

        private sealed class PerkInfo
        {
            public string Name;
            public string Description;
        }

        private const float MinimumWindowWidth = 560f;
        private const float MinimumWindowHeight = 420f;
        private const float DefaultWindowWidth = 780f;
        private const float DefaultWindowHeight = 700f;
        private const float TitleBarHeight = 24f;
        private const float ResizeGripSize = 20f;

        private void Awake()
        {
            autoMaxNewEmployees = Config.Bind("General", "AutoMaxNewEmployees", false, "Automatically max newly added employees after initialization.");
            configuredCap = Config.Bind("Stats", "StatCap", 100, "Maximum stat value (1 to 9999). Default game scale is 0 to 100.");
            var globalLock = Config.Bind("Locks", "LockEditedStats", true, "Keep edited skills at their assigned values during work, training and save/load.");
            savedWindowX = Config.Bind("Window", "X", 30f, "Saved editor window X position.");
            savedWindowY = Config.Bind("Window", "Y", 30f, "Saved editor window Y position.");
            savedWindowWidth = Config.Bind("Window", "Width", DefaultWindowWidth, "Saved editor window width.");
            savedWindowHeight = Config.Bind("Window", "Height", DefaultWindowHeight, "Saved editor window height.");
            cap = Mathf.Clamp(configuredCap.Value, 1, 9999);
            configuredCap.Value = cap;
            windowRect = new Rect(savedWindowX.Value, savedWindowY.Value, savedWindowWidth.Value, savedWindowHeight.Value);
            ClampWindowToScreen();
            overrides = new StatOverrides(Logger, globalLock);
            overrides.Install();
            designPriorities = new DesignPriorityOverrides(Logger,
                Config.Bind("Design Priority", "UncappedDesignPriority", false, "Allow player game design priorities above 100%."),
                Config.Bind("Design Priority", "PriorityMax", 100, "Maximum priority per slider: 100, 200, 500, 1000, 9999, or 100000 percent."),
                Config.Bind("Design Priority", "AllowTotalAbove100", true, "Allow a total above 100% while the priority cheat is enabled."));
            designPriorities.Install();
            updateContent = new UpdateContentOverrides(Config, Logger);
            updateContent.Install();
            Logger.LogInfo("Character Editor Deluxe loaded. Toggle with F8.");
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F8)) SetVisible(!visible);

            UpdateGameplayInputGuard();

            if (visible && IsMouseOverWindow())
                Input.ResetInputAxes();

            if (geometryDirty && !Input.GetMouseButton(0))
                SaveWindowGeometry();

            if (Time.unscaledTime >= nextLookup)
            {
                nextLookup = Time.unscaledTime + 1f;
                if (game == null) game = UnityEngine.Object.FindObjectOfType<mainScript>();
                TrackNewEmployees();
                RefreshPerkCatalog();
                designPriorities.UpdateMenu();
            }
        }

        private void OnGUI()
        {
            if (!visible) return;
            try
            {
                ClampWindowToScreen();
                Rect previous = windowRect;
                windowRect = GUI.Window(740219, windowRect, DrawWindow, "Mad Games Tycoon 2 Character Editor Deluxe");
                DrawPerkTooltip();
                if (windowRect.position != previous.position || windowRect.size != previous.size) geometryDirty = true;
                ClampWindowToScreen();
                Event current = Event.current;
                if (geometryDirty && current != null && current.type == EventType.MouseUp) SaveWindowGeometry();
                uiErrorLogged = false;
            }
            catch (Exception ex)
            {
                if (!uiErrorLogged) Logger.LogError("Editor UI recovered from an error: " + ex);
                uiErrorLogged = true;
            }
        }

        private void DrawWindow(int id)
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Close", GUILayout.Width(70))) SetVisible(false);
            GUILayout.Label("Toggle: F8 | Default stat range: 0-100 | Current cap: " + cap);
            GUILayout.EndHorizontal();
            cap = Mathf.Clamp(cap, 1, 9999);
            if (configuredCap.Value != cap) configuredCap.Value = cap;
            autoMaxNewEmployees.Value = GUILayout.Toggle(autoMaxNewEmployees.Value, "Auto-max newly hired employees (stats to configured cap, all perks)");
            designPriorities.DrawOptions();
            updateContent.DrawOptions();
            bool globalLock = GUILayout.Toggle(overrides.GlobalLock.Value, "Lock Edited Stats (global)");
            if (globalLock != overrides.GlobalLock.Value)
            {
                overrides.GlobalLock.Value = globalLock;
                if (globalLock) overrides.RestoreAllLocked();
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label("Custom stat cap", GUILayout.Width(110));
            string capText = GUILayout.TextField(cap.ToString(CultureInfo.InvariantCulture), GUILayout.Width(70));
            int parsed;
            if (int.TryParse(capText, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) cap = Mathf.Clamp(parsed, 1, 9999);
            if (GUILayout.Button("Use configured cap", GUILayout.Width(140))) cap = Mathf.Clamp(configuredCap.Value, 1, 9999);
            GUILayout.EndHorizontal();
            RefreshPerkCatalog();
            var employees = GetEmployees();
            if (employees.Count == 0)
            {
                GUILayout.Label("No active employee data found. Load or start a game, then reopen this editor.");
                HandleResize();
                GUI.DragWindow(new Rect(0, 0, windowRect.width, TitleBarHeight));
                return;
            }
            if (selectedIndex >= employees.Count) selectedIndex = 0;
            if (selected != employees[selectedIndex]) LoadSelection(employees[selectedIndex]);
            RefreshCurrentStats();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("<", GUILayout.Width(35))) ChangeSelection(-1, employees);
            GUILayout.Label(GetCharacterLabel(employees[selectedIndex], selectedIndex) + "  (" + (selectedIndex + 1) + "/" + employees.Count + ")", GUILayout.Width(350));
            if (GUILayout.Button(">", GUILayout.Width(35))) ChangeSelection(1, employees);
            GUILayout.Label("Perks are independent flags; there is no point pool in the game data.");
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            bool statsLock = GUILayout.Toggle(overrides.IsStatsLocked(selected), "Lock Stats", GUILayout.Width(130));
            if (statsLock != overrides.IsStatsLocked(selected)) overrides.SetStatsLock(selected, statsLock);
            bool motivationLock = GUILayout.Toggle(overrides.IsMotivationLocked(selected), "Lock Motivation", GUILayout.Width(160));
            if (motivationLock != overrides.IsMotivationLocked(selected)) overrides.SetMotivationLock(selected, motivationLock);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Max All Stats")) for (int i = 0; i < stagedStats.Length; i++) SetStagedStat(i, cap);
            if (GUILayout.Button("Unlock All Perks")) for (int i = 0; i < stagedPerks.Length; i++) stagedPerks[i] = true;
            if (GUILayout.Button("Clear All Perks")) for (int i = 0; i < stagedPerks.Length; i++) stagedPerks[i] = false;
            GUILayout.EndHorizontal();

            scroll = GUILayout.BeginScrollView(scroll, GUILayout.ExpandHeight(true));
            GUILayout.Label("Character stats");
            for (int i = 0; i < StatFields.Length; i++)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(StatLabels[i] + " (" + ReadStat(selected, i).ToString("0.##", CultureInfo.InvariantCulture) + ")", GUILayout.Width(165));
                float sliderValue = Mathf.Clamp(stagedStats[i], 0f, cap);
                float val = GUILayout.HorizontalSlider(sliderValue, 0f, cap, GUILayout.Width(330));
                if (Math.Abs(val - sliderValue) > 0.01f) SetStagedStat(i, Mathf.Round(val));
                string typed = GUILayout.TextField(statInputs[i], GUILayout.Width(80));
                if (typed != statInputs[i])
                {
                    statInputs[i] = typed;
                    float n;
                    if (float.TryParse(typed, NumberStyles.Float, CultureInfo.InvariantCulture, out n) && !float.IsNaN(n) && !float.IsInfinity(n))
                    {
                        stagedStats[i] = Mathf.Clamp(n, 0f, cap);
                        statDirty[i] = true;
                    }
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(8);
            GUILayout.Label("In-game perks");
            for (int i = 0; i < stagedPerks.Length; i++)
            {
                PerkInfo perk = GetPerkInfo(i);
                string label = i.ToString(CultureInfo.InvariantCulture) + ". " + perk.Name;
                bool value = GUILayout.Toggle(stagedPerks[i], new GUIContent(label, "CEDPERK:" + i.ToString(CultureInfo.InvariantCulture)));
                if (value != stagedPerks[i]) stagedPerks[i] = value;
            }
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Apply to Selected", GUILayout.Height(32))) Apply(employees, false);
            if (GUILayout.Button("Apply to All Employees", GUILayout.Height(32))) Apply(employees, true);
            GUILayout.EndHorizontal();
            GUILayout.Label(status);
            HandleResize();
            GUI.DragWindow(new Rect(0, 0, windowRect.width, TitleBarHeight));
        }

        private void SetVisible(bool show)
        {
            if (visible == show) return;
            visible = show;
            if (visible)
            {
                ClampWindowToScreen();
                UpdateGameplayInputGuard();
            }
            else
            {
                resizing = false;
                SaveWindowGeometry();
                ReleaseGameplayInputGuard();
            }
            Input.ResetInputAxes();
        }

        private void HandleResize()
        {
            Event current = Event.current;
            if (current == null) return;
            Rect grip = new Rect(windowRect.width - ResizeGripSize, windowRect.height - ResizeGripSize, ResizeGripSize, ResizeGripSize);
            GUI.Box(grip, "↘");

            if (current.type == EventType.MouseDown && current.button == 0 && grip.Contains(current.mousePosition))
            {
                resizing = true;
                resizeStartMouse = current.mousePosition;
                resizeStartSize = windowRect.size;
                GUIUtility.hotControl = GUIUtility.GetControlID(FocusType.Passive);
                current.Use();
            }
            else if (resizing && current.type == EventType.MouseDrag && current.button == 0)
            {
                Vector2 delta = current.mousePosition - resizeStartMouse;
                windowRect.width = Mathf.Clamp(resizeStartSize.x + delta.x, Mathf.Min(MinimumWindowWidth, Screen.width), Screen.width);
                windowRect.height = Mathf.Clamp(resizeStartSize.y + delta.y, Mathf.Min(MinimumWindowHeight, Screen.height), Screen.height);
                geometryDirty = true;
                current.Use();
            }
            else if (resizing && current.type == EventType.MouseUp && current.button == 0)
            {
                resizing = false;
                GUIUtility.hotControl = 0;
                SaveWindowGeometry();
                current.Use();
            }
        }

        private void ClampWindowToScreen()
        {
            float screenWidth = Mathf.Max(1f, Screen.width);
            float screenHeight = Mathf.Max(1f, Screen.height);
            windowRect.width = Mathf.Clamp(windowRect.width, Mathf.Min(MinimumWindowWidth, screenWidth), screenWidth);
            windowRect.height = Mathf.Clamp(windowRect.height, Mathf.Min(MinimumWindowHeight, screenHeight), screenHeight);
            windowRect.x = Mathf.Clamp(windowRect.x, 0f, Mathf.Max(0f, screenWidth - windowRect.width));
            windowRect.y = Mathf.Clamp(windowRect.y, 0f, Mathf.Max(0f, screenHeight - windowRect.height));
        }

        private void SaveWindowGeometry()
        {
            ClampWindowToScreen();
            savedWindowX.Value = windowRect.x;
            savedWindowY.Value = windowRect.y;
            savedWindowWidth.Value = windowRect.width;
            savedWindowHeight.Value = windowRect.height;
            Config.Save();
            geometryDirty = false;
        }

        private bool IsMouseOverWindow()
        {
            Vector2 mouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            return windowRect.Contains(mouse);
        }

        private void UpdateGameplayInputGuard()
        {
            GUI_Main gui = game != null ? game.guiMain_ : null;
            if (!visible)
            {
                ReleaseGameplayInputGuard();
                return;
            }
            if (gui == null) return;
            if (!inputGuardApplied || inputGuardGui != gui)
            {
                ReleaseGameplayInputGuard();
                inputGuardGui = gui;
                inputGuardPreviousMenuOpen = gui.menuOpen;
                inputGuardPreviousSelectInputField = gui.selectInputField;
                inputGuardApplied = true;
            }
            gui.menuOpen = true;
            gui.selectInputField = true;
        }

        private void ReleaseGameplayInputGuard()
        {
            if (inputGuardApplied && inputGuardGui != null)
            {
                inputGuardGui.menuOpen = inputGuardPreviousMenuOpen;
                inputGuardGui.selectInputField = inputGuardPreviousSelectInputField;
            }
            inputGuardGui = null;
            inputGuardApplied = false;
        }

        private void OnDisable()
        {
            if (savedWindowX != null) SaveWindowGeometry();
            ReleaseGameplayInputGuard();
            if (overrides != null) overrides.Uninstall();
            if (designPriorities != null) designPriorities.Uninstall();
            if (updateContent != null) updateContent.Uninstall();
        }

        private void OnApplicationQuit()
        {
            if (savedWindowX != null) SaveWindowGeometry();
            ReleaseGameplayInputGuard();
        }

        private List<characterScript> GetEmployees()
        {
            var result = new List<characterScript>();
            if (game == null) return result;
            try
            {
                var array = game.arrayCharactersScripts;
                if (array != null) foreach (var character in array) if (character != null) result.Add(character);
            }
            catch (Exception ex) { Logger.LogWarning("Could not read employee list: " + ex.Message); }
            return result;
        }

        private void ChangeSelection(int delta, List<characterScript> employees)
        {
            selectedIndex = (selectedIndex + delta + employees.Count) % employees.Count;
            LoadSelection(employees[selectedIndex]);
        }

        private void LoadSelection(characterScript character)
        {
            selected = character;
            if (selected == null) return;
            for (int i = 0; i < StatFields.Length; i++)
            {
                float value = ReadStat(selected, i);
                if (float.IsNaN(value) || float.IsInfinity(value)) value = 0f;
                stagedStats[i] = value;
                statInputs[i] = stagedStats[i].ToString("0.##", CultureInfo.InvariantCulture);
                statDirty[i] = false;
            }
            var source = selected.perks ?? new bool[0];
            int perkCount = source.Length;
            try
            {
                var ui = game != null && game.guiMain_ != null ? game.guiMain_.uiPerks : null;
                if (ui != null)
                    for (int i = 0; i < ui.Length; i++)
                        if (ui[i] != null && i >= perkCount) perkCount = i + 1;
            }
            catch { }
            stagedPerks = new bool[perkCount];
            Array.Copy(source, stagedPerks, source.Length);
            status = "Staged changes are not written until an Apply button is used.";
        }

        private void SetStagedStat(int index, float value)
        {
            stagedStats[index] = Mathf.Clamp(value, 0f, cap);
            statInputs[index] = stagedStats[index].ToString("0.##", CultureInfo.InvariantCulture);
            statDirty[index] = true;
        }

        private static float ReadStat(characterScript character, int index)
        {
            return Convert.ToSingle(typeof(characterScript).GetField(StatFields[index], InstanceFlags).GetValue(character), CultureInfo.InvariantCulture);
        }

        private void RefreshCurrentStats()
        {
            if (selected == null) return;
            for (int i = 0; i < stagedStats.Length; i++)
            {
                if (statDirty[i]) continue;
                float value = ReadStat(selected, i);
                if (float.IsNaN(value) || float.IsInfinity(value)) continue;
                stagedStats[i] = value;
                statInputs[i] = value.ToString("0.##", CultureInfo.InvariantCulture);
            }
        }

        private void Apply(List<characterScript> employees, bool all)
        {
            int applied = 0;
            bool skillsChanged = false;
            for (int i = 1; i < statDirty.Length; i++) skillsChanged |= statDirty[i];
            foreach (var character in employees)
            {
                if (!all && character != selected) continue;
                try
                {
                    for (int i = 0; i < StatFields.Length; i++)
                        if (statDirty[i] || all && i > 0 && skillsChanged)
                            typeof(characterScript).GetField(StatFields[i], InstanceFlags).SetValue(character, stagedStats[i]);
                    overrides.RecordApplied(character, skillsChanged, statDirty[0]);
                    if (character.perks == null || character.perks.Length != stagedPerks.Length)
                        character.perks = new bool[stagedPerks.Length];
                    Array.Copy(stagedPerks, character.perks, stagedPerks.Length);
                    applied++;
                }
                catch (Exception ex) { Logger.LogWarning("Skipped an invalid employee record: " + ex.Message); }
            }
            if (selected != null) LoadSelection(selected);
            status = "Applied to " + applied + " employee(s). Save the game to persist locked values.";
        }

        private void RefreshPerkCatalog()
        {
            if (game == null || game.guiMain_ == null || game.tS_ == null) return;
            GameObject[] prefabs = game.guiMain_.uiPerks;
            textScript texts = game.tS_;
            if (prefabs == null || texts.text_EN == null || ReferenceEquals(prefabs, perkCatalogSource) && texts == perkTextSource) return;

            var catalog = new PerkInfo[prefabs.Length];
            int localized = 0;
            int activeIcons = 0;
            for (int i = 0; i < prefabs.Length; i++)
            {
                if (prefabs[i] == null) continue;
                activeIcons++;
                string officialText = null;
                try
                {
                    tooltip gameTooltip = prefabs[i].GetComponent<tooltip>();
                    if (gameTooltip == null) gameTooltip = prefabs[i].GetComponentInChildren<tooltip>(true);
                    if (gameTooltip != null && gameTooltip.textArray == "text" && gameTooltip.textID >= 0 && gameTooltip.textID < texts.text_EN.Length)
                        officialText = texts.text_EN[gameTooltip.textID];
                }
                catch (Exception ex) { Logger.LogWarning("Could not inspect perk " + i + ": " + ex.Message); }

                PerkInfo info = ParsePerkText(officialText);
                if (info == null)
                {
                    Logger.LogWarning("Official English tooltip unavailable for perk index " + i + ".");
                    info = new PerkInfo { Name = "Perk " + i.ToString(CultureInfo.InvariantCulture), Description = "Official English effect text is unavailable for this perk." };
                }
                else localized++;
                catalog[i] = info;
            }
            perkCatalog = catalog;
            perkCatalogSource = prefabs;
            perkTextSource = texts;
            Logger.LogInfo("Resolved official English perk text for " + localized + "/" + activeIcons + " active game perk icons.");
        }

        private static PerkInfo ParsePerkText(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return null;
            Match match = Regex.Match(source, @"^\s*<b>\s*(.*?)\s*</b>\s*(?:<br\s*/?>)?\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!match.Success) return null;
            string name = Regex.Replace(match.Groups[1].Value, "<[^>]+>", "").Trim();
            string effect = Regex.Replace(match.Groups[2].Value, @"<br\s*/?>", " ", RegexOptions.IgnoreCase);
            effect = Regex.Replace(effect, "<[^>]+>", "");
            effect = Regex.Replace(effect, @"\s+", " ").Trim();
            if (name.Length == 0 || effect.Length == 0) return null;
            return new PerkInfo { Name = name, Description = effect };
        }

        private PerkInfo GetPerkInfo(int index)
        {
            if (index >= 0 && index < perkCatalog.Length && perkCatalog[index] != null) return perkCatalog[index];
            return new PerkInfo { Name = "Perk " + index.ToString(CultureInfo.InvariantCulture), Description = "Waiting for the game's English perk text." };
        }

        private void DrawPerkTooltip()
        {
            string hover = GUI.tooltip;
            if (string.IsNullOrEmpty(hover) || !hover.StartsWith("CEDPERK:", StringComparison.Ordinal)) return;
            Vector2 mouse = Event.current.mousePosition;
            int index;
            if (!int.TryParse(hover.Substring(8), NumberStyles.Integer, CultureInfo.InvariantCulture, out index)) return;
            PerkInfo perk = GetPerkInfo(index);
            if (perkTooltipStyle == null)
            {
                perkTooltipStyle = new GUIStyle(GUI.skin.box);
                perkTooltipStyle.wordWrap = true;
                perkTooltipStyle.alignment = TextAnchor.UpperLeft;
                perkTooltipStyle.padding = new RectOffset(10, 10, 8, 8);
            }
            string content = perk.Name + "\n" + perk.Description;
            float width = Mathf.Min(390f, Mathf.Max(160f, Screen.width - 16f));
            float height = perkTooltipStyle.CalcHeight(new GUIContent(content), width);
            Rect rect = new Rect(
                Mathf.Clamp(mouse.x + 18f, 0f, Mathf.Max(0f, Screen.width - width)),
                Mathf.Clamp(mouse.y + 18f, 0f, Mathf.Max(0f, Screen.height - height)),
                width, height);
            GUI.Box(rect, content, perkTooltipStyle);
        }

        private string GetCharacterLabel(characterScript character, int index)
        {
            string label = character.myName;
            if (string.IsNullOrEmpty(label)) label = "Employee ID " + character.myID.ToString(CultureInfo.InvariantCulture);
            return character.myID == 1 ? "CEO — " + label : label;
        }

        private void TrackNewEmployees()
        {
            if (game == null) return;
            if (trackedGame != game)
            {
                trackedGame = game;
                trackedCharacters.Clear();
                pendingNewCharacters.Clear();
                var current = GetEmployees();
                foreach (var character in current) trackedCharacters.Add(character);
                return;
            }
            var employees = GetEmployees();
            foreach (var character in employees)
            {
                if (trackedCharacters.Contains(character)) continue;
                trackedCharacters.Add(character);
                if (autoMaxNewEmployees.Value) pendingNewCharacters[character] = Time.unscaledTime + 1f;
            }
            if (!autoMaxNewEmployees.Value)
            {
                pendingNewCharacters.Clear();
                return;
            }
            var ready = new List<characterScript>();
            foreach (var pending in pendingNewCharacters)
                if (pending.Key == null || Time.unscaledTime >= pending.Value) ready.Add(pending.Key);
            foreach (var character in ready)
            {
                pendingNewCharacters.Remove(character);
                if (character == null) continue;
                MaxCharacter(character);
            }
        }

        private void MaxCharacter(characterScript character)
        {
            try
            {
                foreach (string name in StatFields)
                    typeof(characterScript).GetField(name, InstanceFlags).SetValue(character, (float)cap);
                overrides.RecordApplied(character, true, true);
                if (character.perks != null) for (int i = 0; i < character.perks.Length; i++) character.perks[i] = true;
            }
            catch (Exception ex) { Logger.LogWarning("Auto-max skipped an incomplete character: " + ex.Message); }
        }
    }
}
