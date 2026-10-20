using UnityEngine;

namespace ZPunk.DAW
{
    // Read actual audio energy, not an assumed constant amplitude from the native synth.
    [RequireComponent(typeof(AudioSource))]
    public sealed class ZPunkHelmMeterTap : MonoBehaviour
    {
        private volatile float rms;
        public float ReadPeak()
        {
            float value = rms;
            rms *= 0.60f;
            return value < 0.002f ? 0f : Mathf.Clamp01(value);
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (data == null || data.Length == 0) return;
            double sumSquares = 0.0;
            for (int i = 0; i < data.Length; ++i)
            {
                float v = data[i];
                if (float.IsNaN(v) || float.IsInfinity(v)) continue;
                sumSquares += (double)v * v;
            }
            float level = (float)System.Math.Sqrt(sumSquares / data.Length);
            rms = level < 0.002f ? 0f : Mathf.Min(level, 1f);
        }
    }
}
