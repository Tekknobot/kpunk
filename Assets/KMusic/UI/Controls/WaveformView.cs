// Assets/KMusic/UI/Controls/WaveformView.cs
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace KMusic.UI
{
    // UXML: <km:WaveformView name="Waveform" />
    public class WaveformView : VisualElement
    {
        public new class UxmlFactory : UxmlFactory<WaveformView, UxmlTraits> { }
        public new class UxmlTraits : VisualElement.UxmlTraits { }

        // Peaks packed as [-1..1] amplitudes, interleaved min/max per x pixel sample:
        // peaks[i*2] = min, peaks[i*2+1] = max
        private float[] _peaks;
        private int _peakCount;

        private readonly List<float> _markers01 = new(); // normalized 0..1
        private float _playhead01 = 0f;

        public Color WaveColor = new(0.90f, 0.92f, 0.95f, 1f);
        public Color MarkerColor = new(0.42f, 0.89f, 1f, 1f);
        public Color PlayheadColor = new(1f, 0.36f, 0.39f, 1f);

        public WaveformView()
        {
            style.flexGrow = 1;
            style.height = 160;
            style.marginTop = 8;
            style.marginBottom = 8;

            generateVisualContent += OnGenerate;
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

        // Build peaks from AudioClip (call from coroutine on main thread).
        public void SetClip(AudioClip clip, int targetSamples = 2048)
        {
            _peaks = null;
            _peakCount = 0;

            if (clip == null || clip.samples <= 0) { MarkDirtyRepaint(); return; }

            // Read full clip (OK for now; optimize later if needed)
            var channels = clip.channels;
            var data = new float[clip.samples * channels];
            clip.GetData(data, 0);

            _peakCount = Mathf.Max(64, targetSamples);
            _peaks = new float[_peakCount * 2];

            int samplesPerBucket = Mathf.Max(1, clip.samples / _peakCount);

            for (int i = 0; i < _peakCount; i++)
            {
                int start = i * samplesPerBucket;
                int end = Mathf.Min(clip.samples, start + samplesPerBucket);

                float mn = 1f;
                float mx = -1f;

                for (int s = start; s < end; s++)
                {
                    // Mixdown channels by max abs
                    float v = 0f;
                    int baseIdx = s * channels;
                    for (int c = 0; c < channels; c++)
                        v = Mathf.Max(v, Mathf.Abs(data[baseIdx + c]));

                    // Re-expand to signed-ish display (symmetric)
                    mn = Mathf.Min(mn, -v);
                    mx = Mathf.Max(mx, v);
                }

                _peaks[i * 2] = mn;
                _peaks[i * 2 + 1] = mx;
            }

            MarkDirtyRepaint();
        }

        private void OnGenerate(MeshGenerationContext mgc)
        {
            var r = contentRect;
            if (r.width <= 2 || r.height <= 2) return;

            var painter = mgc.painter2D;
            painter.lineWidth = 1.5f;

            // Background faint border
            painter.strokeColor = new Color(1f, 1f, 1f, 0.08f);
            painter.BeginPath();
            painter.MoveTo(new Vector2(r.xMin, r.yMin));
            painter.LineTo(new Vector2(r.xMax, r.yMin));
            painter.LineTo(new Vector2(r.xMax, r.yMax));
            painter.LineTo(new Vector2(r.xMin, r.yMax));
            painter.ClosePath();
            painter.Stroke();

            // Waveform
            if (_peaks != null && _peakCount > 0)
            {
                painter.strokeColor = WaveColor;

                float midY = r.center.y;
                float halfH = (r.height * 0.45f);

                // Draw as vertical min/max lines
                int n = _peakCount;
                for (int i = 0; i < n; i++)
                {
                    float x = Mathf.Lerp(r.xMin, r.xMax, (i / (float)(n - 1)));
                    float mn = _peaks[i * 2];
                    float mx = _peaks[i * 2 + 1];

                    float y1 = midY + mn * halfH;
                    float y2 = midY + mx * halfH;

                    painter.BeginPath();
                    painter.MoveTo(new Vector2(x, y1));
                    painter.LineTo(new Vector2(x, y2));
                    painter.Stroke();
                }
            }

            // Markers
            painter.strokeColor = MarkerColor;
            painter.lineWidth = 2f;
            for (int i = 0; i < _markers01.Count; i++)
            {
                float x = Mathf.Lerp(r.xMin, r.xMax, _markers01[i]);
                painter.BeginPath();
                painter.MoveTo(new Vector2(x, r.yMin));
                painter.LineTo(new Vector2(x, r.yMax));
                painter.Stroke();
            }

            // Playhead
            painter.strokeColor = PlayheadColor;
            painter.lineWidth = 2.5f;
            {
                float x = Mathf.Lerp(r.xMin, r.xMax, _playhead01);
                painter.BeginPath();
                painter.MoveTo(new Vector2(x, r.yMin));
                painter.LineTo(new Vector2(x, r.yMax));
                painter.Stroke();
            }
        }
    }
}