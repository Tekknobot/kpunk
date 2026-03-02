using System;
using System.Collections.Generic;
using UnityEngine;

namespace KMusic
{
    /// <summary>
    /// Stores the currently applied chop set (up to 16 slices) for the Sampler.
    /// Saved in PlayerPrefs by the Player page when you press APPLY.
    ///
    /// We store explicit slice start/end arrays (0..1) so the sampler has a stable 1..16 mapping
    /// even if the user creates >15 markers.
    /// </summary>
    public static class KMusicChopState
    {
        // Keep keys stable.
        private const string PrefKey_Json = "kmusic.chops.applied.v1";

        [Serializable]
        private class ChopSave
        {
            public string resourcesPath;      // e.g. "Tracks/01 My Song"
            public float[] sliceStart01;      // length 16
            public float[] sliceEnd01;        // length 16
        }

        public static void SaveApplied(string resourcesPath, List<float> userMarkers01)
        {
            if (string.IsNullOrEmpty(resourcesPath)) return;

            // Boundaries: 0 + markers + 1
            var boundaries = new List<float>((userMarkers01?.Count ?? 0) + 2) { 0f };
            if (userMarkers01 != null)
            {
                for (int i = 0; i < userMarkers01.Count; i++)
                    boundaries.Add(Mathf.Clamp01(userMarkers01[i]));
            }
            boundaries.Add(1f);
            boundaries.Sort();

            // Build up to 16 slices.
            var starts = new float[16];
            var ends = new float[16];

            int sliceCount = Mathf.Max(1, boundaries.Count - 1);
            for (int i = 0; i < 16; i++)
            {
                int a = Mathf.Clamp(i, 0, sliceCount - 1);
                int b = Mathf.Clamp(i + 1, 1, sliceCount);
                float s = boundaries[a];
                float e = boundaries[b];

                // If we ran out of real slices, just make the remainder silent (0..0).
                if (i >= sliceCount)
                {
                    s = 0f;
                    e = 0f;
                }

                starts[i] = s;
                ends[i] = Mathf.Max(s, e);
            }

            var save = new ChopSave
            {
                resourcesPath = resourcesPath,
                sliceStart01 = starts,
                sliceEnd01 = ends
            };

            PlayerPrefs.SetString(PrefKey_Json, JsonUtility.ToJson(save));
            PlayerPrefs.Save();

            Debug.Log($"[Chops] Saved applied chops for '{resourcesPath}'.");
        }

        public static bool TryLoadApplied(out string resourcesPath, out float[] sliceStart01, out float[] sliceEnd01)
        {
            resourcesPath = null;
            sliceStart01 = null;
            sliceEnd01 = null;

            if (!PlayerPrefs.HasKey(PrefKey_Json))
                return false;

            var json = PlayerPrefs.GetString(PrefKey_Json, "");
            if (string.IsNullOrEmpty(json))
                return false;

            try
            {
                var save = JsonUtility.FromJson<ChopSave>(json);
                if (save == null) return false;
                if (string.IsNullOrEmpty(save.resourcesPath)) return false;
                if (save.sliceStart01 == null || save.sliceEnd01 == null) return false;
                if (save.sliceStart01.Length < 16 || save.sliceEnd01.Length < 16) return false;

                resourcesPath = save.resourcesPath;

                sliceStart01 = new float[16];
                sliceEnd01 = new float[16];
                Array.Copy(save.sliceStart01, sliceStart01, 16);
                Array.Copy(save.sliceEnd01, sliceEnd01, 16);

                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
