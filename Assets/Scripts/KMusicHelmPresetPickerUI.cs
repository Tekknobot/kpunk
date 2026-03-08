// Assets/Scripts/KMusicHelmPresetPickerUI.cs
using System;
using UnityEngine.Networking;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;
using AudioHelm;
using KMusic.Core;

namespace KMusic
{
    [DefaultExecutionOrder(110)]
    public class KMusicHelmPresetPickerUI : MonoBehaviour
    {
        [Header("UI Doc")]
        [SerializeField] private UIDocument doc;

        [Header("Helm")]
        [SerializeField] private HelmController helm; // auto-find if null

        [Header("StreamingAssets")]
        [SerializeField] private string presetsRootFolder = "HelmPresets";
        [SerializeField] private string indexFileName = "presets_index.txt";

        [Header("UXML Names (Patch Browser)")]
        [SerializeField] private string patchPrevBtnName = "PatchPrevButton";
        [SerializeField] private string patchNextBtnName = "PatchNextButton";
        [SerializeField] private string patchNameLabelName = "PatchNameLabel";
        [SerializeField] private string patchIndexLabelName = "PatchIndexLabel";

        private Button _prevBtn;
        private Button _nextBtn;
        private Label _patchNameLabel;
        private Label _indexLabel;

        private readonly List<PresetEntry> _all = new();
        private int _index = 0;

        private HelmPatch _runtimePatch;

        // Optional (we try to call RequestPullFromHelm via reflection if you add it later)
        private KMusicHelmSynthBinder _binder;

        public event Action<int, string> PresetApplied;

        private class PresetEntry
        {
            public string relPath;   // relative to HelmPresets/
            public string display;   // nice name
        }

        private const string PrefKey_SynthPresetRelPath = "kmusic.synth.preset.relpath.v1";
        private const string PrefKey_SynthPresetIndex = "kmusic.synth.preset.index.v1";

        // Project system hooks
        public int CurrentPresetIndex => _index;

        public string CurrentPresetRelPath
        {
            get
            {
                if (_all != null && _all.Count > 0 && _index >= 0 && _index < _all.Count)
                    return _all[_index].relPath;
                return ProjectPrefs.GetString(PrefKey_SynthPresetRelPath, "");
            }
        }

        private int _pendingPresetIndex = -1;
        private string _pendingPresetRelPath = null;

        private void SaveCurrentPresetChoice()
        {
            if (_all == null || _all.Count == 0) return;

            ProjectPrefs.SetInt(PrefKey_SynthPresetIndex, _index);
            ProjectPrefs.SetString(PrefKey_SynthPresetRelPath, _all[_index].relPath ?? "");
            ProjectPrefs.Save();
        }

        private void RestorePresetChoice()
        {
            if (_all == null || _all.Count == 0) return;

            // Prefer relPath (stable if index file changes order)
            string want = ProjectPrefs.GetString(PrefKey_SynthPresetRelPath, "");
            if (!string.IsNullOrEmpty(want))
            {
                for (int i = 0; i < _all.Count; i++)
                {
                    if (string.Equals(_all[i].relPath, want, StringComparison.OrdinalIgnoreCase))
                    {
                        _index = i;
                        return;
                    }
                }
            }

            // Fallback to index
            _index = Mathf.Clamp(ProjectPrefs.GetInt(PrefKey_SynthPresetIndex, 0), 0, _all.Count - 1);
        }

        private void OnEnable()
        {
            if (doc == null) doc = GetComponent<UIDocument>();
            if (doc == null)
            {
                Debug.LogError("[KMusicHelmPresetPickerUI] No UIDocument found.");
                enabled = false;
                return;
            }

            if (helm == null) helm = FindObjectOfType<HelmController>();
            if (helm == null)
            {
                Debug.LogError("[KMusicHelmPresetPickerUI] No HelmController found.");
                enabled = false;
                return;
            }

            _binder = FindObjectOfType<KMusicHelmSynthBinder>();

            var root = doc.rootVisualElement;

            // Debug: confirm the elements exist
            Debug.Log("[KMusicHelmPresetPickerUI] UIDocument = " + doc.name);
            Debug.Log("[KMusicHelmPresetPickerUI] Has PatchPrevButton? " + (root.Q<VisualElement>(patchPrevBtnName) != null));
            Debug.Log("[KMusicHelmPresetPickerUI] Has PatchNextButton? " + (root.Q<VisualElement>(patchNextBtnName) != null));
            Debug.Log("[KMusicHelmPresetPickerUI] Has PatchNameLabel? " + (root.Q<VisualElement>(patchNameLabelName) != null));
            Debug.Log("[KMusicHelmPresetPickerUI] Has PatchIndexLabel? " + (root.Q<VisualElement>(patchIndexLabelName) != null));

            _prevBtn = root.Q<Button>(patchPrevBtnName);
            _nextBtn = root.Q<Button>(patchNextBtnName);
            _patchNameLabel = root.Q<Label>(patchNameLabelName);
            _indexLabel = root.Q<Label>(patchIndexLabelName);

            if (_prevBtn == null || _nextBtn == null || _patchNameLabel == null || _indexLabel == null)
            {
                Debug.LogError("[KMusicHelmPresetPickerUI] Missing UXML elements. Need: " +
                               $"{patchPrevBtnName}, {patchNextBtnName}, {patchNameLabelName}, {patchIndexLabelName}");
                enabled = false;
                return;
            }

            _prevBtn.clicked += OnPrev;
            _nextBtn.clicked += OnNext;

            // Optional: tap patch name to reload current patch
            _patchNameLabel.RegisterCallback<PointerDownEvent>(_ => OnReload());

            // Create runtime HelmPatch holder once
            var go = new GameObject("_RuntimeHelmPatch");
            go.hideFlags = HideFlags.HideAndDontSave;
            _runtimePatch = go.AddComponent<HelmPatch>();

            StartCoroutine(LoadIndexThenAutoLoadCurrent());

            _patchNameLabel.text = "PATCH NAME TEST";
            _patchNameLabel.style.color = Color.white;
        }

        /// <summary>
        /// Force-apply a preset choice (used by project load).
        /// If presets haven't loaded yet, the choice will be applied after presets_index.txt loads.
        /// </summary>
        public void ApplyPresetChoice(int index, string relPath)
        {
            _pendingPresetIndex = index;
            _pendingPresetRelPath = relPath;

            if (_all != null && _all.Count > 0)
                ApplyPendingPresetNow();
        }

        private void ApplyPendingPresetNow()
        {
            if (_all == null || _all.Count == 0) return;

            // Prefer relPath if provided.
            if (!string.IsNullOrEmpty(_pendingPresetRelPath))
            {
                for (int i = 0; i < _all.Count; i++)
                {
                    if (string.Equals(_all[i].relPath, _pendingPresetRelPath, StringComparison.OrdinalIgnoreCase))
                    {
                        _index = i;
                        _pendingPresetIndex = -1;
                        _pendingPresetRelPath = null;
                        UpdatePatchUI();
                        SaveCurrentPresetChoice();
                        StartCoroutine(LoadAndApplyPreset(_all[_index].relPath));
                        return;
                    }
                }
            }

            // Fallback to index.
            if (_pendingPresetIndex >= 0)
                _index = Mathf.Clamp(_pendingPresetIndex, 0, _all.Count - 1);

            _pendingPresetIndex = -1;
            _pendingPresetRelPath = null;
            UpdatePatchUI();
            SaveCurrentPresetChoice();
            StartCoroutine(LoadAndApplyPreset(_all[_index].relPath));
        }

        private void OnDisable()
        {
            if (_prevBtn != null) _prevBtn.clicked -= OnPrev;
            if (_nextBtn != null) _nextBtn.clicked -= OnNext;

            if (_patchNameLabel != null)
                _patchNameLabel.UnregisterCallback<PointerDownEvent>(_ => OnReload()); // harmless if already gone

            if (_runtimePatch != null)
                Destroy(_runtimePatch.gameObject);

            _all.Clear();
        }

        private IEnumerator LoadIndexThenAutoLoadCurrent()
        {
            _all.Clear();

            string rel = presetsRootFolder + "/" + indexFileName;

            string txt = null;
            yield return ReadStreamingText(rel, s => txt = s);

            if (string.IsNullOrEmpty(txt))
            {
                Debug.LogError("[KMusicHelmPresetPickerUI] Preset index empty/missing: " + StreamingAssetPath(rel));
                yield break;
            }

            var lines = txt.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var relLine in lines)
            {
                var clean = relLine.Trim().Replace("\\", "/");
                if (string.IsNullOrEmpty(clean)) continue;
                if (!clean.EndsWith(".helm", StringComparison.OrdinalIgnoreCase)) continue;

                _all.Add(new PresetEntry
                {
                    relPath = clean,
                    display = NiceNameFromPath(clean)
                });
            }

            if (_all.Count == 0)
            {
                Debug.LogWarning("[KMusicHelmPresetPickerUI] Index loaded but no .helm entries found.");
                UpdatePatchUI();
                yield break;
            }

            // ✅ NOW restore last saved preset
            RestorePresetChoice();

            // If a project load queued a preset, prefer that.
            if (_pendingPresetIndex >= 0 || !string.IsNullOrEmpty(_pendingPresetRelPath))
            {
                ApplyPendingPresetNow();
                yield break;
            }

            // keep index if component re-enabled
            _index = Mathf.Clamp(_index, 0, _all.Count - 1);

            UpdatePatchUI();

            // auto-load current
            yield return LoadAndApplyPreset(_all[_index].relPath);
        }

        private void OnPrev()
        {
            if (_all.Count == 0) return;
            _index = (_index - 1 + _all.Count) % _all.Count;
            UpdatePatchUI();
            SaveCurrentPresetChoice();
            StartCoroutine(LoadAndApplyPreset(_all[_index].relPath));
        }

        private void OnNext()
        {
            if (_all.Count == 0) return;
            _index = (_index + 1) % _all.Count;
            UpdatePatchUI();
            SaveCurrentPresetChoice();
            StartCoroutine(LoadAndApplyPreset(_all[_index].relPath));
        }

        private void OnReload()
        {
            if (_all.Count == 0) return;
            StartCoroutine(LoadAndApplyPreset(_all[_index].relPath));
        }

        private void UpdatePatchUI()
        {
            if (_patchNameLabel == null || _indexLabel == null) return;

            if (_all.Count == 0)
            {
                _patchNameLabel.text = "No Presets";
                _indexLabel.text = "- / -";
                return;
            }

            _patchNameLabel.text = _all[_index].display;
            _indexLabel.text = $"{_index + 1} / {_all.Count}";
        }

        private IEnumerator LoadAndApplyPreset(string relPath)
        {
            string rel = presetsRootFolder + "/" + relPath;

            string json = null;
            yield return ReadStreamingText(rel, s => json = s);

            if (string.IsNullOrEmpty(json))
            {
                Debug.LogError("[KMusicHelmPresetPickerUI] Failed to load preset: " + StreamingAssetPath(rel));
                yield break;
            }

            HelmPatchFormat fmt = null;
            try
            {
                fmt = JsonUtility.FromJson<HelmPatchFormat>(json);
            }
            catch (Exception e)
            {
                Debug.LogError("[KMusicHelmPresetPickerUI] JSON parse failed for: " + relPath + "\n" + e);
                yield break;
            }

            if (fmt == null || fmt.settings == null)
            {
                Debug.LogError("[KMusicHelmPresetPickerUI] Parsed preset invalid: " + relPath);
                yield break;
            }

            _runtimePatch.patchData = fmt;
            helm.LoadPatch(_runtimePatch);

            // Let Helm finish internal patch application before we mirror values back into the bus.
            yield return null;
            yield return null;

            // Optional: if binder exposes RequestPullFromHelm(), call it so UI syncs to loaded patch.
            if (_binder != null)
            {
                var mi = _binder.GetType().GetMethod("RequestPullFromHelm");
                if (mi != null) mi.Invoke(_binder, null);
            }

            // Fire after the patch has had a couple of frames to settle and the bus has been synced.
            PresetApplied?.Invoke(_index, relPath);
        }

        // ---- StreamingAssets reader (UnityWebRequest on Android, direct file IO elsewhere) ----
        private static IEnumerator ReadStreamingText(string relativePath, Action<string> onDone)
        {
        #if UNITY_ANDROID && !UNITY_EDITOR
            // On Android, StreamingAssets are inside the APK; use UnityWebRequest to read them.
            string url = StreamingAssetPath(relativePath);

            using (UnityWebRequest req = UnityWebRequest.Get(url))
            {
                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError("[KMusicHelmPresetPickerUI] UWR error: " + req.error + "\n" + url);
                    onDone(null);
                }
                else
                {
                    onDone(req.downloadHandler.text);
                }
            }
        #else
            // On desktop/editor, StreamingAssets are normal files on disk.
            string path = StreamingAssetPath(relativePath);

            if (!File.Exists(path))
            {
                Debug.LogError("[KMusicHelmPresetPickerUI] Missing file: " + path);
                onDone(null);
                yield break;
            }

            onDone(File.ReadAllText(path));
            yield break;
        #endif
        }
        private static string NiceNameFromPath(string relPath)
        {
            relPath = relPath.Replace("\\", "/");
            string noExt = relPath.EndsWith(".helm", StringComparison.OrdinalIgnoreCase)
                ? relPath.Substring(0, relPath.Length - 5)
                : relPath;

            return noExt.Replace("/", " / ");
        }

        private static string StreamingAssetPath(string relativePath)
        {
            relativePath = relativePath.TrimStart('/').Replace("\\", "/");
#if UNITY_ANDROID && !UNITY_EDITOR
            return "jar:file://" + Application.dataPath + "!/assets/" + relativePath;
#else
            return Path.Combine(Application.streamingAssetsPath, relativePath);
#endif
        }
    }
}
