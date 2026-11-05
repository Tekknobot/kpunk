using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZPunk.DAW
{
    /// <summary>
    /// Standalone, monophonic 303-inspired instrument. No AudioHelm dependency.
    /// MIDI commands are queued on the main thread with absolute DSP times and
    /// rendered on the audio thread. No Unity objects are accessed per sample.
    /// This is a musical approximation, not circuit/component-level emulation.
    /// </summary>
    public sealed class ZPunkAcidVoice : MonoBehaviour
    {
        private struct Command
        {
            public double time;
            public int pitch, velocity, flags;
            public bool on;
            public int generation;
        }
        private readonly object queueLock = new object();
        private readonly List<Command> queued = new List<Command>(256);
        private readonly List<Command> audioQueue = new List<Command>(256);
        private int generation;
        private int audioGeneration;
        private float gain = 1f;
        private AcidState parameters = new AcidState();
        private double phase, frequency, targetFrequency, phaseStep, portamentoLeft;
        private double envelope, filterEnvelope;
        private double lp1, lp2, lp3, lp4;
        // New factory patches opt into a more musical 303-style contour. Kept
        // separate from the legacy topology to protect older saved projects.
        private double accentSweep, accentPulse, vcaAttackPulse;
        private double smoothed303Cutoff, smoothed303Resonance;
        private double distortionToneLow;
        private double distortionMetalLow, distortionDiodeDc;
        // Persist audio-thread DSP envelopes between audio buffers.
        private double pedalPreLow, pedalPostLow, compressorDetector;
        private double pedalDc, tapeHighPassL, tapeHighPassR;
        private double delayToneLeft, delayToneRight;
        private double tapePhase;
        private double smoothedOutputLevel = 1.0; // 6ms de-zippered user LEVEL
        private double smoothedPatchOutputTrim = 1.0; // avoid preset-switch clicks
        private bool gate, activeAccent;
        private int currentPitch = -1;
        private float lastPeak;
        private int sampleRate = 44100;
        private AudioSource source;
        // Bounded, per-voice buffers for the independent ACID effects rack.
        // Allocated once on the MAIN thread; no DSP callback allocations.
        private double[] chorusLine, delayLeft, delayRight;
        private double[] roomLeftA, roomLeftB, roomRightA, roomRightB;
        private int chorusWrite, delayWrite, roomWriteLA, roomWriteLB, roomWriteRA, roomWriteRB;
        private double chorusPhase;
        private volatile float tempoBpm = 120f;

        public float Peak => lastPeak;

        public void Initialize()
        {
            if (source != null) return;
            sampleRate = Math.Max(8000, AudioSettings.outputSampleRate);
            chorusLine = new double[Math.Max(4096, sampleRate / 12)];
            delayLeft = new double[sampleRate * 3];
            delayRight = new double[sampleRate * 3];
            roomLeftA = new double[Math.Max(1, sampleRate * 37 / 1000)];
            roomLeftB = new double[Math.Max(1, sampleRate * 43 / 1000)];
            roomRightA = new double[Math.Max(1, sampleRate * 41 / 1000)];
            roomRightB = new double[Math.Max(1, sampleRate * 47 / 1000)];
            source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = true;
            source.spatialBlend = 0f;
            // The silent clip is only a DSP clock. The callback writes all audio.
            source.clip = AudioClip.Create("ACID DSP clock", 1024, 2, sampleRate, false);
            source.volume = 1f;
            source.Play();
        }

        public void Apply(AcidState state, float trackGain, float tempo)
        {
            if (state == null) state = new AcidState();
            // Publish a new immutable snapshot; never change the audio thread's
            // parameter object in place during an interactive drag.
            parameters = new AcidState {
                square = state.square, tune = state.tune, cutoff = state.cutoff,
                resonance = state.resonance, envMod = state.envMod, decay = state.decay,
                accent = state.accent, drive = state.drive, glide = state.glide,
                outputLevelSet = state.outputLevelSet, outputLevel = state.outputLevel,
                outputLevelUserAdjusted = state.outputLevelUserAdjusted,
                patchOutputTrimSet = state.patchOutputTrimSet,
                patchOutputTrim = state.patchOutputTrim,
                refined303Engine = state.refined303Engine,
                fxDistortion = state.fxDistortion,
                fxDistControlsInitialized = state.fxDistControlsInitialized,
                fxDistTone = state.fxDistTone, fxDistMix = state.fxDistMix,
                fxDistBite = state.fxDistBite, fxDistType = state.fxDistType,
                fx303DriveModel = state.fx303DriveModel,
                fxChorus = state.fxChorus,
                fxDelay = state.fxDelay, fxFeedback = state.fxFeedback,
                fxReverb = state.fxReverb, fxRoom = state.fxRoom,
                fxDelayRate = state.fxDelayRate,
                fxDelayMode = state.fxDelayMode, fxDelayTone = state.fxDelayTone,
                fx303DelayModel = state.fx303DelayModel
            };
            gain = Mathf.Clamp(trackGain, 0f, 2f);
            SetTempo(tempo);
        }

        public void SetTempo(float tempo)
        {
            tempoBpm = Mathf.Clamp(tempo, 1f, 400f);
        }

        // Fractional read for smoothly modulated chorus delay taps.
        private static double ReadLine(double[] line, int write, double back)
        {
            double pos = write - back;
            if (pos < 0) pos += line.Length;
            int index = (int)pos;
            int next = index + 1;
            if (next == line.Length) next = 0;
            double fraction = pos - index;
            return line[index] * (1.0 - fraction) + line[next] * fraction;
        }

        public void Schedule(double when, bool noteOn, int pitch, int velocity, int flags)
        {
            var command = new Command { time = when, on = noteOn,
                pitch = pitch, velocity = velocity, flags = flags, generation = generation };
            lock (queueLock)
            {
                // Plan events are normally ordered. Insert sorted to cope with
                // live edits / a newly prefetched loop without shifting timestamps.
                int at = queued.Count;
                while (at > 0 && queued[at - 1].time > when) at--;
                queued.Insert(at, command);
            }
        }

        public void AllNotesOff()
        {
            lock (queueLock) { generation++; queued.Clear(); }
        }

        private static double PolyBlep(double t, double dt)
        {
            if (t < dt) { t /= dt; return t + t - t * t - 1.0; }
            if (t > 1.0 - dt) { t = (t - 1.0) / dt; return t * t + t + t + 1.0; }
            return 0.0;
        }
        private static double Clip(double x) => x / (1.0 + Math.Abs(x));
        private static double Norm(float n) => Math.Max(0.0, Math.Min(1.0, n));
        private static double Hz(int pitch, float tune) => 440.0 * Math.Pow(2.0,
            (pitch - 69.0 + (Norm(tune) - .5) * 24.0) / 12.0);

        private void ApplyCommand(Command c, AcidState p)
        {
            if (!c.on)
            {
                if (currentPitch == c.pitch) gate = false;
                return;
            }
            bool sliding = (c.flags & 2) != 0 && currentPitch >= 0 && envelope > .01;
            double next = Hz(c.pitch, p.tune);
            currentPitch = c.pitch;
            targetFrequency = next;
            if (sliding)
            {
                portamentoLeft = .025 + Norm(p.glide) * .225;
            }
            else
            {
                frequency = next;
                portamentoLeft = 0;
                envelope = 1.0;
                filterEnvelope = 1.0;
            }
            activeAccent = (c.flags & 1) != 0;
            if (activeAccent) filterEnvelope = 1.0;
            if (p.refined303Engine)
            {
                // A slide preserves the running envelopes; an accent adds a
                // brief VCA attack and pumps the filter's slower accent circuit.
                if (!sliding) vcaAttackPulse = 1.0;
                if (activeAccent)
                {
                    accentSweep = Math.Min(1.5, accentSweep +
                        (.42 + Norm(p.accent) * .62));
                    accentPulse = 1.0;
                }
            }
            gate = true;
            // Accented velocities are more pronounced, without making unaccented
            // notes inaudible. The filter/volume changes are the main accent effect.
            envelope *= .75 + .25 * Math.Max(0.0, Math.Min(1.0, c.velocity / 127.0));
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (data == null || channels <= 0) return;
            var p = parameters;
            float g = gain;
            int localGeneration = generation;
            if (localGeneration != audioGeneration)
            {
                audioGeneration = localGeneration;
                audioQueue.Clear();
                gate = false; envelope = filterEnvelope = 0;
                currentPitch = -1;
                lp1 = lp2 = lp3 = lp4 = 0;
                accentSweep = accentPulse = vcaAttackPulse = 0;
                smoothed303Cutoff = smoothed303Resonance = 0;
                distortionToneLow = distortionMetalLow = distortionDiodeDc = 0;
                pedalPreLow = pedalPostLow = compressorDetector = pedalDc = 0;
                tapeHighPassL = tapeHighPassR = tapePhase = 0;
                delayToneLeft = delayToneRight = 0;
            }
            lock (queueLock)
            {
                if (queued.Count > 0) { audioQueue.AddRange(queued); queued.Clear(); }
            }
            double now = AudioSettings.dspTime;
            double delta = 1.0 / Math.Max(8000, sampleRate);
            double decay = .09 + Norm(p.decay) * 1.5;
            double decayFactor = Math.Exp(-delta / decay);
            double releaseFactor = Math.Exp(-delta / .032);
            double drive = 1.0 + Norm(p.drive) * 11.0;
            double cutoffBase = 65.0 * Math.Pow(72.0, Norm(p.cutoff));
            double res = Norm(p.resonance) * 3.55;
            double envDepth = Norm(p.envMod) * 4.5;
            double accentStrength = Norm(p.accent);
            double outputLevel = p.outputLevelSet ? Norm(p.outputLevel) : 1.0;
            double patchOutputTrim = p.patchOutputTrimSet ? Norm(p.patchOutputTrim) : 1.0;
            bool refined303 = p.refined303Engine;
            double filterSlew = 1.0 - Math.Exp(-delta / .0035);
            double resonanceSlew = 1.0 - Math.Exp(-delta / .008);
            double accentSweepFall = Math.Exp(-delta / .160);
            double accentPulseFall = Math.Exp(-delta / .045);
            double vcaTransientFall = Math.Exp(-delta / .060);
            double outputSlew = 1.0 - Math.Exp(-delta / .006);
            // All new rack amounts default to zero in existing song JSON:
            // with every FX at zero, original DSP output is bit-identical.
            double fxDistortion = Norm(p.fxDistortion);
            double distortionGain = 1.0 + fxDistortion * 10.0;
            double distortionNormal = fxDistortion > .0001 ? 1.0 / Math.Tanh(distortionGain) : 1.0;
            bool advancedDistortion = p.fxDistControlsInitialized;
            double distortionTone = advancedDistortion ? Norm(p.fxDistTone) : .5;
            double distortionMix = advancedDistortion ? Norm(p.fxDistMix) : fxDistortion * .85;
            double distortionBite = advancedDistortion ? Norm(p.fxDistBite) : 0.0;
            // IMPORTANT: type 0 uses the unchanged earlier DSP branch, including
            // existing extended TONE/MIX/BITE settings saved by previous builds.
            int distortionType = advancedDistortion ? Math.Max(0, Math.Min(8, p.fxDistType)) : 0;
            bool use303Pedal = p.fx303DriveModel && distortionType != 0;
            double tonePole = 1.0 - Math.Exp(-2.0 * Math.PI * 2000.0 * delta);
            double metalPole = 1.0 - Math.Exp(-2.0 * Math.PI * 650.0 * delta);
            double dcPole = 1.0 - Math.Exp(-2.0 * Math.PI * 18.0 * delta);
            // Analogue-pedal-inspired voicing, not a proprietary Roland algorithm.
            // Separate pre-emphasis and final low pass avoid Metal collapsing into
            // simple full-band clipping; varying TONE shapes harmonics substantially.
            double pedalPrePole = 1.0 - Math.Exp(-2.0 * Math.PI * 440.0 * delta);
            double pedalToneHz = 1250.0 + Math.Pow(distortionTone, 1.35) * 9600.0;
            double pedalPostPole = 1.0 - Math.Exp(-2.0 * Math.PI * pedalToneHz * delta);
            double compressorAttack = 1.0 - Math.Exp(-delta / .004);
            double compressorRelease = 1.0 - Math.Exp(-delta / .115);
            double fxChorus = Norm(p.fxChorus);
            double fxDelay = Norm(p.fxDelay);
            int delayMode = Math.Max(0, Math.Min(6, p.fxDelayMode));
            bool use303Delay = p.fx303DelayModel && delayMode != 0;
            // Preserve exact gain for old sessions. Higher mix/feedback and
            // stronger stereo repeats are opt-in with the new modes.
            double fxFeedback = .14 + Norm(p.fxFeedback) * .73;
            double enhancedFeedback = .18 + Norm(p.fxFeedback) * .72;
            double delayTone = Norm(p.fxDelayTone);
            double delayToneHz = 300.0 + Math.Pow(delayTone, 1.6) * 11500.0;
            if (delayMode == 2 || delayMode == 4) delayToneHz *= .52; // warm dub / tape
            double delayTonePole = 1.0 - Math.Exp(-2.0 * Math.PI * delayToneHz * delta);
            double fxReverb = Norm(p.fxReverb);
            double reverbFeedback = .52 + Norm(p.fxRoom) * .36;
            double beatSeconds = 60.0 / Math.Max(1.0, tempoBpm);
            double delayBeats = p.fxDelayRate == 1 ? .25 : p.fxDelayRate == 2 ? 1.0 :
                                p.fxDelayRate == 3 ? .75 : .5;
            int delayFrames = Math.Max(1, Math.Min(delayLeft.Length - 2,
                (int)(beatSeconds * delayBeats * sampleRate)));
            float peak = 0f;
            int offset = 0;
            for (int frame = 0; frame < data.Length / channels; frame++)
            {
                double t = now + frame * delta;
                while (offset < audioQueue.Count && audioQueue[offset].time <= t)
                {
                    if (audioQueue[offset].generation == localGeneration)
                        ApplyCommand(audioQueue[offset], p);
                    offset++;
                }
                if (portamentoLeft > 0)
                {
                    double fraction = Math.Min(1.0, delta / portamentoLeft);
                    // Interpolate in musical pitch, not raw linear Hz.
                    frequency *= Math.Pow(targetFrequency / Math.Max(1.0, frequency), fraction);
                    portamentoLeft -= delta;
                }
                else frequency = targetFrequency;
                double dt = Math.Min(.45, frequency * delta);
                phase += dt;
                if (phase >= 1.0) phase -= 1.0;
                double osc = p.square
                    ? ((phase < .5 ? 1.0 : -1.0) + PolyBlep(phase, dt) - PolyBlep((phase + .5) % 1.0, dt))
                    : (2.0 * phase - 1.0 - PolyBlep(phase, dt));
                if (!gate) envelope *= releaseFactor;
                filterEnvelope *= decayFactor;
                double accent = activeAccent ? accentStrength : 0.0;
                double sample;
                if (refined303)
                {
                    // Original TB-303 characteristics: a predominantly 18 dB
                    // lowpass response, short accent pluck, slower accent sweep,
                    // and legato slides that carry the filter envelope forward.
                    // This is an original perceptual approximation, not ACB DSP.
                    accentSweep *= accentSweepFall;
                    accentPulse *= accentPulseFall;
                    vcaAttackPulse *= vcaTransientFall;
                    double sweepDecay = activeAccent ? .73 : 1.0;
                    // Accents briefly shorten the filter decay as they brighten
                    // the sound, giving a sharper 303-style bite.
                    if (activeAccent) filterEnvelope *=
                        Math.Exp(-delta * (1.0 - sweepDecay) / Math.Max(.08, decay));
                    double baseFc = 62.0 * Math.Pow(96.0, Norm(p.cutoff));
                    double envCurve = Math.Pow(Math.Max(0.0, filterEnvelope), .78);
                    double targetFc = baseFc * Math.Exp(
                        envCurve * (Norm(p.envMod) * 3.95) +
                        accentSweep * accentStrength * .84);
                    targetFc = Math.Max(35.0, Math.Min(sampleRate * .30, targetFc));
                    if (smoothed303Cutoff <= 0) smoothed303Cutoff = targetFc;
                    smoothed303Cutoff += (targetFc - smoothed303Cutoff) * filterSlew;
                    double wantedRes = .22 + Norm(p.resonance) * 2.80;
                    smoothed303Resonance +=
                        (wantedRes - smoothed303Resonance) * resonanceSlew;
                    // Three soft-saturating poles with resonant feedback. This
                    // yields a sharper 303-like nasal response than the legacy
                    // four-stage ladder, without raising CPU cost or adding
                    // a fourth oscillator/voice. Limit a for stable high Fc.
                    double a303 = Math.Min(.81, 1.0 - Math.Exp(
                        -2.0 * Math.PI * smoothed303Cutoff * delta));
                    double input303 = Math.Tanh((osc *
                        (1.0 + .16 * accentPulse * accentStrength) -
                        lp3 * smoothed303Resonance) * (1.1 + Norm(p.drive) * 3.9));
                    lp1 += a303 * (input303 - lp1);
                    lp2 += a303 * (Math.Tanh(lp1 * 1.35) - lp2);
                    lp3 += a303 * (Math.Tanh(lp2 * 1.35) - lp3);
                    double transientAmp = .83 + .17 * vcaAttackPulse;
                    double accentAmp = 1.0 + .38 * accentPulse * accentStrength;
                    sample = Math.Tanh(lp3 * 2.05) * transientAmp * accentAmp *
                        envelope * g * .61;
                }
                else
                {
                    // Original ZPUNK model, untouched for older song files.
                    double fc = cutoffBase * Math.Exp((filterEnvelope *
                        (envDepth + accent * 1.2)));
                    fc = Math.Max(35.0, Math.Min(sampleRate * .34, fc));
                    double a = 1.0 - Math.Exp(-2.0 * Math.PI * fc * delta);
                    double input = Clip((osc * (1.0 + accent * .35) - lp4 * res) * drive);
                    lp1 += a * (input - lp1);
                    lp2 += a * (Clip(lp1 * 1.4) - lp2);
                    lp3 += a * (Clip(lp2 * 1.4) - lp3);
                    lp4 += a * (Clip(lp3 * 1.4) - lp4);
                    sample = Clip(lp4 * 2.0) * envelope * g * .62;
                }
                double left = sample, right = sample;
                // Post-filter distortion. With an old save/patch, this is
                // numerically the same soft drive/mix path as the previous build.
                // Extended controls provide genuine tone, wet/dry and bite.
                if (fxDistortion > .0001)
                {
                    double saturated;
                    double inputDrive = sample * distortionGain;
                    if (distortionType == 0)
                    {
                        // Original/legacy distortion: keep the old branch exactly.
                        saturated = Math.Tanh(inputDrive) * distortionNormal;
                        if (advancedDistortion)
                        {
                            double hard = Math.Max(-1.0, Math.Min(1.0, inputDrive));
                            saturated = saturated * (1.0 - distortionBite) + hard * distortionBite;
                            distortionToneLow += tonePole * (saturated - distortionToneLow);
                            saturated += (saturated - distortionToneLow) * (distortionTone - .5) * 1.5;
                        }
                    }
                    else if (use303Pedal)
                    {
                        // New TB-303-software-inspired drive families. The original
                        // TB-303 had no distortion: these are our own algorithms,
                        // not Roland's protected circuitry/plug-in DSP.
                        pedalPreLow += pedalPrePole * (sample - pedalPreLow);
                        double pedalHigh = sample - pedalPreLow;
                        double voiced = (sample * .85 + pedalHigh *
                            (distortionType == 4 ? 1.70 : .66)) * distortionGain;
                        switch (distortionType)
                        {
                            case 1: // OVERDRIVE: symmetrical soft knee, warm mids
                                saturated = Math.Tanh(voiced * 1.35 + .06) - Math.Tanh(.06);
                                break;
                            case 2: // DISTORTION: firmer clipping and distinct note edge
                                saturated = .73 * Math.Tanh(voiced * 2.65) +
                                    .27 * Math.Max(-1.0, Math.Min(1.0, voiced * 1.8));
                                break;
                            case 3: // FUZZ: double stage, strong compressed harmonics
                                saturated = Math.Tanh(Math.Tanh(voiced * 4.1) * 3.0);
                                break;
                            case 4: // METAL: treble pre-emphasis, two clipping stages,
                                    // post-filtered fizz, and a little dry low-end
                                double stage1 = Math.Tanh(voiced * 1.35);
                                double stage2 = Math.Tanh(stage1 * 3.55 + .035) - Math.Tanh(.035);
                                saturated = stage2 * .88 + pedalPreLow * 1.4;
                                break;
                            case 5: // DIODE: unequal + and - knees
                                saturated = voiced >= 0 ? Math.Tanh(voiced * 1.95) :
                                    Math.Tanh(voiced * 1.18) * .79;
                                break;
                            case 6: // HARD CLIP: intentionally sharp digital edge
                                saturated = Math.Max(-1.0, Math.Min(1.0, voiced * 1.85));
                                break;
                            case 7: // COMPRESSOR: feed-forward gain control before
                                    // mild saturation, useful for even 303 basslines
                                double detected = Math.Abs(voiced);
                                compressorDetector += (detected > compressorDetector ?
                                    compressorAttack : compressorRelease) *
                                    (detected - compressorDetector);
                                double threshold = .27 - fxDistortion * .16;
                                double ratio = 3.0 + fxDistortion * 7.0;
                                double reduction = compressorDetector > threshold ?
                                    Math.Pow(compressorDetector / threshold, 1.0 / ratio - 1.0) : 1.0;
                                saturated = Math.Tanh(voiced * reduction * 1.55) *
                                    (1.20 + fxDistortion * .24);
                                break;
                            default: // DRIVE: more rounded, low-mid-biased saturation
                                saturated = .65 * Math.Tanh(voiced * 1.9) +
                                    .35 * Math.Tanh(sample * distortionGain * 2.0);
                                break;
                        }
                        // BITE changes edge without overwhelming the iconic filter.
                        double pedalHard = Math.Max(-1.0, Math.Min(1.0, voiced * 1.4));
                        saturated = saturated * (1.0 - distortionBite * .30) +
                            pedalHard * distortionBite * .30;
                        pedalPostLow += pedalPostPole * (saturated - pedalPostLow);
                        saturated = (pedalPostLow * .92 + saturated * .08) * .82;
                        // Eliminate steady-state DC from asymmetric clipping.
                        pedalDc += dcPole * (saturated - pedalDc);
                        saturated = Math.Max(-1.0, Math.Min(1.0, saturated - pedalDc));
                    }
                    else
                    {
                        // LEGACY pedal styles. Retained verbatim for old saves.
                        switch (distortionType)
                        {
                            case 1: // OVERDRIVE: rounded, mildly asymmetric clipping
                                saturated = Math.Tanh(inputDrive * 1.65 + .08) - Math.Tanh(.08);
                                break;
                            case 2: // DISTORTION: aggressive clipping, focused mids
                                saturated = Math.Tanh(inputDrive * 2.65) * .78 +
                                    Math.Max(-1.0, Math.Min(1.0, inputDrive * 1.5)) * .22;
                                break;
                            case 3: // FUZZ: compressed, square-edged, deliberately ragged
                                saturated = Math.Tanh(inputDrive * 4.7 + .22) - Math.Tanh(.22);
                                break;
                            case 4: // METAL: pre-emphasis into high-gain saturation
                                distortionMetalLow += metalPole * (sample - distortionMetalLow);
                                double metalHigh = sample - distortionMetalLow;
                                saturated = Math.Tanh((sample * .72 + metalHigh * 1.95) *
                                    distortionGain * 3.2);
                                break;
                            case 5: // DIODE: unequal positive / negative knee
                                double diode = inputDrive >= 0 ? Math.Tanh(inputDrive * 2.2) :
                                    Math.Tanh(inputDrive * 1.15) * .78;
                                distortionDiodeDc += dcPole * (diode - distortionDiodeDc);
                                saturated = diode - distortionDiodeDc;
                                break;
                            default: // HARD CLIP: clipped edges without extra filtering
                                saturated = Math.Max(-1.0, Math.Min(1.0, inputDrive * 2.0));
                                break;
                        }
                        // Existing BITE knob remains effective in all new types.
                        double hard = Math.Max(-1.0, Math.Min(1.0, inputDrive * 2.2));
                        saturated = saturated * (1.0 - distortionBite * .5) +
                            hard * (distortionBite * .5);
                        saturated *= .84; // output match across pedal types
                        distortionToneLow += tonePole * (saturated - distortionToneLow);
                        saturated += (saturated - distortionToneLow) * (distortionTone - .5) * 1.5;
                        saturated = Math.Max(-1.0, Math.Min(1.0, saturated));
                    }
                    left = right = sample * (1.0 - distortionMix) + saturated * distortionMix;
                }
                // Stereo chorus: two fractional modulated taps of the MONO input,
                // opposed in phase to widen the ACID track without extra voices.
                if (fxChorus > .0001 && chorusLine != null)
                {
                    chorusLine[chorusWrite] = left;
                    double modulation = Math.Sin(chorusPhase) * (sampleRate * .0035);
                    double baseDelay = sampleRate * .019;
                    double wetLeft = ReadLine(chorusLine, chorusWrite, baseDelay + modulation);
                    double wetRight = ReadLine(chorusLine, chorusWrite, baseDelay - modulation);
                    double mix = fxChorus * .52;
                    left = left * (1.0 - mix) + wetLeft * mix;
                    right = right * (1.0 - mix) + wetRight * mix;
                    chorusWrite++;
                    if (chorusWrite >= chorusLine.Length) chorusWrite = 0;
                    chorusPhase += 2.0 * Math.PI * .67 * delta;
                    if (chorusPhase >= 2.0 * Math.PI) chorusPhase -= 2.0 * Math.PI;
                }
                // Beat-synced stereo ping-pong delay; no feedback when mix is OFF.
                if (fxDelay > .0001 && delayLeft != null)
                {
                    int read = delayWrite - delayFrames;
                    if (read < 0) read += delayLeft.Length;
                    double tapL = delayLeft[read], tapR = delayRight[read];
                    // A subtle wow/flutter only on the opt-in TAPE ECHO type.
                    // Fractional taps avoid jumping between integer samples.
                    if (use303Delay && delayMode == 4)
                    {
                        double flutter = (Math.Sin(tapePhase) * .0007 +
                            Math.Sin(tapePhase * .23) * .00035) * sampleRate;
                        double readBack = Math.Max(1.0, Math.Min(delayLeft.Length - 2.0,
                            delayFrames + flutter));
                        tapL = ReadLine(delayLeft, delayWrite, readBack);
                        tapR = ReadLine(delayRight, delayWrite, readBack);
                        tapePhase += 2.0 * Math.PI * .83 * delta;
                        if (tapePhase > 2.0 * Math.PI) tapePhase -= 2.0 * Math.PI;
                    }
                    double incomingL = left, incomingR = right;
                    if (delayMode == 0)
                    {
                        // ORIGINAL: exactly the same quieter echo as older saves.
                        delayLeft[delayWrite] = Clip(incomingL * .72 + tapR * fxFeedback);
                        delayRight[delayWrite] = Clip(incomingR * .12 + tapL * fxFeedback);
                        double mix = fxDelay * .59;
                        left = incomingL + tapL * mix;
                        right = incomingR + tapR * mix;
                    }
                    else if (use303Delay)
                    {
                        // Musical echo voicing modelled after the *types* available
                        // in later 303 software: tape coloration, pan and digital.
                        // Feedback is filtered and tanh-limited to prevent runaway.
                        delayToneLeft += delayTonePole * (tapL - delayToneLeft);
                        delayToneRight += delayTonePole * (tapR - delayToneRight);
                        double echoL = delayToneLeft, echoR = delayToneRight;
                        double mono = (incomingL + incomingR) * .5;
                        if (delayMode == 4) // TAPE: darker saturating mono repeats
                        {
                            // Remove low-frequency accumulation from feedback.
                            tapeHighPassL += dcPole * (echoL - tapeHighPassL);
                            double feedback = echoL - tapeHighPassL;
                            double tape = Math.Tanh(mono * .98 + feedback *
                                Math.Min(.87, enhancedFeedback) * 1.10);
                            delayLeft[delayWrite] = tape;
                            delayRight[delayWrite] = tape * .96;
                        }
                        else if (delayMode == 1 || delayMode == 2 || delayMode == 5)
                        {
                            // Pan: dry input goes left, right carries next echo.
                            double warmth = delayMode == 2 ? .86 : 1.0;
                            delayLeft[delayWrite] = Math.Tanh(mono * 1.10 +
                                echoR * enhancedFeedback * warmth);
                            delayRight[delayWrite] = Math.Tanh(echoL * enhancedFeedback * warmth);
                        }
                        else // stereo + digital: clear parallel repeats
                        {
                            delayLeft[delayWrite] = Math.Tanh(incomingL * 1.1 +
                                echoL * enhancedFeedback * .95);
                            delayRight[delayWrite] = Math.Tanh(incomingR * 1.1 +
                                echoR * enhancedFeedback * .95);
                        }
                        // Strong first echo, retaining enough dry for articulation.
                        double wet = fxDelay * (delayMode == 4 ? 1.10 : 1.32);
                        double dry = 1.0 - fxDelay * .24;
                        left = incomingL * dry + echoL * wet;
                        right = incomingR * dry + echoR * wet;
                    }
                    else
                    {
                        // LEGACY stereo echo path retained for previous saves.
                        // A real mono-to-stereo echo, with a clear first repeat,
                        // alternating ping-pong taps and feedback tone control.
                        // Damp feedback, not the main dry signal.
                        delayToneLeft += delayTonePole * (tapL - delayToneLeft);
                        delayToneRight += delayTonePole * (tapR - delayToneRight);
                        double echoL = delayToneLeft, echoR = delayToneRight;
                        double inputMono = (incomingL + incomingR) * .5;
                        if (delayMode == 3) // STEREO: simultaneous L/R taps
                        {
                            delayLeft[delayWrite] = Math.Tanh(incomingL * 1.05 +
                                (echoR * .74 + echoL * .20) * enhancedFeedback);
                            delayRight[delayWrite] = Math.Tanh(incomingR * 1.05 +
                                (echoL * .74 + echoR * .20) * enhancedFeedback);
                        }
                        else // PING-PONG / DUB: first bounce L, then R
                        {
                            double dubGain = delayMode == 2 ? 1.12 : 1.0;
                            delayLeft[delayWrite] = Math.Tanh(inputMono * dubGain +
                                echoR * enhancedFeedback);
                            delayRight[delayWrite] = Math.Tanh(echoL * enhancedFeedback);
                        }
                        double dryGain = 1.0 - fxDelay * .36;
                        double wetGain = fxDelay * 1.28;
                        left = incomingL * dryGain + echoL * wetGain;
                        right = incomingR * dryGain + echoR * wetGain;
                    }
                    delayWrite++;
                    if (delayWrite == delayLeft.Length) delayWrite = 0;
                }
                // Compact parallel-comb stereo room. Each side has two different
                // lengths; feedback below unity is stable even at 100% ROOM.
                if (fxReverb > .0001 && roomLeftA != null)
                {
                    double aL = roomLeftA[roomWriteLA], bL = roomLeftB[roomWriteLB];
                    double aR = roomRightA[roomWriteRA], bR = roomRightB[roomWriteRB];
                    roomLeftA[roomWriteLA] = Clip(left * .28 + aL * reverbFeedback);
                    roomLeftB[roomWriteLB] = Clip(left * .23 + bL * reverbFeedback);
                    roomRightA[roomWriteRA] = Clip(right * .28 + aR * reverbFeedback);
                    roomRightB[roomWriteRB] = Clip(right * .23 + bR * reverbFeedback);
                    if (++roomWriteLA == roomLeftA.Length) roomWriteLA = 0;
                    if (++roomWriteLB == roomLeftB.Length) roomWriteLB = 0;
                    if (++roomWriteRA == roomRightA.Length) roomWriteRA = 0;
                    if (++roomWriteRB == roomRightB.Length) roomWriteRB = 0;
                    double mix = fxReverb * .50;
                    left = left * (1.0 - mix) + (aL + bL) * mix * 1.05;
                    right = right * (1.0 - mix) + (aR + bR) * mix * 1.05;
                }
                // Cap occasional combined resonance/feedback peaks at the voice,
                // before the shared master stage. All-bypass is unchanged.
                if (fxDistortion > .0001 || fxChorus > .0001 || fxDelay > .0001 || fxReverb > .0001)
                {
                    left = Math.Max(-1.0, Math.Min(1.0, left));
                    right = Math.Max(-1.0, Math.Min(1.0, right));
                }
                // ACID LEVEL is an output trim, applied after distortion,
                // chorus, delay, reverb and safety limiting. Changing it must
                // not change filter response or distortion drive.
                smoothedOutputLevel += (outputLevel - smoothedOutputLevel) * outputSlew;
                // Factory calibration and user LEVEL are independent. For
                // legacy saves, the patch trim defaults to unity gain.
                smoothedPatchOutputTrim += (patchOutputTrim -
                    smoothedPatchOutputTrim) * outputSlew;
                left *= smoothedOutputLevel * smoothedPatchOutputTrim;
                right *= smoothedOutputLevel * smoothedPatchOutputTrim;
                peak = (float)Math.Max(peak, Math.Max(Math.Abs(left), Math.Abs(right)));
                int baseIndex = frame * channels;
                if (channels == 1) data[baseIndex] = (float)((left + right) * .5);
                else
                    for (int c = 0; c < channels; c++)
                        data[baseIndex + c] = (float)((c & 1) == 0 ? left : right);
            }
            if (offset > 0) audioQueue.RemoveRange(0, offset);
            lastPeak = peak;
        }
    }
}
