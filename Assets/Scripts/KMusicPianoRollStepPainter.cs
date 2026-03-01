using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace KMusic.UI
{
    /// <summary>
    /// Bridges PianoRollGrid (note picker) -> StepGrid (paint target).
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class KMusicPianoRollStepPainter : MonoBehaviour
    {
        [Header("UXML element names")]
        public string pianoRollName = "SeqPianoRoll";
        public string stepGridName = "StepGrid";

        [Header("Debug")]
        public bool verboseLogs = false;

        private UIDocument _doc;
        private VisualElement _root;
        private PianoRollGrid _piano;
        private StepGrid _step;

        // valueId -> label/color
        private readonly Dictionary<int, string> _labelByValue = new();
        private readonly Dictionary<int, Color> _colorByValue = new();

        private void OnEnable()
        {
            _doc = GetComponent<UIDocument>();
            StartCoroutine(BindWhenReady());
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