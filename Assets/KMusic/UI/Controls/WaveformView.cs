// Assets/KMusic/UI/Controls/WaveformView.cs
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace KMusic.UI
{
    public class WaveformView : VisualElement
    {
        public new class UxmlFactory : UxmlFactory<WaveformView, UxmlTraits> { }
        public new class UxmlTraits : VisualElement.UxmlTraits { }

        private float[] _peaks;
        private int _peakCount;

        private readonly List<float> _markers01 = new();
        // -1 means "hidden". (So we don't draw a confusing default line at t=0.)
        private float _playhead01 = -1f;

        public Color WaveColor = new(0.90f, 0.92f, 0.95f, 1f);
        public Color MarkerColor = new(0.42f, 0.89f, 1f, 1f);
        public Color PlayheadColor = new(1f, 0.36f, 0.39f, 1f);

        /// <summary>
        /// Fired when the user interacts with the waveform using ONE finger/mouse.
        /// t01 is absolute timeline normalized [0..1].
        /// isDrag true on move while pressed.
        /// </summary>
        public event Action<float, bool> UserPointer01;
        public event Action<int, float, bool> MarkerDrag01;

        private readonly VisualElement _markerLabelLayer;
        private readonly List<Label> _markerLabels = new();

        private bool _pointerDown;
        private int _pointerIdPrimary = -1;
        private bool _draggingMarker;
        private int _dragMarkerIndex = -1;
        private int _activeMarkerIndex = -1;
        private const float MarkerGrabPx = 18f;

        public bool MarkersDraggable { get; set; } = false;

        // --- Zoom window in timeline-normalized space ---
        // View maps [ViewStart01 .. ViewStart01+ViewLen01] to the control width.
        public float ViewStart01 { get; private set; } = 0f;
        public float ViewLen01 { get; private set; } = 1f;

        // Limit zoom (smaller = more zoom-in)
        private const float MinViewLen01 = 1f / 32f;   // max zoom-in ~32x
        private const float MaxViewLen01 = 1f;         // zoomed out = full track

        // --- Pinch/pan (2 pointers) ---
        private bool _pinching;
        private int _p1 = -1, _p2 = -1;
        private Vector2 _p1Pos, _p2Pos;
        private float _pinchStartDist;
        private float _pinchStartViewLen;
        private float _pinchStartViewStart;
        private float _pinchAnchorTimeline01; // the timeline position under pinch midpoint at pinch start
        private Vector2 _pinchStartMid;

        public WaveformView()
        {
            style.flexGrow = 1;
            style.height = 320;
            style.marginTop = 8;
            style.marginBottom = 8;

            pickingMode = PickingMode.Position;
            focusable = true;

            generateVisualContent += OnGenerate;

            _markerLabelLayer = new VisualElement();
            _markerLabelLayer.pickingMode = PickingMode.Ignore;
            _markerLabelLayer.style.position = Position.Absolute;
            _markerLabelLayer.style.left = 0;
            _markerLabelLayer.style.right = 0;
            _markerLabelLayer.style.top = 0;
            _markerLabelLayer.style.bottom = 0;
            Add(_markerLabelLayer);

            RegisterCallback<GeometryChangedEvent>(_ => UpdateMarkerLabels());
            RegisterCallback<PointerDownEvent>(OnPointerDown);
            RegisterCallback<PointerMoveEvent>(OnPointerMove);
            RegisterCallback<PointerUpEvent>(OnPointerUp);
            RegisterCallback<PointerCancelEvent>(OnPointerCancel);

            // Editor convenience: mouse wheel zoom
            RegisterCallback<WheelEvent>(OnWheel);
        }

        /// <summary>
        /// Set playhead position in absolute normalized timeline [0..1].
        /// Pass a negative value (e.g. -1) to hide the playhead.
        /// </summary>
        public void SetPlayhead01(float t01)
        {
            _playhead01 = (t01 < 0f) ? -1f : Mathf.Clamp01(t01);
            MarkDirtyRepaint();
        }

        public void SetMarkers01(IEnumerable<float> markers01)
        {
            _markers01.Clear();
            if (markers01 != null)
            {
                foreach (var m in markers01)
                {
                    // 0 and 1 are implied boundaries for chops; don't draw them as "user markers".
                    float t = Mathf.Clamp01(m);
                    if (t <= 0.0005f) continue;
                    if (t >= 0.9995f) continue;
                    _markers01.Add(t);
                }
            }
            _markers01.Sort();
            UpdateMarkerLabels();
            MarkDirtyRepaint();
        }

        public void ResetZoom()
        {
            ViewStart01 = 0f;
            ViewLen01 = 1f;
            UpdateMarkerLabels();
            MarkDirtyRepaint();
        }

        public void SetClip(AudioClip clip, int targetSamples = 2048)
        {
            _peaks = null;
            _peakCount = 0;

            ResetZoom();

            if (clip == null || clip.samples <= 0) { MarkDirtyRepaint(); return; }

            var channels = clip.channels;
            var data = new float[clip.samples * channels];
            clip.GetData(data, 0);

            _peakCount = Mathf.Max(128, targetSamples);
            _peaks = new float[_peakCount * 2];

            int samplesPerBucket = Mathf.Max(1, clip.samples / _peakCount);

            for (int i = 0; i < _peakCount; i++)
            {
                int start = i * samplesPerBucket * channels;
                int end = Mathf.Min(data.Length, start + samplesPerBucket * channels);

                float min = 0f, max = 0f;
                for (int s = start; s < end; s += channels)
                {
                    float v = data[s];
                    if (v < min) min = v;
                    if (v > max) max = v;
                }

                _peaks[i * 2] = min;
                _peaks[i * 2 + 1] = max;
            }

            MarkDirtyRepaint();
        }

        private void OnGenerate(MeshGenerationContext ctx)
        {
            var r = contentRect;
            if (r.width <= 2 || r.height <= 2) return;

            var painter = ctx.painter2D;

            float v0 = ViewStart01;
            float v1 = Mathf.Clamp01(ViewStart01 + ViewLen01);
            float vLen = Mathf.Max(1e-6f, v1 - v0);

            // Waveform: draw only the visible window range
            if (_peaks != null && _peakCount > 0)
            {
                painter.lineWidth = 1.5f;
                painter.strokeColor = WaveColor;

                float midY = r.y + r.height * 0.5f;
                float halfH = r.height * 0.45f;

                // Map window into peak indices
                int i0 = Mathf.Clamp(Mathf.FloorToInt(v0 * (_peakCount - 1)), 0, _peakCount - 1);
                int i1 = Mathf.Clamp(Mathf.CeilToInt(v1 * (_peakCount - 1)), 0, _peakCount - 1);
                int span = Mathf.Max(1, i1 - i0);

                for (int i = 0; i <= span; i++)
                {
                    int idx = i0 + i;
                    if (idx < 0 || idx >= _peakCount) continue;

                    float tWin = i / (float)span; // 0..1 across visible window
                    float px = r.x + tWin * r.width;

                    float min = _peaks[idx * 2];
                    float max = _peaks[idx * 2 + 1];

                    float y1p = midY + min * halfH;
                    float y2p = midY + max * halfH;

                    painter.BeginPath();
                    painter.MoveTo(new Vector2(px, y1p));
                    painter.LineTo(new Vector2(px, y2p));
                    painter.Stroke();
                }
            }

            // Markers (only those inside window)
            if (_markers01.Count > 0)
            {
                painter.lineWidth = 2f;
                painter.strokeColor = MarkerColor;

                for (int i = 0; i < _markers01.Count; i++)
                {
                    float m = _markers01[i];
                    if (m < v0 || m > v1) continue;
                    float tWin = (m - v0) / vLen;
                    float px = r.x + Mathf.Clamp01(tWin) * r.width;

                    painter.lineWidth = (i == _activeMarkerIndex) ? 4f : 2f;
                    painter.BeginPath();
                    painter.MoveTo(new Vector2(px, r.y));
                    painter.LineTo(new Vector2(px, r.y + r.height));
                    painter.Stroke();
                }
            }

            // Playhead (hidden when _playhead01 < 0)
            if (_playhead01 >= 0f)
            {
                painter.lineWidth = 2.5f;
                painter.strokeColor = PlayheadColor;

                float ph = _playhead01;
                float tWin = (ph - v0) / vLen;
                float px = r.x + Mathf.Clamp01(tWin) * r.width;

                painter.BeginPath();
                painter.MoveTo(new Vector2(px, r.y));
                painter.LineTo(new Vector2(px, r.y + r.height));
                painter.Stroke();
            }
        }

        // --- Input ---
        private void OnPointerDown(PointerDownEvent evt)
        {
            if (evt == null) return;
            Focus();

            // Track up to 2 pointers
            if (_p1 == -1)
            {
                _p1 = evt.pointerId;
                _p1Pos = evt.localPosition;
            }
            else if (_p2 == -1 && evt.pointerId != _p1)
            {
                _p2 = evt.pointerId;
                _p2Pos = evt.localPosition;
                BeginPinch();
            }

            // If we are NOT pinching, treat first pointer as primary for scrub/tap/marker-drag.
            if (!_pinching)
            {
                _pointerDown = true;
                _pointerIdPrimary = evt.pointerId;

                float t01 = PositionToTimeline01(evt.localPosition);
                int markerIndex = MarkersDraggable ? FindMarkerIndexNear(evt.localPosition) : -1;
                if (markerIndex >= 0)
                {
                    _draggingMarker = true;
                    _dragMarkerIndex = markerIndex;
                    _activeMarkerIndex = markerIndex;
                    MarkerDrag01?.Invoke(markerIndex, t01, false);
                    UpdateMarkerLabels();
                    MarkDirtyRepaint();
                }
                else
                {
                    _draggingMarker = false;
                    _dragMarkerIndex = -1;
                    _activeMarkerIndex = -1;
                    UpdateMarkerLabels();
                    UserPointer01?.Invoke(t01, false);
                }
            }

            evt.StopPropagation();
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (evt == null) return;

            // update pointer positions
            if (evt.pointerId == _p1) _p1Pos = evt.localPosition;
            if (evt.pointerId == _p2) _p2Pos = evt.localPosition;

            if (_pinching)
            {
                UpdatePinch();
                evt.StopPropagation();
                return;
            }

            if (_pointerDown && evt.pointerId == _pointerIdPrimary)
            {
                float t01 = PositionToTimeline01(evt.localPosition);
                if (_draggingMarker && _dragMarkerIndex >= 0)
                    MarkerDrag01?.Invoke(_dragMarkerIndex, t01, true);
                else
                    UserPointer01?.Invoke(t01, true);
                evt.StopPropagation();
            }
        }

        private void OnPointerUp(PointerUpEvent evt)
        {
            if (evt == null) return;

            if (evt.pointerId == _p1) _p1 = -1;
            if (evt.pointerId == _p2) _p2 = -1;

            if (_pinching && (_p1 == -1 || _p2 == -1))
                EndPinch();

            if (evt.pointerId == _pointerIdPrimary)
            {
                if (_draggingMarker && _dragMarkerIndex >= 0)
                {
                    float t01 = PositionToTimeline01(evt.localPosition);
                    MarkerDrag01?.Invoke(_dragMarkerIndex, t01, false);
                }

                _pointerDown = false;
                _pointerIdPrimary = -1;
                _draggingMarker = false;
                _dragMarkerIndex = -1;
                _activeMarkerIndex = -1;
                UpdateMarkerLabels();
                MarkDirtyRepaint();
            }
        }

        private void OnPointerCancel(PointerCancelEvent evt)
        {
            _pointerDown = false;
            _pointerIdPrimary = -1;
            _draggingMarker = false;
            _dragMarkerIndex = -1;
            _activeMarkerIndex = -1;
            UpdateMarkerLabels();

            _p1 = -1;
            _p2 = -1;
            EndPinch();
        }

        private void OnWheel(WheelEvent evt)
        {
            // Editor only: zoom under mouse
            if (evt == null) return;

            float mouseTimeline01 = PositionToTimeline01(evt.localMousePosition);
            float dir = Mathf.Sign(evt.delta.y); // wheel down positive usually
            float factor = (dir > 0f) ? 1.12f : 0.89f; // down = zoom out, up = zoom in

            ZoomAround(mouseTimeline01, factor);
            evt.StopPropagation();
        }

        private void BeginPinch()
        {
            _pinching = true;

            _pinchStartDist = Vector2.Distance(_p1Pos, _p2Pos);
            _pinchStartViewLen = ViewLen01;
            _pinchStartViewStart = ViewStart01;

            _pinchStartMid = (_p1Pos + _p2Pos) * 0.5f;
            _pinchAnchorTimeline01 = PositionToTimeline01(_pinchStartMid);
        }

        private void UpdatePinch()
        {
            float dist = Vector2.Distance(_p1Pos, _p2Pos);
            if (_pinchStartDist <= 1e-3f) return;

            // pinch scale: dist larger -> zoom in (smaller view len)
            float scale = dist / _pinchStartDist;
            float newLen = _pinchStartViewLen / Mathf.Max(0.25f, scale);
            newLen = Mathf.Clamp(newLen, MinViewLen01, MaxViewLen01);

            // pan: midpoint delta shifts view window
            Vector2 mid = (_p1Pos + _p2Pos) * 0.5f;
            float dx = (mid.x - _pinchStartMid.x);

            // convert pixel delta to timeline delta based on current len
            float w = Mathf.Max(1f, contentRect.width);
            float panTimeline = -(dx / w) * newLen; // drag right pans window left (content follows fingers)

            // keep anchor stable while zooming
            float anchor = _pinchAnchorTimeline01;
            float start = anchor - newLen * ((anchor - _pinchStartViewStart) / Mathf.Max(1e-6f, _pinchStartViewLen));
            start += panTimeline;

            SetViewWindow(start, newLen);
        }

        private void EndPinch()
        {
            _pinching = false;
        }

        private void ZoomAround(float anchorTimeline01, float factor)
        {
            float newLen = Mathf.Clamp(ViewLen01 * factor, MinViewLen01, MaxViewLen01);

            // keep anchor under the same relative x
            float t = Mathf.InverseLerp(ViewStart01, ViewStart01 + ViewLen01, anchorTimeline01);
            float newStart = anchorTimeline01 - t * newLen;

            SetViewWindow(newStart, newLen);
        }

        private void SetViewWindow(float start01, float len01)
        {
            len01 = Mathf.Clamp(len01, MinViewLen01, MaxViewLen01);

            float maxStart = 1f - len01;
            start01 = Mathf.Clamp(start01, 0f, Mathf.Max(0f, maxStart));

            ViewStart01 = start01;
            ViewLen01 = len01;

            UpdateMarkerLabels();
            MarkDirtyRepaint();
        }


        private void UpdateMarkerLabels()
        {
            if (_markerLabelLayer == null)
                return;

            while (_markerLabels.Count < _markers01.Count)
            {
                var lbl = new Label();
                lbl.pickingMode = PickingMode.Ignore;
                lbl.style.position = Position.Absolute;
                lbl.style.top = 2;
                lbl.style.unityFontStyleAndWeight = FontStyle.Bold;
                lbl.style.fontSize = 12;
                lbl.style.color = MarkerColor;
                lbl.style.backgroundColor = new Color(0f, 0f, 0f, 0.45f);
                lbl.style.paddingLeft = 4;
                lbl.style.paddingRight = 4;
                lbl.style.paddingTop = 1;
                lbl.style.paddingBottom = 1;
                lbl.style.borderTopLeftRadius = 4;
                lbl.style.borderTopRightRadius = 4;
                lbl.style.borderBottomLeftRadius = 4;
                lbl.style.borderBottomRightRadius = 4;
                _markerLabels.Add(lbl);
                _markerLabelLayer.Add(lbl);
            }

            var r = contentRect;
            float v0 = ViewStart01;
            float v1 = Mathf.Clamp01(ViewStart01 + ViewLen01);
            float vLen = Mathf.Max(1e-6f, v1 - v0);

            for (int i = 0; i < _markerLabels.Count; i++)
            {
                var lbl = _markerLabels[i];
                if (i >= _markers01.Count)
                {
                    lbl.style.display = DisplayStyle.None;
                    continue;
                }

                float m = _markers01[i];
                if (r.width <= 2f || m < v0 || m > v1)
                {
                    lbl.style.display = DisplayStyle.None;
                    continue;
                }

                float tWin = (m - v0) / vLen;
                float px = Mathf.Clamp01(tWin) * r.width;

                lbl.text = $"{(i + 1):00}";
                lbl.style.display = DisplayStyle.Flex;
                lbl.style.left = Mathf.Max(0f, px - 12f);
                lbl.style.color = (i == _activeMarkerIndex) ? Color.white : MarkerColor;
            }
        }

        private int FindMarkerIndexNear(Vector2 localPos)
        {
            var r = contentRect;
            if (r.width <= 1f || _markers01.Count == 0)
                return -1;

            float v0 = ViewStart01;
            float v1 = Mathf.Clamp01(ViewStart01 + ViewLen01);
            float vLen = Mathf.Max(1e-6f, v1 - v0);
            float bestPx = MarkerGrabPx;
            int bestIndex = -1;

            for (int i = 0; i < _markers01.Count; i++)
            {
                float m = _markers01[i];
                if (m < v0 || m > v1) continue;

                float tWin = (m - v0) / vLen;
                float px = r.x + Mathf.Clamp01(tWin) * r.width;
                float d = Mathf.Abs(localPos.x - px);
                if (d <= bestPx)
                {
                    bestPx = d;
                    bestIndex = i;
                }
            }

            return bestIndex;
        }

        // Convert local x into absolute timeline position based on view window.
        private float PositionToTimeline01(Vector2 localPos)
        {
            var r = contentRect;
            if (r.width <= 1f) return 0f;

            float x = Mathf.Clamp(localPos.x - r.x, 0f, r.width);
            float tWin = Mathf.Clamp01(x / r.width);

            return Mathf.Clamp01(ViewStart01 + tWin * ViewLen01);
        }
    }
}