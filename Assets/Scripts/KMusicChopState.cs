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
        // ----------------------------
        // Runtime cache (device-loaded clips)
        // ----------------------------
        private static AudioClip _cachedClip;
        private static string _cachedId;

        // Bumped any time applied chops are saved/cleared so samplers can refresh.
        private static int _appliedRevision;
        public static int AppliedRevision => _appliedRevision;

public static bool TryGetCachedClip(out AudioClip clip)
        {
            clip = _cachedClip;
            return clip != null;
        }

        

        public static void SetCachedClip(string id, AudioClip clip)
        {
            _cachedId = id;
            _cachedClip = clip;
        }

        public static bool TryGetCachedClip(string id, out AudioClip clip)
        {
            if (!string.IsNullOrEmpty(id) && id == _cachedId && _cachedClip != null)
            {
                clip = _cachedClip;
                return true;
            }
            clip = null;
            return false;
        }

        public static void ClearApplied()
        {
            if (PlayerPrefs.HasKey(PrefKey_Json))
            {
                PlayerPrefs.DeleteKey(PrefKey_Json);
                PlayerPrefs.Save();
            }

            _appliedRevision++;
            // Keep cached clip/id; caller may be mid-session with a device-loaded clip.
            Debug.Log("[Chops] Cleared applied chops.");
        }

public static void SaveAppliedFromClip(AudioClip clip, string resourcesPathOrNull, IList<float> markerPositions01)
        {
            string id = !string.IsNullOrEmpty(resourcesPathOrNull)
                ? resourcesPathOrNull
                : (clip != null ? $"cached:{clip.name}" : "cached:null");

            // convert to List<float> to match SaveApplied signature
            var list = markerPositions01 != null
                ? new List<float>(markerPositions01)
                : new List<float>();

            SaveApplied(id, list);

            _cachedId = id;
            _cachedClip = clip;
        }     
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

            // Only use real user markers
            var boundaries = new List<float>();

            if (userMarkers01 != null)
            {
                for (int i = 0; i < userMarkers01.Count; i++)
                {
                    float t = Mathf.Clamp01(userMarkers01[i]);

                    // ignore edges (implicit boundaries)
                    if (t <= 0.0001f || t >= 0.9999f)
                        continue;

                    boundaries.Add(t);
                }
            }

            boundaries.Sort();

            boundaries.Add(1f);
            boundaries.Sort();

            // Build up to 16 slices.
            var starts = new float[16];
            var ends = new float[16];

            int sliceCount = Mathf.Max(0, boundaries.Count - 1);
            for (int i = 0; i < 16; i++)
            {
                if (i >= sliceCount)
                {
                    starts[i] = 0f;
                    ends[i] = 0f;
                    continue;
                }

                float s = boundaries[i];
                float e = boundaries[i + 1];

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

            _appliedRevision++;

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
