using System;
using UnityEngine;
using KMusic.Core;

namespace KMusic.Core
{
    /// <summary>
    /// A simple bar-by-bar chain. Each slot references a PatternBank pattern id.
    /// </summary>
    [Serializable]
    public sealed class ChainState
    {
        private const string Key_ChainSlots = "kmusic.chain.slots.v1";
        private const string Key_ChainLen = "kmusic.chain.len.v1";
        private const string Key_ChainCursor = "kmusic.chain.cursor.v1";
        private const string Key_ChainEnabled = "kmusic.chain.enabled.v1";

        [Serializable]
        private class IntArraySave { public int[] v; }

        public int length = 32;
        public int cursor = 0;            // UI cursor (0..length-1)
        public bool enabled = false;      // song mode
        public int[] slots = new int[64]; // pattern id or -1

        public ChainState()
        {
            for (int i = 0; i < slots.Length; i++) slots[i] = -1;
        }

        public int GetSlot(int barIndex)
        {
            if (barIndex < 0) return -1;
            if (length <= 0) return -1;
            int i = barIndex % Mathf.Clamp(length, 1, slots.Length);
            return slots[i];
        }

        public void SetSlot(int barIndex, int patternId)
        {
            if (barIndex < 0 || barIndex >= slots.Length) return;
            slots[barIndex] = patternId;
        }

        public void Clear()
        {
            for (int i = 0; i < slots.Length; i++) slots[i] = -1;
        }

        public void Save()
        {
            try
            {
                ProjectPrefs.SetInt(Key_ChainLen, Mathf.Clamp(length, 1, 64));
                ProjectPrefs.SetInt(Key_ChainCursor, Mathf.Clamp(cursor, 0, 63));
                ProjectPrefs.SetInt(Key_ChainEnabled, enabled ? 1 : 0);
                ProjectPrefs.SetString(Key_ChainSlots, JsonUtility.ToJson(new IntArraySave { v = slots }));
                ProjectPrefs.Save();
            }
            catch
            {
                // ignore
            }
        }

        public static ChainState LoadOrCreate()
        {
            var s = new ChainState();
            try
            {
                s.length = Mathf.Clamp(ProjectPrefs.GetInt(Key_ChainLen, 32), 1, 64);
                s.cursor = Mathf.Clamp(ProjectPrefs.GetInt(Key_ChainCursor, 0), 0, 63);
                s.enabled = ProjectPrefs.GetInt(Key_ChainEnabled, 0) != 0;

                if (ProjectPrefs.HasKey(Key_ChainSlots))
                {
                    var json = ProjectPrefs.GetString(Key_ChainSlots, "");
                    var arr = JsonUtility.FromJson<IntArraySave>(json);
                    if (arr?.v != null && arr.v.Length == 64)
                        s.slots = arr.v;
                }
            }
            catch
            {
                // ignore
            }

            return s;
        }
    }
}
