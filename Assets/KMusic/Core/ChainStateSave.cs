using System;
using UnityEngine;

namespace KMusic.Core
{
    /// <summary>
    /// Project-friendly snapshot of ChainState.
    /// ChainState itself is PlayerPrefs-backed; this wrapper can apply into PlayerPrefs.
    /// </summary>
    [Serializable]
    public sealed class ChainStateSave
    {
        private const string Key_ChainSlots = "kmusic.chain.slots.v1";
        private const string Key_ChainLen = "kmusic.chain.len.v1";
        private const string Key_ChainCursor = "kmusic.chain.cursor.v1";
        private const string Key_ChainEnabled = "kmusic.chain.enabled.v1";

        [Serializable]
        private class IntArraySave { public int[] v; }

        public int length = 32;
        public int cursor = 0;
        public bool enabled = false;
        public int[] slots = new int[64];

        public static ChainStateSave From(ChainState s)
        {
            var save = new ChainStateSave();
            if (s == null) return save;

            save.length = Mathf.Clamp(s.length, 1, 64);
            save.cursor = Mathf.Clamp(s.cursor, 0, 63);
            save.enabled = s.enabled;
            save.slots = new int[64];
            if (s.slots != null)
                Array.Copy(s.slots, save.slots, Mathf.Min(64, s.slots.Length));
            return save;
        }

        public void ApplyToPrefs()
        {
            try
            {
                PlayerPrefs.SetInt(Key_ChainLen, Mathf.Clamp(length, 1, 64));
                PlayerPrefs.SetInt(Key_ChainCursor, Mathf.Clamp(cursor, 0, 63));
                PlayerPrefs.SetInt(Key_ChainEnabled, enabled ? 1 : 0);
                PlayerPrefs.SetString(Key_ChainSlots, JsonUtility.ToJson(new IntArraySave { v = slots ?? new int[64] }));
                PlayerPrefs.Save();
            }
            catch
            {
                // ignore
            }
        }
    }
}
