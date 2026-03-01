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
            style.height = 132;
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

        public FaderElement()
        {
            style.marginRight = 10;
        }

        private void Build()
        {
            Clear();

            // ---- Tunables (uniform layout) ----
            const float TRACK_W = 10f;
            const float TRACK_H = 360f;
            const float HOLDER_W = 40f;
            const float HOLDER_H = 400f;
            const float PAD_TOP  = 20f;          // top padding inside holder (keeps it off the rounded ends)
            const float THUMB = 36f;
            const float HTRACK_H = 18f;          // horizontal track height
            const float HHOLDER_H = 42f;

            if (!Horizontal)
            {
                style.width = 58;
                style.height = 460;
                style.flexDirection = FlexDirection.Column;
                style.alignItems = Align.Center;
            }
            else
            {
                style.width = 620;
                style.height = 64;
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
            _thumb.style.width = THUMB;
            _thumb.style.height = THUMB;
            _thumb.style.position = Position.Absolute;

            if (!Horizontal)
            {
                // ---- Holder (absolute layout so everything shares the same origin) ----
                var trackHolder = new VisualElement();
                trackHolder.style.width = HOLDER_W;
                trackHolder.style.height = HOLDER_H;
                trackHolder.style.position = Position.Relative; // important: anchors absolute children
                trackHolder.style.alignItems = Align.Center;

                // Track centered
                float trackX = (HOLDER_W - TRACK_W) * 0.5f;
                _track.style.position = Position.Absolute;
                _track.style.left = trackX;
                _track.style.top = PAD_TOP;
                _track.style.width = TRACK_W;
                _track.style.height = TRACK_H;

                // Fill exactly on top of track (same X/width)
                _fill.style.left = trackX;
                _fill.style.width = TRACK_W;

                // Thumb centered in holder (same centerline as track)
                _thumb.style.left = (HOLDER_W - THUMB) * 0.5f;

                trackHolder.Add(_track);
                trackHolder.Add(_fill);
                trackHolder.Add(_thumb);

                // Labels
                _name = new Label(Label);
                _name.style.color = new Color(0.60f, 0.64f, 0.70f, 1f);
                _name.style.fontSize = 20;
                _name.style.unityTextAlign = TextAnchor.MiddleCenter;
                _name.style.marginTop = 8;

                _val = new Label("--");
                _val.style.color = new Color(0.91f, 0.92f, 0.93f, 1f);
                _val.style.fontSize = 20;
                _val.style.unityTextAlign = TextAnchor.MiddleCenter;

                var wrap = new VisualElement();
                wrap.style.flexDirection = FlexDirection.Column;
                wrap.style.alignItems = Align.Center;

                wrap.Add(trackHolder);
                if (!string.IsNullOrEmpty(Label)) wrap.Add(_name);
                wrap.Add(_val);

                Add(wrap);
            }
            else
            {
                // ---- Horizontal ----
                var holder = new VisualElement();
                holder.style.flexGrow = 1;
                holder.style.height = HHOLDER_H;
                holder.style.position = Position.Relative;
                holder.style.justifyContent = Justify.Center;
                holder.style.alignItems = Align.Center;

                // Track
                _track.style.height = HTRACK_H;
                _track.style.position = Position.Absolute;
                _track.style.left = 0;
                _track.style.right = 0;
                _track.style.top = (HHOLDER_H - HTRACK_H) * 0.5f;

                // Fill (MAKE IT ABSOLUTE + START WIDTH 0)
                _fill.style.position = Position.Absolute;
                _fill.style.height = HTRACK_H;
                _fill.style.top = (HHOLDER_H - HTRACK_H) * 0.5f;
                _fill.style.left = 0;
                _fill.style.width = 0;

                // Thumb
                _thumb.style.top = (HHOLDER_H - THUMB) * 0.5f;

                holder.Add(_track);
                holder.Add(_fill);
                holder.Add(_thumb);
                Add(holder);
            }

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
            LayoutFromT(t);
            if (_bus.TryGet(ParamId, out var p))
                if (_val != null) _val.text = p.Format();
        }

        private void LayoutFromT(float t)
        {
            t = Mathf.Clamp01(t);
            if (!Horizontal)
            {
                float top = 20f + (360f * (1f - t));
                _thumb.style.top = top;
                _thumb.style.left = 2;
                _fill.style.left = 15;
                _fill.style.top = top + 18;
                _fill.style.height = (400f - (top + 18));
            }
            else
            {
                const float HPAD_X = 20f;   // left/right padding
                const float THUMB = 36f;    // thumb width

                float w = resolvedStyle.width;

                // distance thumb can travel
                float travel = Mathf.Max(0f, w - (HPAD_X * 2f) - THUMB);

                // compute thumb position
                float x = HPAD_X + travel * t;

                _thumb.style.left = x;
                _thumb.style.top = 3;

                // fill stays centered with thumb
                _fill.style.left = 0;
                _fill.style.top = 12;
                _fill.style.width = x + (THUMB * 0.5f);
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
                _bus.SetValue(ParamId, p.Default);
                Refresh();
            }
        }

        private void OnMove(PointerMoveEvent e)
        {
            if (!_drag || _bus == null || string.IsNullOrEmpty(ParamId)) return;
            float delta = Horizontal ? (e.position.x - _startPos.x) : (_startPos.y - e.position.y);
            float speed = e.shiftKey ? 0.0008f : 0.0028f;
            float t = Mathf.Clamp01(_startT + delta * speed);
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

        public void EnableValueTint(bool on, Func<int, Color> tintForValue)
        {
            _useValueTint = on;
            _valueTint = tintForValue;
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

        public void SetValue(int r, int c, int v, bool fireEvent = true)
        {
            _val[r, c] = v;
            var cell = _cells[r, c];

            if (v != 0) cell.AddToClassList("km-step--active");
            else cell.RemoveFromClassList("km-step--active");

            UpdateCellVisual(r, c);

            if (fireEvent)
                OnCellValueChanged?.Invoke(r, c, v);
        }

        private void UpdateCellVisual(int r, int c)
        {
            int v = GetValue(r, c);
            var cell = _cells[r, c];
            if (cell == null) return;

            // ✅ ensure the CSS selector can match if you keep `.km-step.km-step--playhead`
            cell.AddToClassList("km-step");

            int idx = r * Cols + c;
            bool isPlayhead = (_playheadStep == idx);
            if (isPlayhead) cell.AddToClassList("is-playhead");
            else cell.RemoveFromClassList("is-playhead");

            // active background tint
            if (v != 0)
            {
                if (_useValueTint && _valueTint != null)
                    cell.style.backgroundColor = _valueTint(v);
                else if (_useActiveTint)
                    cell.style.backgroundColor = _activeTint;
                else
                    cell.style.backgroundColor = _defaultActiveTint;
            }
            else
            {
                cell.style.backgroundColor = StyleKeyword.Null;
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

        public int[,] ExportValues()
        {
            var copy = new int[Rows, Cols];
            Array.Copy(_val, copy, _val.Length);
            return copy;
        }

        public void ImportValues(int[,] src, bool fireEvent = false)
        {
            if (src == null || src.GetLength(0) != Rows || src.GetLength(1) != Cols)
                return;

            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Cols; c++)
                    SetValue(r, c, src[r, c], fireEvent);
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

                        // ✅ capture pointer on the GRID, not the cell (older UITK uses helper)
                        PointerCaptureHelper.CapturePointer(this, e.pointerId);

                        // paint immediately on down
                        ApplyPaint(rr, cc, _strokeErase);
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
            _painting = false;
            _capturedPointerId = -1;
            _strokeErase = false;
            _lastPaintR = -1;
            _lastPaintC = -1;
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

            var title = new Label("PRESETS");
            title.style.color = new Color(0.60f, 0.64f, 0.70f, 1f);
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.fontSize = 18;
            Add(title);

            var hint = new Label("Preset browser scaffold (wire to JSON presets next).");
            hint.style.color = new Color(0.60f, 0.64f, 0.70f, 1f);
            hint.style.fontSize = 14;
            hint.style.marginTop = 8;
            Add(hint);
        }
    }
}
