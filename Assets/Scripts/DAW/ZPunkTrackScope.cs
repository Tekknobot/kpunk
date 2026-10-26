using UnityEngine;
using UnityEngine.UIElements;

namespace ZPunk.DAW
{
    /// <summary>
    /// Small, audio-driven CREATE track identity display. These are time histories
    /// of measured post-fader track peaks (not raw PCM oscilloscope samples).
    /// Each track keeps its own history; only the selected track is drawn.
    /// No audio callback, textures, allocations per sample, or synthetic motion.
    /// </summary>
    internal sealed class ZPunkTrackScope : VisualElement
    {
        private const int HistoryCount = 64;
        private readonly float[,] history = new float[5, HistoryCount];
        private readonly float[] smoothed = new float[5];
        private int selectedTrack = 1;
        private Color trackColor = new Color(.96f, .36f, .48f);

        internal ZPunkTrackScope()
        {
            name = "ZPunkTrackAudioScope";
            pickingMode = PickingMode.Ignore;
            style.flexGrow = 1;
            style.minWidth = 0;
            style.height = 36;
            style.backgroundColor = new Color(.045f, .065f, .095f);
            style.borderTopLeftRadius = 4;
            style.borderTopRightRadius = 4;
            style.borderBottomLeftRadius = 4;
            style.borderBottomRightRadius = 4;
            generateVisualContent += Paint;
        }

        internal void Select(int trackIndex, Color color)
        {
            if (trackIndex < 1 || trackIndex > 4) return;
            selectedTrack = trackIndex;
            trackColor = color;
            MarkDirtyRepaint();
        }

        internal void PushLevels(float bass, float synth, float pads, float chops)
        {
            Push(1, bass);
            Push(2, synth);
            Push(3, pads);
            Push(4, chops);
            MarkDirtyRepaint();
        }

        private void Push(int track, float peak)
        {
            // A decibel-scaled envelope makes quiet sounds visible, while staying
            // completely flat when the engine reports silence.
            float target = 0f;
            if (peak > .001f && !float.IsNaN(peak) && !float.IsInfinity(peak))
                target = Mathf.Clamp01((20f * Mathf.Log10(peak) + 54f) / 54f);
            float release = track == 3 ? .91f : (track == 4 ? .69f : .83f);
            smoothed[track] = target > smoothed[track] ? target : smoothed[track] * release;
            for (int i = 0; i < HistoryCount - 1; ++i)
                history[track, i] = history[track, i + 1];
            history[track, HistoryCount - 1] = smoothed[track];
        }

        private void Paint(MeshGenerationContext context)
        {
            float w = contentRect.width;
            float h = contentRect.height;
            if (w <= 4f || h <= 4f) return;
            float mid = h * .5f;
            var painter = context.painter2D;
            painter.lineCap = LineCap.Round;
            painter.lineWidth = 1f;
            painter.strokeColor = new Color(trackColor.r, trackColor.g, trackColor.b, .18f);
            painter.BeginPath();
            painter.MoveTo(new Vector2(2f, mid));
            painter.LineTo(new Vector2(w - 2f, mid));
            painter.Stroke();

            // CHOPS uses individual transient bars; the other tracks show a
            // mirrored peak-history trace. Different shapes, identical PCM basis.
            if (selectedTrack == 4)
            {
                painter.lineCap = LineCap.Butt;
                painter.lineWidth = Mathf.Max(1.5f, (w - 8f) / HistoryCount * .48f);
                painter.strokeColor = trackColor;
                painter.BeginPath();
                for (int i = 0; i < HistoryCount; ++i)
                {
                    float level = history[selectedTrack, i];
                    if (level < .01f) continue;
                    float x = 4f + (w - 8f) * i / (HistoryCount - 1f);
                    float extent = Mathf.Min(h * .42f, level * h * .42f);
                    painter.MoveTo(new Vector2(x, mid - extent));
                    painter.LineTo(new Vector2(x, mid + extent));
                }
                painter.Stroke();
                return;
            }

            painter.lineWidth = selectedTrack == 1 ? 2.2f : (selectedTrack == 3 ? 1.5f : 1.75f);
            painter.strokeColor = trackColor;
            for (int side = -1; side <= 1; side += 2)
            {
                painter.BeginPath();
                float startY = mid + side * history[selectedTrack, 0] * h * .42f;
                painter.MoveTo(new Vector2(4f, startY));
                for (int i = 1; i < HistoryCount; ++i)
                {
                    float x = 4f + (w - 8f) * i / (HistoryCount - 1f);
                    float y = mid + side * history[selectedTrack, i] * h * .42f;
                    // Bass: solid flat-topped pulses. Synth: clean angular
                    // transients. Pads: smooth, slow-moving curves.
                    if (selectedTrack == 1)
                    {
                        float previousY = mid + side * history[selectedTrack, i - 1] * h * .42f;
                        painter.LineTo(new Vector2(x, previousY));
                        painter.LineTo(new Vector2(x, y));
                    }
                    else if (selectedTrack == 3)
                    {
                        float prevX = 4f + (w - 8f) * (i - 1) / (HistoryCount - 1f);
                        float prevY = mid + side * history[selectedTrack, i - 1] * h * .42f;
                        float halfX = (prevX + x) * .5f;
                        painter.BezierCurveTo(new Vector2(halfX, prevY), new Vector2(halfX, y), new Vector2(x, y));
                    }
                    else painter.LineTo(new Vector2(x, y));
                }
                painter.Stroke();
            }
        }
    }
}
