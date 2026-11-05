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

        public float Peak => lastPeak;

        public void Initialize()
        {
            if (source != null) return;
            sampleRate = Math.Max(8000, AudioSettings.outputSampleRate);
            source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = true;
            source.spatialBlend = 0f;
            // The silent clip is only a DSP clock. The callback writes all audio.
            source.clip = AudioClip.Create("ACID DSP clock", 1024, 1, sampleRate, false);
            source.volume = 1f;
            source.Play();
        }

        public void Apply(AcidState state, float trackGain)
        {
            if (state == null) state = new AcidState();
            // Publish a new immutable snapshot; never change the audio thread's
            // parameter object in place during an interactive drag.
            parameters = new AcidState {
                square = state.square, tune = state.tune, cutoff = state.cutoff,
                resonance = state.resonance, envMod = state.envMod, decay = state.decay,
                accent = state.accent, drive = state.drive, glide = state.glide
            };
            gain = Mathf.Clamp(trackGain, 0f, 2f);
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
                float output = (float)sample;
                peak = Math.Max(peak, Math.Abs(output));
                int baseIndex = frame * channels;
                for (int c = 0; c < channels; c++) data[baseIndex + c] = output;
            }
            if (offset > 0) audioQueue.RemoveRange(0, offset);
            lastPeak = peak;
        }
    }
}
