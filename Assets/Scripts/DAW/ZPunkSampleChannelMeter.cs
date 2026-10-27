using System;
using UnityEngine;

/// <summary>
/// Sample-voice PCM stage: existing lane/track faders plus optional per-drum
/// tone, decay, drive, trim and gentle dynamics. This filter runs on the audio
/// thread, uses no UnityEngine APIs or allocations, and does not touch CHOPS.
/// </summary>
[RequireComponent(typeof(AudioSource))]
public sealed class ZPunkSampleChannelMeter : MonoBehaviour
{
    private volatile float gain = 1f;
    private volatile float peak;
    private volatile float trimGain = 1f;
    private volatile float tone = 0.5f;
    private volatile float decay = 1f;
    private volatile float drive;
    private volatile float punch;
    private volatile float hitSeconds = 0.25f;
    private int sampleRate = 48000;
    private double samplePosition;
    private float filterL, filterR;
    private float compressorEnvelope;
    public float Gain { get => gain; set => gain = Mathf.Clamp(value, 0f, 4f); }
    public float Peak => peak;

    public void SetDrumShape(ZPunk.DAW.DrumShapeData shape)
    {
        if (shape == null)
        {
            trimGain = 1f; tone = .5f; decay = 1f; drive = 0f; punch = 0f;
            return;
        }
        trimGain = Mathf.Pow(10f, Mathf.Clamp(shape.trim, -12f, 12f) / 20f);
        tone = Mathf.Clamp01(shape.tone);
        decay = Mathf.Clamp(shape.decay, 0.04f, 1f);
        drive = Mathf.Clamp01(shape.drive);
        punch = Mathf.Clamp01(shape.punch);
    }

    public void PrepareHit(float durationSeconds)
    {
        // Voices are stopped and reused before this reset; no envelopes are
        // shared across voices or lanes. The audio thread owns filter history.
        hitSeconds = Mathf.Max(0.015f, durationSeconds);
        sampleRate = Mathf.Max(8000, AudioSettings.outputSampleRate);
        samplePosition = 0;
        filterL = filterR = compressorEnvelope = 0f;
    }

    private void OnAudioFilterRead(float[] data, int channels)
    {
        if (data == null || data.Length == 0 || channels <= 0) { peak = 0f; return; }
        float g = gain * trimGain;
        float localTone = tone;
        float localDecay = decay;
        float localDrive = drive;
        float localPunch = punch;
        float seconds = hitSeconds;
        float max = 0f;
        // At 50% tone the legacy signal is unchanged. The single-pole section
        // below is deliberately subtle and uses a separate history per channel.
        float cutoff = localTone < .5f
            ? 350f * (float)Math.Pow(45.0, localTone / .5f)
            : 3200f;
        float alpha = 1f - (float)Math.Exp(-2.0 * Math.PI * cutoff / sampleRate);
        float boost = Math.Max(0f, (localTone - .5f) * .7f);
        float compressedRelease = (float)Math.Exp(-1.0 / (Math.Max(8000, sampleRate) * .06));
        for (int frame = 0; frame < data.Length / channels; ++frame)
        {
            // No shortening at DECAY 100%; shorter settings taper to zero with
            // a squared, click-free envelope rather than stopping AudioSource.
            float env = 1f;
            if (localDecay < .999f)
            {
                double t = samplePosition / Math.Max(.015, seconds * localDecay * sampleRate);
                if (t >= 1.0) env = 0f;
                else { float remain = (float)(1.0 - t); env = remain * remain; }
            }
            samplePosition++;
            for (int channel = 0; channel < channels; ++channel)
            {
                int i = frame * channels + channel;
                float x = data[i];
                if (localTone < .495f || boost > .0001f)
                {
                    float low = channel == 0 ? filterL : filterR;
                    low += alpha * (x - low);
                    if (channel == 0) filterL = low; else filterR = low;
                    x = localTone < .495f ? low : x + (x - low) * boost;
                }
                x *= g * env;
                if (localDrive > .0001f)
                {
                    float amount = localDrive * 2.2f;
                    x = x * (1f + amount) / (1f + amount * Math.Abs(x));
                }
                if (localPunch > .0001f)
                {
                    float a = Math.Abs(x);
                    compressorEnvelope = Math.Max(a, compressorEnvelope * compressedRelease);
                    float threshold = .38f;
                    if (compressorEnvelope > threshold)
                    {
                        float target = (float)Math.Pow(threshold / compressorEnvelope, localPunch * .38f);
                        x *= target;
                    }
                }
                data[i] = x;
                float abs = Math.Abs(x);
                if (abs > max) max = abs;
            }
        }
        peak = max;
    }
    private void OnDisable() { peak = 0f; }
}
