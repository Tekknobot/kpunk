using System;
using System.Threading;
using UnityEngine;

namespace ZPunk.DAW
{
    /// <summary>
    /// Read-only post-mix scope input. Installed on the active AudioListener.
    /// OnAudioFilterRead never allocates, takes locks, or changes the audio samples.
    /// Each ring entry holds the signed min/max of 64 output frames, so the UI
    /// displays a genuine waveform envelope from the combined master output.
    /// Export capture, mixer gains and audio routing are untouched.
    /// </summary>
    [RequireComponent(typeof(AudioListener))]
    public sealed class ZPunkMasterAudioTap : MonoBehaviour
    {
        private const int RingSize = 2048; // power of two, ~2.7 seconds @ 48 kHz
        private const int FramesPerBin = 64;
        // The scope is optional: when closed the callback returns immediately.
        public volatile bool CaptureEnabled;
        private readonly float[] lows = new float[RingSize];
        private readonly float[] highs = new float[RingSize];
        private int published;
        private int partialFrames;
        private float partialLow, partialHigh;

        private void OnAudioFilterRead(float[] samples, int channels)
        {
            if (!CaptureEnabled || samples == null || channels < 1) return;
            int frames = samples.Length / channels;
            int position = 0;
            for (int frame = 0; frame < frames; ++frame)
            {
                // Read the loudest signed sample across the available channels.
                // Unlike a simple average, this preserves out-of-phase stereo.
                float value = 0f;
                for (int channel = 0; channel < channels; ++channel)
                {
                    float sample = samples[position++];
                    if (Math.Abs(sample) > Math.Abs(value)) value = sample;
                }
                if (value < partialLow) partialLow = value;
                if (value > partialHigh) partialHigh = value;
                if (++partialFrames < FramesPerBin) continue;

                int index = published & (RingSize - 1);
                lows[index] = partialLow;
                highs[index] = partialHigh;
                // Publishing the index last ensures the UI reads completed bins.
                Volatile.Write(ref published, published + 1);
                partialFrames = 0;
                partialLow = 0f;
                partialHigh = 0f;
            }
        }

        private void OnDisable() { CaptureEnabled = false; }

        /// <summary>Copies the newest waveform bins to caller-owned arrays.</summary>
        public int CopyLatest(float[] outLows, float[] outHighs)
        {
            if (outLows == null || outHighs == null) return 0;
            int count = Math.Min(outLows.Length, outHighs.Length);
            int end = Volatile.Read(ref published);
            int valid = Math.Min(Math.Min(end, RingSize), count);
            int first = end - valid;
            int empty = count - valid;
            if (empty > 0)
            {
                Array.Clear(outLows, 0, empty);
                Array.Clear(outHighs, 0, empty);
            }
            for (int i = 0; i < valid; ++i)
            {
                int source = (first + i) & (RingSize - 1);
                outLows[empty + i] = lows[source];
                outHighs[empty + i] = highs[source];
            }
            return end;
        }
    }
}
