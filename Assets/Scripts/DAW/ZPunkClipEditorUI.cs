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
        private int generatorGenre = 1, generatorStyle, generatorBars = 2, generatorKey = 9, generatorVariation;
        private bool generatorMinor = true, generatorSynthChords;
        private int generatorTargetTrack;
        private VisualElement editorPlayhead;
        private VisualElement samplerPage, samplerWave;
        private ScrollView samplerScroll;
        private Label samplerSelectedMarker, samplerHitSliceStatus;
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

        private sealed class Drag
        {
            public int index;
            public Vector2 start;
            public long startTick;
            public long duration;
            public int pitch;
            public bool resizing;
            public bool moved;
            public int pointerId;
        }
        private Drag drag;
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

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginTop = 2;
            row.style.flexShrink = 0;
            row.style.flexWrap = Wrap.NoWrap;
            row.Add(Btn("OCT -", () => { octave = Mathf.Max(-1,octave-1); ScrollToOctave(); }, 56));
            row.Add(Btn("OCT +", () => { octave = Mathf.Min(9,octave+1); ScrollToOctave(); }, 56));
            row.Add(Btn("LEN -", () => ChangeLength(-1), 56));
            row.Add(Btn("LEN +", () => ChangeLength(1), 56));
            row.Add(Btn("NOTE DEL", DeleteSelectedNote, 77));
            generatorQuickButton = Btn("GEN", () => OpenGenerator(trackIndex), 58);
            row.Add(generatorQuickButton);
            var controls = new ScrollView(ScrollViewMode.Horizontal);
            controls.style.height = 54;
            controls.style.flexShrink = 0;
            controls.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            controls.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            controls.Add(row);
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
            scroller.verticalScroller.valueChanged += _ => SyncPianoRollLabels();
            scroller.horizontalScroller.valueChanged += _ => SyncFixedRuler();
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
            var footer = Txt("TAP: add/remove step  |  HOLD active step: edit velocity  |  DRAG notes: move/resize",10,Muted);
            footer.style.whiteSpace = WhiteSpace.Normal;
            footer.style.marginTop = 5;
            footer.style.marginBottom = 8;
            root.Add(footer);
            BuildSamplerPage();
            BuildGeneratorPanel();
        }

        private void BuildSamplerPage()
        {
            samplerScroll = new ScrollView(ScrollViewMode.Vertical) { name = "ZPunkSamplerPage" };
            samplerScroll.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            samplerScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            samplerScroll.contentContainer.style.flexDirection = FlexDirection.Column;
            samplerScroll.contentContainer.style.paddingBottom = 16;
            samplerPage = samplerScroll;
            samplerPage.style.display = DisplayStyle.None;
            samplerPage.style.flexDirection = FlexDirection.Column;
            samplerPage.style.flexGrow = 1;
            samplerPage.style.flexShrink = 1;
            samplerPage.style.minHeight = 0;
            samplerPage.style.paddingTop = 9;
            var heading = Txt("SAMPLER  /  SLICE LAB",16,Ink);
            heading.style.unityFontStyleAndWeight = FontStyle.Bold;
            samplerPage.Add(heading);
            samplerTitle = Txt("NO SAMPLE",12,Accent);
            samplerTitle.style.height = 39;
            samplerTitle.style.whiteSpace = WhiteSpace.Normal;
            samplerPage.Add(samplerTitle);
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexWrap = Wrap.NoWrap;
            row.style.height = 43;
            row.Add(Btn("BACK",()=>ShowSampler(false),65));
            row.Add(Btn("BROWSE",()=>{FindSampler();samplePlayer?.DawOpenBrowser();},80));
            row.Add(Btn("<",()=>{FindSampler();samplePlayer?.DawPreviousSample();RefreshSampler();},43));
            row.Add(Btn(">",()=>{FindSampler();samplePlayer?.DawNextSample();RefreshSampler();},43));
            row.Add(Btn("PLAY",()=>{FindSampler();samplePlayer?.DawPlaySample();},60));
            row.Add(Btn("STOP",()=>{FindSampler();samplePlayer?.DawStopSample();},60));
            var actions = new ScrollView(ScrollViewMode.Horizontal);
            actions.style.height = 46;
            actions.style.flexShrink = 0;
            actions.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            actions.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            actions.Add(row);
            samplerPage.Add(actions);
            var warpCard = Box(new Color(.108f,.147f,.204f),5);
            warpCard.style.paddingLeft = 8;
            warpCard.style.paddingRight = 8;
            warpCard.style.paddingTop = 7;
            warpCard.style.paddingBottom = 7;
            warpCard.style.marginTop = 7;
            warpCard.style.flexShrink = 0;
            var warpRow = new VisualElement();
            warpRow.style.flexDirection = FlexDirection.Row;
            warpRow.style.alignItems = Align.Center;
            warpRow.style.height = 42;
            warpRow.style.flexShrink = 0;
            samplerWarpButton = Btn("WARP ON", () => { FindSampler(); samplePlayer?.DawToggleAutoWarp(); RefreshSampler(); }, 98);
            warpRow.Add(samplerWarpButton);
            warpRow.Add(Btn("-10", () => { FindSampler(); samplePlayer?.DawAdjustSourceBpm(-10); RefreshSampler(); }, 48));
            warpRow.Add(Btn("-1", () => { FindSampler(); samplePlayer?.DawAdjustSourceBpm(-1); RefreshSampler(); }, 45));
            warpRow.Add(Btn("+1", () => { FindSampler(); samplePlayer?.DawAdjustSourceBpm(1); RefreshSampler(); }, 45));
            warpRow.Add(Btn("+10", () => { FindSampler(); samplePlayer?.DawAdjustSourceBpm(10); RefreshSampler(); }, 48));
            warpCard.Add(warpRow);
            samplerWarpLabel = Txt("SOURCE BPM / WAITING",11,Accent);
            samplerWarpLabel.style.whiteSpace = WhiteSpace.Normal;
            warpCard.Add(samplerWarpLabel);

            // Local PCM warp progress: the track fills only while analysis or
            // transient-guided stretch is running. Its width follows real work.
            samplerWarpProgressLabel = Txt("ANALYZING / 0%", 10, Muted);
            samplerWarpProgressLabel.style.marginTop = 7;
            samplerWarpProgressLabel.style.marginBottom = 3;
            samplerWarpProgressLabel.style.display = DisplayStyle.None;
            warpCard.Add(samplerWarpProgressLabel);
            samplerWarpProgressTrack = new VisualElement();
            samplerWarpProgressTrack.style.height = 5;
            samplerWarpProgressTrack.style.flexShrink = 0;
            samplerWarpProgressTrack.style.backgroundColor = Surface;
            samplerWarpProgressTrack.style.borderTopLeftRadius = 3;
            samplerWarpProgressTrack.style.borderTopRightRadius = 3;
            samplerWarpProgressTrack.style.borderBottomLeftRadius = 3;
            samplerWarpProgressTrack.style.borderBottomRightRadius = 3;
            samplerWarpProgressTrack.style.overflow = Overflow.Hidden;
            samplerWarpProgressTrack.style.display = DisplayStyle.None;
            samplerWarpProgressFill = new VisualElement();
            samplerWarpProgressFill.style.height = Length.Percent(100);
            samplerWarpProgressFill.style.width = Length.Percent(0);
            samplerWarpProgressFill.style.backgroundColor = Accent;
            samplerWarpProgressTrack.Add(samplerWarpProgressFill);
            warpCard.Add(samplerWarpProgressTrack);

            // Integer-only pitch transport. Step buttons make accidental cents / 
            // in-between pitches impossible, including on touchscreens.
            var pitchCaption = Txt("PITCH  /  SEMITONES (-12 TO +12)",10,Muted);
            pitchCaption.style.marginTop = 8;
            pitchCaption.style.marginBottom = 3;
            warpCard.Add(pitchCaption);
            var pitchRow = new VisualElement();
            pitchRow.style.flexDirection = FlexDirection.Row;
            pitchRow.style.alignItems = Align.Center;
            pitchRow.style.height = 44;
            pitchRow.style.flexShrink = 0;
            pitchRow.Add(Btn("-1", () => { FindSampler(); samplePlayer?.DawAdjustPitchSemitones(-1); RefreshSampler(); }, 52));
            samplerPitchLabel = Txt("0 ST",13,Accent);
            samplerPitchLabel.style.minWidth = 68;
            samplerPitchLabel.style.flexGrow = 1;
            samplerPitchLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            samplerPitchLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            pitchRow.Add(samplerPitchLabel);
            pitchRow.Add(Btn("+1", () => { FindSampler(); samplePlayer?.DawAdjustPitchSemitones(1); RefreshSampler(); }, 52));
            pitchRow.Add(Btn("RESET", () => { FindSampler(); samplePlayer?.DawSetPitchSemitones(0); RefreshSampler(); }, 68));
            warpCard.Add(pitchRow);

            var keyArea = new VisualElement();
            keyArea.style.flexDirection = FlexDirection.Column;
            keyArea.style.marginTop = 6;
            keyArea.style.paddingTop = 8;
            keyArea.style.paddingBottom = 6;
            keyArea.style.paddingLeft = 7;
            keyArea.style.paddingRight = 7;
            keyArea.style.backgroundColor = Surface;
            keyArea.style.borderTopLeftRadius = 5;
            keyArea.style.borderTopRightRadius = 5;
            keyArea.style.borderBottomLeftRadius = 5;
            keyArea.style.borderBottomRightRadius = 5;
            samplerSourceKeyLabel = Txt("SOURCE KEY  /  WAITING", 12, Ink);
            samplerSourceKeyLabel.style.whiteSpace = WhiteSpace.Normal;
            samplerSourceKeyLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            keyArea.Add(samplerSourceKeyLabel);
            samplerPitchedKeyLabel = Txt("PITCHED KEY  /  UNKNOWN", 12, Accent);
            samplerPitchedKeyLabel.style.whiteSpace = WhiteSpace.Normal;
            samplerPitchedKeyLabel.style.marginTop = 5;
            keyArea.Add(samplerPitchedKeyLabel);
            warpCard.Add(keyArea);

            samplerLibraryLabel = Txt("SAMPLE LIBRARY",10,Muted);
            samplerLibraryLabel.style.whiteSpace = WhiteSpace.Normal;
            samplerLibraryLabel.style.marginTop = 4;
            warpCard.Add(samplerLibraryLabel);
            samplerPage.Add(warpCard);
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
            samplerWave = Box(new Color(.049f,.073f,.105f),4);
            samplerWave.style.height = 145;
            samplerWave.style.flexShrink = 0;
            samplerWave.style.marginTop = 10;
            samplerPage.Add(samplerWave);
            var zoomActions = new VisualElement();
            zoomActions.style.flexDirection = FlexDirection.Row;
            zoomActions.style.alignItems = Align.Center;
            zoomActions.style.height = 43;
            zoomActions.style.flexShrink = 0;
            zoomActions.style.marginTop = 4;
            zoomActions.Add(Btn("- ZOOM", () => { FindSampler(); samplePlayer?.DawZoomSample(false); RefreshSampler(); }, 80));
            zoomActions.Add(Btn("+ ZOOM", () => { FindSampler(); samplePlayer?.DawZoomSample(true); RefreshSampler(); }, 80));
            zoomActions.Add(Btn("FULL", () => { FindSampler(); samplePlayer?.DawShowFullSample(); RefreshSampler(); }, 66));
            samplerSnapButton = Btn("HIT SNAP", () => { FindSampler(); samplePlayer?.DawToggleTransientSnap(); RefreshSampler(); }, 94);
            zoomActions.Add(samplerSnapButton);
            // Keep all four controls reachable even on narrow portrait phones.
            var zoomScroll = new ScrollView(ScrollViewMode.Horizontal);
            zoomScroll.style.height = 45;
            zoomScroll.style.flexShrink = 0;
            zoomScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            zoomScroll.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            zoomScroll.Add(zoomActions);
            samplerPage.Add(zoomScroll);
            var help = Txt("DRAG HEAD TO A GHOST HIT, THEN SLICE HITS TO CHOP AT THE FOLLOWING ATTACKS. PLAY STARTS AT HEAD. HIT SNAP LOCKS TO GHOSTS.",10,Muted);
            help.style.whiteSpace = WhiteSpace.Normal;
            help.style.flexShrink = 0;
            help.style.marginTop = 7;
            help.style.marginBottom = 8;
            samplerPage.Add(help);
            var sliceHits = Btn("SLICE HITS", () =>
            {
                FindSampler();
                samplePlayer?.DawSliceHits();
                RefreshSampler();
            }, 160);
            sliceHits.style.height = 42;
            sliceHits.style.flexShrink = 0;
            sliceHits.style.marginTop = 2;
            sliceHits.style.backgroundColor = new Color(.14f, .34f, .32f);
            sliceHits.style.color = Ink;
            samplerPage.Add(sliceHits);
            samplerHitSliceStatus = Txt("PLACE HEAD ON FIRST HIT, THEN SLICE HITS", 10, Muted);
            samplerHitSliceStatus.style.whiteSpace = WhiteSpace.Normal;
            samplerHitSliceStatus.style.marginTop = 4;
            samplerHitSliceStatus.style.marginBottom = 7;
            samplerHitSliceStatus.style.flexShrink = 0;
            samplerPage.Add(samplerHitSliceStatus);
            var slices = new VisualElement();
            slices.style.flexDirection = FlexDirection.Row;
            slices.style.height = 46;
            slices.Add(Btn("ADD MARK",()=>{FindSampler();samplePlayer?.DawAddMarker();RefreshSampler();},86));
            slices.Add(Btn("DEL SELECTED",()=>{FindSampler();samplePlayer?.DawDeleteMarker();RefreshSampler();},111));
            slices.Add(Btn("APPLY", ApplySamplerChops, 75));
            var sliceActions = new ScrollView(ScrollViewMode.Horizontal);
            sliceActions.style.height = 49;
            sliceActions.style.flexShrink = 0;
            sliceActions.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            sliceActions.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            sliceActions.Add(slices);
            samplerPage.Add(sliceActions);
            // One continuous vertical scroller owns the entire sampler page;
            // nested scroll views previously trapped touch drags on the chop pads.
            samplerSelectedMarker = Txt("SELECT A MARKER TO MOVE OR DELETE",11,Accent);
            samplerSelectedMarker.style.whiteSpace = WhiteSpace.Normal;
            samplerSelectedMarker.style.marginTop = 5;
            samplerSelectedMarker.style.marginBottom = 5;
            samplerPage.Add(samplerSelectedMarker);
            var markerTools = new ScrollView(ScrollViewMode.Horizontal);
            markerTools.style.height = 48;
            markerTools.style.flexShrink = 0;
            markerTools.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            markerTools.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            var markerRow = new VisualElement();
            markerRow.style.flexDirection = FlexDirection.Row;
            markerRow.style.flexWrap = Wrap.NoWrap;
            markerRow.Add(Btn("< MARK", () => { FindSampler(); samplePlayer?.DawSelectMarker(-1); RefreshSampler(); }, 76));
            markerRow.Add(Btn("MARK >", () => { FindSampler(); samplePlayer?.DawSelectMarker(1); RefreshSampler(); }, 76));
            markerRow.Add(Btn("- 10 MS", () => { FindSampler(); samplePlayer?.DawNudgeSelectedMarker(-1); RefreshSampler(); }, 78));
            markerRow.Add(Btn("+ 10 MS", () => { FindSampler(); samplePlayer?.DawNudgeSelectedMarker(1); RefreshSampler(); }, 78));
            markerTools.Add(markerRow);
            samplerPage.Add(markerTools);
            var padScroll = new VisualElement();
            padScroll.style.flexDirection = FlexDirection.Column;
            padScroll.style.flexShrink = 0;
            for (int group = 0; group < 4; group++)
            {
                var padRow = new VisualElement();
                padRow.style.flexDirection = FlexDirection.Row;
                padRow.style.height = 69;
                padRow.style.flexShrink = 0;
                for (int j = 0; j < 4; j++)
                {
                    int slice = group * 4 + j + 1;
                    var button = Btn("S"+slice.ToString("00"),()=>{
                        FindSampler();
                        samplePlayer?.DawSelectChopPad(slice);
                        RefreshSampler();
                    },50);
                    button.style.flexGrow = 1;
                    button.style.flexShrink = 0;
                    button.style.minWidth = 56;
                    button.style.marginRight = 5;
                    button.style.height = 62;
                    button.style.fontSize = 11;
                    button.style.whiteSpace = WhiteSpace.Normal;
                    button.style.backgroundColor = Raised;
                    button.style.position = Position.Relative;
                    // A tiny bottom mini-timeline indicates which part of the
                    // whole sample the pad plays, including its end boundary.
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
            keyRow.Add(Btn("<",() => { generatorKey = (generatorKey + 11)%12; RefreshGeneratorPanel(); },43));
            generatorKeyLabel = Txt("A MIN",13,Accent);
            generatorKeyLabel.style.flexGrow = 1;
            generatorKeyLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            keyRow.Add(generatorKeyLabel);
            keyRow.Add(Btn(">",() => { generatorKey = (generatorKey + 1)%12; RefreshGeneratorPanel(); },43));
            keyRow.Add(Btn("MIN/MAJ",() => { generatorMinor = !generatorMinor; RefreshGeneratorPanel(); },85));
            body.Add(keyRow);

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

        private void OpenGenerator(int requestedTrack)
        {
            if (!ready || showingSampler || generatorPanel == null || song == null || song.tracks == null ||
                requestedTrack < 0 || requestedTrack >= song.tracks.Count ||
                song.tracks[requestedTrack].kind == TrackKind.Chops) return;
            if (requestedTrack != trackIndex) SelectTrack(requestedTrack);
            generatorTargetTrack = trackIndex;
            if (song.tracks[trackIndex].kind == TrackKind.Synth)
                generatorSynthChords = content != null &&
                    !string.IsNullOrEmpty(content.generatedTag) &&
                    content.generatedTag.Contains(" / CHORDS / ");
            generatorBars = clip == null ? 2 :
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
            for (int st = 0; st < ZPunkGenreGenerator.Styles[generatorGenre].Length; st++)
            {
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
            generatorSummary.text = (chords ?
                ZPunkGenreGenerator.ChordsDescription(generatorGenre, generatorStyle) :
                generatorKind == TrackKind.Bass ? ZPunkGenreGenerator.BassDescription(generatorGenre, generatorStyle) :
                ZPunkGenreGenerator.Description(generatorGenre, generatorStyle)) + "\n" +
                "READY: " + ZPunkGenreGenerator.Label(generatorGenre, generatorStyle, generatorKey, generatorMinor, chords) +
                (isSynth && !chords ? " / MELODY" : "");
            generatorKeyLabel.text = ZPunkGenreGenerator.Keys[generatorKey] + (generatorMinor ? " MIN" : " MAJ");
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
                string styleName = ZPunkGenreGenerator.Styles[generatorGenre][Mathf.Clamp(entry.style, 0, 2)];
                var details = Txt(styleName + "  /  " + ZPunkGenreGenerator.Keys[Mathf.Clamp(entry.key, 0, 11)] +
                    (entry.minor ? "m" : "") + "  /  " + entry.bars + " BAR" +
                    (entry.bars == 1 ? "" : "S") + "  /  V" + entry.variation, 12, Ink);
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
        private void ApplyGenerator()
        {
            if (!PrepareGeneratorContent(generatorBars)) return;
            TrackKind kind = song.tracks[trackIndex].kind;
            bool chords = kind == TrackKind.Synth && generatorSynthChords;
            ZPunkGenreGenerator.Fill(content, kind, generatorGenre, generatorStyle,
                generatorBars, generatorKey, generatorMinor, generatorVariation, chords);
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
            if (samplerWarpButton != null)
                samplerWarpButton.text = samplePlayer != null && samplePlayer.DawAutoWarp ? "WARP ON" : "WARP OFF";
            if (samplerWarpLabel != null)
                samplerWarpLabel.text = samplePlayer != null ?
                    "SOURCE BPM (LENGTH EST.) " + samplePlayer.DawSourceBpm.ToString("0.#") + "  /  " + samplePlayer.DawWarpStatus :
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
                    samplePlayer.DawHitSliceStatus + "  /  UP TO 16 PADS  /  FROM HEAD" : "NO SAMPLE";
            if (samplerSnapButton != null)
                samplerSnapButton.text = samplePlayer != null && samplePlayer.DawTransientSnapEnabled
                    ? "HIT SNAP" : "GRID SNAP";
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
            samplePlayer?.DawEnableManualMarkers();
            if (samplerWave != null && samplerWave.childCount == 0)
            {
                var doc = FindObjectOfType<UIDocument>();
                var oldHost = doc != null ? doc.rootVisualElement.Q<VisualElement>("Waveform") : null;
                if (oldHost != null)
                {
                    oldHost.RemoveFromHierarchy();
                    oldHost.style.height = 140;
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
            if(content==null || content.kind!=ClipKind.Midi || selectedNote<0 ||
                content.notes==null || selectedNote>=content.notes.Count || !Writable())return;
            content.notes.RemoveAt(selectedNote);
            selectedNote=-1;
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
            if (content.kind == ClipKind.Drums)
                return content.drums != null && content.drums.Exists(d => d != null && d.lane == row && d.startTick == tick);
            if (content.kind == ClipKind.Chops)
                return content.chops != null && content.chops.Exists(d => d != null && d.sliceId == row + 1 && d.startTick == tick);
            return false;
        }
        private void SelectStepVelocity(int row, long tick)
        {
            if (!HasStepEvent(row, tick)) return;
            selectedNote = -1;
            selectedEventRow = row;
            selectedEventTick = tick;
            if (content.kind == ClipKind.Drums)
            {
                var hit = content.drums.Find(d => d != null && d.lane == row && d.startTick == tick);
                velocity = Mathf.Clamp(hit.velocity, 1, 127);
            }
            else
            {
                var hit = content.chops.Find(d => d != null && d.sliceId == row + 1 && d.startTick == tick);
                velocity = Mathf.Clamp(hit.velocity, 1, 127);
            }
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
        private int FindNote(int pitch,long tick)
        {
            if (content == null || content.notes == null) return -1;
            for (int i=content.notes.Count-1;i>=0;i--)
            {
                var n=content.notes[i];
                if(n != null && n.pitch == pitch && tick >= n.startTick && tick < n.startTick+n.durationTicks)
                    return i;
            }
            return -1;
        }
        private void ToggleGrid(int row,int step)
        {
            if (content == null || !Writable()) return;
            long tick = step * quantum;
            if (content.kind == ClipKind.Drums)
            {
                if (content.drums == null) content.drums = new List<DrumEvent>();
                int i = content.drums.FindIndex(d => d != null && d.lane == row && d.startTick == tick);
                if (i >= 0)
                {
                    content.drums.RemoveAt(i);
                    if (selectedEventRow == row && selectedEventTick == tick) selectedEventRow = -1;
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
                int i = content.chops.FindIndex(d => d != null && d.sliceId == row + 1 && d.startTick == tick);
                if (i >= 0)
                {
                    content.chops.RemoveAt(i);
                    if (selectedEventRow == row && selectedEventTick == tick) selectedEventRow = -1;
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
                if (existing >= 0) { content.notes.RemoveAt(existing); selectedNote = -1; }
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
                var item=Btn(song.tracks[i].name.ToUpperInvariant(),()=> {
                    if (suppressNextClick) { suppressNextClick = false; return; }
                    SelectTrack(index);
                },83);
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
            chips.Add(Btn("SAMPLER", OpenSampler, 91));
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
            steps=Mathf.Clamp(Mathf.CeilToInt((float)editLength/quantum),16,128);
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
            editorPlayhead.style.display = DisplayStyle.Flex;

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
                rulerContent.Add(rulerPlayhead);
            }
            plane.RegisterCallback<PointerDownEvent>(evt=>
            {
                if (evt.button != 0) return;
                Vector2 p = plane.WorldToLocal(evt.position);
                int row = RowAt(p.y), step = QuantizedStep(p.x);
                if (IsMidi)
                {
                    int index = FindNote(PitchForRow(row), step * quantum);
                    if (index >= 0)
                    {
                        SelectNoteVelocity(index);
                        var n = content.notes[index];
                        float right = (float)(n.startTick + n.durationTicks) / quantum * StepW;
                        drag = new Drag { index=index, start=p, startTick=n.startTick, duration=n.durationTicks,
                            pitch=n.pitch, resizing=(right-p.x)<10f, pointerId=evt.pointerId };
                        plane.CapturePointer(evt.pointerId);
                        evt.StopPropagation();
                        return;
                    }
                }
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
                Vector2 p = plane.WorldToLocal(evt.position);
                if ((p-drag.start).sqrMagnitude>16) drag.moved=true;
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
                var d = drag; drag = null;
                if (plane.HasPointerCapture(evt.pointerId)) plane.ReleasePointer(evt.pointerId);
                if (d.moved && Writable() && d.index >= 0 && d.index < content.notes.Count)
                {
                    Vector2 p = plane.WorldToLocal(evt.position);
                    int tickChange = Mathf.RoundToInt((p.x-d.start.x)/StepW);
                    var n = content.notes[d.index];
                    if (d.resizing) n.durationTicks = Math.Max(quantum, d.duration+tickChange*quantum);
                    else
                    {
                        n.startTick = Math.Max(0, d.startTick+tickChange*quantum);
                        n.pitch = Mathf.Clamp(d.pitch-Mathf.RoundToInt((p.y-d.start.y)/RowH),0,127);
                    }
                    content.lengthTicks = Math.Max(content.lengthTicks,n.startTick+n.durationTicks);
                }
                Changed();
                evt.StopPropagation();
            });
            plane.RegisterCallback<PointerCancelEvent>(evt =>
            {
                if (pendingTap != null && pendingTap.pointerId == evt.pointerId)
                {
                    pendingTap.hold?.Pause();
                    pendingTap = null;
                }
                if (drag != null && drag.pointerId == evt.pointerId) drag = null;
                if (plane.HasPointerCapture(evt.pointerId)) plane.ReleasePointer(evt.pointerId);
            });
            plane.RegisterCallback<PointerCaptureOutEvent>(evt =>
            {
                if (pendingTap != null && pendingTap.pointerId == evt.pointerId)
                {
                    pendingTap.hold?.Pause();
                    pendingTap = null;
                }
                if (drag != null && drag.pointerId == evt.pointerId) drag = null;
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
                    }
                }).ExecuteLater(0);
        }
        private void Update()
        {
            // Keeping pitch guidance in sync doesn't depend on the transport playing.
            FollowPitchedSampleKey();
            if (!ready ||
                root == null ||
                editorPlayhead == null ||
                clip == null ||
                content == null)
                return;

            // Recover the transport if it was created after initialization.
            if (songTransport == null)
                songTransport = FindObjectOfType<ZPunkSongTransport>();

            if (songTransport == null)
                return;

            if (root.resolvedStyle.display == DisplayStyle.None ||
                showingSampler)
                return;

            // Limit UI Toolkit positioning updates to 30 FPS.
            if (Time.unscaledTime < nextPlayheadPaint)
                return;

            nextPlayheadPaint = Time.unscaledTime + 1f / 30f;

            // Use the arrangement's musical clock.
            long globalTick = songTransport.CurrentTick;

            // Calculate the selected clip's relative position.
            long localTick = globalTick - clip.startTick
                            + clip.sourceOffsetTick;

            // Determine the visible editor sequence length.
            long cycleTicks = clip.loopEnabled && content.lengthTicks > 0
                ? content.lengthTicks
                : Math.Max(content.lengthTicks, clip.lengthTicks);

            long visibleTicks = (long)steps * quantum;

            cycleTicks = Math.Max(
                quantum,
                Math.Min(cycleTicks, visibleTicks)
            );

            // Wrap the position instead of hiding the playhead.
            // This also handles clips placed later in the arrangement.
            long wrappedTick = localTick % cycleTicks;

            if (wrappedTick < 0)
                wrappedTick += cycleTicks;

            // Convert ticks to horizontal grid pixels.
            float x = (float)wrappedTick / quantum * StepW;

            // Keep the line visible at the left edge.
            x = Mathf.Max(1f, x);

            // Only change the element when the position actually moves.
            if (Mathf.Abs(x - lastPlayheadX) >= 0.4f)
            {
                lastPlayheadX = x;
                editorPlayhead.style.left = x;
                if (rulerPlayhead != null) rulerPlayhead.style.left = x;
            }

            // Keep the position indicator visible even when stopped.
            editorPlayhead.style.display = DisplayStyle.Flex;
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
