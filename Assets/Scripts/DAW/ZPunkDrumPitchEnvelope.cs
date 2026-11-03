using UnityEngine;

/// <summary>
/// One scheduled/preview drum voice's downward pitch sweep. Does not touch
/// the underlying AudioClip, volume, DAC callback, or any other voice.
/// Update uses DSP time to avoid restarts while the transport is running.
/// </summary>
[DisallowMultipleComponent]
public sealed class ZPunkDrumPitchEnvelope : MonoBehaviour
{
    private AudioSource source;
    private float basePitch = 1f;
    private float amount;
    private double startsAt;
    private float sweepSeconds;
    private bool armed;

    private void Awake()
    {
        source = GetComponent<AudioSource>();
        enabled = false;
    }

    public void Configure(float pitch, float depth)
    {
        basePitch = Mathf.Clamp(pitch, .1f, 3f);
        amount = Mathf.Clamp01(depth);
        if (amount <= .001f)
        {
            armed = false;
            enabled = false;
            if (source != null) source.pitch = basePitch;
        }
        else if (!armed && source != null) source.pitch = basePitch;
    }

    public void Arm(double dspTime)
    {
        if (source == null) return;
        if (amount <= .001f)
        {
            source.pitch = basePitch;
            armed = false;
            enabled = false;
            return;
        }
        startsAt = dspTime;
        sweepSeconds = .045f + amount * .105f;
        armed = true;
        enabled = true;
        source.pitch = SweepPitch(1f);
    }

    private float SweepPitch(float remaining)
    {
        float semitones = amount * 14f * remaining * remaining;
        return Mathf.Clamp(basePitch * Mathf.Pow(2f, semitones / 12f), .1f, 3f);
    }

    private void Update()
    {
        if (!armed || source == null) { enabled = false; return; }
        double elapsed = AudioSettings.dspTime - startsAt;
        if (elapsed < 0.0) return; // hit is scheduled in the future
        float fraction = Mathf.Clamp01((float)(elapsed / sweepSeconds));
        if (fraction >= 1f)
        {
            source.pitch = basePitch;
            armed = false;
            enabled = false;
        }
        else source.pitch = SweepPitch(1f - fraction);
    }
}
