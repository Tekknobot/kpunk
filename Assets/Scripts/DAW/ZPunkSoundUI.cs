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
    /// Touch-first instrument macros. Each knob captures its pointer and changes
    /// relative to the initial value; a knob gesture never scrolls the sound page.
    /// </summary>
    public sealed class ZPunkSoundUI : MonoBehaviour
    {
        private static readonly Color Bg = new Color(.073f, .101f, .146f);
        private static readonly Color Tile = new Color(.105f, .145f, .207f);
        private static readonly Color Ink = new Color(.91f, .94f, .98f);
        private static readonly Color Dim = new Color(.57f, .66f, .76f);
        private static readonly Color Aqua = new Color(.30f, .87f, .75f);
        private static readonly Color Gold = new Color(.99f, .72f, .35f);
        private static readonly Color Blue = new Color(.43f, .68f, .99f);
        private static readonly Color Pink = new Color(.97f, .48f, .61f);
        private VisualElement root, modules, trackBar;
        private ScrollView pageScroll;
        private Label subhead, presetName, instrumentStatus;
        private readonly List<Macro> macros = new List<Macro>();
        private readonly Dictionary<TrackKind, Button> selectors = new Dictionary<TrackKind, Button>();
        private readonly Dictionary<VisualElement, Macro> knobTargets = new Dictionary<VisualElement, Macro>();
        private KMusicApp app;
        private KMusicPadSynthRuntime padRuntime;
        private KMusicHelmPresetPickerUI mainPicker;
        private ZPunkInstrumentRack rack;
        private TrackKind selected = TrackKind.Synth;
        private float nextRefresh;

        private Macro dragging;
        private int dragPointer = -1;
        private float dragY, dragStart, scrollLockUntil;
        private Vector2 lockedScroll;

        private sealed class Macro
        {
            public string id;
            public string label;
            public Param param;
            public VisualElement marker;
            public Label value;
            public float normalized;
            public int lastPercent = -1;
        }

        private static Label T(string value, int size, Color color)
        {
            var l = new Label(value);
            l.style.fontSize = Mathf.Max(12, size);
            l.style.color = color;
            l.style.unityTextAlign = TextAnchor.MiddleLeft;
            l.pickingMode = PickingMode.Ignore;
            return l;
        }
        private static void Corners(VisualElement e, float r)
        {
            e.style.borderTopLeftRadius = r;
            e.style.borderTopRightRadius = r;
            e.style.borderBottomLeftRadius = r;
            e.style.borderBottomRightRadius = r;
        }
        private static Button Action(string name, Action click)
        {
            var b = new Button(click) { text = name };
            b.style.height = 46;
            b.style.minWidth = 46;
            b.style.flexShrink = 0;
            b.style.fontSize = 14;
            b.style.color = Ink;
            b.style.backgroundColor = Tile;
            Corners(b, 5);
            return b;
        }
        public void Initialize(VisualElement host, VisualElement ignoredLegacyTree)
        {
            root = host;
            if (root == null) return;
            // Register in trickle-down, ahead of ScrollView's own touch handling.
            // This prevents the scroller from stealing a knob swipe on phones.
            root.UnregisterCallback<PointerDownEvent>(OnKnobDown, TrickleDown.TrickleDown);
            root.UnregisterCallback<PointerMoveEvent>(OnKnobMove, TrickleDown.TrickleDown);
            root.UnregisterCallback<PointerUpEvent>(OnKnobUp, TrickleDown.TrickleDown);
            root.UnregisterCallback<PointerCancelEvent>(OnKnobCancel, TrickleDown.TrickleDown);
            root.UnregisterCallback<PointerCaptureOutEvent>(OnKnobCaptureLost);
            root.Clear(); macros.Clear(); selectors.Clear(); knobTargets.Clear();
            root.RegisterCallback<PointerDownEvent>(OnKnobDown, TrickleDown.TrickleDown);
            root.RegisterCallback<PointerMoveEvent>(OnKnobMove, TrickleDown.TrickleDown);
            root.RegisterCallback<PointerUpEvent>(OnKnobUp, TrickleDown.TrickleDown);
            root.RegisterCallback<PointerCancelEvent>(OnKnobCancel, TrickleDown.TrickleDown);
            root.RegisterCallback<PointerCaptureOutEvent>(OnKnobCaptureLost);
            dragging = null; dragPointer = -1;
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
            trackBar.style.height = 52;
            trackBar.style.flexShrink = 0;
            trackBar.style.marginBottom = 9;
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
            presetCard.style.height = 61;
            presetCard.style.flexShrink = 0;
            presetCard.style.paddingLeft = 8;
            presetCard.style.paddingRight = 8;
            presetCard.style.backgroundColor = Tile;
            Corners(presetCard, 6);
            presetCard.style.marginBottom = 8;
            var prev = Action("<", () => ChangePreset(-1));
            prev.style.width = 46; prev.style.marginRight = 5;
            presetCard.Add(prev);
            presetName = T("LOADING PRESET", 12, Ink);
            presetName.style.flexGrow = 1;
            presetName.style.minWidth = 0;
            presetName.style.unityTextAlign = TextAnchor.MiddleCenter;
            presetName.style.whiteSpace = WhiteSpace.Normal;
            presetCard.Add(presetName);
            var next = Action(">", () => ChangePreset(1));
            next.style.width = 46; next.style.marginLeft = 5;
            presetCard.Add(next);
            root.Add(presetCard);

            instrumentStatus = T("", 10, Dim);
            instrumentStatus.style.marginBottom = 7;
            root.Add(instrumentStatus);
            pageScroll = new ScrollView(ScrollViewMode.Vertical);
            pageScroll.style.flexGrow = 1;
            pageScroll.style.minHeight = 0;
            pageScroll.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            pageScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            modules = pageScroll.contentContainer;
            root.Add(pageScroll);

            BuildModule("FILTER", Gold, new[] {
                new Macro { id="filter.cutoff", label="CUTOFF", param=Param.kFilterCutoff },
                new Macro { id="filter.res", label="RESONANCE", param=Param.kResonance },
                new Macro { id="filter.drive", label="DRIVE", param=Param.kFilterDrive }
            });
            BuildModule("ENVELOPE", Blue, new[] {
                new Macro { id="amp.atk", label="ATTACK", param=Param.kAmplitudeAttack },
                new Macro { id="amp.dec", label="DECAY", param=Param.kAmplitudeDecay },
                new Macro { id="amp.sus", label="SUSTAIN", param=Param.kAmplitudeSustain },
                new Macro { id="amp.rel2", label="RELEASE", param=Param.kAmplitudeRelease }
            });
            BuildModule("TONE", Pink, new[] {
                new Macro { id="osc1.vol", label="OSC 1", param=Param.kOsc1Volume },
                new Macro { id="osc2.vol", label="OSC 2", param=Param.kOsc2Volume },
                new Macro { id="sub.vol", label="SUB", param=Param.kSubVolume },
                new Macro { id="master.porta", label="PORTAMENTO", param=Param.kPortamento }
            });
            BuildModule("MOVEMENT", Aqua, new[] {
                new Macro { id="mod.rate", label="LFO RATE", param=Param.kMonoLfo1Frequency },
                new Macro { id="mod.depth", label="LFO DEPTH", param=Param.kMonoLfo1Amplitude },
                new Macro { id="fx.dist.drive", label="DISTORTION", param=Param.kDistortionDrive },
                new Macro { id="master.vol", label="LEVEL", param=Param.kVolume }
            });
            var note = T("Drag knobs up or down to shape the selected instrument.", 10, Dim);
            note.style.whiteSpace = WhiteSpace.Normal;
            note.style.marginBottom = 14;
            modules.Add(note);
            OnShown();
        }
        private void FindServices()
        {
            if (app == null) app = FindObjectOfType<KMusicApp>();
            if (padRuntime == null) padRuntime = FindObjectOfType<KMusicPadSynthRuntime>();
            if (mainPicker == null) mainPicker = FindObjectOfType<KMusicHelmPresetPickerUI>();
            if (rack == null) rack = FindObjectOfType<ZPunkInstrumentRack>();
        }
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
            selected = track;
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
            foreach (var item in selectors)
            {
                bool active = item.Key == selected;
                item.Value.style.backgroundColor = active ? Aqua : Tile;
                item.Value.style.color = active ? new Color(.04f, .09f, .12f) : Ink;
            }
            if (subhead != null)
                subhead.text = selected.ToString().ToUpperInvariant() + "  /  AUDIO HELM PRESET SHAPER";
            // A changed track/preset must redraw every indicator, even if the
            // corresponding parameter happens to have the same rounded percent.
            foreach (var m in macros) m.lastPercent = -1;
            Refresh();
        }
        private void Refresh()
        {
            var controller = Controller();
            string state = controller == null ?
                "INSTRUMENT INITIALIZING - RETRY IN A MOMENT" :
                "CHANNEL " + controller.channel + "  /  INDEPENDENT SYNTH ENGINE";
            if (instrumentStatus != null && instrumentStatus.text != state) instrumentStatus.text = state;
            string name = "NO PRESET";
            if (selected == TrackKind.Bass && rack != null) name = rack.BassPresetName;
            else if (selected == TrackKind.Synth && mainPicker != null)
                name = DisplayPath(mainPicker.CurrentPresetRelPath);
            else if (selected == TrackKind.Pads && padRuntime != null)
                name = padRuntime.PresetCount > 0
                    ? DisplayPath(padRuntime.CurrentPresetRelPath) + "  (" + (padRuntime.CurrentPresetIndex + 1) + "/" + padRuntime.PresetCount + ")"
                    : "PAD PRESETS UNAVAILABLE";
            if (presetName != null && presetName.text != name) presetName.text = name;
            var bus = CurrentBus();
            foreach (var m in macros)
            {
                if (dragging == m) continue;
                float amount = 0f;
                bool okay = false;
                if (bus != null && bus.TryGet(m.id, out Parameter parameter) && parameter != null)
                {
                    amount = Mathf.Clamp01(parameter.Normalized);
                    okay = true;
                }
                if (!okay && controller != null)
                {
                    try { amount = Mathf.Clamp01(controller.GetParameterPercent(m.param)); okay = true; }
                    catch (Exception) { }
                }
                int shown = okay ? Mathf.RoundToInt(amount * 100f) : -1;
                m.normalized = amount;
                if (shown == m.lastPercent) continue;
                m.lastPercent = shown;
                if (m.value != null) m.value.text = shown >= 0 ? shown + "%" : "--";
                PositionMarker(m, amount);
            }
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
        private void SetMacro(Macro m, float amount)
        {
            amount = Mathf.Clamp01(amount);
            var bus = CurrentBus();
            var controller = Controller();
            bool written = false;
            if (bus != null && bus.TryGet(m.id, out Parameter parameter) && parameter != null)
            {
                bus.SetNormalized(m.id, amount);
                written = true;
            }
            if (!written && controller != null)
            {
                try { controller.SetParameterPercent(m.param, amount); } catch (Exception) { }
            }
            m.normalized = amount;
            m.lastPercent = Mathf.RoundToInt(amount * 100f);
            if (m.value != null) m.value.text = m.lastPercent + "%";
            PositionMarker(m, amount);
        }
        // The marker travels over 270 degrees; the knob has no reliance on
        // platform textures, special font glyphs or custom shaders.
        private static void PositionMarker(Macro m, float amount)
        {
            if (m.marker == null) return;
            float angle = (135f + Mathf.Clamp01(amount) * 270f) * Mathf.Deg2Rad;
            m.marker.style.left = 35f + Mathf.Cos(angle) * 26f;
            m.marker.style.top = 35f + Mathf.Sin(angle) * 26f;
        }
        private void LockScroll()
        {
            if (pageScroll == null) return;
            pageScroll.scrollOffset = lockedScroll;
        }
        private void EndDrag(int pointer)
        {
            if (dragPointer != pointer) return;
            dragging = null;
            dragPointer = -1;
            scrollLockUntil = Time.unscaledTime + .25f;
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
            if (macro == null) return; // Let empty space scroll naturally.
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
        private void BuildModule(string heading, Color color, Macro[] controls)
        {
            var panel = new VisualElement();
            Corners(panel, 7);
            panel.style.backgroundColor = Tile;
            panel.style.marginBottom = 9;
            panel.style.paddingLeft = 10;
            panel.style.paddingRight = 10;
            panel.style.paddingTop = 10;
            panel.style.paddingBottom = 8;
            var cap = T(heading, 12, color);
            cap.style.marginBottom = 8;
            cap.style.unityFontStyleAndWeight = FontStyle.Bold;
            panel.Add(cap);
            var grid = new VisualElement();
            grid.style.flexDirection = FlexDirection.Row;
            grid.style.flexWrap = Wrap.Wrap;
            grid.style.width = new Length(100, LengthUnit.Percent);
            panel.Add(grid);
            var tiles = new List<VisualElement>();
            foreach (var macro in controls)
            {
                var tile = new VisualElement();
                tile.style.width = new Length(50, LengthUnit.Percent);
                tile.style.height = 136;
                tile.style.alignItems = Align.Center;
                tile.style.justifyContent = Justify.Center;
                tile.style.paddingBottom = 7;
                tile.pickingMode = PickingMode.Position;
                var face = new VisualElement();
                face.style.position = Position.Relative;
                face.style.width = 78;
                face.style.height = 78;
                face.style.alignItems = Align.Center;
                face.style.justifyContent = Justify.Center;
                face.style.backgroundColor = new Color(.065f, .092f, .133f);
                Corners(face, 39);
                face.style.borderLeftWidth = 4; face.style.borderTopWidth = 4;
                face.style.borderRightWidth = 4; face.style.borderBottomWidth = 4;
                var rim = new Color(.24f, .32f, .39f);
                face.style.borderLeftColor = rim; face.style.borderTopColor = rim;
                face.style.borderRightColor = rim; face.style.borderBottomColor = rim;
                face.pickingMode = PickingMode.Position;
                var mid = T("", 13, Ink);
                mid.style.unityTextAlign = TextAnchor.MiddleCenter;
                mid.style.unityFontStyleAndWeight = FontStyle.Bold;
                macro.value = mid;
                face.Add(mid);
                var marker = new VisualElement();
                marker.style.position = Position.Absolute;
                marker.style.width = 8; marker.style.height = 8;
                marker.style.backgroundColor = color;
                Corners(marker, 4);
                marker.pickingMode = PickingMode.Ignore;
                macro.marker = marker;
                face.Add(marker);
                PositionMarker(macro, 0f);
                tile.Add(face);
                var label = T(macro.label, 11, Dim);
                label.style.unityTextAlign = TextAnchor.MiddleCenter;
                label.style.marginTop = 8;
                label.style.width = new Length(100, LengthUnit.Percent);
                tile.Add(label);
                knobTargets[face] = macro;
                grid.Add(tile);
                tiles.Add(tile);
                macros.Add(macro);
            }
            grid.RegisterCallback<GeometryChangedEvent>(evt =>
            {
                float width = evt.newRect.width;
                if (width < 1f) return;
                int cols = width >= 480f ? controls.Length : 2;
                float percent = 100f / Mathf.Max(1, cols);
                foreach (var tile in tiles) tile.style.width = new Length(percent, LengthUnit.Percent);
            });
            modules.Add(panel);
        }
        private void Update()
        {
            if (root == null || root.resolvedStyle.display == DisplayStyle.None) return;
            if (dragging != null || Time.unscaledTime < scrollLockUntil) LockScroll();
            if (Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + .25f;
            Refresh();
        }
    }
}
