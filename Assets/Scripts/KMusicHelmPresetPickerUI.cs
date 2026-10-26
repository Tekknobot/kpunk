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
        [SerializeField] private string excludedRootName = "PadSynth";

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
        public HelmController ActiveHelmController => helm;
        public string LastAppliedPresetRelPath { get; private set; }
        public int CurrentPresetIndex => _index;
        public int SynthPresetCount => _all.Count;

        public string CurrentPresetRelPath
        {
            get
            {
                if (!string.IsNullOrEmpty(_legacyPresetRelPath)) return _legacyPresetRelPath;
                if (_all != null && _all.Count > 0 && _index >= 0 && _index < _all.Count)
                    return _all[_index].relPath;
                return ProjectPrefs.GetString(PrefKey_SynthPresetRelPath, "");
            }
        }

        private int _pendingPresetIndex = -1;
        private string _pendingPresetRelPath = null;
        private int _loadRevision; // latest selection wins over in-flight disk/APK reads
        private string _legacyPresetRelPath; // Old Lead/Keys sound used by an existing project; not in curated browser.

        private static bool IsLegacySynthPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Contains("..") || path.Contains("\\") || path.StartsWith("/")) return false;
            return path.EndsWith(".helm", StringComparison.OrdinalIgnoreCase) &&
                (path.StartsWith("Lead/", StringComparison.OrdinalIgnoreCase) ||
                 path.StartsWith("Keys/", StringComparison.OrdinalIgnoreCase));
        }

        private void SaveCurrentPresetChoice()
        {
            if (_all == null || _all.Count == 0) return;

            ProjectPrefs.SetInt(PrefKey_SynthPresetIndex, _index);
            ProjectPrefs.SetString(PrefKey_SynthPresetRelPath, !string.IsNullOrEmpty(_legacyPresetRelPath)
                ? _legacyPresetRelPath : (_all[_index].relPath ?? ""));
            ProjectPrefs.Save();
        }

        private void RestorePresetChoice()
        {
            if (_all == null || _all.Count == 0) return;

            // Prefer relPath (stable if index file changes order)
            string want = ProjectPrefs.GetString(PrefKey_SynthPresetRelPath, "");
            if (IsLegacySynthPath(want))
            {
                _legacyPresetRelPath = want;
                return;
            }
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

            // A saved path that no longer exists must NEVER be translated through
            // an old numeric index: the curated bank can have a different order.
            _index = !string.IsNullOrEmpty(want) ? 0 :
                Mathf.Clamp(ProjectPrefs.GetInt(PrefKey_SynthPresetIndex, 0), 0, _all.Count - 1);
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

            ResolveMainSynthHelm();
            if (helm == null)
            {
                Debug.LogError("[KMusicHelmPresetPickerUI] No main HelmController found.");
                enabled = false;
                return;
            }

            ResolveMainSynthBinder();

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
                // Projects using older Lead/Keys presets retain the original sound.
                // Bass/Pad paths are intentionally not permitted for the SYNTH engine.
                if (IsLegacySynthPath(_pendingPresetRelPath))
                {
                    _legacyPresetRelPath = _pendingPresetRelPath;
                    _pendingPresetIndex = -1;
                    _pendingPresetRelPath = null;
                    UpdatePatchUI();
                    SaveCurrentPresetChoice();
                    StartCoroutine(LoadAndApplyPreset(_legacyPresetRelPath));
                    return;
                }
                for (int i = 0; i < _all.Count; i++)
                {
                    if (string.Equals(_all[i].relPath, _pendingPresetRelPath, StringComparison.OrdinalIgnoreCase))
                    {
                        _index = i;
                        _legacyPresetRelPath = null;
                        _pendingPresetIndex = -1;
                        _pendingPresetRelPath = null;
                        UpdatePatchUI();
                        SaveCurrentPresetChoice();
                        StartCoroutine(LoadAndApplyPreset(_all[_index].relPath));
                        return;
                    }
                }
            }

            // An old path missing from this bank must not accidentally select
            // an unrelated patch with the same numeric index.
            if (!string.IsNullOrEmpty(_pendingPresetRelPath))
            {
                Debug.LogWarning("[ZPUNK SYNTH] Saved patch unavailable: " + _pendingPresetRelPath +
                                 "; selecting first loadable SYNTH patch.");
                _index = 0;
            }
            else if (_pendingPresetIndex >= 0)
                _index = Mathf.Clamp(_pendingPresetIndex, 0, _all.Count - 1);

            _pendingPresetIndex = -1;
            _pendingPresetRelPath = null;
            _legacyPresetRelPath = null;
            UpdatePatchUI();
            SaveCurrentPresetChoice();
            StartCoroutine(LoadAndApplyPreset(_all[_index].relPath));
        }

        private void OnDisable()
        {
            ++_loadRevision;
            LastAppliedPresetRelPath = null;
            if (_prevBtn != null) _prevBtn.clicked -= OnPrev;
            if (_nextBtn != null) _nextBtn.clicked -= OnNext;

            if (_patchNameLabel != null)
                _patchNameLabel.UnregisterCallback<PointerDownEvent>(_ => OnReload()); // harmless if already gone

            if (_runtimePatch != null)
                Destroy(_runtimePatch.gameObject);

            _all.Clear();
        }


        private void ResolveMainSynthBinder()
        {
            var excludedRoot = FindSceneObjectByName(excludedRootName);
#if UNITY_2023_1_OR_NEWER
            var all = FindObjectsByType<KMusicHelmSynthBinder>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
            var all = Resources.FindObjectsOfTypeAll<KMusicHelmSynthBinder>();
#endif
            KMusicHelmSynthBinder fallback = null;
            foreach (var b in all)
            {
                if (b == null || !b.gameObject.scene.IsValid()) continue;
                if (IsInHierarchy(b.gameObject, excludedRoot)) continue;
                if (b.gameObject == gameObject)
                {
                    _binder = b;
                    return;
                }
                if (BinderTargetsHelm(b, helm))
                {
                    _binder = b;
                    return;
                }
                if (fallback == null) fallback = b;
            }
            _binder = fallback;
        }

        private static bool BinderTargetsHelm(KMusicHelmSynthBinder binder, HelmController target)
        {
            if (binder == null || target == null) return false;
            var field = binder.GetType().GetField("helm", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (field == null || field.FieldType != typeof(HelmController)) return false;
            return ReferenceEquals(field.GetValue(binder) as HelmController, target);
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
                // Dedicated curated SYNTH bank only. The shared index still lists
                // Bass/ and Pad/ for their independent synth engines.
                if (!clean.StartsWith("Synth/", StringComparison.OrdinalIgnoreCase)) continue;

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
            yield return LoadAndApplyPreset(!string.IsNullOrEmpty(_legacyPresetRelPath)
                ? _legacyPresetRelPath : _all[_index].relPath);
        }

        private void OnPrev()
        {
            if (_all.Count == 0) return;
            _legacyPresetRelPath = null;
            _index = (_index - 1 + _all.Count) % _all.Count;
            UpdatePatchUI();
            SaveCurrentPresetChoice();
            StartCoroutine(LoadAndApplyPreset(_all[_index].relPath));
        }

        private void OnNext()
        {
            if (_all.Count == 0) return;
            _legacyPresetRelPath = null;
            _index = (_index + 1) % _all.Count;
            UpdatePatchUI();
            SaveCurrentPresetChoice();
            StartCoroutine(LoadAndApplyPreset(_all[_index].relPath));
        }

        private void OnReload()
        {
            if (_all.Count == 0) return;
            StartCoroutine(LoadAndApplyPreset(!string.IsNullOrEmpty(_legacyPresetRelPath)
                ? _legacyPresetRelPath : _all[_index].relPath));
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

            if (!string.IsNullOrEmpty(_legacyPresetRelPath))
            {
                _patchNameLabel.text = NiceNameFromPath(_legacyPresetRelPath) + " (Legacy)";
                _indexLabel.text = "LEGACY";
            }
            else
            {
                _patchNameLabel.text = _all[_index].display;
                _indexLabel.text = $"{_index + 1} / {_all.Count}";
            }
        }

        private IEnumerator LoadAndApplyPreset(string relPath)
        {
            int revision = ++_loadRevision;
            ResolveMainSynthHelm();
            if (helm == null || _runtimePatch == null)
                yield break;

            // Load the requested patch, if it still exists; otherwise try the bank
            // from index zero. Index entries alone do not guarantee .helm files.
            HelmPatchFormat fmt = null;
            string resolvedPath = null;
            int resolvedIndex = -1;
            for (int attempt = -1; attempt < _all.Count; attempt++)
            {
                string candidate = attempt < 0 ? relPath : _all[attempt].relPath;
                if (string.IsNullOrEmpty(candidate) ||
                    (attempt >= 0 && string.Equals(candidate, relPath, StringComparison.OrdinalIgnoreCase)))
                    continue;
                string json = null;
                yield return ReadStreamingText(presetsRootFolder + "/" + candidate, s => json = s);
                if (revision != _loadRevision) yield break;
                if (string.IsNullOrEmpty(json)) continue;
                HelmPatchFormat parsed = null;
                try { parsed = JsonUtility.FromJson<HelmPatchFormat>(json); }
                catch (Exception e) { Debug.LogWarning("[ZPUNK SYNTH] Invalid preset " + candidate + ": " + e.Message); }
                if (parsed == null || parsed.settings == null) continue;
                fmt = parsed;
                resolvedPath = candidate;
                resolvedIndex = attempt >= 0 ? attempt :
                    _all.FindIndex(x => string.Equals(x.relPath, candidate, StringComparison.OrdinalIgnoreCase));
                break;
            }
            if (fmt == null)
            {
                Debug.LogError("[ZPUNK SYNTH] No loadable SYNTH presets found in " + presetsRootFolder);
                yield break;
            }
            if (!string.Equals(resolvedPath, relPath, StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogWarning("[ZPUNK SYNTH] Missing/invalid patch '" + relPath +
                                 "'; using '" + resolvedPath + "'.");
                _legacyPresetRelPath = null;
                _index = resolvedIndex;
                UpdatePatchUI();
                SaveCurrentPresetChoice();
            }

            // Startup can complete before Audio Helm's native output and the
            // scene's controllers have finished their first frame. Load only
            // after that initialization and discard superseded requests.
            yield return null;
            yield return null;
            if (revision != _loadRevision) yield break;
            _runtimePatch.patchData = fmt;
            // Track the actual applied preset, not the UI's selected index.
            // This prevents an in-flight preset change from being saved as if
            // it were already the sound currently running in native Helm.
            LastAppliedPresetRelPath = null;
            // A newly selected factory patch invalidates edits from the old one.
            ZPunk.DAW.ZPunkSoundUI.ClearLiveEdits(helm);
            helm.LoadPatch(_runtimePatch);

            // Let Helm finish internal patch application before we mirror values back into the bus.
            yield return null;
            yield return null;

            if (revision != _loadRevision) yield break;
            LastAppliedPresetRelPath = resolvedPath;
            // Replace stale bus values with the newly loaded patch's actual
            // parameters before project-specific overrides are restored.
            ResolveMainSynthBinder();
            if (_binder != null && BinderTargetsHelm(_binder, helm))
                _binder.RequestPullFromHelm();

            // Fire after the patch has had a couple of frames to settle and the bus has been synced.
            PresetApplied?.Invoke(_index, resolvedPath);
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

            return noExt.StartsWith("Synth/", StringComparison.OrdinalIgnoreCase)
                ? noExt.Substring(6) : noExt.Replace("/", " / ");
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

        private void ResolveMainSynthHelm()
        {
            var excludedRoot = FindSceneObjectByName(excludedRootName);

            if (helm != null && !IsInHierarchy(helm.gameObject, excludedRoot))
                return;

            HelmSequencer seq = FindFirstSceneComponentOutsideRoot<HelmSequencer>(excludedRoot);
            HelmController linked = FindLinkedController(seq);
            helm = linked != null && !IsInHierarchy(linked.gameObject, excludedRoot)
                ? linked
                : FindFirstSceneComponentOutsideRoot<HelmController>(excludedRoot);
        }

        private static HelmController FindLinkedController(HelmSequencer seq)
        {
            if (seq == null) return null;
            var field = seq.GetType().GetField("helmController", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (field != null && field.FieldType == typeof(HelmController))
                return field.GetValue(seq) as HelmController;
            return null;
        }

        private static T FindFirstSceneComponentOutsideRoot<T>(GameObject excludedRoot) where T : Component
        {
#if UNITY_2023_1_OR_NEWER
            var all = FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
            var all = Resources.FindObjectsOfTypeAll<T>();
#endif
            foreach (var c in all)
            {
                if (c == null || !c.gameObject.scene.IsValid()) continue;
                if (IsInHierarchy(c.gameObject, excludedRoot)) continue;
                return c;
            }
            return null;
        }

        private static bool IsInHierarchy(GameObject go, GameObject root)
        {
            if (go == null) return false;
            // Bass is a new independent native synth, never the legacy MAIN synth.
            // All historical main-synth lookups exclude it, including preset loading.
            if (go.transform.root != null && go.transform.root.name == "ZPunkBassSynth") return true;
            if (root == null) return false;
            var tr = go.transform;
            while (tr != null)
            {
                if (tr.gameObject == root) return true;
                tr = tr.parent;
            }
            return false;
        }

        private static GameObject FindSceneObjectByName(string targetName)
        {
            if (string.IsNullOrWhiteSpace(targetName)) return null;
#if UNITY_2023_1_OR_NEWER
            var all = FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
            var all = Resources.FindObjectsOfTypeAll<Transform>();
#endif
            GameObject partial = null;
            foreach (var tr in all)
            {
                if (tr == null || !tr.gameObject.scene.IsValid()) continue;
                if (string.Equals(tr.name, targetName, StringComparison.OrdinalIgnoreCase))
                    return tr.gameObject;
                if (partial == null && tr.name.IndexOf(targetName, StringComparison.OrdinalIgnoreCase) >= 0)
                    partial = tr.gameObject;
            }
            return partial;
        }

    }
}
