using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace CharacterEditorDeluxe
{
    internal sealed class StatOverrides
    {
        private const string SaveKey = "com.codex.mgt2.charactereditordeluxe.statlocks";
        private const int FormatVersion = 1;
        private const int MaxRecords = 10000;
        private const float VanillaSkillCap = 100f;
        private const float SupportedSkillCap = 1000f;
        private const float MotivationCap = 100f;

        private sealed class Record
        {
            internal int Id;
            internal string Name;
            internal bool LockStats;
            internal bool LockMotivation;
            internal float Motivation;
            internal readonly float[] Skills = new float[8];
        }

        private static StatOverrides active;
        private static readonly FieldInfo TrainingCourseMenuField =
            AccessTools.Field(typeof(Item_Training_Kurs), "menuTraining_");
        private readonly ManualLogSource log;
        private readonly Dictionary<characterScript, Record> records = new Dictionary<characterScript, Record>();
        private readonly HashSet<characterScript> playerEmployees = new HashSet<characterScript>();
        private Dictionary<int, Record> pendingLoaded;
        private readonly Func<int> getSkillCap;
        private Menu_Training_Select trainingMenu;
        private float[] trainingTargets;
        private float[] originalTrainingTargets;
        private Harmony harmony;

        internal readonly ConfigEntry<bool> GlobalLock;

        internal StatOverrides(ManualLogSource log, ConfigEntry<bool> globalLock, Func<int> getSkillCap)
        {
            this.log = log;
            GlobalLock = globalLock;
            this.getSkillCap = getSkillCap;
        }

        internal void Install()
        {
            active = this;
            harmony = new Harmony("com.codex.mgt2.charactereditordeluxe.statlocks");
            harmony.Patch(AccessTools.Method(typeof(characterScript), "Learn"),
                transpiler: new HarmonyMethod(typeof(StatOverrides), nameof(ExtendLearnCapForPlayerEmployees)),
                postfix: new HarmonyMethod(typeof(StatOverrides), nameof(AfterLearn)) { priority = Priority.Last });
            Patch(typeof(characterScript), "GetSkillCap", null, nameof(AfterGetSkillCap));
            Patch(typeof(characterScript), "GetSkillCap_Skill", null, nameof(AfterGetSkillCap));
            Patch(typeof(characterScript), "AddMotivation", nameof(BeforeAddMotivation), nameof(AfterAddMotivation));
            Patch(typeof(characterScript), "Init", null, nameof(AfterCharacterInit));
            Patch(typeof(mainScript), "CopyArbeitsmarktCharacterData", null, nameof(AfterHireCopy));
            Patch(typeof(mainScript), "InitNewGame", nameof(BeforeNewGame), null);
            Patch(typeof(Menu_Training_Select), "Init", null, nameof(AfterTrainingMenuInitialized));
            Patch(typeof(Item_Training_Kurs), "SetData", nameof(BeforeTrainingCourseData), null);
            Patch(typeof(savegameScript), "SaveMitarbeiter", nameof(BeforeSaveEmployees), nameof(AfterSaveEmployees));
            Patch(typeof(savegameScript), "LoadMitarbeiter", null, nameof(AfterLoadEmployees));
            Patch(typeof(savegameScript), "Load", nameof(BeforeLoadGame), nameof(AfterLoadGame));
        }

        internal void Uninstall()
        {
            if (harmony != null) harmony.UnpatchSelf();
            playerEmployees.Clear();
            if (active == this) active = null;
        }

        internal void SetPlayer(mainScript player)
        {
            playerEmployees.Clear();
            if (player == null || player.arrayCharactersScripts == null) return;
            foreach (characterScript character in player.arrayCharactersScripts)
                if (character != null) playerEmployees.Add(character);
        }

        private void Patch(Type type, string name, string prefix, string postfix)
        {
            var method = AccessTools.Method(type, name);
            if (method == null) throw new MissingMethodException(type.FullName, name);
            HarmonyMethod before = prefix == null ? null : new HarmonyMethod(typeof(StatOverrides), prefix);
            HarmonyMethod after = postfix == null ? null : new HarmonyMethod(typeof(StatOverrides), postfix) { priority = Priority.Last };
            harmony.Patch(method, before, after);
        }

        internal void SetMotivationLock(characterScript character, bool value)
        {
            if (character == null) return;
            Record record = GetOrCreate(character);
            if (value) record.Motivation = character.s_motivation;
            record.LockMotivation = value;
        }

        internal bool IsMotivationLocked(characterScript character)
        {
            Record record;
            return character != null && records.TryGetValue(character, out record) && record.LockMotivation;
        }

        internal bool IsStatsLocked(characterScript character)
        {
            Record record;
            return character != null && records.TryGetValue(character, out record) && record.LockStats;
        }

        internal void SetStatsLock(characterScript character, bool value)
        {
            if (character == null) return;
            Record record = GetOrCreate(character);
            if (value) CaptureSkills(record, character);
            record.LockStats = value;
        }

        internal void RestoreAllLocked()
        {
            foreach (var entry in records) RestoreLocked(entry.Key);
        }

        internal void RefreshTrainingTargets()
        {
            if (trainingMenu != null) ScaleTrainingTargets(trainingMenu);
        }

        internal void ClearLocks()
        {
            records.Clear();
            pendingLoaded = null;
        }

        internal void RecordApplied(characterScript character, bool skillsChanged, bool motivationChanged)
        {
            if (character == null || (!skillsChanged && !motivationChanged)) return;
            Record record;
            bool hadRecord = records.TryGetValue(character, out record);
            if (!hadRecord)
            {
                record = GetOrCreate(character);
                record.LockStats = skillsChanged && GlobalLock.Value;
            }
            if (skillsChanged)
            {
                CaptureSkills(record, character);
            }
            if (motivationChanged) record.Motivation = character.s_motivation;
        }

        private Record GetOrCreate(characterScript character)
        {
            Record record;
            if (records.TryGetValue(character, out record)) return record;
            record = new Record { Id = character.myID, Name = character.myName ?? "", Motivation = character.s_motivation };
            CaptureSkills(record, character);
            records.Add(character, record);
            return record;
        }

        private static void CaptureSkills(Record record, characterScript character)
        {
            record.Skills[0] = character.s_gamedesign;
            record.Skills[1] = character.s_programmieren;
            record.Skills[2] = character.s_grafik;
            record.Skills[3] = character.s_sound;
            record.Skills[4] = character.s_pr;
            record.Skills[5] = character.s_gametests;
            record.Skills[6] = character.s_technik;
            record.Skills[7] = character.s_forschen;
        }

        private static void RestoreSkills(Record record, characterScript character)
        {
            character.s_gamedesign = SafeSkill(record.Skills[0]);
            character.s_programmieren = SafeSkill(record.Skills[1]);
            character.s_grafik = SafeSkill(record.Skills[2]);
            character.s_sound = SafeSkill(record.Skills[3]);
            character.s_pr = SafeSkill(record.Skills[4]);
            character.s_gametests = SafeSkill(record.Skills[5]);
            character.s_technik = SafeSkill(record.Skills[6]);
            character.s_forschen = SafeSkill(record.Skills[7]);
        }

        private void RestoreLocked(characterScript character)
        {
            Record record;
            if (records.Count == 0 || character == null || !records.TryGetValue(character, out record) ||
                character.myID != record.Id ||
                !string.Equals(character.myName ?? "", record.Name, StringComparison.Ordinal)) return;
            if (GlobalLock.Value && record.LockStats) RestoreSkills(record, character);
            if (record.LockMotivation) character.s_motivation = SafeMotivation(record.Motivation);
        }

        private static IEnumerable<CodeInstruction> ExtendLearnCapForPlayerEmployees(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            int patched = 0;
            for (int i = 0; i + 2 < code.Count; i++)
            {
                FieldInfo field = code[i + 2].operand as FieldInfo;
                if (code[i].opcode != OpCodes.Ldarg_0 ||
                    code[i + 1].opcode != OpCodes.Ldc_R4 ||
                    !Equals(code[i + 1].operand, VanillaSkillCap) ||
                    code[i + 2].opcode != OpCodes.Stfld ||
                    field == null ||
                    !IsSkillField(field.Name)) continue;

                code[i + 2].opcode = OpCodes.Call;
                code[i + 2].operand = GetLearnCapAssignment(field.Name);
                patched++;
                i += 2;
            }
            if (patched != 8)
                throw new InvalidOperationException("Expected eight legacy skill clamps in characterScript.Learn; found " + patched.ToString(CultureInfo.InvariantCulture) + ".");
            return code;
        }

        private static MethodInfo GetLearnCapAssignment(string fieldName)
        {
            switch (fieldName)
            {
                case "s_gamedesign": return AccessTools.Method(typeof(StatOverrides), nameof(ApplyLearnCap_GameDesign));
                case "s_programmieren": return AccessTools.Method(typeof(StatOverrides), nameof(ApplyLearnCap_Programming));
                case "s_grafik": return AccessTools.Method(typeof(StatOverrides), nameof(ApplyLearnCap_Graphics));
                case "s_sound": return AccessTools.Method(typeof(StatOverrides), nameof(ApplyLearnCap_Sound));
                case "s_pr": return AccessTools.Method(typeof(StatOverrides), nameof(ApplyLearnCap_PR));
                case "s_gametests": return AccessTools.Method(typeof(StatOverrides), nameof(ApplyLearnCap_Testing));
                case "s_technik": return AccessTools.Method(typeof(StatOverrides), nameof(ApplyLearnCap_Technology));
                case "s_forschen": return AccessTools.Method(typeof(StatOverrides), nameof(ApplyLearnCap_Research));
                default: throw new ArgumentOutOfRangeException(nameof(fieldName));
            }
        }

        private static void ApplyLearnCap_GameDesign(characterScript character, float value)
        {
            if (!IsPlayerEmployee(character)) character.s_gamedesign = value;
        }

        private static void ApplyLearnCap_Programming(characterScript character, float value)
        {
            if (!IsPlayerEmployee(character)) character.s_programmieren = value;
        }

        private static void ApplyLearnCap_Graphics(characterScript character, float value)
        {
            if (!IsPlayerEmployee(character)) character.s_grafik = value;
        }

        private static void ApplyLearnCap_Sound(characterScript character, float value)
        {
            if (!IsPlayerEmployee(character)) character.s_sound = value;
        }

        private static void ApplyLearnCap_PR(characterScript character, float value)
        {
            if (!IsPlayerEmployee(character)) character.s_pr = value;
        }

        private static void ApplyLearnCap_Testing(characterScript character, float value)
        {
            if (!IsPlayerEmployee(character)) character.s_gametests = value;
        }

        private static void ApplyLearnCap_Technology(characterScript character, float value)
        {
            if (!IsPlayerEmployee(character)) character.s_technik = value;
        }

        private static void ApplyLearnCap_Research(characterScript character, float value)
        {
            if (!IsPlayerEmployee(character)) character.s_forschen = value;
        }

        private static bool IsPlayerEmployee(characterScript character)
        {
            return active != null && character != null && active.playerEmployees.Contains(character);
        }

        private static bool IsSkillField(string name)
        {
            return name == "s_gamedesign" || name == "s_programmieren" || name == "s_grafik" ||
                name == "s_sound" || name == "s_pr" || name == "s_gametests" ||
                name == "s_technik" || name == "s_forschen";
        }

        private static void AfterGetSkillCap(characterScript __instance, ref float __result)
        {
            if (IsPlayerEmployee(__instance)) __result = active.SkillCap;
        }

        private static void AfterTrainingMenuInitialized(Menu_Training_Select __instance)
        {
            if (active != null) active.ScaleTrainingTargets(__instance);
        }

        private static void BeforeTrainingCourseData(Item_Training_Kurs __instance)
        {
            if (active == null || __instance == null || TrainingCourseMenuField == null) return;
            Menu_Training_Select menu = TrainingCourseMenuField.GetValue(__instance) as Menu_Training_Select;
            if (menu != null) active.ScaleTrainingTargets(menu);
        }

        private void ScaleTrainingTargets(Menu_Training_Select menu)
        {
            if (menu == null || menu.trainingMaxLearn == null) return;
            if (menu != trainingMenu || menu.trainingMaxLearn != trainingTargets)
            {
                trainingMenu = menu;
                trainingTargets = menu.trainingMaxLearn;
                originalTrainingTargets = (float[])trainingTargets.Clone();
            }

            float multiplier = SkillCap / VanillaSkillCap;
            for (int i = 0; i < trainingTargets.Length; i++)
            {
                float target = originalTrainingTargets[i];
                trainingTargets[i] = IsFinite(target)
                    ? Mathf.Clamp(target * multiplier, 0f, SkillCap)
                    : 0f;
            }
        }

        private int SkillCap
        {
            get { return Mathf.Clamp(getSkillCap(), 1, (int)SupportedSkillCap); }
        }

        private void ClampLearnedSkill(characterScript character, ref float skill)
        {
            skill = SafeSkill(skill, SkillCap);
        }

        private static void AfterLearn(characterScript __instance, bool gamedesign_, bool programmieren_, bool grafik_, bool sound_, bool pr_, bool gametests_, bool technik_, bool forschen_)
        {
            if (!IsPlayerEmployee(__instance)) return;
            if (gamedesign_) active.ClampLearnedSkill(__instance, ref __instance.s_gamedesign);
            if (programmieren_) active.ClampLearnedSkill(__instance, ref __instance.s_programmieren);
            if (grafik_) active.ClampLearnedSkill(__instance, ref __instance.s_grafik);
            if (sound_) active.ClampLearnedSkill(__instance, ref __instance.s_sound);
            if (pr_) active.ClampLearnedSkill(__instance, ref __instance.s_pr);
            if (gametests_) active.ClampLearnedSkill(__instance, ref __instance.s_gametests);
            if (technik_) active.ClampLearnedSkill(__instance, ref __instance.s_technik);
            if (forschen_) active.ClampLearnedSkill(__instance, ref __instance.s_forschen);
            active.RestoreLocked(__instance);
        }

        private static bool BeforeAddMotivation(characterScript __instance)
        {
            if (active == null || active.records.Count == 0) return true;
            Record record;
            if (__instance == null || !active.records.TryGetValue(__instance, out record) || !record.LockMotivation ||
                __instance.myID != record.Id ||
                !string.Equals(__instance.myName ?? "", record.Name, StringComparison.Ordinal)) return true;
            __instance.s_motivation = record.Motivation;
            return false;
        }

        private static void AfterAddMotivation(characterScript __instance)
        {
            if (active != null) active.RestoreLocked(__instance);
        }

        private static void AfterCharacterInit(characterScript __instance)
        {
            if (active != null) active.RestoreLocked(__instance);
        }

        private static void AfterHireCopy(mainScript __instance, characterScript cS_)
        {
            if (active == null) return;
            if (cS_ != null && __instance == Plugin.CurrentGame) active.playerEmployees.Add(cS_);
            active.RestoreLocked(cS_);
        }

        private static void BeforeNewGame()
        {
            if (active != null)
            {
                active.records.Clear();
                active.pendingLoaded = null;
                active.playerEmployees.Clear();
            }
        }

        private static void BeforeLoadGame()
        {
            if (active != null)
            {
                active.records.Clear();
                active.pendingLoaded = null;
                active.playerEmployees.Clear();
            }
        }

        private static void AfterLoadGame()
        {
            if (active != null) active.AttachLoaded();
        }

        private static void BeforeSaveEmployees()
        {
            if (active == null) return;
            if (active.records.Count == 0) return;
            mainScript game = Plugin.CurrentGame;
            HashSet<characterScript> currentEmployees = null;
            if (game != null && game.arrayCharactersScripts != null)
                currentEmployees = new HashSet<characterScript>(game.arrayCharactersScripts);
            var stale = new List<characterScript>();
            foreach (var entry in active.records)
            {
                characterScript character = entry.Key;
                Record record = entry.Value;
                if (character == null || record == null || character.myID != record.Id ||
                    !string.Equals(character.myName ?? "", record.Name, StringComparison.Ordinal) ||
                    currentEmployees != null && !currentEmployees.Contains(character))
                {
                    stale.Add(character);
                    continue;
                }
                active.RestoreLocked(character);
            }
            foreach (characterScript character in stale) active.records.Remove(character);
        }

        private static void AfterSaveEmployees(ES3Writer writer)
        {
            if (active == null || writer == null) return;
            try { writer.Write<string>(SaveKey, active.Serialize()); }
            catch (Exception ex) { active.log.LogError("Could not save character locks: " + ex); }
        }

        private static void AfterLoadEmployees(ES3Reader reader)
        {
            if (active == null || reader == null) return;
            try
            {
                string data = reader.Read<string>(SaveKey, "");
                active.Deserialize(data);
            }
            catch (Exception ex) { active.log.LogError("Could not load character locks: " + ex); }
        }

        private string Serialize()
        {
            var live = new List<KeyValuePair<characterScript, Record>>();
            foreach (var entry in records)
                if (entry.Key != null && entry.Value != null && entry.Key.myID == entry.Value.Id &&
                    string.Equals(entry.Key.myName ?? "", entry.Value.Name, StringComparison.Ordinal))
                    live.Add(entry);
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(FormatVersion);
                writer.Write(live.Count);
                foreach (var entry in live)
                {
                    Record record = entry.Value;
                    writer.Write(record.Id);
                    writer.Write(entry.Key.myName ?? "");
                    writer.Write(record.LockStats);
                    writer.Write(record.LockMotivation);
                    writer.Write(record.Motivation);
                    foreach (float value in record.Skills) writer.Write(value);
                }
                return Convert.ToBase64String(stream.ToArray());
            }
        }

        private void Deserialize(string data)
        {
            if (string.IsNullOrEmpty(data))
            {
                pendingLoaded = new Dictionary<int, Record>();
                return;
            }
            byte[] bytes = Convert.FromBase64String(data);
            if (bytes.Length > 1024 * 1024) throw new InvalidDataException("Character lock data is too large.");
            var saved = new Dictionary<int, Record>();
            using (var stream = new MemoryStream(bytes))
            using (var reader = new BinaryReader(stream))
            {
                int version = reader.ReadInt32();
                if (version != FormatVersion) throw new InvalidDataException("Unsupported character lock version " + version.ToString(CultureInfo.InvariantCulture));
                int count = reader.ReadInt32();
                if (count < 0 || count > MaxRecords) throw new InvalidDataException("Invalid character lock count.");
                for (int i = 0; i < count; i++)
                {
                    var record = new Record();
                    record.Id = reader.ReadInt32();
                    record.Name = reader.ReadString();
                    record.LockStats = reader.ReadBoolean();
                    record.LockMotivation = reader.ReadBoolean();
                    record.Motivation = SafeMotivation(reader.ReadSingle());
                    for (int j = 0; j < record.Skills.Length; j++) record.Skills[j] = SafeSkill(reader.ReadSingle());
                    if (IsFinite(record.Motivation) && AllFinite(record.Skills)) saved[record.Id] = record;
                }
            }
            pendingLoaded = saved;
        }

        private void AttachLoaded()
        {
            if (pendingLoaded == null) return;
            mainScript game = Plugin.CurrentGame;
            if (game == null) game = UnityEngine.Object.FindObjectOfType<mainScript>();
            if (game == null || game.arrayCharactersScripts == null) return;
            SetPlayer(game);
            foreach (characterScript character in game.arrayCharactersScripts)
            {
                Record record;
                if (character == null || !pendingLoaded.TryGetValue(character.myID, out record)) continue;
                if (!string.Equals(character.myName ?? "", record.Name, StringComparison.Ordinal)) continue;
                records[character] = record;
                RestoreLocked(character);
            }
            pendingLoaded = null;
            log.LogInfo("Restored " + records.Count.ToString(CultureInfo.InvariantCulture) + " character lock records from the game save.");
        }

        private static bool IsFinite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }

        private static float SafeMotivation(float value)
        {
            if (!IsFinite(value)) return 0f;
            return Mathf.Clamp(value, 0f, MotivationCap);
        }

        private static float SafeSkill(float value, float maximum = SupportedSkillCap)
        {
            if (!IsFinite(value)) return 0f;
            return Mathf.Clamp(value, 0f, maximum);
        }

        private static bool AllFinite(float[] values)
        {
            foreach (float value in values) if (!IsFinite(value)) return false;
            return true;
        }
    }
}
