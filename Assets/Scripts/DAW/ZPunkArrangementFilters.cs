using System;
using System.Collections.Generic;
using AudioHelm;
using UnityEngine;
using UnityEngine.Audio;

namespace ZPunk.DAW
{
    /// <summary>
    /// Two independent, post-instrument filter pairs. MUSIC affects Helm's
    /// Bass/Synth/Pads mixer outputs and CHOPS AudioSource voices; DRUMS affects
    /// drum AudioSource voices only. Native Helm patch/filter parameters are
    /// deliberately untouched. No filtering is applied to the final master.
    /// </summary>
    public sealed class ZPunkArrangementFilters : MonoBehaviour
    {
        public const string MusicLowParam = "ZPunkMusicLowPassHz";
        public const string MusicHighParam = "ZPunkMusicHighPassHz";
        private const string Pref = "ZPunk.2.0.7.ArrangementFilter.";
        private static readonly List<AudioLowPassFilter> drumLows = new List<AudioLowPassFilter>();
        private static readonly List<AudioHighPassFilter> drumHighs = new List<AudioHighPassFilter>();
        private static readonly List<AudioLowPassFilter> chopLows = new List<AudioLowPassFilter>();
        private static readonly List<AudioHighPassFilter> chopHighs = new List<AudioHighPassFilter>();
        private static readonly HashSet<AudioMixer> musicMixers = new HashSet<AudioMixer>();
        private static bool loaded;
        private static readonly float[] values = { 1f, 0f, 1f, 0f }; // MUSIC LP/HP, DRUM LP/HP
        private static bool warnedMissingMixer;

        public static float Get(bool drums, bool lowPass)
        {
            EnsureLoaded();
            return values[(drums ? 2 : 0) + (lowPass ? 0 : 1)];
        }

        public static void Set(bool drums, bool lowPass, float amount)
        {
            EnsureLoaded();
            int index = (drums ? 2 : 0) + (lowPass ? 0 : 1);
            values[index] = Mathf.Clamp01(amount);
            PlayerPrefs.SetFloat(Pref + index, values[index]);
            Apply();
        }

        private static void EnsureLoaded()
        {
            if (loaded) return;
            loaded = true;
            for (int i = 0; i < values.Length; ++i)
                values[i] = Mathf.Clamp01(PlayerPrefs.GetFloat(Pref + i, i % 2 == 0 ? 1f : 0f));
        }

        // Perceptually uniform Hz spacing for touch-knobs. Neutral ends bypass.
        public static float Cutoff(bool lowPass, float normalized)
        {
            if (lowPass && normalized >= 0.995f) return 22000f;
            if (!lowPass && normalized <= 0.005f) return 10f;
            return Mathf.Clamp(20f * Mathf.Pow(1100f, Mathf.Clamp01(normalized)), 10f, 22000f);
        }

        public static string Readout(bool lowPass, float normalized)
        {
            if ((lowPass && normalized >= .995f) || (!lowPass && normalized <= .005f)) return "OFF";
            float hz = Cutoff(lowPass, normalized);
            return hz >= 1000f ? (hz / 1000f).ToString("0.#") + "k" : hz.ToString("0") + "Hz";
        }

        /// <summary>Called on creation of drum/chop voices; preserves their existing
        /// output mixer routing and source gains. Safe to call repeatedly.</summary>
        public static void RegisterVoice(AudioSource source, bool drums)
        {
            if (source == null) return;
            EnsureLoaded();
            var lp = source.GetComponent<AudioLowPassFilter>();
            if (lp == null) lp = source.gameObject.AddComponent<AudioLowPassFilter>();
            var hp = source.GetComponent<AudioHighPassFilter>();
            if (hp == null) hp = source.gameObject.AddComponent<AudioHighPassFilter>();
            var lows = drums ? drumLows : chopLows;
            var highs = drums ? drumHighs : chopHighs;
            if (!lows.Contains(lp)) lows.Add(lp);
            if (!highs.Contains(hp)) highs.Add(hp);
            SetSourcePair(lp, hp, drums);
        }

        private static void SetSourcePair(AudioLowPassFilter lp, AudioHighPassFilter hp, bool drums)
        {
            int index = drums ? 2 : 0;
            if (lp != null)
            {
                lp.cutoffFrequency = Cutoff(true, values[index]);
                lp.lowpassResonanceQ = 1f;
                lp.enabled = values[index] < .995f;
            }
            if (hp != null)
            {
                hp.cutoffFrequency = Cutoff(false, values[index + 1]);
                hp.highpassResonanceQ = 1f;
                hp.enabled = values[index + 1] > .005f;
            }
        }

        private static void ApplyList(List<AudioLowPassFilter> lows, List<AudioHighPassFilter> highs, bool drums)
        {
            for (int i = lows.Count - 1; i >= 0; i--)
                if (lows[i] == null) lows.RemoveAt(i);
                else { lows[i].cutoffFrequency = Cutoff(true, values[drums ? 2 : 0]);
                       lows[i].enabled = values[drums ? 2 : 0] < .995f; }
            for (int i = highs.Count - 1; i >= 0; i--)
                if (highs[i] == null) highs.RemoveAt(i);
                else { highs[i].cutoffFrequency = Cutoff(false, values[drums ? 3 : 1]);
                       highs[i].enabled = values[drums ? 3 : 1] > .005f; }
        }

        public static void RefreshHelmMixers()
        {
            EnsureLoaded();
            musicMixers.Clear();
            foreach (var controller in FindObjectsOfType<HelmController>())
            {
                if (controller == null) continue;
                var source = controller.GetComponent<AudioSource>();
                if (source == null || source.outputAudioMixerGroup == null) continue;
                var mixer = source.outputAudioMixerGroup.audioMixer;
                if (mixer == null) continue;
                // Never touch unrelated mixer buses: only the three Helm instruments.
                if (mixer.name == "Helm" || mixer.name == "HelmPads" || mixer.name == "ZPunkBassHelm")
                    musicMixers.Add(mixer);
            }
            Apply();
        }

        private static void Apply()
        {
            EnsureLoaded();
            ApplyList(chopLows, chopHighs, false);
            ApplyList(drumLows, drumHighs, true);
            foreach (AudioMixer mixer in musicMixers)
            {
                if (mixer == null) continue;
                // Exposed parameters in all 3 Helm mixer assets. No Helm patch changes.
                bool lo = mixer.SetFloat(MusicLowParam, Cutoff(true, values[0]));
                bool hi = mixer.SetFloat(MusicHighParam, Cutoff(false, values[1]));
                if ((!lo || !hi) && !warnedMissingMixer)
                {
                    warnedMissingMixer = true;
                    Debug.LogWarning("[ZPUNK FILTERS] Helm mixer is missing filter effect/parameter. " +
                        "Import the three patched .mixer assets alongside the scripts.");
                }
            }
        }

        private void OnEnable() { RefreshHelmMixers(); }
        private void Start() { RefreshHelmMixers(); }
    }
}
