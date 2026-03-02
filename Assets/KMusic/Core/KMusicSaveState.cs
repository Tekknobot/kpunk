using System;
using System.Collections.Generic;
using UnityEngine;
using KMusic.Core;

namespace KMusic
{
    /// <summary>
    /// Small PlayerPrefs-backed save state for KMusic UI.
    /// Keeps it dead-simple for mobile: patterns + mixer values persist across launches.
    /// </summary>
    public static class KMusicSaveState
    {
        // ----------------------------
        // ParameterBus
        // ----------------------------

        [Serializable]
        private class ParamPair
        {
            public string id;
            public float v;
        }

        [Serializable]
        private class ParamSave
        {
            public List<ParamPair> items = new();
        }

        public static void SaveBus(ParameterBus bus, string key)
        {
            if (bus == null) return;
            var s = new ParamSave();
            foreach (var p in bus.All)
                s.items.Add(new ParamPair { id = p.Id, v = p.Value });

            PlayerPrefs.SetString(key, JsonUtility.ToJson(s));
            PlayerPrefs.Save();
        }

        public static void LoadBus(ParameterBus bus, string key)
        {
            if (bus == null) return;
            if (!PlayerPrefs.HasKey(key)) return;

            try
            {
                var json = PlayerPrefs.GetString(key, "");
                if (string.IsNullOrEmpty(json)) return;
                var s = JsonUtility.FromJson<ParamSave>(json);
                if (s?.items == null) return;
                foreach (var it in s.items)
                {
                    if (string.IsNullOrEmpty(it.id)) continue;
                    bus.SetValue(it.id, it.v);
                }
            }
            catch
            {
                // ignore corrupt prefs
            }
        }

        // ----------------------------
        // Int arrays (StepGrid patterns)
        // ----------------------------

        [Serializable]
        private class IntArraySave
        {
            public int[] v;
        }

        public static void SaveIntArray(string key, int[] values)
        {
            if (values == null) return;
            PlayerPrefs.SetString(key, JsonUtility.ToJson(new IntArraySave { v = values }));
            PlayerPrefs.Save();
        }

        public static int[] LoadIntArray(string key, int expectedLen)
        {
            if (!PlayerPrefs.HasKey(key)) return null;
            try
            {
                var s = JsonUtility.FromJson<IntArraySave>(PlayerPrefs.GetString(key, ""));
                if (s?.v == null) return null;
                if (expectedLen > 0 && s.v.Length != expectedLen) return null;
                return s.v;
            }
            catch
            {
                return null;
            }
        }

        // ----------------------------
        // Bytes (drum stepmask)
        // ----------------------------

        public static void SaveBytes(string key, byte[] bytes)
        {
            if (bytes == null) return;
            PlayerPrefs.SetString(key, Convert.ToBase64String(bytes));
            PlayerPrefs.Save();
        }

        public static byte[] LoadBytes(string key, int expectedLen)
        {
            if (!PlayerPrefs.HasKey(key)) return null;
            try
            {
                var b = Convert.FromBase64String(PlayerPrefs.GetString(key, ""));
                if (expectedLen > 0 && b.Length != expectedLen) return null;
                return b;
            }
            catch
            {
                return null;
            }
        }

        // ----------------------------
        // Bool arrays (mutes)
        // ----------------------------

        [Serializable]
        private class BoolArraySave
        {
            public bool[] v;
        }

        public static void SaveBools(string key, bool[] values)
        {
            if (values == null) return;
            PlayerPrefs.SetString(key, JsonUtility.ToJson(new BoolArraySave { v = values }));
            PlayerPrefs.Save();
        }

        public static bool[] LoadBools(string key, int expectedLen)
        {
            if (!PlayerPrefs.HasKey(key)) return null;
            try
            {
                var s = JsonUtility.FromJson<BoolArraySave>(PlayerPrefs.GetString(key, ""));
                if (s?.v == null) return null;
                if (expectedLen > 0 && s.v.Length != expectedLen) return null;
                return s.v;
            }
            catch
            {
                return null;
            }
        }

        public static void SaveBus(ParameterBus bus, string key, Func<string, bool> allowId)
        {
            if (bus == null) return;
            var s = new ParamSave();
            foreach (var p in bus.All)
            {
                if (allowId != null && !allowId(p.Id)) continue;
                s.items.Add(new ParamPair { id = p.Id, v = p.Value });
            }

            PlayerPrefs.SetString(key, JsonUtility.ToJson(s));
            PlayerPrefs.Save();
        }

        public static void LoadBus(ParameterBus bus, string key, Func<string, bool> allowId)
        {
            if (bus == null) return;
            if (!PlayerPrefs.HasKey(key)) return;

            try
            {
                var json = PlayerPrefs.GetString(key, "");
                if (string.IsNullOrEmpty(json)) return;
                var s = JsonUtility.FromJson<ParamSave>(json);
                if (s?.items == null) return;

                foreach (var it in s.items)
                {
                    if (string.IsNullOrEmpty(it.id)) continue;
                    if (allowId != null && !allowId(it.id)) continue;

                    bus.SetValue(it.id, it.v);
                }
            }
            catch
            {
                // ignore corrupt prefs
            }
        }        
    }
}
