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
                fxDistortion = state.fxDistortion, fxChorus = state.fxChorus,
                fxDelay = state.fxDelay, fxFeedback = state.fxFeedback,
                fxReverb = state.fxReverb, fxRoom = state.fxRoom,
                fxDelayRate = state.fxDelayRate
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
            // All new rack amounts default to zero in existing song JSON:
            // with every FX at zero, original DSP output is bit-identical.
            double fxDistortion = Norm(p.fxDistortion);
            double distortionGain = 1.0 + fxDistortion * 10.0;
            double distortionNormal = fxDistortion > .0001 ? 1.0 / Math.Tanh(distortionGain) : 1.0;
            double fxChorus = Norm(p.fxChorus);
            double fxDelay = Norm(p.fxDelay);
            double fxFeedback = .14 + Norm(p.fxFeedback) * .73;
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
                double fc = cutoffBase * Math.Exp((filterEnvelope * (envDepth + accent * 1.2)));
                fc = Math.Max(35.0, Math.Min(sampleRate * .34, fc));
                // A feedback-saturated, four-stage resonant low-pass topology;
                // stable at mobile sample rates and smooth under live cutoff sweeps.
                double a = 1.0 - Math.Exp(-2.0 * Math.PI * fc * delta);
                double input = Clip((osc * (1.0 + accent * .35) - lp4 * res) * drive);
                lp1 += a * (input - lp1);
                lp2 += a * (Clip(lp1 * 1.4) - lp2);
                lp3 += a * (Clip(lp2 * 1.4) - lp3);
                lp4 += a * (Clip(lp3 * 1.4) - lp4);
                double sample = Clip(lp4 * 2.0) * envelope * g * .62;
                double left = sample, right = sample;
                // Post-filter warm overdrive. Pure bypass at 0.
                if (fxDistortion > .0001)
                {
                    double saturated = Math.Tanh(sample * distortionGain) * distortionNormal;
                    left = right = sample * (1.0 - fxDistortion * .85) +
                        saturated * fxDistortion * .85;
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
                    double incomingL = left, incomingR = right;
                    delayLeft[delayWrite] = Clip(incomingL * .72 + tapR * fxFeedback);
                    delayRight[delayWrite] = Clip(incomingR * .12 + tapL * fxFeedback);
                    double mix = fxDelay * .59;
                    left = incomingL + tapL * mix;
                    right = incomingR + tapR * mix;
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
