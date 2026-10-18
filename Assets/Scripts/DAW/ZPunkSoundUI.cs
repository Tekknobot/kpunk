using System;
using System.Collections.Generic;
using AudioHelm;
using KMusic;
using KMusic.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace ZPunk.DAW
{
    /// <summary>
    /// SOUND workspace for the three independent Helm instruments. Controls are
    /// generated here at runtime (the legacy Main.uxml SOUND panel is not used).
    /// The native controller is the source of truth for presets, live knob edits,
    /// and bypass toggles; no stale knob cache may reapply an older patch.
    /// </summary>
    public sealed class ZPunkSoundUI : MonoBehaviour
    {
        private static readonly Color Bg = new Color(.073f, .101f, .146f);
        private static readonly Color Tile = new Color(.105f, .145f, .207f);
        private static readonly Color Face = new Color(.065f, .092f, .133f);
        private static readonly Color Ink = new Color(.91f, .94f, .98f);
        private static readonly Color Dim = new Color(.57f, .66f, .76f);
        private static readonly Color Aqua = new Color(.30f, .87f, .75f);
        private static readonly Color Gold = new Color(.99f, .72f, .35f);
        private static readonly Color Blue = new Color(.43f, .68f, .99f);
        private static readonly Color Pink = new Color(.97f, .48f, .61f);
        private static readonly string[] SyncModes = { "FREE", "SYNC", "DOTTED", "TRIPLET" };

        private VisualElement root, moduleLayout, trackBar;
        private ScrollView pageScroll;
        private Label subhead, presetName, instrumentStatus;
        private readonly List<Macro> macros = new List<Macro>();
        private readonly List<Switch> switches = new List<Switch>();
        private readonly List<VisualElement> moduleCells = new List<VisualElement>();
        private readonly Dictionary<TrackKind, Button> selectors = new Dictionary<TrackKind, Button>();
        private readonly Dictionary<VisualElement, Macro> knobTargets = new Dictionary<VisualElement, Macro>();
        private KMusicApp app;
        private KMusicPadSynthRuntime padRuntime;
        private KMusicHelmPresetPickerUI mainPicker;
        private ZPunkInstrumentRack rack;
        private TrackKind selected = TrackKind.Synth;
        private float nextRefresh;
        private float lastModuleWidth = -1f;
        // Short-lived UI acknowledgement, never a replay queue. Native Helm is
        // still authoritative after this grace interval; a preset change clears it.
        private sealed class PendingDistortion
        {
            public float normalized;
            public float until;
        }
        private readonly Dictionary<Param, PendingDistortion> pendingDistortion =
            new Dictionary<Param, PendingDistortion>();
        private HelmController pendingController;
        private string pendingPreset;
        private Macro dragging;
        private int dragPointer = -1;
        private float dragY, dragStart, scrollLockUntil;
        private Vector2 lockedScroll;
        private sealed class Macro
        {
            public string id, label;
            public Param param;
            public VisualElement marker;
            public Label value;
            public float normalized;
            public int lastDisplay = -999;
            public int steps; // 0 is continuous, other values are exact native detents.
            public string[] choices;
        }

        private sealed class Switch
        {
            public string id;
            public Param param;
            public Button button;
            public bool sync;
            public int lastState = -1;
        }

        private static Macro K(string id, string label, Param param)
        {
            return new Macro { id = id, label = label, param = param };
        }
        private static Macro Choices(string id, string label, Param param, params string[] choices)
        {
            return new Macro { id = id, label = label, param = param,
                steps = choices.Length, choices = choices };
        }
        private static Label T(string text, int size, Color color)
        {
            var l = new Label(text);
            l.style.fontSize = Mathf.Max(10, size);
            l.style.color = color;
            l.style.unityTextAlign = TextAnchor.MiddleLeft;
            l.pickingMode = PickingMode.Ignore;
            return l;
        }
        private static void Corners(VisualElement e, float radius)
        {
            e.style.borderTopLeftRadius = radius;
            e.style.borderTopRightRadius = radius;
            e.style.borderBottomLeftRadius = radius;
            e.style.borderBottomRightRadius = radius;
        }
        private static Button Action(string text, Action click)
        {
            var b = new Button(click) { text = text };
            b.style.minHeight = 42;
            b.style.minWidth = 42;
            b.style.flexShrink = 0;
            b.style.fontSize = 13;
            b.style.color = Ink;
            b.style.backgroundColor = Tile;
            Corners(b, 6);
            return b;
        }

        public void Initialize(VisualElement host, VisualElement ignoredLegacyTree)
        {
            root = host;
            if (root == null) return;
            root.UnregisterCallback<PointerDownEvent>(OnKnobDown, TrickleDown.TrickleDown);
            root.UnregisterCallback<PointerMoveEvent>(OnKnobMove, TrickleDown.TrickleDown);
            root.UnregisterCallback<PointerUpEvent>(OnKnobUp, TrickleDown.TrickleDown);
            root.UnregisterCallback<PointerCancelEvent>(OnKnobCancel, TrickleDown.TrickleDown);
            root.UnregisterCallback<PointerCaptureOutEvent>(OnKnobCaptureLost);
            root.Clear();
            macros.Clear(); switches.Clear(); moduleCells.Clear();
            selectors.Clear(); knobTargets.Clear();
            lastModuleWidth = -1f;
            dragging = null; dragPointer = -1;
            root.RegisterCallback<PointerDownEvent>(OnKnobDown, TrickleDown.TrickleDown);
            root.RegisterCallback<PointerMoveEvent>(OnKnobMove, TrickleDown.TrickleDown);
            root.RegisterCallback<PointerUpEvent>(OnKnobUp, TrickleDown.TrickleDown);
            root.RegisterCallback<PointerCancelEvent>(OnKnobCancel, TrickleDown.TrickleDown);
            root.RegisterCallback<PointerCaptureOutEvent>(OnKnobCaptureLost);
            root.style.flexDirection = FlexDirection.Column;
            root.style.flexGrow = 1;
            root.style.minHeight = 0;
            root.style.paddingLeft = 10;
            root.style.paddingRight = 10;
            root.style.paddingTop = 9;
            root.style.backgroundColor = Bg;

            var title = T("SOUND  /  INSTRUMENT", 16, Ink);
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.marginBottom = 5;
            root.Add(title);
            subhead = T("SELECT AN INSTRUMENT", 10, Dim);
            subhead.style.marginBottom = 8;
            root.Add(subhead);

            trackBar = new VisualElement();
            trackBar.style.flexDirection = FlexDirection.Row;
            trackBar.style.height = 51;
            trackBar.style.flexShrink = 0;
            trackBar.style.marginBottom = 8;
            foreach (var kind in new[] { TrackKind.Bass, TrackKind.Synth, TrackKind.Pads })
            {
                TrackKind chosen = kind;
                var button = Action(kind.ToString().ToUpperInvariant(), () => Select(chosen));
                button.style.flexGrow = 1;
                button.style.flexShrink = 1;
                button.style.minWidth = 0;
                button.style.marginRight = 5;
                selectors[kind] = button;
                trackBar.Add(button);
            }
            root.Add(trackBar);

            var presetCard = new VisualElement();
            presetCard.style.flexDirection = FlexDirection.Row;
            presetCard.style.alignItems = Align.Center;
            presetCard.style.height = 60;
            presetCard.style.flexShrink = 0;
            presetCard.style.paddingLeft = 8;
            presetCard.style.paddingRight = 8;
            presetCard.style.backgroundColor = Tile;
            Corners(presetCard, 6);
            presetCard.style.marginBottom = 7;
            var prev = Action("<", () => ChangePreset(-1));
            prev.style.width = 44;
            prev.style.marginRight = 5;
            presetCard.Add(prev);
            presetName = T("LOADING PRESET", 12, Ink);
            presetName.style.flexGrow = 1;
            presetName.style.minWidth = 0;
            presetName.style.unityTextAlign = TextAnchor.MiddleCenter;
            presetName.style.whiteSpace = WhiteSpace.Normal;
            presetCard.Add(presetName);
            var next = Action(">", () => ChangePreset(1));
            next.style.width = 44;
            next.style.marginLeft = 5;
            presetCard.Add(next);
            root.Add(presetCard);

            instrumentStatus = T("", 10, Dim);
            instrumentStatus.style.marginBottom = 6;
            root.Add(instrumentStatus);
            pageScroll = new ScrollView(ScrollViewMode.Vertical);
            pageScroll.style.flexGrow = 1;
            pageScroll.style.minHeight = 0;
            pageScroll.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            pageScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            root.Add(pageScroll);
            moduleLayout = new VisualElement();
            moduleLayout.style.flexDirection = FlexDirection.Row;
            moduleLayout.style.flexWrap = Wrap.Wrap;
            moduleLayout.style.width = new Length(100, LengthUnit.Percent);
            pageScroll.Add(moduleLayout);
            moduleLayout.RegisterCallback<GeometryChangedEvent>(OnLayoutChanged);

            BuildModule("OSCILLATORS", Pink, null, null, new[] {
                K("osc1.vol", "OSC 1 LEVEL", Param.kOsc1Volume),
                K("osc2.vol", "OSC 2 LEVEL", Param.kOsc2Volume),
                K("sub.vol", "SUB LEVEL", Param.kSubVolume),
                K("noise.vol", "NOISE", Param.kNoiseVolume),
                K("osc1.detune", "OSC 1 DETUNE", Param.kOsc1UnisonDetune),
                K("osc2.detune", "OSC 2 DETUNE", Param.kOsc2UnisonDetune)
            });
            BuildModule("FILTER", Gold, null, null, new[] {
                K("filter.cutoff", "CUTOFF", Param.kFilterCutoff),
                K("filter.res", "RESONANCE", Param.kResonance),
                K("filter.drive", "FILTER DRIVE", Param.kFilterDrive),
                K("filter.env", "ENV DEPTH", Param.kFilterEnvelopeDepth),
                K("filter.atk", "ENV ATTACK", Param.kFilterAttack),
                K("filter.rel", "ENV RELEASE", Param.kFilterRelease)
            });
            BuildModule("AMP / MASTER", Blue, null, null, new[] {
                K("amp.atk", "ATTACK", Param.kAmplitudeAttack),
                K("amp.dec", "DECAY", Param.kAmplitudeDecay),
                K("amp.sus", "SUSTAIN", Param.kAmplitudeSustain),
                K("amp.rel2", "RELEASE", Param.kAmplitudeRelease),
                K("master.porta", "PORTAMENTO", Param.kPortamento),
                K("master.vol", "LEVEL", Param.kVolume)
            });
            BuildModule("MODULATION", Aqua, null, null, new[] {
                K("mod.rate", "LFO 1 RATE", Param.kMonoLfo1Frequency),
                K("mod.depth", "LFO 1 DEPTH", Param.kMonoLfo1Amplitude),
                Choices("mod.wave", "LFO 1 WAVE", Param.kMonoLfo1Waveform,
                    "SIN", "TRI", "SQR", "SAW +", "SAW -", "3 STEP", "4 STEP",
                    "8 STEP", "3 PYR", "5 PYR", "9 PYR", "RAND 1", "RAND 2")
            });
            BuildModule("ARPEGGIATOR", Aqua, "arp.on", "arp.sync", new[] {
                Choices("arp.pattern", "PATTERN", Param.kArpPattern,
                    "UP", "DOWN", "UP / DOWN", "AS PLAYED", "RANDOM"),
                K("arp.rate", "RATE", Param.kArpTempo),
                K("arp.gate", "GATE", Param.kArpGate),
                Choices("arp.octaves", "OCTAVES", Param.kArpOctaves,
                    "1 OCT", "2 OCT", "3 OCT", "4 OCT")
            });
            BuildModule("DISTORTION", Pink, "fx.dist.on", null, new[] {
                Choices("fx.dist.type", "TYPE", Param.kDistortionType,
                    "SOFT CLIP", "HARD CLIP", "LINEAR FOLD", "SINE FOLD"),
                K("fx.dist.drive", "DRIVE", Param.kDistortionDrive),
                K("fx.dist.mix", "DRY / WET", Param.kDistortionMix)
            });
            BuildModule("DELAY", Gold, "fx.delay.on", "fx.delay.sync", new[] {
                K("fx.delay.time", "TIME / RATE", Param.kDelayTempo),
                K("fx.delay.fb", "FEEDBACK", Param.kDelayFeedback),
                K("fx.delay.mix", "DRY / WET", Param.kDelayDryWet)
            });
            BuildModule("REVERB", Blue, "fx.rev.on", null, new[] {
                K("fx.rev.fb", "DECAY / FB", Param.kReverbFeedback),
                K("fx.rev.damp", "DAMPING", Param.kReverbDamping),
                K("fx.rev.mix", "DRY / WET", Param.kReverbDryWet)
            });
            var note = T("Drag a knob vertically to adjust. ON / OFF switches apply to effects and the arpeggiator.", 10, Dim);
            note.style.whiteSpace = WhiteSpace.Normal;
            note.style.marginBottom = 16;
            note.style.marginLeft = 6;
            moduleLayout.Add(note);
            OnShown();
        }

        private void OnLayoutChanged(GeometryChangedEvent evt)
        {
            // Each card holds three knobs per row. Two cards fit side by side on
            // tablets / wider landscape views; phones show one full-width card.
            float width = evt.newRect.width;
            if (width <= 0 || float.IsNaN(width) || Mathf.Abs(width - lastModuleWidth) < 1f) return;
            lastModuleWidth = width;
            float cellPercent = width >= 710f ? 50f : 100f;
            foreach (var cell in moduleCells)
                cell.style.width = new Length(cellPercent, LengthUnit.Percent);
        }
        private void FindServices()
        {
            if (app == null) app = FindObjectOfType<KMusicApp>();
            if (padRuntime == null) padRuntime = FindObjectOfType<KMusicPadSynthRuntime>();
            if (mainPicker == null) mainPicker = FindObjectOfType<KMusicHelmPresetPickerUI>();
            if (rack == null) rack = FindObjectOfType<ZPunkInstrumentRack>();
        }

        private static bool IsDistortion(Param param)
        {
            return param == Param.kDistortionOn || param == Param.kDistortionType ||
                param == Param.kDistortionDrive || param == Param.kDistortionMix;
        }
        private static bool IsNativeOnSwitch(Param param)
        {
            // Helm's switches are binary native values; its normalized getter
            // isn't a reliable ON/OFF source on every plugin build.
            return param == Param.kDistortionOn || param == Param.kFilterOn ||
                   param == Param.kDelayOn || param == Param.kReverbOn ||
                   param == Param.kArpOn;
        }

        // The distortion values are normalized by Helm's own native API.
        // One SOUND gesture must cause one native write, not a bus write plus
        // a second direct write (and no delayed read-back/retry).
        private HelmController Controller()
        {
            FindServices();
            if (rack != null) return rack.Controller(selected);
            return selected == TrackKind.Pads ? padRuntime?.ActiveHelmController : null;
        }
        private ParameterBus CurrentBus()
        {
            FindServices();
            return selected == TrackKind.Synth ? app?.Bus :
                selected == TrackKind.Pads ? padRuntime?.SoundBus : null;
        }
        private void Select(TrackKind track)
        {
            if (dragging != null && root != null && root.HasPointerCapture(dragPointer))
                root.ReleasePointer(dragPointer);
            dragging = null; dragPointer = -1;
            selected = track;
            ClearPendingDistortion();
            OnShown();
        }
        public void OnShown(int requestedTrack = -1)
        {
            if (requestedTrack >= 0 && requestedTrack <= 4)
            {
                var kind = (TrackKind)requestedTrack;
                if (kind == TrackKind.Bass || kind == TrackKind.Synth || kind == TrackKind.Pads)
                    selected = kind;
            }
            if (root == null) return;
            FindServices();
            ClearPendingDistortion();
            foreach (var entry in selectors)
            {
                bool active = entry.Key == selected;
                entry.Value.style.backgroundColor = active ? Aqua : Tile;
                entry.Value.style.color = active ? Face : Ink;
            }
            if (subhead != null)
                subhead.text = selected.ToString().ToUpperInvariant() + "  /  AUDIO HELM";
            foreach (var m in macros) m.lastDisplay = -999;
            foreach (var sw in switches) sw.lastState = -1;
            Refresh();
        }
        private static string DisplayPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "INIT PRESET";
            string clean = path.Replace('\\', '/');
            int slash = clean.LastIndexOf('/');
            clean = clean.Substring(slash + 1);
            return clean.EndsWith(".helm", StringComparison.OrdinalIgnoreCase)
                ? clean.Substring(0, clean.Length - 5) : clean;
        }
        private void ChangePreset(int direction)
        {
            FindServices();
            if (selected == TrackKind.Bass) rack?.StepBassPreset(direction);
            else if (selected == TrackKind.Synth && mainPicker != null)
                mainPicker.ApplyPresetChoice(mainPicker.CurrentPresetIndex + direction, null);
            else if (selected == TrackKind.Pads && padRuntime != null)
                padRuntime.StepPreset(direction);
            Refresh();
        }

        private Param ActualParam(string id, Param fallback)
        {
            if (id != "arp.rate" && id != "fx.delay.time") return fallback;
            bool sync = GetSyncMode(id == "arp.rate" ? "arp.sync" : "fx.delay.sync") != 0;
            if (id == "arp.rate") return sync ? Param.kArpTempo : Param.kArpFrequency;
            return sync ? Param.kDelayTempo : Param.kDelayFrequency;
        }
        private static Param SwitchParam(string id)
        {
            switch (id)
            {
                case "filter.on": return Param.kFilterOn;
                case "fx.dist.on": return Param.kDistortionOn;
                case "fx.delay.on": return Param.kDelayOn;
                case "fx.delay.sync": return Param.kDelaySync;
                case "fx.rev.on": return Param.kReverbOn;
                case "arp.on": return Param.kArpOn;
                case "arp.sync": return Param.kArpSync;
                default: return Param.kNone;
            }
        }
        private void ClearPendingDistortion()
        {
            pendingDistortion.Clear();
            pendingController = null;
            pendingPreset = null;
        }
        private bool ReadPercent(HelmController controller, Param param, out float amount)
        {
            amount = 0f;
            if (controller == null) return false;
            try
            {
                // Read binary switches in native units; the percent getter
                // can return stale/rounded values that cause toggle snapback.
                amount = IsNativeOnSwitch(param)
                    ? (controller.GetParameterValue(param) >= .5f ? 1f : 0f)
                    : Mathf.Clamp01(controller.GetParameterPercent(param));
                if (pendingController == controller && IsDistortion(param) &&
                    pendingDistortion.TryGetValue(param, out PendingDistortion pending))
                {
                    if (Mathf.Abs(amount - pending.normalized) < .008f)
                        pendingDistortion.Remove(param);
                    else if (Time.unscaledTime < pending.until)
                        amount = pending.normalized; // Ignore transient stale read-back.
                    else
                        pendingDistortion.Remove(param); // Reveal a real native mismatch.
                }
                return true;
            }
            catch (Exception) { return false; }
        }
        private int GetSyncMode(string id)
        {
            Param param = SwitchParam(id);
            if (param != Param.kNone && ReadPercent(Controller(), param, out float current))
                return Mathf.Clamp(Mathf.RoundToInt(current * 3f), 0, 3);
            var bus = CurrentBus();
            if (bus != null && bus.TryGet(id, out Parameter value) && value != null)
                return Mathf.Clamp(Mathf.RoundToInt(value.Value), 0, 3);
            return 0;
        }
        private bool IsOn(string id)
        {
            if (id == "arp.sync" || id == "fx.delay.sync") return GetSyncMode(id) != 0;
            var ctl = Controller();
            var param = SwitchParam(id);
            if (param != Param.kNone && ReadPercent(ctl, param, out float current))
                return current >= .5f;
            var bus = CurrentBus();
            return bus != null && bus.TryGet(id, out Parameter v) && v != null && v.Normalized >= .5f;
        }
        private void Refresh()
        {
            var controller = Controller();
            string status = controller == null ? "INSTRUMENT INITIALIZING" :
                "CHANNEL " + controller.channel + "  /  AUDIO HELM";
            if (instrumentStatus != null && instrumentStatus.text != status)
                instrumentStatus.text = status;
            string preset = "NO PRESET";
            if (selected == TrackKind.Bass && rack != null) preset = rack.BassPresetName;
            else if (selected == TrackKind.Synth && mainPicker != null)
                preset = DisplayPath(mainPicker.CurrentPresetRelPath);
            else if (selected == TrackKind.Pads && padRuntime != null)
                preset = padRuntime.PresetCount > 0
                    ? DisplayPath(padRuntime.CurrentPresetRelPath) + "  (" + (padRuntime.CurrentPresetIndex + 1) + "/" + padRuntime.PresetCount + ")"
                    : "PAD PRESETS UNAVAILABLE";
            if (pendingController != controller || pendingPreset != preset)
            {
                pendingDistortion.Clear();
                pendingController = controller;
                pendingPreset = preset;
            }
            if (presetName != null && presetName.text != preset) presetName.text = preset;

            var bus = CurrentBus();
            foreach (var sw in switches)
            {
                bool available = ReadPercent(controller, sw.param, out float n);
                if (!available && bus != null && bus.TryGet(sw.id, out Parameter setting))
                {
                    n = setting.Normalized;
                    available = true;
                }
                int state = available ? (sw.sync
                    ? Mathf.Clamp(Mathf.RoundToInt(n * 3f), 0, 3)
                    : (n >= .5f ? 1 : 0)) : -1;
                if (state != sw.lastState)
                {
                    sw.lastState = state;
                    sw.button.text = state < 0 ? "--" : sw.sync
                        ? SyncModes[state] : (state == 1 ? "ON" : "OFF");
                    bool highlighted = sw.sync ? state > 0 : state == 1;
                    sw.button.style.backgroundColor = highlighted ? Aqua : Face;
                    sw.button.style.color = highlighted ? Face : Ink;
                }
                if (sw.button.enabledSelf != (controller != null))
                    sw.button.SetEnabled(controller != null);
            }
            foreach (var m in macros)
            {
                if (dragging == m) continue;
                Param param = ActualParam(m.id, m.param);
                bool available = ReadPercent(controller, param, out float amount);
                if (!available && bus != null && bus.TryGet(m.id, out Parameter stored))
                {
                    amount = Mathf.Clamp01(stored.Normalized);
                    available = true;
                }
                int shown = available ? Mathf.RoundToInt(amount * 1000f) : -1;
                m.normalized = amount;
                if (m.lastDisplay == shown) continue;
                m.lastDisplay = shown;
                if (m.value != null) m.value.text = available ? DisplayValue(m, amount, controller, param) : "--";
                PositionMarker(m, amount);
            }
        }
        private static float Quantize(Macro m, float amount)
        {
            amount = Mathf.Clamp01(amount);
            return m.steps > 1
                ? Mathf.Round(amount * (m.steps - 1)) / (m.steps - 1)
                : amount;
        }
        private static string DisplayValue(Macro m, float amount, HelmController controller, Param actualParam)
        {
            if (m.choices != null && m.choices.Length > 1)
            {
                int index = Mathf.Clamp(Mathf.RoundToInt(amount * (m.choices.Length - 1)), 0, m.choices.Length - 1);
                return m.choices[index];
            }
            if ((m.id == "arp.rate" || m.id == "fx.delay.time") && controller != null)
            {
                try
                {
                    float native = controller.GetParameterValue(actualParam);
                    if (actualParam == Param.kArpTempo || actualParam == Param.kDelayTempo)
                        return "DIV " + Mathf.RoundToInt(native);
                    return native.ToString("0.0") + " Hz";
                }
                catch (Exception) { }
            }
            if (m.id == "fx.dist.drive")
            {
                // Helm drive range is -30..+30 dB; display only.
                float db = Mathf.Lerp(-30f, 30f, amount);
                return (db > 0.05f ? "+" : "") + db.ToString("0.#") + " dB";
            }
            return Mathf.RoundToInt(amount * 100f) + "%";
        }
        // The preset is the initial state; knob edits go to the selected Helm
        // channel. Switching ON/OFF never changes TYPE, DRIVE or MIX.
        private void WriteNormalized(string id, Param param, float amount)
        {
            var controller = Controller();
            if (controller == null || param == Param.kNone) return;
            amount = Mathf.Clamp01(amount);

            // Mirror a SOUND distortion edit without firing the legacy bus
            // listeners: this selected controller owns the single native write.
            var bus = CurrentBus();
            if (bus != null && bus.TryGet(id, out Parameter existing) && existing != null)
            {
                if (IsDistortion(param) || IsNativeOnSwitch(param))
                    bus.SetNormalizedSilently(id, amount);
                else bus.SetNormalized(id, amount);
            }

            try
            {
                if (IsDistortion(param))
                {
                    // A single native write per edit. Binary ON/OFF and TYPE
                    // have discrete *native* values; DRIVE and MIX use Helm's
                    // normalized setter, like the working FILTER controls.
                    if (param == Param.kDistortionOn)
                        amount = amount >= .5f ? 1f : 0f;
                    else if (param == Param.kDistortionType)
                        amount = Mathf.RoundToInt(amount * 3f) / 3f;

                    if (param == Param.kDistortionOn)
                        controller.SetParameterValue(param, amount);
                    else if (param == Param.kDistortionType)
                        controller.SetParameterValue(param, Mathf.RoundToInt(amount * 3f));
                    else
                        controller.SetParameterPercent(param, amount);

                    if (pendingController != controller) pendingDistortion.Clear();
                    pendingController = controller;
                    pendingPreset = presetName != null ? presetName.text : null;
                    pendingDistortion[param] = new PendingDistortion
                    {
                        normalized = amount,
                        until = Time.unscaledTime + .45f
                    };
                }
                else if (IsNativeOnSwitch(param))
                    controller.SetParameterValue(param, amount >= 0.5f ? 1f : 0f);
                else if (id == "fx.delay.sync" || id == "arp.sync")
                    controller.SetParameterValue(param, Mathf.RoundToInt(amount * 3f));
                else
                    controller.SetParameterPercent(param, amount);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ZPUNK SOUND] Helm write failed for " + id +
                    " on channel " + controller.channel + ": " + ex.Message);
            }
        }

        private void SetMacro(Macro m, float amount)
        {
            amount = Quantize(m, amount);
            // Do not spam Helm with identical native values during a stationary
            // drag (particularly the discrete distortion TYPE selector).
            if (Mathf.Abs(amount - m.normalized) < .0005f) return;
            WriteNormalized(m.id, ActualParam(m.id, m.param), amount);
            m.normalized = amount;
            m.lastDisplay = Mathf.RoundToInt(amount * 1000f);
            if (m.value != null) m.value.text = DisplayValue(m, amount, Controller(), ActualParam(m.id, m.param));
            PositionMarker(m, amount);
        }
        private void Toggle(Switch sw)
        {
            var controller = Controller();
            if (controller == null) return;

            // Read the native ON value, not a possibly stale bus toggle state.
            // This must touch only the ON parameter: TYPE, DRIVE and MIX remain
            // exactly as in the loaded patch when bypassing or re-enabling.
            float next;
            if (sw.sync)
                next = ((GetSyncMode(sw.id) + 1) % 4) / 3f;
            else
            {
                bool current = IsOn(sw.id);
                // The native ON value is authoritative for this channel.
                next = current ? 0f : 1f;
            }
            WriteNormalized(sw.id, sw.param, next);

            // Paint the intended native switch value immediately. Subsequent
            // refreshes read back from Helm; no persistent override or retry.
            if (!sw.sync && sw.button != null)
            {
                int state = next >= .5f ? 1 : 0;
                sw.lastState = state;
                sw.button.text = state == 1 ? "ON" : "OFF";
                sw.button.style.backgroundColor = state == 1 ? Aqua : Face;
                sw.button.style.color = state == 1 ? Face : Ink;
            }
            else sw.lastState = -1;
            foreach (var m in macros)
                if (m.id == "arp.rate" || m.id == "fx.delay.time") m.lastDisplay = -999;
            Refresh();
        }

        private static void PositionMarker(Macro m, float amount)
        {
            if (m.marker == null) return;
            const float center = 32f, radius = 23f;
            float angle = (135f + Mathf.Clamp01(amount) * 270f) * Mathf.Deg2Rad;
            m.marker.style.left = center + Mathf.Cos(angle) * radius;
            m.marker.style.top = center + Mathf.Sin(angle) * radius;
        }
        private void LockScroll()
        {
            if (pageScroll != null) pageScroll.scrollOffset = lockedScroll;
        }
        private void EndDrag(int pointer)
        {
            if (dragPointer != pointer) return;
            dragging = null;
            dragPointer = -1;
            scrollLockUntil = Time.unscaledTime + .18f;
            LockScroll();
        }
        private Macro FindKnob(VisualElement target)
        {
            while (target != null && target != root)
            {
                if (knobTargets.TryGetValue(target, out Macro macro)) return macro;
                target = target.parent;
            }
            return null;
        }
        private void OnKnobDown(PointerDownEvent evt)
        {
            if (evt.button != 0 || dragging != null) return;
            var macro = FindKnob(evt.target as VisualElement);
            if (macro == null) return;
            dragging = macro;
            dragPointer = evt.pointerId;
            dragY = evt.position.y;
            dragStart = macro.normalized;
            lockedScroll = pageScroll != null ? pageScroll.scrollOffset : Vector2.zero;
            root.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        }
        private void OnKnobMove(PointerMoveEvent evt)
        {
            if (dragging == null || dragPointer != evt.pointerId) return;
            float sensitivity = evt.shiftKey ? .0012f : .006f;
            SetMacro(dragging, dragStart + (dragY - evt.position.y) * sensitivity);
            LockScroll();
            evt.StopPropagation();
        }
        private void OnKnobUp(PointerUpEvent evt)
        {
            if (dragging == null || dragPointer != evt.pointerId) return;
            EndDrag(evt.pointerId);
            if (root.HasPointerCapture(evt.pointerId)) root.ReleasePointer(evt.pointerId);
            evt.StopPropagation();
        }
        private void OnKnobCancel(PointerCancelEvent evt)
        {
            if (dragging == null || dragPointer != evt.pointerId) return;
            EndDrag(evt.pointerId);
            if (root.HasPointerCapture(evt.pointerId)) root.ReleasePointer(evt.pointerId);
            evt.StopPropagation();
        }
        private void OnKnobCaptureLost(PointerCaptureOutEvent evt)
        {
            if (dragging != null && dragPointer == evt.pointerId) EndDrag(evt.pointerId);
        }

        private void BuildModule(string heading, Color color, string onId, string syncId, Macro[] controls)
        {
            var cell = new VisualElement();
            cell.style.width = new Length(100, LengthUnit.Percent);
            cell.style.flexShrink = 0;
            cell.style.paddingLeft = 4;
            cell.style.paddingRight = 4;
            cell.style.marginBottom = 8;
            moduleCells.Add(cell);
            moduleLayout.Add(cell);

            var panel = new VisualElement();
            panel.style.flexDirection = FlexDirection.Column;
            panel.style.backgroundColor = Tile;
            panel.style.paddingLeft = 7;
            panel.style.paddingRight = 7;
            panel.style.paddingTop = 9;
            panel.style.paddingBottom = 7;
            Corners(panel, 7);
            cell.Add(panel);

            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.minHeight = 39;
            header.style.paddingLeft = 4;
            header.style.marginBottom = 4;
            panel.Add(header);
            var cap = T(heading, 12, color);
            cap.style.unityFontStyleAndWeight = FontStyle.Bold;
            cap.style.flexGrow = 1;
            cap.style.minWidth = 0;
            header.Add(cap);
            if (!string.IsNullOrEmpty(syncId)) AddSwitch(header, syncId, true);
            if (!string.IsNullOrEmpty(onId)) AddSwitch(header, onId, false);

            var grid = new VisualElement();
            grid.style.flexDirection = FlexDirection.Row;
            grid.style.flexWrap = Wrap.Wrap;
            grid.style.width = new Length(100, LengthUnit.Percent);
            panel.Add(grid);
            foreach (var macro in controls)
            {
                var tile = new VisualElement();
                tile.style.width = new Length(33.3333f, LengthUnit.Percent);
                tile.style.height = 118;
                tile.style.alignItems = Align.Center;
                tile.style.justifyContent = Justify.Center;
                tile.style.paddingBottom = 3;
                tile.pickingMode = PickingMode.Position;
                var face = new VisualElement();
                face.style.position = Position.Relative;
                face.style.width = 72;
                face.style.height = 72;
                face.style.alignItems = Align.Center;
                face.style.justifyContent = Justify.Center;
                face.style.backgroundColor = Face;
                Corners(face, 36);
                face.style.borderLeftWidth = 3; face.style.borderTopWidth = 3;
                face.style.borderRightWidth = 3; face.style.borderBottomWidth = 3;
                var rim = new Color(.24f, .32f, .39f);
                face.style.borderLeftColor = rim; face.style.borderTopColor = rim;
                face.style.borderRightColor = rim; face.style.borderBottomColor = rim;
                face.pickingMode = PickingMode.Position;
                var value = T("--", macro.choices != null ? 10 : 12, Ink);
                value.style.unityTextAlign = TextAnchor.MiddleCenter;
                value.style.unityFontStyleAndWeight = FontStyle.Bold;
                value.style.whiteSpace = WhiteSpace.Normal;
                value.style.maxWidth = 57;
                macro.value = value;
                face.Add(value);
                var marker = new VisualElement();
                marker.style.position = Position.Absolute;
                marker.style.width = 7;
                marker.style.height = 7;
                marker.style.backgroundColor = color;
                Corners(marker, 4);
                marker.pickingMode = PickingMode.Ignore;
                macro.marker = marker;
                face.Add(marker);
                PositionMarker(macro, 0f);
                tile.Add(face);
                var label = T(macro.label, 11, Dim);
                label.style.unityTextAlign = TextAnchor.MiddleCenter;
                label.style.whiteSpace = WhiteSpace.Normal;
                label.style.marginTop = 6;
                label.style.minHeight = 25;
                label.style.width = new Length(100, LengthUnit.Percent);
                tile.Add(label);
                knobTargets[face] = macro;
                macros.Add(macro);
                grid.Add(tile);
            }
        }
        private void AddSwitch(VisualElement header, string id, bool sync)
        {
            var sw = new Switch { id = id, param = SwitchParam(id), sync = sync };
            var button = Action(sync ? "SYNC" : "OFF", () => Toggle(sw));
            button.style.height = 39;
            button.style.minWidth = sync ? 75 : 59;
            button.style.marginLeft = 5;
            button.style.paddingLeft = 8;
            button.style.paddingRight = 8;
            button.style.backgroundColor = Face;
            button.style.unityFontStyleAndWeight = FontStyle.Bold;
            sw.button = button;
            switches.Add(sw);
            header.Add(button);
        }
        private void Update()
        {
            FindServices();
            if (root == null || root.resolvedStyle.display == DisplayStyle.None) return;
            if (dragging != null || Time.unscaledTime < scrollLockUntil) LockScroll();
            if (Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + .25f;
            Refresh();
        }
    }
}
