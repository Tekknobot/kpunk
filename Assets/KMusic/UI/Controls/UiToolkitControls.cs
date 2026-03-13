// UiToolkitControls.cs

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using KMusic.Core;

namespace KMusic.UI
{
    public interface IParamBindable
    {
        void Bind(ParameterBus bus);
    }

    // --- Knob ---
    public class KnobElement : VisualElement, IParamBindable
    {
        public new class UxmlFactory : UxmlFactory<KnobElement, UxmlTraits> { }

        public new class UxmlTraits : VisualElement.UxmlTraits
        {
            UxmlStringAttributeDescription _label = new() { name = "label", defaultValue = "" };
            UxmlStringAttributeDescription _paramId = new() { name = "paramId", defaultValue = "" };
            public override void Init(VisualElement ve, IUxmlAttributes bag, CreationContext cc)
            {
                base.Init(ve, bag, cc);
                var k = (KnobElement)ve;
                k.Label = _label.GetValueFromBag(bag, cc);
                k.ParamId = _paramId.GetValueFromBag(bag, cc);
                k.Build();
            }
        }

        public string Label { get; set; } = "";
        public string ParamId { get; set; } = "";

        private ParameterBus _bus;
        private VisualElement _ring;
        private Label _name;
        private Label _value;
        private float _startT;
        private Vector2 _startPos;
        private bool _drag;

        public KnobElement()
        {
            style.width = 98;
            style.height = 200;
            style.marginRight = 12;

            // ✅ ensures knob contents align cleanly
            style.alignItems = Align.Center;
            style.justifyContent = Justify.FlexStart;
        }

        private void Build()
        {
            Clear();

            var knob = new VisualElement();
            knob.style.width = 85;
            knob.style.height = 85;

            knob.style.minWidth = 85;
            knob.style.minHeight = 85;
            knob.style.flexShrink = 0;
            knob.style.alignSelf = Align.Center; // optional but helps

            knob.style.borderTopLeftRadius = 999;
            knob.style.borderTopRightRadius = 999;
            knob.style.borderBottomLeftRadius = 999;
            knob.style.borderBottomRightRadius = 999;
            knob.style.backgroundColor = new Color(0.11f, 0.12f, 0.16f, 1f);
            knob.style.borderBottomWidth = 3;
            knob.style.borderLeftWidth = 3;
            knob.style.borderRightWidth = 3;
            knob.style.borderTopWidth = 3;
            knob.style.borderBottomColor = new Color(0.14f, 0.16f, 0.22f, 1f);
            knob.style.borderTopColor = new Color(0.14f, 0.16f, 0.22f, 1f);
            knob.style.borderLeftColor = new Color(0.14f, 0.16f, 0.22f, 1f);
            knob.style.borderRightColor = new Color(0.14f, 0.16f, 0.22f, 1f);

            _ring = new VisualElement();
            _ring.style.position = Position.Absolute;
            _ring.style.left = 0;
            _ring.style.top = 0;
            _ring.style.width = 80;
            _ring.style.height = 80;
            knob.Add(_ring);

            _name = new Label(Label);
            _name.style.unityTextAlign = TextAnchor.MiddleCenter;
            _name.style.color = new Color(0.60f, 0.64f, 0.70f, 1f);
            _name.style.fontSize = 20;
            _name.style.marginTop = 8;

            _value = new Label("--");
            _value.style.unityTextAlign = TextAnchor.MiddleCenter;
            _value.style.color = new Color(0.91f, 0.92f, 0.93f, 1f);
            _value.style.fontSize = 20;

            Add(knob);
            Add(_name);
            Add(_value);

            RegisterCallback<PointerDownEvent>(OnDown);
            RegisterCallback<PointerMoveEvent>(OnMove);
            RegisterCallback<PointerUpEvent>(OnUp);
        }

        public void Bind(ParameterBus bus)
        {
            _bus = bus;
            if (_bus != null)
                _bus.OnChanged += OnParamChanged;
            Refresh();
        }

        private void OnParamChanged(string id, float v)
        {
            if (id != ParamId) return;
            Refresh();
        }

        private void Refresh()
        {
            if (_bus == null || string.IsNullOrEmpty(ParamId)) return;
            float t = _bus.GetNormalized(ParamId);
            DrawArc(t);
            if (_bus.TryGet(ParamId, out var p))
                _value.text = p.Format();
            else
                _value.text = $"{Mathf.RoundToInt(t * 100)}%";
        }

        private void OnDown(PointerDownEvent e)
        {
            if (_bus == null || string.IsNullOrEmpty(ParamId)) return;
            _drag = true;
            _startPos = e.position;
            _startT = _bus.GetNormalized(ParamId);
            this.CapturePointer(e.pointerId);
            e.StopPropagation();

            // Double click reset
            if (e.clickCount >= 2 && _bus.TryGet(ParamId, out var p))
            {
                if (ParamId == "sample.pitch")
                    _bus.SetValue(ParamId, 0f);
                else
                    _bus.SetValue(ParamId, p.Default);

                Refresh();
            }
        }

        private void OnMove(PointerMoveEvent e)
        {
            if (!_drag || _bus == null || string.IsNullOrEmpty(ParamId)) return;

            float dy = (_startPos.y - e.position.y);
            float speed = e.shiftKey ? 0.0008f : 0.0030f;
            float t = Mathf.Clamp01(_startT + dy * speed);

            if (_bus.TryGet(ParamId, out var p))
            {
                float value = Mathf.Lerp(p.Min, p.Max, t);

                if (ParamId == "sample.pitch")
                {
                    value = Mathf.Round(value);
                    value = Mathf.Clamp(value, -12f, 12f);
                }

                _bus.SetValue(ParamId, value);
            }
            else
            {
                _bus.SetNormalized(ParamId, t);
            }

            Refresh();
        }

        private void OnUp(PointerUpEvent e)
        {
            if (!_drag) return;
            _drag = false;
            this.ReleasePointer(e.pointerId);
            e.StopPropagation();
        }

        private void DrawArc(float t)
        {
            t = Mathf.Clamp01(t);
            _ring.Clear();

            // subtle ring
            var ring = new VisualElement();
            ring.style.position = Position.Absolute;
            ring.style.left = 6;
            ring.style.top = 6;
            ring.style.width = 68;
            ring.style.height = 68;
            ring.style.borderTopLeftRadius = 999;
            ring.style.borderTopRightRadius = 999;
            ring.style.borderBottomLeftRadius = 999;
            ring.style.borderBottomRightRadius = 999;
            ring.style.borderLeftWidth = 3;
            ring.style.borderRightWidth = 3;
            ring.style.borderTopWidth = 3;
            ring.style.borderBottomWidth = 3;
            ring.style.borderLeftColor = new Color(1, 1, 1, 0.06f);
            ring.style.borderRightColor = new Color(1, 1, 1, 0.06f);
            ring.style.borderTopColor = new Color(1, 1, 1, 0.06f);
            ring.style.borderBottomColor = new Color(1, 1, 1, 0.06f);
            _ring.Add(ring);

            // ✅ pointer tick centered + stable rotation
            var tick = new VisualElement();
            tick.style.position = Position.Absolute;
            tick.style.width = 6;
            tick.style.height = 18;

            // center tick in 80x80, place near top
            tick.style.left = (80 - 6) * 0.5f;
            tick.style.top = 8;

            tick.style.backgroundColor = new Color(0.41f, 0.89f, 1f, 1f);
            tick.style.borderTopLeftRadius = 3;
            tick.style.borderTopRightRadius = 3;
            tick.style.borderBottomLeftRadius = 3;
            tick.style.borderBottomRightRadius = 3;

            // rotate around knob center-ish
            float ang = Mathf.Lerp(-135f, 135f, t);
            tick.style.transformOrigin = new TransformOrigin(3, 32, 0);
            tick.style.rotate = new StyleRotate(new Rotate(new Angle(ang, AngleUnit.Degree)));

            _ring.Add(tick);
        }
    }

    // --- Fader ---
    public class FaderElement : VisualElement, IParamBindable
    {
        public new class UxmlFactory : UxmlFactory<FaderElement, UxmlTraits> { }

        public new class UxmlTraits : VisualElement.UxmlTraits
        {
            UxmlStringAttributeDescription _label = new() { name = "label", defaultValue = "" };
            UxmlStringAttributeDescription _paramId = new() { name = "paramId", defaultValue = "" };
            UxmlBoolAttributeDescription _horizontal = new() { name = "horizontal", defaultValue = false };

            public override void Init(VisualElement ve, IUxmlAttributes bag, CreationContext cc)
            {
                base.Init(ve, bag, cc);
                var f = (FaderElement)ve;
                f.Label = _label.GetValueFromBag(bag, cc);
                f.ParamId = _paramId.GetValueFromBag(bag, cc);
                f.Horizontal = _horizontal.GetValueFromBag(bag, cc);
                f.Build();
            }
        }

        public string Label { get; set; } = "";
        public string ParamId { get; set; } = "";
        public bool Horizontal { get; set; } = false;

        private ParameterBus _bus;
        private VisualElement _track;
        private VisualElement _fill;
        private VisualElement _thumb;
        private Label _name;
        private Label _val;

        private bool _drag;
        private float _startT;
        private Vector2 _startPos;

        // --- runtime metrics (vertical) ---
        private float _padTop = 16f;
        private float _holderW = 40f;
        private float _trackW = 10f;
        private float _thumbSize = 36f;

        private float _holderH = 400f; // computed
        private float _trackH = 360f;  // computed
        private float _labelBlockH = 62f; // space for name + value

        private VisualElement _trackHolder; // vertical holder
        private VisualElement _wrap;        // vertical wrap
        private VisualElement _hHolder;     // horizontal holder

        public FaderElement()
        {
            style.marginRight = 10;
        }

        private void Build()
        {
            Clear();

            // Allow USS to drive sizing.
            // (If USS doesn't set height, we provide a reasonable default.)
            if (!Horizontal)
            {
                style.width = 58;
                if (style.height.keyword == StyleKeyword.Auto || style.height.keyword == StyleKeyword.Null)
                    style.height = 300; // default only (USS can override)
                style.flexDirection = FlexDirection.Column;
                style.alignItems = Align.Center;
            }
            else
            {
                style.width = 620;
                style.height = 64; // horizontal "master" style is fine fixed
                style.flexDirection = FlexDirection.Row;
                style.alignItems = Align.Center;
            }

            _track = new VisualElement();
            _track.style.backgroundColor = new Color(0.35f, 0.37f, 0.43f, 0.7f);
            _track.style.borderTopLeftRadius = 10;
            _track.style.borderTopRightRadius = 10;
            _track.style.borderBottomLeftRadius = 10;
            _track.style.borderBottomRightRadius = 10;

            _fill = new VisualElement();
            _fill.style.backgroundColor = new Color(0.41f, 0.89f, 1f, 0.6f);
            _fill.style.borderTopLeftRadius = 10;
            _fill.style.borderTopRightRadius = 10;
            _fill.style.borderBottomLeftRadius = 10;
            _fill.style.borderBottomRightRadius = 10;
            _fill.style.position = Position.Absolute;

            _thumb = new VisualElement();
            _thumb.style.backgroundColor = new Color(0.91f, 0.92f, 0.93f, 1f);
            _thumb.style.borderTopLeftRadius = 14;
            _thumb.style.borderTopRightRadius = 14;
            _thumb.style.borderBottomLeftRadius = 14;
            _thumb.style.borderBottomRightRadius = 14;
            _thumb.style.borderBottomWidth = 2;
            _thumb.style.borderLeftWidth = 2;
            _thumb.style.borderRightWidth = 2;
            _thumb.style.borderTopWidth = 2;
            _thumb.style.borderBottomColor = new Color(1, 1, 1, 0.2f);
            _thumb.style.borderTopColor = new Color(1, 1, 1, 0.2f);
            _thumb.style.borderLeftColor = new Color(1, 1, 1, 0.2f);
            _thumb.style.borderRightColor = new Color(1, 1, 1, 0.2f);
            _thumb.style.width = _thumbSize;
            _thumb.style.height = _thumbSize;
            _thumb.style.position = Position.Absolute;

            if (!Horizontal)
            {
                _trackHolder = new VisualElement();
                _trackHolder.style.width = _holderW;
                _trackHolder.style.position = Position.Relative;
                _trackHolder.style.alignItems = Align.Center;
                _trackHolder.style.flexGrow = 1;
                _trackHolder.style.minHeight = 0;

                float trackX = (_holderW - _trackW) * 0.5f;

                _track.style.position = Position.Absolute;
                _track.style.left = trackX;
                _track.style.top = _padTop;
                _track.style.width = _trackW;

                _fill.style.left = trackX;
                _fill.style.width = _trackW;

                _thumb.style.left = (_holderW - _thumbSize) * 0.5f;

                _trackHolder.Add(_track);
                _trackHolder.Add(_fill);
                _trackHolder.Add(_thumb);

                _name = new Label(Label);
                _name.style.color = new Color(0.60f, 0.64f, 0.70f, 1f);
                _name.style.fontSize = 20;
                _name.style.unityTextAlign = TextAnchor.MiddleCenter;
                _name.style.marginTop = 8;

                _val = new Label("--");
                _val.style.color = new Color(0.91f, 0.92f, 0.93f, 1f);
                _val.style.fontSize = 20;
                _val.style.unityTextAlign = TextAnchor.MiddleCenter;

                _wrap = new VisualElement();
                _wrap.style.flexDirection = FlexDirection.Column;
                _wrap.style.alignItems = Align.Center;
                _wrap.style.flexGrow = 1;
                _wrap.style.minHeight = 0;

                _wrap.Add(_trackHolder);
                if (!string.IsNullOrEmpty(Label)) _wrap.Add(_name);
                _wrap.Add(_val);

                Add(_wrap);

                // Recompute sizes whenever USS/viewport changes our height.
            }
            else
            {
                // ---- Horizontal ----
                const float HTRACK_H = 18f;
                const float HHOLDER_H = 42f;

                _hHolder = new VisualElement();
                _hHolder.style.flexGrow = 1;
                _hHolder.style.height = HHOLDER_H;
                _hHolder.style.position = Position.Relative;
                _hHolder.style.justifyContent = Justify.Center;
                _hHolder.style.alignItems = Align.Center;

                _track.style.height = HTRACK_H;
                _track.style.position = Position.Absolute;
                _track.style.left = 0;
                _track.style.right = 0;
                _track.style.top = (HHOLDER_H - HTRACK_H) * 0.5f;

                _fill.style.position = Position.Absolute;
                _fill.style.height = HTRACK_H;
                _fill.style.top = (HHOLDER_H - HTRACK_H) * 0.5f;
                _fill.style.left = 0;
                _fill.style.width = 0;

                _thumb.style.top = (HHOLDER_H - _thumbSize) * 0.5f;

                _hHolder.Add(_track);
                _hHolder.Add(_fill);
                _hHolder.Add(_thumb);
                Add(_hHolder);
            }

            // Recompute layout whenever USS/viewport/visibility changes our size.
            // This is especially important for controls that start on hidden tabs
            // (e.g. synth master / tempo horizontal faders).
            UnregisterCallback<GeometryChangedEvent>(OnGeom);
            RegisterCallback<GeometryChangedEvent>(OnGeom);

            RegisterCallback<PointerDownEvent>(OnDown);
            RegisterCallback<PointerMoveEvent>(OnMove);
            RegisterCallback<PointerUpEvent>(OnUp);
        }

        private void OnGeom(GeometryChangedEvent e)
        {
            if (Horizontal)
            {
                if (_bus != null && !string.IsNullOrEmpty(ParamId))
                    LayoutFromT(_bus.GetNormalized(ParamId));
                else
                    LayoutFromT(0f);
                return;
            }

            // Total element height (from USS or default)
            float totalH = resolvedStyle.height;
            if (totalH <= 0f) return;

            // If label is empty, we only show value line -> smaller label block.
            float labelBlock = string.IsNullOrEmpty(Label) ? 38f : _labelBlockH;

            // Holder is the remaining height for the track area.
            float holderH = Mathf.Max(80f, totalH - labelBlock);
            _holderH = holderH;

            // Track height inside holder
            float trackH = Mathf.Max(40f, holderH - _padTop - (_thumbSize * 0.5f));
            _trackH = trackH;

            _trackHolder.style.height = holderH;
            _track.style.height = trackH;

            // Refresh layout using current param value
            if (_bus != null && !string.IsNullOrEmpty(ParamId))
            {
                float t = _bus.GetNormalized(ParamId);
                LayoutFromT(t);
            }
            else
            {
                LayoutFromT(0f);
            }
        }

        public void Bind(ParameterBus bus)
        {
            if (_bus != null)
                _bus.OnChanged -= OnParamChanged;

            _bus = bus;
            if (_bus != null)
                _bus.OnChanged += OnParamChanged;
            Refresh();
        }

        private void OnParamChanged(string id, float v)
        {
            if (id != ParamId) return;
            Refresh();
        }

        private void Refresh()
        {
            if (_bus == null || string.IsNullOrEmpty(ParamId)) return;
            float t = _bus.GetNormalized(ParamId);
            LayoutFromT(t);
            if (_bus.TryGet(ParamId, out var p))
                if (_val != null) _val.text = p.Format();
        }

        private void LayoutFromT(float t)
        {
            t = Mathf.Clamp01(t);

            if (!Horizontal)
            {
                float trackTop = _padTop;
                float travel = Mathf.Max(0f, _trackH - _thumbSize);
                float thumbTop = trackTop + (travel * (1f - t));

                _thumb.style.top = thumbTop;

                // Fill starts at center of thumb to bottom of track
                float fillTop = thumbTop + (_thumbSize * 0.5f);
                float fillH = Mathf.Max(0f, (trackTop + _trackH) - fillTop);

                _fill.style.top = fillTop;
                _fill.style.height = fillH;
            }
            else
            {
                const float HPAD_X = 20f;
                float w = resolvedStyle.width;
                float travel = Mathf.Max(0f, w - (HPAD_X * 2f) - _thumbSize);
                float x = HPAD_X + travel * t;

                _thumb.style.left = x;
                _thumb.style.top = 3;

                _fill.style.left = 0;
                _fill.style.top = 12;
                _fill.style.width = x + (_thumbSize * 0.5f);
            }
        }

        private void OnDown(PointerDownEvent e)
        {
            if (_bus == null || string.IsNullOrEmpty(ParamId)) return;
            _drag = true;
            _startPos = e.position;
            _startT = _bus.GetNormalized(ParamId);
            this.CapturePointer(e.pointerId);
            e.StopPropagation();

            if (e.clickCount >= 2 && _bus.TryGet(ParamId, out var p))
            {
                if (ParamId == "sample.pitch")
                    _bus.SetValue(ParamId, 0f);
                else
                    _bus.SetValue(ParamId, p.Default);

                Refresh();
            }
        }

        private void OnMove(PointerMoveEvent e)
        {
            if (!_drag || _bus == null || string.IsNullOrEmpty(ParamId)) return;

            bool useHorizontalDrag =
                Horizontal &&
                (ParamId == "tempo" || ParamId == "master.vol");

            float speed;
            float t;

            if (useHorizontalDrag)
            {
                float dx = e.position.x - _startPos.x;
                speed = e.shiftKey ? 0.0010f : 0.0040f;
                t = Mathf.Clamp01(_startT + dx * speed);
            }
            else
            {
                float dy = _startPos.y - e.position.y;
                speed = e.shiftKey ? 0.0008f : 0.0030f;
                t = Mathf.Clamp01(_startT + dy * speed);
            }

            _bus.SetNormalized(ParamId, t);
            Refresh();
        }

        private void OnUp(PointerUpEvent e)
        {
            if (!_drag) return;
            _drag = false;
            this.ReleasePointer(e.pointerId);
            e.StopPropagation();
        }
    }
    // --- Step Grid ---
    public class StepGrid : VisualElement
    {
        // --- Robust stroke capture (grid-level) ---
        private bool _painting = false;
        private int _capturedPointerId = -1;
        private int _lastPaintR = -1;
        private int _lastPaintC = -1;

        // Fast lookup: cell VisualElement -> (r,c)
        private readonly Dictionary<VisualElement, Vector2Int> _cellIndex = new Dictionary<VisualElement, Vector2Int>();

        private bool _strokeErase = false;

        // Optional: show a label per active cell (e.g. "02")
        private bool _showValueLabel = false;
        private Func<int, string> _valueLabelFormatter = null;

        // Optional: tint cells by stored value (e.g. chop ID)
        private bool _useValueTint = false;
        private Func<int, Color> _valueTint = null;

        // Fallback visible tint when no USS styles active cells
        private Color _defaultActiveTint = new Color(1f, 1f, 1f, 0.18f);
        // Background for EMPTY cells (v==0). If you leave v==0 as Null, USS/default can turn them white.
        private Color _emptyCellTint = new Color(0.08f, 0.10f, 0.14f, 1f);


        public new class UxmlFactory : UxmlFactory<StepGrid, UxmlTraits> { }

        private const int Rows = 2;
        private const int Cols = 8;

        // 0 = empty, >0 = "value" (drum on=1, sample chop=1..16)
        private int[,] _val = new int[Rows, Cols];

        // paint brush value (0 = erase)
        private int _paintValue = 1;

        // Stroke state (so drag keeps erase/paint mode consistent)
        private int _activePointerId = -1;
        private bool _activeErase = false;

        private VisualElement[,] _cells = new VisualElement[Rows, Cols];

        // Optional: tint all "active" cells with a color (useful for drum lanes)
        private bool _useActiveTint = false;
        private Color _activeTint = Color.white;

        // ✅ Optional: clicking a cell that already has the paint value will erase it.
        // Great for drum sequencers (toggle on/off).
        private bool _toggleEraseOnSameValue = false;

        private static string DrumLabelForValue(int v)
        {
            if (v <= 0) return "";
            switch (v)
            {
                case 1: return "K";
                case 2: return "S";
                case 3: return "C";
                case 4: return "HC";
                case 5: return "HO";
                case 6: return "RD";
                case 7: return "RM";
                case 8: return "CR";
                default: return v.ToString();
            }
        }

        // --- Playhead (single system) ---
        // Uses the class: "is-playhead"
        private int _playheadStep = -1;
        private bool _showPlayhead = true;

        public void SetPlayheadVisible(bool visible)
        {
            _showPlayhead = visible;

            if (!visible)
            {
                ClearPlayheadVisual();
            }
            else
            {
                // re-apply to current playhead if any
                ApplyPlayheadVisual(_playheadStep);
            }
        }

        public void SetPlayheadStep(int step)
        {
            // allow clear + clamp
            if (step < 0 || step >= (Rows * Cols))
                step = -1;

            if (_playheadStep == step)
                return;

            int prev = _playheadStep;
            _playheadStep = step;

            // refresh only the affected cells so visuals always update
            if (prev >= 0)
                UpdateCellVisual(prev / Cols, prev % Cols);

            if (_playheadStep >= 0)
                UpdateCellVisual(_playheadStep / Cols, _playheadStep % Cols);
        }

        private void ClearPlayheadVisual()
        {
            if (_playheadStep < 0)
                return;

            int r = _playheadStep / Cols;
            int c = _playheadStep % Cols;

            var cell = _cells[r, c];
            if (cell != null)
                cell.RemoveFromClassList("is-playhead");
        }

        private void ApplyPlayheadVisual(int step)
        {
            if (step < 0)
                return;

            int r = step / Cols;
            int c = step % Cols;

            var cell = _cells[r, c];
            if (cell != null)
                cell.AddToClassList("is-playhead");
        }

        // Fired when user clicks/paints a cell
        public event Action<int, int, int> OnCellValueChanged;

        public event Action<int, int> OnCellClicked;

        // Fired at the START of a pointer-down stroke (before any cells are painted).
        // bool = isErase
        public event Action<bool> OnStrokeStarted;

        // Fired when the pointer is released / stroke ends.
        // Carries every (r,c) cell that was painted/erased during the stroke, in order.
        public event Action<List<Vector2Int>, bool> OnStrokeEnded;

        // Cells visited during the current stroke (in paint order).
        private readonly List<Vector2Int> _strokeCells = new List<Vector2Int>();

        public int RowCount => Rows;
        public int ColCount => Cols;

        public StepGrid()
        {
            AddToClassList("km-seq-grid");
            Build();

            RegisterCallback<PointerMoveEvent>(OnGridPointerMove);
            RegisterCallback<PointerUpEvent>(OnGridPointerUp);
            RegisterCallback<PointerCancelEvent>(OnGridPointerCancel);
            RegisterCallback<PointerCaptureOutEvent>(OnGridPointerCaptureOut);

            ClearAll(); // ✅ no default highlights
        }

        public void SetPaintValue(int v) => _paintValue = Mathf.Clamp(v, 0, 999);
        public int GetPaintValue() => _paintValue;

        public void EnableToggleEraseOnSameValue(bool on)
        {
            _toggleEraseOnSameValue = on;
        }

        public void SetActiveTint(Color c)
        {
            _useActiveTint = true;
            _activeTint = c;
            RefreshAll();
        }

        public void ClearActiveTint()
        {
            _useActiveTint = false;
            RefreshAll();
        }

        public void EnableValueLabels(bool on, Func<int, string> formatter)
        {
            _showValueLabel = on;
            _valueLabelFormatter = formatter;
            RefreshAll();
        }

        public void EnableValueTint(bool enabled, Func<int, Color> tintFunc)
        {
            _useValueTint = enabled;
            _valueTint = tintFunc;
            RefreshAll();
        }

        public void RefreshAll()
        {
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Cols; c++)
                    UpdateCellVisual(r, c);
        }

        public void ClearAll()
        {
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Cols; c++)
                    SetValue(r, c, 0, fireEvent: false);
        }

        public int GetValue(int r, int c) => _val[r, c];

        /// <summary>
        /// Export current grid values as a copy (Rows x Cols).
        /// </summary>
        public int[,] ExportValues()
        {
            var dst = new int[Rows, Cols];
            Array.Copy(_val, dst, _val.Length);
            return dst;
        }

        /// <summary>
        /// Export current grid values as a flat array (length Rows*Cols).
        /// </summary>
        public int[] ExportValuesFlat()
        {
            var dst = new int[Rows * Cols];
            int i = 0;
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Cols; c++)
                    dst[i++] = _val[r, c];
            return dst;
        }

        /// <summary>
        /// Import values into the grid.
        /// </summary>
        public void ImportValues(int[,] src, bool fireEvent = false)
        {
            if (src == null) return;
            if (src.GetLength(0) != Rows || src.GetLength(1) != Cols) return;

            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Cols; c++)
                    SetValue(r, c, src[r, c], fireEvent: fireEvent);
        }

        /// <summary>
        /// Import values from a flat array (length must be Rows*Cols).
        /// </summary>
        public void ImportValuesFlat(int[] src, bool fireEvent = false)
        {
            if (src == null) return;
            if (src.Length != Rows * Cols) return;

            int i = 0;
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Cols; c++)
                    SetValue(r, c, src[i++], fireEvent: fireEvent);
        }

        public void SetValue(int r, int c, int v, bool fireEvent = true)
        {
            if (r < 0 || r >= Rows || c < 0 || c >= Cols) return;

            _val[r, c] = v;

            // let UpdateCellVisual decide active class + bg
            UpdateCellVisual(r, c);

            if (fireEvent)
                OnCellValueChanged?.Invoke(r, c, v);
        }

        private void UpdateCellVisual(int r, int c)
        {
            int v = GetValue(r, c);
            var cell = _cells[r, c];
            if (cell == null) return;

            int idx = r * Cols + c;
            bool isPlayhead = (_playheadStep == idx);

            if (isPlayhead) cell.AddToClassList("is-playhead");
            else cell.RemoveFromClassList("is-playhead");

            // ✅ EMPTY background MUST be explicit, otherwise you get white from defaults/USS.
            if (v == 0)
            {
                cell.RemoveFromClassList("km-step--active");
                cell.style.backgroundColor = new StyleColor(_emptyCellTint);
            }
            else
            {
                cell.AddToClassList("km-step--active");

                // active background tint
                if (_useValueTint && _valueTint != null)
                    cell.style.backgroundColor = new StyleColor(_valueTint(v));
                else if (_useActiveTint)
                    cell.style.backgroundColor = new StyleColor(_activeTint);
                else
                    cell.style.backgroundColor = new StyleColor(_defaultActiveTint);
            }

            // label
            var tag = cell.Q<Label>("CellTag");
            if (tag != null)
            {
                if (_showValueLabel && v != 0)
                {
                    string txt = _valueLabelFormatter != null ? _valueLabelFormatter(v) : v.ToString();
                    tag.text = txt;
                    tag.style.display = string.IsNullOrEmpty(txt) ? DisplayStyle.None : DisplayStyle.Flex;
                }
                else
                {
                    tag.text = "";
                    tag.style.display = DisplayStyle.None;
                }
            }
        }

        public bool[,] ExportBools()
        {
            var b = new bool[Rows, Cols];
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Cols; c++)
                    b[r, c] = (_val[r, c] != 0);
            return b;
        }

        public void ImportBools(bool[,] src, bool fireEvent = false)
        {
            if (src == null || src.GetLength(0) != Rows || src.GetLength(1) != Cols)
                return;

            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Cols; c++)
                    SetValue(r, c, src[r, c] ? 1 : 0, fireEvent);
        }

        private void Build()
        {
            Clear();
            _cellIndex.Clear(); // ✅ IMPORTANT: prevent stale cell refs after rebuild

            for (int r = 0; r < Rows; r++)
            {
                var row = new VisualElement();
                row.AddToClassList("km-seq-row");

                for (int c = 0; c < Cols; c++)
                {
                    int rr = r, cc = c;

                    var cell = new VisualElement();
                    cell.AddToClassList("km-step");
                    cell.style.position = Position.Relative;

                    var tag = new Label();
                    tag.name = "CellTag";
                    tag.style.color = Color.white;
                    tag.style.display = DisplayStyle.None;
                    tag.pickingMode = PickingMode.Ignore;
                    tag.style.position = Position.Absolute;
                    tag.style.left = 0;
                    tag.style.right = 0;
                    tag.style.top = 0;
                    tag.style.bottom = 0;
                    tag.style.unityTextAlign = TextAnchor.MiddleCenter;
                    tag.style.fontSize = 28;
                    tag.style.unityFontStyleAndWeight = FontStyle.Bold;

                    cell.Add(tag);

                    _cells[r, c] = cell;
                    _cellIndex[cell] = new Vector2Int(rr, cc);

                    cell.RegisterCallback<PointerDownEvent>(e =>
                    {
                        OnCellClicked?.Invoke(rr, cc);

                        bool rightButton = e.button == (int)MouseButton.RightMouse;
                        bool modErase = e.ctrlKey || e.commandKey;
                        bool gestureErase = rightButton || modErase;

                        _activePointerId = e.pointerId;
                        _activeErase = gestureErase;

                        int existing = GetValue(rr, cc);
                        bool clickOccupiedErase = (e.button == (int)MouseButton.LeftMouse) && !gestureErase && existing > 0;

                        _strokeErase = gestureErase || clickOccupiedErase;

                        // --- Start stroke (GRID-level pointer capture) ---
                        _painting = true;
                        _capturedPointerId = e.pointerId;
                        _lastPaintR = -1;
                        _lastPaintC = -1;
                        _strokeCells.Clear();

                        OnStrokeStarted?.Invoke(_strokeErase);

                        // ✅ capture pointer on the GRID, not the cell (older UITK uses helper)
                        PointerCaptureHelper.CapturePointer(this, e.pointerId);

                        // paint immediately on down
                        ApplyPaint(rr, cc, _strokeErase);
                        _strokeCells.Add(new Vector2Int(rr, cc));
                        _lastPaintR = rr;
                        _lastPaintC = cc;

                        e.StopImmediatePropagation();
                    });

                    row.Add(cell);
                }

                Add(row);
            }
        }

        private void ApplyPaint(int r, int c, bool eraseOverride)
        {
            int cur = _val[r, c];

            // Right-click / Ctrl / Cmd stroke erase
            if (eraseOverride)
            {
                if (cur != 0) SetValue(r, c, 0);
                return;
            }

            // Paint value 0 = erase
            if (_paintValue == 0)
            {
                if (cur != 0) SetValue(r, c, 0);
                return;
            }

            // ✅ Toggle erase if clicking the same value again
            if (_toggleEraseOnSameValue && cur == _paintValue)
            {
                SetValue(r, c, 0);
                return;
            }

            // Normal paint
            if (cur != _paintValue) SetValue(r, c, _paintValue);
        }

        private void OnGridPointerMove(PointerMoveEvent e)
        {
            if (!_painting) return;
            if (_capturedPointerId != -1 && e.pointerId != _capturedPointerId) return;
            if (!PointerCaptureHelper.HasPointerCapture(this, e.pointerId)) return;

            if (!TryGetCellAt(e.localPosition, out int r, out int c))
                return;

            if (r == _lastPaintR && c == _lastPaintC)
                return;

            ApplyPaint(r, c, _strokeErase);
            _strokeCells.Add(new Vector2Int(r, c));
            _lastPaintR = r;
            _lastPaintC = c;

            e.StopImmediatePropagation();
        }

        private void OnGridPointerUp(PointerUpEvent e)
        {
            if (_capturedPointerId != -1 && e.pointerId != _capturedPointerId) return;
            EndStroke(e.pointerId);
            e.StopImmediatePropagation();
        }

        private void OnGridPointerCancel(PointerCancelEvent e)
        {
            if (_capturedPointerId != -1 && e.pointerId != _capturedPointerId) return;
            EndStroke(e.pointerId);
            e.StopImmediatePropagation();
        }

        private void OnGridPointerCaptureOut(PointerCaptureOutEvent e)
        {
            // Something else stole capture (ScrollView/panel/etc.)
            if (_painting && _strokeCells.Count > 0)
                OnStrokeEnded?.Invoke(new List<Vector2Int>(_strokeCells), _strokeErase);

            _painting = false;
            _capturedPointerId = -1;
            _strokeErase = false;
            _lastPaintR = -1;
            _lastPaintC = -1;
            _strokeCells.Clear();
        }

        /// <summary>
        /// Convert a pointer position (local to StepGrid) into a (row,col) by picking the element under it.
        /// Compatible with older UI Toolkit Pick overload.
        /// </summary>
        private bool TryGetCellAt(Vector2 localPos, out int r, out int c)
        {
            r = -1;
            c = -1;

            if (panel == null)
                return false;

            Vector2 worldPos = this.LocalToWorld(localPos);
            VisualElement picked = panel.Pick(worldPos);

            while (picked != null)
            {
                if (_cellIndex.TryGetValue(picked, out var rc))
                {
                    r = rc.x;
                    c = rc.y;
                    return true;
                }
                picked = picked.parent;
            }

            return false;
        }

        private void EndStroke(int pointerId)
        {
            bool wasErase = _strokeErase;
            var cells = new List<Vector2Int>(_strokeCells);

            _painting = false;

            if (PointerCaptureHelper.HasPointerCapture(this, pointerId))
                PointerCaptureHelper.ReleasePointer(this, pointerId);

            if (pointerId == _capturedPointerId)
                _capturedPointerId = -1;

            _strokeErase = false;
            _activePointerId = -1;
            _activeErase = false;

            _lastPaintR = -1;
            _lastPaintC = -1;
            _strokeCells.Clear();

            OnStrokeEnded?.Invoke(cells, wasErase);
        }
    }

    // --- Preset Browser Overlay (scaffold) ---
    public class PresetBrowser : VisualElement
    {
        public new class UxmlFactory : UxmlFactory<PresetBrowser, UxmlTraits> { }

        public PresetBrowser()
        {
            style.position = Position.Absolute;
            style.left = 60;
            style.top = 200;
            style.right = 60;
            style.bottom = 520;
            style.backgroundColor = new Color(0.07f, 0.08f, 0.10f, 0.98f);
            style.borderTopLeftRadius = 26;
            style.borderTopRightRadius = 26;
            style.borderBottomLeftRadius = 26;
            style.borderBottomRightRadius = 26;
            style.borderBottomWidth = 2;
            style.borderTopWidth = 2;
            style.borderLeftWidth = 2;
            style.borderRightWidth = 2;
            style.borderBottomColor = new Color(0.14f, 0.16f, 0.22f, 1f);
            style.borderTopColor = new Color(0.14f, 0.16f, 0.22f, 1f);
            style.borderLeftColor = new Color(0.14f, 0.16f, 0.22f, 1f);
            style.borderRightColor = new Color(0.14f, 0.16f, 0.22f, 1f);
            style.paddingLeft = 24;
            style.paddingRight = 24;
            style.paddingTop = 18;
            style.paddingBottom = 18;

            var title = new Label("PROJECTS");
            title.style.color = new Color(0.60f, 0.64f, 0.70f, 1f);
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.fontSize = 32;
            Add(title);

            var hint = new Label("Preset browser scaffold (wire to JSON presets next).");
            hint.style.color = new Color(0.60f, 0.64f, 0.70f, 1f);
            hint.style.fontSize = 24;
            hint.style.marginTop = 8;
            Add(hint);
        }
    }
}
