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
    private volatile int characterFx;
    private volatile float characterAmount;
    // Per-hit bitcrush state, owned by this AudioSource's audio callback.
    private int crushCounter;
    private float crushLeft, crushRight;
    // Ring-modulator state: oscillator step is precomputed per PCM callback,
    // so METAL performs no trigonometry or allocations inside the frame loop.
    private float ringSin, ringCos = 1f;
    // GATE creates a few sharp, finite early reflections, without a shared FX
    // bus or touching the kit/sample gain. Allocated only when a lane uses it.
    private float[] gateDelayLeft, gateDelayRight;
    private int gateDelayIndex;
    private const int GateDelaySamples = 8192;
    private volatile float hitSeconds = 0.25f;
    // Audible boundaries in playback seconds, excluding silence padding.
    private volatile float audibleStartSeconds;
    private volatile float audibleEndSeconds = .25f;
    private volatile float audibleOnsetThreshold = .00005f;
    // Only increment the decay clock once the incoming drum audio really begins.
    // The old clock included pre-scheduled silence and could expire early.
    private bool audibleOnsetReached;
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
            characterFx = 0; characterAmount = 0f;
            return;
        }
        trimGain = Mathf.Pow(10f, Mathf.Clamp(shape.trim, -12f, 12f) / 20f);
        tone = Mathf.Clamp01(shape.tone);
        decay = Mathf.Clamp(shape.decay, 0.04f, 1f);
        drive = Mathf.Clamp01(shape.drive);
        punch = Mathf.Clamp01(shape.punch);
        characterAmount = Mathf.Clamp01(shape.characterAmount);
        characterFx = Mathf.Clamp(shape.characterFx, 0, 6);
        if (characterFx == 6 && gateDelayLeft == null)
        {
            gateDelayLeft = new float[GateDelaySamples];
            gateDelayRight = new float[GateDelaySamples];
        }
    }

    /// <summary>
    /// Decay is a direct fraction of the sample's audible duration after
    /// silence padding has been excluded. No logarithmic remapping.
    /// At FULL, the sample is unmodified (including any source padding).
    /// </summary>
    public static float DecayTimeSeconds(float audibleSeconds, float amount)
    {
        return Math.Max(.001f, audibleSeconds) * Math.Max(0f, Math.Min(1f, amount));
    }

    public void PrepareHit(float durationSeconds, float audibleStart = 0f,
        float audibleEnd = -1f, float onsetThreshold = .00005f)
    {
        // Voices are stopped and reused before reset; clip analysis is done
        // on the main thread and never from the audio callback.
        hitSeconds = Mathf.Max(.001f, durationSeconds);
        audibleStartSeconds = Mathf.Clamp(audibleStart, 0f, hitSeconds - .00001f);
        audibleEndSeconds = audibleEnd > 0f
            ? Mathf.Clamp(audibleEnd, audibleStartSeconds + .00001f, hitSeconds)
            : hitSeconds;
        audibleOnsetThreshold = Mathf.Max(.00001f, onsetThreshold);
        sampleRate = Mathf.Max(8000, AudioSettings.outputSampleRate);
        samplePosition = 0;
        audibleOnsetReached = false;
        filterL = filterR = compressorEnvelope = 0f;
        crushCounter = 0;
        crushLeft = crushRight = 0f;
        ringSin = 0f; ringCos = 1f;
        gateDelayIndex = 0;
        // Reset old reflections on reuse; never let one hit leak into the next.
        if (gateDelayLeft != null)
        {
            Array.Clear(gateDelayLeft, 0, gateDelayLeft.Length);
            Array.Clear(gateDelayRight, 0, gateDelayRight.Length);
        }
    }

    private void OnAudioFilterRead(float[] data, int channels)
    {
        if (data == null || data.Length == 0 || channels <= 0) { peak = 0f; return; }
        float g = gain * trimGain;
        float localTone = tone;
        float localDecay = decay;
        float localDrive = drive;
        float localPunch = punch;
        bool crush = characterFx == 1 && characterAmount > .001f;
        float crushWet = crush ? characterAmount * .88f : 0f;
        int crushStride = crush ? 1 + (int)Math.Round(characterAmount * 22f) : 1;
        // A sample-and-hold / bit-reduction stage. No changes to source gain.
        float crushSteps = crush ? (float)Math.Pow(2.0,
            15 - (int)Math.Round(characterAmount * 10f)) : 32768f;
        float seconds = hitSeconds;
        bool metal = characterFx == 4 && characterAmount > .001f;
        float metalWet = metal ? characterAmount * .90f : 0f;
        float metalNorm = metal ? (float)Math.Min(1.20,
            1.0 / Math.Sqrt((1.0 - metalWet) * (1.0 - metalWet) + metalWet * metalWet)) : 1f;
        double ringFrequency = 85.0 + 1100.0 * characterAmount * characterAmount;
        double omega = 2.0 * Math.PI * ringFrequency / Math.Max(8000, sampleRate);
        float ringStepSin = metal ? (float)Math.Sin(omega) : 0f;
        float ringStepCos = metal ? (float)Math.Cos(omega) : 1f;
        bool gated = characterFx == 6 && characterAmount > .001f && gateDelayLeft != null;
        // Three short reflections for gated-room percussion. All wet-only:
        // the dry hit (and its DECAY/TRIM) is never shortened or attenuated.
        // Scale reflections for short one-shots so even tiny Resources hats
        // receive an audible early-reflection character.
        int gateTap1 = Math.Max(1, (int)(sampleRate * Math.Min(.009, seconds * .12)));
        int gateTap2 = Math.Max(2, (int)(sampleRate * Math.Min(.020, seconds * .25)));
        int gateTap3 = Math.Max(3, (int)(sampleRate * Math.Min(.036, seconds * .42)));
        int gateLength = Math.Max(1, (int)(sampleRate *
            Math.Min(seconds * .85, .055f + .11f * characterAmount)));
        int gateFade = Math.Max(1, Math.Min((int)(sampleRate * .012f), gateLength / 4));
        float gateWet = gated ? characterAmount * .52f : 0f;
        float max = 0f;
        // At 50% tone the legacy signal is unchanged. The single-pole section
        // below is deliberately subtle and uses a separate history per channel.
        float cutoff = localTone < .5f
            ? 350f * (float)Math.Pow(45.0, localTone / .5f)
            : 3200f;
        float alpha = 1f - (float)Math.Exp(-2.0 * Math.PI * cutoff / sampleRate);
        float boost = Math.Max(0f, (localTone - .5f) * .7f);
        float compressedRelease = (float)Math.Exp(-1.0 / (Math.Max(8000, sampleRate) * .06));
        // Silent padding is removed from the LENGTH used by DECAY. The audio
        // itself is never moved or truncated, and FULL always bypasses decay.
        // We start the timer at the actual incoming audio onset, not PrepareHit:
        // scheduled voices can sit idle for several audio buffers before PLAY.
        bool shortenHit = localDecay < .9999f;
        double audibleSeconds = Math.Max(.00001,
            audibleEndSeconds - audibleStartSeconds);
        double endFrame = DecayTimeSeconds((float)audibleSeconds, localDecay) * sampleRate;
        double fadeFrames = Math.Max(1.0, Math.Min(sampleRate * .008,
            Math.Min(audibleSeconds * sampleRate * .08,
                endFrame * .35)));
        float onsetThreshold = audibleOnsetThreshold;
        for (int frame = 0; frame < data.Length / channels; ++frame)
        {
            if (!audibleOnsetReached)
            {
                int frameOffset = frame * channels;
                for (int channel = 0; channel < channels; ++channel)
                    if (Math.Abs(data[frameOffset + channel]) >= onsetThreshold)
                    {
                        audibleOnsetReached = true;
                        break;
                    }
            }
            float env = 1f;
            if (shortenHit && audibleOnsetReached)
            {
                double remaining = endFrame - samplePosition;
                if (remaining <= 0.0) env = 0f;
                else if (remaining < fadeFrames)
                {
                    // Short, smooth anti-click ramp right at the audible cut.
                    float t = (float)(remaining / fadeFrames);
                    env = t * t * (3f - 2f * t);
                }
            }
            float carrier = ringSin;
            if (metal)
            {
                float nextSin = ringSin * ringStepCos + ringCos * ringStepSin;
                ringCos = ringCos * ringStepCos - ringSin * ringStepSin;
                ringSin = nextSin;
            }
            float gateEnvelope = gated ? (samplePosition < gateLength - gateFade ? 1f :
                samplePosition >= gateLength ? 0f :
                (float)((gateLength - samplePosition) / gateFade)) : 0f;
            if (audibleOnsetReached) samplePosition++;
            bool crushCapture = crush && crushCounter <= 0;
            if (crushCapture) crushCounter = crushStride;
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
                if (crush)
                {
                    if (crushCapture)
                    {
                        float held = (float)Math.Round(x * crushSteps) / crushSteps;
                        if (channel == 0) crushLeft = held;
                        else crushRight = held;
                    }
                    float heldSample = channel == 0 ? crushLeft : crushRight;
                    x += (heldSample - x) * crushWet;
                }
                if (metal)
                    x = x * ((1f - metalWet) + (1.41421356f * carrier) * metalWet) * metalNorm;
                if (gated && channel < 2)
                {
                    float[] delays = channel == 0 ? gateDelayLeft : gateDelayRight;
                    // Buffer size is a power of two; no modulo allocation.
                    int mask = GateDelaySamples - 1;
                    float reflect = delays[(gateDelayIndex - gateTap1) & mask] * .53f +
                        delays[(gateDelayIndex - gateTap2) & mask] * .34f +
                        delays[(gateDelayIndex - gateTap3) & mask] * .21f;
                    delays[gateDelayIndex] = x;
                    x += reflect * gateWet * gateEnvelope;
                }
                data[i] = x;
                float abs = Math.Abs(x);
                if (abs > max) max = abs;
            }
            if (crush) --crushCounter;
            if (gated) gateDelayIndex = (gateDelayIndex + 1) & (GateDelaySamples - 1);
        }
        peak = max;
    }
    private void OnDisable() { peak = 0f; }
}
