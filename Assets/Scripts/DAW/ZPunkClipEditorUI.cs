using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace ZPunk.DAW
{
    /// <summary>
    /// Clip-native step grid / piano roll. All edits go to SongData events, not the
    /// old 16-step groovebox buffers. Playback uses the next Play snapshot.
    /// </summary>
    public sealed class ZPunkClipEditorUI : MonoBehaviour
    {
        private static readonly Color Ink = new Color(.91f,.94f,.98f);
        private static readonly Color Muted = new Color(.55f,.64f,.75f);
        private static readonly Color Surface = new Color(.075f,.105f,.15f);
        private static readonly Color Raised = new Color(.13f,.18f,.25f);
        private static readonly Color Accent = new Color(.31f,.88f,.74f);
        private readonly Color[] colors = {
            new Color(.98f,.66f,.28f), new Color(.96f,.36f,.48f),
            new Color(.35f,.60f,.98f), new Color(.66f,.45f,.94f), new Color(.24f,.82f,.69f)
        };
        private VisualElement root, canvas, gridScrollContainer, chips, labelViewport, labelColumn;
        // Note hit targets sit ABOVE (not inside) the ScrollView. This prevents
        // ScrollView's touch pan manipulator from taking ownership of note drags.
        private VisualElement noteTouchLayer;
        private readonly List<MidiNoteTouchTarget> midiNoteTouchTargets = new List<MidiNoteTouchTarget>();
        // This ruler is outside the note ScrollView, so vertical swipes never hide the bar positions.
        private VisualElement rulerGutter, rulerContent, rulerPlayhead;
        private readonly List<VisualElement> pitchRowBackgrounds = new List<VisualElement>();
        private readonly List<Label> pitchRowLabels = new List<Label>();
        private bool sampleKeyGuideEnabled;
        private int sampleKeyGuideTonic;
        private bool sampleKeyGuideMinor;
        private string sampleKeyGuideSampleId;
        private int sampleKeyGuideProjectSlot = -1;
        private VisualElement generatorPanel, generatorGenres, generatorStyles, generatorBarsRow, generatorSynthModeRow;
        private VisualElement generatorHistoryRows;
        private Label generatorHistoryLabel;
        private Button generatorQuickButton, generatorMelodyButton, generatorChordsButton;
        private Label generatorHeading, generatorSummary, generatorHint, generatorKeyLabel, generatorVariationLabel;
        private Label generatorProgressionLabel, generatorProgressionChords;
        private VisualElement generatorProgressionSection;
        private int generatorGenre = 1, generatorStyle, generatorBars = 2, generatorKey = 9, generatorVariation;
        private bool generatorMinor = true, generatorSynthChords;
        private int generatorTargetTrack;
        private VisualElement editorPlayhead;
        private VisualElement samplerPage, samplerWave;
        private ScrollView samplerScroll;
        private Label samplerSelectedMarker, samplerHitSliceStatus, samplerBpmLabel, samplerHitProgressLabel, samplerTapBpmStatus;
        private VisualElement samplerHitProgressTrack, samplerHitProgressFill;
        private Button samplerGridButton;
        private Button samplerSnapButton;
        private readonly List<Button> samplerChopPads = new List<Button>();
        private readonly List<VisualElement> samplerChopRangeBars = new List<VisualElement>();
        private Label samplerTitle, samplerWarpLabel, samplerLibraryLabel, samplerPitchLabel;
        private Label samplerSourceKeyLabel, samplerPitchedKeyLabel, generatorSampleKeyHint, clipSampleKeyReference;
        private Button generatorSampleKeyButton;
        private Button samplerWarpButton;
        private VisualElement samplerWarpProgressTrack, samplerWarpProgressFill;
        private Label samplerWarpProgressLabel;
        private bool showingSampler;
        private KMusicPlayerUI samplePlayer;
        private ZPunkSongTransport songTransport;
        private ScrollView scroller;
        private Label title, info, selectedInfo;
        private VisualElement velocityFill, velocityThumb;
        private readonly List<VelocityDisplay> velocityDisplays = new List<VelocityDisplay>();
        private VisualElement kitSelector;
        private Label kitName;
        private KMusicDrumSequencer drumEngine;
        private ScrollView trackScroller;
        private KMusicProjectManager projects;
        private SongData song;
        private ClipData clip;
        private ClipContentData content;
        private int trackIndex;
        private string clipId;
        private int octave = 4;
        private int noteFocusRevision;
        private long quantum = SongData.TicksPerSixteenth;
        private int selectedNote = -1;
        private int selectedEventRow = -1;
        private long selectedEventTick = -1;
        private int velocity = 110;
        private const float StepW = 32f;
        private const float RowH = 36f;
        // Render every MIDI pitch (C-1 through G9). The user scrolls the same
        // continuous 128-note keyboard; octave controls only move the viewport.
        private const int MidiRows = 128;
        private int steps;
        private bool ready;
        private float nextPlayheadPaint;
        private float lastPlayheadX = -1f;

        // MIDI notes use two unambiguous gestures: drag the note body to move
        // its position (snapped in time and pitch); drag ONLY the trailing/right
        // handle to resize its duration. There is no leading/left-edge resize.
        // Empty piano-roll space remains scrollable.
        private enum MidiDragMode { Move, ResizeEnd }
        private const float MidiGestureThreshold = 5f;
        private const float MidiEdgeScrollZone = 34f;
        private const float MidiMaxEdgeScrollPerTick = 11f; // 16 ms scheduler tick
        private sealed class Drag
        {
            public int index;
            public Vector2 start;
            public Vector2 latestWorldPosition;
            public Vector2 allowedScrollOffset;
            public long startTick;
            public long duration;
            public int pitch;
            public MidiDragMode mode;
            public bool moved;
            public int pointerId;
            public VisualElement preview;
            public VelocityDisplay display;
            public IVisualElementScheduledItem edgeScroll;
            public long candidateTick;
            public long candidateDuration;
            public int candidatePitch;
        }
        private Drag drag;
        private sealed class MidiNoteTouchTarget
        {
            public VisualElement body;
            public int noteIndex;
        }
        private sealed class Tap
        {
            public int pointerId;
            public Vector2 start;
            public int row;
            public int step;
            public bool handled;
            public IVisualElementScheduledItem hold;
        }
        private sealed class VelocityDisplay
        {
            public VisualElement cell;
            public VisualElement meter;
            public Label number;
            public int row;
            public int noteIndex;
            public long tick;
            public float width;
            public int velocity;
        }
        private Tap pendingTap;
        public Action OnClipChanged;

        public void Initialize(VisualElement host)
        {
            if (host == null) return;
            root = host;
            projects = FindObjectOfType<KMusicProjectManager>();
            songTransport = FindObjectOfType<ZPunkSongTransport>();
            BuildShell();
            ready = true;
        }

        private static Label Txt(string str, int size=12, Color? color=null)
        {
            var label = new Label(str);
            label.style.fontSize = Mathf.Max(12, size);
            label.style.color = color ?? Ink;
            label.style.unityTextAlign = TextAnchor.MiddleLeft;
            return label;
        }
        private static VisualElement Box(Color bg, float radius=8)
        {
            var e = new VisualElement();
            e.style.backgroundColor = bg;
            e.style.borderTopLeftRadius = radius;
            e.style.borderTopRightRadius = radius;
            e.style.borderBottomLeftRadius = radius;
            e.style.borderBottomRightRadius = radius;
            return e;
        }
        private static Button Btn(string text, Action action, int width=48)
        {
            var b = new Button(action) { text = text };
            b.style.minWidth = width;
            b.style.flexShrink = 0;
            b.style.flexGrow = 0;
            b.style.height = 44;
            b.style.marginRight = 5;
            b.style.marginBottom = 1;
            b.style.whiteSpace = WhiteSpace.NoWrap;
            b.style.paddingLeft = 9;
            b.style.paddingRight = 9;
            b.style.backgroundColor = Raised;
            b.style.color = Ink;
            b.style.borderTopLeftRadius = 7;
            b.style.borderTopRightRadius = 7;
            b.style.borderBottomLeftRadius = 7;
            b.style.borderBottomRightRadius = 7;
            b.style.fontSize = 13;
            b.style.unityFontStyleAndWeight = FontStyle.Bold;
            return b;
        }
        private static Button EqualButton(string label, Action action)
        {
            var button = Btn(label, action, 0);
            button.style.flexGrow = 1;
            button.style.flexShrink = 1;
            button.style.flexBasis = new Length(0, LengthUnit.Pixel);
            button.style.minWidth = 0;
            button.style.marginLeft = 2;
            button.style.marginRight = 2;
            button.style.paddingLeft = 2;
            button.style.paddingRight = 2;
            button.style.fontSize = 11;
            return button;
        }

        private void BuildShell()
        {
            root.Clear();
            root.style.flexDirection = FlexDirection.Column;
            root.style.position = Position.Relative;
            root.style.flexGrow = 1;
            root.style.minHeight = 0;
            root.style.paddingLeft = 9;
            root.style.paddingRight = 9;
            root.style.paddingTop = 8;
            root.style.backgroundColor = Surface;

            var heading = new VisualElement { name = "ZPunkEditorHeading" };
            heading.style.flexDirection = FlexDirection.Row;
            heading.style.alignItems = Align.Center;
            heading.style.justifyContent = Justify.SpaceBetween;
            title = Txt("CLIP EDITOR", 16);
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.flexShrink = 1;
            title.style.minWidth = 0;
            title.style.whiteSpace = WhiteSpace.NoWrap;
            title.style.overflow = Overflow.Hidden;
            title.style.textOverflow = TextOverflow.Ellipsis;
            heading.style.height = 50;
            heading.style.flexShrink = 0;
            heading.Add(title);
            heading.Add(Btn("+ NEW CLIP", NewClip, 98));
            root.Add(heading);

            var tracks = new ScrollView(ScrollViewMode.Horizontal);
            trackScroller = tracks;
            tracks.style.height = 55;
            tracks.style.flexShrink = 0;
            tracks.style.marginTop = 6;
            tracks.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            tracks.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            chips = tracks.contentContainer;
            chips.style.flexDirection = FlexDirection.Row;
            chips.style.flexWrap = Wrap.NoWrap;
            chips.style.width = new Length(100, LengthUnit.Percent);
            chips.style.flexGrow = 1;
            root.Add(tracks);

            // Reserve this one toolbar row for every track: switching Drums/Bass/etc
            // must not shift the piano roll or the scroll viewport vertically.
            kitSelector = new VisualElement { name = "ZPunkDrumKitSelector" };
            kitSelector.style.height = 50;
            kitSelector.style.minHeight = 50;
            kitSelector.style.maxHeight = 50;
            kitSelector.style.flexShrink = 0;
            kitSelector.style.flexDirection = FlexDirection.Row;
            kitSelector.style.alignItems = Align.Center;
            kitSelector.style.marginTop = 2;
            var kitCaption = Txt("DRUM KIT",10,Muted);
            kitCaption.style.width = 72;
            kitCaption.style.flexShrink = 0;
            kitSelector.Add(kitCaption);
            kitSelector.Add(Btn("<", () => StepDrumKit(-1), 40));
            kitName = Txt("LOADING",11,Ink);
            kitName.style.flexGrow = 1;
            kitName.style.minWidth = 0;
            kitName.style.whiteSpace = WhiteSpace.NoWrap;
            kitName.style.unityTextAlign = TextAnchor.MiddleCenter;
            kitSelector.Add(kitName);
            kitSelector.Add(Btn(">", () => StepDrumKit(1), 40));
            root.Add(kitSelector);

            // Six equally sized controls fit a portrait phone on ONE row.
            // Never make the last buttons inaccessible through a hidden scroller.
            var controls = new VisualElement { name = "ZPunkNoteCommands" };
            controls.style.flexDirection = FlexDirection.Row;
            controls.style.flexWrap = Wrap.NoWrap;
            controls.style.alignItems = Align.Center;
            controls.style.width = new Length(100, LengthUnit.Percent);
            controls.style.height = 54;
            controls.style.flexShrink = 0;
            controls.style.marginTop = 2;
            controls.Add(EqualButton("OCT -", () => { octave = Mathf.Max(-1,octave-1); ScrollToOctave(); }));
            controls.Add(EqualButton("OCT +", () => { octave = Mathf.Min(9,octave+1); ScrollToOctave(); }));
            controls.Add(EqualButton("LEN -", () => ChangeLength(-1)));
            controls.Add(EqualButton("LEN +", () => ChangeLength(1)));
            controls.Add(EqualButton("DEL", DeleteSelectedNote));
            generatorQuickButton = EqualButton("GEN", () => OpenGenerator(trackIndex));
            controls.Add(generatorQuickButton);
            root.Add(controls);

            info = Txt("Choose a track, then create or select a clip.",11,Muted);
            info.style.height = 28;
            info.style.flexShrink = 0;
            root.Add(info);
            clipSampleKeyReference = Txt("", 11, Accent);
            clipSampleKeyReference.style.height = 19;
            clipSampleKeyReference.style.flexShrink = 0;
            clipSampleKeyReference.style.display = DisplayStyle.None;
            root.Add(clipSampleKeyReference);

            // The names stay on screen while the note grid scrolls horizontally.
            // Both areas share the same vertical scroll position.
            var editorViewport = new VisualElement { name = "ZPunkPianoRollViewport" };
            editorViewport.style.flexDirection = FlexDirection.Row;
            editorViewport.style.flexGrow = 1;
            editorViewport.style.minHeight = 125;
            editorViewport.style.maxHeight = 655;
            editorViewport.style.minWidth = 0;
            editorViewport.style.overflow = Overflow.Hidden;
            editorViewport.style.backgroundColor = new Color(.047f,.068f,.105f);
            editorViewport.style.borderTopLeftRadius = 8;
            editorViewport.style.borderTopRightRadius = 8;
            labelViewport = new VisualElement { name = "ZPunkPianoRollLabels" };
            labelViewport.style.position = Position.Relative;
            labelViewport.style.flexShrink = 0;
            labelViewport.style.width = 57;
            labelViewport.style.overflow = Overflow.Hidden;
            labelViewport.style.backgroundColor = new Color(.095f,.13f,.18f);
            editorViewport.Add(labelViewport);

            scroller = new ScrollView(ScrollViewMode.VerticalAndHorizontal) { name = "ZPunkNoteScroll" };
            scroller.style.flexGrow = 1;
            scroller.style.minWidth = 0;
            scroller.style.minHeight = 0;
            scroller.style.backgroundColor = new Color(.047f,.068f,.105f);
            scroller.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            scroller.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            gridScrollContainer = new VisualElement();
            gridScrollContainer.style.flexDirection = FlexDirection.Row;
            scroller.Add(gridScrollContainer);
            scroller.verticalScroller.valueChanged += _ => { SyncPianoRollLabels(); SyncMidiNoteTouchTargets(); };
            scroller.horizontalScroller.valueChanged += _ => { SyncFixedRuler(); SyncMidiNoteTouchTargets(); };
            editorViewport.Add(scroller);

            // Fixed top ruler: identical on DRUMS, CHOPS and the 128-note piano roll.
            // Only the ruler's inner content translates when the grid moves horizontally.
            var fixedRuler = new VisualElement { name = "ZPunkPinnedBarRuler" };
            fixedRuler.style.position = Position.Absolute;
            fixedRuler.style.top = 0;
            fixedRuler.style.left = 0;
            fixedRuler.style.right = 0;
            fixedRuler.style.height = 25;
            fixedRuler.style.flexDirection = FlexDirection.Row;
            fixedRuler.style.backgroundColor = new Color(.065f, .092f, .13f);
            fixedRuler.pickingMode = PickingMode.Ignore;

            rulerGutter = new VisualElement { name = "ZPunkRulerGutter" };
            rulerGutter.style.width = 57;
            rulerGutter.style.flexShrink = 0;
            rulerGutter.style.backgroundColor = new Color(.095f, .13f, .18f);
            rulerGutter.pickingMode = PickingMode.Ignore;
            fixedRuler.Add(rulerGutter);

            var rulerViewport = new VisualElement { name = "ZPunkRulerViewport" };
            rulerViewport.style.flexGrow = 1;
            rulerViewport.style.minWidth = 0;
            rulerViewport.style.overflow = Overflow.Hidden;
            rulerViewport.pickingMode = PickingMode.Ignore;
            rulerContent = new VisualElement { name = "ZPunkRulerTicks" };
            rulerContent.style.position = Position.Relative;
            rulerContent.style.flexShrink = 0;
            rulerContent.style.height = 25;
            rulerContent.pickingMode = PickingMode.Ignore;
            rulerViewport.Add(rulerContent);
            fixedRuler.Add(rulerViewport);
            editorViewport.Add(fixedRuler);

            // This transparent sibling of the ScrollView is only pickable at
            // existing MIDI notes. Empty spaces still reach the ScrollView for
            // normal scrolling and tap-to-add editing.
            noteTouchLayer = new VisualElement { name = "ZPunkMidiNoteTouchOverlay" };
            noteTouchLayer.style.position = Position.Absolute;
            noteTouchLayer.style.left = 57;
            noteTouchLayer.style.right = 0;
            noteTouchLayer.style.top = 25;
            noteTouchLayer.style.bottom = 0;
            noteTouchLayer.style.overflow = Overflow.Hidden;
            noteTouchLayer.pickingMode = PickingMode.Ignore;
            noteTouchLayer.style.display = DisplayStyle.None;
            editorViewport.Add(noteTouchLayer);
            root.Add(editorViewport);

            var velocityRow = new VisualElement();
            velocityRow.style.flexDirection = FlexDirection.Row;
            velocityRow.style.alignItems = Align.Center;
            velocityRow.style.marginTop = 9;
            var caption = Txt("VELOCITY",10,Muted);
            caption.style.width = 75;
            velocityRow.Add(caption);
            var velRail = new VisualElement { name = "ZPunkVelocity" };
            velRail.style.flexGrow = 1;
            velRail.style.height = 36;
            velRail.style.position = Position.Relative;
            velRail.style.justifyContent = Justify.Center;
            var velBase = Box(new Color(.055f,.075f,.12f),5);
            velBase.style.height = 8;
            velBase.pickingMode=PickingMode.Ignore;
            velRail.Add(velBase);
            var velFill = Box(Accent,5);
            velFill.style.position = Position.Absolute;
            velFill.style.left = 0;
            velFill.style.top = 14;
            velFill.style.height = 8;
            velFill.style.width = new Length(velocity/127f*100,LengthUnit.Percent);
            velFill.pickingMode=PickingMode.Ignore;
            velRail.Add(velFill);
            var velThumb = Box(Ink,6);
            velThumb.style.position=Position.Absolute;
            velThumb.style.width=13;
            velThumb.style.height=22;
            velThumb.style.top=7;
            velThumb.style.left=new Length(velocity/127f*100,LengthUnit.Percent);
            velThumb.pickingMode=PickingMode.Ignore;
            velRail.Add(velThumb);
            velocityFill = velFill;
            velocityThumb = velThumb;
            Action<Vector2> setVelocity=(position)=>
            {
                float normalized=Mathf.Clamp01(velRail.WorldToLocal(position).x/Mathf.Max(1,velRail.contentRect.width));
                int next = Mathf.Clamp(Mathf.RoundToInt(1+126*normalized),1,127);
                if (next == velocity) return;
                velocity = next;
                RefreshVelocityRail();
                ChangeVelocity();
            };
            velRail.RegisterCallback<PointerDownEvent>(evt=>
            {
                if(evt.button!=0)return;
                velRail.CapturePointer(evt.pointerId);
                setVelocity(evt.position);
                evt.StopPropagation();
            });
            velRail.RegisterCallback<PointerMoveEvent>(evt=>
            {
                if(!velRail.HasPointerCapture(evt.pointerId))return;
                setVelocity(evt.position);
                evt.StopPropagation();
            });
            velRail.RegisterCallback<PointerUpEvent>(evt=>
            {
                if(!velRail.HasPointerCapture(evt.pointerId))return;
                setVelocity(evt.position);
                velRail.ReleasePointer(evt.pointerId);
                evt.StopPropagation();
            });
            velocityRow.Add(velRail);
            selectedInfo = Txt("110",11,Accent);
            selectedInfo.style.width = 43;
            selectedInfo.style.unityTextAlign = TextAnchor.MiddleRight;
            velocityRow.Add(selectedInfo);
            root.Add(velocityRow);
            var footer = Txt("TAP: add/remove  |  DRAG NOTE: move steps / pitch  |  RIGHT END: resize  |  EDGE: scroll",10,Muted);
            footer.style.whiteSpace = WhiteSpace.Normal;
            footer.style.marginTop = 5;
            footer.style.marginBottom = 8;
            root.Add(footer);
            BuildSamplerPage();
            BuildGeneratorPanel();
        }

        private void BuildSamplerPage()
        {
            // One vertical scroll surface: no horizontally clipped transport or
            // nested scroll areas that steal pad and waveform drag gestures.
            samplerScroll = new ScrollView(ScrollViewMode.Vertical) { name = "ZPunkSamplerPage" };
            samplerScroll.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            samplerScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            samplerScroll.contentContainer.style.flexDirection = FlexDirection.Column;
            samplerScroll.contentContainer.style.paddingBottom = 20;
            samplerPage = samplerScroll;
            samplerPage.style.display = DisplayStyle.None;
            samplerPage.style.flexDirection = FlexDirection.Column;
            samplerPage.style.flexGrow = 1;
            samplerPage.style.flexShrink = 1;
            samplerPage.style.minHeight = 0;
            samplerPage.style.paddingTop = 9;

            var headingRow = new VisualElement();
            headingRow.style.flexDirection = FlexDirection.Row;
            headingRow.style.alignItems = Align.Center;
            headingRow.style.justifyContent = Justify.SpaceBetween;
            headingRow.style.height = 43;
            var heading = Txt("SAMPLER / SLICE LAB", 16, Ink);
            heading.style.unityFontStyleAndWeight = FontStyle.Bold;
            heading.style.flexShrink = 1;
            headingRow.Add(heading);
            heading.style.flexGrow = 1;
            heading.style.minWidth = 0;
            var back = Btn("BACK", () => ShowSampler(false), 62);
            back.style.height = 38;
            back.style.marginRight = 6;
            headingRow.Add(back);
            var browse = Btn("BROWSE", () => { FindSampler(); samplePlayer?.DawOpenBrowser(); }, 78);
            browse.style.height = 38;
            browse.style.marginRight = 0;
            headingRow.Add(browse);
            samplerPage.Add(headingRow);

            var sampleRow = new VisualElement();
            sampleRow.style.flexDirection = FlexDirection.Row;
            sampleRow.style.alignItems = Align.Center;
            sampleRow.style.height = 36;
            sampleRow.style.marginBottom = 6;
            samplerTitle = Txt("NO SAMPLE", 12, Accent);
            samplerTitle.style.flexGrow = 1;
            samplerTitle.style.flexShrink = 1;
            samplerTitle.style.minWidth = 0;
            samplerTitle.style.whiteSpace = WhiteSpace.NoWrap;
            samplerTitle.style.overflow = Overflow.Hidden;
            samplerTitle.style.textOverflow = TextOverflow.Ellipsis;
            sampleRow.Add(samplerTitle);
            samplerPage.Add(sampleRow);

            // Keep sample tempo separate from the ARRANGE tempo. The TAP and
            // half/double controls are compact enough for portrait phones.
            var tempoRow = new VisualElement();
            tempoRow.style.flexDirection = FlexDirection.Row;
            tempoRow.style.alignItems = Align.Center;
            tempoRow.style.height = 41;
            tempoRow.style.marginBottom = 2;
            samplerBpmLabel = Txt("SOURCE BPM --", 11, Accent);
            samplerBpmLabel.style.flexGrow = 1;
            samplerBpmLabel.style.flexShrink = 1;
            samplerBpmLabel.style.minWidth = 0;
            tempoRow.Add(samplerBpmLabel);
            var tapBpm = Btn("TAP", () =>
            {
                FindSampler();
                samplePlayer?.DawTapSourceBpm();
                RefreshSampler();
            }, 70);
            tapBpm.style.height = 37;
            tapBpm.style.marginRight = 4;
            tapBpm.style.backgroundColor = Accent;
            tapBpm.style.color = new Color(.035f, .09f, .105f);
            tempoRow.Add(tapBpm);
            var halfBpm = Btn("1/2", () =>
            {
                FindSampler();
                samplePlayer?.DawScaleSourceBpm(.5f);
                RefreshSampler();
            }, 48);
            halfBpm.style.height = 37;
            halfBpm.style.marginRight = 4;
            tempoRow.Add(halfBpm);
            var doubleBpm = Btn("x2", () =>
            {
                FindSampler();
                samplePlayer?.DawScaleSourceBpm(2f);
                RefreshSampler();
            }, 48);
            doubleBpm.style.height = 37;
            doubleBpm.style.marginRight = 0;
            tempoRow.Add(doubleBpm);
            samplerPage.Add(tempoRow);
            samplerTapBpmStatus = Txt("", 10, Muted);
            samplerTapBpmStatus.style.display = DisplayStyle.None;
            samplerTapBpmStatus.style.whiteSpace = WhiteSpace.Normal;
            samplerTapBpmStatus.style.marginBottom = 3;
            samplerPage.Add(samplerTapBpmStatus);

            var primaryRow = new VisualElement();
            primaryRow.style.flexDirection = FlexDirection.Row;
            primaryRow.style.alignItems = Align.Center;
            primaryRow.style.height = 53;
            primaryRow.style.marginBottom = 4;
            var sliceHits = Btn("SLICE HITS", () =>
            {
                FindSampler();
                samplePlayer?.DawSliceHits();
                RefreshSampler();
            }, 110);
            sliceHits.style.flexGrow = 1;
            sliceHits.style.flexBasis = new Length(0, LengthUnit.Pixel);
            sliceHits.style.minWidth = 0;
            sliceHits.style.height = 50;
            sliceHits.style.backgroundColor = Accent;
            sliceHits.style.color = new Color(.035f, .09f, .105f);
            sliceHits.style.fontSize = 15;
            primaryRow.Add(sliceHits);
            samplerWarpButton = Btn("WARP OFF", () =>
            {
                FindSampler();
                samplePlayer?.DawToggleAutoWarp();
                RefreshSampler();
            }, 105);
            samplerWarpButton.style.height = 50;
            samplerWarpButton.style.flexGrow = 1;
            samplerWarpButton.style.flexBasis = new Length(0, LengthUnit.Pixel);
            samplerWarpButton.style.minWidth = 0;
            samplerWarpButton.style.marginRight = 0;
            samplerWarpButton.style.fontSize = 11;
            primaryRow.Add(samplerWarpButton);
            samplerPage.Add(primaryRow);

            samplerHitSliceStatus = Txt("SET HEAD ON A MAIN HIT, THEN SLICE HITS", 10, Muted);
            samplerHitSliceStatus.style.whiteSpace = WhiteSpace.Normal;
            samplerHitSliceStatus.style.marginBottom = 3;
            samplerPage.Add(samplerHitSliceStatus);
            // Amber guide detection runs asynchronously. Show real elapsed time
            // and measured progress; ETA is explicitly approximate.
            samplerHitProgressLabel = Txt("", 10, new Color(.98f, .7f, .37f));
            samplerHitProgressLabel.style.display = DisplayStyle.None;
            samplerPage.Add(samplerHitProgressLabel);
            samplerHitProgressTrack = Box(new Color(.06f, .08f, .12f), 3);
            samplerHitProgressTrack.style.height = 4;
            samplerHitProgressTrack.style.marginBottom = 4;
            samplerHitProgressTrack.style.display = DisplayStyle.None;
            samplerHitProgressFill = new VisualElement();
            samplerHitProgressFill.style.height = Length.Percent(100);
            samplerHitProgressFill.style.backgroundColor = new Color(.98f, .67f, .28f);
            samplerHitProgressFill.style.width = Length.Percent(0);
            samplerHitProgressTrack.Add(samplerHitProgressFill);
            samplerPage.Add(samplerHitProgressTrack);

            // Transport belongs to the waveform being auditioned; the main
            // application play/stop controls above remain unchanged.
            var transport = new VisualElement();
            transport.style.flexDirection = FlexDirection.Row;
            transport.style.alignItems = Align.Center;
            transport.style.height = 47;
            var samplePlay = Btn("PLAY", () => { FindSampler(); samplePlayer?.DawPlaySample(); }, 58);
            var sampleStop = Btn("STOP", () => { FindSampler(); samplePlayer?.DawStopSample(); }, 60);
            samplePlay.style.flexGrow = 1;
            samplePlay.style.flexBasis = new Length(0, LengthUnit.Pixel);
            samplePlay.style.minWidth = 0;
            sampleStop.style.flexGrow = 1;
            sampleStop.style.flexBasis = new Length(0, LengthUnit.Pixel);
            sampleStop.style.minWidth = 0;
            samplePlay.style.backgroundColor = new Color(.12f, .33f, .31f);
            transport.Add(samplePlay);
            transport.Add(sampleStop);
            samplerSnapButton = Btn("HIT SNAP", () =>
            {
                FindSampler();
                samplePlayer?.DawToggleTransientSnap();
                RefreshSampler();
            }, 85);
            samplerSnapButton.style.flexGrow = 1;
            samplerSnapButton.style.flexBasis = new Length(0, LengthUnit.Pixel);
            samplerSnapButton.style.minWidth = 0;
            samplerSnapButton.style.fontSize = 11;
            transport.Add(samplerSnapButton);
            var full = Btn("FULL", () => { FindSampler(); samplePlayer?.DawShowFullSample(); RefreshSampler(); }, 53);
            full.style.flexGrow = 1;
            full.style.flexBasis = new Length(0, LengthUnit.Pixel);
            full.style.minWidth = 0;
            full.style.marginRight = 0;
            transport.Add(full);
            samplerPage.Add(transport);

            samplerWave = Box(new Color(.049f,.073f,.105f), 5);
            samplerWave.style.height = 175;
            samplerWave.style.flexShrink = 0;
            samplerWave.style.marginTop = 3;
            samplerWave.style.marginBottom = 4;
            samplerPage.Add(samplerWave);

            // Keep the warp job status and optional pitch/key tools directly below
            // the waveform, where their effect can be seen while editing samples.
            samplerWarpLabel = Txt("ORIGINAL AUDIO / NON-DESTRUCTIVE SLICING", 10, Muted);
            samplerWarpLabel.style.whiteSpace = WhiteSpace.Normal;
            samplerWarpLabel.style.marginTop = 4;
            samplerPage.Add(samplerWarpLabel);
            samplerWarpProgressLabel = Txt("WARPING", 10, Muted);
            samplerWarpProgressLabel.style.display = DisplayStyle.None;
            samplerPage.Add(samplerWarpProgressLabel);
            samplerWarpProgressTrack = Box(Surface, 3);
            samplerWarpProgressTrack.style.height = 5;
            samplerWarpProgressTrack.style.overflow = Overflow.Hidden;
            samplerWarpProgressTrack.style.display = DisplayStyle.None;
            samplerWarpProgressFill = new VisualElement();
            samplerWarpProgressFill.style.height = Length.Percent(100);
            samplerWarpProgressFill.style.width = Length.Percent(0);
            samplerWarpProgressFill.style.backgroundColor = Accent;
            samplerWarpProgressTrack.Add(samplerWarpProgressFill);
            samplerPage.Add(samplerWarpProgressTrack);


            // Optional pitch/key tools remain available without dominating the
            // chopping workflow or pushing the waveform far down the screen.
            var utility = Box(new Color(.085f,.12f,.17f), 5);
            utility.style.marginTop = 8;
            utility.style.paddingLeft = 8;
            utility.style.paddingRight = 8;
            utility.style.paddingTop = 6;
            utility.style.paddingBottom = 6;
            var pitchRow = new VisualElement();
            pitchRow.style.flexDirection = FlexDirection.Row;
            pitchRow.style.alignItems = Align.Center;
            pitchRow.style.height = 43;
            pitchRow.Add(Txt("PITCH", 11, Muted));
            pitchRow.Add(Btn("-1", () => { FindSampler(); samplePlayer?.DawAdjustPitchSemitones(-1); RefreshSampler(); }, 43));
            samplerPitchLabel = Txt("0 ST", 12, Accent);
            samplerPitchLabel.style.flexGrow = 1;
            samplerPitchLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            pitchRow.Add(samplerPitchLabel);
            pitchRow.Add(Btn("+1", () => { FindSampler(); samplePlayer?.DawAdjustPitchSemitones(1); RefreshSampler(); }, 43));
            pitchRow.Add(Btn("RESET", () => { FindSampler(); samplePlayer?.DawSetPitchSemitones(0); RefreshSampler(); }, 63));
            utility.Add(pitchRow);
            samplerSourceKeyLabel = Txt("SOURCE KEY / NO SAMPLE", 11, Ink);
            samplerSourceKeyLabel.style.whiteSpace = WhiteSpace.Normal;
            utility.Add(samplerSourceKeyLabel);
            samplerPitchedKeyLabel = Txt("PITCHED KEY / UNKNOWN", 11, Accent);
            samplerPitchedKeyLabel.style.whiteSpace = WhiteSpace.Normal;
            samplerPitchedKeyLabel.style.marginTop = 4;
            utility.Add(samplerPitchedKeyLabel);
            samplerLibraryLabel = Txt("SAMPLE LIBRARY", 10, Muted);
            samplerLibraryLabel.style.whiteSpace = WhiteSpace.Normal;
            samplerLibraryLabel.style.marginTop = 6;
            utility.Add(samplerLibraryLabel);
            samplerPage.Add(utility);


            var zoomRow = new VisualElement();
            zoomRow.style.flexDirection = FlexDirection.Row;
            zoomRow.style.alignItems = Align.Center;
            zoomRow.style.height = 45;
            var zoomOut = Btn("- ZOOM", () => { FindSampler(); samplePlayer?.DawZoomSample(false); }, 77);
            var zoomIn = Btn("+ ZOOM", () => { FindSampler(); samplePlayer?.DawZoomSample(true); }, 77);
            samplerGridButton = Btn("GRID ON", () => { FindSampler(); samplePlayer?.DawToggleWaveGrid(); RefreshSampler(); }, 77);
            foreach (var button in new[] { zoomOut, zoomIn, samplerGridButton })
            {
                button.style.flexGrow = 1;
                button.style.flexBasis = new Length(0, LengthUnit.Pixel);
                button.style.minWidth = 0;
                button.style.marginRight = 5;
                zoomRow.Add(button);
            }
            samplerGridButton.style.marginRight = 0;
            samplerPage.Add(zoomRow);

            var editRow = new VisualElement();
            editRow.style.flexDirection = FlexDirection.Row;
            editRow.style.alignItems = Align.Center;
            editRow.style.height = 47;
            var add = Btn("ADD MARK", () => { FindSampler(); samplePlayer?.DawAddMarker(); RefreshSampler(); }, 87);
            var del = Btn("DELETE", () => { FindSampler(); samplePlayer?.DawDeleteMarker(); RefreshSampler(); }, 72);
            var apply = Btn("APPLY CHOPS", ApplySamplerChops, 98);
            add.style.flexGrow = 1;
            del.style.flexGrow = 1;
            apply.style.flexGrow = 1;
            add.style.flexBasis = new Length(0, LengthUnit.Pixel);
            del.style.flexBasis = new Length(0, LengthUnit.Pixel);
            apply.style.flexBasis = new Length(0, LengthUnit.Pixel);
            add.style.minWidth = 0;
            del.style.minWidth = 0;
            apply.style.minWidth = 0;
            apply.style.marginRight = 0;
            editRow.Add(add);
            editRow.Add(del);
            editRow.Add(apply);
            samplerPage.Add(editRow);
            samplerSelectedMarker = Txt("TAP A PAD TO SELECT ITS MARKER", 10, Accent);
            samplerSelectedMarker.style.whiteSpace = WhiteSpace.Normal;
            samplerSelectedMarker.style.marginTop = 4;
            samplerSelectedMarker.style.marginBottom = 5;
            samplerPage.Add(samplerSelectedMarker);

            // These controls are for fine edits, not song navigation.
            var markerRow = new VisualElement();
            markerRow.style.flexDirection = FlexDirection.Row;
            markerRow.style.height = 43;
            var prev = Btn("< MARK", () => { FindSampler(); samplePlayer?.DawSelectMarker(-1); RefreshSampler(); }, 70);
            var next = Btn("MARK >", () => { FindSampler(); samplePlayer?.DawSelectMarker(1); RefreshSampler(); }, 70);
            var nudgeL = Btn("- 10 MS", () => { FindSampler(); samplePlayer?.DawNudgeSelectedMarker(-1); RefreshSampler(); }, 70);
            var nudgeR = Btn("+ 10 MS", () => { FindSampler(); samplePlayer?.DawNudgeSelectedMarker(1); RefreshSampler(); }, 70);
            foreach (var button in new[] { prev, next, nudgeL, nudgeR })
            {
                button.style.flexGrow = 1;
                button.style.flexBasis = new Length(0, LengthUnit.Pixel);
                button.style.minWidth = 0;
                button.style.paddingLeft = 3;
                button.style.paddingRight = 3;
                button.style.fontSize = 11;
                markerRow.Add(button);
            }
            nudgeR.style.marginRight = 0;
            samplerPage.Add(markerRow);

            var padScroll = new VisualElement();
            padScroll.style.flexDirection = FlexDirection.Column;
            padScroll.style.flexShrink = 0;
            for (int group = 0; group < 4; group++)
            {
                var padRow = new VisualElement();
                padRow.style.flexDirection = FlexDirection.Row;
                padRow.style.height = 68;
                padRow.style.flexShrink = 0;
                for (int j = 0; j < 4; j++)
                {
                    int slice = group * 4 + j + 1;
                    var button = Btn("S" + slice.ToString("00"), () =>
                    {
                        FindSampler();
                        samplePlayer?.DawSelectChopPad(slice);
                        RefreshSampler();
                    }, 44);
                    button.style.flexGrow = 1;
                    button.style.flexShrink = 1;
                    button.style.minWidth = 0;
                    button.style.marginRight = j == 3 ? 0 : 5;
                    button.style.height = 62;
                    button.style.fontSize = 10;
                    button.style.whiteSpace = WhiteSpace.Normal;
                    button.style.backgroundColor = Raised;
                    button.style.position = Position.Relative;
                    var track = new VisualElement();
                    track.pickingMode = PickingMode.Ignore;
                    track.style.position = Position.Absolute;
                    track.style.left = 5;
                    track.style.right = 5;
                    track.style.bottom = 5;
                    track.style.height = 4;
                    track.style.backgroundColor = Surface;
                    var bar = new VisualElement();
                    bar.pickingMode = PickingMode.Ignore;
                    bar.style.position = Position.Absolute;
                    bar.style.top = 0;
                    bar.style.bottom = 0;
                    bar.style.backgroundColor = Accent;
                    bar.style.display = DisplayStyle.None;
                    track.Add(bar);
                    button.Add(track);
                    samplerChopPads.Add(button);
                    samplerChopRangeBars.Add(bar);
                    padRow.Add(button);
                }
                padScroll.Add(padRow);
            }
            samplerPage.Add(padScroll);

            samplerPage.schedule.Execute(() =>
            {
                if (showingSampler) RefreshSampler();
                RefreshClipSampleKeyReference();
                if (generatorPanel != null && generatorPanel.style.display.value == DisplayStyle.Flex && generatorSampleKeyHint != null)
                {
                    FindSampler();
                    bool available = samplePlayer != null && samplePlayer.DawSampleKeyAvailable;
                    generatorSampleKeyButton?.SetEnabled(available);
                    generatorSampleKeyHint.text = samplePlayer == null ? "NO SAMPLE" :
                        samplePlayer.DawSampleKeyAnalyzing ? "ANALYZING SAMPLE" :
                        available ? samplePlayer.DawPitchedKeyText + " / " + samplePlayer.DawSampleKeyConfidence + " EST." :
                        "KEY NOT DETECTED";
                }
            }).Every(500);
            root.Add(samplerPage);
        }

        // The editor remains usable at a small phone viewport: the generator is a
        // scrollable in-editor sheet instead of another permanent toolbar row.
        private void BuildGeneratorPanel()
        {
            generatorPanel = new VisualElement { name = "ZPunkGenreGenerator" };
            generatorPanel.style.position = Position.Absolute;
            generatorPanel.style.left = 0;
            generatorPanel.style.right = 0;
            generatorPanel.style.top = 0;
            generatorPanel.style.bottom = 0;
            generatorPanel.style.backgroundColor = new Color(.055f,.078f,.116f,.99f);
            generatorPanel.style.display = DisplayStyle.None;
            generatorPanel.style.paddingTop = 6;
            generatorPanel.style.paddingLeft = 8;
            generatorPanel.style.paddingRight = 8;
            var body = new ScrollView(ScrollViewMode.Vertical);
            body.style.flexGrow = 1;
            body.style.minHeight = 0;
            body.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            body.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            body.contentContainer.style.paddingBottom = 14;
            generatorPanel.Add(body);

            generatorHeading = Txt("GENERATE / DRUMS",16,Ink);
            generatorHeading.style.unityFontStyleAndWeight = FontStyle.Bold;
            generatorHeading.style.marginTop = 4;
            body.Add(generatorHeading);
            generatorHint = Txt("Select the sound before writing notes to your clip.",11,Muted);
            generatorHint.style.whiteSpace = WhiteSpace.Normal;
            generatorHint.style.marginTop = 4;
            generatorHint.style.marginBottom = 8;
            body.Add(generatorHint);

            body.Add(Txt("GENRE",11,Muted));
            var genresScroll = new ScrollView(ScrollViewMode.Horizontal);
            genresScroll.style.height = 52;
            genresScroll.style.flexShrink = 0;
            genresScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            genresScroll.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            generatorGenres = genresScroll.contentContainer;
            generatorGenres.style.flexDirection = FlexDirection.Row;
            body.Add(genresScroll);

            body.Add(Txt("STYLE",11,Muted));
            var stylesScroll = new ScrollView(ScrollViewMode.Horizontal);
            stylesScroll.style.height = 52;
            stylesScroll.style.flexShrink = 0;
            stylesScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            stylesScroll.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            generatorStyles = stylesScroll.contentContainer;
            generatorStyles.style.flexDirection = FlexDirection.Row;
            body.Add(stylesScroll);

            // SYNTH alone can generate either a melody or scale-aware chord
            // progressions. Keep this inside the scrolling sheet for small phones.
            generatorSynthModeRow = new VisualElement();
            generatorSynthModeRow.style.flexDirection = FlexDirection.Column;
            var synthModeLabel = Txt("SYNTH / GENERATION TYPE",11,Muted);
            synthModeLabel.style.marginTop = 5;
            generatorSynthModeRow.Add(synthModeLabel);
            var synthModeButtons = new VisualElement();
            synthModeButtons.style.flexDirection = FlexDirection.Row;
            synthModeButtons.style.height = 48;
            synthModeButtons.style.alignItems = Align.Center;
            generatorMelodyButton = Btn("MELODY", () => { generatorSynthChords = false; RefreshGeneratorPanel(); }, 90);
            generatorChordsButton = Btn("CHORDS", () => { generatorSynthChords = true; RefreshGeneratorPanel(); }, 90);
            generatorMelodyButton.style.flexGrow = 1;
            generatorChordsButton.style.flexGrow = 1;
            synthModeButtons.Add(generatorMelodyButton);
            synthModeButtons.Add(generatorChordsButton);
            generatorSynthModeRow.Add(synthModeButtons);
            body.Add(generatorSynthModeRow);

            generatorSummary = Txt("",12,Accent);
            generatorSummary.style.whiteSpace = WhiteSpace.Normal;
            generatorSummary.style.marginTop = 8;
            generatorSummary.style.marginBottom = 8;
            body.Add(generatorSummary);

            body.Add(Txt("KEY / SHARED BY BASS, SYNTH AND PADS",10,Muted));
            var keyRow = new VisualElement();
            keyRow.style.height = 48;
            keyRow.style.flexDirection = FlexDirection.Row;
            keyRow.style.alignItems = Align.Center;
            keyRow.Add(Btn("<",() => { generatorKey = (generatorKey + 11)%12; SaveSharedHarmonicSelection(); RefreshGeneratorPanel(); },43));
            generatorKeyLabel = Txt("A MIN",13,Accent);
            generatorKeyLabel.style.flexGrow = 1;
            generatorKeyLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            keyRow.Add(generatorKeyLabel);
            keyRow.Add(Btn(">",() => { generatorKey = (generatorKey + 1)%12; SaveSharedHarmonicSelection(); RefreshGeneratorPanel(); },43));
            keyRow.Add(Btn("MIN/MAJ",() => { generatorMinor = !generatorMinor; SaveSharedHarmonicSelection(); RefreshGeneratorPanel(); },85));
            body.Add(keyRow);

            // Shared project-level harmonic direction, independent of the selected
            // Bass / Synth / Pads track and independent of NEW VARIATION.
            generatorProgressionSection = new VisualElement();
            generatorProgressionSection.style.flexDirection = FlexDirection.Column;
            var progressionHeading = Txt("PROGRESSION / SHARED BY BASS, SYNTH AND PADS", 10, Muted);
            progressionHeading.style.marginTop = 8;
            generatorProgressionSection.Add(progressionHeading);
            var progressionRow = new VisualElement();
            progressionRow.style.height = 48;
            progressionRow.style.flexDirection = FlexDirection.Row;
            progressionRow.style.alignItems = Align.Center;
            progressionRow.Add(Btn("<", () => StepSharedProgression(-1), 43));
            generatorProgressionLabel = Txt("GENRE DEFAULT", 11, Accent);
            generatorProgressionLabel.style.flexGrow = 1;
            generatorProgressionLabel.style.minWidth = 0;
            generatorProgressionLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            progressionRow.Add(generatorProgressionLabel);
            progressionRow.Add(Btn(">", () => StepSharedProgression(1), 43));
            generatorProgressionSection.Add(progressionRow);
            generatorProgressionChords = Txt("", 11, Ink);
            generatorProgressionChords.style.whiteSpace = WhiteSpace.Normal;
            generatorProgressionChords.style.unityTextAlign = TextAnchor.MiddleCenter;
            generatorProgressionChords.style.marginBottom = 6;
            generatorProgressionSection.Add(generatorProgressionChords);
            var progressionHelp = Txt("Changes future generations; existing clips are kept until regenerated.", 10, Muted);
            progressionHelp.style.whiteSpace = WhiteSpace.Normal;
            progressionHelp.style.marginBottom = 4;
            generatorProgressionSection.Add(progressionHelp);
            body.Add(generatorProgressionSection);

            // Explicit opt-in: importing a sample never silently changes the key
            // of other tracks or previously saved generations.
            var sampleKeyRow = new VisualElement();
            sampleKeyRow.style.flexDirection = FlexDirection.Row;
            sampleKeyRow.style.alignItems = Align.Center;
            sampleKeyRow.style.minHeight = 46;
            sampleKeyRow.style.marginTop = 4;
            generatorSampleKeyButton = Btn("USE SAMPLE KEY", () =>
            {
                FindSampler();
                if (samplePlayer == null || !samplePlayer.DawSampleKeyAvailable) return;
                generatorKey = samplePlayer.DawPitchedKeyTonic;
                generatorMinor = samplePlayer.DawSampleKeyMinor;
                SaveSharedHarmonicSelection();
                // Opt-in harmonic guide. Do not highlight unrelated piano rolls merely
                // because a sample was loaded or its key was detected.
                sampleKeyGuideEnabled = true;
                sampleKeyGuideTonic = generatorKey;
                sampleKeyGuideMinor = generatorMinor;
                sampleKeyGuideProjectSlot = projects != null ? projects.CurrentSlot : -1;
                sampleKeyGuideSampleId = samplePlayer.DawSampleFolder + "/" + samplePlayer.DawSampleName;
                ApplySampleKeyGuide();
                RefreshClipSampleKeyReference();
                RefreshGeneratorPanel();
            }, 132);
            generatorSampleKeyButton.style.height = 42;
            generatorSampleKeyButton.style.flexShrink = 0;
            sampleKeyRow.Add(generatorSampleKeyButton);
            generatorSampleKeyHint = Txt("NO SAMPLE KEY", 11, Muted);
            generatorSampleKeyHint.style.flexGrow = 1;
            generatorSampleKeyHint.style.minWidth = 0;
            generatorSampleKeyHint.style.whiteSpace = WhiteSpace.Normal;
            generatorSampleKeyHint.style.marginLeft = 9;
            sampleKeyRow.Add(generatorSampleKeyHint);
            body.Add(sampleKeyRow);

            body.Add(Txt("LENGTH",10,Muted));
            generatorBarsRow = new VisualElement();
            generatorBarsRow.style.flexDirection = FlexDirection.Row;
            generatorBarsRow.style.height = 48;
            body.Add(generatorBarsRow);

            var variationRow = new VisualElement();
            variationRow.style.flexDirection = FlexDirection.Row;
            variationRow.style.alignItems = Align.Center;
            variationRow.style.height = 48;
            generatorVariationLabel = Txt("VARIATION 1",11,Muted);
            generatorVariationLabel.style.flexGrow = 1;
            variationRow.Add(generatorVariationLabel);
            variationRow.Add(Btn("NEW VARIATION",() => { generatorVariation++; RefreshGeneratorPanel(); },118));
            body.Add(variationRow);

            // Persistent history is scoped to the selected project, instrument, genre
            // and melodic/chord mode. It lives within the sheet's existing scroll area.
            generatorHistoryLabel = Txt("PREVIOUS GENERATIONS",11,Muted);
            generatorHistoryLabel.style.marginTop = 12;
            generatorHistoryLabel.style.marginBottom = 5;
            body.Add(generatorHistoryLabel);
            var past = new ScrollView(ScrollViewMode.Vertical);
            generatorHistoryRows = past.contentContainer;
            generatorHistoryRows.style.paddingRight = 1;
            // Share the same complete-height, draggable scrollbar as Project Library
            // and Browse Samples. No native white UI Toolkit arrow controls.
            var historyHost = ZPunkOverlaySkin.ScrollWithRail(past);
            historyHost.style.height = 184;
            historyHost.style.flexGrow = 0;
            historyHost.style.flexShrink = 0;
            historyHost.style.marginBottom = 7;
            body.Add(historyHost);

            var actionRow = new VisualElement();
            actionRow.style.flexDirection = FlexDirection.Row;
            actionRow.style.height = 52;
            actionRow.style.flexShrink = 0;
            var cancel = Btn("CANCEL",CloseGenerator,82);
            cancel.style.flexGrow = 1;
            actionRow.Add(cancel);
            var apply = Btn("GENERATE",ApplyGenerator,109);
            apply.style.backgroundColor = new Color(.12f,.36f,.34f);
            apply.style.color = Accent;
            apply.style.flexGrow = 1;
            actionRow.Add(apply);
            // Keep cancel/apply visible even on short phone displays.
            actionRow.style.paddingTop = 2;
            generatorPanel.Add(actionRow);
            root.Add(generatorPanel);
        }

        private void SaveSharedHarmonicSelection(int? newProgression = null)
        {
            if (projects == null || song == null) return;
            SongData edited = projects.GetSongForArrangementEditor();
            if (edited == null) return;
            if (!ReferenceEquals(projects.CurrentSong, edited))
                edited = ArrangementEditing.IndependentCopy(edited);
            if (newProgression.HasValue)
                edited.sharedProgressionChoice = Mathf.Clamp(newProgression.Value, 0,
                    ZPunkGenreGenerator.ProgressionChoiceCount - 1);
            edited.sharedProgressionKey = generatorKey;
            edited.sharedProgressionMinor = generatorMinor;
            projects.AcceptArrangementEdit(edited);
            song = edited;
            clip = ArrangementEditing.FindClip(song, clipId, out _);
            content = clip == null ? null : ArrangementEditing.FindContent(song, clip.contentId);
            // This changes future generation settings, not currently playing notes;
            // avoid rebuilding the editor or interrupting the open generator sheet.
        }

        private void StepSharedProgression(int direction)
        {
            int count = ZPunkGenreGenerator.ProgressionChoiceCount;
            int current = song != null ? song.sharedProgressionChoice : 0;
            int selected = (current + direction + count) % count;
            SaveSharedHarmonicSelection(selected);
            // A four-chord progression needs four bars to be fully audible.
            if (selected > 0 && current == 0 && clip == null) generatorBars = 4;
            RefreshGeneratorPanel();
        }

        private void OpenGenerator(int requestedTrack)
        {
            if (!ready || showingSampler || generatorPanel == null || song == null || song.tracks == null ||
                requestedTrack < 0 || requestedTrack >= song.tracks.Count ||
                song.tracks[requestedTrack].kind == TrackKind.Chops) return;
            if (requestedTrack != trackIndex) SelectTrack(requestedTrack);
            generatorTargetTrack = trackIndex;
            // All generator tracks read the SAME saved project-wide progression/key.
            if (song.sharedProgressionChoice > 0)
            {
                generatorKey = Mathf.Clamp(song.sharedProgressionKey, 0, 11);
                generatorMinor = song.sharedProgressionMinor;
            }
            if (song.tracks[trackIndex].kind == TrackKind.Synth)
                generatorSynthChords = content != null &&
                    !string.IsNullOrEmpty(content.generatedTag) &&
                    content.generatedTag.Contains(" / CHORDS / ");
            generatorBars = clip == null ? (song.sharedProgressionChoice > 0 ? 4 : 2) :
                (clip.lengthTicks >= 4 * SongData.TicksPerBar4 ? 4 :
                 clip.lengthTicks >= 2 * SongData.TicksPerBar4 ? 2 : 1);
            generatorVariation++;
            FindSampler();
            RefreshGeneratorPanel();
            generatorPanel.style.display = DisplayStyle.Flex;
        }
        private void CloseGenerator()
        {
            if (generatorPanel != null) generatorPanel.style.display = DisplayStyle.None;
        }
        private void RefreshGeneratorPanel()
        {
            if (generatorPanel == null || song == null || song.tracks == null ||
                generatorTargetTrack >= song.tracks.Count) return;
            generatorHeading.text = "GENERATE  /  " + song.tracks[generatorTargetTrack].name.ToUpperInvariant();
            generatorHint.text = clip == null ?
                "Creates a new clip. No changes until GENERATE is tapped." :
                "REPLACES all notes in " + clip.name + ". Cancel leaves it unchanged." +
                (string.IsNullOrEmpty(content?.generatedTag) ? "" : "\nCURRENT: " + content.generatedTag);
            generatorGenres.Clear();
            for (int g = 0; g < ZPunkGenreGenerator.Genres.Length; g++)
            {
                int index = g;
                var button = Btn(ZPunkGenreGenerator.Genres[g],() => { generatorGenre = index; generatorStyle = 0; RefreshGeneratorPanel(); },90);
                if (g == generatorGenre) { button.style.backgroundColor = SelectedColor(); button.style.color = Accent; }
                generatorGenres.Add(button);
            }
            generatorStyles.Clear();
            if (generatorGenre == 1 && generatorStyle == 3 && song.tracks[generatorTargetTrack].kind == TrackKind.Drums) generatorStyle = 0;
            for (int st = 0; st < ZPunkGenreGenerator.Styles[generatorGenre].Length; st++)
            {
                if (generatorGenre == 1 && st == 3 && song.tracks[generatorTargetTrack].kind == TrackKind.Drums) continue;
                int index = st;
                var button = Btn(ZPunkGenreGenerator.Styles[generatorGenre][st],() => { generatorStyle = index; RefreshGeneratorPanel(); },91);
                if (st == generatorStyle) { button.style.backgroundColor = SelectedColor(); button.style.color = Accent; }
                generatorStyles.Add(button);
            }
            bool isSynth = song.tracks[generatorTargetTrack].kind == TrackKind.Synth;
            if (generatorSynthModeRow != null)
                generatorSynthModeRow.style.display = isSynth ? DisplayStyle.Flex : DisplayStyle.None;
            if (generatorMelodyButton != null && generatorChordsButton != null)
            {
                generatorMelodyButton.style.backgroundColor = !generatorSynthChords ? SelectedColor() : Raised;
                generatorMelodyButton.style.color = !generatorSynthChords ? Accent : Ink;
                generatorChordsButton.style.backgroundColor = generatorSynthChords ? SelectedColor() : Raised;
                generatorChordsButton.style.color = generatorSynthChords ? Accent : Ink;
            }
            bool chords = isSynth && generatorSynthChords;
            TrackKind generatorKind = song.tracks[generatorTargetTrack].kind;
            if (generatorProgressionSection != null)
                generatorProgressionSection.style.display = generatorKind == TrackKind.Drums
                    ? DisplayStyle.None : DisplayStyle.Flex;
            generatorSummary.text = (chords ?
                ZPunkGenreGenerator.ChordsDescription(generatorGenre, generatorStyle) :
                generatorKind == TrackKind.Bass ? ZPunkGenreGenerator.BassDescription(generatorGenre, generatorStyle) :
                generatorKind == TrackKind.Pads ? ZPunkGenreGenerator.ChordsDescription(generatorGenre, generatorStyle) :
                ZPunkGenreGenerator.Description(generatorGenre, generatorStyle)) + "\n" +
                "READY: " + ZPunkGenreGenerator.Label(generatorGenre, generatorStyle, generatorKey, generatorMinor, chords) +
                (isSynth && !chords ? " / MELODY" : "") +
                (song.sharedProgressionChoice > 0 ? " / SHARED PROGRESSION " + song.sharedProgressionChoice : "");
            generatorKeyLabel.text = ZPunkGenreGenerator.Keys[generatorKey] + (generatorMinor ? " MIN" : " MAJ");
            int progressionChoice = song.sharedProgressionChoice;
            if (generatorProgressionLabel != null)
                generatorProgressionLabel.text = ZPunkGenreGenerator.ProgressionLabel(generatorGenre, generatorMinor, progressionChoice);
            if (generatorProgressionChords != null)
                generatorProgressionChords.text = ZPunkGenreGenerator.ProgressionChordNames(
                    generatorGenre, generatorKey, generatorMinor, progressionChoice) +
                    (progressionChoice > 0 ? " / all parts follow these chords" : " / original genre harmony");
            FindSampler();
            bool hasSampleKey = samplePlayer != null && samplePlayer.DawSampleKeyAvailable;
            if (generatorSampleKeyButton != null) generatorSampleKeyButton.SetEnabled(hasSampleKey);
            if (generatorSampleKeyHint != null)
                generatorSampleKeyHint.text = samplePlayer == null ? "NO SAMPLE" :
                    samplePlayer.DawSampleKeyAnalyzing ? "ANALYZING SAMPLE" :
                    hasSampleKey ? samplePlayer.DawPitchedKeyText + " / " + samplePlayer.DawSampleKeyConfidence + " EST." :
                    "KEY NOT DETECTED";
            generatorBarsRow.Clear();
            foreach (int count in new[]{1,2,4})
            {
                int selected = count;
                var button = Btn(count + (count == 1 ? " BAR" : " BARS"), () => { generatorBars = selected; RefreshGeneratorPanel(); },76);
                button.style.flexGrow = 1;
                if (generatorBars == count) { button.style.backgroundColor = SelectedColor(); button.style.color = Accent; }
                generatorBarsRow.Add(button);
            }
            generatorVariationLabel.text = "VARIATION " + generatorVariation.ToString();
            RefreshGenerationHistory();
        }

        private void RefreshGenerationHistory()
        {
            if (generatorHistoryRows == null || generatorHistoryLabel == null || projects == null ||
                song == null || song.tracks == null || generatorTargetTrack < 0 ||
                generatorTargetTrack >= song.tracks.Count) return;
            generatorHistoryRows.Clear();
            TrackKind kind = song.tracks[generatorTargetTrack].kind;
            bool chords = kind == TrackKind.Synth && generatorSynthChords;
            var entries = ZPunkGenerationHistory.Read(projects.CurrentSlot, kind, generatorGenre, chords);
            generatorHistoryLabel.text = "PREVIOUS / " + ZPunkGenreGenerator.Genres[generatorGenre] +
                "  (" + entries.Count + ")";
            if (entries.Count == 0)
            {
                var empty = Txt("No saved generations yet. Generate a pattern to keep it here.", 11, Muted);
                empty.style.whiteSpace = WhiteSpace.Normal;
                empty.style.marginTop = 10;
                generatorHistoryRows.Add(empty);
                return;
            }
            for (int historyIndex = 0; historyIndex < entries.Count; historyIndex++)
            {
                var entry = entries[historyIndex];
                if (entry == null || entry.snapshot == null) continue;
                int deleteIndex = historyIndex;
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;
                row.style.marginBottom = 6;
                row.style.paddingLeft = 8;
                row.style.backgroundColor = Raised;
                row.style.borderTopLeftRadius = 6;
                row.style.borderBottomLeftRadius = 6;
                row.style.borderTopRightRadius = 6;
                row.style.borderBottomRightRadius = 6;
                string styleName = ZPunkGenreGenerator.Styles[generatorGenre][Mathf.Clamp(entry.style, 0, ZPunkGenreGenerator.Styles[generatorGenre].Length - 1)];
                var details = Txt(styleName + "  /  " + ZPunkGenreGenerator.Keys[Mathf.Clamp(entry.key, 0, 11)] +
                    (entry.minor ? "m" : "") + "  /  " + entry.bars + " BAR" +
                    (entry.bars == 1 ? "" : "S") + "  /  V" + entry.variation +
                    (ZPunkGenreGenerator.ProgressionFromTag(entry.snapshot.generatedTag) > 0
                        ? "  /  P" + ZPunkGenreGenerator.ProgressionFromTag(entry.snapshot.generatedTag) : ""), 12, Ink);
                details.style.flexGrow = 1;
                details.style.minWidth = 0;
                details.style.overflow = Overflow.Hidden;
                details.style.textOverflow = TextOverflow.Ellipsis;
                details.style.whiteSpace = WhiteSpace.NoWrap;
                row.style.paddingRight = 5;
                row.style.minHeight = 48;
                row.Add(details);
                var use = Btn("USE", () => RestoreGeneration(entry), 53);
                use.style.height = 42;
                use.style.minWidth = 53;
                use.style.backgroundColor = SelectedColor();
                use.style.color = Accent;
                row.Add(use);
                var del = Btn("DEL", () => DeleteGeneration(deleteIndex), 49);
                del.style.height = 42;
                del.style.minWidth = 49;
                del.style.marginLeft = 4;
                del.style.color = new Color(1f,.69f,.67f);
                del.style.backgroundColor = Surface;
                row.Add(del);
                generatorHistoryRows.Add(row);
            }
        }

        private void DeleteGeneration(int index)
        {
            if (projects == null || song == null || song.tracks == null ||
                generatorTargetTrack < 0 || generatorTargetTrack >= song.tracks.Count) return;
            TrackKind kind = song.tracks[generatorTargetTrack].kind;
            bool chords = kind == TrackKind.Synth && generatorSynthChords;
            if (ZPunkGenerationHistory.Delete(projects.CurrentSlot, kind, generatorGenre, chords, index))
                RefreshGenerationHistory();
        }

        private static Color SelectedColor() { return new Color(.10f,.32f,.34f); }

        // Both new generations and restored history use exactly the same clip-writing
        // path, leaving sample content and other tracks untouched.
        private bool PrepareGeneratorContent(int bars)
        {
            if (song == null || song.tracks == null || generatorTargetTrack != trackIndex ||
                song.tracks[trackIndex].kind == TrackKind.Chops) return false;
            if (!Writable()) return false;
            TrackData track = song.tracks[trackIndex];
            if (clip == null)
            {
                long start = ArrangementEditing.NextFreeStart(track, 0, bars * SongData.TicksPerBar4);
                clip = ArrangementEditing.CreateClip(song, track, start);
                if (clip == null) return false;
                clipId = clip.id;
                content = ArrangementEditing.FindContent(song, clip.contentId);
            }
            if (content == null)
            {
                if (song.contents == null) song.contents = new List<ClipContentData>();
                content = new ClipContentData { id = ArrangementEditing.Id("content") };
                song.contents.Add(content);
                clip.contentId = content.id;
            }
            return true;
        }
        private void FinishGeneratorContent()
        {
            clip.sourceOffsetTick = 0;
            clip.loopEnabled = false;
            clip.lengthTicks = content.lengthTicks;
            ArrangementEditing.ExtendSong(song, clip.startTick + clip.lengthTicks);
            selectedNote = -1;
            selectedEventRow = -1;
            selectedEventTick = -1;
            CloseGenerator();
            Changed(true, false);
            if (content.kind == ClipKind.Midi) FocusPatternNotes();
        }
        // Prefer existing PAD or chord-mode SYNTH harmony at the selected clip location.
        // Never read notes from the clip currently being regenerated.
        private void ReadArrangementHarmony(int bars, out int[] degrees, out int[][] voices)
        {
            degrees = new int[bars];
            voices = new int[bars][];
            for (int b = 0; b < bars; b++) degrees[b] = -1;
            if (song == null || song.tracks == null || song.contents == null || clip == null) return;
            for (int b = 0; b < bars; b++)
            {
                long tick = clip.startTick + b * SongData.TicksPerBar4;
                List<int> pitches = null;
                foreach (var track in song.tracks)
                {
                    if (track == null || (track.kind != TrackKind.Pads && track.kind != TrackKind.Synth) || track.clips == null) continue;
                    if (track.kind == song.tracks[trackIndex].kind && track.kind != TrackKind.Pads) continue;
                    foreach (var item in track.clips)
                    {
                        if (item == null || item.id == clip.id || tick < item.startTick || tick >= item.startTick + item.lengthTicks) continue;
                        var source = song.contents.Find(x => x != null && x.id == item.contentId);
                        if (source == null || source.kind != ClipKind.Midi || source.notes == null || source.notes.Count < 3) continue;
                        long local = tick - item.startTick + item.sourceOffsetTick;
                        if (item.loopEnabled && source.lengthTicks > 0) local %= source.lengthTicks;
                        var notes = new List<int>();
                        foreach (var note in source.notes)
                            if (note != null && note.startTick <= local && note.startTick + note.durationTicks > local)
                                notes.Add(note.pitch);
                        // A chord may be stabbed on an offbeat instead of held at the barline.
                        if (notes.Count < 3)
                            foreach (var note in source.notes)
                                if (note != null && note.startTick >= local && note.startTick < local + SongData.TicksPerQuarter)
                                    notes.Add(note.pitch);
                        if (notes.Count >= 3) { pitches = notes; break; }
                    }
                    if (pitches != null) break;
                }
                if (pitches == null) continue;
                pitches.Sort();
                voices[b] = pitches.ToArray();
                // Choose the diatonic triad whose pitch classes best fit the chord.
                int bestDegree = -1, bestScore = -1;
                int[] scale = generatorMinor ? new[] {0,2,3,5,7,8,10} : new[] {0,2,4,5,7,9,11};
                for (int d = 0; d < 7; d++)
                {
                    int score = 0;
                    for (int offset = 0; offset <= 4; offset += 2)
                    {
                        int pc = (generatorKey + scale[(d + offset) % 7]) % 12;
                        foreach (int pitch in pitches)
                            if ((pitch % 12 + 12) % 12 == pc) { score += offset == 0 ? 3 : 2; break; }
                    }
                    if (score > bestScore) { bestScore = score; bestDegree = d; }
                }
                if (bestScore >= 4) degrees[b] = bestDegree;
            }
        }

        private void ApplyGenerator()
        {
            if (!PrepareGeneratorContent(generatorBars)) return;
            TrackKind kind = song.tracks[trackIndex].kind;
            bool chords = kind == TrackKind.Synth && generatorSynthChords;
            int[] detectedDegrees = null;
            int[][] detectedVoices = null;
            if (song.sharedProgressionChoice == 0 &&
                (kind == TrackKind.Bass || (kind == TrackKind.Synth && !chords)))
                ReadArrangementHarmony(generatorBars, out detectedDegrees, out detectedVoices);
            // The shared progression takes priority over previously arranged harmony;
            // bar offset is the clip's actual arrangement position, not its local bar.
            int barOffset = clip == null ? 0 : (int)(clip.startTick / SongData.TicksPerBar4);
            ZPunkGenreGenerator.Fill(content, kind, generatorGenre, generatorStyle,
                generatorBars, generatorKey, generatorMinor, generatorVariation, chords,
                detectedDegrees, detectedVoices, song.sharedProgressionChoice, barOffset);
            ZPunkGenerationHistory.Add(projects.CurrentSlot, kind, generatorGenre, generatorStyle,
                generatorKey, generatorMinor, generatorBars, generatorVariation, chords, content);
            FinishGeneratorContent();
        }
        private void RestoreGeneration(ZPunkGenerationHistory.Entry entry)
        {
            ClipContentData saved = ZPunkGenerationHistory.Copy(entry);
            if (saved == null || !PrepareGeneratorContent(Mathf.Clamp(entry.bars, 1, 4))) return;
            // Preserve this clip's stable contentId so linked song references remain valid.
            content.kind = saved.kind;
            content.lengthTicks = saved.lengthTicks;
            content.generatedTag = saved.generatedTag;
            int historyProgression = ZPunkGenreGenerator.ProgressionFromTag(saved.generatedTag);
            if (historyProgression > 0)
            {
                generatorKey = Mathf.Clamp(entry.key, 0, 11);
                generatorMinor = entry.minor;
                SaveSharedHarmonicSelection(historyProgression);
            }
            content.notes = saved.notes ?? new List<NoteEvent>();
            content.drums = saved.drums ?? new List<DrumEvent>();
            content.chops = new List<ChopEvent>();
            // Older saved generations may still contain an empty first step.
            // Bring restored patterns onto the same tick-zero convention.
            ZPunkGenreGenerator.AlignToStart(content);
            FinishGeneratorContent();
        }

        private void FindDrumEngine()
        {
            if (drumEngine == null) drumEngine = FindObjectOfType<KMusicDrumSequencer>();
        }
        private void RefreshKitSelector()
        {
            if (kitSelector == null) return;
            // Visibility.Hidden reserves layout, unlike Display.None.
            kitSelector.style.visibility = trackIndex == 0 ? Visibility.Visible : Visibility.Hidden;
            FindDrumEngine();
            if (kitName != null) kitName.text = drumEngine != null ? drumEngine.DawKitName : "KIT UNAVAILABLE";
        }
        private void StepDrumKit(int direction)
        {
            FindDrumEngine();
            if (drumEngine == null) return;
            drumEngine.DawStepKit(direction);
            RefreshKitSelector();
            if (projects != null)
            {
                var edited = projects.GetSongForArrangementEditor();
                if (edited != null && edited.tracks != null && edited.tracks.Count > 0)
                {
                    if (!ReferenceEquals(projects.CurrentSong, edited))
                        edited = ArrangementEditing.IndependentCopy(edited);
                    edited.tracks[0].instrument.drumKitIndex = drumEngine.CaptureKitIndex();
                    edited.tracks[0].instrument.drumKitId = drumEngine.CaptureKitId();
                    projects.AcceptArrangementEdit(edited);
                    song = edited;
                }
            }
            OnClipChanged?.Invoke();
        }

        private void FindSampler()
        {
            if (samplePlayer == null) samplePlayer = FindObjectOfType<KMusicPlayerUI>();
        }
        private void RefreshSampler()
        {
            FindSampler();
            if (samplerTitle != null)
                samplerTitle.text = samplePlayer != null ? samplePlayer.DawSampleName : "SAMPLER UNAVAILABLE";
            if (samplerBpmLabel != null)
                samplerBpmLabel.text = "SOURCE " + (samplePlayer != null ? samplePlayer.DawTrackBpmCompact : "BPM --");
            if (samplerTapBpmStatus != null)
            {
                string tapStatus = samplePlayer != null ? samplePlayer.DawTapBpmStatus : "";
                samplerTapBpmStatus.text = tapStatus;
                samplerTapBpmStatus.style.display = string.IsNullOrEmpty(tapStatus) ?
                    DisplayStyle.None : DisplayStyle.Flex;
            }
            if (samplerWarpButton != null)
                samplerWarpButton.text = samplePlayer != null && samplePlayer.DawAutoWarp ? "WARP ON" : "WARP OFF";
            if (samplerWarpLabel != null)
                samplerWarpLabel.text = samplePlayer != null ?
                    (samplePlayer.DawAutoWarp ?
                        "WARP / " + samplePlayer.DawTrackBpmLabel + " / " + samplePlayer.DawWarpStatus :
                        "ORIGINAL AUDIO / NO TIME STRETCH") :
                    "NO SAMPLE";
            bool warpBusy = samplePlayer != null && samplePlayer.DawWarpProcessing;
            if (samplerWarpProgressTrack != null)
                samplerWarpProgressTrack.style.display = warpBusy ? DisplayStyle.Flex : DisplayStyle.None;
            if (samplerWarpProgressLabel != null)
            {
                samplerWarpProgressLabel.style.display = warpBusy ? DisplayStyle.Flex : DisplayStyle.None;
                if (warpBusy)
                    samplerWarpProgressLabel.text = samplePlayer.DawWarpStage + " / " +
                        Mathf.RoundToInt(samplePlayer.DawWarpProgress * 100f) + "%";
            }
            if (samplerWarpProgressFill != null)
                samplerWarpProgressFill.style.width = Length.Percent(
                    samplePlayer != null ? Mathf.Clamp01(samplePlayer.DawWarpProgress) * 100f : 0f);
            if (samplerPitchLabel != null)
            {
                int semitones = samplePlayer != null ? samplePlayer.DawPitchSemitones : 0;
                samplerPitchLabel.text = (semitones > 0 ? "+" : "") + semitones + " ST";
            }
            if (samplerSourceKeyLabel != null)
                samplerSourceKeyLabel.text = samplePlayer == null ? "SOURCE KEY  /  NO SAMPLE" :
                    "SOURCE KEY  /  " + samplePlayer.DawSourceKeyText +
                    (samplePlayer.DawSampleKeyAvailable ? "  (" + samplePlayer.DawSampleKeyConfidence + " EST.)" : "");
            if (samplerPitchedKeyLabel != null)
                samplerPitchedKeyLabel.text = samplePlayer == null ? "PITCHED KEY  /  UNKNOWN" :
                    "PITCHED KEY  /  " + samplePlayer.DawPitchedKeyText +
                    "  (" + (samplePlayer.DawPitchSemitones >= 0 ? "+" : "") + samplePlayer.DawPitchSemitones + " ST)";
            if (samplerLibraryLabel != null)
                samplerLibraryLabel.text = samplePlayer != null ? samplePlayer.DawSampleFolder : "SAMPLE LIBRARY";
            if (samplerSelectedMarker != null)
                samplerSelectedMarker.text = samplePlayer != null ? samplePlayer.DawSelectedMarkerLabel : "NO MARKER SELECTED";
            if (samplerHitSliceStatus != null)
                samplerHitSliceStatus.text = samplePlayer != null ?
                    samplePlayer.DawHitSliceStatus + " / UP TO 16 PADS" : "NO SAMPLE";
            bool hitsBusy = samplePlayer != null && samplePlayer.DawHitAnalysisRunning;
            if (samplerHitProgressLabel != null)
            {
                samplerHitProgressLabel.style.display = hitsBusy ? DisplayStyle.Flex : DisplayStyle.None;
                if (hitsBusy)
                {
                    float eta = samplePlayer.DawHitAnalysisEta;
                    samplerHitProgressLabel.text = "AMBER HIT GUIDES / " +
                        Mathf.RoundToInt(samplePlayer.DawHitAnalysisProgress * 100f) + "% / " +
                        samplePlayer.DawHitAnalysisSeconds.ToString("0.0") + "s ELAPSED" +
                        (eta >= 0f ? " / ~" + eta.ToString("0") + "s LEFT" : "");
                }
            }
            if (samplerHitProgressTrack != null)
                samplerHitProgressTrack.style.display = hitsBusy ? DisplayStyle.Flex : DisplayStyle.None;
            if (samplerHitProgressFill != null)
                samplerHitProgressFill.style.width = Length.Percent(hitsBusy ?
                    samplePlayer.DawHitAnalysisProgress * 100f : 0f);
            if (samplerGridButton != null)
            {
                bool enabled = samplePlayer != null && samplePlayer.DawShowWaveGrid;
                samplerGridButton.text = enabled ? "GRID ON" : "GRID OFF";
                samplerGridButton.style.backgroundColor = enabled ? new Color(.12f, .31f, .29f) : Raised;
                samplerGridButton.style.color = enabled ? Accent : Ink;
            }
            if (samplerSnapButton != null)
                samplerSnapButton.text = samplePlayer != null && samplePlayer.DawTransientSnapEnabled
                    ? "HIT SNAP" : "SNAP OFF";
            for (int i = 0; i < samplerChopPads.Count; ++i)
            {
                Button button = samplerChopPads[i];
                VisualElement bar = samplerChopRangeBars[i];
                float start = 0f, end = 0f;
                bool exists = samplePlayer != null && samplePlayer.DawTryGetChopRange(i + 1, out start, out end);
                bool selected = exists && samplePlayer.DawSelectedChopPad == i + 1;
                float duration = samplePlayer != null ? samplePlayer.DawSampleDuration : 0f;
                button.text = exists
                    ? "S" + (i + 1).ToString("00") + "\n" +
                      (start * duration).ToString("0.0") + " - " + (end * duration).ToString("0.0") + "s"
                    : "S" + (i + 1).ToString("00") + "\nEMPTY";
                button.style.color = exists ? Ink : Muted;
                button.style.backgroundColor = selected ? new Color(.15f, .38f, .36f) : Raised;
                button.style.borderBottomWidth = selected ? 2f : 0f;
                button.style.borderBottomColor = Accent;
                bar.style.display = exists ? DisplayStyle.Flex : DisplayStyle.None;
                if (exists)
                {
                    bar.style.left = Length.Percent(start * 100f);
                    bar.style.width = Length.Percent(Mathf.Max(1f, (end - start) * 100f));
                }
            }
        }
        private void RefreshClipSampleKeyReference()
        {
            if (clipSampleKeyReference == null) return;
            FindSampler();
            bool midiTrack = song != null && song.tracks != null &&
                trackIndex >= 0 && trackIndex < song.tracks.Count &&
                (song.tracks[trackIndex].kind == TrackKind.Bass ||
                 song.tracks[trackIndex].kind == TrackKind.Synth ||
                 song.tracks[trackIndex].kind == TrackKind.Pads);
            bool show = midiTrack && samplePlayer != null && samplePlayer.DawSampleKeyAvailable;
            clipSampleKeyReference.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (show)
                clipSampleKeyReference.text = (sampleKeyGuideEnabled ? "KEY GUIDE  /  " : "SAMPLE KEY  /  ") +
                    samplePlayer.DawPitchedKeyText +
                    "  (" + samplePlayer.DawSampleKeyConfidence + " EST.)";
        }

        private void ShowSampler(bool value)
        {
            showingSampler = value;
            if (root == null || samplerPage == null) return;
            foreach (var child in root.Children())
                child.style.display = child == generatorPanel ? DisplayStyle.None :
                    child == samplerPage ? (value ? DisplayStyle.Flex : DisplayStyle.None) :
                    (value ? DisplayStyle.None : DisplayStyle.Flex);
            if (!value) return;
            FindSampler();
            samplePlayer?.DawPrepareSliceLab();
            if (samplerWave != null && samplerWave.childCount == 0)
            {
                var doc = FindObjectOfType<UIDocument>();
                var oldHost = doc != null ? doc.rootVisualElement.Q<VisualElement>("Waveform") : null;
                if (oldHost != null)
                {
                    oldHost.RemoveFromHierarchy();
                    oldHost.style.height = 170;
                    oldHost.style.flexGrow = 1;
                    samplerWave.Add(oldHost);
                    var waveform = oldHost.Q<KMusic.UI.WaveformView>();
                    if (waveform != null)
                    {
                        waveform.VerticalDragDelta -= ScrollSamplerFromWaveform;
                        waveform.VerticalDragDelta += ScrollSamplerFromWaveform;
                    }
                }
            }
            RefreshSampler();
        }
        private void ScrollSamplerFromWaveform(float deltaY)
        {
            if (!showingSampler || samplerScroll == null) return;
            samplerScroll.scrollOffset = new Vector2(samplerScroll.scrollOffset.x,
                Mathf.Max(0, samplerScroll.scrollOffset.y - deltaY));
        }
        private void OpenSampler() { ShowSampler(true); }

        // A saved set of waveform markers defines playable slices, but not MIDI-style
        // step events. Copy the applied sample assignment into the DAW Chops track
        // so a painted S01-S16 cell can actually trigger that saved slice.
        private void ApplySamplerChops()
        {
            FindSampler();
            if (samplePlayer == null) return;
            samplePlayer.DawApplySlices();
            RefreshSampler();

            if (projects == null) projects = FindObjectOfType<KMusicProjectManager>();
            if (projects == null) return;
            var edited = projects.GetSongForArrangementEditor();
            if (edited == null || edited.tracks == null) return;
            if (!KMusic.KMusicChopState.TryLoadApplied(out string path, out float[] starts, out float[] ends))
                return;
            if (!ReferenceEquals(projects.CurrentSong, edited))
                edited = ArrangementEditing.IndependentCopy(edited);
            if (edited == null) return;
            foreach (var track in edited.tracks)
            {
                if (track == null || track.kind != TrackKind.Chops) continue;
                if (track.instrument == null) track.instrument = new InstrumentState();
                track.instrument.sampleResourcesPath = path;
                track.instrument.sliceStart01 = (float[])starts.Clone();
                track.instrument.sliceEnd01 = (float[])ends.Clone();
                projects.AcceptArrangementEdit(edited);
                song = edited;
                LoadClip(); // Rebind references after copying the document.
                OnClipChanged?.Invoke();
                break;
            }
        }

        // A saved chop kit may have no sequenced steps yet. The legacy converter
        // deliberately omits empty clips, which used to leave CHOPS with no piano
        // roll at all: Redraw() returned before registering the paint callbacks.
        // Open a single editable clip for that track without auto-firing all slices.
        // If a saved 16-step pattern exists, restore its cells once on creation.
        private void EnsureChopsEditable()
        {
            if (projects == null || song == null || song.tracks == null ||
                trackIndex < 0 || trackIndex >= song.tracks.Count) return;
            var track = song.tracks[trackIndex];
            if (track == null || track.kind != TrackKind.Chops) return;
            bool hasApplied = KMusic.KMusicChopState.TryLoadApplied(
                out string path, out float[] starts, out float[] ends);
            bool updateSample = hasApplied && (track.instrument == null ||
                track.instrument.sampleResourcesPath != path ||
                !SameSlices(track.instrument.sliceStart01, starts) ||
                !SameSlices(track.instrument.sliceEnd01, ends));
            if (clip != null && content != null && !updateSample) return;

            if (!ReferenceEquals(projects.CurrentSong, song))
                song = ArrangementEditing.IndependentCopy(song);
            if (song == null) return;
            track = song.tracks[trackIndex];
            if (track == null) return;
            if (track.instrument == null) track.instrument = new InstrumentState();

            if (hasApplied)
            {
                track.instrument.sampleResourcesPath = path;
                track.instrument.sliceStart01 = (float[])starts.Clone();
                track.instrument.sliceEnd01 = (float[])ends.Clone();
            }

            // Repair an existing clip with missing content rather than creating
            // overlapping placements on an imported or partially saved project.
            if (clip != null && content == null)
            {
                clip = ArrangementEditing.FindClip(song, clipId, out _);
                if (clip != null)
                {
                    if (song.contents == null) song.contents = new List<ClipContentData>();
                    content = new ClipContentData {
                        id = ArrangementEditing.Id("content"),
                        kind = ClipKind.Chops,
                        lengthTicks = Math.Max(SongData.TicksPerSixteenth, clip.lengthTicks)
                    };
                    song.contents.Add(content);
                    clip.contentId = content.id;
                }
            }
            else if (clip == null)
            {
                // The track may have saved slices but no arrangement events yet.
                long start = ArrangementEditing.NextFreeStart(track, 0, SongData.TicksPerBar4);
                clip = ArrangementEditing.CreateClip(song, track, start);
                content = clip == null ? null : ArrangementEditing.FindContent(song, clip.contentId);
                if (content != null)
                {
                    // Restore already sequenced legacy chops if present. Slice
                    // markers alone never become automatic note-on events.
                    int[] steps = null;
                    var sampler = FindObjectOfType<KMusicSampleSequencerUI>();
                    if (sampler != null) steps = sampler.CaptureSampleStepsFlat();
                    if (!HasChopSteps(steps))
                    {
                        FindDrumEngine();
                        if (drumEngine != null) steps = drumEngine.CaptureSampleStepsFlat();
                    }
                    if (steps != null)
                    {
                        if (content.chops == null) content.chops = new List<ChopEvent>();
                        for (int step = 0; step < Math.Min(16, steps.Length); step++)
                            if (steps[step] >= 1 && steps[step] <= 16)
                                content.chops.Add(new ChopEvent {
                                    sliceId = steps[step],
                                    startTick = step * SongData.TicksPerSixteenth,
                                    velocity = 100
                                });
                    }
                }
            }
            if (clip != null) clipId = clip.id;
            projects.AcceptArrangementEdit(song);
            OnClipChanged?.Invoke();
        }

        private static bool SameSlices(float[] current, float[] applied)
        {
            if (ReferenceEquals(current, applied)) return true;
            if (current == null || applied == null || current.Length != applied.Length) return false;
            for (int i = 0; i < current.Length; i++)
                if (Mathf.Abs(current[i] - applied[i]) > 0.00001f) return false;
            return true;
        }

        private static bool HasChopSteps(int[] steps)
        {
            if (steps == null) return false;
            for (int i = 0; i < Math.Min(16, steps.Length); i++)
                if (steps[i] >= 1 && steps[i] <= 16) return true;
            return false;
        }

        public void OnShown(int requestedTrack=-1, string requestedClip=null)
        {
            if (!ready) return;
            ShowSampler(false);
            if (projects == null) projects = FindObjectOfType<KMusicProjectManager>();
            if (projects == null) return;
            song = projects.GetSongForArrangementEditor();
            if (sampleKeyGuideEnabled && sampleKeyGuideProjectSlot != projects.CurrentSlot)
                sampleKeyGuideEnabled = false;
            if (song == null || song.tracks == null || song.tracks.Count == 0) return;
            if (requestedTrack >= 0 && requestedTrack < song.tracks.Count) trackIndex = requestedTrack;
            trackIndex = Mathf.Clamp(trackIndex,0,song.tracks.Count-1);
            if (!string.IsNullOrEmpty(requestedClip)) clipId = requestedClip;
            LoadClip();
            EnsureChopsEditable();
            Redraw(false);
            FocusPatternNotes();
        }
        private void LoadClip()
        {
            clip = ArrangementEditing.FindClip(song,clipId,out TrackData owner);
            if (clip == null || owner != song.tracks[trackIndex])
            {
                clip = null;
                var list = song.tracks[trackIndex].clips;
                if (list != null && list.Count > 0) clip = list[0];
            }
            clipId = clip != null ? clip.id : null;
            content = clip == null ? null : ArrangementEditing.FindContent(song,clip.contentId);
            selectedNote = -1;
            selectedEventRow = -1;
            selectedEventTick = -1;
        }
        private bool Writable()
        {
            if (projects == null || song == null) return false;
            if (!ReferenceEquals(projects.CurrentSong,song))
                song = ArrangementEditing.IndependentCopy(song);
            if (song == null) return false;
            projects.AcceptArrangementEdit(song);
            clip = ArrangementEditing.FindClip(song,clipId,out _);
            content = clip == null ? null : ArrangementEditing.FindContent(song,clip.contentId);
            // Legacy migration can reuse the same content in multiple placements.
            // Give the edited clip independent notes to avoid accidental linked edits.
            if (clip != null && content != null)
            {
                int refs = 0;
                foreach(var t in song.tracks)
                    if (t != null && t.clips != null)
                        foreach(var c in t.clips)
                            if (c != null && c.contentId == content.id) refs++;
                if (refs > 1)
                {
                    content = JsonUtility.FromJson<ClipContentData>(JsonUtility.ToJson(content));
                    content.id = ArrangementEditing.Id("content");
                    song.contents.Add(content);
                    clip.contentId = content.id;
                }
            }
            return true;
        }
        private void Changed(bool rebuild=true, bool preserveViewport=true)
        {
            if (rebuild) Redraw(preserveViewport);
            OnClipChanged?.Invoke();
            // The workspace callback updates the currently sounding transport plan live.
        }
        private void NewClip()
        {
            if (!Writable()) return;
            var track = song.tracks[trackIndex];
            long start = ArrangementEditing.NextFreeStart(track,0,SongData.TicksPerBar4);
            clip = ArrangementEditing.CreateClip(song,track,start);
            clipId = clip != null ? clip.id : null;
            content = clip == null ? null : ArrangementEditing.FindContent(song,clip.contentId);
            selectedNote = -1;
            selectedEventRow = -1;
            selectedEventTick = -1;
            Changed(true, false);
            FocusPatternNotes();
        }
        private void Save()
        {
            if (projects == null) return;
            projects.SaveProject(projects.CurrentSlot);
            if (info != null) info.text = "SAVED";
        }
        private void SelectTrack(int index)
        {
            ShowSampler(false);
            trackIndex = index;
            clipId = null;
            LoadClip();
            EnsureChopsEditable();
            Redraw(false);
            FocusPatternNotes();
        }
        private void ChangeLength(int direction)
        {
            if (content == null || !Writable()) return;
            if (content.kind == ClipKind.Midi && content.notes != null && selectedNote >= 0 && selectedNote < content.notes.Count)
            {
                var n = content.notes[selectedNote];
                n.durationTicks = Math.Max(quantum,n.durationTicks + direction * quantum);
                Changed();
            }
            else if (clip != null)
            {
                ArrangementEditing.Resize(song,clip,Math.Max(quantum,clip.lengthTicks + direction * SongData.TicksPerBar4));
                if (content.lengthTicks < clip.lengthTicks) content.lengthTicks = clip.lengthTicks;
                Changed();
            }
        }
        private void DeleteSelectedNote()
        {
            if (content == null || content.kind != ClipKind.Midi || selectedNote < 0 ||
                content.notes == null || selectedNote >= content.notes.Count || !Writable()) return;
            var selected = content.notes[selectedNote];
            if (selected == null) return;
            // An earlier version could stack a manual note directly over a humanized
            // generated note. Delete the complete same-pitch, same-step stack, rather
            // than leaving an inaccessible note underneath the visible one.
            int pitch = selected.pitch;
            int step = GridStepForTick(selected.startTick);
            for (int i = content.notes.Count - 1; i >= 0; i--)
            {
                var note = content.notes[i];
                if (note != null && note.pitch == pitch && GridStepForTick(note.startTick) == step)
                    content.notes.RemoveAt(i);
            }
            selectedNote = -1;
            Changed();
        }
        // The slider edits the selected event, or acts as the brush velocity when
        // nothing is selected. Selecting never toggles/deletes the event.
        private void RefreshVelocityRail()
        {
            float percent = Mathf.Clamp(velocity, 1, 127) / 127f * 100f;
            if (velocityFill != null) velocityFill.style.width = new Length(percent, LengthUnit.Percent);
            if (velocityThumb != null) velocityThumb.style.left = new Length(percent, LengthUnit.Percent);
            if (selectedInfo != null) selectedInfo.text = velocity.ToString();
        }
        private void SelectNoteVelocity(int index)
        {
            if (content == null || content.notes == null || index < 0 || index >= content.notes.Count ||
                content.notes[index] == null) return;
            selectedNote = index;
            selectedEventRow = -1;
            selectedEventTick = -1;
            velocity = Mathf.Clamp(content.notes[index].velocity, 1, 127);
            RefreshVelocityRail();
            RefreshVelocityDisplays();
        }
        private bool HasStepEvent(int row, long tick)
        {
            if (content == null) return false;
            int step = GridStepForTick(tick);
            if (content.kind == ClipKind.Drums) return FindDrumStepIndex(row, step) >= 0;
            if (content.kind == ClipKind.Chops) return FindChopStepIndex(row, step) >= 0;
            return false;
        }
        private void SelectStepVelocity(int row, long tick)
        {
            if (content == null) return;
            int step = GridStepForTick(tick);
            int index = content.kind == ClipKind.Drums ? FindDrumStepIndex(row, step) :
                        content.kind == ClipKind.Chops ? FindChopStepIndex(row, step) : -1;
            if (index < 0) return;
            // Keep the REAL (humanized) tick for subsequent velocity updates.
            // Replacing it with the grid tick would make the selected event
            // inaccessible to ChangeVelocity().
            long actualTick;
            if (content.kind == ClipKind.Drums)
            {
                var hit = content.drums[index];
                actualTick = hit.startTick;
                velocity = Mathf.Clamp(hit.velocity, 1, 127);
            }
            else
            {
                var hit = content.chops[index];
                actualTick = hit.startTick;
                velocity = Mathf.Clamp(hit.velocity, 1, 127);
            }
            selectedNote = -1;
            selectedEventRow = row;
            selectedEventTick = actualTick;
            RefreshVelocityRail();
            RefreshVelocityDisplays();
        }
        private void RefreshVelocityDisplays()
        {
            foreach (var display in velocityDisplays)
            {
                bool selected = IsMidi ? display.noteIndex == selectedNote && selectedNote >= 0 :
                    display.row == selectedEventRow && display.tick == selectedEventTick && selectedEventRow >= 0;
                display.cell.style.backgroundColor = selected ? Accent : colors[Mathf.Min(trackIndex, 4)];
                display.cell.style.borderBottomWidth = selected ? 2 : 0;
                display.cell.style.borderBottomColor = Ink;
                float span = Mathf.Max(1f, display.width - 6f);
                display.meter.style.width = Mathf.Max(1f, span * Mathf.Clamp(display.velocity, 1, 127) / 127f);
                if (display.number != null) display.number.text = Mathf.Clamp(display.velocity, 1, 127).ToString();
            }
        }
        private void ChangeVelocity()
        {
            if (selectedInfo != null) selectedInfo.text = velocity.ToString();
            if (content == null) return;
            if (content.kind == ClipKind.Midi)
            {
                if (selectedNote < 0 || content.notes == null || selectedNote >= content.notes.Count ||
                    content.notes[selectedNote] == null || content.notes[selectedNote].velocity == velocity || !Writable()) return;
                content.notes[selectedNote].velocity = velocity;
            }
            else if (content.kind == ClipKind.Drums)
            {
                if (content.drums == null || selectedEventRow < 0) return;
                var hit = content.drums.Find(d => d != null && d.lane == selectedEventRow && d.startTick == selectedEventTick);
                if (hit == null || hit.velocity == velocity || !Writable()) return;
                hit = content.drums.Find(d => d != null && d.lane == selectedEventRow && d.startTick == selectedEventTick);
                if (hit == null) return;
                hit.velocity = velocity;
            }
            else if (content.kind == ClipKind.Chops)
            {
                if (content.chops == null || selectedEventRow < 0) return;
                var hit = content.chops.Find(d => d != null && d.sliceId == selectedEventRow + 1 && d.startTick == selectedEventTick);
                if (hit == null || hit.velocity == velocity || !Writable()) return;
                hit = content.chops.Find(d => d != null && d.sliceId == selectedEventRow + 1 && d.startTick == selectedEventTick);
                if (hit == null) return;
                hit.velocity = velocity;
            }
            else return;
            // Update the active cell without rebuilding the piano roll while the
            // slider is moving; the scroll position and touch gesture stay put.
            foreach (var display in velocityDisplays)
            {
                bool selected = IsMidi ? display.noteIndex == selectedNote && selectedNote >= 0 :
                    display.row == selectedEventRow && display.tick == selectedEventTick && selectedEventRow >= 0;
                if (selected) display.velocity = velocity;
            }
            RefreshVelocityDisplays();
            OnClipChanged?.Invoke();
        }
        private bool IsMidi => content != null && content.kind == ClipKind.Midi;
        private int RowCount => IsMidi ? MidiRows : content != null && content.kind == ClipKind.Chops ? 16 : 8;
        private int PitchForRow(int row) => Mathf.Clamp(127 - row, 0, 127);
        private int RowForPitch(int pitch) => 127 - pitch;

        private static bool IsGuidePitch(int pitch, int tonic, bool minor)
        {
            int interval = ((pitch - tonic) % 12 + 12) % 12;
            // Major 0,2,4,5,7,9,11; natural minor 0,2,3,5,7,8,10.
            int intervals = minor ? 0x5AD : 0xAB5;
            return ((intervals >> interval) & 1) != 0;
        }

        // Colors are intentionally restrained and do not obscure note velocities.
        // Recolor existing rows without rebuilding the clip or resetting its viewport.
        private void ApplySampleKeyGuide()
        {
            bool enabled = sampleKeyGuideEnabled && IsMidi && song != null &&
                song.tracks != null && trackIndex >= 0 && trackIndex < song.tracks.Count &&
                (song.tracks[trackIndex].kind == TrackKind.Bass ||
                 song.tracks[trackIndex].kind == TrackKind.Synth ||
                 song.tracks[trackIndex].kind == TrackKind.Pads);
            for (int row = 0; row < pitchRowBackgrounds.Count; row++)
            {
                int pitch = PitchForRow(row);
                bool inKey = enabled && IsGuidePitch(pitch, sampleKeyGuideTonic, sampleKeyGuideMinor);
                bool tonic = inKey && pitch % 12 == sampleKeyGuideTonic;
                pitchRowBackgrounds[row].style.backgroundColor = tonic
                    ? new Color(.125f, .215f, .213f)
                    : inKey ? new Color(.108f, .174f, .182f)
                    : row % 2 == 0 ? new Color(.11f, .145f, .19f) : new Color(.09f, .12f, .16f);
                if (row < pitchRowLabels.Count)
                    pitchRowLabels[row].style.color = tonic ? Accent :
                        inKey ? new Color(.68f, .82f, .79f) : pitch % 12 == 0 ? Accent : Muted;
            }
        }

        // Follow live semitone changes only after the user chose USE SAMPLE KEY.
        // This is deliberately independent of generatorKey's manual selector.
        private void FollowPitchedSampleKey()
        {
            if (!sampleKeyGuideEnabled || samplePlayer == null) return;
            // Selecting a different sample must not silently transfer the old guide.
            string sampleId = samplePlayer.DawSampleFolder + "/" + samplePlayer.DawSampleName;
            if (sampleId != sampleKeyGuideSampleId)
            {
                sampleKeyGuideEnabled = false;
                ApplySampleKeyGuide();
                RefreshClipSampleKeyReference();
                return;
            }
            if (!samplePlayer.DawSampleKeyAvailable) return;
            int tonic = samplePlayer.DawPitchedKeyTonic;
            bool minor = samplePlayer.DawSampleKeyMinor;
            if (tonic == sampleKeyGuideTonic && minor == sampleKeyGuideMinor) return;
            sampleKeyGuideTonic = tonic;
            sampleKeyGuideMinor = minor;
            ApplySampleKeyGuide();
            RefreshClipSampleKeyReference();
        }

        private void SyncFixedRuler()
        {
            if (rulerContent == null || scroller == null) return;
            rulerContent.style.translate = new Translate(
                new Length(-scroller.scrollOffset.x, LengthUnit.Pixel),
                new Length(0f, LengthUnit.Pixel), 0f);
        }

        // Keep the fixed label column aligned with the vertically scrolling notes.
        // Translation avoids relaying out the 128-row MIDI keyboard on each scroll.
        private void SyncPianoRollLabels()
        {
            if (labelColumn == null || scroller == null) return;
            labelColumn.style.translate = new Translate(
                new Length(0f, LengthUnit.Pixel),
                new Length(-scroller.scrollOffset.y, LengthUnit.Pixel), 0f);
        }

        // Auto-frame the populated pitch area on pattern load or generation.
        // Never invoke this from an ordinary paint/drag redraw: doing so would
        // fight the user's manual scrolling and touch gestures.
        private void FocusPatternNotes()
        {
            if (scroller == null || !IsMidi) return;
            if (content.notes == null || content.notes.Count == 0)
            {
                ScrollToOctave();
                return;
            }

            var noteRows = new List<int>();
            foreach (var note in content.notes)
                if (note != null) noteRows.Add(RowForPitch(Mathf.Clamp(note.pitch, 0, 127)));
            if (noteRows.Count == 0) { ScrollToOctave(); return; }
            noteRows.Sort();
            int revision = ++noteFocusRevision;
            var intendedScroller = scroller;
            intendedScroller.schedule.Execute(() =>
            {
                if (revision != noteFocusRevision || scroller != intendedScroller || !IsMidi) return;
                float height = intendedScroller.contentViewport.resolvedStyle.height;
                if (height < RowH * 2f) height = intendedScroller.resolvedStyle.height;
                if (height < RowH * 2f) height = RowH * 7f;
                int rowsVisible = Mathf.Max(2, Mathf.FloorToInt((height - 25f) / RowH));

                // Fit all notes if possible. Otherwise show the densest group
                // of notes within the visible rows (not merely the first note).
                int top = noteRows[0], bottom = noteRows[noteRows.Count - 1];
                if (bottom - top >= rowsVisible - 2)
                {
                    int bestStart = 0, bestEnd = 0, end = 0;
                    for (int start = 0; start < noteRows.Count; start++)
                    {
                        if (end < start) end = start;
                        while (end + 1 < noteRows.Count &&
                               noteRows[end + 1] - noteRows[start] <= rowsVisible - 2)
                            end++;
                        if (end - start > bestEnd - bestStart)
                        {
                            bestStart = start;
                            bestEnd = end;
                        }
                    }
                    top = noteRows[bestStart];
                    bottom = noteRows[bestEnd];
                }
                float middle = 0.5f * (top + bottom);
                float y = Mathf.Max(0f, 25f + (middle + .5f) * RowH - height * .5f);
                float maxY = Mathf.Max(0f, 25f + MidiRows * RowH - height);
                // Also bring a pattern's first note into horizontal view if the
                // previous clip was scrolled to a different part of the timeline.
                long earliestTick = long.MaxValue;
                foreach (var note in content.notes)
                    if (note != null && note.startTick < earliestTick) earliestTick = note.startTick;
                float earliestX = earliestTick == long.MaxValue ? 0f : (float)earliestTick / quantum * StepW;
                float visibleWidth = intendedScroller.contentViewport.resolvedStyle.width;
                if (visibleWidth < StepW * 2f) visibleWidth = StepW * 8f;
                float gridWidth = steps * StepW;
                float maxX = Mathf.Max(0f, gridWidth - visibleWidth);
                float x = Mathf.Clamp(earliestX - StepW, 0f, maxX);
                intendedScroller.scrollOffset = new Vector2(x, Mathf.Min(y, maxY));
                SyncPianoRollLabels();
                SyncFixedRuler();
                int pitch = Mathf.Clamp(127 - Mathf.RoundToInt(middle), 0, 127);
                octave = Mathf.Clamp(pitch / 12 - 1, -1, 9);
            }).ExecuteLater(20);
        }

        // Full 128-note keyboard: OCT buttons scroll vertically, not horizontally.
        private void ScrollToOctave()
        {
            // Manual octave navigation must supersede an older scheduled auto-focus.
            ++noteFocusRevision;
            if (scroller == null || !IsMidi) return;
            int desiredTopPitch = Mathf.Clamp((octave + 2) * 12 - 1, 0, 127);
            float y = 25f + (127 - desiredTopPitch) * RowH;
            scroller.schedule.Execute(() =>
            {
                if (scroller != null && IsMidi)
                {
                    scroller.scrollOffset = new Vector2(scroller.scrollOffset.x, y);
                    SyncPianoRollLabels();
                }
            }).ExecuteLater(0);
        }
        private float EditorHeight => RowCount * RowH;
        private int QuantizedStep(float x) => Mathf.Clamp(Mathf.FloorToInt(Mathf.Max(0,x)/StepW),0,steps-1);
        private long TickAt(float x) => QuantizedStep(x)*quantum;
        private int RowAt(float y) => Mathf.Clamp(Mathf.FloorToInt(y/RowH),0,RowCount-1);
        // Generator humanization deliberately offsets notes slightly from each
        // sixteenth-note boundary. The editor must identify events by their musical
        // cell, not by exact tick equality, or tapping an existing event creates a
        // second one on top of it. No event timing is changed by these lookups.
        private int GridStepForTick(long tick)
        {
            return (int)Math.Max(0L, (Math.Max(0L, tick) + quantum / 2) / quantum);
        }

        private int FindDrumStepIndex(int row, int step)
        {
            if (content?.drums == null) return -1;
            int best = -1;
            long distance = long.MaxValue;
            long gridTick = (long)step * quantum;
            for (int i = 0; i < content.drums.Count; i++)
            {
                var hit = content.drums[i];
                if (hit == null || hit.lane != row || GridStepForTick(hit.startTick) != step) continue;
                long delta = Math.Abs(hit.startTick - gridTick);
                if (delta < distance) { best = i; distance = delta; }
            }
            return best;
        }

        private int FindChopStepIndex(int row, int step)
        {
            if (content?.chops == null) return -1;
            int best = -1;
            long distance = long.MaxValue;
            long gridTick = (long)step * quantum;
            for (int i = 0; i < content.chops.Count; i++)
            {
                var hit = content.chops[i];
                if (hit == null || hit.sliceId != row + 1 || GridStepForTick(hit.startTick) != step) continue;
                long delta = Math.Abs(hit.startTick - gridTick);
                if (delta < distance) { best = i; distance = delta; }
            }
            return best;
        }

        private int FindNote(int pitch, long tick)
        {
            if (content?.notes == null) return -1;
            int step = GridStepForTick(tick);
            // Prefer a note starting in this step over a note sustained from an
            // earlier one. Scan in reverse to handle legacy duplicate stacks.
            for (int i = content.notes.Count - 1; i >= 0; i--)
            {
                var n = content.notes[i];
                if (n != null && n.pitch == pitch && GridStepForTick(n.startTick) == step)
                    return i;
            }
            // A longer generated MIDI note also occupies subsequent grid cells.
            // Check intersection with the cell, including microtimed attacks.
            long cellEnd = tick + quantum;
            for (int i = content.notes.Count - 1; i >= 0; i--)
            {
                var n = content.notes[i];
                if (n != null && n.pitch == pitch &&
                    n.startTick < cellEnd && n.startTick + n.durationTicks > tick)
                    return i;
            }
            return -1;
        }

        // Resolve taps against the *drawn* rectangle before falling back to a
        // quantized grid step. Humanized hits can straddle the next cell by a
        // couple of pixels; selecting from the artwork prevents a second note
        // being inserted when the user taps that visible trailing edge.
        private int HitStepAtVisualPosition(int row, int defaultStep, float x)
        {
            if (content == null) return defaultStep;
            const float hitSlop = 1.5f;
            float nearest = float.MaxValue;
            int foundStep = -1;
            if (content.kind == ClipKind.Drums && content.drums != null)
            {
                foreach (var hit in content.drums)
                {
                    if (hit == null || hit.lane != row) continue;
                    float left = (float)hit.startTick / quantum * StepW + 1f;
                    float width = Mathf.Max(13f, StepW - 2f);
                    if (x < left - hitSlop || x > left + width + hitSlop) continue;
                    float distance = Mathf.Abs(x - (left + width * .5f));
                    if (distance >= nearest) continue;
                    nearest = distance;
                    foundStep = GridStepForTick(hit.startTick);
                }
            }
            else if (content.kind == ClipKind.Chops && content.chops != null)
            {
                foreach (var hit in content.chops)
                {
                    if (hit == null || hit.sliceId != row + 1) continue;
                    float left = (float)hit.startTick / quantum * StepW + 1f;
                    float width = Mathf.Max(13f, StepW - 2f);
                    if (x < left - hitSlop || x > left + width + hitSlop) continue;
                    float distance = Mathf.Abs(x - (left + width * .5f));
                    if (distance >= nearest) continue;
                    nearest = distance;
                    foundStep = GridStepForTick(hit.startTick);
                }
            }
            else if (content.kind == ClipKind.Midi && content.notes != null)
            {
                int pitch = PitchForRow(row);
                foreach (var note in content.notes)
                {
                    if (note == null || note.pitch != pitch) continue;
                    float left = (float)note.startTick / quantum * StepW + 1f;
                    float width = Mathf.Max(13f, (float)note.durationTicks / quantum * StepW - 2f);
                    if (x < left - hitSlop || x > left + width + hitSlop) continue;
                    float distance = Mathf.Abs(x - (left + width * .5f));
                    if (distance >= nearest) continue;
                    nearest = distance;
                    foundStep = GridStepForTick(note.startTick);
                }
            }
            return foundStep >= 0 ? foundStep : defaultStep;
        }

        private void ToggleGrid(int row,int step)
        {
            if (content == null || !Writable()) return;
            long tick = step * quantum;
            if (content.kind == ClipKind.Drums)
            {
                if (content.drums == null) content.drums = new List<DrumEvent>();
                int i = FindDrumStepIndex(row, step);
                if (i >= 0)
                {
                    // Clear any pre-existing duplicate stack in this musical cell.
                    for (int j = content.drums.Count - 1; j >= 0; j--)
                    {
                        var hit = content.drums[j];
                        if (hit != null && hit.lane == row && GridStepForTick(hit.startTick) == step)
                            content.drums.RemoveAt(j);
                    }
                    if (selectedEventRow == row && GridStepForTick(selectedEventTick) == step)
                    { selectedEventRow = -1; selectedEventTick = -1; }
                }
                else
                {
                    content.drums.Add(new DrumEvent { lane = row, startTick = tick, velocity = velocity, legacyVelocityTier = 2 });
                    selectedEventRow = row;
                    selectedEventTick = tick;
                }
                selectedNote = -1;
            }
            else if (content.kind == ClipKind.Chops)
            {
                if (content.chops == null) content.chops = new List<ChopEvent>();
                int i = FindChopStepIndex(row, step);
                if (i >= 0)
                {
                    for (int j = content.chops.Count - 1; j >= 0; j--)
                    {
                        var hit = content.chops[j];
                        if (hit != null && hit.sliceId == row + 1 && GridStepForTick(hit.startTick) == step)
                            content.chops.RemoveAt(j);
                    }
                    if (selectedEventRow == row && GridStepForTick(selectedEventTick) == step)
                    { selectedEventRow = -1; selectedEventTick = -1; }
                }
                else
                {
                    content.chops.Add(new ChopEvent { sliceId = row + 1, startTick = tick, velocity = velocity });
                    selectedEventRow = row;
                    selectedEventTick = tick;
                }
                selectedNote = -1;
            }
            else
            {
                if (content.notes == null) content.notes = new List<NoteEvent>();
                int pitch = PitchForRow(row);
                int existing = FindNote(pitch, tick);
                if (existing >= 0)
                {
                    var hit = content.notes[existing];
                    int startingStep = GridStepForTick(hit.startTick);
                    // Remove a legacy pile of same-pitch notes that start on the
                    // same step; tapping must not reveal another hidden copy.
                    for (int i = content.notes.Count - 1; i >= 0; i--)
                    {
                        var n = content.notes[i];
                        if (n != null && n.pitch == pitch && GridStepForTick(n.startTick) == startingStep)
                            content.notes.RemoveAt(i);
                    }
                    selectedNote = -1;
                }
                else
                {
                    content.notes.Add(new NoteEvent { pitch = pitch, startTick = tick, durationTicks = quantum, velocity = velocity });
                    selectedNote = content.notes.Count - 1;
                }
                selectedEventRow = -1;
            }
            content.lengthTicks = Math.Max(content.lengthTicks, tick + quantum);
            Changed();
        }

        // Use the grid's local coordinates, which continue advancing correctly
        // while edge auto-scroll shifts the viewport beneath a stationary finger.
        // The starting drag mode is fixed on pointer-down: body=move, end=resize.
        private void UpdateMidiDragPreview(Drag d, Vector2 localPosition)
        {
            if (d == null) return;
            float dx = localPosition.x - d.start.x;
            float dy = localPosition.y - d.start.y;
            int stepDelta = Mathf.RoundToInt(dx / StepW);
            long gridEnd = Math.Max(quantum, (long)steps * quantum);
            long newStart = d.startTick;
            long newDuration = d.duration;
            int newPitch = d.pitch;

            if (d.mode == MidiDragMode.ResizeEnd)
            {
                // Only the right end moves. The start and pitch stay anchored.
                newDuration = Math.Max(quantum, Math.Min(gridEnd - d.startTick,
                    d.duration + stepDelta * quantum));
            }
            else // MidiDragMode.Move
            {
                // Move the WHOLE note by sixteenth-note steps; length stays fixed.
                // The same gesture can also shift pitch without moving the viewport.
                long maxStart = Math.Max(0L, gridEnd - d.duration);
                newStart = Math.Max(0L, Math.Min(maxStart,
                    d.startTick + stepDelta * quantum));
                newPitch = Mathf.Clamp(d.pitch - Mathf.RoundToInt(dy / RowH), 0, 127);
            }

            d.candidateTick = newStart;
            d.candidateDuration = newDuration;
            d.candidatePitch = newPitch;
            if (d.preview == null) return;
            float width = Mathf.Max(13f, (float)newDuration / quantum * StepW - 2f);
            d.preview.style.left = (float)newStart / quantum * StepW + 1f;
            d.preview.style.top = RowForPitch(newPitch) * RowH + 4f;
            d.preview.style.width = width;
            SyncMidiNoteTouchTargets();
            // Keep the velocity meter and number visually coherent as the note changes width.
            if (d.display != null)
            {
                d.display.meter.style.width = Mathf.Max(1f, (width - 6f) *
                    Mathf.Clamp(d.display.velocity, 1, 127) / 127f);
                if (d.display.number != null)
                    d.display.number.style.display = width >= 28f ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        private void RestoreMidiDragPreview(Drag d)
        {
            if (d == null || d.preview == null) return;
            float width = Mathf.Max(13f, (float)d.duration / quantum * StepW - 2f);
            d.preview.style.left = (float)d.startTick / quantum * StepW + 1f;
            d.preview.style.top = RowForPitch(d.pitch) * RowH + 4f;
            d.preview.style.width = width;
            if (d.display != null)
            {
                d.display.meter.style.width = Mathf.Max(1f, (width - 6f) *
                    Mathf.Clamp(d.display.velocity, 1, 127) / 127f);
                if (d.display.number != null)
                    d.display.number.style.display = width >= 28f ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        // UI Toolkit's ScrollView has its own touch pan manipulator. Capture on
        // the note plane and reassert only this intentional offset while editing,
        // so vertical note dragging never scrolls the entire 128-key keyboard.
        private void KeepMidiDragViewportLocked()
        {
            if (drag == null || scroller == null) return;
            if ((scroller.scrollOffset - drag.allowedScrollOffset).sqrMagnitude > .1f)
                scroller.scrollOffset = drag.allowedScrollOffset;
        }

        private static float MidiEdgeScrollVelocity(float pos, float min, float max)
        {
            if (pos < min + MidiEdgeScrollZone)
                return -Mathf.Clamp01((min + MidiEdgeScrollZone - pos) / MidiEdgeScrollZone) *
                    MidiMaxEdgeScrollPerTick;
            if (pos > max - MidiEdgeScrollZone)
                return Mathf.Clamp01((pos - (max - MidiEdgeScrollZone)) / MidiEdgeScrollZone) *
                    MidiMaxEdgeScrollPerTick;
            return 0f;
        }

        // Runs during an active gesture even when the finger is held stationary
        // at the viewport's edge (there may be no further PointerMoveEvents).
        private void AutoScrollMidiDrag(Drag d, VisualElement plane)
        {
            if (!ReferenceEquals(drag, d) || !d.moved || scroller == null ||
                scroller.contentViewport == null || plane == null) return;
            var bounds = scroller.contentViewport.worldBound;
            if (bounds.width <= 1f || bounds.height <= 1f) return;
            float sx = 0f, sy = 0f;
            // Body drags can cross both time and pitch boundaries. Resize handles
            // only need horizontal edge scrolling; never pan vertically while resizing.
            sx = MidiEdgeScrollVelocity(d.latestWorldPosition.x, bounds.xMin, bounds.xMax);
            if (d.mode == MidiDragMode.Move)
                sy = MidiEdgeScrollVelocity(d.latestWorldPosition.y, bounds.yMin, bounds.yMax);
            if (Mathf.Abs(sx) < .01f && Mathf.Abs(sy) < .01f) return;
            float maxX = Mathf.Max(0f, steps * StepW - bounds.width);
            float maxY = Mathf.Max(0f, 25f + EditorHeight - bounds.height);
            Vector2 desired = new Vector2(
                Mathf.Clamp(d.allowedScrollOffset.x + sx, 0f, maxX),
                Mathf.Clamp(d.allowedScrollOffset.y + sy, 0f, maxY));
            if ((desired - d.allowedScrollOffset).sqrMagnitude <= .01f) return;
            d.allowedScrollOffset = desired;
            scroller.scrollOffset = desired;
            SyncPianoRollLabels();
            SyncFixedRuler();
            UpdateMidiDragPreview(d, MidiWorldToPlane(d.latestWorldPosition));
            SyncMidiNoteTouchTargets();
        }

        // Coordinates are measured in the moving grid's own space. Using the
        // transparent overlay coordinates plus the ScrollView offset keeps note
        // movement accurate even while the viewport follows the finger.
        private Vector2 MidiWorldToPlane(Vector2 worldPosition)
        {
            if (noteTouchLayer == null || scroller == null) return worldPosition;
            Vector2 position = noteTouchLayer.WorldToLocal(worldPosition);
            return position + scroller.scrollOffset;
        }

        // Frame a tapped MIDI note when it sits partly outside the viewport.
        // While the user drags, the edge-scroll scheduler follows as needed.
        private void FocusSelectedMidiNote(int index)
        {
            if (scroller == null || scroller.contentViewport == null || !IsMidi ||
                content?.notes == null || index < 0 || index >= content.notes.Count) return;
            var note = content.notes[index];
            if (note == null) return;
            float vw = scroller.contentViewport.resolvedStyle.width;
            float vh = scroller.contentViewport.resolvedStyle.height;
            if (vw < StepW || vh < RowH) return;
            float nx = (float)note.startTick / quantum * StepW;
            float ny = 25f + RowForPitch(note.pitch) * RowH;
            var offset = scroller.scrollOffset;
            const float xMargin = 20f;
            const float yMargin = 18f;
            float x = offset.x, y = offset.y;
            if (nx < x + xMargin) x = nx - xMargin;
            else if (nx > x + vw - xMargin - StepW) x = nx - vw + xMargin + StepW;
            // For a long note, favour its beginning rather than scrolling
            // to its right edge and losing the selected pitch on-screen.
            if (ny < y + yMargin) y = ny - yMargin;
            else if (ny + RowH > y + vh - yMargin) y = ny + RowH - vh + yMargin;
            float maxX = Mathf.Max(0f, steps * StepW - vw);
            float maxY = Mathf.Max(0f, 25f + EditorHeight - vh);
            var desired = new Vector2(Mathf.Clamp(x, 0f, maxX), Mathf.Clamp(y, 0f, maxY));
            if ((desired - offset).sqrMagnitude > 0.25f)
            {
                scroller.scrollOffset = desired;
                SyncPianoRollLabels();
                SyncFixedRuler();
                SyncMidiNoteTouchTargets();
            }
        }

        // Create one transparent, independent touch target for each MIDI note.
        // The rightmost portion is the resize anchor; the body moves the note.
        private void AddMidiNoteTouchTarget(int index)
        {
            if (noteTouchLayer == null || content?.notes == null ||
                index < 0 || index >= content.notes.Count || content.notes[index] == null) return;
            var hit = new VisualElement { name = "ZPunkMidiNoteHit_" + index };
            hit.style.position = Position.Absolute;
            hit.pickingMode = PickingMode.Position;
            var rightEnd = new VisualElement { name = "ZPunkMidiNoteRightEnd_" + index };
            rightEnd.style.position = Position.Absolute;
            rightEnd.style.right = 0;
            rightEnd.style.top = 0;
            rightEnd.style.bottom = 0;
            rightEnd.style.width = 12;
            rightEnd.pickingMode = PickingMode.Position;
            hit.Add(rightEnd);
            noteTouchLayer.Add(hit);
            midiNoteTouchTargets.Add(new MidiNoteTouchTarget { body = hit, noteIndex = index });

            hit.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0 || !IsMidi || drag != null ||
                    content?.notes == null || index >= content.notes.Count) return;
                var note = content.notes[index];
                if (note == null) return;
                SelectNoteVelocity(index);
                FocusSelectedMidiNote(index);
                var display = velocityDisplays.Find(v => v.noteIndex == index);
                Vector2 startPosition = MidiWorldToPlane(evt.position);
                drag = new Drag {
                    index = index, pointerId = evt.pointerId,
                    start = startPosition, startTick = note.startTick,
                    duration = note.durationTicks, pitch = note.pitch,
                    mode = ReferenceEquals(evt.target, rightEnd) ? MidiDragMode.ResizeEnd : MidiDragMode.Move,
                    preview = display != null ? display.cell : null, display = display,
                    latestWorldPosition = evt.position,
                    allowedScrollOffset = scroller.scrollOffset,
                    candidateTick = note.startTick, candidateDuration = note.durationTicks,
                    candidatePitch = note.pitch
                };
                var active = drag;
                // Scheduled scrolling also works if the finger is held motionless
                // at an edge of the viewport.
                active.edgeScroll = hit.schedule.Execute(() =>
                    AutoScrollMidiDrag(active, hit)).Every(16);
                hit.CapturePointer(evt.pointerId);
                evt.StopPropagation();
            });
            hit.RegisterCallback<PointerMoveEvent>(evt =>
            {
                if (drag == null || drag.pointerId != evt.pointerId ||
                    !hit.HasPointerCapture(evt.pointerId)) return;
                drag.latestWorldPosition = evt.position;
                KeepMidiDragViewportLocked();
                Vector2 position = MidiWorldToPlane(evt.position);
                if ((position - drag.start).sqrMagnitude > MidiGestureThreshold * MidiGestureThreshold)
                    drag.moved = true;
                if (drag.moved) UpdateMidiDragPreview(drag, position);
                evt.StopPropagation();
            });
            hit.RegisterCallback<PointerUpEvent>(evt =>
            {
                if (drag == null || drag.pointerId != evt.pointerId ||
                    !hit.HasPointerCapture(evt.pointerId)) return;
                var d = drag;
                KeepMidiDragViewportLocked();
                if (d.moved) UpdateMidiDragPreview(d, MidiWorldToPlane(evt.position));
                // Release capture first: Changed() rebuilds the overlay and
                // must not trigger cancellation of an already committed drag.
                EndMidiDragGesture(d);
                if (hit.HasPointerCapture(evt.pointerId)) hit.ReleasePointer(evt.pointerId);
                if (d.moved) CommitMidiDragGesture(d);
                evt.StopPropagation();
            });
            hit.RegisterCallback<PointerCancelEvent>(evt =>
            {
                if (drag == null || drag.pointerId != evt.pointerId) return;
                RestoreMidiDragPreview(drag);
                EndMidiDragGesture(drag);
                if (hit.HasPointerCapture(evt.pointerId)) hit.ReleasePointer(evt.pointerId);
                evt.StopPropagation();
            });
            hit.RegisterCallback<PointerCaptureOutEvent>(evt =>
            {
                if (drag == null || drag.pointerId != evt.pointerId) return;
                RestoreMidiDragPreview(drag);
                EndMidiDragGesture(drag);
            });
        }

        // Move the invisible targets together with their notes when the user
        // pans the piano roll. Only the note rectangles, not the empty overlay,
        // intercept pointer events.
        private void SyncMidiNoteTouchTargets()
        {
            if (noteTouchLayer == null || scroller == null || !IsMidi || content?.notes == null) return;
            Vector2 offset = scroller.scrollOffset;
            foreach (var target in midiNoteTouchTargets)
            {
                if (target.noteIndex < 0 || target.noteIndex >= content.notes.Count) continue;
                var note = content.notes[target.noteIndex];
                if (note == null) continue;
                var active = drag != null && drag.index == target.noteIndex ? drag : null;
                long tick = active != null ? active.candidateTick : note.startTick;
                long duration = active != null ? active.candidateDuration : note.durationTicks;
                int pitch = active != null ? active.candidatePitch : note.pitch;
                target.body.style.left = (float)tick / quantum * StepW + 1f - offset.x;
                target.body.style.top = RowForPitch(pitch) * RowH + 4f - offset.y;
                target.body.style.width = Mathf.Max(13f, (float)duration / quantum * StepW - 2f);
                target.body.style.height = RowH - 7f;
            }
        }

        private void CommitMidiDragGesture(Drag d)
        {
            if (d == null) return;
            bool modified = d.candidateTick != d.startTick ||
                d.candidateDuration != d.duration || d.candidatePitch != d.pitch;
            if (!modified) { RestoreMidiDragPreview(d); return; }
            if (!Writable() || content?.notes == null || d.index < 0 || d.index >= content.notes.Count)
            {
                RestoreMidiDragPreview(d);
                return;
            }
            var note = content.notes[d.index];
            if (note == null) { RestoreMidiDragPreview(d); return; }
            note.startTick = d.candidateTick;
            note.durationTicks = d.candidateDuration;
            note.pitch = d.candidatePitch;
            content.lengthTicks = Math.Max(content.lengthTicks, note.startTick + note.durationTicks);
            selectedNote = d.index;
            // Selected note remains visible after a commit, including when the
            // viewport has followed the drag to a new pitch or beat.
            Changed();
            // Redraw restores the previous scroll offset on its next layout pass.
            // Focus the moved note AFTER that restoration so the viewport follows
            // its new location instead of jumping back to its original position.
            var focusedScroller = scroller;
            var focusedClipId = clipId;
            int focusedIndex = d.index;
            focusedScroller?.schedule.Execute(() =>
            {
                if (scroller == focusedScroller && clipId == focusedClipId &&
                    selectedNote == focusedIndex && IsMidi)
                    FocusSelectedMidiNote(focusedIndex);
            }).ExecuteLater(20);
        }

        private void EndMidiDragGesture(Drag d)
        {
            if (d == null) return;
            d.edgeScroll?.Pause();
            if (ReferenceEquals(drag, d)) drag = null;
        }

        private void Redraw(bool preserveViewport=true)
        {
            if (!ready || root == null || chips == null || gridScrollContainer == null) return;
            // A refresh detaches the old note plane. Cancel any hold that was
            // scheduled against it before rebuilding the cells.
            if (pendingTap != null)
            {
                pendingTap.hold?.Pause();
                pendingTap = null;
            }
            EndMidiDragGesture(drag);
            // Refreshing note rectangles must not throw the user back to MIDI 127.
            // OCT +/- and initial track selection can still reposition afterwards.
            Vector2 previousOffset = scroller != null ? scroller.scrollOffset : Vector2.zero;
            if (!preserveViewport) ++noteFocusRevision;
            float chipX = trackScroller != null ? trackScroller.scrollOffset.x : 0f;
            chips.Clear();
            if (song == null || song.tracks == null) return;
            for (int i=0;i<song.tracks.Count;i++)
            {
                int index=i;
                bool isGeneratable = song.tracks[i].kind != TrackKind.Chops;
                bool suppressNextClick = false;
                bool pressing = false;
                bool moved = false;
                Vector2 down = Vector2.zero;
                IVisualElementScheduledItem hold = null;
                var item=EqualButton(song.tracks[i].name.ToUpperInvariant(),()=> {
                    if (suppressNextClick) { suppressNextClick = false; return; }
                    SelectTrack(index);
                });
                if (isGeneratable)
                {
                    item.RegisterCallback<PointerDownEvent>(evt => {
                        if (evt.button != 0) return;
                        hold?.Pause();
                        pressing = true;
                        moved = false;
                        down = evt.position;
                        hold = item.schedule.Execute(() => {
                            if (!pressing || moved) return;
                            suppressNextClick = true;
                            pressing = false;
                            OpenGenerator(index);
                        });
                        hold.ExecuteLater(525);
                    }, TrickleDown.TrickleDown);
                    item.RegisterCallback<PointerMoveEvent>(evt => {
                        if (pressing && ((Vector2)evt.position - down).sqrMagnitude > 196f)
                        {
                            moved = true;
                            pressing = false;
                            hold?.Pause();
                        }
                    }, TrickleDown.TrickleDown);
                    item.RegisterCallback<PointerUpEvent>(evt => { pressing = false; hold?.Pause(); }, TrickleDown.TrickleDown);
                    item.RegisterCallback<PointerCancelEvent>(evt => { pressing = false; hold?.Pause(); });
                    item.RegisterCallback<PointerCaptureOutEvent>(evt => { pressing = false; hold?.Pause(); });
                    item.RegisterCallback<DetachFromPanelEvent>(evt => { pressing = false; hold?.Pause(); });
                }
                item.style.backgroundColor=i==trackIndex ? colors[Mathf.Min(i,4)] : Raised;
                item.style.color=i==trackIndex ? new Color(.03f,.07f,.10f) : Ink;
                chips.Add(item);
            }
            chips.Add(EqualButton("SAMPLE", OpenSampler));
            if (trackScroller != null)
                trackScroller.schedule.Execute(() => { if (trackScroller != null) trackScroller.scrollOffset = new Vector2(chipX, 0); }).ExecuteLater(0);
            RefreshKitSelector();
            RefreshClipSampleKeyReference();
            if (generatorQuickButton != null)
                generatorQuickButton.SetEnabled(song.tracks[trackIndex].kind != TrackKind.Chops);
            if (title != null) title.text = song.tracks[trackIndex].name.ToUpperInvariant()+"  /  " + (clip != null ? clip.name : "NO CLIP");
            if (info != null) info.text= clip == null ? "NO CLIP  /  HOLD TRACK TO GENERATE" :
                (!string.IsNullOrEmpty(content?.generatedTag) ? "GEN " + content.generatedTag :
                (IsMidi ? "PIANO ROLL" : content != null && content.kind==ClipKind.Drums ? "DRUM STEP EDITOR" : "SAMPLE STEP EDITOR") +
                "  /  Bar " + (clip.startTick / SongData.TicksPerBar4 + 1)) + "  /  HOLD TRACK TO GEN";
            gridScrollContainer.Clear();
            noteTouchLayer?.Clear();
            midiNoteTouchTargets.Clear();
            if (noteTouchLayer != null)
            {
                noteTouchLayer.style.display = IsMidi && clip != null && content != null ?
                    DisplayStyle.Flex : DisplayStyle.None;
                noteTouchLayer.style.left = IsMidi ? 57f : 83f;
            }
            rulerContent?.Clear();
            rulerPlayhead = null;
            pitchRowBackgrounds.Clear();
            pitchRowLabels.Clear();
            labelViewport?.Clear();
            labelColumn = null;
            if (labelViewport != null)
                labelViewport.style.display = clip != null && content != null ? DisplayStyle.Flex : DisplayStyle.None;
            velocityDisplays.Clear();
            RefreshVelocityRail();
            editorPlayhead = null;
            lastPlayheadX = -1f;
            if (clip==null || content==null) return;
            long editLength=Math.Max(clip.lengthTicks,content.lengthTicks);
            steps=Mathf.Clamp(Mathf.CeilToInt((float)editLength/quantum),16,4096);
            var names=Box(new Color(.095f,.13f,.18f));
            names.style.width=IsMidi ? 57f : 83f;
            names.style.position=Position.Absolute;
            names.style.left=0;
            names.style.top=0;
            names.style.flexShrink=0;
            names.style.flexGrow=0;
            if (labelViewport != null)
                labelViewport.style.width = IsMidi ? 57f : 83f;
            if (rulerGutter != null)
                rulerGutter.style.width = IsMidi ? 57f : 83f;
            names.style.borderTopLeftRadius=0;
            names.style.borderTopRightRadius=0;
            names.style.borderBottomLeftRadius=0;
            names.style.borderBottomRightRadius=0;
            var labelRulerSpacer = Txt("", 10);
            labelRulerSpacer.style.height = 25;
            labelRulerSpacer.style.minHeight = 25;
            labelRulerSpacer.style.maxHeight = 25;
            labelRulerSpacer.style.flexShrink = 0;
            names.Add(labelRulerSpacer);
            // Exact DrumKit.GetClipByDrumId() order: 1..8. No Tom/FX lanes.
            string[] drums={"KICK","SNARE","CLAP","CLOSED","OPEN","RIDE","RIM","CRASH"};
            for(int row=0;row<RowCount;row++)
            {
                string name=IsMidi? NoteName(PitchForRow(row)) : content.kind==ClipKind.Drums ? drums[row] : "S"+(row+1).ToString("00");
                var label=Txt(name,9,IsMidi && PitchForRow(row)%12==0?Accent:Muted);
                label.style.height=RowH;
                label.style.minHeight=RowH;
                label.style.maxHeight=RowH;
                label.style.flexShrink=0;
                label.style.flexGrow=0;
                label.style.paddingTop=0;
                label.style.paddingBottom=0;
                label.style.paddingLeft=5;
                label.style.marginTop=0;
                label.style.marginBottom=0;
                label.style.unityTextAlign=TextAnchor.MiddleLeft;
                names.Add(label);
                if (IsMidi) pitchRowLabels.Add(label);
            }
            labelColumn = names;
            labelViewport.Add(names);
            SyncPianoRollLabels();
            var grid=new VisualElement();
            canvas=grid;
            grid.style.position=Position.Relative;
            grid.style.flexShrink=0;
            grid.style.width=steps*StepW;
            grid.style.height=25+EditorHeight;
            grid.style.minHeight=25+EditorHeight;
            grid.style.backgroundColor=new Color(.055f,.075f,.112f);
            gridScrollContainer.Add(grid);
            // Every beat label lives in the pinned ruler, outside vertical scrolling.
            // The 1.1 / 1.2 / 1.3 / 1.4 notation is common to MIDI and step clips.
            if (rulerContent != null)
            {
                rulerContent.style.width = steps * StepW;
                for (int beat = 0; beat < steps; beat += 4)
                {
                    var num = Txt(((beat / 16) + 1) + "." + ((beat % 16) / 4 + 1), 11,
                        beat % 16 == 0 ? Ink : Muted);
                    num.style.position = Position.Absolute;
                    num.style.left = beat * StepW + 4;
                    num.style.top = 3;
                    num.style.height = 19;
                    num.pickingMode = PickingMode.Ignore;
                    rulerContent.Add(num);
                }
                SyncFixedRuler();
            }
            var plane=new VisualElement { name="ZPunkNotePlane" };
            plane.style.position=Position.Absolute;
            plane.style.left=0;
            plane.style.top=25;
            plane.style.width=steps*StepW;
            plane.style.height=EditorHeight;
            plane.style.minHeight=EditorHeight;
            grid.Add(plane);
            for(int row=0;row<RowCount;row++)
            {
                var background=new VisualElement();
                background.style.position=Position.Absolute;
                background.style.top=row*RowH;
                background.style.left=0;
                background.style.width=steps*StepW;
                background.style.height=RowH;
                background.style.backgroundColor=row%2==0?new Color(.11f,.145f,.19f):new Color(.09f,.12f,.16f);
                background.pickingMode=PickingMode.Ignore;
                plane.Add(background);
                if (IsMidi) pitchRowBackgrounds.Add(background);
            }
            ApplySampleKeyGuide();
            for(int step=0;step<=steps;step++)
            {
                var line=new VisualElement();
                line.style.position=Position.Absolute;
                line.style.left=step*StepW;
                line.style.top=0;
                line.style.width=step%16==0?2:1;
                line.style.height=EditorHeight;
                line.style.backgroundColor=step%16==0?new Color(.40f,.48f,.56f):
                    step%4==0?new Color(.24f,.31f,.40f):new Color(.16f,.21f,.29f);
                line.pickingMode=PickingMode.Ignore;
                plane.Add(line);
            }
            if (IsMidi && content.notes!=null)
            {
                for(int i=0;i<content.notes.Count;i++)
                {
                    var n=content.notes[i];
                    if(n==null) continue;
                    int row=RowForPitch(n.pitch);
                    if(row<0 || row>=RowCount) continue;
                    DrawEvent(plane,n.startTick,n.durationTicks,row,i,n.velocity);
                    AddMidiNoteTouchTarget(i);
                }
            }
            else if (content.kind == ClipKind.Drums && content.drums != null)
            {
                foreach (var hit in content.drums)
                {
                    if (hit != null && hit.lane >= 0 && hit.lane < RowCount)
                        DrawEvent(plane, hit.startTick, quantum, hit.lane, -1, hit.velocity);
                }
            }
            else if (content.kind == ClipKind.Chops && content.chops != null)
            {
                foreach (var chop in content.chops)
                {
                    if (chop != null && chop.sliceId >= 1 && chop.sliceId <= RowCount)
                        DrawEvent(plane, chop.startTick, quantum, chop.sliceId - 1, -1, chop.velocity);
                }
            }
            RefreshVelocityDisplays();
            SyncMidiNoteTouchTargets();
            // Shared transport playhead for all clip editors.
            // Covers both the timeline ruler and the note grid.
            editorPlayhead = new VisualElement
            {
                name = "ZPunkEditorPlayhead"
            };

            editorPlayhead.style.position = Position.Absolute;
            editorPlayhead.style.top = 0;
            editorPlayhead.style.left = 0;
            editorPlayhead.style.width = 3;
            editorPlayhead.style.height = 25 + EditorHeight;
            editorPlayhead.style.backgroundColor = Accent;
            editorPlayhead.style.opacity = 0.95f;

            editorPlayhead.pickingMode = PickingMode.Ignore;
            editorPlayhead.style.display = DisplayStyle.None;

            // Add to the outer grid so it spans the ruler and notes.
            // Adding it last also keeps it visible above the notes.
            grid.Add(editorPlayhead);
            if (rulerContent != null)
            {
                rulerPlayhead = new VisualElement { name = "ZPunkRulerPlayhead" };
                rulerPlayhead.style.position = Position.Absolute;
                rulerPlayhead.style.top = 0;
                rulerPlayhead.style.left = 0;
                rulerPlayhead.style.width = 2;
                rulerPlayhead.style.height = 25;
                rulerPlayhead.style.backgroundColor = Accent;
                rulerPlayhead.pickingMode = PickingMode.Ignore;
                rulerPlayhead.style.display = DisplayStyle.None;
                rulerContent.Add(rulerPlayhead);
            }
            plane.RegisterCallback<PointerDownEvent>(evt=>
            {
                if (evt.button != 0) return;
                Vector2 p = plane.WorldToLocal(evt.position);
                int row = RowAt(p.y), step = QuantizedStep(p.x);
                step = HitStepAtVisualPosition(row, step, p.x);
                // Existing MIDI notes are picked by the overlay, not this
                // ScrollView child. Empty cells remain regular grid touches.
                // A finger may be scrolling the piano roll. Defer creating a new
                // note/step until pointer up, and never create one during a swipe.
                var tap = new Tap { pointerId = evt.pointerId, start = evt.position, row = row, step = step };
                pendingTap = tap;
                if (!IsMidi && HasStepEvent(row, step * quantum))
                {
                    tap.hold = plane.schedule.Execute(() =>
                    {
                        if (!ReferenceEquals(pendingTap, tap)) return;
                        tap.handled = true;
                        SelectStepVelocity(tap.row, tap.step * quantum);
                        plane.CapturePointer(tap.pointerId);
                    });

                    tap.hold.ExecuteLater(440);
                }
            });
            plane.RegisterCallback<PointerMoveEvent>(evt=>
            {
                if (pendingTap != null && pendingTap.pointerId == evt.pointerId && pendingTap.handled)
                {
                    evt.StopPropagation();
                    return;
                }
                if (pendingTap != null && pendingTap.pointerId == evt.pointerId && !pendingTap.handled &&
                    ((Vector2)evt.position - pendingTap.start).sqrMagnitude > 49f)
                {
                    pendingTap.hold?.Pause();
                    pendingTap = null;
                }
                if (drag == null || drag.pointerId != evt.pointerId) return;
                drag.latestWorldPosition = evt.position;
                KeepMidiDragViewportLocked();
                Vector2 p = plane.WorldToLocal(evt.position);
                if ((p-drag.start).sqrMagnitude > MidiGestureThreshold * MidiGestureThreshold)
                    drag.moved = true;
                if (drag.moved) UpdateMidiDragPreview(drag, p);
                evt.StopPropagation();
            });
            plane.RegisterCallback<PointerUpEvent>(evt=>
            {
                if (pendingTap != null && pendingTap.pointerId == evt.pointerId)
                {
                    var tap = pendingTap;
                    pendingTap = null;
                    tap.hold?.Pause();
                    if (plane.HasPointerCapture(evt.pointerId)) plane.ReleasePointer(evt.pointerId);
                    if (!tap.handled && ((Vector2)evt.position - tap.start).sqrMagnitude <= 49f)
                        ToggleGrid(tap.row, tap.step);
                    evt.StopPropagation();
                    return;
                }
                if (drag == null || drag.pointerId != evt.pointerId) return;
                var d = drag;
                KeepMidiDragViewportLocked();
                // Finish the preview before releasing capture; otherwise the
                // scroll view may interpret the release as a pan or fling.
                if (d.moved)
                {
                    UpdateMidiDragPreview(d, MidiWorldToPlane(evt.position));
                    CommitMidiDragGesture(d);
                }
                EndMidiDragGesture(d);
                if (plane.HasPointerCapture(evt.pointerId)) plane.ReleasePointer(evt.pointerId);
                evt.StopPropagation();
            });
            plane.RegisterCallback<PointerCancelEvent>(evt =>
            {
                if (pendingTap != null && pendingTap.pointerId == evt.pointerId)
                {
                    pendingTap.hold?.Pause();
                    pendingTap = null;
                }
                if (drag != null && drag.pointerId == evt.pointerId)
                {
                    RestoreMidiDragPreview(drag);
                    EndMidiDragGesture(drag);
                }
                if (plane.HasPointerCapture(evt.pointerId)) plane.ReleasePointer(evt.pointerId);
            });
            plane.RegisterCallback<PointerCaptureOutEvent>(evt =>
            {
                if (pendingTap != null && pendingTap.pointerId == evt.pointerId)
                {
                    pendingTap.hold?.Pause();
                    pendingTap = null;
                }
                if (drag != null && drag.pointerId == evt.pointerId)
                {
                    RestoreMidiDragPreview(drag);
                    EndMidiDragGesture(drag);
                }
            });
            if (scroller != null)
                scroller.schedule.Execute(() =>
                {
                    if (scroller != null)
                    {
                        // New drum/chop clips start at the first lane; MIDI clips
                        // are positioned by FocusPatternNotes / ScrollToOctave.
                        if (preserveViewport) scroller.scrollOffset = previousOffset;
                        else if (!IsMidi) scroller.scrollOffset = Vector2.zero;
                        SyncPianoRollLabels();
                        SyncFixedRuler();
                        SyncMidiNoteTouchTargets();
                    }
                }).ExecuteLater(0);
        }
        private void SetEditorPlayheadVisible(bool visible)
        {
            var display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (editorPlayhead != null) editorPlayhead.style.display = display;
            if (rulerPlayhead != null) rulerPlayhead.style.display = display;
            if (!visible) lastPlayheadX = -1f;
        }

        private void Update()
        {
            KeepMidiDragViewportLocked();
            // Sample-key guidance must continue updating when the transport is stopped.
            FollowPitchedSampleKey();
            if (!ready || editorPlayhead == null) return;

            if (songTransport == null)
                songTransport = FindObjectOfType<ZPunkSongTransport>();

            if (root == null || root.resolvedStyle.display == DisplayStyle.None ||
                showingSampler || songTransport == null || clip == null || content == null ||
                song == null || song.tracks == null || trackIndex < 0 ||
                trackIndex >= song.tracks.Count ||
                !songTransport.SongMode || !songTransport.IsPlaying)
            {
                SetEditorPlayheadVisible(false);
                return;
            }

            // Only display the neon cursor while this particular clip is being
            // played by the arrangement. Never wrap an inactive clip's timeline.
            TrackData track = song.tracks[trackIndex];
            if (track == null || track.mute || track.volume <= 0f)
            {
                SetEditorPlayheadVisible(false);
                return;
            }
            bool anotherTrackSoloed = false;
            bool thisTrackSoloed = track.solo;
            foreach (TrackData other in song.tracks)
                if (other != null && other.solo) { anotherTrackSoloed = true; break; }
            if (anotherTrackSoloed && !thisTrackSoloed)
            {
                SetEditorPlayheadVisible(false);
                return;
            }

            long globalTick = songTransport.CurrentTick;
            long clipRelativeTick = globalTick - clip.startTick;
            if (clipRelativeTick < 0 || clip.lengthTicks <= 0 ||
                clipRelativeTick >= clip.lengthTicks)
            {
                SetEditorPlayheadVisible(false);
                return;
            }

            // Match SongEventPlanner: non-loop clips end after their source
            // content has played; looping clips wrap at source-content length.
            long contentTicks = Math.Max(1L, content.lengthTicks);
            long sourceOffset = ((clip.sourceOffsetTick % contentTicks) + contentTicks) % contentTicks;
            long sourceTick = clipRelativeTick + sourceOffset;
            if (!clip.loopEnabled && sourceTick >= contentTicks)
            {
                SetEditorPlayheadVisible(false);
                return;
            }
            if (clip.loopEnabled) sourceTick %= contentTicks;

            // Throttle *position updates*, not the active/inactive visibility check.
            SetEditorPlayheadVisible(true);
            if (Time.unscaledTime < nextPlayheadPaint) return;
            nextPlayheadPaint = Time.unscaledTime + 1f / 30f;

            long visibleTicks = Math.Max(quantum, (long)steps * quantum);
            if (sourceTick >= visibleTicks)
            {
                SetEditorPlayheadVisible(false);
                return;
            }
            float x = Mathf.Max(1f, (float)sourceTick / quantum * StepW);
            if (Mathf.Abs(x - lastPlayheadX) >= 0.4f)
            {
                lastPlayheadX = x;
                editorPlayhead.style.left = x;
                if (rulerPlayhead != null) rulerPlayhead.style.left = x;
            }
        }
        private void DrawEvent(VisualElement plane, long tick, long duration, int row, int index, int eventVelocity)
        {
            float x = (float)tick / quantum * StepW;
            if (x >= steps * StepW) return;
            float w = Mathf.Max(13f, (float)duration / quantum * StepW - 2f);
            var e = Box(colors[Mathf.Min(trackIndex, 4)], 3);
            e.style.position = Position.Absolute;
            e.style.left = x + 1;
            e.style.top = row * RowH + 4;
            e.style.width = w;
            e.style.height = RowH - 7;
            e.pickingMode = PickingMode.Ignore; // Single plane handles all touch gestures.

            // Every occupied cell carries its own numerical velocity and a
            // proportional mini-meter. Neither adds a touch target to the grid.
            Label amount = null;
            if (w >= 28f)
            {
                amount = new Label(Mathf.Clamp(eventVelocity, 1, 127).ToString());
                amount.style.position = Position.Absolute;
                amount.style.left = 2;
                amount.style.top = 3;
                amount.style.width = Mathf.Min(31f, w - 5f);
                amount.style.height = 17;
                amount.style.fontSize = 10;
                amount.style.color = new Color(.045f, .07f, .10f);
                amount.style.unityTextAlign = TextAnchor.MiddleCenter;
                amount.pickingMode = PickingMode.Ignore;
                e.Add(amount);
            }
            var meter = Box(new Color(.045f, .075f, .10f, .82f), 1);
            meter.style.position = Position.Absolute;
            meter.style.left = 3;
            meter.style.bottom = 3;
            meter.style.height = 3;
            meter.style.width = 1;
            meter.pickingMode = PickingMode.Ignore;
            e.Add(meter);
            if (IsMidi && w > 20)
            {
                var grip = Box(new Color(.08f, .18f, .21f, .55f), 1);
                grip.style.position = Position.Absolute;
                grip.style.right = 2;
                grip.style.top = 2;
                grip.style.width = 3;
                grip.style.height = RowH - 11;
                grip.pickingMode = PickingMode.Ignore;
                e.Add(grip);
            }
            velocityDisplays.Add(new VelocityDisplay
            {
                cell = e, meter = meter, number = amount, row = row, noteIndex = index,
                tick = tick, width = w, velocity = eventVelocity
            });
            plane.Add(e);
        }
        private static string NoteName(int pitch)
        {
            string[] names={"C","C#","D","D#","E","F","F#","G","G#","A","A#","B"};
            return names[pitch%12]+(pitch/12-1);
        }
    }
}
