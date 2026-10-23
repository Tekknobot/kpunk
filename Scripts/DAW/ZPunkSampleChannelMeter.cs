using UnityEngine;

/// <summary>
/// Unity sample-voice post-fader PCM gain and metering. Unlike reading an
/// AudioSource before its mixer fader, this applies the fader to the samples
/// and measures the actual samples that are forwarded to Unity's audio mixer.
/// DRUMS, its eight lanes, and CHOPS all use the same component/measurement.
/// AudioSource.volume supplies velocity or sample-master attenuation; the UI
/// includes that value once when reporting this tap's peak.
/// </summary>
[RequireComponent(typeof(AudioSource))]
public sealed class ZPunkSampleChannelMeter : MonoBehaviour
{
    private volatile float gain = 1f;
    private volatile float peak;
    public float Gain { get => gain; set => gain = Mathf.Clamp(value, 0f, 4f); }
    public float Peak => peak;

    private void OnAudioFilterRead(float[] data, int channels)
    {
        if (data == null || data.Length == 0) { peak = 0f; return; }
        float g = gain;
        float max = 0f;
        for (int i = 0; i < data.Length; ++i)
        {
            float sample = data[i] * g;
            data[i] = sample;
            float magnitude = sample >= 0f ? sample : -sample;
            if (magnitude > max) max = magnitude;
        }
        peak = max;
    }

    private void OnDisable() { peak = 0f; }
}
