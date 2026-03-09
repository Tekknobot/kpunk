
using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UIElements;
using AudioHelm;
using KMusic.Core;
using KMusic.UI;

namespace KMusic
{
    [RequireComponent(typeof(UIDocument))]
    public sealed class KMusicChordTrackUI : MonoBehaviour
    {
        private const string PrefKey_PadStepGrid = "kmusic.pad.stepgrid";
        private const string PrefKey_PadChordType = "kmusic.pad.chordtype";

        [SerializeField] private UIDocument doc;
        [SerializeField] private HelmSequencer helmSequencer;
        [SerializeField] private HelmController helm;
        [SerializeField] private string padSynthRootName = "PadSynth";
        [SerializeField, Range(0.1f, 1.0f)] private float gateSixteenths = 0.92f;
        [SerializeField] private string pianoRollName = "PadPianoRoll";
        [SerializeField] private string stepGridName = "PadStepGrid";

        private PianoRollGrid _piano;
        private StepGrid _step;
        private UIDocument _doc;
        private HelmController _helm;
        private KMusicDrumSequencer _drums;
        private IVisualElementScheduledItem _rebindLoop;
        private bool _allowSaving = true;
        private int[] _cachedFlat;
        private bool _preferCachedOnNextBind;
        private bool _sequenceBuilt;
        private int _lastPlayhead = -1;

        private enum ChordMode
        {
            Major = 0,
            Minor = 1,
            Seventh = 2,
            Sus2 = 3,
        }

        private ChordMode _mode = ChordMode.Major;
        private Button _btnMaj;
        private Button _btnMin;
        private Button _btn7;
        private Button _btnSus;
        private Label _modeLabel;

        private void OnEnable()
        {
            if (!doc) doc = GetComponent<UIDocument>();
            _doc = doc;
            ResolvePadSynthRefs();
            _drums = FindObjectOfType<KMusicDrumSequencer>();

            EnsureIndependentPadChannel();

            if (_helm != null && helmSequencer != null)
            {
                int ch = GetControllerChannel(_helm);
                helmSequencer.channel = ch;
                helmSequencer.length = 16;
                helmSequencer.loop = true;
                helmSequencer.division = Sequencer.Division.kSixteenth;
                TryAssignHelmController(helmSequencer, _helm);
            }

            _mode = (ChordMode)Mathf.Clamp(ProjectPrefs.GetInt(PrefKey_PadChordType, 0), 0, 3);

            if (_doc == null || _doc.rootVisualElement == null)
                return;

            var root = _doc.rootVisualElement;
            WireChordButtons(root);

            _rebindLoop?.Pause();
            _rebindLoop = root.schedule.Execute(() =>
            {
                var piano = root.Q<PianoRollGrid>(pianoRollName);
                var step = root.Q<StepGrid>(stepGridName);
                if (piano == null || step == null) return;
                if (ReferenceEquals(piano, _piano) && ReferenceEquals(step, _step)) return;

                UnbindGrid();
                _piano = piano;
                _step = step;

                _piano.EnablePickerMode(true);
                _step.EnableValueLabels(true, FormatValueLabel);
                _step.EnableValueTint(true, TintForValue);
                _piano.OnCellPicked += OnPianoPicked;
                _step.OnCellValueChanged += OnStepChanged;

                if (_preferCachedOnNextBind && _cachedFlat != null)
                    ApplyPadStepsFlat(_cachedFlat);
                else
                {
                    var loaded = KMusicSaveState.LoadIntArray(PrefKey_PadStepGrid, _step.RowCount * _step.ColCount);
                    if (loaded != null)
                        ApplyPadStepsFlat(loaded);
                    else
                    {
                        _step.ClearAll();
                        _step.RefreshAll();
                    }
                }

                _preferCachedOnNextBind = false;
                RefreshChordButtons();
                _step.SetPlayheadStep(-1);
                _lastPlayhead = -1;
                RebuildSequenceNow();
            }).Every(120);
        }

        private void Update()
        {
            if (_drums == null) _drums = FindObjectOfType<KMusicDrumSequencer>();
            if (_helm == null || helmSequencer == null) ResolvePadSynthRefs();

            if (_step != null && _drums != null)
            {
                int playhead = _drums.IsPlaying ? _drums.CurrentStepIndex : -1;
                if (playhead != _lastPlayhead)
                {
                    _step.SetPlayheadStep(playhead);
                    _lastPlayhead = playhead;
                }
            }

            if (_drums == null || helmSequencer == null) return;

            if (_drums.IsPlaying)
            {
                if (!_sequenceBuilt)
                {
                    RebuildSequenceNow();
                    _sequenceBuilt = true;
                }
            }
            else
            {
                _sequenceBuilt = false;
            }
        }

        private void OnDisable()
        {
            SaveGrid();
            if (_step != null) _step.SetPlayheadStep(-1);
            _lastPlayhead = -1;
            _rebindLoop?.Pause();
            UnbindGrid();
        }

        private void UnbindGrid()
        {
            if (_piano != null) _piano.OnCellPicked -= OnPianoPicked;
            if (_step != null) _step.OnCellValueChanged -= OnStepChanged;
            _piano = null;
            _step = null;
        }

        private void WireChordButtons(VisualElement root)
        {
            _btnMaj = root.Q<Button>("PadChordMajButton");
            _btnMin = root.Q<Button>("PadChordMinButton");
            _btn7 = root.Q<Button>("PadChord7Button");
            _btnSus = root.Q<Button>("PadChordSusButton");
            _modeLabel = root.Q<Label>("PadChordModeLabel");

            WireButton(_btnMaj, ChordMode.Major);
            WireButton(_btnMin, ChordMode.Minor);
            WireButton(_btn7, ChordMode.Seventh);
            WireButton(_btnSus, ChordMode.Sus2);
            RefreshChordButtons();
        }

        private void WireButton(Button btn, ChordMode mode)
        {
            if (btn == null) return;
            btn.clicked += () =>
            {
                _mode = mode;
                ProjectPrefs.SetInt(PrefKey_PadChordType, (int)_mode);
                ProjectPrefs.Save();
                RefreshChordButtons();
                RebuildSequenceNow();
            };
        }

        private void RefreshChordButtons()
        {
            SetActive(_btnMaj, _mode == ChordMode.Major);
            SetActive(_btnMin, _mode == ChordMode.Minor);
            SetActive(_btn7, _mode == ChordMode.Seventh);
            SetActive(_btnSus, _mode == ChordMode.Sus2);
            if (_modeLabel != null) _modeLabel.text = ChordModeText(_mode);
        }

        private static void SetActive(VisualElement ve, bool on)
        {
            if (ve == null) return;
            const string cls = "km-chip--active";
            if (on) ve.AddToClassList(cls);
            else ve.RemoveFromClassList(cls);
        }

        private void OnPianoPicked(int r, int c, string label, int valueId)
        {
            if (_step == null) return;
            _step.SetPaintValue(valueId);
            AuditionChord(valueId);
        }

        private void OnStepChanged(int r, int c, int v)
        {
            CacheFlat(_step.ExportValuesFlat());
            SaveGrid();
            RebuildSequenceNow();
        }

        private void AuditionChord(int valueId)
        {
            if (_helm == null) ResolvePadSynthRefs();
            EnsureIndependentPadChannel();
            if (_helm == null) return;
            var notes = BuildChordFromValueId(valueId);
            for (int i = 0; i < notes.Length; i++)
                _helm.NoteOn(notes[i], 1f, 0.24f + (i * 0.02f));
        }

        private int[] BuildChordFromValueId(int valueId)
        {
            int root = Mathf.Clamp(60 + Mathf.Max(0, valueId - 1), 0, 127);
            switch (_mode)
            {
                default:
                case ChordMode.Major: return new[] { root, root + 4, root + 7 };
                case ChordMode.Minor: return new[] { root, root + 3, root + 7 };
                case ChordMode.Seventh: return new[] { root, root + 4, root + 7, root + 10 };
                case ChordMode.Sus2: return new[] { root, root + 2, root + 7 };
            }
        }

        private string FormatValueLabel(int v)
        {
            if (v <= 0 || _piano == null) return "";
            int idx = v - 1;
            int r = idx / _piano.ColCount;
            int c = idx % _piano.ColCount;
            string label = _piano.GetCellLabel(r, c);
            if (string.IsNullOrEmpty(label)) return v.ToString();
            return label;
        }

        private Color TintForValue(int v)
        {
            if (v <= 0 || _piano == null) return new Color(0,0,0,0);
            int idx = v - 1;
            int r = idx / _piano.ColCount;
            int c = idx % _piano.ColCount;
            Color baseColor = _piano.GetCellColor(r, c);
            return new Color(baseColor.r * 0.90f, baseColor.g * 0.90f, baseColor.b * 0.95f, 0.95f);
        }

        private void SaveGrid()
        {
            if (!_allowSaving || _step == null) return;
            var flat = _step.ExportValuesFlat();
            CacheFlat(flat);
            KMusicSaveState.SaveIntArray(PrefKey_PadStepGrid, flat);
            ProjectPrefs.SetInt(PrefKey_PadChordType, (int)_mode);
            ProjectPrefs.Save();
        }

        private void CacheFlat(int[] flat)
        {
            if (flat == null)
            {
                _cachedFlat = null;
                return;
            }
            _cachedFlat = (int[])flat.Clone();
        }

        public void SetAllowSaving(bool on) => _allowSaving = on;

        public int[] CapturePadStepsFlat()
        {
            if (_step != null)
            {
                var flat = _step.ExportValuesFlat();
                CacheFlat(flat);
                return flat;
            }
            return _cachedFlat != null ? (int[])_cachedFlat.Clone() : null;
        }

        public void ApplyPadStepsFlat(int[] flat)
        {
            CacheFlat(flat);
            _preferCachedOnNextBind = true;

            if (_step == null) return;

            if (flat == null || flat.Length != _step.RowCount * _step.ColCount)
            {
                _step.ClearAll();
                _step.RefreshAll();
                return;
            }

            _step.ImportValuesFlat(flat, fireEvent: false);
            _step.RefreshAll();
            KMusicSaveState.SaveIntArray(PrefKey_PadStepGrid, flat);
        }

        public int CaptureChordMode() => (int)_mode;

        public void ApplyChordMode(int mode)
        {
            _mode = (ChordMode)Mathf.Clamp(mode, 0, 3);
            ProjectPrefs.SetInt(PrefKey_PadChordType, (int)_mode);
            ProjectPrefs.Save();
            RefreshChordButtons();
            RebuildSequenceNow();
        }

        public void RebuildSequenceNow()
        {
            if (helmSequencer == null || _step == null) return;

            helmSequencer.Clear();
            int total = _step.RowCount * _step.ColCount;
            for (int i = 0; i < total; i++)
            {
                int r = i / _step.ColCount;
                int c = i % _step.ColCount;
                int valueId = _step.GetValue(r, c);
                if (valueId <= 0) continue;

                var notes = BuildChordFromValueId(valueId);
                float start = i;
                float end = i + gateSixteenths;
                for (int n = 0; n < notes.Length; n++)
                    helmSequencer.AddNote(notes[n], start, end, 0.90f);
            }

            helmSequencer.enabled = false;
            helmSequencer.enabled = true;
        }



        private void ResolvePadSynthRefs()
        {
            GameObject root = FindSceneObjectByName(padSynthRootName);
            if (root == null)
                return;

            var rootSeq = root.GetComponentInChildren<HelmSequencer>(true);
            var rootHelm = FindLinkedController(rootSeq) ?? root.GetComponentInChildren<HelmController>(true);

            if (rootSeq != null)
                helmSequencer = rootSeq;
            if (rootHelm != null)
                helm = rootHelm;

            _helm = helm;

            EnsureIndependentPadChannel();

            if (_helm != null && helmSequencer != null)
            {
                helmSequencer.channel = GetControllerChannel(_helm);
                helmSequencer.length = 16;
                helmSequencer.loop = true;
                helmSequencer.division = Sequencer.Division.kSixteenth;
                TryAssignHelmController(helmSequencer, _helm);
            }
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
            if (_helm == null) return;

            var excludedRoot = FindSceneObjectByName(padSynthRootName);
            var mainHelm = FindFirstSceneComponentOutsideRoot<HelmController>(excludedRoot);
            if (mainHelm == null || ReferenceEquals(mainHelm, _helm)) return;

            int padChannel = GetControllerChannel(_helm);
            int mainChannel = GetControllerChannel(mainHelm);
            if (padChannel != mainChannel) return;

            int newChannel = FindUnusedChannel(mainChannel, _helm);
            if (newChannel == padChannel) return;

            SetControllerChannel(_helm, newChannel);
            if (helmSequencer != null)
                helmSequencer.channel = newChannel;

            Debug.Log($"[KMusicChordTrackUI] PadSynth channel was shared with main synth ({mainChannel}). Reassigned PadSynth to channel {newChannel}.");
        }

        private static int FindUnusedChannel(int avoidChannel, HelmController current)
        {
#if UNITY_2023_1_OR_NEWER
            var all = FindObjectsByType<HelmController>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
            var all = Resources.FindObjectsOfTypeAll<HelmController>();
#endif
            var used = new System.Collections.Generic.HashSet<int>();
            foreach (var h in all)
            {
                if (h == null || !h.gameObject.scene.IsValid()) continue;
                if (ReferenceEquals(h, current)) continue;
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
            var prop = type.GetProperty("channel", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (prop != null && prop.PropertyType == typeof(int) && prop.CanRead)
                return (int)prop.GetValue(target, null);
            var field = type.GetField("channel", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (field != null && field.FieldType == typeof(int))
                return (int)field.GetValue(target);
            return 0;
        }

        private static void SetControllerChannel(HelmController target, int channel)
        {
            if (target == null) return;
            var type = target.GetType();
            var prop = type.GetProperty("channel", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (prop != null && prop.PropertyType == typeof(int) && prop.CanWrite)
            {
                prop.SetValue(target, channel, null);
                return;
            }
            var field = type.GetField("channel", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (field != null && field.FieldType == typeof(int))
                field.SetValue(target, channel);
        }

        private static void TryAssignHelmController(HelmSequencer seq, HelmController target)
        {
            if (seq == null || target == null) return;
            var field = seq.GetType().GetField("helmController", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (field != null && field.FieldType == typeof(HelmController))
                field.SetValue(seq, target);
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
            if (go == null || root == null) return false;
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

        private static string ChordModeText(ChordMode mode)
        {
            switch (mode)
            {
                case ChordMode.Minor: return "MIN";
                case ChordMode.Seventh: return "7TH";
                case ChordMode.Sus2: return "SUS2";
                default: return "MAJ";
            }
        }
    }
}
