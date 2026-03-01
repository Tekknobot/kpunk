using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using AudioHelm;

namespace KMusic.UI
{
    /// <summary>
    /// Bridges PianoRollGrid (note picker) -> StepGrid (paint target).
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class KMusicPianoRollStepPainter : MonoBehaviour
    {
        private const string PrefKey_SeqStepGrid = "kmusic.seq.stepgrid";

        [Header("UXML element names")]
        public string pianoRollName = "SeqPianoRoll";
        public string stepGridName = "StepGrid";

        [Header("Debug")]
        public bool verboseLogs = false;

        private UIDocument _doc;
        private VisualElement _root;
        private PianoRollGrid _piano;
        private StepGrid _step;

        private IVisualElementScheduledItem _rebindLoop;
        private StepGrid _lastStep;
        private PianoRollGrid _lastPiano;

        private HelmController _helm;

        // valueId -> label/color
        private readonly Dictionary<int, string> _labelByValue = new();
        private readonly Dictionary<int, Color> _colorByValue = new();

        private KMusicDrumSequencer _drumSeq;
        private int _lastTransportStep = -999;
        private float _lastBpm = -1f;

        [SerializeField] private HelmController helm;

        private void Update()
        {
            // Sync Helm playback to the drum transport (your Play button drives that).
            if (_step == null) return;

            if (_drumSeq == null)
                _drumSeq = FindObjectOfType<KMusicDrumSequencer>();

            if (_drumSeq == null) return;
            if (!_drumSeq.IsPlaying) { _lastTransportStep = -999; return; }

            int stepIndex = _drumSeq.CurrentStepIndex; // 0..15
            if (stepIndex == _lastTransportStep) return;
            _lastTransportStep = stepIndex;

            // Read the StepGrid value at this step (this is the stored "note id" from piano roll).
            int r = stepIndex / _step.ColCount; // should be 2 rows, 8 cols
            int c = stepIndex % _step.ColCount;

            int valueId = _step.GetValue(r, c);
            if (valueId <= 0) return;

            // Compute tempo-based length so it feels musical.
            float bpm = GetTempoBpm();
            float stepDur = 60f / Mathf.Max(1f, bpm) / 4f;      // 16th note
            float len = Mathf.Clamp(stepDur * 0.9f, 0.05f, 0.25f);

            PlayHelmValueId(valueId, velocity: 1.0f, length: len);
        }

        private void OnEnable()
        {
            _doc = GetComponent<UIDocument>();
            _helm = FindObjectOfType<HelmController>();
            _drumSeq = FindObjectOfType<KMusicDrumSequencer>();

            if (_doc == null) return;

            var root = _doc.rootVisualElement;
            if (root == null) return;

            _rebindLoop?.Pause();
            _rebindLoop = root.schedule.Execute(() =>
            {
                var piano = root.Q<PianoRollGrid>(pianoRollName);
                var step  = root.Q<StepGrid>(stepGridName);
                if (piano == null || step == null) return;

                // only rebind if instances changed (UI rebuilt)
                if (piano == _lastPiano && step == _lastStep) return;
                _lastPiano = piano;
                _lastStep = step;

                Unbind();
                _piano = piano;
                _step = step;

                _piano.EnablePickerMode(true);
                _step.EnableValueLabels(true, FormatValueLabel);
                _step.EnableValueTint(true, TintForValue);

                _piano.OnCellPicked += OnPianoPicked;
                _step.OnCellClicked += OnStepClicked;

                // default brush
                int v0 = _piano.GetCellValueId(0, 0);
                CacheValue(v0, _piano.GetCellLabel(0, 0), _piano.GetCellColor(0, 0));
                _step.SetPaintValue(v0);

                // LOAD after wiring
                LoadStepGrid();

                // reset transport tracking so first tick plays correctly
                _lastTransportStep = -999;

            }).Every(100);
        }
        private IEnumerator BindWhenReady()
        {
            yield return null;
            yield return null;

            while (!Bind())
                yield return null;
        }

        private void OnDisable()
        {
            SaveStepGrid();
            _rebindLoop?.Pause();
            Unbind();
        }
        private bool Bind()
        {
            if (_doc == null) return false;
            _root = _doc.rootVisualElement;
            if (_root == null) return false;

            _piano = _root.Q<PianoRollGrid>(pianoRollName);
            _step  = _root.Q<StepGrid>(stepGridName);

            if (verboseLogs)
                Debug.Log($"[PianoRollPainter] piano found={_piano!=null} step found={_step!=null}");

            if (_piano == null || _step == null)
                return false;

            _piano.EnablePickerMode(true);

            _step.EnableValueLabels(true, FormatValueLabel);
            _step.EnableValueTint(true, TintForValue);

            _piano.OnCellPicked += OnPianoPicked;
            _step.OnCellClicked += OnStepClicked;

            // ✅ CRITICAL: cache the ENTIRE palette so loaded steps know their label/color
            CacheAllPaletteValues();

            // default brush
            int v0 = _piano.GetCellValueId(0, 0);
            _step.SetPaintValue(v0);

            LoadStepGrid();
            return true;
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
            if (_piano != null) _piano.OnCellPicked -= OnPianoPicked;
            if (_step != null) _step.OnCellClicked -= OnStepClicked;
            _piano = null;
            _step = null;
            _root = null;
        }

        private void OnPianoPicked(int r, int c, string label, int valueId)
        {
            if (_step == null || _piano == null) return;

            CacheValue(valueId, label, _piano.GetCellColor(r, c));
            _step.SetPaintValue(valueId);

            // ALWAYS audition on pick (and prove it in logs)
            if (_helm == null) _helm = FindObjectOfType<HelmController>();
            Debug.Log($"[PianoRollPainter] PICK r={r} c={c} label={label} valueId={valueId} helm={(_helm!=null)}");

            TryAuditionSynthNote(r, c);

            if (verboseLogs)
                Debug.Log($"Picked {label} -> brush={valueId}");
        }

        private void OnStepClicked(int r, int c)
        {
            if (_step == null) return;

            int v = _step.GetValue(r, c);
            if (v <= 0) return;

            _step.SetPaintValue(v);

            // preview the stored note when clicking a painted step
            PlayHelmValueId(v, velocity: 1.0f, length: 0.18f);

            if (verboseLogs)
                Debug.Log($"Step clicked -> brush={v}");
        }

        private float GetTempoBpm()
        {
            // Try to match whatever your drum sequencer uses (KMusicApp Bus "tempo").
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

            // Chromatic mapping: valueId(1..48) => MIDI from C4 upward.
            int midi = MidiFromValueIdChromatic(valueId, baseMidi: 60);
            _helm.NoteOn(midi, Mathf.Clamp01(velocity), Mathf.Max(0.01f, length));

            if (verboseLogs)
                Debug.Log($"[PianoRollPainter] HELM NoteOn valueId={valueId} midi={midi} len={length:0.000}");
        }

        private static int MidiFromValueIdChromatic(int valueId, int baseMidi = 60)
        {
            int idx = Mathf.Max(0, valueId - 1);      // 0..47
            int midi = baseMidi + idx;                // chromatic steps
            return Mathf.Clamp(midi, 0, 127);
        }

        private void AuditionValueId(int valueId, int pianoRowHint = -1, int pianoColHint = -1)
        {
            if (_helm == null) _helm = FindObjectOfType<HelmController>();
            if (_helm == null) return;

            // We prefer the cached label because it matches your palette.
            string lbl = _labelByValue.TryGetValue(valueId, out var s) ? s : null;

            // If cache missing, derive from id -> (r,c)
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

        // Maps your 6x8 palette to a real chromatic keyboard.
        // valueId is 1-based (from PianoRollGrid.GetCellValueId).
        // baseMidi = the MIDI note for palette cell (row 0, col 0). C4 = 60.
        private static int MidiFromValueIdChromatic(int valueId, int cols, int baseMidi = 60)
        {
            int idx = Mathf.Max(0, valueId - 1);     // 0..(rows*cols-1)
            int midi = baseMidi + idx;               // +1 semitone per cell
            return Mathf.Clamp(midi, 0, 127);
        }

        // Map the A..G labels from your 6x8 palette to a musically sensible MIDI range.
        // Top half is a bit higher so it feels responsive.
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

            int octave = (row < 3) ? 5 : 4; // C5..B5 then C4..B4
            return (octave + 1) * 12 + semitone;
        }

        private void SaveStepGrid()
        {
            if (_step == null) return;
            KMusic.KMusicSaveState.SaveIntArray(PrefKey_SeqStepGrid, _step.ExportValuesFlat());
        }

        private void LoadStepGrid()
        {
            if (_step == null) return;
            var v = KMusic.KMusicSaveState.LoadIntArray(PrefKey_SeqStepGrid, _step.RowCount * _step.ColCount);
            if (v == null) return;
            _step.ImportValuesFlat(v, fireEvent: false);
            _step.RefreshAll();

            Debug.Log($"LOAD {PrefKey_SeqStepGrid} hasKey={PlayerPrefs.HasKey(PrefKey_SeqStepGrid)}");
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

            // If cache exists, use it
            if (_colorByValue.TryGetValue(v, out var c))
                return c;

            // Safety fallback: try to recover from valueId -> (r,c)
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
    }
}