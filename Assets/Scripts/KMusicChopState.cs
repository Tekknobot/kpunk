using System;
using System.Collections.Generic;
using UnityEngine;
using KMusic.Core;
using ZPunk.DAW;

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
        // Runtime-only map for the cropped warped AudioClip. Project saves keep
        // all original full-song 0..1 slice positions.
        private static float _runtimeStart01;
        private static float _runtimeEnd01 = 1f;
        private static List<ZPunkSampleWarp.TimeMapPoint> _runtimeTimeMap;

        // Bumped any time applied chops are saved/cleared so samplers can refresh.
        private static int _appliedRevision;
        public static int AppliedRevision => _appliedRevision;

public static bool TryGetCachedClip(out AudioClip clip)
        {
            clip = _cachedClip;
            return clip != null;
        }

        

        public static void SetCachedClip(string id, AudioClip clip,
            float regionStart01 = 0f, float regionEnd01 = 1f,
            IList<ZPunkSampleWarp.TimeMapPoint> timeMap = null)
        {
            bool changed = _cachedClip != clip || _cachedId != id ||
                _runtimeTimeMap != null || timeMap != null;
            _cachedId = id;
            _cachedClip = clip;
            _runtimeStart01 = Mathf.Clamp01(regionStart01);
            _runtimeEnd01 = Mathf.Clamp01(regionEnd01);
            _runtimeTimeMap = timeMap != null
                ? new List<ZPunkSampleWarp.TimeMapPoint>(timeMap) : null;
            if (changed) _appliedRevision++;
        }

        // Converts original saved slice positions to the active (shortened)
        // AudioClip. Never call this on persisted project values.
        public static float MapSourceToCachedClip(string id, float source01)
        {
            source01 = Mathf.Clamp01(source01);
            if (_cachedClip == null || _cachedId != id || _runtimeTimeMap == null ||
                _runtimeTimeMap.Count < 2) return source01;
            float local = Mathf.Clamp01((source01 - _runtimeStart01) /
                Mathf.Max(.0000001f, _runtimeEnd01 - _runtimeStart01));
            for (int i = 0; i + 1 < _runtimeTimeMap.Count; i++)
            {
                var a = _runtimeTimeMap[i];
                var b = _runtimeTimeMap[i + 1];
                if (local > b.source01 && i + 2 < _runtimeTimeMap.Count) continue;
                return Mathf.Lerp(a.destination01, b.destination01,
                    Mathf.Clamp01((local - a.source01) /
                        Mathf.Max(.0000001f, b.source01 - a.source01)));
            }
            return local;
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
            if (ProjectPrefs.HasKey(PrefKey_Json))
            {
                ProjectPrefs.DeleteKey(PrefKey_Json);
                ProjectPrefs.Save();
            }

            _appliedRevision++;
            // Keep cached clip/id; caller may be mid-session with a device-loaded clip.
            Debug.Log("[Chops] Cleared applied chops.");
        }

public static void SaveAppliedFromClip(AudioClip clip, string resourcesPathOrNull,
            IList<float> markerPositions01, bool lastMarkerIsEndOnly = false)
        {
            string id = !string.IsNullOrEmpty(resourcesPathOrNull)
                ? resourcesPathOrNull
                : (clip != null ? $"cached:{clip.name}" : "cached:null");

            // convert to List<float> to match SaveApplied signature
            var list = markerPositions01 != null
                ? new List<float>(markerPositions01)
                : new List<float>();

            SaveApplied(id, list, lastMarkerIsEndOnly);

            // SaveApplied() increments the revision and retains the currently
            // playing cached warp for the same source. Do not swap it back to
            // original audio just because an applied marker was edited.
            if (_cachedClip == null || _cachedId != id)
                SetCachedClip(id, clip);
        }     
        // Keep keys stable.
        private const string PrefKey_Json = "kmusic.chops.applied.v1";

        [Serializable]
        private class ChopSave
        {
            public string resourcesPath;      // e.g. "Tracks/01 My Song"
            public float[] sliceStart01;      // length 16
            public float[] sliceEnd01;        // length 16
            public float[] markerPositions01; // explicit user markers, allows 17th marker to close chop 16
            public bool lastMarkerIsEndOnly; // true for hit-sliced closing boundary (including partial sets)
        }

        public static void SaveApplied(string resourcesPath, List<float> userMarkers01,
            bool lastMarkerIsEndOnly = false)
        {
            if (string.IsNullOrEmpty(resourcesPath)) return;

            // Markers represent chop START points.
            // So if the first user marker is at 0.245, chop 01 starts at 0.245
            // instead of implicitly creating a 0.000 -> 0.245 chop.
            var markers = new List<float>();

            if (userMarkers01 != null)
            {
                for (int i = 0; i < userMarkers01.Count; i++)
                {
                    float t = Mathf.Clamp01(userMarkers01[i]);

                    // 0.0 is an explicit playable slice start; only the end
                    // of the sample is a non-playable closing boundary.
                    if (t >= 0.9999f)
                        continue;

                    markers.Add(t);
                }
            }

            markers.Sort();
            for (int i = markers.Count - 1; i > 0; i--)
                if (Mathf.Abs(markers[i] - markers[i - 1]) < 0.0001f)
                    markers.RemoveAt(i);

            // Build up to 16 playable slices from marker[i] -> marker[i+1] / end-of-file.
            // Allow a 17th marker so chop 16 can end before the full sample tail.
            var starts = new float[16];
            var ends = new float[16];

            lastMarkerIsEndOnly = lastMarkerIsEndOnly && markers.Count >= 2;
            int playableCount = Mathf.Min(16, markers.Count - (lastMarkerIsEndOnly ? 1 : 0));
            for (int i = 0; i < 16; i++)
            {
                if (i >= playableCount)
                {
                    starts[i] = 0f;
                    ends[i] = 0f;
                    continue;
                }

                float s = markers[i];
                float e = (i + 1 < markers.Count) ? markers[i + 1] : 1f;

                starts[i] = s;
                ends[i] = Mathf.Max(s, e);
            }

            var save = new ChopSave
            {
                resourcesPath = resourcesPath,
                sliceStart01 = starts,
                sliceEnd01 = ends,
                markerPositions01 = markers.ToArray(),
                lastMarkerIsEndOnly = lastMarkerIsEndOnly
            };

            ProjectPrefs.SetString(PrefKey_Json, JsonUtility.ToJson(save));
            ProjectPrefs.Save();

            _appliedRevision++;

            Debug.Log($"[Chops] Saved applied chops for '{resourcesPath}'.");
        }

        // Retain exactly what the user marked, including a marker at 0.0.
        // A legacy project may have slice boundaries but no explicit markers.
        public static bool TryLoadAppliedMarkerPositions(out float[] markers01)
        {
            bool ignored;
            return TryLoadAppliedMarkerPositions(out markers01, out ignored);
        }

        public static bool TryLoadAppliedMarkerPositions(out float[] markers01,
            out bool lastMarkerIsEndOnly)
        {
            markers01 = null;
            lastMarkerIsEndOnly = false;
            if (!ProjectPrefs.HasKey(PrefKey_Json)) return false;
            try
            {
                var save = JsonUtility.FromJson<ChopSave>(ProjectPrefs.GetString(PrefKey_Json, ""));
                if (save == null || save.markerPositions01 == null) return false;
                markers01 = (float[])save.markerPositions01.Clone();
                lastMarkerIsEndOnly = save.lastMarkerIsEndOnly;
                Array.Sort(markers01);
                return true;
            }
            catch (Exception) { return false; }
        }

        public static bool TryLoadApplied(out string resourcesPath, out float[] sliceStart01, out float[] sliceEnd01)
        {
            resourcesPath = null;
            sliceStart01 = null;
            sliceEnd01 = null;

            if (!ProjectPrefs.HasKey(PrefKey_Json))
                return false;

            var json = ProjectPrefs.GetString(PrefKey_Json, "");
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

        /// <summary>
        /// Directly set applied chops (used by project load).
        /// Bumps AppliedRevision so samplers refresh.
        /// </summary>
        public static void SetAppliedRaw(string resourcesPath, float[] sliceStart01, float[] sliceEnd01,
            float[] exactMarkers01 = null, bool exactClosingMarker = false)
        {
            if (string.IsNullOrEmpty(resourcesPath)) return;

            var starts = new float[16];
            var ends = new float[16];

            if (sliceStart01 != null)
                Array.Copy(sliceStart01, starts, Mathf.Min(16, sliceStart01.Length));
            if (sliceEnd01 != null)
                Array.Copy(sliceEnd01, ends, Mathf.Min(16, sliceEnd01.Length));

            var markers = new List<float>();
            int finalPlayable = -1;
            for (int i = 0; i < starts.Length; i++)
            {
                float s = starts[i];
                float e = ends[i];
                if (e > s && (s > 0.0001f || (i == 0 && e > 0f)))
                {
                    markers.Add(Mathf.Clamp01(s));
                    finalPlayable = i;
                }
            }
            // 2.1.1 snapshots preserve the exact user marker list and its
            // closing-boundary flag. Legacy snapshots still reconstruct it.
            bool closing = finalPlayable >= 0 &&
                ends[finalPlayable] > starts[finalPlayable] + 0.0001f &&
                ends[finalPlayable] < 0.9999f;
            if (exactMarkers01 != null)
            {
                markers.Clear();
                foreach (float position in exactMarkers01)
                {
                    if (float.IsNaN(position) || float.IsInfinity(position)) continue;
                    float clamped = Mathf.Clamp01(position);
                    if (clamped >= .9999f) continue;
                    markers.Add(clamped);
                }
                markers.Sort();
                for (int i = markers.Count - 1; i > 0; i--)
                    if (Mathf.Abs(markers[i] - markers[i - 1]) < .0001f)
                        markers.RemoveAt(i);
                closing = exactClosingMarker && markers.Count >= 2;
            }
            else if (closing) markers.Add(Mathf.Clamp01(ends[finalPlayable]));

            var save = new ChopSave
            {
                resourcesPath = resourcesPath,
                sliceStart01 = starts,
                sliceEnd01 = ends,
                markerPositions01 = markers.ToArray(),
                lastMarkerIsEndOnly = closing
            };

            ProjectPrefs.SetString(PrefKey_Json, JsonUtility.ToJson(save));
            ProjectPrefs.Save();
            _appliedRevision++;
        }
    }
}
