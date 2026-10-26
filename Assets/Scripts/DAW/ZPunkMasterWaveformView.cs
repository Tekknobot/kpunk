using UnityEngine;
using UnityEngine.UIElements;

namespace ZPunk.DAW
{
    /// <summary>
    /// Draws the real master-output min/max waveform as a continuous, softly
    /// filled ribbon. Repaints only while its parent drawer is open, with no
    /// per-frame texture generation or managed allocations.
    /// </summary>
    internal sealed class ZPunkMasterWaveformView : VisualElement
    {
        private const int Columns = 192;
        private readonly float[] low = new float[Columns];
        private readonly float[] high = new float[Columns];
        private static readonly Color Bright = new Color(.42f, .98f, .84f, 1f);
        private static readonly Color Glow = new Color(.15f, .78f, .91f, .25f);
        private static readonly Color FillColor = new Color(.20f, .82f, .74f, .17f);
        private static readonly Color GridColor = new Color(.38f, .57f, .65f, .12f);
        private int previousWrite = -1;
        private float lastAdvance = -1000f;
        private float gain = 1f;
        private bool hasSignal;

        internal bool HasSignal => hasSignal;

        internal ZPunkMasterWaveformView()
        {
            name = "ZPunkMasterWaveformViewport";
            style.flexGrow = 1;
            style.minHeight = 0;
            style.minWidth = 0;
            style.backgroundColor = new Color(.033f, .051f, .078f);
            style.borderTopLeftRadius = 8;
            style.borderTopRightRadius = 8;
            style.borderBottomLeftRadius = 8;
            style.borderBottomRightRadius = 8;
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Paint;
        }

        internal void Sample(ZPunkMasterAudioTap input, float now)
        {
            if (input == null)
            {
                Clear();
                return;
            }
            int write = input.CopyLatest(low, high);
            if (write != previousWrite)
            {
                previousWrite = write;
                lastAdvance = now;
            }
            if (now - lastAdvance > .35f)
            {
                Clear();
                return;
            }
            float largest = 0f;
            for (int i = 0; i < Columns; ++i)
                largest = Mathf.Max(largest, Mathf.Max(-low[i], high[i]));
            // Slow auto gain raises quieter signals without amplifying silence.
            float target = largest > .001f ? Mathf.Clamp(.80f / largest, 1f, 5f) : 1f;
            gain = Mathf.Lerp(gain, target, .10f);
            hasSignal = largest > .001f;
            MarkDirtyRepaint();
        }

        internal void Clear()
        {
            System.Array.Clear(low, 0, low.Length);
            System.Array.Clear(high, 0, high.Length);
            gain = 1f;
            hasSignal = false;
            previousWrite = -1;
            MarkDirtyRepaint();
        }

        private void Paint(MeshGenerationContext context)
        {
            float width = contentRect.width;
            float height = contentRect.height;
            if (width < 24f || height < 24f) return;
            var p = context.painter2D;
            float left = 8f;
            float right = width - 8f;
            float top = 8f;
            float bottom = height - 8f;
            float center = height * .5f;
            float amplitude = height * .40f;

            p.lineWidth = 1f;
            p.strokeColor = GridColor;
            p.BeginPath();
            for (int row = 1; row < 4; ++row)
            {
                float y = Mathf.Lerp(top, bottom, row / 4f);
                p.MoveTo(new Vector2(left, y));
                p.LineTo(new Vector2(right, y));
            }
            for (int column = 1; column < 8; ++column)
            {
                float x = Mathf.Lerp(left, right, column / 8f);
                p.MoveTo(new Vector2(x, top));
                p.LineTo(new Vector2(x, bottom));
            }
            p.Stroke();

            float dx = (right - left) / (Columns - 1f);
            // The closed upper/lower outline is a translucent, fluid signal body.
            p.fillColor = FillColor;
            p.BeginPath();
            p.MoveTo(new Vector2(left, Upper(0, center, amplitude)));
            for (int i = 1; i < Columns; ++i)
                p.LineTo(new Vector2(left + i * dx, Upper(i, center, amplitude)));
            for (int i = Columns - 1; i >= 0; --i)
                p.LineTo(new Vector2(left + i * dx, Lower(i, center, amplitude)));
            p.ClosePath();
            p.Fill();

            // Light halo, then a restrained crisp edge. Curves follow measured
            // bin envelopes, not a simulated sine wave.
            for (int pass = 0; pass < 2; ++pass)
            {
                p.lineCap = LineCap.Round;
                p.lineJoin = LineJoin.Round;
                p.lineWidth = pass == 0 ? 3f : 1.4f;
                p.strokeColor = pass == 0 ? Glow : Bright;
                for (int side = 0; side < 2; ++side)
                {
                    p.BeginPath();
                    p.MoveTo(new Vector2(left, side == 0 ? Upper(0, center, amplitude) : Lower(0, center, amplitude)));
                    for (int i = 1; i < Columns; ++i)
                    {
                        float x = left + i * dx;
                        float y = side == 0 ? Upper(i, center, amplitude) : Lower(i, center, amplitude);
                        float previous = side == 0 ? Upper(i - 1, center, amplitude) : Lower(i - 1, center, amplitude);
                        float before = x - dx;
                        float middle = (before + x) * .5f;
                        p.BezierCurveTo(new Vector2(middle, previous), new Vector2(middle, y), new Vector2(x, y));
                    }
                    p.Stroke();
                }
            }
        }

        private float Upper(int i, float center, float amplitude)
        {
            float value = Mathf.Clamp01(high[i] * gain);
            return center - value * amplitude;
        }
        private float Lower(int i, float center, float amplitude)
        {
            float value = Mathf.Clamp01(-low[i] * gain);
            return center + value * amplitude;
        }
    }
}
