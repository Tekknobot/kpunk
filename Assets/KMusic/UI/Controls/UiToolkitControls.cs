// UiToolkitControls.cs

using System;
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
            const float HPAD_X = 20f;            // horizontal left/right padding for thumb travel

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
                _fill.style.position = Position.Absolute;           // ✅ add/ensure
                _fill.style.height = HTRACK_H;
                _fill.style.top = (HHOLDER_H - HTRACK_H) * 0.5f;
                _fill.style.left = 0;
                _fill.style.width = 0;                             // ✅ add

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
        public new class UxmlFactory : UxmlFactory<StepGrid, UxmlTraits> { }

        private const int Rows = 2;
        private const int Cols = 8;

        private bool[,] _on = new bool[Rows, Cols];
        private int _paintValue = 1;

        public StepGrid()
        {
            AddToClassList("km-seq-grid");
            Build();
        }

        private void Build()
        {
            Clear();
            for (int r = 0; r < Rows; r++)
            {
                var row = new VisualElement();
                row.AddToClassList("km-seq-row");
                for (int c = 0; c < Cols; c++)
                {
                    int rr = r, cc = c;
                    var cell = new VisualElement();
                    cell.AddToClassList("km-step");
                    cell.RegisterCallback<PointerDownEvent>(e =>
                    {
                        Toggle(rr, cc);
                        cell.CapturePointer(e.pointerId);
                        e.StopPropagation();
                    });
                    cell.RegisterCallback<PointerMoveEvent>(e =>
                    {
                        if (cell.HasPointerCapture(e.pointerId))
                        {
                            if (_paintValue == 1 && !_on[rr, cc]) Set(rr, cc, true);
                            if (_paintValue == 0 && _on[rr, cc]) Set(rr, cc, false);
                        }
                    });
                    cell.RegisterCallback<PointerUpEvent>(e =>
                    {
                        if (cell.HasPointerCapture(e.pointerId))
                            cell.ReleasePointer(e.pointerId);
                    });
                    row.Add(cell);
                }
                Add(row);
            }
            // Seed pattern similar to mockup
            Set(0, 0, true); Set(0, 3, true); Set(0, 4, true); Set(0, 7, true);
            Set(1, 2, true); Set(1, 5, true);
        }

        private void Toggle(int r, int c) => Set(r, c, !_on[r, c]);

        private void Set(int r, int c, bool v)
        {
            _on[r, c] = v;
            var row = this.ElementAt(r) as VisualElement;
            var ve = row.ElementAt(c) as VisualElement;
            if (v) ve.AddToClassList("km-step--active");
            else ve.RemoveFromClassList("km-step--active");
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