// KMusicApp.cs

using System;
using UnityEngine;
using UnityEngine.UIElements;
using KMusic.Core;

namespace KMusic
{
    // ✅ Make sure Bus exists before binders/other UI scripts run.
    [DefaultExecutionOrder(-1000)]
    public sealed class KMusicApp : MonoBehaviour
    {
        private const int PrefVer = 2;
        private static readonly string PrefKey_Bus = "kmusic.bus.v" + PrefVer;
        private static readonly string PrefKey_BusVer = "kmusic.bus.ver";

        [Header("UI Toolkit")]
        public VisualTreeAsset mainUxml;
        public StyleSheet darkTheme;

        private UIDocument _doc;
        private ParameterBus _bus;

        public ParameterBus Bus => _bus;

        private ThemeStyleSheet _runtimeTheme;

        // prevent stacking event handlers on enable/disable
        private Action<string, float> _onBusChanged;

        private void Awake()
        {
            Application.targetFrameRate = 60;

            _doc = GetComponent<UIDocument>();
            if (_doc == null) _doc = gameObject.AddComponent<UIDocument>();

            if (mainUxml == null)
                mainUxml = Resources.Load<VisualTreeAsset>("KMusic/UI/Main");

            if (darkTheme == null)
                darkTheme = Resources.Load<StyleSheet>("KMusic/UI/kmusic_dark");

            // ✅ Make sure PanelSettings exists and is assigned properly.
            if (_doc.panelSettings == null)
            {
                var ps = ScriptableObject.CreateInstance<PanelSettings>();
                ps.scaleMode = PanelScaleMode.ConstantPixelSize;
                ps.referenceResolution = new Vector2Int(1024, 2048);

                // UI Toolkit expects a ThemeStyleSheet on PanelSettings.
                if (_runtimeTheme == null)
                    _runtimeTheme = ScriptableObject.CreateInstance<ThemeStyleSheet>();

                ps.themeStyleSheet = _runtimeTheme;
                _doc.panelSettings = ps;
            }

            if (mainUxml != null)
                _doc.visualTreeAsset = mainUxml;

            // If version changed, wipe old prefs so new defaults apply
            int savedVer = PlayerPrefs.GetInt(PrefKey_BusVer, -1);
            if (savedVer != PrefVer)
            {
                PlayerPrefs.DeleteKey("kmusic.bus");        // delete old unversioned key
                PlayerPrefs.DeleteKey(PrefKey_Bus);         // delete current version key (just in case)
                PlayerPrefs.SetInt(PrefKey_BusVer, PrefVer);
                PlayerPrefs.Save();
            }
            BuildParameters();

            // Load saved mixer/fader values after defaults exist.
            KMusicSaveState.LoadBus(_bus, PrefKey_Bus, id =>
                id.StartsWith("mix.") ||
                id.StartsWith("drum.") ||
                id.StartsWith("sampler.") ||
                id.StartsWith("mutes.") ||
                id == "tempo" ||
                id == "sample.master"
            );
        }

        private void OnEnable()
        {
            var root = _doc.rootVisualElement;
            if (root == null) return;

            // Background behind UI (prevents white letterbox/notch areas)
            if (Camera.main != null)
            {
                Camera.main.clearFlags = CameraClearFlags.SolidColor;
                Camera.main.backgroundColor = new Color(0.06f, 0.07f, 0.09f, 1f); // ~#0F1115
            }

            // Fullscreen UI background element
            var bg = root.Q<VisualElement>("KmBg");
            if (bg == null)
            {
                bg = new VisualElement { name = "KmBg" };
                bg.style.position = Position.Absolute;
                bg.style.left = 0;
                bg.style.top = 0;
                bg.style.right = 0;
                bg.style.bottom = 0;
                bg.style.backgroundColor = new Color(0.06f, 0.07f, 0.09f, 1f);
                root.Insert(0, bg);
            }

            // Safe-area padding
            var sa = Screen.safeArea;
            float topInsetPx = Screen.height - (sa.y + sa.height);
            float topInsetUI = 0f;

            if (_doc.panelSettings != null)
            {
                var refRes = _doc.panelSettings.referenceResolution;
                topInsetUI = topInsetPx * (refRes.y / Mathf.Max(1f, Screen.height));
            }

            float baseTop = 64f;
            root.style.paddingTop = baseTop + topInsetUI;

            if (darkTheme != null && !root.styleSheets.Contains(darkTheme))
                root.styleSheets.Add(darkTheme);

            // Bind all custom controls (KnobElement / FaderElement / etc.)
            foreach (var ve in root.Query<VisualElement>().ToList())
            {
                if (ve is KMusic.UI.IParamBindable bindable)
                    bindable.Bind(_bus);
            }

            // BPM label
            var bpmLabel = root.Q<Label>("BpmLabel");
            if (bpmLabel != null && _bus.TryGet("tempo", out var tempo))
                bpmLabel.text = tempo.Format();

            // ✅ avoid stacking listeners across enables
            if (_onBusChanged != null)
                _bus.OnChanged -= _onBusChanged;

            _onBusChanged = (id, v) =>
            {
                if (id == "tempo" && bpmLabel != null && _bus.TryGet("tempo", out var t))
                    bpmLabel.text = t.Format();
            };
            _bus.OnChanged += _onBusChanged;

            // Toggle preset browser on preset label click
            var presetLabel = root.Q<Label>("PresetLabel");
            var presetBrowser = root.Q<VisualElement>("PresetBrowser");
            if (presetLabel != null && presetBrowser != null)
            {
                presetLabel.RegisterCallback<PointerDownEvent>(_ =>
                {
                    presetBrowser.style.display =
                        presetBrowser.resolvedStyle.display == DisplayStyle.None
                            ? DisplayStyle.Flex
                            : DisplayStyle.None;
                });
            }
        }

    private void OnDisable()
    {
        // Save bus values (mixer/drum/sampler/mutes only)
        KMusicSaveState.SaveBus(_bus, PrefKey_Bus, id =>
            id.StartsWith("mix.") ||
            id.StartsWith("drum.") ||
            id.StartsWith("sampler.") ||
            id.StartsWith("mutes.") ||
            id == "tempo" ||
            id == "sample.master"
        );

        // Unhook event handler (prevents duplicate notifications later)
        if (_bus != null && _onBusChanged != null)
            _bus.OnChanged -= _onBusChanged;

        _onBusChanged = null;
    }

    private void OnApplicationPause(bool pause)
    {
        if (!pause) return;

        KMusicSaveState.SaveBus(_bus, PrefKey_Bus, id =>
            id.StartsWith("mix.") ||
            id.StartsWith("drum.") ||
            id.StartsWith("sampler.") ||
            id.StartsWith("mutes.") ||
            id == "tempo" ||
            id == "sample.master"
        );
    }

    private void OnApplicationQuit()
    {
        KMusicSaveState.SaveBus(_bus, PrefKey_Bus, id =>
            id.StartsWith("mix.") ||
            id.StartsWith("drum.") ||
            id.StartsWith("sampler.") ||
            id.StartsWith("mutes.") ||
            id == "tempo" ||
            id == "sample.master"
        );
    }

        private void BuildParameters()
        {
            _bus = new ParameterBus();

            // -----------------
            // SYNTH / MASTER
            // -----------------
            _bus.Add(new Parameter("master.vol",   0f, 1f, 0.80f));
            _bus.Add(new Parameter("master.porta", 0f, 100f, 0f, unit: "%"));
            _bus.Add(new Parameter("master.poly",  0f, 100f, 0f, unit: "%"));

            // OSC1
            _bus.Add(new Parameter("osc1.vol",    0f, 100f, 0f, unit: "%"));
            _bus.Add(new Parameter("osc1.tune",   0f, 100f, 0f, unit: "%"));
            _bus.Add(new Parameter("osc1.trans",  0f, 100f, 0f, unit: "%"));
            _bus.Add(new Parameter("osc1.uni",    0f, 100f, 0f, unit: "%"));
            _bus.Add(new Parameter("osc1.detune", 0f, 100f, 0f, unit: "%"));
            _bus.Add(new Parameter("osc1.harm",   0f, 100f, 0f, unit: "%"));

            // OSC2
            _bus.Add(new Parameter("osc2.vol",    0f, 100f, 0f, unit: "%"));
            _bus.Add(new Parameter("osc2.tune",   0f, 100f, 0f, unit: "%"));
            _bus.Add(new Parameter("osc2.trans",  0f, 100f, 0f, unit: "%"));
            _bus.Add(new Parameter("osc2.uni",    0f, 100f, 0f, unit: "%"));
            _bus.Add(new Parameter("osc2.detune", 0f, 100f, 0f, unit: "%"));
            _bus.Add(new Parameter("osc2.harm",   0f, 100f, 0f, unit: "%"));

            // SUB / NOISE / MISC
            _bus.Add(new Parameter("sub.vol",       0f, 100f, 0f, unit: "%"));
            _bus.Add(new Parameter("sub.oct",       0f, 100f, 0f, unit: "%"));
            _bus.Add(new Parameter("noise.vol",     0f, 100f, 0f, unit: "%"));
            _bus.Add(new Parameter("synth.xmod",    0f, 100f, 0f, unit: "%"));
            _bus.Add(new Parameter("filter.keytrk", 0f, 100f, 0f, unit: "%"));
            _bus.Add(new Parameter("synth.vel",     0f, 100f, 0f, unit: "%"));

            // FX
            _bus.Add(new Parameter("fx.delay.mix",  0f, 100f, 0f, unit: "%"));
            _bus.Add(new Parameter("fx.delay.fb",   0f, 100f, 0f, unit: "%"));
            _bus.Add(new Parameter("fx.delay.time", 0f, 100f, 0f, unit: "%"));

            _bus.Add(new Parameter("fx.rev.mix",  0f, 100f, 0f, unit: "%"));
            _bus.Add(new Parameter("fx.rev.fb",   0f, 100f, 0f, unit: "%"));
            _bus.Add(new Parameter("fx.rev.damp", 0f, 100f, 0f, unit: "%"));

            // -----------------
            // FILTER
            // -----------------
            _bus.Add(new Parameter("filter.cutoff", 20, 20000, 3200, log: true, unit: "hz"));
            _bus.Add(new Parameter("filter.res", 0, 100, 30, unit: "%"));
            _bus.Add(new Parameter("filter.drive", 0, 100, 20, unit: "%"));
            _bus.Add(new Parameter("filter.atk", 0, 500, 120, unit: "ms"));
            _bus.Add(new Parameter("filter.dec", 0, 500, 220, unit: "ms"));
            _bus.Add(new Parameter("filter.sus", 0, 100, 60, unit: "%"));
            _bus.Add(new Parameter("filter.rel", 0, 500, 240, unit: "ms"));

            // -----------------
            // AMP
            // -----------------
            _bus.Add(new Parameter("amp.atk", 0, 500, 120, unit: "ms"));
            _bus.Add(new Parameter("amp.dec", 0, 500, 220, unit: "ms"));
            _bus.Add(new Parameter("amp.sus", 0, 100, 60, unit: "%"));
            _bus.Add(new Parameter("amp.atk2", 0, 500, 320, unit: "ms"));
            _bus.Add(new Parameter("amp.dec2", 0, 500, 240, unit: "ms"));
            _bus.Add(new Parameter("amp.sus2", 0, 100, 55, unit: "%"));
            _bus.Add(new Parameter("amp.rel2", 0, 500, 180, unit: "ms"));

            // -----------------
            // MOD
            // -----------------
            _bus.Add(new Parameter("mod.rate", 0, 20, 4, unit: "hz"));
            _bus.Add(new Parameter("mod.depth", 0, 100, 40, unit: "%"));
            _bus.Add(new Parameter("mod.mix", 0, 100, 30, unit: "%"));
            _bus.Add(new Parameter("mod.atk", 0, 500, 120, unit: "ms"));
            _bus.Add(new Parameter("mod.dec", 0, 500, 220, unit: "ms"));
            _bus.Add(new Parameter("mod.sus", 0, 100, 60, unit: "%"));
            _bus.Add(new Parameter("mod.rel", 0, 500, 240, unit: "ms"));

            // Tempo
            _bus.Add(new Parameter("tempo", 40, 200, 107, unit: "bpm"));

            // Drum mixer
            _bus.Add(new Parameter("drum.vol01", 0f, 1f, 0.8f));
            _bus.Add(new Parameter("drum.vol02", 0f, 1f, 0.8f));
            _bus.Add(new Parameter("drum.vol03", 0f, 1f, 0.8f));
            _bus.Add(new Parameter("drum.vol04", 0f, 1f, 0.8f));
            _bus.Add(new Parameter("drum.vol05", 0f, 1f, 0.8f));
            _bus.Add(new Parameter("drum.vol06", 0f, 1f, 0.8f));
            _bus.Add(new Parameter("drum.vol07", 0f, 1f, 0.8f));
            _bus.Add(new Parameter("drum.vol08", 0f, 1f, 0.8f));
            // Sample / Chop mixer
            _bus.Add(new Parameter("sample.master", 0f, 1f, 1f));
        }
    }
}