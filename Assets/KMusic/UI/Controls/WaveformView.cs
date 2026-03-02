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
        /// Fired when the user touches/clicks on the waveform.
        /// t01 is normalized [0..1] across the visible content rect.
        /// isDrag is true for pointer-move scrubbing while pressed.
        /// </summary>
        public event Action<float, bool> UserPointer01;

        private bool _pointerDown;

        public WaveformView()
        {
            style.flexGrow = 1;
            style.height = 320;
            style.marginTop = 8;
            style.marginBottom = 8;

            // ✅ Ensure this element receives pointer events.
            pickingMode = PickingMode.Position;
            focusable = true;

            generateVisualContent += OnGenerate;

            RegisterCallback<PointerDownEvent>(OnPointerDown);
            RegisterCallback<PointerMoveEvent>(OnPointerMove);
            RegisterCallback<PointerUpEvent>(OnPointerUp);
            RegisterCallback<PointerCancelEvent>(OnPointerCancel);
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

        public void SetClip(AudioClip clip, int targetSamples = 2048)
        {
            _peaks = null;
            _peakCount = 0;

            if (clip == null || clip.samples <= 0) { MarkDirtyRepaint(); return; }

            var channels = clip.channels;
            var data = new float[clip.samples * channels];
            clip.GetData(data, 0);

            _peakCount = Mathf.Max(64, targetSamples);
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

            // Waveform
            if (_peaks != null && _peakCount > 0)
            {
                painter.lineWidth = 1.5f;
                painter.strokeColor = WaveColor;

                float midY = r.y + r.height * 0.5f;
                float halfH = r.height * 0.45f;

                for (int x = 0; x < _peakCount; x++)
                {
                    float t = (x / (float)(_peakCount - 1));
                    float px = r.x + t * r.width;

                    float min = _peaks[x * 2];
                    float max = _peaks[x * 2 + 1];

                    float y1 = midY + min * halfH;
                    float y2 = midY + max * halfH;

                    painter.BeginPath();
                    painter.MoveTo(new Vector2(px, y1));
                    painter.LineTo(new Vector2(px, y2));
                    painter.Stroke();
                }
            }

            // Markers
            if (_markers01.Count > 0)
            {
                painter.lineWidth = 2f;
                painter.strokeColor = MarkerColor;

                foreach (var m in _markers01)
                {
                    float px = r.x + Mathf.Clamp01(m) * r.width;
                    painter.BeginPath();
                    painter.MoveTo(new Vector2(px, r.y));
                    painter.LineTo(new Vector2(px, r.y + r.height));
                    painter.Stroke();
                }
            }

            // Playhead
            painter.lineWidth = 2.5f;
            painter.strokeColor = PlayheadColor;

            float phx = r.x + Mathf.Clamp01(_playhead01) * r.width;
            painter.BeginPath();
            painter.MoveTo(new Vector2(phx, r.y));
            painter.LineTo(new Vector2(phx, r.y + r.height));
            painter.Stroke();
        }

        private void OnPointerDown(PointerDownEvent evt)
        {
            if (evt == null) return;

            _pointerDown = true;
            Focus();

            float t01 = PositionTo01(evt.localPosition);
            UserPointer01?.Invoke(t01, false);

            evt.StopPropagation();
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (!_pointerDown || evt == null) return;

            float t01 = PositionTo01(evt.localPosition);
            UserPointer01?.Invoke(t01, true);

            evt.StopPropagation();
        }

        private void OnPointerUp(PointerUpEvent evt)
        {
            _pointerDown = false;
        }

        private void OnPointerCancel(PointerCancelEvent evt)
        {
            _pointerDown = false;
        }

        private float PositionTo01(Vector2 localPos)
        {
            var r = contentRect;
            if (r.width <= 1f) return 0f;

            float x = Mathf.Clamp(localPos.x - r.x, 0f, r.width);
            return Mathf.Clamp01(x / r.width);
        }
    }
}