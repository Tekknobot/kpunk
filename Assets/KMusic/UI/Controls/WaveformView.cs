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
        private float _playhead01 = 0f;

        public Color WaveColor = new(0.90f, 0.92f, 0.95f, 1f);
        public Color MarkerColor = new(0.42f, 0.89f, 1f, 1f);
        public Color PlayheadColor = new(1f, 0.36f, 0.39f, 1f);

        /// <summary>
        /// Fired when the user interacts with the waveform using ONE finger/mouse.
        /// t01 is absolute timeline normalized [0..1].
        /// isDrag true on move while pressed.
        /// </summary>
        public event Action<float, bool> UserPointer01;

        private bool _pointerDown;
        private int _pointerIdPrimary = -1;

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

            RegisterCallback<PointerDownEvent>(OnPointerDown);
            RegisterCallback<PointerMoveEvent>(OnPointerMove);
            RegisterCallback<PointerUpEvent>(OnPointerUp);
            RegisterCallback<PointerCancelEvent>(OnPointerCancel);

            // Editor convenience: mouse wheel zoom
            RegisterCallback<WheelEvent>(OnWheel);
        }

        public void SetPlayhead01(float t01)
        {
            _playhead01 = Mathf.Clamp01(t01);
            MarkDirtyRepaint();
        }

        public void SetMarkers01(IEnumerable<float> markers01)
        {
            _markers01.Clear();
            if (markers01 != null)
            {
                foreach (var m in markers01)
                    _markers01.Add(Mathf.Clamp01(m));
            }
            _markers01.Sort();
            MarkDirtyRepaint();
        }

        public void ResetZoom()
        {
            ViewStart01 = 0f;
            ViewLen01 = 1f;
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

                foreach (var m in _markers01)
                {
                    if (m < v0 || m > v1) continue;
                    float tWin = (m - v0) / vLen;
                    float px = r.x + Mathf.Clamp01(tWin) * r.width;

                    painter.BeginPath();
                    painter.MoveTo(new Vector2(px, r.y));
                    painter.LineTo(new Vector2(px, r.y + r.height));
                    painter.Stroke();
                }
            }

            // Playhead (only draw if inside window; otherwise clamp to edge for “where am I”)
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

            // If we are NOT pinching, treat first pointer as primary for scrub/tap
            if (!_pinching)
            {
                _pointerDown = true;
                _pointerIdPrimary = evt.pointerId;

                float t01 = PositionToTimeline01(evt.localPosition);
                UserPointer01?.Invoke(t01, false);
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
                _pointerDown = false;
                _pointerIdPrimary = -1;
            }
        }

        private void OnPointerCancel(PointerCancelEvent evt)
        {
            _pointerDown = false;
            _pointerIdPrimary = -1;

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

            MarkDirtyRepaint();
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