using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace KMusic.UI
{
    /// <summary>
    /// Bridges PianoRollGrid (note picker) -> StepGrid (paint target).
    /// - Click/drag on PianoRollGrid selects a note (valueId)
    /// - Left-click/drag on StepGrid paints that valueId
    /// - Right-click/drag OR Ctrl/Cmd on StepGrid erases (handled inside StepGrid)
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

        // valueId -> label/color (cached from piano picks)
        private readonly Dictionary<int, string> _labelByValue = new Dictionary<int, string>();
        private readonly Dictionary<int, Color> _colorByValue = new Dictionary<int, Color>();

        private void OnEnable()
        {
            _doc = GetComponent<UIDocument>();
            StartCoroutine(BindWhenReady());
        }

        private IEnumerator BindWhenReady()
        {
            // wait for KMusicApp to assign visualTreeAsset + for UI Toolkit to clone it
            yield return null;
            yield return null;

            while (!Bind())
                yield return null;
        }

        private void OnDisable()
        {
            Unbind();
        }

        private void TryBindNextFrame()
        {
            if (_doc == null) return;
            _root = _doc.rootVisualElement;
            if (_root == null)
            {
                // If panel not ready yet, retry shortly.
                Invoke(nameof(TryBindNextFrame), 0.05f);
                return;
            }

            // schedule to end of frame to ensure UXML cloned
            _root.schedule.Execute(() =>
            {
                if (!Bind()) Invoke(nameof(TryBindNextFrame), 0.05f);
            }).StartingIn(0);
        }

        private bool Bind()
        {
            if (_doc == null) return false;
            _root = _doc.rootVisualElement;
            if (_root == null) return false;

            _piano = _root.Q<PianoRollGrid>(pianoRollName);
            _step  = _root.Q<StepGrid>(stepGridName);

            _step.OnCellValueChanged += (r, c, v) =>
                Debug.Log($"[PR->Step] Painted cell[{r},{c}]={v}");
                
            if (verboseLogs)
                Debug.Log($"[KMusicPianoRollStepPainter] bind root={(_root!=null)} piano='{pianoRollName}' found={(_piano!=null)} step='{stepGridName}' found={(_step!=null)}");

            if (_piano == null || _step == null)
                return false;

            // Put piano into picker mode (no self-painting)
            _piano.EnablePickerMode(true);

            // StepGrid: show labels + tint based on valueId
            _step.EnableValueLabels(true, FormatValueLabel);
            _step.EnableValueTint(true, TintForValue);

            // Hook events
            _piano.OnCellPicked += OnPianoPicked;
            _step.OnCellClicked += OnStepClicked;

            // Prime with a default selection (top-left)
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
                Debug.Log($"[KMusicPianoRollStepPainter] picked r={r} c={c} label='{label}' valueId={valueId} -> SetPaintValue");
        }

        private void OnStepClicked(int r, int c)
        {
            if (_step == null) return;

            // If user clicks an existing painted cell, switch brush to that value.
            // (StepGrid stores values; 0 means empty)
            int v = _step.GetValue(r, c);
            if (v <= 0) return;

            _step.SetPaintValue(v);

            if (verboseLogs)
                Debug.Log($"[KMusicPianoRollStepPainter] step clicked r={r} c={c} -> brush={v}");
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
            return _labelByValue.TryGetValue(v, out var s) ? (s ?? "") : v.ToString();
        }

        private Color TintForValue(int v)
        {
            if (v <= 0) return new Color(0, 0, 0, 0);
            return _colorByValue.TryGetValue(v, out var c) ? c : Color.white;
        }
    }
}
