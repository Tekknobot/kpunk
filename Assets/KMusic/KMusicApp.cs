// KMusicApp.cs

using System;
using System.Collections.Generic;
using AudioHelm;
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
        private VisualElement _safeRoot;
        private Rect _lastSafeArea;
        private int _lastScreenWidth = -1, _lastScreenHeight = -1;
        private PanelSettings _responsivePanel;


        // prevent stacking event handlers on enable/disable
        private Action<string, float> _onBusChanged;
        private readonly List<KeyValuePair<Button, Action>> _soundSwitches = new();
        private readonly List<HelmController> _soundHelms = new();
        private bool _warnedSoundRouting;

        [Header("Audio Helm SOUND Routing (optional, assign when multiple Helm instances exist)")]
        [SerializeField] private HelmController soundSynth1;
        [SerializeField] private HelmController soundSynth2;

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

            // Use a phone-sized UI coordinate system on portrait devices, and a
            // desktop-sized one in landscape. Clone PanelSettings so the editor asset
            // is never permanently modified by Device Simulator/Play Mode.
            if (_doc.panelSettings != null)
            {
                _responsivePanel = Instantiate(_doc.panelSettings);
                _responsivePanel.name = "ZPunk Runtime Responsive Panel";
                _doc.panelSettings = _responsivePanel;
                ConfigurePanelResolution();
            }

            if (mainUxml != null)
                _doc.visualTreeAsset = mainUxml;

            // If version changed, wipe old prefs so new defaults apply
            int savedVer = ProjectPrefs.GetInt(PrefKey_BusVer, -1);
            if (savedVer != PrefVer)
            {
                ProjectPrefs.DeleteKey("kmusic.bus");        // delete old unversioned key
                ProjectPrefs.DeleteKey(PrefKey_Bus);         // delete current version key (just in case)
                ProjectPrefs.SetInt(PrefKey_BusVer, PrefVer);
                ProjectPrefs.Save();
            }
            BuildParameters();

            // Ensure CHAIN controller exists (safe to add at runtime)
            if (GetComponent<global::KMusicChainUI>() == null)
                gameObject.AddComponent<global::KMusicChainUI>();

            // PAD / CHORD track prototype
            if (GetComponent<global::KMusic.KMusicChordTrackUI>() == null)
                gameObject.AddComponent<global::KMusic.KMusicChordTrackUI>();

            // Load saved mixer/fader values after defaults exist.
            KMusicSaveState.LoadBus(_bus, PrefKey_Bus, id =>
                id.StartsWith("fx.") || id.StartsWith("arp.") ||
                id == "mod.wave" || id == "filter.env" || id == "filter.blend" ||
                id.StartsWith("mix.") ||
                id.StartsWith("drum.") ||
                id.StartsWith("sampler.") ||
                id.StartsWith("mutes.") ||
                id == "tempo" ||
                id == "sample.master" ||
                id == "sample.pitch" ||
                id == "master.vol"
            );

            // Load the project *after* global preferences, so global effect
            // defaults can never replace the previous project's sound on launch.
            // AddComponent invokes the manager's OnEnable immediately.
            if (GetComponent<global::KMusicProjectManager>() == null)
                gameObject.AddComponent<global::KMusicProjectManager>();
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

            // Apply a genuine 4-sided device safe area, not a top-only estimate.
            // Keep background full-bleed while placing interactive UI inside
            // the notch/camera/rounded-corner/home-indicator boundaries.
            _safeRoot = root;
            EnsureRuntimeFont(root);
            ApplySafeArea(true);
            root.RegisterCallback<GeometryChangedEvent>(OnSafeRootGeometry);

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
            // Do not use the legacy global native FX sender here. It used to
            // reapply saved distortion/delay/reverb 300ms after launch, sometimes
            // to multiple Helm channels, overwriting freshly loaded presets.
            // The main binder and independent Pad/Bass runtimes own their audio.
            BindSoundSwitches(root);
        }

    private void OnSafeRootGeometry(GeometryChangedEvent evt)
    {
        ApplySafeArea(false);
        if (_safeRoot != null && _safeRoot.resolvedStyle.width > 0)
        {
            float available = _safeRoot.resolvedStyle.width;
            available -= _safeRoot.resolvedStyle.paddingLeft + _safeRoot.resolvedStyle.paddingRight;
            _safeRoot.EnableInClassList("km-sound-wide", available >= 620f);
        }
    }

    private void Update()
    {
        // Simulator/device safe areas can change on rotation and when the
        // device simulation changes without recreating the UIDocument.
        if (_safeRoot != null && (Screen.width != _lastScreenWidth ||
            Screen.height != _lastScreenHeight || Screen.safeArea != _lastSafeArea))
        {
            ConfigurePanelResolution();
            ApplySafeArea(true);
        }
    }

    private static void EnsureRuntimeFont(VisualElement root)
    {
        // IMPORTANT: Do NOT override UI Toolkit's inherited runtime font with
        // Resources.GetBuiltinResource<Font>("Arial.ttf") or OS fallback fonts.
        // On iOS/Device Simulator that override can produce blank glyph atlases:
        // buttons render their backgrounds, but all their text disappears.
        // Let PanelSettings' default runtime theme resolve the platform font.
        root.style.color = new Color(.93f, .96f, .98f);
        root.style.fontSize = 15;
    }

    private void ConfigurePanelResolution()
    {
        if (_responsivePanel == null) return;
        bool portrait = Screen.height > Screen.width;
        float shortEdge = Mathf.Min(Screen.width, Screen.height);
        float aspect = Mathf.Max(Screen.width, Screen.height) / Mathf.Max(1f, shortEdge);
        // Pixel density alone cannot distinguish a high-resolution phone from a
        // tablet. A tablet-like aspect ratio needs a wider logical canvas; using
        // a phone's 430-point canvas on an iPad destroys the usable viewport.
        bool tabletLayout = shortEdge >= 700f && aspect <= 1.67f;
        _responsivePanel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
        _responsivePanel.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
        _responsivePanel.match = 0f;
        _responsivePanel.referenceResolution = tabletLayout
            ? (portrait ? new Vector2Int(760, 1024) : new Vector2Int(980, 760))
            : (portrait ? new Vector2Int(430, 932)
                : (Application.isMobilePlatform ? new Vector2Int(900, 520) : new Vector2Int(1280, 800)));
    }

    private void ApplySafeArea(bool force)
    {
        if (_safeRoot == null || Screen.width < 1 || Screen.height < 1) return;
        Rect sa = Screen.safeArea;
        if (!force && sa == _lastSafeArea && _lastScreenWidth == Screen.width &&
            _lastScreenHeight == Screen.height) return;
        _lastSafeArea = sa;
        _lastScreenWidth = Screen.width;
        _lastScreenHeight = Screen.height;

        // Convert physical pixel insets to panel logical points. PanelSettings
        // ScaleWithScreenSize has already scaled the coordinates; never apply
        // a second portrait multiplier to individual widgets.
        float w = _safeRoot.resolvedStyle.width;
        float h = _safeRoot.resolvedStyle.height;
        if (float.IsNaN(w) || w <= 1) w = _doc.panelSettings != null ? _doc.panelSettings.referenceResolution.x : Screen.width;
        if (float.IsNaN(h) || h <= 1) h = _doc.panelSettings != null ? _doc.panelSettings.referenceResolution.y : Screen.height;
        float fx = w / Mathf.Max(1f, Screen.width);
        float fy = h / Mathf.Max(1f, Screen.height);
        float top = Mathf.Max(0, Screen.height - sa.yMax) * fy;
        float bottom = Mathf.Max(0, sa.yMin) * fy;
        float left = Mathf.Max(0, sa.xMin) * fx;
        float right = Mathf.Max(0, Screen.width - sa.xMax) * fx;
        // A little breathing room even on devices reporting no hardware inset.
        _safeRoot.style.paddingTop = top + 7f;
        _safeRoot.style.paddingBottom = bottom + 8f;
        _safeRoot.style.paddingLeft = left + 7f;
        _safeRoot.style.paddingRight = right + 7f;
        // Logical available width (not screen pixels) distinguishes phone and tablet.
        // A narrow landscape viewport still stacks modules safely.
        _safeRoot.EnableInClassList("km-sound-wide", w - left - right >= 620f);
    }

    private void OnDisable()
    {
        foreach (var subscription in _soundSwitches)
            subscription.Key.clicked -= subscription.Value;
        _soundSwitches.Clear();
        if (_bus != null) _bus.OnChanged -= OnSoundParameterChanged;
        if (_safeRoot != null) _safeRoot.UnregisterCallback<GeometryChangedEvent>(OnSafeRootGeometry);
        _safeRoot = null;
        // Save bus values (mixer/drum/sampler/mutes only)
        KMusicSaveState.SaveBus(_bus, PrefKey_Bus, id =>
            id.StartsWith("fx.") || id.StartsWith("arp.") ||
            id == "mod.wave" || id == "filter.env" || id == "filter.blend" ||
            id.StartsWith("mix.") ||
            id.StartsWith("drum.") ||
            id.StartsWith("sampler.") ||
            id.StartsWith("mutes.") ||
            id == "tempo" ||
            id == "sample.master" ||
            id == "sample.pitch" ||
            id == "master.vol"
        );

        // Unhook event handler (prevents duplicate notifications later)
        if (_bus != null && _onBusChanged != null)
            _bus.OnChanged -= _onBusChanged;

        _onBusChanged = null;
    }

    private void OnDestroy()
    {
        if (_responsivePanel != null)
        {
            if (Application.isPlaying) Destroy(_responsivePanel);
            else DestroyImmediate(_responsivePanel);
            _responsivePanel = null;
        }
    }

    private void OnApplicationPause(bool pause)
    {
        if (!pause) return;

        KMusicSaveState.SaveBus(_bus, PrefKey_Bus, id =>
            id.StartsWith("fx.") || id.StartsWith("arp.") ||
            id == "mod.wave" || id == "filter.env" || id == "filter.blend" ||
            id.StartsWith("mix.") ||
            id.StartsWith("drum.") ||
            id.StartsWith("sampler.") ||
            id.StartsWith("mutes.") ||
            id == "tempo" ||
            id == "sample.master" ||
            id == "sample.pitch" ||
            id == "master.vol"
        );
    }

    private void OnApplicationQuit()
    {
        KMusicSaveState.SaveBus(_bus, PrefKey_Bus, id =>
            id.StartsWith("fx.") || id.StartsWith("arp.") ||
            id == "mod.wave" || id == "filter.env" || id == "filter.blend" ||
            id.StartsWith("mix.") ||
            id.StartsWith("drum.") ||
            id.StartsWith("sampler.") ||
            id.StartsWith("mutes.") ||
            id == "tempo" ||
            id == "sample.master" ||
            id == "sample.pitch" ||
            id == "master.vol"
        );
    }

        // The FX / arp controls use Audio Helm's native Param enum rather than
        // relying on similarly named fields that might not be wired to audio.
        // Only route explicitly assigned controllers, controllers under this app,
        // or a single unambiguous controller in the scene. Never change a random
        // drum or unrelated Helm channel in a multi-controller project.
        private void ResolveSoundHelms()
        {
            _soundHelms.Clear();
            AddSoundHelm(soundSynth1);
            AddSoundHelm(soundSynth2);
            if (_soundHelms.Count > 0) return;
            foreach (var helm in GetComponentsInChildren<HelmController>(true))
                AddSoundHelm(helm);
            if (_soundHelms.Count > 0) return;
            var found = FindObjectsByType<HelmController>(FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            if (found.Length == 1) AddSoundHelm(found[0]);
        }

        private void AddSoundHelm(HelmController helm)
        {
            if (helm != null && !_soundHelms.Contains(helm)) _soundHelms.Add(helm);
        }

        private void RestoreSoundState()
        {
            if (!isActiveAndEnabled || _bus == null) return;
            ResolveSoundHelms();
            if (_soundHelms.Count == 0) return;
            if (_bus.GetValue("fx.dist.on") > .5f) PushSoundGroup("fx.dist.");
            if (_bus.GetValue("fx.delay.on") > .5f) PushSoundGroup("fx.delay.");
            if (_bus.GetValue("fx.rev.on") > .5f) PushSoundGroup("fx.rev.");
            if (_bus.GetValue("arp.on") > .5f) PushSoundGroup("arp.");
            PushSoundParameter("fx.dist.on");
            PushSoundParameter("fx.delay.on");
            PushSoundParameter("fx.rev.on");
            PushSoundParameter("arp.on");
        }

        private void BindSoundSwitches(VisualElement root)
        {
            BindSoundSwitch(root, "DistToggleButton", "fx.dist.on");
            BindSoundSwitch(root, "DistToggleButton2", "fx.dist.on");
            BindSoundSwitch(root, "DelayToggleButton", "fx.delay.on");
            BindSoundSwitch(root, "DelayToggleButton2", "fx.delay.on");
            BindSoundSwitch(root, "DelaySyncToggleButton", "fx.delay.sync");
            BindSoundSwitch(root, "DelaySyncToggleButton2", "fx.delay.sync");
            BindSoundSwitch(root, "ReverbToggleButton", "fx.rev.on");
            BindSoundSwitch(root, "ReverbToggleButton2", "fx.rev.on");
            BindSoundSwitch(root, "ArpToggleButton", "arp.on");
            BindSoundSwitch(root, "ArpToggleButton2", "arp.on");
            BindSoundSwitch(root, "ArpSyncToggleButton", "arp.sync");
            BindSoundSwitch(root, "ArpSyncToggleButton2", "arp.sync");
        }

        private void BindSoundSwitch(VisualElement root, string name, string id)
        {
            var button = root.Q<Button>(name);
            if (button == null || !_bus.TryGet(id, out _)) return;
            Action click = () => _bus.SetValue(id, _bus.GetValue(id) > .5f ? 0f : 1f);
            button.clicked += click;
            _soundSwitches.Add(new KeyValuePair<Button, Action>(button, click));
            RefreshSoundSwitch(button, id);
        }

        private void RefreshSoundSwitch(Button button, string id)
        {
            bool enabled = _bus.GetValue(id) > .5f;
            bool sync = id == "arp.sync" || id == "fx.delay.sync";
            button.text = sync ? (enabled ? "SYNC" : "FREE") : (enabled ? "ON" : "OFF");
            button.EnableInClassList("is-on", enabled);
        }

        private void OnSoundParameterChanged(string id, float value)
        {
            if (id.StartsWith("fx.", StringComparison.Ordinal) || id.StartsWith("arp.", StringComparison.Ordinal))
            {
                foreach (var binding in _soundSwitches)
                {
                    if (binding.Key.name.StartsWith("DistToggle") && id == "fx.dist.on") RefreshSoundSwitch(binding.Key, id);
                    if (binding.Key.name.StartsWith("DelayToggle") && id == "fx.delay.on") RefreshSoundSwitch(binding.Key, id);
                    if (binding.Key.name.StartsWith("ReverbToggle") && id == "fx.rev.on") RefreshSoundSwitch(binding.Key, id);
                    if (binding.Key.name.StartsWith("ArpToggle") && id == "arp.on") RefreshSoundSwitch(binding.Key, id);
                    if (binding.Key.name.StartsWith("ArpSyncToggle") && id == "arp.sync") RefreshSoundSwitch(binding.Key, id);
                    if (binding.Key.name.StartsWith("DelaySyncToggle") && id == "fx.delay.sync") RefreshSoundSwitch(binding.Key, id);
                }
            }
            if (id == "fx.dist.on" && value > .5f) PushSoundGroup("fx.dist.");
            if (id == "fx.delay.on" && value > .5f) PushSoundGroup("fx.delay.");
            if (id == "fx.rev.on" && value > .5f) PushSoundGroup("fx.rev.");
            if (id == "arp.on" && value > .5f) PushSoundGroup("arp.");
            if (id == "arp.sync") PushSoundParameter("arp.rate");
            if (id == "fx.delay.sync") PushSoundParameter("fx.delay.time");
            PushSoundParameter(id);
        }

        private void PushSoundGroup(string prefix)
        {
            foreach (var param in _bus.All)
                if (param.Id.StartsWith(prefix, StringComparison.Ordinal) &&
                    param.Id != prefix + "on") PushSoundParameter(param.Id);
        }

        private void PushSoundParameter(string id)
        {
            Param helmParam;
            switch (id)
            {
                case "fx.dist.on": helmParam = Param.kDistortionOn; break;
                case "fx.dist.type": helmParam = Param.kDistortionType; break;
                case "fx.dist.drive": helmParam = Param.kDistortionDrive; break;
                case "fx.dist.mix": helmParam = Param.kDistortionMix; break;
                case "fx.delay.on": helmParam = Param.kDelayOn; break;
                case "fx.delay.mix": helmParam = Param.kDelayDryWet; break;
                case "fx.delay.fb": helmParam = Param.kDelayFeedback; break;
                case "fx.delay.time": helmParam = _bus.GetValue("fx.delay.sync") > .5f
                    ? Param.kDelayTempo : Param.kDelayFrequency; break;
                case "fx.delay.sync": helmParam = Param.kDelaySync; break;
                case "fx.rev.on": helmParam = Param.kReverbOn; break;
                case "fx.rev.mix": helmParam = Param.kReverbDryWet; break;
                case "fx.rev.fb": helmParam = Param.kReverbFeedback; break;
                case "fx.rev.damp": helmParam = Param.kReverbDamping; break;
                case "arp.on": helmParam = Param.kArpOn; break;
                case "arp.sync": helmParam = Param.kArpSync; break;
                case "arp.pattern": helmParam = Param.kArpPattern; break;
                case "arp.gate": helmParam = Param.kArpGate; break;
                case "arp.octaves": helmParam = Param.kArpOctaves; break;
                case "arp.rate": helmParam = _bus.GetValue("arp.sync") > .5f
                    ? Param.kArpTempo : Param.kArpFrequency; break;
                case "filter.env": helmParam = Param.kFilterEnvelopeDepth; break;
                case "filter.blend": helmParam = Param.kFilterBlend; break;
                case "mod.wave": helmParam = Param.kMonoLfo1Waveform; break;
                default: return;
            }
            if (_soundHelms.Count == 0) ResolveSoundHelms();
            if (_soundHelms.Count == 0)
            {
                if (!_warnedSoundRouting)
                {
                    Debug.LogWarning("ZPUNK SOUND: Assign HelmController references on KMusicApp when multiple Audio Helm instances are in the scene.");
                    _warnedSoundRouting = true;
                }
                return;
            }
            float t = _bus.GetNormalized(id);
            // Helm's SetParameterPercent applies each parameter's actual native
            // range and scaling; the UI operates in normalized 0..1 space.
            foreach (var helm in _soundHelms)
            {
                if (helm == null) continue;
                try { helm.SetParameterPercent(helmParam, t); }
                catch (Exception ex) when (ex is EntryPointNotFoundException || ex is DllNotFoundException)
                {
                    if (!_warnedSoundRouting)
                    {
                        Debug.LogWarning("ZPUNK SOUND: Audio Helm native plugin is unavailable on this platform.");
                        _warnedSoundRouting = true;
                    }
                }
            }
        }

        private void BuildParameters()
        {
            _bus = new ParameterBus();

            // -----------------
            // SYNTH / MASTER
            // -----------------
            _bus.Add(new Parameter("master.vol",   0f, 1f, 0.80f));
            _bus.Add(new Parameter("master.porta", 0f, 100f, 0f, unit: "%"));
            _bus.Add(new Parameter("master.poly",  1f, 16f, 8f));

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

            _bus.Add(new Parameter("osc1.wave", 0f, 100f, 0f, unit: "%"));
            _bus.Add(new Parameter("osc2.wave", 0f, 100f, 0f, unit: "%"));

            // SUB / NOISE / MISC
            _bus.Add(new Parameter("sub.vol",       0f, 100f, 0f, unit: "%"));
            _bus.Add(new Parameter("sub.oct",       0f, 100f, 0f, unit: "%"));
            _bus.Add(new Parameter("noise.vol",     0f, 100f, 0f, unit: "%"));
            _bus.Add(new Parameter("synth.xmod",    0f, 100f, 0f, unit: "%"));
            _bus.Add(new Parameter("filter.keytrk", 0f, 100f, 0f, unit: "%"));
            _bus.Add(new Parameter("synth.vel",     0f, 100f, 0f, unit: "%"));

            // FX
            _bus.Add(new Parameter("fx.delay.on",   0f, 1f, 0f));
            _bus.Add(new Parameter("fx.delay.sync", 0f, 3f, 1f));
            _bus.Add(new Parameter("fx.delay.mix",  0f, 100f, 24f, unit: "%"));
            _bus.Add(new Parameter("fx.delay.fb",   0f, 100f, 35f, unit: "%"));
            _bus.Add(new Parameter("fx.delay.time", 0f, 100f, 42f, unit: "%"));

            _bus.Add(new Parameter("fx.rev.on",   0f, 1f, 0f));
            _bus.Add(new Parameter("fx.rev.mix",  0f, 100f, 25f, unit: "%"));
            _bus.Add(new Parameter("fx.rev.fb",   0f, 100f, 55f, unit: "%"));
            _bus.Add(new Parameter("fx.rev.damp", 0f, 100f, 45f, unit: "%"));

            // DISTORTION
            _bus.Add(new Parameter("fx.dist.on",    0f, 1f, 0f));
            _bus.Add(new Parameter("fx.dist.type",  0f, 3f, 0f));
            _bus.Add(new Parameter("fx.dist.drive", 0f, 100f, 45f, unit: "%"));
            _bus.Add(new Parameter("fx.dist.mix",   0f, 100f, 38f, unit: "%"));            

            // Audio Helm arpeggiator (the synth engine, not the DAW clip sequencer).
            _bus.Add(new Parameter("arp.on", 0f, 1f, 0f));
            _bus.Add(new Parameter("arp.sync", 0f, 3f, 1f));
            _bus.Add(new Parameter("arp.pattern", 0f, 4f, 0f));
            _bus.Add(new Parameter("arp.gate", 0f, 100f, 65f, unit: "%"));
            _bus.Add(new Parameter("arp.octaves", 1f, 4f, 1f));
            _bus.Add(new Parameter("arp.rate", 0f, 100f, 50f, unit: "%"));

            // -----------------
            // FILTER
            // -----------------
            _bus.Add(new Parameter("filter.cutoff", 20, 20000, 3200, log: true, unit: "hz"));
            _bus.Add(new Parameter("filter.res", 0, 100, 30, unit: "%"));
            _bus.Add(new Parameter("filter.drive", 0, 100, 20, unit: "%"));
            _bus.Add(new Parameter("filter.env", 0, 100, 50, unit: "%"));
            _bus.Add(new Parameter("filter.blend", 0, 100, 50, unit: "%"));
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
            _bus.Add(new Parameter("mod.wave", 0, 100, 0, unit: "%"));
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
            _bus.Add(new Parameter("sample.pitch", -12f, 12f, 0f, unit: "st"));
        }
    }
}