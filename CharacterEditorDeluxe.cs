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
    [BepInPlugin("com.codex.mgt2.charactereditordeluxe", "MGT2 Character Editor Deluxe", "1.0.6")]
    public sealed class Plugin : BaseUnityPlugin
    {
        private static Plugin activePlugin;
        private static readonly string[] StatFields = {
            "s_motivation", "s_gamedesign", "s_programmieren", "s_grafik", "s_sound",
            "s_pr", "s_gametests", "s_technik", "s_forschen"
        };
        private static readonly string[] StatLabels = {
            "Motivation", "Game Design", "Programming", "Graphics", "Sound",
            "PR", "Game Testing", "Technology", "Research"
        };
        private static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo[] StatMembers = Array.ConvertAll(StatFields, name => typeof(characterScript).GetField(name, InstanceFlags));
        private static readonly string[] Tabs = { "EMPLOYEES", "PERKS", "WORK PRIORITY", "GAME UPDATES", "GLOBAL / SAFETY" };

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
        private readonly List<characterScript> employeeCache = new List<characterScript>();
        private readonly List<characterScript> readyNewCharacters = new List<characterScript>();
        private bool visible;
        private int activeTab;
        private Vector2 scroll;
        private int selectedIndex;
        private float nextStatsRefresh;
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
        private bool showNegativePerks;
        private bool showNeutralPerks;
        private StatOverrides overrides;
        private DesignPriorityOverrides designPriorities;
        private UpdateContentOverrides updateContent;

        private enum PerkKind { Positive, Negative, Neutral, Unknown }

        private sealed class PerkInfo
        {
            public int Index;
            public string Name;
            public string Description;
            public PerkKind Kind;
            public GUIContent Content;
            public GUIContent TooltipContent;
        }

        private const float MinimumWindowWidth = 560f;
        private const float MinimumWindowHeight = 420f;
        private const float DefaultWindowWidth = 780f;
        private const float DefaultWindowHeight = 700f;
        private const float TitleBarHeight = 24f;
        private const float ResizeGripSize = 20f;

        private void Awake()
        {
            activePlugin = this;
            autoMaxNewEmployees = Config.Bind("General", "AutoMaxNewEmployees", false, "Automatically max newly added employees after initialization.");
            configuredCap = Config.Bind("Stats", "StatCap", 100, "Safe maximum stat value (0 to 100). MGT2's native scale is 0 to 100.");
            var globalLock = Config.Bind("Locks", "LockEditedStats", true, "Keep edited skills at their assigned values during work, training and save/load.");
            savedWindowX = Config.Bind("Window", "X", 30f, "Saved editor window X position.");
            savedWindowY = Config.Bind("Window", "Y", 30f, "Saved editor window Y position.");
            savedWindowWidth = Config.Bind("Window", "Width", DefaultWindowWidth, "Saved editor window width.");
            savedWindowHeight = Config.Bind("Window", "Height", DefaultWindowHeight, "Saved editor window height.");
            cap = Mathf.Clamp(configuredCap.Value, 1, 100);
            configuredCap.Value = cap;
            windowRect = new Rect(savedWindowX.Value, savedWindowY.Value, savedWindowWidth.Value, savedWindowHeight.Value);
            ClampWindowToScreen();
            overrides = new StatOverrides(Logger, globalLock);
            overrides.Install();
            designPriorities = new DesignPriorityOverrides(Logger,
                Config.Bind("Design Priority", "ExtendedDesignPriority", false, "Enable Extended Design Priority up to the safe 200% per-category limit."),
                Config.Bind("Design Priority", "PriorityMax", 100, "Safe maximum priority per slider: 100 or 200 percent."),
                Config.Bind("Design Priority", "AllowTotalAbove100", true, "Allow a total above 100% while the priority cheat is enabled."));
            designPriorities.Install();
            updateContent = new UpdateContentOverrides(Config, Logger);
            updateContent.Install();
            Logger.LogInfo("Character Editor Deluxe loaded. Toggle with F8.");
        }

        internal static mainScript CurrentGame
        {
            get { return activePlugin == null ? null : activePlugin.game; }
        }

        internal static void SetGame(mainScript source)
        {
            if (activePlugin == null || source == null) return;
            activePlugin.game = source;
            if (activePlugin.designPriorities != null) activePlugin.designPriorities.SetPlayer(source);
            if (activePlugin.updateContent != null) activePlugin.updateContent.SetPlayer(source);
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F8)) SetVisible(!visible);

            if (visible) UpdateGameplayInputGuard();
            else if (inputGuardApplied) ReleaseGameplayInputGuard();

            if (visible && IsMouseOverWindow())
                Input.ResetInputAxes();

            if (geometryDirty && !Input.GetMouseButton(0))
                SaveWindowGeometry();

            bool autoMax = autoMaxNewEmployees.Value;
            bool refreshEditor = visible;
            bool refreshDesignMenus = visible || designPriorities.NeedsMenuUpdate;
            if ((refreshEditor || autoMax || refreshDesignMenus) && Time.unscaledTime >= nextLookup)
            {
                nextLookup = Time.unscaledTime + 1f;
                if (refreshEditor || autoMax)
                {
                    if (game == null && refreshEditor)
                    {
                        game = UnityEngine.Object.FindObjectOfType<mainScript>();
                        if (game != null) SetGame(game);
                    }
                    RefreshEmployeeCache();
                    TrackNewEmployees();
                    if (refreshEditor) RefreshPerkCatalog();
                }
                if (refreshDesignMenus) designPriorities.UpdateMenu();
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
            GUILayout.BeginHorizontal("box");
            GUILayout.Label("CHARACTER EDITOR DELUXE", GUILayout.ExpandWidth(true));
            GUILayout.Label("F8", GUILayout.Width(28));
            if (GUILayout.Button("Close", GUILayout.Width(60))) SetVisible(false);
            GUILayout.EndHorizontal();
            GUILayout.Label("Safe limits are enforced before values reach MGT2. Current stat cap: " + cap + "/100.");
            activeTab = GUILayout.SelectionGrid(activeTab, Tabs, 3, GUILayout.Height(52));
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.ExpandHeight(true));
            if (activeTab == 0) DrawEmployeesTab();
            else if (activeTab == 1) DrawPerksTab();
            else if (activeTab == 2) DrawGameDesignTab();
            else if (activeTab == 3) DrawGameUpdatesTab();
            else DrawGlobalTab();
            GUILayout.EndScrollView();
            if (!string.IsNullOrEmpty(status)) GUILayout.Label(status, "box");
            HandleResize();
            GUI.DragWindow(new Rect(0, 0, windowRect.width, TitleBarHeight));
        }

        private void DrawEmployeesTab()
        {
            List<characterScript> employees;
            if (!DrawEmployeeSelector(out employees)) return;
            RefreshCurrentStats();
            GUILayout.BeginVertical("box");
            GUILayout.Label("STATS  (safe range 0-" + cap + ")", GUI.skin.GetStyle("boldlabel"));
            for (int i = 0; i < StatFields.Length; i++)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(StatLabels[i], GUILayout.Width(120));
                float sliderValue = Mathf.Clamp(stagedStats[i], 0f, cap);
                float val = GUILayout.HorizontalSlider(sliderValue, 0f, cap, GUILayout.ExpandWidth(true));
                if (Math.Abs(val - sliderValue) > 0.01f) SetStagedStat(i, Mathf.Round(val));
                string typed = GUILayout.TextField(statInputs[i], GUILayout.Width(62));
                if (typed != statInputs[i]) SetTypedStat(i, typed);
                GUILayout.Label("/" + cap, GUILayout.Width(35));
                GUILayout.EndHorizontal();
            }
            GUILayout.EndVertical();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Apply selected", GUILayout.Height(30))) Apply(employees, false);
            if (GUILayout.Button("Apply all employees", GUILayout.Height(30))) Apply(employees, true);
            GUILayout.EndHorizontal();
        }

        private void DrawPerksTab()
        {
            List<characterScript> employees;
            if (!DrawEmployeeSelector(out employees)) return;
            GUILayout.BeginVertical("box");
            GUILayout.Label("PERKS", GUI.skin.GetStyle("boldlabel"));
            DrawPerks();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Stage all positive perks")) StageAllPositivePerks();
            if (GUILayout.Button("Apply positive to selected")) ApplyPositivePerks(employees, false);
            if (GUILayout.Button("Apply positive to all")) ApplyPositivePerks(employees, true);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Clear all")) for (int i = 0; i < stagedPerks.Length; i++) stagedPerks[i] = false;
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Apply selected", GUILayout.Height(30))) Apply(employees, false);
            if (GUILayout.Button("Apply all employees", GUILayout.Height(30))) Apply(employees, true);
            GUILayout.EndHorizontal();
        }

        private bool DrawEmployeeSelector(out List<characterScript> employees)
        {
            employees = GetEmployees();
            GUILayout.BeginVertical("box");
            GUILayout.Label("EMPLOYEE", GUI.skin.GetStyle("boldlabel"));
            if (employees.Count == 0)
            {
                GUILayout.Label("Load or start a game to edit employees.");
                GUILayout.EndVertical();
                return false;
            }
            if (selectedIndex >= employees.Count) selectedIndex = 0;
            if (selected != employees[selectedIndex]) LoadSelection(employees[selectedIndex]);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("<", GUILayout.Width(32))) ChangeSelection(-1, employees);
            GUILayout.Label(GetCharacterLabel(employees[selectedIndex], selectedIndex) + "  (" + (selectedIndex + 1) + "/" + employees.Count + ")", GUILayout.ExpandWidth(true));
            if (GUILayout.Button(">", GUILayout.Width(32))) ChangeSelection(1, employees);
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            return true;
        }

        private void DrawGameDesignTab()
        {
            GUILayout.BeginVertical("box");
            GUILayout.Label("DESIGN / WORK PRIORITY", GUI.skin.GetStyle("boldlabel"));
            GUILayout.Label("Design Priority is limited to the verified stable maximum. Higher values can overflow update and sales formulas.");
            designPriorities.DrawOptions();
            GUILayout.EndVertical();
        }

        private void DrawPerks()
        {
            int positiveSelected = 0, positiveTotal = 0;
            int neutralSelected = 0, neutralTotal = 0;
            int negativeSelected = 0, negativeTotal = 0;
            bool hasUnresolved = false;
            foreach (PerkInfo perk in perkCatalog)
            {
                if (perk == null) continue;
                bool selectedValue = perk.Index >= 0 && perk.Index < stagedPerks.Length && stagedPerks[perk.Index];
                if (perk.Kind == PerkKind.Negative)
                {
                    negativeTotal++;
                    if (selectedValue) negativeSelected++;
                }
                else if (perk.Kind == PerkKind.Neutral)
                {
                    neutralTotal++;
                    if (selectedValue) neutralSelected++;
                }
                else if (perk.Kind == PerkKind.Unknown) hasUnresolved = true;
            }
            GUILayout.Label("POSITIVE PERKS", GUI.skin.GetStyle("boldlabel"));
            foreach (PerkInfo perk in perkCatalog)
            {
                if (perk == null || perk.Kind != PerkKind.Positive) continue;
                positiveTotal++;
                DrawPerkToggle(perk, ref positiveSelected);
            }
            GUILayout.Label("Positive selected " + positiveSelected + "/" + positiveTotal);

            showNegativePerks = GUILayout.Toggle(showNegativePerks, "Show Negative Perks (manual only)");
            if (showNegativePerks)
            {
                GUILayout.Label("NEGATIVE PERKS", GUI.skin.GetStyle("boldlabel"));
                int displayedNegativeSelected = 0;
                foreach (PerkInfo perk in perkCatalog)
                {
                    if (perk == null || perk.Kind != PerkKind.Negative) continue;
                    DrawPerkToggle(perk, ref displayedNegativeSelected);
                }
            }
            GUILayout.Label("Negative selected " + negativeSelected + "/" + negativeTotal);
            GUILayout.Label("Neutral selected " + neutralSelected + "/" + neutralTotal);

            showNeutralPerks = GUILayout.Toggle(showNeutralPerks, "Show Neutral Perks (manual only)");
            if (showNeutralPerks)
            {
                GUILayout.Label("NEUTRAL PERKS (manual only)", GUI.skin.GetStyle("boldlabel"));
                foreach (PerkInfo perk in perkCatalog)
                    if (perk != null && perk.Kind == PerkKind.Neutral) DrawManualPerkToggle(perk);
            }
            if (hasUnresolved)
                GUILayout.Label("Unresolved perks are hidden until their official game mapping is available.");
            if (positiveTotal == 0 && negativeTotal == 0 && neutralTotal == 0)
                GUILayout.Label("Open a game to resolve the official perk catalog.");
        }

        private void DrawPerkToggle(PerkInfo perk, ref int selectedCount)
        {
            bool selectedValue = stagedPerks != null && perk.Index >= 0 && perk.Index < stagedPerks.Length && stagedPerks[perk.Index];
            bool value = GUILayout.Toggle(selectedValue, perk.Content);
            if (value != selectedValue && perk.Index >= 0 && perk.Index < stagedPerks.Length) stagedPerks[perk.Index] = value;
            if (value) selectedCount++;
        }

        private void DrawManualPerkToggle(PerkInfo perk)
        {
            bool selectedValue = stagedPerks != null && perk.Index >= 0 && perk.Index < stagedPerks.Length && stagedPerks[perk.Index];
            bool value = GUILayout.Toggle(selectedValue, perk.Content);
            if (value != selectedValue && perk.Index >= 0 && perk.Index < stagedPerks.Length) stagedPerks[perk.Index] = value;
        }

        private void StageAllPositivePerks()
        {
            if (stagedPerks == null) return;
            for (int i = 0; i < stagedPerks.Length; i++) stagedPerks[i] = false;
            foreach (PerkInfo perk in perkCatalog)
                if (perk != null && perk.Kind == PerkKind.Positive && perk.Index >= 0 && perk.Index < stagedPerks.Length)
                    stagedPerks[perk.Index] = true;
            status = "Staged all positively classified official perks; negative and unresolved perks remain disabled.";
        }

        private void ApplyPositivePerks(List<characterScript> employees, bool all)
        {
            int applied = 0;
            foreach (characterScript character in employees)
            {
                if (!all && character != selected) continue;
                if (WritePositivePerks(character)) applied++;
            }
            if (selected != null) LoadSelection(selected);
            status = "Applied official positive perks to " + applied + " employee(s). Neutral, negative, and unresolved perks were disabled.";
        }

        private void DrawGameUpdatesTab()
        {
            GUILayout.BeginVertical("box");
            GUILayout.Label("GAME UPDATES", GUI.skin.GetStyle("boldlabel"));
            GUILayout.Label("Update content is limited to the verified stable range. Vanilla calculations remain active when disabled.");
            updateContent.DrawOptions();
            GUILayout.EndVertical();
        }

        private void DrawGlobalTab()
        {
            GUILayout.BeginVertical("box");
            GUILayout.Label("GLOBAL / SAFETY", GUI.skin.GetStyle("boldlabel"));
            autoMaxNewEmployees.Value = GUILayout.Toggle(autoMaxNewEmployees.Value, "Auto-max new hires to the safe stat cap");
            bool globalLock = GUILayout.Toggle(overrides.GlobalLock.Value, "Lock edited stats globally");
            if (globalLock != overrides.GlobalLock.Value)
            {
                overrides.GlobalLock.Value = globalLock;
                if (globalLock) overrides.RestoreAllLocked();
            }
            GUILayout.Label("Stat cap", GUILayout.Width(70));
            string capText = GUILayout.TextField(cap.ToString(CultureInfo.InvariantCulture), GUILayout.Width(62));
            int parsed;
            if (int.TryParse(capText, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) cap = Mathf.Clamp(parsed, 1, 100);
            configuredCap.Value = cap;
            if (GUILayout.Button("Reset cheats to vanilla")) ResetToVanilla();
            GUILayout.Label("Version 1.0.6 | F8 toggles this window | window position and size are saved.");
            GUILayout.EndVertical();
        }

        private void SetTypedStat(int index, string typed)
        {
            statInputs[index] = typed;
            float n;
            if (float.TryParse(typed, NumberStyles.Float, CultureInfo.InvariantCulture, out n) && !float.IsNaN(n) && !float.IsInfinity(n))
            {
                stagedStats[index] = Mathf.Clamp(n, 0f, cap);
                statDirty[index] = true;
            }
            else status = "Enter a finite number between 0 and " + cap + ".";
        }

        private void ResetToVanilla()
        {
            autoMaxNewEmployees.Value = false;
            overrides.GlobalLock.Value = false;
            designPriorities.ResetToVanilla();
            updateContent.ResetToVanilla();
            cap = 100;
            configuredCap.Value = cap;
            status = "Cheats disabled; vanilla calculations restored for future actions.";
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
            if (activePlugin == this) activePlugin = null;
        }

        private void OnApplicationQuit()
        {
            if (savedWindowX != null) SaveWindowGeometry();
            ReleaseGameplayInputGuard();
        }

        private List<characterScript> GetEmployees()
        {
            return employeeCache;
        }

        private void RefreshEmployeeCache()
        {
            employeeCache.Clear();
            if (game == null) return;
            try
            {
                var array = game.arrayCharactersScripts;
                if (array != null) foreach (var character in array) if (character != null) employeeCache.Add(character);
            }
            catch (Exception ex) { Logger.LogWarning("Could not read employee list: " + ex.Message); }
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
                stagedStats[i] = ClampStatValue(value);
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
            return Convert.ToSingle(StatMembers[index].GetValue(character), CultureInfo.InvariantCulture);
        }

        private void RefreshCurrentStats()
        {
            if (selected == null || Time.unscaledTime < nextStatsRefresh) return;
            nextStatsRefresh = Time.unscaledTime + 1f;
            for (int i = 0; i < stagedStats.Length; i++)
            {
                if (statDirty[i]) continue;
                float value = ReadStat(selected, i);
                if (float.IsNaN(value) || float.IsInfinity(value)) continue;
                stagedStats[i] = ClampStatValue(value, false);
                statInputs[i] = stagedStats[i].ToString("0.##", CultureInfo.InvariantCulture);
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
                            StatMembers[i].SetValue(character, ClampStatValue(stagedStats[i]));
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

        private float ClampStatValue(float value, bool warn = true)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                if (warn) Logger.LogWarning("Reset non-finite employee stat to 0.");
                return 0f;
            }
            if (value < 0f || value > cap)
            {
                if (warn) Logger.LogWarning("Clamped employee stat to safe cap " + cap.ToString(CultureInfo.InvariantCulture) + ".");
                return Mathf.Clamp(value, 0f, cap);
            }
            return value;
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
                    info = new PerkInfo { Name = "Unknown / unresolved", Description = "The official game name or effect could not be resolved; hidden by default.", Kind = PerkKind.Unknown };
                }
                else localized++;
                info.Index = i;
                if (!string.Equals(info.Name, "Unknown / unresolved", StringComparison.Ordinal))
                    info.Kind = ClassifyPerk(info.Name, info.Description);
                info.Content = new GUIContent(info.Name, "CEDPERK:" + i.ToString(CultureInfo.InvariantCulture));
                info.TooltipContent = new GUIContent(info.Name + "\n" + info.Description);
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
            return new PerkInfo { Name = name, Description = effect, Kind = PerkKind.Unknown };
        }

        private static PerkKind ClassifyPerk(string name, string description)
        {
            string key = (name ?? string.Empty).Trim().ToLowerInvariant();
            switch (key)
            {
                case "greedy":
                case "unfocused":
                case "untalented":
                case "immunocompromised":
                case "unlucky":
                case "messy":
                case "stress-averse":
                    return PerkKind.Negative;
                case "loyal":
                case "nature lover":
                case "modest":
                case "ceo":
                    return PerkKind.Neutral;
            }
            return PerkKind.Positive;
        }

        private PerkInfo GetPerkInfo(int index)
        {
            if (index >= 0 && index < perkCatalog.Length && perkCatalog[index] != null) return perkCatalog[index];
            const string unknown = "Unknown / unresolved";
            return new PerkInfo
            {
                Index = index,
                Name = unknown,
                Description = "The official game name or effect could not be resolved.",
                Kind = PerkKind.Unknown,
                Content = new GUIContent(unknown, "CEDPERK:" + index.ToString(CultureInfo.InvariantCulture)),
                TooltipContent = new GUIContent(unknown + "\nThe official game name or effect could not be resolved.")
            };
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
            float width = Mathf.Min(390f, Mathf.Max(160f, Screen.width - 16f));
            float height = perkTooltipStyle.CalcHeight(perk.TooltipContent, width);
            Rect rect = new Rect(
                Mathf.Clamp(mouse.x + 18f, 0f, Mathf.Max(0f, Screen.width - width)),
                Mathf.Clamp(mouse.y + 18f, 0f, Mathf.Max(0f, Screen.height - height)),
                width, height);
            GUI.Box(rect, perk.TooltipContent, perkTooltipStyle);
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
            readyNewCharacters.Clear();
            foreach (var pending in pendingNewCharacters)
                if (pending.Key == null || Time.unscaledTime >= pending.Value) readyNewCharacters.Add(pending.Key);
            if (readyNewCharacters.Count > 0) RefreshPerkCatalog();
            foreach (var character in readyNewCharacters)
            {
                pendingNewCharacters.Remove(character);
                if (character == null) continue;
                MaxCharacter(character);
            }
            readyNewCharacters.Clear();
        }

        private void MaxCharacter(characterScript character)
        {
            try
            {
                for (int i = 0; i < StatMembers.Length; i++) StatMembers[i].SetValue(character, (float)cap);
                overrides.RecordApplied(character, true, true);
                WritePositivePerks(character);
            }
            catch (Exception ex) { Logger.LogWarning("Auto-max skipped an incomplete character: " + ex.Message); }
        }

        private bool WritePositivePerks(characterScript character)
        {
            if (character == null || perkCatalog == null || perkCatalog.Length == 0) return false;
            int length = Math.Max(character.perks == null ? 0 : character.perks.Length, perkCatalog.Length);
            if (length == 0) return false;
            if (character.perks == null || character.perks.Length != length)
            {
                bool[] current = character.perks;
                character.perks = new bool[length];
                if (current != null) Array.Copy(current, character.perks, Math.Min(current.Length, character.perks.Length));
            }
            Array.Clear(character.perks, 0, character.perks.Length);
            foreach (PerkInfo perk in perkCatalog)
                if (perk != null && perk.Kind == PerkKind.Positive && perk.Index >= 0 && perk.Index < character.perks.Length)
                    character.perks[perk.Index] = true;
            return true;
        }
    }
}
