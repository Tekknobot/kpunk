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

        // Gesture tracking so taps stay single notes, drags become held notes
        private readonly List<int> _gestureCells = new();
        private int _gestureValueId = -1;
        private float _lastGestureTime = -999f;
        private int _lastGestureIndex = -999;
        private IVisualElementScheduledItem _gestureFinalizeJob;

        private const float GestureGapSeconds = 0.40f;

        private void Update()
        {
            if (_drumSeq == null)
                _drumSeq = FindObjectOfType<KMusicDrumSequencer>();

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
            _helm = FindObjectOfType<HelmController>();
            _drumSeq = FindObjectOfType<KMusicDrumSequencer>();
            _chainUI = FindObjectOfType<KMusicChainUI>();

            if (helmSequencer == null)
                helmSequencer = FindObjectOfType<HelmSequencer>();

            if (helmSequencer == null)
            {
                var go = new GameObject("KMusicHelmSequencer");
                helmSequencer = go.AddComponent<HelmSequencer>();
            }

            int ch = (helmChannelOverride >= 0) ? helmChannelOverride : (_helm != null ? _helm.channel : 0);
            helmSequencer.channel = ch;
            helmSequencer.length = 16;
            helmSequencer.loop = true;
            helmSequencer.division = Sequencer.Division.kSixteenth;

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

        private bool IsAdjacentStep(int a, int b)
        {
            return Mathf.Abs(a - b) == 1;
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

        private void SetDraggedRun(List<int> cells, int valueId)
        {
            EnsureRunArrays();
            if (cells == null || cells.Count == 0) return;

            cells.Sort();

            var contiguous = new List<int>();
            int prev = -999;

            foreach (int idx in cells)
            {
                FromIndex(idx, out int r, out int c);
                if (_step.GetValue(r, c) != valueId)
                    continue;

                if (contiguous.Count == 0 || IsAdjacentStep(idx, prev))
                {
                    contiguous.Add(idx);
                    prev = idx;
                }
            }

            if (contiguous.Count == 0) return;

            foreach (int idx in contiguous)
                ClearRunAtOrContaining(idx);

            int start = contiguous[0];
            int len = contiguous.Count;

            _noteRunLengthAtStart[start] = len;
            for (int i = 0; i < len; i++)
                _noteRunStartForCell[start + i] = start;
        }

        private void FinalizeGesture()
        {
            if (_gestureCells.Count == 0) return;

            var unique = new HashSet<int>(_gestureCells);
            var ordered = new List<int>(unique);
            ordered.Sort();

            if (ordered.Count == 1)
            {
                int idx = ordered[0];
                FromIndex(idx, out int r, out int c);
                if (_step.GetValue(r, c) > 0)
                    SetSingleTapNote(idx);
            }
            else
            {
                SetDraggedRun(ordered, _gestureValueId);
            }

            _gestureCells.Clear();
            _gestureValueId = -1;
            _lastGestureIndex = -999;
            _lastGestureTime = -999f;

            _seqDirty = true;
        }

        private void RestartGestureFinalizeTimer()
        {
            _gestureFinalizeJob?.Pause();
            if (_step == null) return;

            _gestureFinalizeJob = _step.schedule.Execute(() =>
            {
                FinalizeGesture();
            }).StartingIn((long)(GestureGapSeconds * 1000f));
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
            FinalizeGesture();
            SaveStepGrid();
            _gestureFinalizeJob?.Pause();
            _rebindLoop?.Pause();
            Unbind();
        }

        private void OnStepValueChanged(int r, int c, int v)
        {
            EnsureRunArrays();

            int index = ToIndex(r, c);
            float now = Time.unscaledTime;

            _seqDirty = true;

            if (v <= 0)
            {
                ClearRunAtOrContaining(index);
                SaveStepGrid();

                if (_chainUI == null) _chainUI = FindObjectOfType<KMusicChainUI>();
                _chainUI?.NotifyLiveEdited();
                return;
            }

            bool continuesGesture =
                _gestureCells.Count > 0 &&
                v == _gestureValueId &&
                (now - _lastGestureTime) <= GestureGapSeconds &&
                IsAdjacentStep(index, _lastGestureIndex);

            if (!continuesGesture)
            {
                FinalizeGesture();
                _gestureCells.Clear();
                _gestureValueId = v;
            }

            if (!_gestureCells.Contains(index))
                _gestureCells.Add(index);

            _gestureValueId = v;
            _lastGestureIndex = index;
            _lastGestureTime = now;

            RestartGestureFinalizeTimer();

            SaveStepGrid();

            if (_chainUI == null) _chainUI = FindObjectOfType<KMusicChainUI>();
            _chainUI?.NotifyLiveEdited();

            PlayHelmValueId(v, 1.0f, 0.18f);
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
            }

            _seqRebuildJob?.Pause();
            _seqRebuildJob = null;
            _seqDirty = false;

            _gestureFinalizeJob?.Pause();
            _gestureFinalizeJob = null;
            _gestureCells.Clear();
            _gestureValueId = -1;
            _lastGestureIndex = -999;
            _lastGestureTime = -999f;

            _piano = null;
            _step = null;
            _root = null;
        }

        private void OnPianoPicked(int r, int c, string label, int valueId)
        {
            if (_step == null || _piano == null) return;

            CacheValue(valueId, label, _piano.GetCellColor(r, c));
            _step.SetPaintValue(valueId);

            if (_helm == null) _helm = FindObjectOfType<HelmController>();
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
                _helm = FindObjectOfType<HelmController>();

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
            if (_helm == null) _helm = FindObjectOfType<HelmController>();
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

            var h = helm != null ? helm : FindObjectOfType<HelmController>();
            if (h == null)
            {
                Debug.LogWarning("[PianoRollPainter] HelmController NOT FOUND - add one to the scene or assign it in inspector.");
                return;
            }

            int valueId = _piano.GetCellValueId(r, c);
            int midi = Mathf.Clamp(60 + (valueId - 1), 0, 127);

            Debug.Log($"[PianoRollPainter] HELM NoteOn midi={midi} valueId={valueId} channel={h.channel}");
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

        private void SaveStepGrid()
        {
            if (!_allowSaving) return;
            if (_step == null) return;

            var flat = _step.ExportValuesFlat();
            CacheSeqFlat(flat);
            PersistSeqFlat(flat);
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

            var v = KMusic.KMusicSaveState.LoadIntArray(PrefKey_SeqStepGrid, _step.RowCount * _step.ColCount);
            if (v == null) return;

            CacheSeqFlat(v);

            EnsureRunArrays();

            for (int i = 0; i < _noteRunLengthAtStart.Length; i++)
            {
                _noteRunLengthAtStart[i] = 0;
                _noteRunStartForCell[i] = -1;
            }

            _step.ImportValuesFlat(v, fireEvent: false);
            _step.RefreshAll();

            // Default imported notes to individual taps.
            for (int i = 0; i < v.Length; i++)
            {
                if (v[i] > 0)
                {
                    _noteRunLengthAtStart[i] = 1;
                    _noteRunStartForCell[i] = i;
                }
            }

            Debug.Log($"LOAD {PrefKey_SeqStepGrid} hasKey={ProjectPrefs.HasKey(PrefKey_SeqStepGrid)}");
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
            CacheSeqFlat(flat);
            _preferCachedOnNextBind = true;

            if (flat != null && flat.Length == ExpectedSeqFlatLength())
                PersistSeqFlat(flat);

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
                return;
            }

            _step.ImportValuesFlat(flat, fireEvent: false);
            _step.RefreshAll();

            for (int i = 0; i < flat.Length; i++)
            {
                if (flat[i] > 0)
                {
                    _noteRunLengthAtStart[i] = 1;
                    _noteRunStartForCell[i] = i;
                }
            }
        }

        public void RebuildSynthSequenceNow()
        {
            if (helmSequencer == null) return;
            FinalizeGesture();
            RebuildHelmSequenceFromGrid();
            helmSequencer.enabled = false;
            helmSequencer.enabled = true;
        }
    }
}