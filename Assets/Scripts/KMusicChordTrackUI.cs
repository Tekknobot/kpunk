using System;
using System.Collections.Generic;
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
        private KMusicChainUI _chainUI;
        private IVisualElementScheduledItem _rebindLoop;
        private bool _allowSaving = true;
        private int[] _cachedFlat;
        private bool _preferCachedOnNextBind;
        private bool _sequenceBuilt;
        private int _lastPlayhead = -1;

        // Note-length run tracking (same pattern as KMusicPianoRollStepPainter)
        private int[] _noteRunLengthAtStart;
        private int[] _noteRunStartForCell;

        // Stroke state (no time dependency)
        private bool _strokeActive = false;

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
            _chainUI = FindObjectOfType<KMusicChainUI>();

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
                _step.OnStrokeStarted += OnStepStrokeStarted;
                _step.OnStrokeEnded += OnStepStrokeEnded;

                if (_preferCachedOnNextBind && _cachedFlat != null)
                    ApplyPadStepsFlat(_cachedFlat);
                else
                {
                    int expected = _step.RowCount * _step.ColCount;
                    var loaded = KMusicSaveState.LoadIntArray(PrefKey_PadStepGrid, expected);
                    var runs = KMusicSaveState.LoadIntArray(PrefKey_PadStepGrid + ".runs", expected);

                    if (loaded != null && loaded.Length == expected)
                    {
                        ApplyPadStepsFlat(loaded, runs);
                    }
                    else
                    {
                        _step.ClearAll();
                        _step.RefreshAll();

                        EnsureRunArrays();
                        for (int i = 0; i < _noteRunLengthAtStart.Length; i++)
                        {
                            _noteRunLengthAtStart[i] = 0;
                            _noteRunStartForCell[i] = -1;
                        }
                    }
                }

                _preferCachedOnNextBind = false;
                RefreshChordButtons();
                _step.SetPlayheadStep(-1);
                _lastPlayhead = -1;
                _sequenceBuilt = false;
                RebuildSequenceNow();
            }).Every(120);
        }

        private void Update()
        {
            if (_drums == null) _drums = FindObjectOfType<KMusicDrumSequencer>();
            if (_chainUI == null) _chainUI = FindObjectOfType<KMusicChainUI>();
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
            if (_step != null)
            {
                _step.OnCellValueChanged -= OnStepChanged;
                _step.OnStrokeStarted -= OnStepStrokeStarted;
                _step.OnStrokeEnded -= OnStepStrokeEnded;
            }
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

                if (_allowSaving)
                {
                    ProjectPrefs.SetInt(PrefKey_PadChordType, (int)_mode);
                    ProjectPrefs.Save();
                }

                RefreshChordButtons();
                _sequenceBuilt = false;
                RebuildSequenceNow();

                if (_chainUI == null) _chainUI = FindObjectOfType<KMusicChainUI>();
                _chainUI?.NotifyLiveEdited();
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
            if (_step == null) return;

            EnsureRunArrays();
            int index = ToIndex(r, c);

            if (v <= 0)
                ClearRunAtOrContaining(index);
            else
                SetSingleNote(index);  // stroke end will correct multi-cell runs

            CacheFlat(_step.ExportValuesFlat());
            // Don't rebuild here during an active stroke — wait for OnStepStrokeEnded
            if (!_strokeActive)
            {
                _sequenceBuilt = false;
                RebuildSequenceNow();
                SaveGrid();

                if (_chainUI == null)
                    _chainUI = FindObjectOfType<KMusicChainUI>();
                _chainUI?.NotifyLiveEdited();
            }
        }

        // --- Run-length helpers ---

        private int TotalStepCount() => _step != null ? _step.RowCount * _step.ColCount : 0;
        private int ToIndex(int r, int c) => r * (_step != null ? _step.ColCount : 8) + c;

        private void EnsureRunArrays()
        {
            int total = TotalStepCount();
            if (total <= 0) return;
            if (_noteRunLengthAtStart == null || _noteRunLengthAtStart.Length != total)
            {
                _noteRunLengthAtStart = new int[total];
                _noteRunStartForCell = new int[total];
                for (int i = 0; i < total; i++)
                    _noteRunStartForCell[i] = -1;
            }
        }

        private void ClearRunAtOrContaining(int index)
        {
            EnsureRunArrays();
            if (_noteRunStartForCell == null) return;
            if (index < 0 || index >= _noteRunStartForCell.Length) return;

            int start = -1;
            if (_noteRunLengthAtStart[index] > 0)
                start = index;
            else if (_noteRunStartForCell[index] >= 0)
                start = _noteRunStartForCell[index];
            if (start < 0) return;

            int len = Mathf.Max(1, _noteRunLengthAtStart[start]);
            for (int i = 0; i < len; i++)
            {
                int cell = start + i;
                if (cell >= 0 && cell < _noteRunStartForCell.Length)
                    _noteRunStartForCell[cell] = -1;
            }
            _noteRunLengthAtStart[start] = 0;
        }

        private void SetSingleNote(int index)
        {
            EnsureRunArrays();
            ClearRunAtOrContaining(index);
            _noteRunLengthAtStart[index] = 1;
            _noteRunStartForCell[index] = index;
        }

        // --- Stroke handlers (no time dependency) ---

        private void OnStepStrokeStarted(bool isErase)
        {
            _strokeActive = !isErase;
        }

        private void OnStepStrokeEnded(List<UnityEngine.Vector2Int> cells, bool isErase)
        {
            _strokeActive = false;
            if (_step == null) return;

            EnsureRunArrays();

            if (isErase || cells == null || cells.Count == 0)
            {
                if (cells != null)
                    foreach (var rc in cells)
                        ClearRunAtOrContaining(ToIndex(rc.x, rc.y));
                _sequenceBuilt = false;
                RebuildSequenceNow();
                SaveGrid();
                return;
            }

            // Group painted cells by row; find contiguous column runs per row.
            // Different rows are independent (chords on separate rows stay separate notes).
            var byRow = new Dictionary<int, List<int>>();
            foreach (var rc in cells)
            {
                if (!byRow.TryGetValue(rc.x, out var cols)) { cols = new List<int>(); byRow[rc.x] = cols; }
                if (!cols.Contains(rc.y)) cols.Add(rc.y);
            }

            foreach (var kv in byRow)
            {
                int row = kv.Key;
                var colsSorted = kv.Value;
                colsSorted.Sort();

                // Split into contiguous runs within this row
                var runs = new List<List<int>>();
                List<int> cur = null;
                foreach (int col in colsSorted)
                {
                    if (cur == null || col != cur[cur.Count - 1] + 1)
                    {
                        cur = new List<int>();
                        runs.Add(cur);
                    }
                    cur.Add(col);
                }

                foreach (var run in runs)
                {
                    int startIdx = ToIndex(row, run[0]);
                    // Clear any overlap first
                    foreach (int col in run) ClearRunAtOrContaining(ToIndex(row, col));

                    _noteRunLengthAtStart[startIdx] = run.Count;
                    for (int i = 0; i < run.Count; i++)
                    {
                        int cellIdx = ToIndex(row, run[i]);
                        _noteRunStartForCell[cellIdx] = startIdx;
                        if (i > 0) _noteRunLengthAtStart[cellIdx] = 0;
                    }
                }
            }

            _sequenceBuilt = false;
            RebuildSequenceNow();
            SaveGrid();

            if (_chainUI == null) _chainUI = FindObjectOfType<KMusicChainUI>();
            _chainUI?.NotifyLiveEdited();
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
            if (v <= 0 || _piano == null) return new Color(0, 0, 0, 0);
            int idx = v - 1;
            int r = idx / _piano.ColCount;
            int c = idx % _piano.ColCount;
            Color baseColor = _piano.GetCellColor(r, c);
            return new Color(baseColor.r * 0.90f, baseColor.g * 0.90f, baseColor.b * 0.95f, 0.95f);
        }

        private void SaveGrid()
        {
            if (_step == null) return;

            int[] flat = _step.ExportValuesFlat();
            if (flat == null) return;

            CacheFlat(flat);

            if (_allowSaving)
            {
                KMusicSaveState.SaveIntArray(PrefKey_PadStepGrid, flat);

                int total = _step.RowCount * _step.ColCount;
                int[] runStarts = new int[total];

                EnsureRunArrays();

                for (int i = 0; i < total; i++)
                    runStarts[i] = (_noteRunLengthAtStart != null && i < _noteRunLengthAtStart.Length)
                        ? _noteRunLengthAtStart[i]
                        : 0;

                KMusicSaveState.SaveIntArray(PrefKey_PadStepGrid + ".runs", runStarts);
                ProjectPrefs.Save();
            }

            int cols = _step.ColCount;

            for (int i = 0; i < flat.Length; i++)
            {
                if (flat[i] <= 0) continue;
                if (_noteRunStartForCell != null && _noteRunStartForCell[i] >= 0 && _noteRunStartForCell[i] != i)
                    continue;

                int r = i / cols;
                int c = i % cols;
                int len = (_noteRunLengthAtStart != null && i < _noteRunLengthAtStart.Length && _noteRunLengthAtStart[i] > 0)
                    ? _noteRunLengthAtStart[i]
                    : 1;

                Debug.Log($"[PAD SAVE] row={r} col={c} len={len} value={flat[i]}");
            }
        }        private void CacheFlat(int[] flat)
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
            int expected = (_step != null) ? (_step.RowCount * _step.ColCount) : (flat != null ? flat.Length : 0);
            int[] runs = KMusicSaveState.LoadIntArray(PrefKey_PadStepGrid + ".runs", expected);
            ApplyPadStepsFlat(flat, runs);
        }

        public void ApplyPadStepsFlat(int[] flat, int[] runs)
        {
            CacheFlat(flat);
            _preferCachedOnNextBind = true;

            if (_step == null) return;

            if (flat == null || flat.Length != _step.RowCount * _step.ColCount)
            {
                _step.ClearAll();
                _step.RefreshAll();
                _noteRunLengthAtStart = null;
                _noteRunStartForCell = null;
                _sequenceBuilt = false;
                return;
            }

            _step.ImportValuesFlat(flat, fireEvent: false);
            _step.RefreshAll();

            EnsureRunArrays();
            for (int i = 0; i < _noteRunLengthAtStart.Length; i++)
            {
                _noteRunLengthAtStart[i] = 0;
                _noteRunStartForCell[i] = -1;
            }

            int total = flat.Length;
            int cols = _step.ColCount;

            bool usedExplicitRuns = false;

            if (runs != null && runs.Length == total)
            {
                for (int i = 0; i < total; i++)
                {
                    int value = flat[i];
                    int len = runs[i];

                    if (value <= 0 || len <= 0)
                        continue;

                    _noteRunLengthAtStart[i] = len;

                    for (int k = 0; k < len && i + k < total; k++)
                    {
                        _noteRunStartForCell[i + k] = i;
                        if (k > 0)
                            _noteRunLengthAtStart[i + k] = 0;
                    }

                    int r = i / cols;
                    int c = i % cols;
                    Debug.Log($"[PAD LOAD] row={r} col={c} len={len} value={value}");
                    usedExplicitRuns = true;
                }
            }

            if (!usedExplicitRuns)
            {
                for (int i = 0; i < total; i++)
                {
                    if (flat[i] > 0)
                    {
                        _noteRunLengthAtStart[i] = 1;
                        _noteRunStartForCell[i] = i;

                        int r = i / cols;
                        int c = i % cols;
                        Debug.Log($"[PAD LOAD] row={r} col={c} len=1 value={flat[i]}");
                    }
                }
            }

            _sequenceBuilt = false;

            if (_allowSaving)
            {
                KMusicSaveState.SaveIntArray(PrefKey_PadStepGrid, flat);

                int[] runStarts = new int[total];
                for (int i = 0; i < total; i++)
                    runStarts[i] = _noteRunLengthAtStart[i];

                KMusicSaveState.SaveIntArray(PrefKey_PadStepGrid + ".runs", runStarts);
                ProjectPrefs.Save();
            }
        }
        public int CaptureChordMode() => (int)_mode;

        public void ApplyChordMode(int mode)
        {
            _mode = (ChordMode)Mathf.Clamp(mode, 0, 3);

            if (_allowSaving)
            {
                ProjectPrefs.SetInt(PrefKey_PadChordType, (int)_mode);
                ProjectPrefs.Save();
            }

            RefreshChordButtons();
            _sequenceBuilt = false;
            RebuildSequenceNow();
        }

        public void RebuildPadSequenceNow()
        {
            _sequenceBuilt = false;
            RebuildSequenceNow();
        }

        public void RebuildSequenceNow()
        {
            if (helmSequencer == null || _step == null) return;

            EnsureRunArrays();
            helmSequencer.Clear();

            int total = _step.RowCount * _step.ColCount;
            for (int i = 0; i < total; i++)
            {
                int r = i / _step.ColCount;
                int c = i % _step.ColCount;
                int valueId = _step.GetValue(r, c);
                if (valueId <= 0) continue;

                // Skip continuation cells — only the run-start cell emits a note
                if (_noteRunStartForCell != null &&
                    _noteRunStartForCell.Length > i &&
                    _noteRunStartForCell[i] >= 0 &&
                    _noteRunStartForCell[i] != i)
                    continue;

                int runLen = (_noteRunLengthAtStart != null && _noteRunLengthAtStart.Length > i)
                    ? _noteRunLengthAtStart[i] : 0;
                if (runLen <= 0) runLen = 1;

                var notes = BuildChordFromValueId(valueId);
                float start = i;
                float end = i + runLen - (1f - gateSixteenths);
                if (end <= start) end = start + gateSixteenths;

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