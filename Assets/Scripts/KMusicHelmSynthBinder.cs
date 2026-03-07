// Assets/Scripts/KMusicHelmSynthBinder.cs
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using AudioHelm;
using KMusic.Core;

namespace KMusic
{
    [DefaultExecutionOrder(100)]
    public class KMusicHelmSynthBinder : MonoBehaviour
    {
        [Header("Refs (auto-find if null)")]
        [SerializeField] private HelmController helm;

        [Header("Spam guard")]
        [SerializeField] private float epsilon = 0.0005f;

        [Header("Debug")]
        [SerializeField] private bool logBusChanges = false;
        [SerializeField] private bool logApply = false;
        [SerializeField] private bool logPull = false;

        private ParameterBus _bus;
        private Coroutine _co;

        // Prevent feedback loops when we mirror Helm->Bus (or Bus->Helm)
        private bool _suppressBusEvents = false;

        private readonly Dictionary<string, float> _lastSent = new Dictionary<string, float>();

        private bool _pulledOnce = false;

        // Central mapping (Bus id -> Helm Param)
        private static readonly Dictionary<string, Param> Map = new Dictionary<string, Param>
        {
            // MASTER
            { "master.vol",   Param.kVolume },
            { "master.porta", Param.kPortamento },
            { "master.poly",  Param.kPolyphony },

            // OSC1
            { "osc1.vol",    Param.kOsc1Volume },
            { "osc1.tune",   Param.kOsc1Tune },
            { "osc1.trans",  Param.kOsc1Transpose },
            { "osc1.uni",    Param.kOsc1UnisonVoices },
            { "osc1.detune", Param.kOsc1UnisonDetune },
            { "osc1.harm",   Param.kOsc1UnisonHarmonize },

            // OSC2
            { "osc2.vol",    Param.kOsc2Volume },
            { "osc2.tune",   Param.kOsc2Tune },
            { "osc2.trans",  Param.kOsc2Transpose },
            { "osc2.uni",    Param.kOsc2UnisonVoices },
            { "osc2.detune", Param.kOsc2UnisonDetune },
            { "osc2.harm",   Param.kOsc2UnisonHarmonize },

            // SUB / NOISE / MISC
            { "sub.vol",       Param.kSubVolume },
            { "sub.oct",       Param.kSubOctave },
            { "noise.vol",     Param.kNoiseVolume },
            { "synth.xmod",    Param.kCrossMod },
            { "filter.keytrk", Param.kKeytrack },
            { "synth.vel",     Param.kVelocityTrack },

            // FILTER
            { "filter.cutoff", Param.kFilterCutoff },
            { "filter.res",    Param.kResonance },
            { "filter.drive",  Param.kFilterDrive },
            { "filter.atk",    Param.kFilterAttack },
            { "filter.dec",    Param.kFilterDecay },
            { "filter.sus",    Param.kFilterSustain },
            { "filter.rel",    Param.kFilterRelease },
            { "filter.env",    Param.kFilterEnvelopeDepth },

            // AMP
            { "amp.atk",  Param.kAmplitudeAttack },
            { "amp.dec",  Param.kAmplitudeDecay },
            { "amp.sus",  Param.kAmplitudeSustain },
            { "amp.rel2", Param.kAmplitudeRelease },

            // MOD
            { "mod.rate",  Param.kMonoLfo1Frequency },
            { "mod.depth", Param.kMonoLfo1Amplitude },
            { "mod.atk",   Param.kModAttack },
            { "mod.dec",   Param.kModDecay },
            { "mod.sus",   Param.kModSustain },
            { "mod.rel",   Param.kModRelease },

            // FX: DELAY
            { "fx.delay.mix",  Param.kDelayDryWet },
            { "fx.delay.fb",   Param.kDelayFeedback },
            { "fx.delay.time", Param.kDelayTempo },

            // FX: REVERB
            { "fx.rev.mix",  Param.kReverbDryWet },
            { "fx.rev.fb",   Param.kReverbFeedback },
            { "fx.rev.damp", Param.kReverbDamping },

            // FX: DISTORTION
            { "fx.dist.on",    Param.kDistortionOn },
            { "fx.dist.type",  Param.kDistortionType },
            { "fx.dist.drive", Param.kDistortionDrive },
            { "fx.dist.mix",   Param.kDistortionMix },            
        };

        private void OnEnable()
        {
            if (_co != null) StopCoroutine(_co);
            _co = StartCoroutine(BindWhenReady());
        }

        private void OnDisable()
        {
            if (_co != null) StopCoroutine(_co);
            _co = null;

            if (_bus != null)
                _bus.OnChanged -= OnBusChanged;

            _bus = null;
            _lastSent.Clear();
            _suppressBusEvents = false;
        }

        private IEnumerator BindWhenReady()
        {
            // 1) Prefer the HelmController that the HelmSequencer is actually using.
            if (helm == null)
            {
                var seq = FindObjectOfType<HelmSequencer>();
                if (seq != null)
                {
                    var field = seq.GetType().GetField("helmController", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (field != null && field.FieldType == typeof(HelmController))
                        helm = field.GetValue(seq) as HelmController;
                }
            }

            // 2) Fallback: any HelmController in scene
            if (helm == null) helm = FindObjectOfType<HelmController>();

            if (helm == null)
            {
                Debug.LogWarning("[KMusicHelmSynthBinder] No HelmController found in scene.");
                yield break;
            }

            // Find KMusicApp INCLUDING inactive objects
            KMusicApp app = null;

#if UNITY_2023_1_OR_NEWER
            var apps = FindObjectsByType<KMusicApp>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
            var apps = Resources.FindObjectsOfTypeAll<KMusicApp>();
#endif
            if (apps != null && apps.Length > 0)
                app = apps[0];

            if (app == null)
            {
                Debug.LogWarning("[KMusicHelmSynthBinder] No KMusicApp found in scene (even inactive).");
                yield break;
            }

            while (app.Bus == null)
                yield return null;

            _bus = app.Bus;

            // Subscribe (single)
            _bus.OnChanged -= OnBusChanged;
            _bus.OnChanged += OnBusChanged;

            ApplyAllFromBus();
            _pulledOnce = true;

            _lastSent.Clear();

            Debug.Log($"[KMusicHelmSynthBinder] Bound OK. helm={helm.name} bus={(_bus != null)}");
        }

        private void ApplyAllFromBus()
        {
            if (_bus == null || helm == null) return;

            _lastSent.Clear();

            foreach (var kvp in Map)
            {
                string id = kvp.Key;

                if (!_bus.TryGet(id, out _))
                    continue;

                ApplyOne(id);
            }
        }

        /// <summary>
        /// Mirrors Helm's current parameter percents into the ParameterBus (so UI reflects synth).
        /// </summary>
        private void PullHelmToBus()
        {
            if (_bus == null || helm == null) return;

            _suppressBusEvents = true;
            try
            {
                foreach (var kvp in Map)
                {
                    string id = kvp.Key;
                    Param p = kvp.Value;

                    // Only mirror ids that exist in your bus
                    if (!_bus.TryGet(id, out _))
                        continue;

                    float t = GetHelmPercentSafe(p);

                    if (logPull)
                        Debug.Log($"[PULL] {id} <- {p} = {t:0.000}");

                    // Write normalized into bus (reflection so we don't depend on exact API name)
                    if (!TrySetBusNormalized(_bus, id, t))
                    {
                        Debug.LogWarning($"[KMusicHelmSynthBinder] Couldn't set bus normalized for '{id}'. (No compatible setter found on ParameterBus)");
                    }
                }
            }
            finally
            {
                _suppressBusEvents = false;
            }
        }

        private void OnBusChanged(string id, float value)
        {
            if (_bus == null || helm == null) return;
            if (_suppressBusEvents) return;

            float n = GetBusNormalizedSafe(id);

            if (logBusChanges)
                Debug.Log($"[BUS] {id} -> norm={n:0.000}");

            ApplyOne(id);
        }

        private void ApplyOne(string id)
        {
            if (_bus == null || helm == null) return;

            if (!Map.TryGetValue(id, out var param))
                return;

            float t = Mathf.Clamp01(GetBusNormalizedSafe(id));

            if (id == "fx.dist.type")
                t = SnapDistType(t);          
            
            if (!ShouldSend(id, t)) return;

            if (logApply)
                Debug.Log($"[APPLY] {id} -> {param} = {t:0.000}");

            helm.SetParameterPercent(param, t);
        }

        private float SnapDistType(float t)
        {
            // Adjust this count to match Helm's real distortion type count.
            const int typeCount = 4;

            if (typeCount <= 1) return 0f;

            int index = Mathf.RoundToInt(t * (typeCount - 1));
            index = Mathf.Clamp(index, 0, typeCount - 1);

            return index / (float)(typeCount - 1);
        }

        private bool ShouldSend(string id, float t)
        {
            if (_lastSent.TryGetValue(id, out float prev))
            {
                if (Mathf.Abs(prev - t) < epsilon) return false;
                _lastSent[id] = t;
                return true;
            }

            _lastSent[id] = t;
            return true;
        }

        // ---- Helpers ----

        private float GetBusNormalizedSafe(string id)
        {
            if (_bus == null) return 0f;

            // Prefer your direct API if it exists
            try
            {
                return Mathf.Clamp01(_bus.GetNormalized(id));
            }
            catch
            {
                // Reflection fallback (in case your ParameterBus differs)
                var type = _bus.GetType();
                var m = type.GetMethod("GetNormalized", new[] { typeof(string) });
                if (m != null)
                {
                    object v = m.Invoke(_bus, new object[] { id });
                    if (v is float f) return Mathf.Clamp01(f);
                }
                return 0f;
            }
        }

        private float GetHelmPercentSafe(Param p)
        {
            // Most HelmController versions have GetParameterPercent(Param)
            var mi = typeof(HelmController).GetMethod("GetParameterPercent", new[] { typeof(Param) });
            if (mi != null)
            {
                object v = mi.Invoke(helm, new object[] { p });
                if (v is float f) return Mathf.Clamp01(f);
            }

            // Fallback: try GetParameterValue(int) and treat it as normalized
            var mi2 = typeof(HelmController).GetMethod("GetParameterValue", new[] { typeof(int) });
            if (mi2 != null)
            {
                object v = mi2.Invoke(helm, new object[] { (int)p });
                if (v is float f) return Mathf.Clamp01(f);
            }

            // If your Helm version exposes neither, we can't pull safely.
            return 0f;
        }

        private static bool TrySetBusNormalized(ParameterBus bus, string id, float t)
        {
            var type = bus.GetType();
            t = Mathf.Clamp01(t);

            // Try common names/signatures without hard dependency.
            // 1) SetNormalized(string, float)
            var m1 = type.GetMethod("SetNormalized", new[] { typeof(string), typeof(float) });
            if (m1 != null) { m1.Invoke(bus, new object[] { id, t }); return true; }

            // 2) SetNormalized(string, float, bool)
            var m2 = type.GetMethod("SetNormalized", new[] { typeof(string), typeof(float), typeof(bool) });
            if (m2 != null) { m2.Invoke(bus, new object[] { id, t, true }); return true; }

            // 3) Set(string, float)
            var m3 = type.GetMethod("Set", new[] { typeof(string), typeof(float) });
            if (m3 != null) { m3.Invoke(bus, new object[] { id, t }); return true; }

            // 4) SetValue(string, float)
            var m4 = type.GetMethod("SetValue", new[] { typeof(string), typeof(float) });
            if (m4 != null) { m4.Invoke(bus, new object[] { id, t }); return true; }

            return false;
        }

        public void RequestPullFromHelm()
        {
            PullHelmToBus();
            _lastSent.Clear();
        }        
    }
}