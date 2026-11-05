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

        private VisualElement root, moduleLayout, trackBar, presetCard, acidPanel;
        private Button acidWave, acidDelayRateButton, acidDistortionTypeButton, acidDelayModeButton;
        private bool acidDialDragging;
        private Vector2 acidDialScrollLock;
        // Capture the gesture on the SOUND root during trickle-down, BEFORE
        // ScrollView's touch manipulator sees it. Capturing from the dial face
        // during bubbling was too late on iOS/Android.
        private readonly Dictionary<VisualElement, AcidDial> acidDialTargets =
            new Dictionary<VisualElement, AcidDial>();
        private AcidDial activeAcidDial;
        private int activeAcidPointer = -1;
        private float acidStartY, acidStartValue;
        private float acidScrollLockUntil;
        // Keep the ScrollView mode CONSTANT while adjusting dials. Switching
        // Vertical -> Horizontal during a drag changes viewport layout/geometry,
        // making the control grid visibly jump or shift vertically on phones.
        private readonly List<AcidDial> acidDials = new List<AcidDial>();
        private sealed class AcidDial
        {
            public VisualElement marker;
            public Label value;
            public Func<AcidState, float> get;
            public Action<AcidState, float> set;
            public string label;
        }
        private ScrollView pageScroll;
        // Fixed audition drawer is a sibling of the scrolling modules, not a child.
        // It sends only live MIDI note events to the current Helm controller.
        private VisualElement keyboardDrawer, keyboardBody, keyboardFrame;
        private Button keyboardToggle, octaveDown, octaveUp;
        private Label octaveCaption;
        private bool keyboardOpen;
        private int bassOctave = 2, synthOctave = 4, padsOctave = 3, acidOctave = 2;
        private readonly List<AuditionKey> auditionKeys = new List<AuditionKey>();
        private readonly Dictionary<int, AuditionPress> auditionPresses = new Dictionary<int, AuditionPress>();
        private static readonly int[] WhiteSemitones = { 0, 2, 4, 5, 7, 9, 11 };
        private static readonly int[] BlackAfterWhite = { 0, 1, 3, 4, 5 };
        private sealed class AuditionKey
        {
            public int semitone;
            public int whiteIndex;
            public bool black;
            public VisualElement visual;
        }
        private sealed class AuditionPress
        {
            public HelmController controller;
            public bool acid;
            public AuditionKey key;
            public int midi;
        }
        private Label subhead, presetName, instrumentStatus;
        // Lightweight SOUND-local browser: no UXML/scene changes or new asset dependencies.
        private VisualElement presetBrowser, presetResults;
        private ScrollView presetScroll;
        private Label presetResultCount;
        private readonly Dictionary<string, Button> presetFilterButtons = new Dictionary<string, Button>();
        private string presetFilter = "ALL";
        private TrackKind browserTrack;
        private readonly List<Macro> macros = new List<Macro>();
        private readonly List<Switch> switches = new List<Switch>();
        private readonly List<VisualElement> moduleCells = new List<VisualElement>();
        private readonly Dictionary<TrackKind, Button> selectors = new Dictionary<TrackKind, Button>();
        private readonly Dictionary<VisualElement, Macro> knobTargets = new Dictionary<VisualElement, Macro>();
        private KMusicApp app;
        private KMusicPadSynthRuntime padRuntime;
        private KMusicHelmPresetPickerUI mainPicker;
        private ZPunkInstrumentRack rack;
        private KMusicProjectManager songProjects;
        private ZPunkSongTransport songTransport;
        private Button motionToggle, motionDepthMinus, motionDepthPlus, motionRate;
        private Label motionCaption;
        private TrackKind selected = TrackKind.Synth;
        // Expose the active instrument lane when switching from SOUND to ARRANGE.
        public int SelectedTrackIndex => (int)selected;
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
        // Live SOUND edits are the user's patch, not the factory preset. Helm's
        // native read-back can lag behind writes, so project save overlays these
        // explicit edits on its full native parameter snapshot. Each preset
        // loader clears the corresponding controller's edits before LoadPatch.
        private static readonly Dictionary<HelmController, Dictionary<Param, float>> liveSoundEdits =
            new Dictionary<HelmController, Dictionary<Param, float>>();

        public static Dictionary<Param, float> CaptureLiveEdits(HelmController controller)
        {
            if (controller != null && liveSoundEdits.TryGetValue(controller, out var values))
                return new Dictionary<Param, float>(values);
            return new Dictionary<Param, float>();
        }

        public static void ClearLiveEdits(HelmController controller)
        {
            if (controller != null) liveSoundEdits.Remove(controller);
        }

        public static void RecordLiveEdit(HelmController controller, Param param, float normalized)
        {
            if (controller == null || param == Param.kNone) return;
            if (!liveSoundEdits.TryGetValue(controller, out var values))
            {
                values = new Dictionary<Param, float>();
                liveSoundEdits[controller] = values;
            }
            values[param] = Mathf.Clamp01(normalized);
        }

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
        // Only change how knob captions are displayed; parameter names and
        // SOUND mappings remain untouched. Keep an instrument prefix together:
        // "OSC 1 LEVEL" -> "OSC 1\nLEVEL", "LFO 1 RATE" -> "LFO 1\nRATE".
        private static string FormatKnobLabel(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // Treat slash-separated captions as two centered lines too.
            // e.g. "DRY / WET" -> "DRY /\nWET".
            int slash = text.IndexOf(" / ", StringComparison.Ordinal);
            if (slash >= 0)
                return text.Substring(0, slash) + " /\n" + text.Substring(slash + 3);

            int split = text.LastIndexOf(' ');
            return split > 0 && split < text.Length - 1
                ? text.Substring(0, split) + "\n" + text.Substring(split + 1)
                : text;
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
            ReleaseAllAuditionNotes();
            root = host;
            if (root == null) return;
            root.UnregisterCallback<PointerDownEvent>(OnAcidDialDown, TrickleDown.TrickleDown);
            root.UnregisterCallback<PointerMoveEvent>(OnAcidDialMove, TrickleDown.TrickleDown);
            root.UnregisterCallback<PointerUpEvent>(OnAcidDialUp, TrickleDown.TrickleDown);
            root.UnregisterCallback<PointerCancelEvent>(OnAcidDialCancel, TrickleDown.TrickleDown);
            root.UnregisterCallback<PointerCaptureOutEvent>(OnAcidDialCaptureLost);
            root.UnregisterCallback<PointerDownEvent>(OnKnobDown, TrickleDown.TrickleDown);
            root.UnregisterCallback<PointerMoveEvent>(OnKnobMove, TrickleDown.TrickleDown);
            root.UnregisterCallback<PointerUpEvent>(OnKnobUp, TrickleDown.TrickleDown);
            root.UnregisterCallback<PointerCancelEvent>(OnKnobCancel, TrickleDown.TrickleDown);
            root.UnregisterCallback<PointerCaptureOutEvent>(OnKnobCaptureLost);
            root.Clear();
            presetBrowser = presetResults = null;
            presetScroll = null;
            presetResultCount = null;
            presetFilterButtons.Clear();
            presetFilter = "ALL";
            macros.Clear(); switches.Clear(); moduleCells.Clear();
            selectors.Clear(); knobTargets.Clear(); acidDials.Clear(); acidDialTargets.Clear();
            activeAcidDial = null; activeAcidPointer = -1; acidScrollLockUntil = 0f;
            acidDelayRateButton = acidDistortionTypeButton = acidDelayModeButton = null;
            acidDialDragging = false;
            auditionKeys.Clear();
            keyboardOpen = false;
            keyboardDrawer = keyboardBody = keyboardFrame = null;
            keyboardToggle = octaveDown = octaveUp = null;
            octaveCaption = null;
            motionToggle = motionDepthMinus = motionDepthPlus = motionRate = null;
            motionCaption = null;
            lastModuleWidth = -1f;
            dragging = null; dragPointer = -1;
            root.RegisterCallback<PointerDownEvent>(OnAcidDialDown, TrickleDown.TrickleDown);
            root.RegisterCallback<PointerMoveEvent>(OnAcidDialMove, TrickleDown.TrickleDown);
            root.RegisterCallback<PointerUpEvent>(OnAcidDialUp, TrickleDown.TrickleDown);
            root.RegisterCallback<PointerCancelEvent>(OnAcidDialCancel, TrickleDown.TrickleDown);
            root.RegisterCallback<PointerCaptureOutEvent>(OnAcidDialCaptureLost);
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
            foreach (var kind in new[] { TrackKind.Bass, TrackKind.Synth, TrackKind.Pads, TrackKind.Acid })
            {
                TrackKind chosen = kind;
                var button = Action(kind.ToString().ToUpperInvariant(), () => Select(chosen));
                button.style.flexGrow = 1;
                button.style.flexShrink = 1;
                button.style.flexBasis = new Length(0, LengthUnit.Pixel);
                button.style.minWidth = 0;
                button.style.marginLeft = 2;
                button.style.marginRight = 2;
                selectors[kind] = button;
                trackBar.Add(button);
            }
            root.Add(trackBar);

            presetCard = new VisualElement();
            presetCard.style.flexDirection = FlexDirection.Row;
            presetCard.style.alignItems = Align.Center;
            presetCard.style.height = 74;
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
            var presetCenter = new VisualElement();
            presetCenter.style.flexGrow = 1;
            presetCenter.style.flexShrink = 1;
            presetCenter.style.minWidth = 0;
            presetCenter.style.justifyContent = Justify.Center;
            presetCenter.pickingMode = PickingMode.Position;
            presetName = T("LOADING PRESET", 12, Ink);
            presetName.style.unityTextAlign = TextAnchor.MiddleCenter;
            presetName.style.whiteSpace = WhiteSpace.Normal;
            presetCenter.Add(presetName);
            var browseHint = T("TAP TO BROWSE PATCHES", 10, Aqua);
            browseHint.style.unityTextAlign = TextAnchor.MiddleCenter;
            browseHint.style.marginTop = 3;
            presetCenter.Add(browseHint);
            // The center section is an easily discovered, generous touch target.
            presetCenter.RegisterCallback<PointerUpEvent>(evt =>
            {
                if (evt.button == 0) OpenPresetBrowser();
            });
            presetCard.Add(presetCenter);
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
            acidPanel = BuildAcidPanel();
            acidPanel.style.display = DisplayStyle.None;
            pageScroll.Add(acidPanel);
            moduleLayout.RegisterCallback<GeometryChangedEvent>(OnLayoutChanged);
            BuildAuditionDrawer(); // Outside pageScroll: remains accessible while knobs scroll.

            BuildModule("OSCILLATORS", Pink, null, null, new[] {
                K("osc1.vol", "OSC 1 LEVEL", Param.kOsc1Volume),
                K("osc2.vol", "OSC 2 LEVEL", Param.kOsc2Volume),
                K("sub.vol", "SUB LEVEL", Param.kSubVolume),
                K("noise.vol", "NOISE LEVEL", Param.kNoiseVolume),
                Choices("osc1.uni", "OSC 1 VOICES", Param.kOsc1UnisonVoices,
                    "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12", "13", "14", "15"),
                K("osc1.detune", "OSC 1 DETUNE", Param.kOsc1UnisonDetune),
                Choices("osc2.uni", "OSC 2 VOICES", Param.kOsc2UnisonVoices,
                    "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12", "13", "14", "15"),
                K("osc2.detune", "OSC 2 DETUNE", Param.kOsc2UnisonDetune)
            });
            AddModuleHint("DETUNE spreads unison voices. Use 2+ VOICES and raise the oscillator LEVEL to hear it. Increasing voices uses more CPU.");
            BuildModule("FILTER", Gold, null, null, new[] {
                K("filter.cutoff", "CUTOFF", Param.kFilterCutoff),
                K("filter.res", "RESONANCE", Param.kResonance),
                K("filter.drive", "FILTER DRIVE", Param.kFilterDrive),
                K("filter.env", "ENV AMOUNT", Param.kFilterEnvelopeDepth),
                K("filter.atk", "ENV ATTACK", Param.kFilterAttack),
                K("filter.dec", "ENV DECAY", Param.kFilterDecay),
                K("filter.sus", "ENV SUSTAIN", Param.kFilterSustain),
                K("filter.rel", "ENV RELEASE", Param.kFilterRelease)
            });
            BuildFilterMotionControls();
            BuildModule("AMPLITUDE / GLIDE", Blue, null, null, new[] {
                K("amp.atk", "ATTACK", Param.kAmplitudeAttack),
                K("amp.dec", "DECAY", Param.kAmplitudeDecay),
                K("amp.sus", "SUSTAIN", Param.kAmplitudeSustain),
                K("amp.rel2", "RELEASE", Param.kAmplitudeRelease),
                K("master.porta", "PORTAMENTO", Param.kPortamento),
                K("master.vol", "SYNTH LEVEL", Param.kVolume)
            });
            BuildModule("MODULATION", Aqua, null, null, new[] {
                K("mod.rate", "LFO 1 RATE", Param.kMonoLfo1Frequency),
                K("mod.depth", "LFO 1 AMOUNT", Param.kMonoLfo1Amplitude),
                Choices("mod.wave", "LFO 1 WAVE", Param.kMonoLfo1Waveform,
                    "SIN", "TRI", "SQR", "SAW +", "SAW -", "3 STEP", "4 STEP",
                    "8 STEP", "3 PYR", "5 PYR", "9 PYR", "RAND 1", "RAND 2")
            });
            AddModuleHint("LFO amount and waveform affect sound only when the loaded preset routes LFO 1 to a synth parameter.");
            BuildModule("ARPEGGIATOR", Aqua, "arp.on", "arp.sync", new[] {
                Choices("arp.pattern", "PATTERN", Param.kArpPattern,
                    "UP", "DOWN", "UP / DOWN", "AS PLAYED", "RANDOM"),
                K("arp.rate", "RATE", Param.kArpTempo),
                K("arp.gate", "GATE", Param.kArpGate),
                Choices("arp.octaves", "OCTAVES", Param.kArpOctaves,
                    "1 OCT", "2 OCT", "3 OCT", "4 OCT")
            });
            /*
            BuildModule("DISTORTION", Pink, "fx.dist.on", null, new[] {
                Choices("fx.dist.type", "TYPE", Param.kDistortionType,
                    "SOFT CLIP", "HARD CLIP", "LINEAR FOLD", "SINE FOLD"),
                K("fx.dist.drive", "DRIVE", Param.kDistortionDrive),
                K("fx.dist.mix", "DRY / WET", Param.kDistortionMix)
            });
            */
            BuildModule("DELAY", Gold, "fx.delay.on", "fx.delay.sync", new[] {
                K("fx.delay.time", "TIME / RATE", Param.kDelayTempo),
                K("fx.delay.fb", "FEEDBACK", Param.kDelayFeedback),
                K("fx.delay.mix", "DRY / WET", Param.kDelayDryWet)
            });
            BuildModule("REVERB", Blue, "fx.rev.on", null, new[] {
                K("fx.rev.fb", "FEEDBACK", Param.kReverbFeedback),
                K("fx.rev.damp", "DAMPING", Param.kReverbDamping),
                K("fx.rev.mix", "DRY / WET", Param.kReverbDryWet)
            });
            var note = T("Drag a knob vertically to adjust. ON / OFF switches bypass modules; FREE / SYNC / DOTTED / TRIPLET set rhythmic timing.", 12, Dim);
            note.style.whiteSpace = WhiteSpace.Normal;
            note.style.marginBottom = 16;
            note.style.marginLeft = 6;
            moduleLayout.Add(note);
            BuildPresetBrowser(); // Last child is the topmost SOUND overlay.
            OnShown();
        }

        // The original six 303 knobs plus useful DRIVE and GLIDE, without a
        // second Helm plugin or an imitation hardware step-sequencer.
        private VisualElement BuildAcidPanel()
        {
            var panel = new VisualElement { name = "ZPunkAcid303Sound" };
            panel.style.flexDirection = FlexDirection.Column;
            panel.style.width = Length.Percent(100);
            panel.style.paddingTop = 8;
            var title = T("303  /  BASS LINE", 17, Gold);
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.marginBottom = 8;
            panel.Add(title);
            acidWave = Action("WAVE   SAW", () =>
            {
                ChangeAcidState(state => state.square = !state.square);
            });
            acidWave.style.height = 47;
            acidWave.style.marginBottom = 11;
            acidWave.style.backgroundColor = Tile;
            panel.Add(acidWave);
            var dialGrid = new VisualElement();
            dialGrid.style.width = Length.Percent(100);
            dialGrid.style.flexDirection = FlexDirection.Row;
            dialGrid.style.flexWrap = Wrap.Wrap;
            panel.Add(dialGrid);
            AddAcidDial(dialGrid, "TUNE", s => s.tune, (s,v) => s.tune=v);
            AddAcidDial(dialGrid, "CUTOFF", s => s.cutoff, (s,v) => s.cutoff=v);
            AddAcidDial(dialGrid, "RESONANCE", s => s.resonance, (s,v) => s.resonance=v);
            AddAcidDial(dialGrid, "ENV MOD", s => s.envMod, (s,v) => s.envMod=v);
            AddAcidDial(dialGrid, "DECAY", s => s.decay, (s,v) => s.decay=v);
            AddAcidDial(dialGrid, "ACCENT", s => s.accent, (s,v) => s.accent=v);
            AddAcidDial(dialGrid, "DRIVE", s => s.drive, (s,v) => s.drive=v);
            AddAcidDial(dialGrid, "GLIDE", s => s.glide, (s,v) => s.glide=v);
            // Post-FX instrument output trim. Missing field in pre-patch JSON
            // means unity gain; explicit zero (with the flag set) means mute.
            AddAcidDial(dialGrid, "LEVEL", s => s.outputLevelSet ? s.outputLevel : 1f,
                (s,v) => { s.outputLevel = v; s.outputLevelSet = true;
                    s.outputLevelUserAdjusted = true; });

            // Independent per-track effects, after the 303 filter, not Helm FX.
            var fxHeader = new VisualElement();
            fxHeader.style.flexDirection = FlexDirection.Row;
            fxHeader.style.alignItems = Align.Center;
            fxHeader.style.marginTop = 12;
            fxHeader.style.marginBottom = 8;
            var fxTitle = T("ACID  /  FX RACK", 16, Aqua);
            fxTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            fxTitle.style.flexGrow = 1;
            fxHeader.Add(fxTitle);
            var fxReset = Action("RESET FX", () => ChangeAcidState(state =>
            {
                state.fxDistortion = state.fxChorus = state.fxDelay = 0f;
                state.fxFeedback = state.fxReverb = state.fxRoom = 0f;
                state.fxDistControlsInitialized = true;
                state.fxDistTone = .5f;
                state.fxDistMix = .85f;
                state.fxDistBite = 0f;
                state.fxDistType = 0;
                state.fx303DriveModel = false;
                state.fxDelayMode = 0;
                state.fx303DelayModel = false;
                state.fxDelayTone = .5f;
                state.fxDelayRate = 0;
            }));
            fxReset.style.fontSize = 11;
            fxReset.style.minHeight = 38;
            fxHeader.Add(fxReset);
            panel.Add(fxHeader);

            // Tap to choose a pedal colour; knobs keep the same pointer-capture
            // handlers as the existing ACID dials (no ScrollView mode changes).
            acidDistortionTypeButton = Action("DISTORTION  /  CLASSIC", () => ChangeAcidState(state =>
            {
                PrepareAcidDistortion(state);
                state.fxDistType = (Mathf.Clamp(state.fxDistType, 0, 8) + 1) % 9;
                state.fx303DriveModel = state.fxDistType != 0;
            }));
            acidDistortionTypeButton.style.height = 44;
            acidDistortionTypeButton.style.backgroundColor = Tile;
            panel.Add(acidDistortionTypeButton);
            var distGrid = new VisualElement();
            distGrid.style.width = Length.Percent(100);
            distGrid.style.flexDirection = FlexDirection.Row;
            distGrid.style.flexWrap = Wrap.Wrap;
            panel.Add(distGrid);
            AddAcidDial(distGrid, "DIST DRIVE", s => s.fxDistortion,
                (s,v) => { PrepareAcidDistortion(s); s.fxDistortion=v;
                    if (s.fxDistType != 0) s.fx303DriveModel = true; }, true);
            AddAcidDial(distGrid, "DIST TONE", s => s.fxDistControlsInitialized ? s.fxDistTone : .5f,
                (s,v) => { PrepareAcidDistortion(s); s.fxDistTone=v;
                    if (s.fxDistType != 0) s.fx303DriveModel = true; }, true);
            AddAcidDial(distGrid, "DIST MIX", s => s.fxDistControlsInitialized ? s.fxDistMix :
                (s.fxDistortion > .0001f ? Mathf.Clamp01(s.fxDistortion * .85f) : .85f),
                (s,v) => { PrepareAcidDistortion(s); s.fxDistMix=v;
                    if (s.fxDistType != 0) s.fx303DriveModel = true; }, true);
            AddAcidDial(distGrid, "DIST BITE", s => s.fxDistControlsInitialized ? s.fxDistBite : 0f,
                (s,v) => { PrepareAcidDistortion(s); s.fxDistBite=v;
                    if (s.fxDistType != 0) s.fx303DriveModel = true; }, true);

            var echoHeader = T("MODULATION  /  ECHO", 14, Aqua);
            echoHeader.style.unityFontStyleAndWeight = FontStyle.Bold;
            echoHeader.style.marginTop = 14;
            echoHeader.style.marginBottom = 8;
            panel.Add(echoHeader);
            acidDelayModeButton = Action("DELAY STYLE  /  ORIGINAL", () => ChangeAcidState(state =>
            {
                int oldMode = Mathf.Clamp(state.fxDelayMode, 0, 6);
                state.fxDelayMode = (oldMode + 1) % 7;
                state.fx303DelayModel = state.fxDelayMode != 0;
                if (oldMode == 0 && state.fxDelayTone == 0f) state.fxDelayTone = .5f;
            }));
            acidDelayModeButton.style.height = 44;
            acidDelayModeButton.style.backgroundColor = Tile;
            panel.Add(acidDelayModeButton);
            var echoGrid = new VisualElement();
            echoGrid.style.width = Length.Percent(100);
            echoGrid.style.flexDirection = FlexDirection.Row;
            echoGrid.style.flexWrap = Wrap.Wrap;
            panel.Add(echoGrid);
            AddAcidDial(echoGrid, "CHORUS", s => s.fxChorus, (s,v) => s.fxChorus=v, true);
            AddAcidDial(echoGrid, "DELAY MIX", s => s.fxDelay, (s,v) => s.fxDelay=v, true);
            AddAcidDial(echoGrid, "FEEDBACK", s => s.fxFeedback, (s,v) => s.fxFeedback=v, true);
            AddAcidDial(echoGrid, "DELAY TONE", s => s.fxDelayMode == 0 && s.fxDelayTone == 0f ? .5f : s.fxDelayTone,
                (s,v) => { s.fxDelayTone = v;
                    if (s.fxDelayMode == 0) s.fxDelayMode = 1;
                    s.fx303DelayModel = true; }, true);
            AddAcidDial(echoGrid, "REVERB", s => s.fxReverb, (s,v) => s.fxReverb=v, true);
            AddAcidDial(echoGrid, "ROOM SIZE", s => s.fxRoom, (s,v) => s.fxRoom=v, true);
            acidDelayRateButton = Action("DELAY  /  1/8", () => ChangeAcidState(state =>
                state.fxDelayRate = (Mathf.Clamp(state.fxDelayRate, 0, 3) + 1) % 4));
            acidDelayRateButton.style.height = 44;
            acidDelayRateButton.style.marginTop = 5;
            acidDelayRateButton.style.marginBottom = 7;
            acidDelayRateButton.style.backgroundColor = Tile;
            panel.Add(acidDelayRateButton);
            return panel;
        }

        private static void PrepareAcidDistortion(AcidState state)
        {
            if (state == null || state.fxDistControlsInitialized) return;
            // A legacy patch uses its original distortion until a user touches
            // one of the extended controls. Preserve sensible neutral defaults.
            state.fxDistTone = .5f;
            state.fxDistMix = state.fxDistortion > .0001f
                ? Mathf.Clamp01(state.fxDistortion * .85f) : .85f;
            state.fxDistBite = 0f;
            state.fxDistControlsInitialized = true;
        }

        private AcidState CurrentAcidState()
        {
            FindServices();
            var song = songProjects != null ? songProjects.GetSongForArrangementEditor() : null;
            return song?.tracks != null && song.tracks.Count > 5 && song.tracks[5] != null &&
                song.tracks[5].instrument != null ? song.tracks[5].instrument.acid : null;
        }

        private void ChangeAcidState(Action<AcidState> change, bool userEdited = true)
        {
            FindServices();
            if (songProjects == null) return;
            var song = songProjects.GetSongForArrangementEditor();
            if (song == null) return;
            if (!ReferenceEquals(songProjects.CurrentSong, song))
                song = ArrangementEditing.IndependentCopy(song);
            song.EnsureAcidTrack();
            if (song.tracks.Count <= 5 || song.tracks[5] == null) return;
            var instrument = song.tracks[5].instrument;
            if (instrument == null) instrument = song.tracks[5].instrument = new InstrumentState();
            if (instrument.acid == null) instrument.acid = new AcidState();
            change(instrument.acid);
            if (userEdited) instrument.acid.patchModified = true;
            songProjects.AcceptArrangementEdit(song);
            if (songTransport != null) songTransport.ApplyLiveMix(song);
            RefreshAcidPanel();
        }

        private void AddAcidDial(VisualElement grid, string caption, Func<AcidState,float> get,
            Action<AcidState,float> set, bool isEffect = false)
        {
            Color dialColor = isEffect ? Aqua : Gold;
            var tile = new VisualElement();
            tile.style.width = Length.Percent(33.333f);
            tile.style.minWidth = 0;
            tile.style.height = 126;
            tile.style.flexShrink = 0;
            tile.style.alignItems = Align.Center;
            tile.style.justifyContent = Justify.Center;
            var face = new VisualElement();
            face.style.width = 72;
            face.style.height = 72;
            face.style.backgroundColor = Face;
            face.style.borderTopLeftRadius = 36;
            face.style.borderTopRightRadius = 36;
            face.style.borderBottomLeftRadius = 36;
            face.style.borderBottomRightRadius = 36;
            face.style.borderTopWidth = 2;
            face.style.borderRightWidth = 2;
            face.style.borderBottomWidth = 2;
            face.style.borderLeftWidth = 2;
            face.style.borderTopColor = dialColor;
            face.style.borderRightColor = dialColor;
            face.style.borderBottomColor = dialColor;
            face.style.borderLeftColor = dialColor;
            face.style.position = Position.Relative;
            face.style.justifyContent = Justify.Center;
            face.style.alignItems = Align.Center;
            var value = T("50", 15, Ink);
            value.style.unityFontStyleAndWeight = FontStyle.Bold;
            face.Add(value);
            var marker = new VisualElement();
            marker.style.position = Position.Absolute;
            marker.style.width = 6;
            marker.style.height = 6;
            marker.style.borderTopLeftRadius = 3;
            marker.style.borderTopRightRadius = 3;
            marker.style.borderBottomLeftRadius = 3;
            marker.style.borderBottomRightRadius = 3;
            marker.style.backgroundColor = dialColor;
            marker.pickingMode = PickingMode.Ignore;
            face.Add(marker);
            var dial = new AcidDial { marker = marker, value = value, get = get, set = set,
                label = caption };
            acidDials.Add(dial);
            // Touches are intercepted at the root in trickle-down; see
            // OnAcidDialDown. Do not register bubbling handlers here, because
            // ScrollView can have already begun dragging by that point.
            acidDialTargets[face] = dial;
            tile.Add(face);
            var label = T(FormatKnobLabel(caption), 11, dialColor);
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            label.style.marginTop = 6;
            tile.Add(label);
            grid.Add(tile);
        }

        private AcidDial FindAcidDial(VisualElement element)
        {
            while (element != null && element != root)
            {
                if (acidDialTargets.TryGetValue(element, out var dial)) return dial;
                element = element.parent;
            }
            return null;
        }

        private void OnAcidDialDown(PointerDownEvent evt)
        {
            if (evt.button != 0 || activeAcidPointer >= 0 || selected != TrackKind.Acid) return;
            var dial = FindAcidDial(evt.target as VisualElement);
            if (dial == null) return;
            var state = CurrentAcidState();
            if (state == null) return;
            activeAcidDial = dial;
            activeAcidPointer = evt.pointerId;
            acidStartValue = dial.get(state);
            acidStartY = evt.position.y;
            acidDialScrollLock = pageScroll != null ? pageScroll.scrollOffset : Vector2.zero;
            acidDialDragging = true;
            // Root captures the pointer BEFORE ScrollView's touch manipulator.
            // Do NOT change pageScroll.mode or any grid/layout style here.
            root.CapturePointer(evt.pointerId);
            evt.StopImmediatePropagation();
        }

        private void OnAcidDialMove(PointerMoveEvent evt)
        {
            if (activeAcidDial == null || evt.pointerId != activeAcidPointer) return;
            float amount = Mathf.Clamp01(acidStartValue + (acidStartY - evt.position.y) / 170f);
            var dial = activeAcidDial;
            ChangeAcidState(state => dial.set(state, amount));
            LockAcidScroll();
            evt.StopImmediatePropagation();
        }

        private void LockAcidScroll()
        {
            if (pageScroll == null) return;
            Vector2 now = pageScroll.scrollOffset;
            if (Mathf.Abs(now.x - acidDialScrollLock.x) > .1f ||
                Mathf.Abs(now.y - acidDialScrollLock.y) > .1f)
                pageScroll.scrollOffset = acidDialScrollLock;
        }

        private void EndAcidDial(int pointerId)
        {
            if (pointerId != activeAcidPointer) return;
            acidDialDragging = false;
            activeAcidDial = null;
            activeAcidPointer = -1;
            acidScrollLockUntil = Time.unscaledTime + .18f;
            LockAcidScroll();
            if (root != null && root.HasPointerCapture(pointerId)) root.ReleasePointer(pointerId);
        }

        private void OnAcidDialUp(PointerUpEvent evt)
        {
            if (evt.pointerId != activeAcidPointer) return;
            EndAcidDial(evt.pointerId);
            evt.StopImmediatePropagation();
        }

        private void OnAcidDialCancel(PointerCancelEvent evt)
        {
            if (evt.pointerId != activeAcidPointer) return;
            EndAcidDial(evt.pointerId);
            evt.StopImmediatePropagation();
        }

        private void OnAcidDialCaptureLost(PointerCaptureOutEvent evt)
        {
            if (evt.pointerId == activeAcidPointer) EndAcidDial(evt.pointerId);
        }

        private void RefreshAcidPanel()
        {
            var state = CurrentAcidState();
            if (state == null) return;
            if (acidWave != null) acidWave.text = "WAVE  /  " + (state.square ? "SQUARE" : "SAWTOOTH");
            if (acidDistortionTypeButton != null)
                acidDistortionTypeButton.text = "DISTORTION TYPE  /  " + ZPunkAcidPresets.DistortionLabel(state.fxDistType);
            if (acidDelayModeButton != null)
                acidDelayModeButton.text = "DELAY STYLE  /  " + ZPunkAcidPresets.DelayModeLabel(state.fxDelayMode);
            if (acidDelayRateButton != null)
                acidDelayRateButton.text = "DELAY RATE  /  " + ZPunkAcidPresets.RateLabel(state.fxDelayRate) + "  ·  BPM SYNC";
            if (presetName != null)
                presetName.text = (string.IsNullOrEmpty(state.patchName) ? "CUSTOM 303" : state.patchName) +
                    (state.patchModified ? "  /  EDITED" : "");
            foreach (var dial in acidDials)
            {
                float amount = Mathf.Clamp01(dial.get(state));
                dial.value.text = dial.label == "TUNE"
                    ? ((Mathf.RoundToInt((amount - .5f) * 24f) >= 0 ? "+" : "") +
                       Mathf.RoundToInt((amount - .5f) * 24f) + "st")
                    : dial.label == "DECAY" ? (.09f + amount * 1.5f).ToString("0.00") + "s"
                    : dial.label == "GLIDE" ? Mathf.RoundToInt(25 + amount * 225f) + "ms"
                    : Mathf.RoundToInt(amount * 100).ToString();
                float angle = Mathf.Lerp(-135f, 135f, amount) * Mathf.Deg2Rad;
                dial.marker.style.left = 33f + Mathf.Sin(angle) * 26f;
                dial.marker.style.top = 33f - Mathf.Cos(angle) * 26f;
            }
        }

        // ---------------------------------------------------------------------
        // Fixed, performance-only keyboard. Does not edit SongData, the piano
        // roll, generation history, a project, or any track/clip event list.
        // ---------------------------------------------------------------------
        private int KeyboardOctave
        {
            get { return selected == TrackKind.Bass ? bassOctave :
                selected == TrackKind.Pads ? padsOctave :
                selected == TrackKind.Acid ? acidOctave : synthOctave; }
        }

        private void BuildAuditionDrawer()
        {
            keyboardDrawer = new VisualElement();
            keyboardDrawer.style.flexShrink = 0;
            keyboardDrawer.style.marginTop = 5;
            keyboardDrawer.style.marginBottom = 5;
            keyboardDrawer.style.paddingTop = 5;
            keyboardDrawer.style.borderTopWidth = 1;
            keyboardDrawer.style.borderTopColor = Tile;
            root.Add(keyboardDrawer);

            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.height = 43;
            header.style.flexShrink = 0;
            keyboardDrawer.Add(header);
            var heading = T("KEYBOARD  /  AUDITION", 12, Dim);
            heading.style.unityFontStyleAndWeight = FontStyle.Bold;
            heading.style.flexGrow = 1;
            heading.style.marginLeft = 4;
            header.Add(heading);
            keyboardToggle = Action("SHOW KEYS  +", ToggleAuditionDrawer);
            keyboardToggle.style.minHeight = 38;
            keyboardToggle.style.marginRight = 3;
            header.Add(keyboardToggle);

            keyboardBody = new VisualElement();
            keyboardBody.style.display = DisplayStyle.None; // Closed by default.
            keyboardBody.style.paddingTop = 4;
            keyboardBody.style.paddingBottom = 4;
            keyboardDrawer.Add(keyboardBody);

            var tools = new VisualElement();
            tools.style.flexDirection = FlexDirection.Row;
            tools.style.alignItems = Align.Center;
            tools.style.height = 39;
            tools.style.marginBottom = 5;
            keyboardBody.Add(tools);
            octaveCaption = T("", 12, Dim);
            octaveCaption.style.flexGrow = 1;
            octaveCaption.style.marginLeft = 4;
            tools.Add(octaveCaption);
            octaveDown = Action("-", () => ShiftKeyboardOctave(-1));
            octaveDown.style.minHeight = 36;
            octaveDown.style.width = 46;
            tools.Add(octaveDown);
            octaveUp = Action("+", () => ShiftKeyboardOctave(1));
            octaveUp.style.minHeight = 36;
            octaveUp.style.width = 46;
            octaveUp.style.marginLeft = 6;
            tools.Add(octaveUp);

            keyboardFrame = new VisualElement();
            keyboardFrame.style.position = Position.Relative;
            keyboardFrame.style.height = 118;
            keyboardFrame.style.flexShrink = 0;
            keyboardFrame.style.backgroundColor = Face;
            keyboardFrame.style.overflow = Overflow.Hidden;
            Corners(keyboardFrame, 5);
            keyboardBody.Add(keyboardFrame);

            // White keys first, then black keys on top. The frame owns each
            // touch stream so fingers can slide and play chords independently.
            for (int i = 0; i < 14; i++)
                AddAuditionKey(i, WhiteSemitones[i % 7] + (i / 7) * 12, false);
            for (int i = 0; i < 14; i++)
            {
                if (Array.IndexOf(BlackAfterWhite, i % 7) < 0) continue;
                AddAuditionKey(i, WhiteSemitones[i % 7] + (i / 7) * 12 + 1, true);
            }
            keyboardFrame.RegisterCallback<GeometryChangedEvent>(LayoutAuditionKeys);
            keyboardFrame.RegisterCallback<PointerDownEvent>(AuditionDown);
            keyboardFrame.RegisterCallback<PointerMoveEvent>(AuditionMove);
            keyboardFrame.RegisterCallback<PointerUpEvent>(AuditionUp);
            keyboardFrame.RegisterCallback<PointerCancelEvent>(AuditionCancel);
            keyboardFrame.RegisterCallback<PointerCaptureOutEvent>(AuditionCaptureLost);
            RefreshAuditionHeader();
        }

        private void AddAuditionKey(int whiteIndex, int semitone, bool black)
        {
            var key = new AuditionKey { semitone = semitone, whiteIndex = whiteIndex, black = black };
            var visual = new VisualElement();
            visual.style.position = Position.Absolute;
            visual.style.top = 0;
            visual.style.borderBottomLeftRadius = 4;
            visual.style.borderBottomRightRadius = 4;
            visual.style.borderTopLeftRadius = black ? 0 : 3;
            visual.style.borderTopRightRadius = black ? 0 : 3;
            visual.style.backgroundColor = black ? new Color(.055f, .068f, .088f) :
                new Color(.94f, .95f, .98f);
            visual.style.borderLeftWidth = black ? 1 : .6f;
            visual.style.borderRightWidth = black ? 1 : .6f;
            visual.style.borderBottomWidth = 1;
            visual.style.borderLeftColor = black ? Face : Dim;
            visual.style.borderRightColor = black ? Face : Dim;
            visual.style.borderBottomColor = Face;
            key.visual = visual;
            // Route all touches through the frame: it owns pointer capture, so a
            // finger can glide across neighbouring keys instead of staying bound
            // to the key where the touch began. The keys are visual-only targets.
            visual.pickingMode = PickingMode.Ignore;
            auditionKeys.Add(key);
            keyboardFrame.Add(visual);
        }

        private void LayoutAuditionKeys(GeometryChangedEvent evt)
        {
            float width = evt.newRect.width;
            if (width <= 0 || float.IsNaN(width)) return;
            float whiteWidth = width / 14f;
            const float whiteHeight = 118f;
            // Put black keys on the boundary between adjacent white keys.
            // Index lookup uses their semitone rather than relying on child order.
            foreach (var key in auditionKeys)
            {
                if (!key.black)
                {
                    key.visual.style.left = key.whiteIndex * whiteWidth + .5f;
                    key.visual.style.width = Mathf.Max(1f, whiteWidth - 1f);
                    key.visual.style.height = whiteHeight;
                }
                else
                {
                    float blackWidth = whiteWidth * .64f;
                    key.visual.style.left = (key.whiteIndex + 1) * whiteWidth - blackWidth * .5f;
                    key.visual.style.width = blackWidth;
                    key.visual.style.height = 72f;
                }
            }
        }

        private void ToggleAuditionDrawer()
        {
            keyboardOpen = !keyboardOpen;
            if (!keyboardOpen) ReleaseAllAuditionNotes();
            if (keyboardBody != null)
                keyboardBody.style.display = keyboardOpen ? DisplayStyle.Flex : DisplayStyle.None;
            if (keyboardToggle != null)
                keyboardToggle.text = keyboardOpen ? "HIDE KEYS  -" : "SHOW KEYS  +";
            RefreshAuditionHeader();
        }

        private void RefreshAuditionHeader()
        {
            int octave = KeyboardOctave;
            if (octaveCaption != null)
                octaveCaption.text = selected.ToString().ToUpperInvariant() +
                    "  /  OCTAVE " + octave + "–" + (octave + 1);
            if (octaveDown != null) octaveDown.SetEnabled(octave > 0);
            if (octaveUp != null) octaveUp.SetEnabled(octave < 7);
        }

        private void ShiftKeyboardOctave(int delta)
        {
            ReleaseAllAuditionNotes();
            int octave = Mathf.Clamp(KeyboardOctave + delta, 0, 7);
            if (selected == TrackKind.Bass) bassOctave = octave;
            else if (selected == TrackKind.Pads) padsOctave = octave;
            else if (selected == TrackKind.Acid) acidOctave = octave;
            else synthOctave = octave;
            RefreshAuditionHeader();
        }

        // Capture each finger on the stationary keyboard frame, not on its
        // initial key. UI Toolkit then continues sending moves during a slide,
        // including when a finger briefly moves beyond the keyboard edge.
        private AuditionKey AuditionKeyAt(Vector3 panelPosition)
        {
            if (keyboardFrame == null || !keyboardOpen || auditionKeys.Count < 14)
                return null;
            var point = new Vector2(panelPosition.x, panelPosition.y);
            Rect bounds = keyboardFrame.worldBound;
            if (bounds.width <= 0f || !bounds.Contains(point)) return null;

            // Black keys have priority in the upper section (they overlap the
            // whites). Resolve against their actual scaled panel bounds.
            for (int i = auditionKeys.Count - 1; i >= 14; --i)
            {
                AuditionKey blackKey = auditionKeys[i];
                if (blackKey.black && blackKey.visual.worldBound.Contains(point))
                    return blackKey;
            }

            // Resolve the remaining area continuously, including the tiny
            // decorative gaps between white keys, to avoid skipped notes.
            int index = Mathf.Clamp(Mathf.FloorToInt(
                (point.x - bounds.xMin) / bounds.width * 14f), 0, 13);
            return auditionKeys[index];
        }

        private void AuditionDown(PointerDownEvent evt)
        {
            if (evt.button != 0 || !keyboardOpen ||
                auditionPresses.ContainsKey(evt.pointerId)) return;
            AuditionKey key = AuditionKeyAt(evt.position);
            if (key == null) return;
            bool isAcid = selected == TrackKind.Acid;
            HelmController controller = isAcid ? null : Controller();
            if (!isAcid && (controller == null || !controller.isActiveAndEnabled)) return;
            if (isAcid)
            {
                if (songTransport == null) songTransport = FindObjectOfType<ZPunkSongTransport>();
                // Audition never plays extra 303 notes over a running pattern.
                if (songTransport == null || songTransport.IsPlaying || songTransport.IsExportingOneShot)
                    return;
            }

            var press = new AuditionPress { controller = controller, acid = isAcid, key = null, midi = -1 };
            auditionPresses.Add(evt.pointerId, press);
            keyboardFrame.CapturePointer(evt.pointerId);
            SetAuditionKey(press, key);
            evt.StopPropagation();
        }

        private void AuditionMove(PointerMoveEvent evt)
        {
            if (!auditionPresses.TryGetValue(evt.pointerId, out AuditionPress press))
                return;
            // A slide gives a fresh note at every new key, without needing to
            // lift the finger. Outside the keys, release the sounding note but
            // retain capture, allowing a slide back onto the keyboard.
            SetAuditionKey(press, AuditionKeyAt(evt.position));
            evt.StopPropagation();
        }

        private void AuditionUp(PointerUpEvent evt)
        {
            if (!auditionPresses.ContainsKey(evt.pointerId)) return;
            ReleaseAuditionPointer(evt.pointerId);
            if (keyboardFrame != null && keyboardFrame.HasPointerCapture(evt.pointerId))
                keyboardFrame.ReleasePointer(evt.pointerId);
            evt.StopPropagation();
        }

        private void AuditionCancel(PointerCancelEvent evt)
        {
            if (!auditionPresses.ContainsKey(evt.pointerId)) return;
            ReleaseAuditionPointer(evt.pointerId);
            if (keyboardFrame != null && keyboardFrame.HasPointerCapture(evt.pointerId))
                keyboardFrame.ReleasePointer(evt.pointerId);
            evt.StopPropagation();
        }

        private void AuditionCaptureLost(PointerCaptureOutEvent evt)
        {
            ReleaseAuditionPointer(evt.pointerId);
        }

        // Helm exposes note pitch rather than a per-touch voice handle. Two
        // fingers on the same pitch therefore share one NoteOn, and the note is
        // released only when the last finger leaves it.
        private bool OtherAuditionHolds(HelmController controller, int midi,
            AuditionPress excluded)
        {
            foreach (var other in auditionPresses.Values)
                if (other != excluded && other.controller == controller &&
                    other.acid == excluded.acid && other.midi == midi)
                    return true;
            return false;
        }

        private void SetAuditionKey(AuditionPress press, AuditionKey newKey)
        {
            if (press.key == newKey) return;
            AuditionKey oldKey = press.key;
            int oldMidi = press.midi;
            press.key = null;
            press.midi = -1;

            if (oldMidi >= 0 && !OtherAuditionHolds(press.controller, oldMidi, press))
            {
                try
                {
                    if (press.acid) songTransport?.AuditionAcidNoteOff(oldMidi);
                    else if (press.controller != null) press.controller.NoteOff(oldMidi);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[ZPUNK SOUND] Audition note-off failed: " + ex.Message);
                }
            }
            if (oldKey != null) RefreshAuditionKey(oldKey);

            if (newKey == null) return;
            int newMidi = (KeyboardOctave + 1) * 12 + newKey.semitone;
            if (newMidi < 0 || newMidi > 127 ||
                (!press.acid && (press.controller == null || !press.controller.isActiveAndEnabled))) return;
            try
            {
                if (!OtherAuditionHolds(press.controller, newMidi, press))
                {
                    if (press.acid) songTransport?.AuditionAcidNoteOn(newMidi);
                    else press.controller.NoteOn(newMidi, .85f);
                }
                press.key = newKey;
                press.midi = newMidi;
                RefreshAuditionKey(newKey);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ZPUNK SOUND] Audition note-on failed: " + ex.Message);
            }
        }

        private void ReleaseAuditionPointer(int pointerId)
        {
            if (!auditionPresses.TryGetValue(pointerId, out AuditionPress press)) return;
            // Keep the current pointer in the collection while looking for
            // other holders; SetAuditionKey explicitly excludes this press.
            SetAuditionKey(press, null);
            auditionPresses.Remove(pointerId);
        }

        private void RefreshAuditionKey(AuditionKey key)
        {
            bool pressed = false;
            foreach (var press in auditionPresses.Values)
                if (press.key == key) { pressed = true; break; }
            if (key.visual != null)
                key.visual.style.backgroundColor = pressed ? Aqua :
                    key.black ? new Color(.055f, .068f, .088f) : new Color(.94f, .95f, .98f);
        }

        private void ReleaseAllAuditionNotes()
        {
            if (auditionPresses.Count == 0) return;
            // Don't use Helm AllNotesOff: that would also stop the song's notes.
            var pointers = new List<int>(auditionPresses.Keys);
            foreach (int id in pointers)
            {
                ReleaseAuditionPointer(id);
                if (keyboardFrame != null && keyboardFrame.HasPointerCapture(id))
                    keyboardFrame.ReleasePointer(id);
            }
        }

        public void OnHidden()
        {
            ClosePresetBrowser();
            ReleaseAllAuditionNotes();
        }

        private void OnDisable() { ClosePresetBrowser(); ReleaseAllAuditionNotes(); }
        private void OnDestroy() { ClosePresetBrowser(); ReleaseAllAuditionNotes(); }
        private void OnApplicationPause(bool paused)
        {
            if (paused) ReleaseAllAuditionNotes();
        }
        private void OnApplicationFocus(bool focused)
        {
            if (!focused) ReleaseAllAuditionNotes();
        }

        private void AddModuleHint(string message)
        {
            if (moduleCells.Count == 0) return;
            var hint = T(message, 12, Dim);
            hint.style.whiteSpace = WhiteSpace.Normal;
            hint.style.marginLeft = 11;
            hint.style.marginRight = 11;
            hint.style.marginTop = 3;
            hint.style.marginBottom = 5;
            moduleCells[moduleCells.Count - 1].Add(hint);
        }

        private static bool IsUnisonVoices(Param param)
        {
            return param == Param.kOsc1UnisonVoices || param == Param.kOsc2UnisonVoices;
        }

        private static int NativeVoiceCount(HelmController controller, Param param)
        {
            return Mathf.Clamp(Mathf.RoundToInt(controller.GetParameterValue(param)), 1, 15);
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
            if (songProjects == null) songProjects = FindObjectOfType<KMusicProjectManager>();
            if (songTransport == null) songTransport = FindObjectOfType<ZPunkSongTransport>();
        }

        // Small, non-scrolling performance row directly below the FILTER module.
        // Unlike Helm LFO routing this always modulates Helm FILTER CUTOFF and
        // always follows the current song's beat clock. No scene/UXML edits.
        private void BuildFilterMotionControls()
        {
            var panel = new VisualElement();
            panel.style.backgroundColor = Tile;
            panel.style.width = Length.Percent(100);
            panel.style.flexShrink = 0;
            Corners(panel, 6);
            panel.style.paddingLeft = 8;
            panel.style.paddingRight = 8;
            panel.style.paddingTop = 8;
            panel.style.paddingBottom = 8;
            panel.style.marginBottom = 8;
            var heading = T("FILTER MOTION  /  TEMPO SYNC", 12, Gold);
            heading.style.unityFontStyleAndWeight = FontStyle.Bold;
            panel.Add(heading);
            var controls = new VisualElement();
            controls.style.flexDirection = FlexDirection.Row;
            controls.style.flexWrap = Wrap.NoWrap;
            controls.style.width = Length.Percent(100);
            controls.style.marginTop = 6;
            panel.Add(controls);
            motionToggle = Action("OFF", () =>
            {
                EditFilterMotion(i =>
                {
                    i.filterMotionEnabled = !i.filterMotionEnabled;
                    if (i.filterMotionEnabled)
                    {
                        // Capture the current patch cutoff when enabling. Keep
                        // a previously chosen rate when re-enabling this track.
                        if (ReadPercent(Controller(), Param.kFilterCutoff, out float current))
                            i.filterMotionCenter = current;
                        if (i.filterMotionDepth < .001f)
                        {
                            i.filterMotionDepth = .4f;
                            i.filterMotionRate = 1;
                        }
                    }
                });
            });
            motionDepthMinus = Action("−", () => EditFilterMotion(i =>
                i.filterMotionDepth = Mathf.Clamp01(Mathf.Round(i.filterMotionDepth * 10f - 1f) / 10f)));
            motionDepthPlus = Action("+", () => EditFilterMotion(i =>
                i.filterMotionDepth = Mathf.Clamp01(Mathf.Round(i.filterMotionDepth * 10f + 1f) / 10f)));
            motionRate = Action("1 BAR", () => EditFilterMotion(i =>
                i.filterMotionRate = (Mathf.Clamp(i.filterMotionRate, 0, 3) + 1) % 4));
            foreach (var button in new[] { motionToggle, motionDepthMinus, motionDepthPlus, motionRate })
            {
                button.style.minWidth = 0;
                button.style.flexShrink = 1;
                button.style.flexGrow = 1;
                button.style.flexBasis = new Length(0, LengthUnit.Pixel);
                button.style.marginRight = 3;
                controls.Add(button);
            }
            motionRate.style.flexGrow = 1.5f;
            motionCaption = T("DEPTH 0%  ·  Set FILTER CUTOFF for center", 11, Dim);
            motionCaption.style.whiteSpace = WhiteSpace.Normal;
            motionCaption.style.marginTop = 6;
            panel.Add(motionCaption);
            moduleLayout.Add(panel);
        }

        private InstrumentState SelectedMotionState()
        {
            FindServices();
            var song = songProjects != null ? songProjects.GetSongForArrangementEditor() : null;
            int index = (int)selected;
            if (song == null || song.tracks == null || index < 0 || index >= song.tracks.Count)
                return null;
            return song.tracks[index] != null ? song.tracks[index].instrument : null;
        }

        private void EditFilterMotion(Action<InstrumentState> edit)
        {
            FindServices();
            if (songProjects == null) return;
            var song = songProjects.GetSongForArrangementEditor();
            int index = (int)selected;
            if (song == null || song.tracks == null || index < 0 || index >= song.tracks.Count ||
                song.tracks[index] == null) return;
            if (!ReferenceEquals(songProjects.CurrentSong, song))
                song = ArrangementEditing.IndependentCopy(song);
            if (song.tracks[index].instrument == null)
                song.tracks[index].instrument = new InstrumentState();
            edit(song.tracks[index].instrument);
            songProjects.AcceptArrangementEdit(song);
            if (songTransport != null) songTransport.ApplyLiveMix(song);
            RefreshMotionButtons();
        }

        private void RefreshMotionButtons()
        {
            var state = SelectedMotionState();
            bool enabled = state != null && state.filterMotionEnabled;
            if (motionToggle != null)
            {
                motionToggle.text = enabled ? "ON" : "OFF";
                motionToggle.style.backgroundColor = enabled ? Aqua : Face;
                motionToggle.style.color = enabled ? Face : Ink;
            }
            if (motionRate != null)
            {
                int rate = state != null ? Mathf.Clamp(state.filterMotionRate, 0, 3) : 1;
                motionRate.text = new[] { "1/2 BAR", "1 BAR", "2 BARS", "4 BARS" }[rate];
            }
            if (motionCaption != null)
                motionCaption.text = "DEPTH " + Mathf.RoundToInt(state != null ?
                    Mathf.Clamp01(state.filterMotionDepth) * 100f : 0f) +
                    "%   ·   CUTOFF sets the center";
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
            if (selected != track) ClosePresetBrowser();
            if (selected != track) ReleaseAllAuditionNotes();
            if (dragging != null && root != null && root.HasPointerCapture(dragPointer))
                root.ReleasePointer(dragPointer);
            dragging = null; dragPointer = -1;
            selected = track;
            ClearPendingDistortion();
            OnShown();
        }
        public void OnShown(int requestedTrack = -1)
        {
            if (requestedTrack >= 0 && requestedTrack <= 5)
            {
                var kind = (TrackKind)requestedTrack;
                if (kind == TrackKind.Bass || kind == TrackKind.Synth || kind == TrackKind.Pads || kind == TrackKind.Acid)
                {
                    if (selected != kind)
                    {
                        ClosePresetBrowser();
                        ReleaseAllAuditionNotes();
                    }
                    selected = kind;
                }
            }
            if (root == null) return;
            // Release any dial capture if a tab switch interrupts a drag.
            if (activeAcidPointer >= 0) EndAcidDial(activeAcidPointer);
            RefreshAuditionHeader();
            FindServices();
            ClearPendingDistortion();
            foreach (var entry in selectors)
            {
                bool active = entry.Key == selected;
                entry.Value.style.backgroundColor = active ? Aqua : Tile;
                entry.Value.style.color = active ? Face : Ink;
            }
            if (subhead != null)
                subhead.text = selected == TrackKind.Acid ? "ACID  /  303 MONO SYNTH" : selected.ToString().ToUpperInvariant() + "  /  AUDIO HELM";
            bool showAcid = selected == TrackKind.Acid;
            if (presetCard != null) presetCard.style.display = DisplayStyle.Flex;
            if (moduleLayout != null) moduleLayout.style.display = showAcid ? DisplayStyle.None : DisplayStyle.Flex;
            if (acidPanel != null) acidPanel.style.display = showAcid ? DisplayStyle.Flex : DisplayStyle.None;
            // The existing two-octave drawer doubles as a monophonic 303
            // audition keyboard; no extra panel height on portrait screens.
            if (keyboardDrawer != null) keyboardDrawer.style.display = DisplayStyle.Flex;
            RefreshMotionButtons();
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
            ReleaseAllAuditionNotes();
            FindServices();
            if (selected == TrackKind.Bass) rack?.StepBassPreset(direction);
            else if (selected == TrackKind.Synth && mainPicker != null)
                mainPicker.ApplyPresetChoice(mainPicker.CurrentPresetIndex + direction, null);
            else if (selected == TrackKind.Pads && padRuntime != null)
                padRuntime.StepPreset(direction);
            else if (selected == TrackKind.Acid)
            {
                var state = CurrentAcidState();
                int index = ZPunkAcidPresets.IndexOf(state != null ? state.patchName : null);
                if (index < 0) index = direction > 0 ? -1 : 0;
                int next = (index + direction + ZPunkAcidPresets.Count) % ZPunkAcidPresets.Count;
                ChangeAcidState(acid => ZPunkAcidPresets.Load(next, acid), false);
            }
            Refresh();
        }

        // ---------------------------------------------------------------------
        // Searchable ZOR preset browser for BASS / SYNTH / PADS.
        // The list is drawn from the existing indexed banks; choosing a row
        // calls the existing loader, preserving project saves and patch edits.
        // ---------------------------------------------------------------------
        private void BuildPresetBrowser()
        {
            presetBrowser = new VisualElement();
            presetBrowser.style.position = Position.Absolute;
            presetBrowser.style.left = 0;
            presetBrowser.style.right = 0;
            presetBrowser.style.top = 0;
            presetBrowser.style.bottom = 0;
            presetBrowser.style.backgroundColor = Bg;
            presetBrowser.style.paddingLeft = 12;
            presetBrowser.style.paddingRight = 12;
            presetBrowser.style.paddingTop = 12;
            presetBrowser.style.paddingBottom = 12;
            presetBrowser.style.flexDirection = FlexDirection.Column;
            presetBrowser.style.display = DisplayStyle.None;
            root.Add(presetBrowser);

            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.flexShrink = 0;
            header.style.marginBottom = 8;
            presetBrowser.Add(header);
            var heading = T("BROWSE PATCHES", 16, Ink);
            heading.style.unityFontStyleAndWeight = FontStyle.Bold;
            heading.style.flexGrow = 1;
            header.Add(heading);
            var close = Action("CLOSE  X", ClosePresetBrowser);
            close.style.minHeight = 44;
            close.style.paddingLeft = 12;
            close.style.paddingRight = 12;
            header.Add(close);

            var hint = T("SELECT A PATCH  /  TAP TO LOAD", 11, Dim);
            hint.style.marginBottom = 10;
            presetBrowser.Add(hint);

            var filters = new VisualElement();
            filters.style.flexDirection = FlexDirection.Row;
            filters.style.flexShrink = 0;
            filters.style.marginBottom = 10;
            foreach (var filter in new[] { "ALL", "TECHNO", "HOUSE", "MOTION" })
            {
                string chosen = filter;
                var chip = Action(chosen, () =>
                {
                    presetFilter = chosen;
                    PopulatePresetResults();
                });
                chip.style.flexGrow = 1;
                chip.style.flexShrink = 1;
                chip.style.flexBasis = new Length(0, LengthUnit.Pixel);
                chip.style.minWidth = 0;
                chip.style.marginLeft = 2;
                chip.style.marginRight = 2;
                chip.style.fontSize = 10;
                chip.style.paddingLeft = 1;
                chip.style.paddingRight = 1;
                presetFilterButtons[chosen] = chip;
                filters.Add(chip);
            }
            presetBrowser.Add(filters);

            presetResultCount = T("", 11, Dim);
            presetResultCount.style.marginBottom = 5;
            presetBrowser.Add(presetResultCount);
            presetScroll = new ScrollView(ScrollViewMode.Vertical);
            presetScroll.style.flexGrow = 1;
            presetScroll.style.minHeight = 0;
            presetScroll.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            presetScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            presetBrowser.Add(presetScroll);
            presetResults = new VisualElement();
            presetResults.style.width = Length.Percent(100);
            presetResults.style.flexDirection = FlexDirection.Column;
            presetScroll.Add(presetResults);
        }

        private int BrowserPresetCount()
        {
            if (selected == TrackKind.Acid) return ZPunkAcidPresets.Count;
            if (selected == TrackKind.Bass) return rack != null ? rack.BassPresetCount : 0;
            if (selected == TrackKind.Pads) return padRuntime != null ? padRuntime.PresetCount : 0;
            return mainPicker != null ? mainPicker.SynthPresetCount : 0;
        }

        private int BrowserCurrentIndex()
        {
            if (selected == TrackKind.Acid)
            {
                var state = CurrentAcidState();
                return ZPunkAcidPresets.IndexOf(state != null ? state.patchName : null);
            }
            if (selected == TrackKind.Bass) return rack != null ? rack.BassPresetIndex : -1;
            if (selected == TrackKind.Pads) return padRuntime != null ? padRuntime.CurrentPresetIndex : -1;
            return mainPicker != null ? mainPicker.CurrentPresetIndex : -1;
        }

        private string BrowserPresetPath(int index)
        {
            if (selected == TrackKind.Acid) return ZPunkAcidPresets.NameAt(index);
            if (selected == TrackKind.Bass) return rack != null ? rack.BassPresetPathAt(index) : null;
            if (selected == TrackKind.Pads) return padRuntime != null ? padRuntime.PresetPathAt(index) : null;
            return mainPicker != null ? mainPicker.PresetPathAt(index) : null;
        }

        private void OpenPresetBrowser()
        {
            FindServices();
            if (presetBrowser == null || BrowserPresetCount() == 0) return;
            ReleaseAllAuditionNotes();
            browserTrack = selected;
            presetFilter = "ALL";
            // Reset the search every time so a previously hidden result is
            // never confused with an unavailable preset.
            presetBrowser.style.display = DisplayStyle.Flex;
            PopulatePresetResults();
        }

        private void ClosePresetBrowser()
        {
            if (presetBrowser != null) presetBrowser.style.display = DisplayStyle.None;
        }

        private void PopulatePresetResults()
        {
            if (presetResults == null || presetBrowser == null) return;
            if (browserTrack != selected) { ClosePresetBrowser(); return; }
            presetResults.Clear();
            foreach (var pair in presetFilterButtons)
            {
                bool chosen = pair.Key == presetFilter;
                pair.Value.style.backgroundColor = chosen ? Aqua : Tile;
                pair.Value.style.color = chosen ? Face : Ink;
            }

            int total = BrowserPresetCount();
            int current = BrowserCurrentIndex();
            int shown = 0;
            VisualElement currentRow = null;
            for (int i = 0; i < total; ++i)
            {
                string path = BrowserPresetPath(i);
                if (string.IsNullOrEmpty(path)) continue;
                string name = DisplayPath(path);
                string group = selected == TrackKind.Acid ? ZPunkAcidPresets.CategoryAt(i) : name;
                if (presetFilter != "ALL" &&
                    (string.IsNullOrEmpty(group) || group.IndexOf(presetFilter, StringComparison.OrdinalIgnoreCase) < 0))
                    continue;

                int choice = i;
                bool active = i == current;
                var row = new Button(() => SelectPresetFromBrowser(choice));
                row.text = (active ? "CURRENT  /  " : "") + name;
                row.style.minHeight = 52;
                row.style.paddingLeft = 12;
                row.style.paddingRight = 12;
                row.style.paddingTop = 10;
                row.style.paddingBottom = 10;
                row.style.marginBottom = 5;
                row.style.unityTextAlign = TextAnchor.MiddleLeft;
                row.style.whiteSpace = WhiteSpace.Normal;
                row.style.fontSize = 13;
                row.style.flexShrink = 0;
                row.style.backgroundColor = active ? Aqua : Tile;
                row.style.color = active ? Face : Ink;
                Corners(row, 5);
                presetResults.Add(row);
                if (active) currentRow = row;
                shown++;
            }
            if (shown == 0)
            {
                var empty = T("NO PATCHES IN THIS CATEGORY", 12, Dim);
                empty.style.whiteSpace = WhiteSpace.Normal;
                empty.style.marginTop = 18;
                presetResults.Add(empty);
            }
            if (presetResultCount != null)
                presetResultCount.text = selected.ToString().ToUpperInvariant() + "  /  " +
                    shown + " OF " + total + " PATCHES";
            // Only jump to selected on initial unfiltered browsing.
            if (shown > 0 && currentRow != null && presetFilter == "ALL")
                presetScroll.ScrollTo(currentRow);
        }

        private void SelectPresetFromBrowser(int index)
        {
            FindServices();
            if (browserTrack != selected || index < 0 || index >= BrowserPresetCount())
            { ClosePresetBrowser(); return; }
            string path = BrowserPresetPath(index);
            if (string.IsNullOrEmpty(path)) return;
            ReleaseAllAuditionNotes();
            if (selected == TrackKind.Bass && rack != null)
                rack.SelectBassPreset(index);
            else if (selected == TrackKind.Synth && mainPicker != null)
                mainPicker.ApplyPresetChoice(index, path);
            else if (selected == TrackKind.Pads && padRuntime != null)
                padRuntime.ApplyPresetChoice(index, path);
            else if (selected == TrackKind.Acid)
                ChangeAcidState(acid => ZPunkAcidPresets.Load(index, acid), false);
            ClosePresetBrowser();
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
                    : IsUnisonVoices(param)
                        ? (NativeVoiceCount(controller, param) - 1f) / 14f
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
            if (selected == TrackKind.Acid)
            {
                RefreshAcidPanel();
                if (instrumentStatus != null) instrumentStatus.text = "MONOPHONIC  /  SAW + SQUARE  /  ACCENT + SLIDE";
                return;
            }
            var controller = Controller();
            string status = controller == null ? "INSTRUMENT INITIALIZING" :
                "CHANNEL " + controller.channel + "  /  AUDIO HELM";
            if (instrumentStatus != null && instrumentStatus.text != status)
                instrumentStatus.text = status;
            string preset = "NO PRESET";
            if (selected == TrackKind.Bass && rack != null)
                preset = rack.BassPresetName + "  (" + (rack.BassPresetCount > 0 ? rack.BassPresetIndex + 1 : 0) + "/" + rack.BassPresetCount + ")";
            else if (selected == TrackKind.Synth && mainPicker != null)
                preset = DisplayPath(mainPicker.CurrentPresetRelPath) + "  (" +
                    (mainPicker.SynthPresetCount > 0 ? mainPicker.CurrentPresetIndex + 1 : 0) +
                    "/" + mainPicker.SynthPresetCount + ")";
            else if (selected == TrackKind.Pads && padRuntime != null)
                preset = padRuntime.PresetCount > 0
                    ? DisplayPath(padRuntime.CurrentPresetRelPath) + "  (" + (padRuntime.CurrentPresetIndex + 1) + "/" + padRuntime.PresetCount + ")"
                    : "PAD PRESETS UNAVAILABLE";
            if (pendingController != controller || pendingPreset != preset)
            {
                // A preset/project change can reset the native voice state.
                // Release only notes owned by this drawer, never transport notes.
                ReleaseAllAuditionNotes();
                pendingDistortion.Clear();
                pendingController = controller;
                pendingPreset = preset;
            }
            if (presetName != null && presetName.text != preset) presetName.text = preset;

            RefreshMotionButtons();
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
                // Show the editable center, not the currently oscillating cutoff.
                if (m.id == "filter.cutoff")
                {
                    var state = SelectedMotionState();
                    if (state != null && state.filterMotionEnabled && state.filterMotionCenter >= 0f)
                    { amount = Mathf.Clamp01(state.filterMotionCenter); available = true; }
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
                return IsUnisonVoices(actualParam) ? m.choices[index] + "V" : m.choices[index];
            }
            // Show native units when reliable; Helm's parameter percent is
            // generally non-linear and a blanket 0-100% hides useful meaning.
            if (controller != null)
            {
                try
                {
                    float nativeValue = controller.GetParameterValue(actualParam);
                    if (m.id == "filter.cutoff")
                    {
                        // Helm expresses filter cutoff in MIDI-note units.
                        float hz = 440f * Mathf.Pow(2f, (nativeValue - 69f) / 12f);
                        return hz >= 1000f ? (hz / 1000f).ToString("0.#") + "kHz"
                            : Mathf.RoundToInt(hz) + "Hz";
                    }
                    if (m.id == "filter.drive")
                        return (nativeValue >= 0f ? "+" : "") + nativeValue.ToString("0.#") + "dB";
                    if (m.id == "filter.env")
                        return (nativeValue > 0f ? "+" : "") + nativeValue.ToString("0.#");
                    if (m.id == "mod.depth")
                        return (nativeValue > 0f ? "+" : "") + Mathf.RoundToInt(nativeValue * 100f) + "%";
                    if (m.id == "amp.atk" || m.id == "amp.dec" || m.id == "amp.rel2" ||
                        m.id == "filter.atk" || m.id == "filter.dec" || m.id == "filter.rel")
                        return nativeValue.ToString(nativeValue < 1f ? "0.00" : "0.0") + "s";
                }
                catch (Exception) { }
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

            // SOUND owns these native Helm writes. Keep the legacy bus in sync
            // without emitting its OnChanged event, which otherwise sends a
            // SECOND (potentially differently mapped) write for Synth/Pads.
            var bus = CurrentBus();
            if (bus != null && bus.TryGet(id, out Parameter existing) && existing != null)
                bus.SetNormalizedSilently(id, amount);

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
                else if (IsUnisonVoices(param))
                    // Unison is an INTEGER count, not a continuous knob.
                    controller.SetParameterValue(param, 1f + Mathf.RoundToInt(amount * 14f));
                else if (id == "fx.delay.sync" || id == "arp.sync")
                    controller.SetParameterValue(param, Mathf.RoundToInt(amount * 3f));
                else
                    controller.SetParameterPercent(param, amount);

                RecordLiveEdit(controller, param, amount);
                if (param == Param.kFilterCutoff)
                {
                    var motion = SelectedMotionState();
                    if (motion != null && motion.filterMotionEnabled)
                        EditFilterMotion(i => i.filterMotionCenter = amount);
                }

                // A single-voice patch has no unison spread, so DETUNE can
                // appear broken. Raise only that oscillator to 2 voices on
                // a nonzero detune edit, and persist both native parameters.
                // Do not touch the other oscillator or an existing 2+ stack.
                if (amount > .0005f && (param == Param.kOsc1UnisonDetune ||
                    param == Param.kOsc2UnisonDetune))
                {
                    Param voices = param == Param.kOsc1UnisonDetune
                        ? Param.kOsc1UnisonVoices : Param.kOsc2UnisonVoices;
                    if (NativeVoiceCount(controller, voices) < 2)
                    {
                        controller.SetParameterValue(voices, 2f);
                        float voicePercent = 1f / 14f;
                        RecordLiveEdit(controller, voices, voicePercent);
                        string voiceId = param == Param.kOsc1UnisonDetune ? "osc1.uni" : "osc2.uni";
                        if (bus != null && bus.TryGet(voiceId, out Parameter storedVoices) && storedVoices != null)
                            bus.SetNormalizedSilently(voiceId, voicePercent);
                        foreach (var voiceKnob in macros)
                            if (voiceKnob.param == voices) voiceKnob.lastDisplay = -999;
                    }
                }
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
                tile.style.height = 136;
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
                var label = T(FormatKnobLabel(macro.label), 14, Ink);
                label.style.unityFontStyleAndWeight = FontStyle.Bold;
                label.style.unityTextAlign = TextAnchor.MiddleCenter;
                label.style.whiteSpace = WhiteSpace.Normal;
                label.style.marginTop = 6;
                // Reserve the same centered two-line caption area for all knobs.
                label.style.height = 42;
                label.style.flexShrink = 0;
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
            if (acidDialDragging || Time.unscaledTime < acidScrollLockUntil)
                LockAcidScroll();
            if (Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + .25f;
            Refresh();
        }
    }
}
