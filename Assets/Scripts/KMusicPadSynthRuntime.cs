using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;
using AudioHelm;
using KMusic.Core;
using KMusic.UI;

namespace KMusic
{
    [DefaultExecutionOrder(105)]
    [RequireComponent(typeof(UIDocument))]
    public sealed class KMusicPadSynthRuntime : MonoBehaviour
    {
        [Header("UI")]
        [SerializeField] private UIDocument doc;
        [SerializeField] private string synthPageName = "PageSynth2";
        [SerializeField] private string padPageName = "PagePad";

        [Header("Pad preset picker UXML names")]
        [SerializeField] private string patchPrevBtnName = "PadPatchPrevButton";
        [SerializeField] private string patchNextBtnName = "PadPatchNextButton";
        [SerializeField] private string patchNameLabelName = "PadPatchNameLabel";
        [SerializeField] private string patchIndexLabelName = "PadPatchIndexLabel";

        [Header("Pad synth target")]
        [SerializeField] private string padSynthRootName = "PadSynth";
        [SerializeField] private HelmController helm;
        [SerializeField] private HelmSequencer helmSequencer;

        [Header("StreamingAssets")]
        [SerializeField] private string presetsRootFolder = "HelmPresets";
        [SerializeField] private string indexFileName = "presets_index.txt";

        [Header("Synth II toggle names")]
        [SerializeField] private string distToggleButtonName = "DistToggleButton2";
        [SerializeField] private string delayToggleButtonName = "DelayToggleButton2";
        [SerializeField] private string reverbToggleButtonName = "ReverbToggleButton2";

        [Header("Spam guard")]
        [SerializeField] private float epsilon = 0.0005f;

        private const string PrefKey_PadPresetRelPath = "kmusic.pad.preset.relpath.v1";
        private const string PrefKey_PadPresetIndex = "kmusic.pad.preset.index.v1";

        private UIDocument _doc;
        private VisualElement _synthPage;
        private ParameterBus _bus;
        private bool _suppressBusEvents;
        private readonly Dictionary<string, float> _lastSent = new();
        private Coroutine _co;

        private Button _distToggleButton;
        private Button _delayToggleButton;
        private Button _reverbToggleButton;

        private Button _prevBtn;
        private Button _nextBtn;
        private Label _patchNameLabel;
        private Label _indexLabel;
        private readonly List<PresetEntry> _all = new();
        private int _index;
        private HelmPatch _runtimePatch;

        private class PresetEntry
        {
            public string relPath;
            public string display;
        }

        private static readonly Dictionary<string, Param> Map = new Dictionary<string, Param>
        {
            { "master.vol",   Param.kVolume },
            { "master.porta", Param.kPortamento },
            { "master.poly",  Param.kPolyphony },

            { "osc1.vol",    Param.kOsc1Volume },
            { "osc1.tune",   Param.kOsc1Tune },
            { "osc1.trans",  Param.kOsc1Transpose },
            { "osc1.uni",    Param.kOsc1UnisonVoices },
            { "osc1.detune", Param.kOsc1UnisonDetune },
            { "osc1.harm",   Param.kOsc1UnisonHarmonize },
            { "osc1.wave",   Param.kOsc1Waveform },

            { "osc2.vol",    Param.kOsc2Volume },
            { "osc2.tune",   Param.kOsc2Tune },
            { "osc2.trans",  Param.kOsc2Transpose },
            { "osc2.uni",    Param.kOsc2UnisonVoices },
            { "osc2.detune", Param.kOsc2UnisonDetune },
            { "osc2.harm",   Param.kOsc2UnisonHarmonize },
            { "osc2.wave",   Param.kOsc2Waveform },

            { "sub.vol",       Param.kSubVolume },
            { "sub.oct",       Param.kSubOctave },
            { "noise.vol",     Param.kNoiseVolume },
            { "synth.xmod",    Param.kCrossMod },
            { "filter.keytrk", Param.kKeytrack },
            { "synth.vel",     Param.kVelocityTrack },

            { "filter.cutoff", Param.kFilterCutoff },
            { "filter.res",    Param.kResonance },
            { "filter.drive",  Param.kFilterDrive },
            { "filter.atk",    Param.kFilterAttack },
            { "filter.dec",    Param.kFilterDecay },
            { "filter.sus",    Param.kFilterSustain },
            { "filter.rel",    Param.kFilterRelease },
            { "filter.env",    Param.kFilterEnvelopeDepth },

            { "amp.atk",  Param.kAmplitudeAttack },
            { "amp.dec",  Param.kAmplitudeDecay },
            { "amp.sus",  Param.kAmplitudeSustain },
            { "amp.rel2", Param.kAmplitudeRelease },

            { "mod.rate",  Param.kMonoLfo1Frequency },
            { "mod.depth", Param.kMonoLfo1Amplitude },
            { "mod.atk",   Param.kModAttack },
            { "mod.dec",   Param.kModDecay },
            { "mod.sus",   Param.kModSustain },
            { "mod.rel",   Param.kModRelease },

            { "fx.delay.mix",  Param.kDelayDryWet },
            { "fx.delay.fb",   Param.kDelayFeedback },
            { "fx.delay.time", Param.kDelayTempo },

            { "fx.rev.mix",  Param.kReverbDryWet },
            { "fx.rev.fb",   Param.kReverbFeedback },
            { "fx.rev.damp", Param.kReverbDamping },

            { "fx.dist.on",    Param.kDistortionOn },
            { "fx.dist.type",  Param.kDistortionType },
            { "fx.dist.drive", Param.kDistortionDrive },
            { "fx.dist.mix",   Param.kDistortionMix },
        };

        private static T FindFirstSceneComponentOutsideRoot<T>(GameObject excludedRoot) where T : Component
        {
        #if UNITY_2023_1_OR_NEWER
            var all = FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        #else
            var all = Resources.FindObjectsOfTypeAll<T>();
        #endif
            foreach (var c in all)
            {
                if (c == null) continue;

                var go = c.gameObject;
                if (go == null || !go.scene.IsValid()) continue;

                if (excludedRoot != null)
                {
                    if (go == excludedRoot) continue;
                    if (go.transform.IsChildOf(excludedRoot.transform)) continue;
                }

                return c;
            }

            return null;
        }

        private void OnEnable()
        {
            if (doc == null) doc = GetComponent<UIDocument>();
            if (_co != null) StopCoroutine(_co);
            _co = StartCoroutine(Bootstrap());
        }

        private void OnDisable()
        {
            if (_co != null) StopCoroutine(_co);
            _co = null;

            if (_bus != null)
                _bus.OnChanged -= OnBusChanged;

            UnwireToggleButtons();

            if (_prevBtn != null) _prevBtn.clicked -= OnPrev;
            if (_nextBtn != null) _nextBtn.clicked -= OnNext;

            _prevBtn = null;
            _nextBtn = null;
            _patchNameLabel = null;
            _indexLabel = null;

            if (_runtimePatch != null)
                Destroy(_runtimePatch.gameObject);
            _runtimePatch = null;

            _all.Clear();
            _lastSent.Clear();
            _bus = null;
            _suppressBusEvents = false;
        }

        private IEnumerator Bootstrap()
        {
            _doc = doc != null ? doc : GetComponent<UIDocument>();
            if (_doc == null)
                yield break;

            while (_doc.rootVisualElement == null)
                yield return null;

            yield return ResolvePadSynthRefs();

            var root = _doc.rootVisualElement;
            if (root == null)
                yield break;

            _synthPage = root.Q<VisualElement>(synthPageName);
            if (_synthPage != null)
            {
                _bus = BuildSynthBus();
                BindAll(_synthPage, _bus);
                _bus.OnChanged -= OnBusChanged;
                _bus.OnChanged += OnBusChanged;
                WireToggleButtons(_synthPage);
                PullHelmToBus();
                RefreshToggleButtons();
            }

            var padPage = root.Q<VisualElement>(padPageName);
            if (padPage != null)
            {
                _prevBtn = padPage.Q<Button>(patchPrevBtnName);
                _nextBtn = padPage.Q<Button>(patchNextBtnName);
                _patchNameLabel = padPage.Q<Label>(patchNameLabelName);
                _indexLabel = padPage.Q<Label>(patchIndexLabelName);

                if (_prevBtn != null) _prevBtn.clicked += OnPrev;
                if (_nextBtn != null) _nextBtn.clicked += OnNext;

                var go = new GameObject("_RuntimePadSynthPatch");
                go.hideFlags = HideFlags.HideAndDontSave;
                _runtimePatch = go.AddComponent<HelmPatch>();

                yield return LoadIndexThenAutoLoadCurrent();
            }
        }

        private IEnumerator ResolvePadSynthRefs()
        {
            if (helm != null && helmSequencer != null)
            {
                ConfigureSequencer();
                yield break;
            }

            // Give late-created scene objects a moment to appear.
            const float timeout = 3f;
            float t0 = Time.realtimeSinceStartup;

            while (Time.realtimeSinceStartup - t0 < timeout)
            {
                var root = FindSceneObjectByName(padSynthRootName);
                if (root != null)
                {
                    helmSequencer = root.GetComponentInChildren<HelmSequencer>(true);
                    helm = FindLinkedController(helmSequencer) ?? root.GetComponentInChildren<HelmController>(true);

                    if (helm != null || helmSequencer != null)
                    {
                        ConfigureSequencer();
                        yield break;
                    }
                }

                yield return null;
            }

            // Do not fall back to arbitrary Helm objects here.
            // Pad runtime must stay locked to the PadSynth hierarchy only.
            ConfigureSequencer();
        }

        private static HelmController FindLinkedController(HelmSequencer seq)
        {
            if (seq == null) return null;
            var field = seq.GetType().GetField("helmController", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (field != null && field.FieldType == typeof(HelmController))
                return field.GetValue(seq) as HelmController;
            return null;
        }

        private void EnsureIndependentPadChannel()
        {
            if (helm == null) return;

            var excludedRoot = FindSceneObjectByName(padSynthRootName);
            var mainHelm = FindFirstSceneComponentOutsideRoot<HelmController>(excludedRoot);
            if (mainHelm == null || ReferenceEquals(mainHelm, helm)) return;

            int padChannel = GetControllerChannel(helm);
            int mainChannel = GetControllerChannel(mainHelm);
            if (padChannel != mainChannel) return;

            int newChannel = FindUnusedChannel(mainChannel, helm, excludedRoot);
            if (newChannel == padChannel) return;

            SetControllerChannel(helm, newChannel);
            if (helmSequencer != null)
                helmSequencer.channel = newChannel;

            Debug.Log($"[KMusicPadSynthRuntime] PadSynth channel was shared with main synth ({mainChannel}). Reassigned PadSynth to channel {newChannel}.");
        }

        private static int FindUnusedChannel(int avoidChannel, HelmController preferred, GameObject excludedRoot)
        {
#if UNITY_2023_1_OR_NEWER
            var all = FindObjectsByType<HelmController>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
            var all = Resources.FindObjectsOfTypeAll<HelmController>();
#endif
            var used = new HashSet<int>();
            foreach (var h in all)
            {
                if (h == null || !h.gameObject.scene.IsValid()) continue;
                if (ReferenceEquals(h, preferred)) continue;
                used.Add(GetControllerChannel(h));
            }

            for (int ch = 0; ch < 16; ch++)
            {
                if (ch == avoidChannel) continue;
                if (!used.Contains(ch)) return ch;
            }

            int fallback = (avoidChannel + 1) % 16;
            if (fallback == avoidChannel) fallback = (fallback + 1) % 16;
            return fallback;
        }

        private static int GetControllerChannel(HelmController target)
        {
            if (target == null) return 0;
            var type = target.GetType();
            var prop = type.GetProperty("channel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null && prop.PropertyType == typeof(int) && prop.CanRead)
                return (int)prop.GetValue(target, null);
            var field = type.GetField("channel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null && field.FieldType == typeof(int))
                return (int)field.GetValue(target);
            return 0;
        }

        private static void SetControllerChannel(HelmController target, int channel)
        {
            if (target == null) return;
            var type = target.GetType();
            var prop = type.GetProperty("channel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null && prop.PropertyType == typeof(int) && prop.CanWrite)
            {
                prop.SetValue(target, channel, null);
                return;
            }
            var field = type.GetField("channel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null && field.FieldType == typeof(int))
                field.SetValue(target, channel);
        }

        private void ConfigureSequencer()
        {
            EnsureIndependentPadChannel();

            if (helm != null && helmSequencer != null)
            {
                helmSequencer.channel = helm.channel;
                helmSequencer.length = 16;
                helmSequencer.loop = true;
                helmSequencer.division = Sequencer.Division.kSixteenth;
                TryAssignHelmController(helmSequencer, helm);
            }
        }

        private void OnBusChanged(string id, float _)
        {
            if (_bus == null || helm == null || _suppressBusEvents)
                return;

            float n = GetBusNormalizedSafe(id);
            float snapped = n;

            if (id == "osc1.wave" || id == "osc2.wave") snapped = SnapWaveform(n);
            else if (id == "fx.dist.type") snapped = SnapDistType(n);

            if (!Mathf.Approximately(snapped, n))
            {
                _suppressBusEvents = true;
                try { _bus.SetNormalized(id, snapped); }
                finally { _suppressBusEvents = false; }
            }

            ApplyOne(id);
            RefreshToggleButtons();
        }

        private void ApplyOne(string id)
        {
            if (_bus == null || helm == null || string.IsNullOrEmpty(id)) return;

            if (id == "fx.delay.on" || id == "fx.delay.mix")
            {
                ApplyPseudoBypass("fx.delay.mix", Param.kDelayDryWet, IsEffectEnabled("fx.delay.on"));
                return;
            }

            if (id == "fx.rev.on" || id == "fx.rev.mix")
            {
                ApplyPseudoBypass("fx.rev.mix", Param.kReverbDryWet, IsEffectEnabled("fx.rev.on"));
                return;
            }

            if (id == "fx.dist.on")
            {
                ApplyMappedParameter("fx.dist.on", Param.kDistortionOn, GetBusNormalizedSafe("fx.dist.on"));
                ApplyPseudoBypass("fx.dist.mix", Param.kDistortionMix, IsEffectEnabled("fx.dist.on"));
                return;
            }

            if (id == "fx.dist.mix")
            {
                ApplyPseudoBypass("fx.dist.mix", Param.kDistortionMix, IsEffectEnabled("fx.dist.on"));
                return;
            }

            if (!Map.TryGetValue(id, out var param))
                return;

            ApplyMappedParameter(id, param, Mathf.Clamp01(GetBusNormalizedSafe(id)));
        }

        private void ApplyMappedParameter(string id, Param param, float t)
        {
            t = Mathf.Clamp01(t);
            if (!ShouldSend(id, t)) return;
            helm.SetParameterPercent(param, t);
        }

        private void ApplyPseudoBypass(string sendId, Param param, bool enabled)
        {
            float t = enabled ? Mathf.Clamp01(GetBusNormalizedSafe(sendId)) : 0f;
            ApplyMappedParameter(sendId, param, t);
        }

        private void PullHelmToBus()
        {
            if (_bus == null || helm == null) return;

            _suppressBusEvents = true;
            try
            {
                foreach (var kvp in Map)
                {
                    if (!_bus.TryGet(kvp.Key, out _))
                        continue;

                    _bus.SetNormalized(kvp.Key, GetHelmPercentSafe(kvp.Value));
                }

                if (_bus.TryGet("fx.delay.on", out _)) _bus.SetNormalized("fx.delay.on", GetHelmPercentSafe(Param.kDelayDryWet) > 0.001f ? 1f : 0f);
                if (_bus.TryGet("fx.rev.on", out _)) _bus.SetNormalized("fx.rev.on", GetHelmPercentSafe(Param.kReverbDryWet) > 0.001f ? 1f : 0f);
                if (_bus.TryGet("fx.dist.on", out _)) _bus.SetNormalized("fx.dist.on", GetHelmPercentSafe(Param.kDistortionOn) >= 0.5f ? 1f : 0f);
            }
            finally
            {
                _suppressBusEvents = false;
            }

            RefreshToggleButtons();
            _lastSent.Clear();
        }

        private float GetHelmPercentSafe(Param p)
        {
            var mi = typeof(HelmController).GetMethod("GetParameterPercent", new[] { typeof(Param) });
            if (mi != null)
            {
                object v = mi.Invoke(helm, new object[] { p });
                if (v is float f) return Mathf.Clamp01(f);
            }

            var mi2 = typeof(HelmController).GetMethod("GetParameterValue", new[] { typeof(int) });
            if (mi2 != null)
            {
                object v = mi2.Invoke(helm, new object[] { (int)p });
                if (v is float f) return Mathf.Clamp01(f);
            }

            return 0f;
        }

        private float GetBusNormalizedSafe(string id)
        {
            if (_bus == null) return 0f;
            return Mathf.Clamp01(_bus.GetNormalized(id));
        }

        private bool IsEffectEnabled(string id)
        {
            return GetBusNormalizedSafe(id) >= 0.5f;
        }

        private void WireToggleButtons(VisualElement root)
        {
            UnwireToggleButtons();
            if (root == null) return;

            _distToggleButton = root.Q<Button>(distToggleButtonName);
            _delayToggleButton = root.Q<Button>(delayToggleButtonName);
            _reverbToggleButton = root.Q<Button>(reverbToggleButtonName);

            RegisterToggleButton(_distToggleButton, "fx.dist.on");
            RegisterToggleButton(_delayToggleButton, "fx.delay.on");
            RegisterToggleButton(_reverbToggleButton, "fx.rev.on");
        }

        private void UnwireToggleButtons()
        {
            UnregisterToggleButton(_distToggleButton, "fx.dist.on");
            UnregisterToggleButton(_delayToggleButton, "fx.delay.on");
            UnregisterToggleButton(_reverbToggleButton, "fx.rev.on");

            _distToggleButton = null;
            _delayToggleButton = null;
            _reverbToggleButton = null;
        }

        private void RegisterToggleButton(Button button, string paramId)
        {
            if (button == null || string.IsNullOrEmpty(paramId)) return;
            button.userData = paramId;
            button.RegisterCallback<PointerDownEvent>(OnEffectTogglePointerDown, TrickleDown.TrickleDown);
        }

        private void UnregisterToggleButton(Button button, string paramId)
        {
            if (button == null) return;
            if (button.userData is string current && current == paramId)
                button.userData = null;
            button.UnregisterCallback<PointerDownEvent>(OnEffectTogglePointerDown, TrickleDown.TrickleDown);
        }

        private void OnEffectTogglePointerDown(PointerDownEvent evt)
        {
            if (_bus == null) return;
            if (evt.currentTarget is not Button button) return;
            if (button.userData is not string paramId || string.IsNullOrEmpty(paramId)) return;

            _bus.SetNormalized(paramId, IsEffectEnabled(paramId) ? 0f : 1f);
            RefreshToggleButtons();
            evt.StopPropagation();
        }

        private void RefreshToggleButtons()
        {
            RefreshToggleButton(_distToggleButton, IsEffectEnabled("fx.dist.on"));
            RefreshToggleButton(_delayToggleButton, IsEffectEnabled("fx.delay.on"));
            RefreshToggleButton(_reverbToggleButton, IsEffectEnabled("fx.rev.on"));
        }

        private static void RefreshToggleButton(Button button, bool isOn)
        {
            if (button == null) return;
            button.text = isOn ? "ON" : "OFF";
            if (isOn) button.AddToClassList("is-on");
            else button.RemoveFromClassList("is-on");
        }

        private static float SnapWaveform(float t)
        {
            const int waveCount = 11;
            int index = Mathf.RoundToInt(Mathf.Clamp01(t) * (waveCount - 1));
            index = Mathf.Clamp(index, 0, waveCount - 1);
            return index / (float)(waveCount - 1);
        }

        private static float SnapDistType(float t)
        {
            const int typeCount = 4;
            int index = Mathf.RoundToInt(Mathf.Clamp01(t) * (typeCount - 1));
            index = Mathf.Clamp(index, 0, typeCount - 1);
            return index / (float)(typeCount - 1);
        }

        private bool ShouldSend(string id, float t)
        {
            if (_lastSent.TryGetValue(id, out float prev))
            {
                if (Mathf.Abs(prev - t) < epsilon) return false;
                _lastSent[id] = t;
                return true;
            }

            _lastSent[id] = t;
            return true;
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

        private void SaveCurrentPresetChoice()
        {
            if (_all.Count == 0) return;
            ProjectPrefs.SetInt(PrefKey_PadPresetIndex, _index);
            ProjectPrefs.SetString(PrefKey_PadPresetRelPath, _all[_index].relPath ?? "");
            ProjectPrefs.Save();
        }

        private void RestorePresetChoice()
        {
            if (_all.Count == 0) return;

            string want = ProjectPrefs.GetString(PrefKey_PadPresetRelPath, "");
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

            _index = Mathf.Clamp(ProjectPrefs.GetInt(PrefKey_PadPresetIndex, 0), 0, _all.Count - 1);
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

        private IEnumerator LoadIndexThenAutoLoadCurrent()
        {
            _all.Clear();
            string rel = presetsRootFolder + "/" + indexFileName;
            string txt = null;
            yield return ReadStreamingText(rel, s => txt = s);

            if (string.IsNullOrEmpty(txt))
            {
                UpdatePatchUI();
                yield break;
            }

            var lines = txt.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var relLine in lines)
            {
                var clean = relLine.Trim().Replace("\\", "/");
                if (string.IsNullOrEmpty(clean) || !clean.EndsWith(".helm", StringComparison.OrdinalIgnoreCase))
                    continue;

                _all.Add(new PresetEntry
                {
                    relPath = clean,
                    display = NiceNameFromPath(clean)
                });
            }

            if (_all.Count == 0)
            {
                UpdatePatchUI();
                yield break;
            }

            RestorePresetChoice();
            _index = Mathf.Clamp(_index, 0, _all.Count - 1);
            UpdatePatchUI();
            yield return LoadAndApplyPreset(_all[_index].relPath);
        }

        private IEnumerator LoadAndApplyPreset(string relPath)
        {
            if (helm == null || _runtimePatch == null) yield break;

            string rel = presetsRootFolder + "/" + relPath;
            string json = null;
            yield return ReadStreamingText(rel, s => json = s);
            if (string.IsNullOrEmpty(json)) yield break;

            HelmPatchFormat fmt = null;
            try { fmt = JsonUtility.FromJson<HelmPatchFormat>(json); }
            catch { }
            if (fmt == null || fmt.settings == null) yield break;

            _runtimePatch.patchData = fmt;
            helm.LoadPatch(_runtimePatch);
            yield return null;
            yield return null;

            PullHelmToBus();
        }

        private static IEnumerator ReadStreamingText(string relativePath, Action<string> onDone)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            string url = StreamingAssetPath(relativePath);
            using (UnityWebRequest req = UnityWebRequest.Get(url))
            {
                yield return req.SendWebRequest();
                onDone(req.result == UnityWebRequest.Result.Success ? req.downloadHandler.text : null);
            }
#else
            string path = StreamingAssetPath(relativePath);
            if (!File.Exists(path))
            {
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

        private static void BindAll(VisualElement root, ParameterBus bus)
        {
            if (root == null || bus == null) return;

            var stack = new Stack<VisualElement>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var ve = stack.Pop();
                if (ve is IParamBindable bindable)
                    bindable.Bind(bus);

                foreach (var child in ve.Children())
                    stack.Push(child);
            }
        }

        private static void TryAssignHelmController(HelmSequencer seq, HelmController target)
        {
            if (seq == null || target == null) return;
            var field = seq.GetType().GetField("helmController", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null && field.FieldType == typeof(HelmController))
                field.SetValue(seq, target);
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
                if (tr == null || !tr.gameObject.scene.IsValid())
                    continue;

                if (string.Equals(tr.name, targetName, StringComparison.OrdinalIgnoreCase))
                    return tr.gameObject;

                if (partial == null && tr.name.IndexOf(targetName, StringComparison.OrdinalIgnoreCase) >= 0)
                    partial = tr.gameObject;
            }

            return partial;
        }

        private static ParameterBus BuildSynthBus()
        {
            var bus = new ParameterBus();

            bus.Add(new Parameter("master.vol",   0f, 1f, 0.80f));
            bus.Add(new Parameter("master.porta", 0f, 100f, 0f, unit: "%"));
            bus.Add(new Parameter("master.poly",  0f, 100f, 0f, unit: "%"));

            bus.Add(new Parameter("osc1.vol",    0f, 100f, 0f, unit: "%"));
            bus.Add(new Parameter("osc1.tune",   0f, 100f, 0f, unit: "%"));
            bus.Add(new Parameter("osc1.trans",  0f, 100f, 0f, unit: "%"));
            bus.Add(new Parameter("osc1.uni",    0f, 100f, 0f, unit: "%"));
            bus.Add(new Parameter("osc1.detune", 0f, 100f, 0f, unit: "%"));
            bus.Add(new Parameter("osc1.harm",   0f, 100f, 0f, unit: "%"));
            bus.Add(new Parameter("osc1.wave",   0f, 100f, 0f, unit: "%"));

            bus.Add(new Parameter("osc2.vol",    0f, 100f, 0f, unit: "%"));
            bus.Add(new Parameter("osc2.tune",   0f, 100f, 0f, unit: "%"));
            bus.Add(new Parameter("osc2.trans",  0f, 100f, 0f, unit: "%"));
            bus.Add(new Parameter("osc2.uni",    0f, 100f, 0f, unit: "%"));
            bus.Add(new Parameter("osc2.detune", 0f, 100f, 0f, unit: "%"));
            bus.Add(new Parameter("osc2.harm",   0f, 100f, 0f, unit: "%"));
            bus.Add(new Parameter("osc2.wave",   0f, 100f, 0f, unit: "%"));

            bus.Add(new Parameter("sub.vol",       0f, 100f, 0f, unit: "%"));
            bus.Add(new Parameter("sub.oct",       0f, 100f, 0f, unit: "%"));
            bus.Add(new Parameter("noise.vol",     0f, 100f, 0f, unit: "%"));
            bus.Add(new Parameter("synth.xmod",    0f, 100f, 0f, unit: "%"));
            bus.Add(new Parameter("filter.keytrk", 0f, 100f, 0f, unit: "%"));
            bus.Add(new Parameter("synth.vel",     0f, 100f, 0f, unit: "%"));

            bus.Add(new Parameter("fx.delay.on",   0f, 1f, 0f));
            bus.Add(new Parameter("fx.delay.mix",  0f, 100f, 0f, unit: "%"));
            bus.Add(new Parameter("fx.delay.fb",   0f, 100f, 0f, unit: "%"));
            bus.Add(new Parameter("fx.delay.time", 0f, 100f, 0f, unit: "%"));

            bus.Add(new Parameter("fx.rev.on",   0f, 1f, 0f));
            bus.Add(new Parameter("fx.rev.mix",  0f, 100f, 0f, unit: "%"));
            bus.Add(new Parameter("fx.rev.fb",   0f, 100f, 0f, unit: "%"));
            bus.Add(new Parameter("fx.rev.damp", 0f, 100f, 0f, unit: "%"));

            bus.Add(new Parameter("fx.dist.on",    0f, 1f, 0f));
            bus.Add(new Parameter("fx.dist.type",  0f, 100f, 0f, unit: "%"));
            bus.Add(new Parameter("fx.dist.drive", 0f, 100f, 0f, unit: "%"));
            bus.Add(new Parameter("fx.dist.mix",   0f, 100f, 0f, unit: "%"));

            bus.Add(new Parameter("filter.cutoff", 20f, 20000f, 3200f, log: true, unit: "hz"));
            bus.Add(new Parameter("filter.res", 0f, 100f, 30f, unit: "%"));
            bus.Add(new Parameter("filter.drive", 0f, 100f, 20f, unit: "%"));
            bus.Add(new Parameter("filter.atk", 0f, 500f, 120f, unit: "ms"));
            bus.Add(new Parameter("filter.dec", 0f, 500f, 220f, unit: "ms"));
            bus.Add(new Parameter("filter.sus", 0f, 100f, 60f, unit: "%"));
            bus.Add(new Parameter("filter.rel", 0f, 500f, 240f, unit: "ms"));

            bus.Add(new Parameter("amp.atk", 0f, 500f, 120f, unit: "ms"));
            bus.Add(new Parameter("amp.dec", 0f, 500f, 220f, unit: "ms"));
            bus.Add(new Parameter("amp.sus", 0f, 100f, 60f, unit: "%"));
            bus.Add(new Parameter("amp.rel2", 0f, 500f, 180f, unit: "ms"));

            bus.Add(new Parameter("mod.rate", 0f, 20f, 4f, unit: "hz"));
            bus.Add(new Parameter("mod.depth", 0f, 100f, 40f, unit: "%"));
            bus.Add(new Parameter("mod.mix", 0f, 100f, 30f, unit: "%"));
            bus.Add(new Parameter("mod.atk", 0f, 500f, 120f, unit: "ms"));
            bus.Add(new Parameter("mod.dec", 0f, 500f, 220f, unit: "ms"));
            bus.Add(new Parameter("mod.sus", 0f, 100f, 60f, unit: "%"));
            bus.Add(new Parameter("mod.rel", 0f, 500f, 240f, unit: "ms"));

            return bus;
        }
    }
}
