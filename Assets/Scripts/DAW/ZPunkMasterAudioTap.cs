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
        // Master performance FX from ARRANGEMENT. The four amounts default to
        // strict bypass; values are copied from SongData on the main thread.
        public static volatile float PerformanceDrive;
        public static volatile float PerformanceGate;
        public static volatile float PerformanceCrush;
        public static volatile float PerformanceRepeat;
        public static volatile int PerformanceGateRate = 2;
        public static volatile int PerformanceRepeatRate = 2;
        public static volatile float PerformanceBpm = 120f;
        // A BAR setting follows the song time signature (4 quarter notes for 4/4).
        public static volatile float PerformanceQuarterNotesPerBar = 4f;
        private static SongData performanceSourceSong;
        private static volatile int performanceSourceRevision;

        // Immutable transport snapshot, published only on the Unity main thread.
        // Audio callbacks can read an entire, consistent clock without locks.
        private sealed class ArrangementFxClock
        {
            public readonly bool playing;
            public readonly double anchorDsp;
            public readonly long startTick;
            public readonly double secondsPerTick;
            public ArrangementFxClock(bool active, double anchor, long start, double tickSeconds)
            {
                playing = active;
                anchorDsp = anchor;
                startTick = start;
                secondsPerTick = tickSeconds;
            }
        }

        private static volatile ArrangementFxClock arrangementFxClock;
        private static volatile int arrangementFxClockStartRevision;
        private static bool arrangementFxClockLastPlaying;

        // Called by the DSP-anchored arrangement transport at start, stop,
        // seek, tempo changes and loop handoffs. Never from the audio thread.
        public static void PublishArrangementClock(bool playing, double anchorDsp,
            long startTick, double secondsPerTick)
        {
            if (playing && !arrangementFxClockLastPlaying)
                System.Threading.Interlocked.Increment(ref arrangementFxClockStartRevision);
            arrangementFxClockLastPlaying = playing;
            arrangementFxClock = new ArrangementFxClock(playing, anchorDsp,
                startTick, Math.Max(1e-9, secondsPerTick));
        }

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
        // Fixed memory: no dynamic allocations in OnAudioFilterRead.
        // 524288 stereo frames accommodate a full 4/4 bar at 60 BPM / 96 kHz.
        // Two buffers total ~8 MiB; no resizing on the audio thread.
        private const int PerformanceHistoryFrames = 524288;
        private readonly float[] performanceHistory = new float[PerformanceHistoryFrames * 2];
        private readonly float[] performanceLoop = new float[PerformanceHistoryFrames * 2];
        private int performanceHistoryWrite, performanceHistoryCount;
        private int performanceLoopFrames, performanceLoopPosition;
        private bool performanceRepeatActive;
        private bool performanceRepeatHeld;
        // Keep a long-slice request pending until a complete bar has been recorded.
        private bool performanceRepeatPending;
        private int seenPerformanceSourceRevision;
        private int seenArrangementClockStartRevision;
        private int performancePreviousRate = -1;
        private float performanceGatePhase, performanceGateEnvelope = 1f;
        private bool performanceGateHeld, performanceGatePending, performanceGateActive;
        private int performanceGateRequestedRate = -1, performanceGateAppliedRate = 2;
        private float performanceRepeatWet;
        private readonly float[] performanceCrushHold = new float[2];
        private int performanceCrushCountdown;
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
            PerformanceDrive = song != null ? Mathf.Clamp01(song.performanceDrive) : 0f;
            PerformanceGate = song != null ? Mathf.Clamp01(song.performanceGate) : 0f;
            PerformanceCrush = song != null ? Mathf.Clamp01(song.performanceCrush) : 0f;
            PerformanceRepeat = song != null ? Mathf.Clamp01(song.performanceRepeat) : 0f;
            PerformanceGateRate = song != null ? Mathf.Clamp(song.performanceGateRate, 0, 5) : 2;
            PerformanceRepeatRate = song != null ? Mathf.Clamp(song.performanceRepeatRate, 0, 5) : 2;
            PerformanceBpm = song != null ? Mathf.Clamp(song.bpm, 1f, 400f) : 120f;
            PerformanceQuarterNotesPerBar = song != null
                ? Mathf.Clamp(song.numerator * 4f / Mathf.Max(1, song.denominator), 1f, 16f)
                : 4f;
            if (!ReferenceEquals(performanceSourceSong, song))
            {
                performanceSourceSong = song;
                System.Threading.Interlocked.Increment(ref performanceSourceRevision);
            }
        }

        private void OnEnable()
        {
            sampleRate = Math.Max(8000, AudioSettings.outputSampleRate);
            compressorEnvelope = 0f;
            limiterGain = 1f;
            ResetFxSmoothing();
            ResetPerformanceFx();
        }

        private void ResetPerformanceFx()
        {
            performanceHistoryWrite = 0;
            performanceHistoryCount = 0;
            performanceLoopFrames = performanceLoopPosition = 0;
            performanceRepeatActive = false;
            performanceRepeatHeld = false;
            performanceRepeatPending = false;
            performancePreviousRate = -1;
            performanceGatePhase = 0f;
            performanceGateEnvelope = 1f;
            performanceGateHeld = performanceGatePending = performanceGateActive = false;
            performanceGateRequestedRate = -1;
            performanceGateAppliedRate = 2;
            performanceRepeatWet = 0f;
            performanceCrushCountdown = 0;
            performanceCrushHold[0] = performanceCrushHold[1] = 0f;
            // The circular buffer uses its valid-frame count; no need to clear it.
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
            ResetPerformanceFx();
        }

        // Copy the latest recorded slice (before the stutter stage) into a
        // frozen loop.  All copies occur on the audio thread; buffer is preallocated.
        private void FreezePerformanceRepeat(int frames)
        {
            performanceLoopFrames = Math.Min(Math.Max(1, frames),
                Math.Min(performanceHistoryCount, PerformanceHistoryFrames));
            if (performanceLoopFrames < 1) { performanceRepeatActive = false; return; }
            int start = performanceHistoryWrite - performanceLoopFrames;
            if (start < 0) start += PerformanceHistoryFrames;
            // Bulk copies avoid per-frame modulo and reduce callback spikes.
            int first = Math.Min(performanceLoopFrames, PerformanceHistoryFrames - start);
            Array.Copy(performanceHistory, start * 2, performanceLoop, 0, first * 2);
            if (first < performanceLoopFrames)
                Array.Copy(performanceHistory, 0, performanceLoop, first * 2,
                    (performanceLoopFrames - first) * 2);
            performanceLoopPosition = 0;
            performanceRepeatActive = true;
        }

        // ARRANGEMENT performance FX are intentionally on the final master, not
        // on individual one-shot AudioSources. This makes repeat/gate act on
        // coherent full beats and keeps native Helm timing completely unchanged.
        private void ProcessPerformanceFx(float[] data, int channels, int frames, int rate,
            double callbackDsp, ArrangementFxClock clock)
        {
            if (channels != 1 && channels != 2) return;
            bool enabled = !StemExport && !BusFxBypassed;
            float drive = enabled ? Math.Max(0f, Math.Min(1f, PerformanceDrive)) : 0f;
            float gate = enabled ? Math.Max(0f, Math.Min(1f, PerformanceGate)) : 0f;
            float crush = enabled ? Math.Max(0f, Math.Min(1f, PerformanceCrush)) : 0f;
            float repeat = enabled ? Math.Max(0f, Math.Min(1f, PerformanceRepeat)) : 0f;
            // Prefer the DSP transport tempo even during a live BPM change,
            // so the frozen audio length and musical grid never disagree.
            float bpm = clock != null && clock.playing
                ? (float)Math.Max(1.0, Math.Min(400.0,
                    60.0 / (clock.secondsPerTick * SongData.TicksPerQuarter)))
                : Math.Max(1f, Math.Min(400f, PerformanceBpm));
            int gateRate = Math.Max(0, Math.Min(5, PerformanceGateRate));
            int repeatRate = Math.Max(0, Math.Min(5, PerformanceRepeatRate));
            float quarterNotesPerBar = Math.Max(1f, Math.Min(16f, PerformanceQuarterNotesPerBar));
            double ticksPerBar = SongData.TicksPerQuarter * (double)quarterNotesPerBar;
            // The last two codes are half/full bars, not half/whole notes.
            // Codes 0..3 remain precisely backward-compatible with saved songs.
            double gateTicks = gateRate == 4 ? ticksPerBar * .5
                : gateRate == 5 ? ticksPerBar
                : SongData.TicksPerQuarter / (double)(1 << gateRate);
            double repeatTicks = repeatRate == 4 ? ticksPerBar * .5
                : repeatRate == 5 ? ticksPerBar
                : SongData.TicksPerQuarter / (double)(1 << repeatRate);
            double gateIncrement = (bpm / 60.0) * SongData.TicksPerQuarter /
                (rate * gateTicks);
            // AudioSettings.dspTime is evaluated ONCE on Unity's audio thread.
            // Subsequent frames are advanced by exact sample increments.
            bool clocked = clock != null && clock.playing &&
                callbackDsp + (double)frames / rate >= clock.anchorDsp;
            double tickAtBuffer = clocked
                ? clock.startTick + (callbackDsp - clock.anchorDsp) / clock.secondsPerTick : 0.0;
            double ticksPerSample = clocked ? 1.0 / (rate * clock.secondsPerTick) : 0.0;
            // The next boundary for a live REPEAT request is always musical:
            // 1/2 and 1/1 hit their true half/full bar downbeats; shorter
            // divisions line up with their respective step grid.
            float gateSlew = 1f - (float)Math.Exp(-1.0 / (rate * .003));
            float repeatSlew = 1f - (float)Math.Exp(-1.0 / (rate * .006));
            int crusherHoldFrames = 1 + (int)(crush * 24f);
            int quantBits = 16 - (int)(crush * 11f);
            float quantLevels = (float)(1 << (quantBits - 1));
            bool wantsGate = gate > .001f;
            if (!wantsGate)
            {
                performanceGatePending = false;
                performanceGateActive = false;
            }
            else if (!performanceGateHeld || performanceGateRequestedRate != gateRate)
                performanceGatePending = true;
            performanceGateHeld = wantsGate;
            performanceGateRequestedRate = gateRate;

            bool wantsRepeat = repeat > .001f;
            if (!wantsRepeat) performanceRepeatPending = false;
            else if (!performanceRepeatHeld || performancePreviousRate != repeatRate)
                performanceRepeatPending = true;
            performanceRepeatHeld = wantsRepeat;
            performancePreviousRate = repeatRate;
            int sliceFrames = Math.Max(32, Math.Min(PerformanceHistoryFrames,
                (int)Math.Round(rate * repeatTicks * 60.0 / bpm /
                    SongData.TicksPerQuarter)));
            // If there is no active arrangement (e.g. previewing a sound),
            // retain the original free-running effect behavior.
            double appliedGateTicks = performanceGateAppliedRate == 4 ? ticksPerBar * .5
                : performanceGateAppliedRate == 5 ? ticksPerBar
                : SongData.TicksPerQuarter / (double)(1 << performanceGateAppliedRate);

            for (int frame = 0; frame < frames; ++frame)
            {
                int offset = frame * channels;
                // Explicitly store two channels even in mono (avoids allocating
                // separate rings). Capture after drive/gate/crush, before stutter.
                float l = data[offset], r = channels == 2 ? data[offset + 1] : l;
                if (drive > .0001f)
                {
                    float gain = 1f + drive * 7f;
                    l = (l * gain / (1f + Math.Abs(l * gain))) * (1f + drive * .15f);
                    r = (r * gain / (1f + Math.Abs(r * gain))) * (1f + drive * .15f);
                }
                // 60% duty cycle produces a pronounced trance-gate rhythm;
                // crossfades smooth the edges without changing event scheduling.
                double audioDsp = callbackDsp + (double)frame / rate;
                bool inArrangement = clocked && audioDsp >= clock.anchorDsp;
                double absoluteTick = tickAtBuffer + frame * ticksPerSample;
                // When disabled there is no expensive per-sample grid math.
                // The 3ms release ramp still resolves naturally after bypass.
                if (wantsGate || performanceGateEnvelope < .9999f)
                {
                    // Rate changes and activation wait until the next half-bar.
                    // The old rate stays in force while a change is pending.
                    if (performanceGatePending)
                    {
                        double halfBarTicks = ticksPerBar * .5;
                        bool halfBarBoundary = (clock == null || !clock.playing) ||
                            (inArrangement &&
                            Math.Floor((absoluteTick + 1e-7) / halfBarTicks) >
                            Math.Floor((absoluteTick - ticksPerSample + 1e-7) / halfBarTicks));
                        if (halfBarBoundary)
                        {
                            performanceGateAppliedRate = gateRate;
                            performanceGateActive = true;
                            performanceGatePending = false;
                            performanceGatePhase = 0f;
                            appliedGateTicks = performanceGateAppliedRate == 4 ? ticksPerBar * .5
                                : performanceGateAppliedRate == 5 ? ticksPerBar
                                : SongData.TicksPerQuarter / (double)(1 << performanceGateAppliedRate);
                        }
                    }
                    double gatePhase;
                    if (inArrangement)
                    {
                        double phase = absoluteTick / appliedGateTicks;
                        gatePhase = phase - Math.Floor(phase);
                    }
                    else
                    {
                        gatePhase = performanceGatePhase;
                        performanceGatePhase += (float)gateIncrement;
                        if (performanceGatePhase >= 1f) performanceGatePhase -= 1f;
                    }
                    float open = gatePhase < .60 ? 1f : 0f;
                    float audibleGate = performanceGateActive ? gate : 0f;
                    float gateTarget = 1f - audibleGate * .98f * (1f - open);
                    performanceGateEnvelope += (gateTarget - performanceGateEnvelope) * gateSlew;
                    l *= performanceGateEnvelope;
                    r *= performanceGateEnvelope;
                }
                else performanceGateEnvelope = 1f;
                if (crush > .0001f)
                {
                    if (performanceCrushCountdown <= 0)
                    {
                        performanceCrushHold[0] = (float)Math.Round(l * quantLevels) / quantLevels;
                        performanceCrushHold[1] = (float)Math.Round(r * quantLevels) / quantLevels;
                        performanceCrushCountdown = crusherHoldFrames;
                    }
                    performanceCrushCountdown--;
                    l += (performanceCrushHold[0] - l) * crush;
                    r += (performanceCrushHold[1] - r) * crush;
                }
                else performanceCrushCountdown = 0;

                // History is always live: a quantized REPEAT freezes the phrase
                // immediately before the chosen step/half-bar/bar boundary.
                // Record up to the exact boundary, then freeze the preceding
                // complete phrase. A half/full-bar repeat therefore never
                // grabs a random partial bar when toggled mid-phrase.
                if (performanceRepeatPending && performanceHistoryCount >= sliceFrames)
                {
                    bool repeatBoundary = clock == null || !clock.playing;
                    if (inArrangement)
                    {
                        double grid = Math.Floor((absoluteTick + 1e-7) / repeatTicks);
                        double prevTick = absoluteTick - ticksPerSample;
                        repeatBoundary = Math.Floor((prevTick + 1e-7) / repeatTicks) < grid;
                    }
                    if (repeatBoundary)
                    {
                        FreezePerformanceRepeat(sliceFrames);
                        performanceRepeatPending = false;
                    }
                }
                int write = performanceHistoryWrite * 2;
                performanceHistory[write] = l;
                performanceHistory[write + 1] = r;
                performanceHistoryWrite++;
                if (performanceHistoryWrite == PerformanceHistoryFrames) performanceHistoryWrite = 0;
                if (performanceHistoryCount < PerformanceHistoryFrames) performanceHistoryCount++;

                float wetTarget = (wantsRepeat && performanceRepeatActive) ? repeat : 0f;
                performanceRepeatWet += (wetTarget - performanceRepeatWet) * repeatSlew;
                if (performanceRepeatActive && performanceLoopFrames > 0)
                {
                    int i = performanceLoopPosition * 2;
                    float wet = performanceRepeatWet;
                    l += (performanceLoop[i] - l) * wet;
                    r += (performanceLoop[i + 1] - r) * wet;
                    if (++performanceLoopPosition >= performanceLoopFrames)
                        performanceLoopPosition = 0;
                }
                // Continue the previous loop briefly while the wet signal fades,
                // preventing an abrupt click when dragging the knob to OFF.
                if (!wantsRepeat && performanceRepeatWet < .0005f)
                {
                    performanceRepeatWet = 0f;
                    performanceRepeatActive = false;
                }
                data[offset] = l;
                if (channels == 2) data[offset + 1] = r;
            }
        }

        private void OnAudioFilterRead(float[] samples, int channels)
        {
            if (samples == null || channels < 1) return;
            int frames = samples.Length / channels;
            int revision = performanceSourceRevision;
            int startRevision = arrangementFxClockStartRevision;
            if (revision != seenPerformanceSourceRevision ||
                startRevision != seenArrangementClockStartRevision)
            {
                ResetPerformanceFx();
                seenPerformanceSourceRevision = revision;
                seenArrangementClockStartRevision = startRevision;
            }
            bool fx = !StemExport && !BusFxBypassed;
            float glue = fx ? Math.Max(0f, Math.Min(1f, GlueAmount)) : 0f;
            float drive = fx ? Math.Max(0f, Math.Min(1f, DriveAmount)) : 0f;
            float width = fx ? 1f + Math.Max(-1f, Math.Min(1f, WidthOffset)) * .30f : 1f;
            float trim = fx ? (float)Math.Pow(10.0, Math.Max(-12f, Math.Min(3f, OutputTrimDb)) / 20.0) : 1f;
            int rate = sampleRate;
            // Unity exposes dspTime on its audio thread; take a single timestamp
            // for this buffer so the whole gate/stutter shares the transport.
            double bufferDsp = AudioSettings.dspTime;
            ArrangementFxClock fxClock = arrangementFxClock;
            ProcessPerformanceFx(samples, channels, frames, rate, bufferDsp, fxClock);
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
