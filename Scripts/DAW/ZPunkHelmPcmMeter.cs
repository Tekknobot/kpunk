using System;
using AudioHelm;
using UnityEngine;

namespace ZPunk.DAW
{
    /// <summary>
    /// Silent monitor for Helm's native per-channel PCM buffer. The monitor's output
    /// is always cleared, so it must never add another audible synth to the mix.
    /// Gain is applied by the transport to the measured PCM in the same linear
    /// domain as the Helm AudioMixer group's exposed attenuation parameter.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public sealed class ZPunkHelmPcmMeter : MonoBehaviour
    {
        public int channel;
        private volatile float measuredPeak;
        private volatile bool available;
        private bool warned;

        public bool Available => available;
        public float Peak => measuredPeak;

        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (data == null || data.Length == 0 || channels < 1) return;
            float peak = 0f;
            bool ok = false;
            try
            {
                // Native function used by AudioHelm.HelmAudioReceive.
                ok = Native.HelmGetBufferData(channel, data, data.Length / channels, channels);
                if (ok)
                {
                    for (int i = 0; i < data.Length; ++i)
                    {
                        float sample = data[i];
                        if (!float.IsNaN(sample) && !float.IsInfinity(sample))
                            peak = Mathf.Max(peak, Mathf.Abs(sample));
                    }
                }
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            finally
            {
                Array.Clear(data, 0, data.Length);
            }
            available = ok;
            measuredPeak = ok ? peak : 0f;
        }
    }
}
