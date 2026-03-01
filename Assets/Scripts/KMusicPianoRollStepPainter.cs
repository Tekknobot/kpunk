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

        private void OnEnable()
        {
            _doc = GetComponent<UIDocument>();
            _helm = FindObjectOfType<HelmController>();
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

            // picker mode = choose value, not paint piano grid
            _piano.EnablePickerMode(true);

            // show labels + tint on StepGrid
            _step.EnableValueLabels(true, FormatValueLabel);
            _step.EnableValueTint(true, TintForValue);

            _piano.OnCellPicked += OnPianoPicked;
            _step.OnCellClicked += OnStepClicked;

            // default brush
            int v0 = _piano.GetCellValueId(0, 0);
            CacheValue(v0, _piano.GetCellLabel(0, 0), _piano.GetCellColor(0, 0));
            _step.SetPaintValue(v0);

            // Restore saved sequencer pattern (note ids) if any.
            LoadStepGrid();

            return true;
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

            // Audition the picked note on the synth.
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

            if (verboseLogs)
                Debug.Log($"Step clicked -> brush={v}");
        }

        private void TryAuditionSynthNote(int r, int c)
        {
            if (_helm == null) _helm = FindObjectOfType<HelmController>();
            if (_helm == null) return;

            string lbl = _piano != null ? _piano.GetCellLabel(r, c) : null;
            if (string.IsNullOrEmpty(lbl)) return;

            int midi = MidiFromLabel(lbl, r);
            if (midi < 0) return;

            // Short note so it feels like a "preview".
            _helm.NoteOn(midi, 1.0f, 0.18f);
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
            if (v <= 0) return new Color(0,0,0,0);
            return _colorByValue.TryGetValue(v, out var c) ? c : Color.white;
        }
    }
}