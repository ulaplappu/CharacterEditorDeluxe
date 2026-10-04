using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
        private const float SafeStatCap = 100f;

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
        private readonly ManualLogSource log;
        private readonly Dictionary<characterScript, Record> records = new Dictionary<characterScript, Record>();
        private Dictionary<int, Record> pendingLoaded;
        private Harmony harmony;

        internal readonly ConfigEntry<bool> GlobalLock;

        internal StatOverrides(ManualLogSource log, ConfigEntry<bool> globalLock)
        {
            this.log = log;
            GlobalLock = globalLock;
        }

        internal void Install()
        {
            active = this;
            harmony = new Harmony("com.codex.mgt2.charactereditordeluxe.statlocks");
            Patch(typeof(characterScript), "Learn", null, nameof(AfterLearn));
            Patch(typeof(characterScript), "AddMotivation", nameof(BeforeAddMotivation), nameof(AfterAddMotivation));
            Patch(typeof(characterScript), "Init", null, nameof(AfterCharacterInit));
            Patch(typeof(mainScript), "CopyArbeitsmarktCharacterData", null, nameof(AfterHireCopy));
            Patch(typeof(mainScript), "InitNewGame", nameof(BeforeNewGame), null);
            Patch(typeof(savegameScript), "SaveMitarbeiter", nameof(BeforeSaveEmployees), nameof(AfterSaveEmployees));
            Patch(typeof(savegameScript), "LoadMitarbeiter", null, nameof(AfterLoadEmployees));
            Patch(typeof(savegameScript), "Load", nameof(BeforeLoadGame), nameof(AfterLoadGame));
        }

        internal void Uninstall()
        {
            if (harmony != null) harmony.UnpatchSelf();
            if (active == this) active = null;
        }

        private void Patch(Type type, string name, string prefix, string postfix)
        {
            var method = AccessTools.Method(type, name);
            if (method == null) throw new MissingMethodException(type.FullName, name);
            HarmonyMethod before = prefix == null ? null : new HarmonyMethod(typeof(StatOverrides), prefix);
            HarmonyMethod after = postfix == null ? null : new HarmonyMethod(typeof(StatOverrides), postfix) { priority = Priority.Last };
            harmony.Patch(method, before, after);
        }

        internal bool IsStatsLocked(characterScript character)
        {
            Record record;
            return character != null && records.TryGetValue(character, out record) && record.LockStats;
        }

        internal bool IsMotivationLocked(characterScript character)
        {
            Record record;
            return character != null && records.TryGetValue(character, out record) && record.LockMotivation;
        }

        internal void SetStatsLock(characterScript character, bool value)
        {
            if (character == null) return;
            Record record = GetOrCreate(character);
            if (value) CaptureSkills(record, character);
            record.LockStats = value;
        }

        internal void SetMotivationLock(characterScript character, bool value)
        {
            if (character == null) return;
            Record record = GetOrCreate(character);
            if (value) record.Motivation = character.s_motivation;
            record.LockMotivation = value;
        }

        internal void RestoreAllLocked()
        {
            foreach (var entry in records) RestoreLocked(entry.Key);
        }

        internal void RecordApplied(characterScript character, bool skillsChanged, bool motivationChanged)
        {
            if (character == null || (!skillsChanged && !motivationChanged)) return;
            Record record = GetOrCreate(character);
            if (skillsChanged)
            {
                CaptureSkills(record, character);
                record.LockStats = true;
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
            character.s_gamedesign = SafeStat(record.Skills[0]);
            character.s_programmieren = SafeStat(record.Skills[1]);
            character.s_grafik = SafeStat(record.Skills[2]);
            character.s_sound = SafeStat(record.Skills[3]);
            character.s_pr = SafeStat(record.Skills[4]);
            character.s_gametests = SafeStat(record.Skills[5]);
            character.s_technik = SafeStat(record.Skills[6]);
            character.s_forschen = SafeStat(record.Skills[7]);
        }

        private void RestoreLocked(characterScript character)
        {
            Record record;
            if (character == null || !records.TryGetValue(character, out record)) return;
            if (GlobalLock.Value && record.LockStats) RestoreSkills(record, character);
            if (record.LockMotivation) character.s_motivation = SafeStat(record.Motivation);
        }

        private static void AfterLearn(characterScript __instance)
        {
            if (active != null) active.RestoreLocked(__instance);
        }

        private static bool BeforeAddMotivation(characterScript __instance)
        {
            if (active == null) return true;
            Record record;
            if (__instance == null || !active.records.TryGetValue(__instance, out record) || !record.LockMotivation) return true;
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

        private static void AfterHireCopy(characterScript cS_)
        {
            if (active != null) active.RestoreLocked(cS_);
        }

        private static void BeforeNewGame()
        {
            if (active != null) { active.records.Clear(); active.pendingLoaded = null; }
        }

        private static void BeforeLoadGame()
        {
            if (active != null) { active.records.Clear(); active.pendingLoaded = null; }
        }

        private static void AfterLoadGame()
        {
            if (active != null) active.AttachLoaded();
        }

        private static void BeforeSaveEmployees()
        {
            if (active == null) return;
            foreach (var entry in active.records) active.RestoreLocked(entry.Key);
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
                if (entry.Key != null && entry.Value != null && entry.Key.myID == entry.Value.Id)
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
            if (string.IsNullOrEmpty(data)) return;
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
                    record.Motivation = SafeStat(reader.ReadSingle());
                    for (int j = 0; j < record.Skills.Length; j++) record.Skills[j] = SafeStat(reader.ReadSingle());
                    if (IsFinite(record.Motivation) && AllFinite(record.Skills)) saved[record.Id] = record;
                }
            }
            pendingLoaded = saved;
        }

        private void AttachLoaded()
        {
            if (pendingLoaded == null) return;
            mainScript game = UnityEngine.Object.FindObjectOfType<mainScript>();
            if (game == null || game.arrayCharactersScripts == null) return;
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

        private static float SafeStat(float value)
        {
            if (!IsFinite(value)) return 0f;
            return Mathf.Clamp(value, 0f, SafeStatCap);
        }

        private static bool AllFinite(float[] values)
        {
            foreach (float value in values) if (!IsFinite(value)) return false;
            return true;
        }
    }
}
