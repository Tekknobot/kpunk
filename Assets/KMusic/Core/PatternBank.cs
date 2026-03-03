using System;
using System.Collections.Generic;
using UnityEngine;

namespace KMusic.Core
{
    /// <summary>
    /// PlayerPrefs-backed pattern bank.
    /// Uses your existing KMusicSaveState serializers (int arrays + bytes).
    /// </summary>
    public static class PatternBank
    {
        private const string Key_Index = "kmusic.pattern.index.v1";
        private const string Key_NextId = "kmusic.pattern.nextid.v1";

        [Serializable]
        private class PatternIndex
        {
            public List<int> ids = new();
            public List<string> names = new();
        }

        private static string KeyDrums(int id)  => $"kmusic.pattern.{id:000}.drum.stepmask";
        private static string KeySample(int id) => $"kmusic.pattern.{id:000}.sample.stepgrid";
        private static string KeySeq(int id)    => $"kmusic.pattern.{id:000}.seq.stepgrid";
        private static string KeyName(int id)   => $"kmusic.pattern.{id:000}.name";

        // ----------------------------
        // Project import/export
        // ----------------------------

        [Serializable]
        public class PatternBankSave
        {
            public int nextId;
            public List<int> ids = new();
            public List<string> names = new();

            // Parallel arrays aligned to ids[]
            public List<string> drumMaskB64 = new();
            public List<int[]> sampleSteps = new();
            public List<int[]> seqSteps = new();
        }

        public static void EnsureDefaultPatternExists()
        {
            var idx = LoadIndexInternal();
            if (idx.ids.Count > 0) return;

            idx.ids.Add(0);
            idx.names.Add("P01");
            SaveIndexInternal(idx);

            if (!PlayerPrefs.HasKey(Key_NextId))
                PlayerPrefs.SetInt(Key_NextId, 1);
        }

        public static IReadOnlyList<int> ListIds()
        {
            EnsureDefaultPatternExists();
            return LoadIndexInternal().ids;
        }

        public static string GetName(int id)
        {
            string n = PlayerPrefs.GetString(KeyName(id), "");
            if (!string.IsNullOrEmpty(n)) return n;

            // fallback to index names
            var idx = LoadIndexInternal();
            for (int i = 0; i < idx.ids.Count; i++)
                if (idx.ids[i] == id)
                    return (i < idx.names.Count && !string.IsNullOrEmpty(idx.names[i])) ? idx.names[i] : $"P{id + 1:00}";

            return $"P{id + 1:00}";
        }

        public static void SetName(int id, string name)
        {
            name ??= "";
            PlayerPrefs.SetString(KeyName(id), name);
            PlayerPrefs.Save();

            var idx = LoadIndexInternal();
            for (int i = 0; i < idx.ids.Count; i++)
            {
                if (idx.ids[i] != id) continue;
                while (idx.names.Count < idx.ids.Count) idx.names.Add("");
                idx.names[i] = name;
                SaveIndexInternal(idx);
                return;
            }
        }

        public static int CreateFrom(PatternData data, string name = null)
        {
            EnsureDefaultPatternExists();

            int next = PlayerPrefs.GetInt(Key_NextId, 1);
            int id = Mathf.Max(0, next);
            PlayerPrefs.SetInt(Key_NextId, id + 1);

            var idx = LoadIndexInternal();
            idx.ids.Add(id);
            idx.names.Add(string.IsNullOrEmpty(name) ? $"P{idx.ids.Count:00}" : name);
            SaveIndexInternal(idx);

            if (!string.IsNullOrEmpty(name))
                PlayerPrefs.SetString(KeyName(id), name);

            Save(id, data);
            return id;
        }

        public static int Duplicate(int sourceId)
        {
            var src = Load(sourceId);
            return CreateFrom(src, null);
        }

        public static void Save(int id, PatternData data)
        {
            if (data == null) return;

            // normalize sizes
            byte[] drums = new byte[PatternData.Steps];
            int[] sample = new int[PatternData.Steps];
            int[] seq = new int[PatternData.Steps];

            if (data.drumMask != null)
                Array.Copy(data.drumMask, drums, Math.Min(data.drumMask.Length, drums.Length));
            if (data.sampleSteps != null)
                Array.Copy(data.sampleSteps, sample, Math.Min(data.sampleSteps.Length, sample.Length));
            if (data.seqSteps != null)
                Array.Copy(data.seqSteps, seq, Math.Min(data.seqSteps.Length, seq.Length));

            KMusic.KMusicSaveState.SaveBytes(KeyDrums(id), drums);
            KMusic.KMusicSaveState.SaveIntArray(KeySample(id), sample);
            KMusic.KMusicSaveState.SaveIntArray(KeySeq(id), seq);
        }

        public static PatternData Load(int id)
        {
            var p = new PatternData();

            var drums = KMusic.KMusicSaveState.LoadBytes(KeyDrums(id), PatternData.Steps);
            var sample = KMusic.KMusicSaveState.LoadIntArray(KeySample(id), PatternData.Steps);
            var seq = KMusic.KMusicSaveState.LoadIntArray(KeySeq(id), PatternData.Steps);

            if (drums != null) p.drumMask = drums;
            if (sample != null) p.sampleSteps = sample;
            if (seq != null) p.seqSteps = seq;

            return p;
        }

        public static PatternBankSave ExportAll()
        {
            EnsureDefaultPatternExists();

            var idx = LoadIndexInternal();
            var save = new PatternBankSave
            {
                nextId = PlayerPrefs.GetInt(Key_NextId, 1),
                ids = new List<int>(idx.ids),
                names = new List<string>(idx.names)
            };

            for (int i = 0; i < idx.ids.Count; i++)
            {
                int id = idx.ids[i];
                var p = Load(id);
                save.drumMaskB64.Add(p.drumMask != null ? Convert.ToBase64String(p.drumMask) : "");
                save.sampleSteps.Add(p.sampleSteps);
                save.seqSteps.Add(p.seqSteps);
            }

            return save;
        }

        public static void ImportAll(PatternBankSave save)
        {
            if (save == null) return;

            ResetAll();

            var idx = new PatternIndex();
            if (save.ids != null) idx.ids.AddRange(save.ids);
            if (save.names != null) idx.names.AddRange(save.names);

            SaveIndexInternal(idx);
            PlayerPrefs.SetInt(Key_NextId, Mathf.Max(1, save.nextId));
            PlayerPrefs.Save();

            int count = idx.ids.Count;
            for (int i = 0; i < count; i++)
            {
                int id = idx.ids[i];

                byte[] drums = null;
                try
                {
                    if (save.drumMaskB64 != null && i < save.drumMaskB64.Count && !string.IsNullOrEmpty(save.drumMaskB64[i]))
                        drums = Convert.FromBase64String(save.drumMaskB64[i]);
                }
                catch { drums = null; }

                int[] sample = (save.sampleSteps != null && i < save.sampleSteps.Count) ? save.sampleSteps[i] : null;
                int[] seq = (save.seqSteps != null && i < save.seqSteps.Count) ? save.seqSteps[i] : null;

                Save(id, new PatternData { drumMask = drums, sampleSteps = sample, seqSteps = seq });

                if (save.names != null && i < save.names.Count && !string.IsNullOrEmpty(save.names[i]))
                    PlayerPrefs.SetString(KeyName(id), save.names[i]);
            }

            PlayerPrefs.Save();
            EnsureDefaultPatternExists();
        }

        public static void ResetAll()
        {
            try
            {
                PlayerPrefs.DeleteKey(Key_Index);
                PlayerPrefs.DeleteKey(Key_NextId);

                // Best-effort cleanup for ids 0..999.
                for (int id = 0; id <= 999; id++)
                {
                    PlayerPrefs.DeleteKey(KeyDrums(id));
                    PlayerPrefs.DeleteKey(KeySample(id));
                    PlayerPrefs.DeleteKey(KeySeq(id));
                    PlayerPrefs.DeleteKey(KeyName(id));
                }

                PlayerPrefs.Save();
            }
            catch
            {
                // ignore
            }

            EnsureDefaultPatternExists();
        }

        private static PatternIndex LoadIndexInternal()
        {
            try
            {
                if (!PlayerPrefs.HasKey(Key_Index))
                    return new PatternIndex();

                var json = PlayerPrefs.GetString(Key_Index, "");
                if (string.IsNullOrEmpty(json))
                    return new PatternIndex();

                var idx = JsonUtility.FromJson<PatternIndex>(json);
                return idx ?? new PatternIndex();
            }
            catch
            {
                return new PatternIndex();
            }
        }

        private static void SaveIndexInternal(PatternIndex idx)
        {
            try
            {
                PlayerPrefs.SetString(Key_Index, JsonUtility.ToJson(idx));
                PlayerPrefs.Save();
            }
            catch
            {
                // ignore
            }
        }
    }
}
