using System;
using System.Threading;
using UnityEngine;

namespace ZPunk.DAW
{
    /// <summary>
    /// Final listener bus processor, live peak/GR meter and scope source.
    /// This callback processes audio exactly once. WAV capture is explicitly
    /// forwarded the post-processor buffer; Unity component order is irrelevant.
    /// No locks, heap allocations or engine API calls in the audio callback.
    /// The always-on ceiling is a SAMPLE peak ceiling, not a true-peak guarantee.
    /// </summary>
    [RequireComponent(typeof(AudioListener))]
    public sealed class ZPunkMasterAudioTap : MonoBehaviour
    {
        private const int RingSize = 2048;
        private const int FramesPerBin = 64;
        private const float Ceiling = 0.89f; // ~ -1.01 dBFS sample peak
        public volatile bool CaptureEnabled;
        public static volatile float GlueAmount;
        public static volatile float DriveAmount;
        public static volatile float WidthOffset; // -1..1 -> 70..130% sides
        public static volatile float OutputTrimDb;
        public static volatile bool BusFxBypassed;

        // Installed/cleared on the main thread by ZPunkWavCapture.
        // Single-processor ownership avoids dependence on Unity component order.
        public volatile ZPunkWavCapture ExportCapture;
        // Isolated stems retain their own sound, but not nonlinear MASTER bus FX.
        // They still receive sample-peak protection for 16-bit WAV safety.
        public volatile bool StemExport;
        public volatile float PeakBeforeProtection;
        public volatile float PeakAfterProtection;
        public volatile float GainReductionDb;
        public volatile float LimiterReductionDb;

        private int sampleRate = 48000;
        private float compressorEnvelope;
        private float limiterGain = 1f;
        private float smoothedDrive;
        private float smoothedGlue;
        private float smoothedWidth = 1f;
        private float smoothedTrim = 1f;
        private float stereoSideLowpass;
        private readonly float[] lows = new float[RingSize];
        private readonly float[] highs = new float[RingSize];
        private int published;
        private int partialFrames;
        private float partialLow, partialHigh;

        public static void ApplySongSettings(SongData song)
        {
            GlueAmount = song != null ? Mathf.Clamp01(song.masterGlue) : 0f;
            DriveAmount = song != null ? Mathf.Clamp01(song.masterDrive) : 0f;
            WidthOffset = song != null ? Mathf.Clamp(song.masterWidthOffset, -1f, 1f) : 0f;
            OutputTrimDb = song != null ? Mathf.Clamp(song.masterOutputTrimDb, -12f, 3f) : 0f;
            BusFxBypassed = song != null && song.masterFxBypassed;
        }

        private void OnEnable()
        {
            sampleRate = Math.Max(8000, AudioSettings.outputSampleRate);
            compressorEnvelope = 0f;
            limiterGain = 1f;
            ResetFxSmoothing();
        }

        private void ResetFxSmoothing()
        {
            bool fx = !StemExport && !BusFxBypassed;
            smoothedDrive = fx ? Math.Max(0f, Math.Min(1f, DriveAmount)) : 0f;
            smoothedGlue = fx ? Math.Max(0f, Math.Min(1f, GlueAmount)) : 0f;
            smoothedWidth = fx ? 1f + Math.Max(-1f, Math.Min(1f, WidthOffset)) * .30f : 1f;
            smoothedTrim = fx ? (float)Math.Pow(10.0,
                Math.Max(-12f, Math.Min(3f, OutputTrimDb)) / 20.0) : 1f;
            stereoSideLowpass = 0f;
        }

        // Call before a new render pass to prevent previous passes leaking
        // release-envelope gain into the start of an isolated stem.
        public void ResetDynamics()
        {
            compressorEnvelope = 0f;
            limiterGain = 1f;
            ResetFxSmoothing();
        }

        private void OnAudioFilterRead(float[] samples, int channels)
        {
            if (samples == null || channels < 1) return;
            int frames = samples.Length / channels;
            bool fx = !StemExport && !BusFxBypassed;
            float glue = fx ? Math.Max(0f, Math.Min(1f, GlueAmount)) : 0f;
            float drive = fx ? Math.Max(0f, Math.Min(1f, DriveAmount)) : 0f;
            float width = fx ? 1f + Math.Max(-1f, Math.Min(1f, WidthOffset)) * .30f : 1f;
            float trim = fx ? (float)Math.Pow(10.0, Math.Max(-12f, Math.Min(3f, OutputTrimDb)) / 20.0) : 1f;
            int rate = sampleRate;
            // ~15 ms target smoothing prevents zipper clicks from stepped knobs.
            float controlSlew = 1f - (float)Math.Exp(-1.0 / (rate * .015));
            // Widen the upper stereo information, preserving low-frequency side
            // content to avoid spreading kicks/bass across the stereo field.
            float sideFilterKeep = (float)Math.Exp(-2.0 * Math.PI * 150.0 / rate);
            float compressorRelease = (float)Math.Exp(-1.0 / (rate * .14));
            float limiterRelease = 1f - (float)Math.Exp(-1.0 / (rate * .075));
            float before = 0f, after = 0f, totalReduction = 0f, limitReduction = 0f;

            for (int frame = 0; frame < frames; ++frame)
            {
                smoothedDrive += (drive - smoothedDrive) * controlSlew;
                smoothedGlue += (glue - smoothedGlue) * controlSlew;
                smoothedWidth += (width - smoothedWidth) * controlSlew;
                smoothedTrim += (trim - smoothedTrim) * controlSlew;
                float saturateDrive = smoothedDrive * 1.4f;
                int offset = frame * channels;
                // The shape is deliberately mild; 0% is a strict linear bypass.
                if (smoothedDrive > .0001f)
                {
                    for (int ch = 0; ch < channels; ch++)
                    {
                        int index = offset + ch;
                        float x = samples[index];
                        samples[index] = x * (1f + saturateDrive) /
                            (1f + saturateDrive * Math.Abs(x)) * (1f - .08f * smoothedDrive);
                    }
                }
                // Stereo width operates on side content above ~150 Hz. Bass stays
                // centered (or retains its original pan), with reliable mono sum.
                if (channels == 2)
                {
                    float rawSide = (samples[offset] - samples[offset + 1]) * .5f;
                    stereoSideLowpass = sideFilterKeep * stereoSideLowpass +
                        (1f - sideFilterKeep) * rawSide;
                    if (Math.Abs(smoothedWidth - 1f) > .0001f)
                    {
                        float mid = (samples[offset] + samples[offset + 1]) * .5f;
                        float side = stereoSideLowpass +
                            (rawSide - stereoSideLowpass) * smoothedWidth;
                        samples[offset] = mid + side;
                        samples[offset + 1] = mid - side;
                    }
                }
                float linkedPeak = 0f;
                for (int ch = 0; ch < channels; ch++)
                    linkedPeak = Math.Max(linkedPeak, Math.Abs(samples[offset + ch] * smoothedTrim));
                compressorEnvelope = Math.Max(linkedPeak, compressorEnvelope * compressorRelease);
                float compressorGain = 1f;
                if (smoothedGlue > .0001f && compressorEnvelope > .25f)
                {
                    float inverseSlope = (2f * smoothedGlue) / (1f + 2f * smoothedGlue);
                    compressorGain = (float)Math.Pow(.25f / compressorEnvelope, inverseSlope);
                }
                float peakBeforeLimiter = linkedPeak * compressorGain;
                before = Math.Max(before, peakBeforeLimiter);
                float requestedGain = peakBeforeLimiter > Ceiling
                    ? Ceiling / peakBeforeLimiter : 1f;
                // Zero-overshoot sample-peak limiting: immediate attack, smooth release.
                limiterGain = Math.Min(requestedGain,
                    Math.Min(1f, limiterGain + (1f - limiterGain) * limiterRelease));
                float resultGain = smoothedTrim * compressorGain * limiterGain;
                float attenuation = Math.Max(.00001f, compressorGain * limiterGain);
                totalReduction = Math.Max(totalReduction, -20f * (float)Math.Log10(attenuation));
                limitReduction = Math.Max(limitReduction,
                    -20f * (float)Math.Log10(Math.Max(.00001f, limiterGain)));
                for (int ch = 0; ch < channels; ch++)
                {
                    int index = offset + ch;
                    float output = samples[index] * resultGain;
                    // Last-resort numerical safety, never the primary limiter.
                    if (output > Ceiling) output = Ceiling;
                    else if (output < -Ceiling) output = -Ceiling;
                    samples[index] = output;
                    after = Math.Max(after, Math.Abs(output));
                }
            }
            PeakBeforeProtection = before;
            PeakAfterProtection = after;
            GainReductionDb = totalReduction;
            LimiterReductionDb = limitReduction;

            // Export must receive the precise post-master samples; never rely on
            // OnAudioFilterRead ordering among two components on a listener.
            var recorder = ExportCapture;
            if (!ReferenceEquals(recorder, null)) recorder.CaptureProcessed(samples, channels);
            if (!CaptureEnabled) return;
            int position = 0;
            for (int frame = 0; frame < frames; ++frame)
            {
                float value = 0f;
                for (int ch = 0; ch < channels; ch++)
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
                Volatile.Write(ref published, published + 1);
                partialFrames = 0;
                partialLow = partialHigh = 0f;
            }
        }

        private void OnDisable()
        {
            CaptureEnabled = false;
            StemExport = false;
        }

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
