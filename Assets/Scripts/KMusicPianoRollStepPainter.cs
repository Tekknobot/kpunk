using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using AudioHelm;
using KMusic.Core;

namespace KMusic.UI
{
    /// <summary>
    /// Bridges PianoRollGrid (note picker) -> StepGrid (paint target).
    /// Tap = individual note.
    /// Drag across adjacent cells = held note matching drag length.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class KMusicPianoRollStepPainter : MonoBehaviour
    {
        [SerializeField] private HelmSequencer helmSequencer;   // assign or auto-find
        [SerializeField] private int helmChannelOverride = -1;  // -1 = use HelmController.channel
        [SerializeField, Range(0.1f, 1.0f)] private float gateSixteenths = 0.90f;
        [SerializeField] private string excludedRootName = "PadSynth";
        private const string PrefKey_SeqStepGrid = "kmusic.seq.stepgrid";

        [Header("UXML element names")]
        public string pianoRollName = "SeqPianoRoll";
        public string stepGridName = "StepGrid";

        [Header("Debug")]
        public bool verboseLogs = false;

        private bool _seqDirty = false;
        private IVisualElementScheduledItem _seqRebuildJob;

        private UIDocument _doc;
        private VisualElement _root;
        private PianoRollGrid _piano;
        private StepGrid _step;

        private IVisualElementScheduledItem _rebindLoop;
        private StepGrid _lastStep;
        private PianoRollGrid _lastPiano;

        private HelmController _helm;

        // CHAIN support: suppress writes while importing patterns
        private bool _allowSaving = true;
        private int[] _cachedSeqStepsFlat;
        private bool _hasCachedSeqSteps;
        private bool _preferCachedOnNextBind;

        // valueId -> label/color
        private readonly Dictionary<int, string> _labelByValue = new();
        private readonly Dictionary<int, Color> _colorByValue = new();

        private KMusicDrumSequencer _drumSeq;
        private int _lastTransportStep = -999;
        private float _lastBpm = -1f;

        [SerializeField] private HelmController helm;

        private bool _wasPlaying = false;
        private bool _sequenceBuilt = false;
        private KMusicChainUI _chainUI;

        // Held-note metadata:
        // start index -> run length
        private int[] _noteRunLengthAtStart;
        // any cell index -> owning run start, or -1
        private int[] _noteRunStartForCell;

        // Stroke-based note-length tracking (no time dependency).
        // Each pointer-down → pointer-up is one atomic stroke.
        // Cells painted in the stroke are committed to run arrays only on stroke end.
        private bool _strokeActive = false;
        private int _strokeValueId = -1;
        // Per-row contiguous run being built during the current stroke: row -> ordered list of column indices
        private readonly Dictionary<int, List<int>> _strokeRunsByRow = new Dictionary<int, List<int>>();

        private void Update()
        {
            if (_drumSeq == null)
                _drumSeq = FindObjectOfType<KMusicDrumSequencer>();

            if (_helm == null || helmSequencer == null)
                ResolveMainSynthRefs();

            if (_drumSeq == null || helmSequencer == null)
                return;

            if (_drumSeq.IsPlaying)
            {
                if (!_sequenceBuilt)
                {
                    RebuildHelmSequenceFromGrid();
                    _sequenceBuilt = true;
                }
            }
            else
            {
                _sequenceBuilt = false;
            }
        }

        private void OnEnable()
        {
            _doc = GetComponent<UIDocument>();
            _drumSeq = FindObjectOfType<KMusicDrumSequencer>();
            _chainUI = FindObjectOfType<KMusicChainUI>();

            ResolveMainSynthRefs();

            if (_doc == null) return;

            var root = _doc.rootVisualElement;
            if (root == null) return;

            _rebindLoop?.Pause();
            _rebindLoop = root.schedule.Execute(() =>
            {
                var piano = root.Q<PianoRollGrid>(pianoRollName);
                var step = root.Q<StepGrid>(stepGridName);
                if (piano == null || step == null) return;

                if (piano == _lastPiano && step == _lastStep) return;
                _lastPiano = piano;
                _lastStep = step;

                Unbind();
                _piano = piano;
                _step = step;

                EnsureRunArrays();

                _piano.EnablePickerMode(true);
                _step.EnableValueLabels(true, FormatValueLabel);
                _step.EnableValueTint(true, TintForValue);

                _piano.OnCellPicked += OnPianoPicked;
                _step.OnCellClicked += OnStepClicked;

                _step.OnCellValueChanged -= OnStepValueChanged;
                _step.OnCellValueChanged += OnStepValueChanged;

                _step.OnStrokeStarted -= OnStepStrokeStarted;
                _step.OnStrokeStarted += OnStepStrokeStarted;

                _step.OnStrokeEnded -= OnStepStrokeEnded;
                _step.OnStrokeEnded += OnStepStrokeEnded;

                _seqRebuildJob?.Pause();
                _seqRebuildJob = _step.schedule.Execute(() =>
                {
                    if (!_seqDirty) return;
                    _seqDirty = false;

                    RebuildHelmSequenceFromGrid();

                    helmSequencer.enabled = false;
                    helmSequencer.enabled = true;
                }).Every(30);

                CacheAllPaletteValues();

                int v0 = _piano.GetCellValueId(0, 0);
                _step.SetPaintValue(v0);

                if (_preferCachedOnNextBind && _hasCachedSeqSteps && _cachedSeqStepsFlat != null)
                    ApplySeqStepsFlat(_cachedSeqStepsFlat);
                else
                    LoadStepGrid();

                _preferCachedOnNextBind = false;
                RebuildHelmSequenceFromGrid();
                helmSequencer.enabled = false;
                helmSequencer.enabled = true;
                _sequenceBuilt = true;

                _lastTransportStep = -999;

            }).Every(100);
        }

        private int TotalStepCount()
        {
            return _step != null ? _step.RowCount * _step.ColCount : 0;
        }

        private int ToIndex(int r, int c)
        {
            return r * _step.ColCount + c;
        }

        private void FromIndex(int index, out int r, out int c)
        {
            r = index / _step.ColCount;
            c = index % _step.ColCount;
        }

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

        private void SetSingleTapNote(int index)
        {
            EnsureRunArrays();
            ClearRunAtOrContaining(index);

            _noteRunLengthAtStart[index] = 1;
            _noteRunStartForCell[index] = index;
        }

        // --- Stroke-based note-length logic (no time dependency) ---

        private void OnStepStrokeStarted(bool isErase)
        {
            // Begin a new stroke; collect which cells are touched per row.
            _strokeActive = !isErase;
            _strokeValueId = _step != null ? _step.GetPaintValue() : -1;
            _strokeRunsByRow.Clear();
        }

        private void OnStepStrokeEnded(List<Vector2Int> cells, bool isErase)
        {
            _strokeActive = false;

            if (isErase || cells == null || cells.Count == 0)
            {
                // Erase strokes: clear runs for each affected cell
                if (cells != null)
                {
                    EnsureRunArrays();
                    foreach (var rc in cells)
                        ClearRunAtOrContaining(ToIndex(rc.x, rc.y));
                }
                _strokeRunsByRow.Clear();
                _seqDirty = true;
                SaveStepGrid();
                return;
            }

            EnsureRunArrays();

            // Group cells by row (chords/pads on different rows stay independent).
            // Within each row, find the contiguous column runs in the order they were painted.
            var byRow = new Dictionary<int, List<int>>(); // row -> ordered columns
            foreach (var rc in cells)
            {
                if (!byRow.TryGetValue(rc.x, out var cols))
                {
                    cols = new List<int>();
                    byRow[rc.x] = cols;
                }
                if (!cols.Contains(rc.y))
                    cols.Add(rc.y);
            }

            foreach (var kv in byRow)
            {
                int row = kv.Key;
                var cols = kv.Value;
                cols.Sort();

                // Split into contiguous runs within this row
                var runStart = new List<int>();
                var runCols = new List<List<int>>();
                List<int> cur = null;

                foreach (int col in cols)
                {
                    if (cur == null || col != cur[cur.Count - 1] + 1)
                    {
                        cur = new List<int>();
                        runStart.Add(col);
                        runCols.Add(cur);
                    }
                    cur.Add(col);
                }

                // Commit each run
                for (int ri = 0; ri < runCols.Count; ri++)
                {
                    var run = runCols[ri];
                    int startIdx = ToIndex(row, run[0]);
                    int len = run.Count;

                    // Clear any pre-existing runs overlapped by this stroke
                    foreach (int col in run)
                        ClearRunAtOrContaining(ToIndex(row, col));

                    _noteRunLengthAtStart[startIdx] = len;
                    for (int i = 0; i < len; i++)
                    {
                        int cellIdx = ToIndex(row, run[i]);
                        _noteRunStartForCell[cellIdx] = startIdx;
                    }
                    // Continuation cells should not have a run length of their own
                    for (int i = 1; i < len; i++)
                        _noteRunLengthAtStart[ToIndex(row, run[i])] = 0;
                }
            }

            _strokeRunsByRow.Clear();
            _seqDirty = true;
            SaveStepGrid();

            if (_chainUI == null) _chainUI = FindObjectOfType<KMusicChainUI>();
            _chainUI?.NotifyLiveEdited();
        }

        private void OnStepValueChanged(int r, int c, int v)
        {
            EnsureRunArrays();

            int index = ToIndex(r, c);

            _seqDirty = true;

            if (v <= 0)
            {
                // Erase: clear run metadata immediately (stroke end will also do it, but keep in sync)
                ClearRunAtOrContaining(index);
                SaveStepGrid();

                if (_chainUI == null) _chainUI = FindObjectOfType<KMusicChainUI>();
                _chainUI?.NotifyLiveEdited();
                return;
            }

            // During an active paint stroke, register this cell as a single sixteenth for now.
            // OnStepStrokeEnded will compute the final run lengths from the full cell list.
            SetSingleTapNote(index);

            PlayHelmValueId(v, 1.0f, 0.18f);
        }

        private void RebuildHelmSequenceFromGrid()
        {
            if (helmSequencer == null || _step == null) return;

            EnsureRunArrays();
            helmSequencer.Clear();

            int totalSteps = _step.RowCount * _step.ColCount;

            for (int stepIndex = 0; stepIndex < totalSteps; stepIndex++)
            {
                int r = stepIndex / _step.ColCount;
                int c = stepIndex % _step.ColCount;
                int valueId = _step.GetValue(r, c);

                if (valueId <= 0)
                    continue;

                if (_noteRunStartForCell[stepIndex] >= 0 && _noteRunStartForCell[stepIndex] != stepIndex)
                    continue;

                int midi = MidiFromValueIdChromatic(valueId, baseMidi: 60);

                int runLen = _noteRunLengthAtStart[stepIndex];
                if (runLen <= 0)
                    runLen = 1;

                float start = stepIndex;
                float end = stepIndex + runLen - (1f - gateSixteenths);
                if (end <= start)
                    end = start + gateSixteenths;

                helmSequencer.AddNote(midi, start, end, 1.0f);
            }
        }

        private void OnDisable()
        {
            SaveStepGrid();
            _rebindLoop?.Pause();
            Unbind();
        }

        private void CacheAllPaletteValues()
        {
            if (_piano == null) return;

            _labelByValue.Clear();
            _colorByValue.Clear();

            for (int r = 0; r < _piano.RowCount; r++)
            for (int c = 0; c < _piano.ColCount; c++)
            {
                int valueId = _piano.GetCellValueId(r, c);
                string label = _piano.GetCellLabel(r, c);
                Color color = _piano.GetCellColor(r, c);

                CacheValue(valueId, label, color);
            }
        }

        private void Unbind()
        {
            if (_piano != null)
                _piano.OnCellPicked -= OnPianoPicked;

            if (_step != null)
            {
                _step.OnCellClicked -= OnStepClicked;
                _step.OnCellValueChanged -= OnStepValueChanged;
                _step.OnStrokeStarted -= OnStepStrokeStarted;
                _step.OnStrokeEnded -= OnStepStrokeEnded;
            }

            _seqRebuildJob?.Pause();
            _seqRebuildJob = null;
            _seqDirty = false;

            // Reset stroke state
            _strokeActive = false;
            _strokeValueId = -1;
            _strokeRunsByRow.Clear();

            _piano = null;
            _step = null;
            _root = null;
        }

        private void OnPianoPicked(int r, int c, string label, int valueId)
        {
            if (_step == null || _piano == null) return;

            CacheValue(valueId, label, _piano.GetCellColor(r, c));
            _step.SetPaintValue(valueId);

            if (_helm == null) ResolveMainSynthRefs();
            Debug.Log($"[PianoRollPainter] PICK r={r} c={c} label={label} valueId={valueId} helm={(_helm != null)}");

            TryAuditionSynthNote(r, c);

            if (verboseLogs)
                Debug.Log($"Picked {label} -> brush={valueId}");
        }

        private void OnStepClicked(int r, int c)
        {
            if (_step == null) return;

            int v = _step.GetValue(r, c);

            _seqDirty = true;

            if (v > 0)
                _step.SetPaintValue(v);

            if (v > 0)
                PlayHelmValueId(v, velocity: 1.0f, length: 0.18f);

            if (verboseLogs)
                Debug.Log($"[PianoRollPainter] Step clicked r={r} c={c} v={v}");
        }

        private float GetTempoBpm()
        {
            var app = FindObjectOfType<KMusicApp>();
            if (app != null && app.Bus != null && app.Bus.TryGet("tempo", out var p))
                return p.Value;

            return 120f;
        }

        private void PlayHelmValueId(int valueId, float velocity, float length)
        {
            if (_helm == null)
                ResolveMainSynthRefs();

            if (_helm == null) return;

            int midi = MidiFromValueIdChromatic(valueId, baseMidi: 60);
            _helm.NoteOn(midi, Mathf.Clamp01(velocity), Mathf.Max(0.01f, length));

            if (verboseLogs)
                Debug.Log($"[PianoRollPainter] HELM NoteOn valueId={valueId} midi={midi} len={length:0.000}");
        }

        private static int MidiFromValueIdChromatic(int valueId, int baseMidi = 60)
        {
            int idx = Mathf.Max(0, valueId - 1);
            int midi = baseMidi + idx;
            return Mathf.Clamp(midi, 0, 127);
        }

        private void AuditionValueId(int valueId, int pianoRowHint = -1, int pianoColHint = -1)
        {
            if (_helm == null) ResolveMainSynthRefs();
            if (_helm == null) return;

            string lbl = _labelByValue.TryGetValue(valueId, out var s) ? s : null;

            int r = pianoRowHint;
            if (string.IsNullOrEmpty(lbl) && _piano != null)
            {
                int idx = valueId - 1;
                r = idx / _piano.ColCount;
                int c = idx % _piano.ColCount;
                if (r >= 0 && r < _piano.RowCount)
                    lbl = _piano.GetCellLabel(r, c);
            }

            if (string.IsNullOrEmpty(lbl)) return;

            int midi = MidiFromLabel(lbl, r >= 0 ? r : 0);
            if (midi < 0) return;

            _helm.NoteOn(midi, 1.0f, 0.18f);
        }

        private void TryAuditionSynthNote(int r, int c)
        {
            if (_piano == null) return;

            if (_helm == null) ResolveMainSynthRefs();
            var h = _helm;
            if (h == null)
            {
                Debug.LogWarning("[PianoRollPainter] Main HelmController not found outside PadSynth.");
                return;
            }

            int valueId = _piano.GetCellValueId(r, c);
            int midi = Mathf.Clamp(60 + (valueId - 1), 0, 127);

            Debug.Log($"[PianoRollPainter] MAIN HELM NoteOn midi={midi} valueId={valueId} channel={h.channel}");
            h.NoteOn(midi, 1.0f, 0.18f);
        }

        private static int MidiFromValueIdChromatic(int valueId, int cols, int baseMidi = 60)
        {
            int idx = Mathf.Max(0, valueId - 1);
            int midi = baseMidi + idx;
            return Mathf.Clamp(midi, 0, 127);
        }

        private static int MidiFromLabel(string label, int row)
        {
            int semitone;
            switch (label)
            {
                case "C": semitone = 0; break;
                case "D": semitone = 2; break;
                case "E": semitone = 4; break;
                case "F": semitone = 5; break;
                case "G": semitone = 7; break;
                case "A": semitone = 9; break;
                case "B": semitone = 11; break;
                default: return -1;
            }

            int octave = (row < 3) ? 5 : 4;
            return (octave + 1) * 12 + semitone;
        }

        private void ResolveMainSynthRefs()
        {
            var excludedRoot = FindSceneObjectByName(excludedRootName);

            bool seqInvalid = helmSequencer == null || IsInHierarchy(helmSequencer.gameObject, excludedRoot);
            if (seqInvalid)
                helmSequencer = FindFirstSceneComponentOutsideRoot<HelmSequencer>(excludedRoot);

            HelmController linked = FindLinkedController(helmSequencer);
            bool helmInvalid = helm == null || IsInHierarchy(helm.gameObject, excludedRoot);
            if (linked != null && !IsInHierarchy(linked.gameObject, excludedRoot))
                helm = linked;
            else if (helmInvalid)
                helm = FindFirstSceneComponentOutsideRoot<HelmController>(excludedRoot);

            _helm = (helm != null && !IsInHierarchy(helm.gameObject, excludedRoot)) ? helm : null;

            if (_helm != null && helmSequencer != null)
            {
                int ch = (helmChannelOverride >= 0) ? helmChannelOverride : _helm.channel;
                helmSequencer.channel = ch;
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

        private void SaveStepGrid()
        {
            if (_step == null) return;

            int[] flat = _step.ExportValuesFlat();
            if (flat == null) return;

            CacheSeqFlat(flat);

            if (_allowSaving)
            {
                PersistSeqFlat(flat);

                int total = _step.RowCount * _step.ColCount;
                int[] runStarts = new int[total];

                EnsureRunArrays();

                for (int i = 0; i < total; i++)
                    runStarts[i] = (_noteRunLengthAtStart != null && i < _noteRunLengthAtStart.Length)
                        ? _noteRunLengthAtStart[i]
                        : 0;

                KMusic.KMusicSaveState.SaveIntArray(PrefKey_SeqStepGrid + ".runs", runStarts);
                ProjectPrefs.Save();
            }

            EnsureRunArrays();

            int rows = _step.RowCount;
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

                Debug.Log($"[SEQ SAVE] row={r} col={c} len={len} value={flat[i]}");
            }
        }
        private int ExpectedSeqFlatLength()
        {
            return _step != null ? _step.RowCount * _step.ColCount : 16;
        }

        private void CacheSeqFlat(int[] flat)
        {
            int len = ExpectedSeqFlatLength();
            if (len <= 0) len = 16;

            if (flat == null || flat.Length != len)
            {
                _cachedSeqStepsFlat = new int[len];
                _hasCachedSeqSteps = true;
                return;
            }

            _cachedSeqStepsFlat = (int[])flat.Clone();
            _hasCachedSeqSteps = true;
        }

        private void PersistSeqFlat(int[] flat)
        {
            if (flat == null) return;
            KMusic.KMusicSaveState.SaveIntArray(PrefKey_SeqStepGrid, flat);
        }

        private void LoadStepGrid()
        {
            if (_step == null) return;

            int expected = _step.RowCount * _step.ColCount;
            int[] flat = KMusic.KMusicSaveState.LoadIntArray(PrefKey_SeqStepGrid, expected);

            if (flat == null || flat.Length != expected)
            {
                _step.ClearAll();
                _step.RefreshAll();

                EnsureRunArrays();
                for (int i = 0; i < _noteRunLengthAtStart.Length; i++)
                {
                    _noteRunLengthAtStart[i] = 0;
                    _noteRunStartForCell[i] = -1;
                }

                return;
            }

            int[] runs = KMusic.KMusicSaveState.LoadIntArray(PrefKey_SeqStepGrid + ".runs", expected);
            ApplySeqStepsFlat(flat, runs);
        }
        private void CacheValue(int valueId, string label, Color color)
        {
            if (valueId <= 0) return;
            _labelByValue[valueId] = label ?? valueId.ToString();
            _colorByValue[valueId] = color;
        }

        private string FormatValueLabel(int v)
        {
            if (v <= 0) return "";
            return _labelByValue.TryGetValue(v, out var s) ? s : v.ToString();
        }

        private Color TintForValue(int v)
        {
            if (v <= 0) return new Color(0, 0, 0, 0);

            if (_colorByValue.TryGetValue(v, out var c))
                return c;

            if (_piano != null)
            {
                int idx = v - 1;
                int r = idx / _piano.ColCount;
                int col = idx % _piano.ColCount;
                if (r >= 0 && r < _piano.RowCount)
                    return _piano.GetCellColor(r, col);
            }

            return Color.white;
        }

        // ----------------------------
        // CHAIN helpers
        // ----------------------------

        public void SetAllowSaving(bool on) => _allowSaving = on;

        public int[] CaptureSeqStepsFlat()
        {
            if (_step != null)
            {
                var flat = _step.ExportValuesFlat();
                CacheSeqFlat(flat);
                return flat;
            }

            if (_hasCachedSeqSteps && _cachedSeqStepsFlat != null)
                return (int[])_cachedSeqStepsFlat.Clone();

            return null;
        }

        public void ApplySeqStepsFlat(int[] flat)
        {
            int expected = ExpectedSeqFlatLength();
            int[] runs = KMusic.KMusicSaveState.LoadIntArray(PrefKey_SeqStepGrid + ".runs", expected);
            ApplySeqStepsFlat(flat, runs);
        }

        public void ApplySeqStepsFlat(int[] flat, int[] runs)
        {
            CacheSeqFlat(flat);
            _preferCachedOnNextBind = true;

            if (_step == null) return;

            EnsureRunArrays();

            for (int i = 0; i < _noteRunLengthAtStart.Length; i++)
            {
                _noteRunLengthAtStart[i] = 0;
                _noteRunStartForCell[i] = -1;
            }

            if (flat == null || flat.Length != _step.RowCount * _step.ColCount)
            {
                _step.ClearAll();
                _step.RefreshAll();
                _sequenceBuilt = false;
                return;
            }

            _step.ImportValuesFlat(flat, fireEvent: false);
            _step.RefreshAll();

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
                    Debug.Log($"[SEQ LOAD] row={r} col={c} len={len} value={value}");
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
                        Debug.Log($"[SEQ LOAD] row={r} col={c} len=1 value={flat[i]}");
                    }
                }
            }

            if (_allowSaving)
            {
                PersistSeqFlat(flat);

                int[] runStarts = new int[total];
                for (int i = 0; i < total; i++)
                    runStarts[i] = _noteRunLengthAtStart[i];

                KMusic.KMusicSaveState.SaveIntArray(PrefKey_SeqStepGrid + ".runs", runStarts);
                ProjectPrefs.Save();
            }

            _sequenceBuilt = false;
        }        public void RebuildSynthSequenceNow()
        {
            if (helmSequencer == null) return;
            RebuildHelmSequenceFromGrid();
            helmSequencer.enabled = false;
            helmSequencer.enabled = true;
        }
    }
}