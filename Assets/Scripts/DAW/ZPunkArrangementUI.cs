using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace ZPunk.DAW
{
    /// <summary>
    /// Phase 3A/B. Runtime UI Toolkit arrangement tab. Scene-independent; attaches to
    /// existing UIDocument via KMusicTabsUI. Edits SongData, never the legacy pattern grids.
    /// Clip edits are snap-to-grid and are auditioned at the next DAW Play (Phase 2 snapshot).
    /// </summary>
    public sealed class ZPunkArrangementUI : MonoBehaviour
    {
        private float HeaderWidth = 170f;
        private float RowHeight = 55f;
        private float RulerHeight = 26f;
        private float uiScale = 1f;
        private const int MaxVisibleBars = 256;
        private static readonly Color Bg = new Color(.075f, .092f, .125f);
        private static readonly Color Surface = new Color(.112f, .135f, .176f);
        private static readonly Color Text = new Color(.91f, .94f, .97f);
        private static readonly Color MutedText = new Color(.58f, .65f, .73f);
        private static readonly Color Highlight = new Color(.32f, .86f, .70f);

        private KMusicProjectManager projects;
        private ZPunkSongTransport transport;
        private VisualElement page;
        // This viewport moves below the expanding project menu without
        // compressing the timeline or its command strips.
        private ScrollView arrangementPageScroll;
        private VisualElement timelineFrame;
        private VisualElement trackHeaders;
        private VisualElement canvas;
        private VisualElement playhead;
        private VisualElement rulerVisual;
        private Label playheadHandle;
        private int playheadPointerId = -1;
        private Vector2 playheadPointerWorld;
        private bool resumeAfterPlayheadDrag;
        private const float PlayheadEdgePixels = 42f;

        // A playback cursor belongs only to an active clip, not to every lane.
        private sealed class ClipPlaybackCursor
        {
            public TrackData track;
            public ClipData clip;
            public long sourceLengthTicks;
            public VisualElement marker;
            public float width;
        }
        private readonly List<ClipPlaybackCursor> clipPlaybackCursors = new List<ClipPlaybackCursor>();
        private ScrollView timelineViewport;
        private Label visibleBarLabel;
        private int viewStartBar;
        private Label hint;
        private Label details;
        private Button loopSongButton;
        private Button snapButton;
        private Label loopBarsLabel;
        private VisualElement bpmRail, bpmFill, bpmThumb;
        private Label bpmValue;
        private float pixelsPerBar = 78f;
        private long snapQuantum = SongData.TicksPerBar4;
        private SongData song;
        private bool ownsSong;
        private int projectSlot = -1;
        private string selectedClipId;
        private int selectedTrackIndex;
        private VisualElement selectedVisual;
        private bool initialized;
        private int renderedBars;

        private sealed class Gesture
        {
            public string id;
            public Vector2 position;
            public long originalStart;
            public long originalLength;
            public long candidateStart;
            public long candidateLength;
            public Vector2 viewportOffset;
            public bool resize;
            public bool moved;
            public int pointerId;
            public VisualElement rect;
        }
        private Gesture gesture;
        public string SelectedClipId => selectedClipId;
        public int SelectedTrackIndex => selectedTrackIndex;
        public Action<int, string> EditRequested;

        public void Initialize(VisualElement target)
        {
            if (target == null || (initialized && ReferenceEquals(page, target))) return;
            // The arrangement intercepts clip drags before ScrollView's touch handler.
            // Unregister if the workspace is rebuilt with a different root.
            if (page != null)
            {
                page.UnregisterCallback<PointerDownEvent>(OnClipPointerDown, TrickleDown.TrickleDown);
                page.UnregisterCallback<PointerMoveEvent>(OnClipPointerMove, TrickleDown.TrickleDown);
                page.UnregisterCallback<PointerUpEvent>(OnClipPointerUp, TrickleDown.TrickleDown);
                page.UnregisterCallback<PointerCancelEvent>(OnClipPointerCancel, TrickleDown.TrickleDown);
                page.UnregisterCallback<PointerCaptureOutEvent>(OnClipCaptureLost);
            }
            gesture = null;
            playheadPointerId = -1;
            page = target;
            projects = FindObjectOfType<KMusicProjectManager>();
            transport = FindObjectOfType<ZPunkSongTransport>();
            BuildLayout();
            page.RegisterCallback<PointerDownEvent>(OnClipPointerDown, TrickleDown.TrickleDown);
            page.RegisterCallback<PointerMoveEvent>(OnClipPointerMove, TrickleDown.TrickleDown);
            page.RegisterCallback<PointerUpEvent>(OnClipPointerUp, TrickleDown.TrickleDown);
            page.RegisterCallback<PointerCancelEvent>(OnClipPointerCancel, TrickleDown.TrickleDown);
            page.RegisterCallback<PointerCaptureOutEvent>(OnClipCaptureLost);
            initialized = true;
        }

        public void OnShown()
        {
            if (!initialized) return;
            if (projects == null) projects = FindObjectOfType<KMusicProjectManager>();
            if (transport == null) transport = FindObjectOfType<ZPunkSongTransport>();
            if (projects == null) { SetHint("Project manager not found."); return; }
            // Loading a different project invalidates the cached arrangement document.
            if (projectSlot != projects.CurrentSlot ||
                (ownsSong && !ReferenceEquals(projects.CurrentSong, song)))
            {
                selectedClipId = null;
                selectedTrackIndex = 0;
                ownsSong = false;
                song = null;
            }
            projectSlot = projects.CurrentSlot;
            if (!ownsSong)
            {
                try { song = projects.GetSongForArrangementEditor(); }
                catch (Exception ex) { Debug.LogWarning("[ZPunk DAW] Cannot open arrangement: " + ex.Message); }
            }
            Refresh();
        }

        /// <summary>Scale touch targets in the Unity PanelSettings reference coordinate
        /// space. Called by the workspace shell when device/orientation changes.</summary>
        public void SetWorkspaceScale(float scale)
        {
            // UI physical dimensions remain stable across tablet/phone/rotation.
            // The timeline is paged at fixed bar widths; no horizontal scroll or resizing.
            scale = Mathf.Clamp(scale, 1f, 1.12f);
            if (Mathf.Abs(scale - uiScale) < .025f) return;
            uiScale = scale;
            HeaderWidth = 170f;
            RowHeight = 55f;
            RulerHeight = 26f;
            pixelsPerBar = Mathf.Clamp(pixelsPerBar, 42f, 154f);
            if (page == null) return;
            page.style.paddingLeft = 8 * scale;
            page.style.paddingRight = 8 * scale;
            page.style.paddingTop = 8 * scale;
            page.style.paddingBottom = 8 * scale;
            foreach (var button in page.Query<Button>().ToList())
            {
                button.style.height = 44 * scale;
                button.style.fontSize = 13 * scale;
                // Keep touch targets usable even before the first layout pass.
                // Equal-width command rows must not inherit minimum widths
                // that push the last button outside the viewport.
                if (button.ClassListContains("zp-equal-command"))
                {
                    button.style.minWidth = 0;
                    button.style.fontSize = 12 * scale;
                    button.style.paddingLeft = 3 * scale;
                    button.style.paddingRight = 3 * scale;
                }
                else
                    button.style.minWidth = Mathf.Max(48f,
                        (button.text != null ? button.text.Length : 0) * 8.5f + 16f) * scale;
            }
            var songTitle = page.Q<Label>("ZPunkSongTitle");
            if (songTitle != null) songTitle.style.fontSize = 13 * scale;
            if (details != null) details.style.fontSize = 12 * scale;
            if (hint != null) hint.style.fontSize = 11 * scale;
            var clipActions = page.Q<VisualElement>("ZPunkClipActions");
            if (clipActions != null) clipActions.style.height = 51 * scale;
            var songActions = page.Q<VisualElement>("ZPunkSongActions");
            if (songActions != null) songActions.style.height = 51 * scale;
            if (timelineFrame != null)
                timelineFrame.style.height = RulerHeight + 5 * RowHeight + 2 * scale;
            if (song != null) Refresh();
        }

        private bool MakeEditable()
        {
            if (projects == null || song == null) { SetHint("No song to edit."); return false; }
            if (ownsSong) return true;
            // First modification promotes a deep copy, so legacy grids/bank are untouched.
            song = ArrangementEditing.IndependentCopy(song);
            if (song == null) return false;
            // The inherited one-bar preview loop should not unexpectedly truncate an arrangement.
            if (song.migratedFromLegacy) song.arrangementLoopEnabled = false;
            if (!song.loopLengthCustom)
                song.loopEndTick = AutomaticLoopEnd(song);
            projects.AcceptArrangementEdit(song);
            ownsSong = true;
            return true;
        }

        private void SetHint(string value) { if (hint != null) hint.text = value; }

        private Button Button(string label, Action click, float minWidth = 64)
        {
            var b = new Button(click) { text = label };
            b.style.minWidth = minWidth * uiScale;
            b.style.flexShrink = 0;
            b.style.height = 44 * uiScale;
            b.style.marginRight = 5 * uiScale;
            b.style.marginBottom = 2 * uiScale;
            b.style.paddingLeft = 10 * uiScale;
            b.style.paddingRight = 10 * uiScale;
            b.style.color = Text;
            b.style.backgroundColor = new Color(.18f, .22f, .29f);
            b.style.borderTopLeftRadius = 5;
            b.style.borderTopRightRadius = 5;
            b.style.borderBottomLeftRadius = 5;
            b.style.borderBottomRightRadius = 5;
            b.style.fontSize = 13 * uiScale;
            b.style.unityFontStyleAndWeight = FontStyle.Bold;
            return b;
        }

        private Label Label(string str, int size, Color color)
        {
            var label = new Label(str);
            label.style.fontSize = Mathf.Max(12f, size * uiScale);
            label.style.color = color;
            label.style.unityTextAlign = TextAnchor.MiddleLeft;
            return label;
        }

        private void BuildLayout()
        {
            page.Clear();
            page.style.backgroundColor = Bg;
            page.style.paddingLeft = 7 * uiScale;
            page.style.paddingRight = 7 * uiScale;
            page.style.paddingTop = 6 * uiScale;
            page.style.paddingBottom = 14 * uiScale;
            page.style.flexDirection = FlexDirection.Column;
            page.style.flexGrow = 1;
            page.style.flexShrink = 1;
            page.style.minHeight = 0;
            page.style.overflow = Overflow.Hidden;

            // Let the visible area get shorter when the project menu opens,
            // not the arrangement inside it. The top of the arrangement is
            // pushed down; anything below the bottom dock remains reachable
            // with a vertical swipe/scroll, as in the other DAW workspaces.
            arrangementPageScroll = new ScrollView(ScrollViewMode.Vertical)
            {
                name = "ZPunkArrangementPageScroll"
            };
            arrangementPageScroll.style.flexGrow = 1;
            arrangementPageScroll.style.flexShrink = 1;
            arrangementPageScroll.style.minHeight = 0;
            arrangementPageScroll.style.minWidth = 0;
            arrangementPageScroll.style.width = new Length(100, LengthUnit.Percent);
            arrangementPageScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            arrangementPageScroll.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            arrangementPageScroll.contentContainer.style.flexDirection = FlexDirection.Column;
            arrangementPageScroll.contentContainer.style.flexShrink = 0;
            page.Add(arrangementPageScroll);

            var title = Label("SONG ARRANGEMENT", 14, Text);
            title.name = "ZPunkSongTitle";
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.marginBottom = 5;
            arrangementPageScroll.Add(title);

            // Equal-width, fully visible command rows; no hidden horizontal scrolling.
            var editing = MakeCommandStrip("ZPunkClipActions");
            editing.Add(CommandButton("+ CLIP", AddClip));
            editing.Add(CommandButton("EDIT", EditSelected));
            editing.Add(CommandButton("DUP", DuplicateSelected));
            editing.Add(CommandButton("DEL", DeleteSelected));
            arrangementPageScroll.Add(editing);

            var transportControls = MakeCommandStrip("ZPunkSongActions");
            transportControls.Add(CommandButton("- ZOOM", () => Zoom(-14)));
            transportControls.Add(CommandButton("+ ZOOM", () => Zoom(14)));
            snapButton = CommandButton("SNAP BAR", CycleSnap);
            transportControls.Add(snapButton);
            loopSongButton = CommandButton("LOOP OFF", ToggleSongLoop);
            transportControls.Add(loopSongButton);
            arrangementPageScroll.Add(transportControls);

            var tempoRow=new VisualElement { name="ZPunkArrangementTempo" };
            tempoRow.style.flexDirection=FlexDirection.Row;
            tempoRow.style.alignItems=Align.Center;
            tempoRow.style.height=37;
            tempoRow.style.flexShrink=0;
            tempoRow.style.marginTop=5;
            var tempoLabel=Label("TEMPO",10,MutedText);
            tempoLabel.style.width=52;
            tempoRow.Add(tempoLabel);
            bpmRail=new VisualElement { name="ZPunkTempoRail" };
            bpmRail.style.height=32;
            bpmRail.style.position=Position.Relative;
            bpmRail.style.flexGrow=1;
            bpmRail.style.minWidth=0;
            bpmRail.style.justifyContent=Justify.Center;
            var railBg=new VisualElement();
            railBg.style.height=7;
            railBg.style.backgroundColor=new Color(.055f,.079f,.118f);
            railBg.style.borderTopLeftRadius=3;
            railBg.style.borderTopRightRadius=3;
            railBg.style.borderBottomLeftRadius=3;
            railBg.style.borderBottomRightRadius=3;
            railBg.pickingMode=PickingMode.Ignore;
            bpmRail.Add(railBg);
            bpmFill=new VisualElement();
            bpmFill.style.position=Position.Absolute;
            bpmFill.style.left=0;
            bpmFill.style.top=12;
            bpmFill.style.height=7;
            bpmFill.style.backgroundColor=Highlight;
            bpmFill.style.borderTopLeftRadius=3;
            bpmFill.style.borderTopRightRadius=3;
            bpmFill.pickingMode=PickingMode.Ignore;
            bpmRail.Add(bpmFill);
            bpmThumb=new VisualElement();
            bpmThumb.style.position=Position.Absolute;
            bpmThumb.style.top=6;
            bpmThumb.style.width=13;
            bpmThumb.style.height=19;
            bpmThumb.style.backgroundColor=Text;
            bpmThumb.style.borderTopLeftRadius=4;
            bpmThumb.style.borderTopRightRadius=4;
            bpmThumb.style.borderBottomLeftRadius=4;
            bpmThumb.style.borderBottomRightRadius=4;
            bpmThumb.pickingMode=PickingMode.Ignore;
            bpmRail.Add(bpmThumb);
            Action<Vector2> moveTempo=pos=>
            {
                float t=Mathf.Clamp01(bpmRail.WorldToLocal(pos).x/Mathf.Max(1f,bpmRail.contentRect.width));
                SetTempo(60f + t*140f);
            };
            bpmRail.RegisterCallback<PointerDownEvent>(evt=>
            {
                if(evt.button!=0)return;
                bpmRail.CapturePointer(evt.pointerId);
                moveTempo(evt.position);
                evt.StopPropagation();
            });
            bpmRail.RegisterCallback<PointerMoveEvent>(evt=>
            {
                if(!bpmRail.HasPointerCapture(evt.pointerId))return;
                moveTempo(evt.position);
                evt.StopPropagation();
            });
            bpmRail.RegisterCallback<PointerUpEvent>(evt=>
            {
                if(!bpmRail.HasPointerCapture(evt.pointerId))return;
                moveTempo(evt.position);
                bpmRail.ReleasePointer(evt.pointerId);
                evt.StopPropagation();
            });
            bpmRail.RegisterCallback<PointerCancelEvent>(evt=>
            {
                if(bpmRail.HasPointerCapture(evt.pointerId))bpmRail.ReleasePointer(evt.pointerId);
            });
            tempoRow.Add(bpmRail);
            bpmValue=Label("120 BPM",11,Highlight);
            bpmValue.style.width=68;
            bpmValue.style.unityTextAlign=TextAnchor.MiddleRight;
            tempoRow.Add(bpmValue);
            arrangementPageScroll.Add(tempoRow);

            // Independent loop length: the arrangement may contain more clips or
            // trailing empty bars than the section the musician wants repeated.
            var loopRow = new VisualElement { name = "ZPunkLoopLengthControls" };
            loopRow.style.flexDirection = FlexDirection.Row;
            loopRow.style.alignItems = Align.Center;
            loopRow.style.height = 39;
            loopRow.style.flexShrink = 0;
            loopRow.style.marginTop = 3;
            var loopCaption = Label("LOOP BARS", 10, MutedText);
            loopCaption.style.flexGrow = 1;
            loopCaption.style.minWidth = 0;
            loopRow.Add(loopCaption);
            var decrease = Button("-", () => AdjustLoopBars(-1), 44);
            decrease.style.minWidth = 44;
            decrease.style.width = 44;
            decrease.style.paddingLeft = 0;
            decrease.style.paddingRight = 0;
            loopRow.Add(decrease);
            loopBarsLabel = Label("4", 12, Highlight);
            loopBarsLabel.style.width = 46;
            loopBarsLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            loopRow.Add(loopBarsLabel);
            var increase = Button("+", () => AdjustLoopBars(1), 44);
            increase.style.minWidth = 44;
            increase.style.width = 44;
            increase.style.paddingLeft = 0;
            increase.style.paddingRight = 0;
            loopRow.Add(increase);
            arrangementPageScroll.Add(loopRow);

            var arrangement = new VisualElement { name = "ZPunkArrangementBoard" };
            arrangement.style.flexDirection = FlexDirection.Row;
            timelineFrame = arrangement;
            arrangement.style.height = RulerHeight + 5 * RowHeight + 2;
            // The timeline is never allowed to shrink just because the
            // project menu takes vertical room from the workspace.
            arrangement.style.flexShrink = 0;
            arrangement.style.marginTop = 12;
            arrangement.style.marginBottom = 6;
            arrangement.style.backgroundColor = new Color(.065f, .092f, .13f);
            arrangement.style.overflow = Overflow.Hidden;
            arrangementPageScroll.Add(arrangement);

            trackHeaders = new VisualElement();
            trackHeaders.style.width = HeaderWidth;
            trackHeaders.style.flexShrink = 0;
            trackHeaders.style.backgroundColor = new Color(.075f, .105f, .15f);
            arrangement.Add(trackHeaders);

            // Native touch scrolling keeps bars at a fixed musical width.
            // Both scrollbars are hidden without disabling finger swipes.
            timelineViewport = new ScrollView(ScrollViewMode.Horizontal) { name = "ZPunkArrangementViewport" };
            timelineViewport.style.flexGrow = 1;
            timelineViewport.style.flexShrink = 1;
            timelineViewport.style.minWidth = 0;
            timelineViewport.style.position = Position.Relative;
            timelineViewport.style.overflow = Overflow.Hidden;
            timelineViewport.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            timelineViewport.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            timelineViewport.contentContainer.style.flexDirection = FlexDirection.Row;
            arrangement.Add(timelineViewport);
            timelineViewport.RegisterCallback<GeometryChangedEvent>(_ => ApplyTimelinePage());

            canvas = new VisualElement { name = "ZPunkTimelineCanvas" };
            canvas.style.position = Position.Relative;
            canvas.style.top = 0;
            canvas.style.flexShrink = 0;
            canvas.style.backgroundColor = Surface;
            timelineViewport.Add(canvas);

            details = Label("Select track, + CLIP, then EDIT to compose.", 11, Text);
            details.style.marginTop = 5 * uiScale;
            details.style.marginBottom = 4 * uiScale;
            arrangementPageScroll.Add(details);
            hint = Label("Drag clips to arrange. Tap EDIT for drums and piano roll.", 10, MutedText);
            hint.style.whiteSpace = WhiteSpace.Normal;
            hint.style.marginTop = 3 * uiScale;
            hint.style.marginBottom = 4 * uiScale;
            arrangementPageScroll.Add(hint);
        }

        private Button CommandButton(string label, Action action)
        {
            var button = Button(label, action, 0);
            button.AddToClassList("zp-equal-command");
            button.style.minWidth = 0;
            button.style.flexGrow = 1;
            button.style.flexShrink = 1;
            button.style.flexBasis = new Length(0, LengthUnit.Pixel);
            button.style.marginLeft = 2 * uiScale;
            button.style.marginRight = 2 * uiScale;
            button.style.paddingLeft = 3 * uiScale;
            button.style.paddingRight = 3 * uiScale;
            button.style.fontSize = 12 * uiScale;
            button.style.whiteSpace = WhiteSpace.NoWrap;
            return button;
        }

        private VisualElement MakeCommandStrip(string name)
        {
            var row = new VisualElement { name = name };
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexWrap = Wrap.NoWrap;
            row.style.alignItems = Align.Center;
            row.style.width = new Length(100, LengthUnit.Percent);
            row.style.height = 51 * uiScale;
            row.style.marginBottom = 3 * uiScale;
            row.style.flexShrink = 0;
            return row;
        }

        private void NudgeSelected(int bars)
        {
            var clip = ArrangementEditing.FindClip(song, selectedClipId, out _);
            if (clip == null) { SetHint("Select a clip to move it."); return; }
            string id = clip.id;
            if (!MakeEditable()) return;
            clip = ArrangementEditing.FindClip(song, id, out _);
            long tick = Math.Max(0, clip.startTick + bars * SongData.TicksPerBar4);
            if (ArrangementEditing.Move(song, clip, tick)) UpdateAfterChange();
            else SetHint("Cannot move this clip there.");
        }

        private void LengthenSelected(int bars)
        {
            var clip = ArrangementEditing.FindClip(song, selectedClipId, out _);
            if (clip == null) { SetHint("Select a clip to resize it."); return; }
            string id = clip.id;
            if (!MakeEditable()) return;
            clip = ArrangementEditing.FindClip(song, id, out _);
            long ticks = Math.Max(SongData.TicksPerSixteenth, clip.lengthTicks + bars * SongData.TicksPerBar4);
            if (ArrangementEditing.Resize(song, clip, ticks)) UpdateAfterChange();
            else SetHint("Cannot resize this clip.");
        }

        private void Refresh()
        {
            if (!initialized || song == null) return;
            // Rebuilding clips must not snap a partially scrolled timeline to a whole bar.
            Vector2 previousOffset = timelineViewport != null ? timelineViewport.scrollOffset : Vector2.zero;
            trackHeaders.Clear();
            canvas.Clear();
            clipPlaybackCursors.Clear();
            int count = song.tracks != null ? song.tracks.Count : 0;
            int bars = (int)Math.Ceiling((double)Math.Max(song.lengthTicks, SongData.TicksPerBar4) / SongData.TicksPerBar4);
            renderedBars = Mathf.Clamp(bars + 4, 16, MaxVisibleBars);
            float width = renderedBars * pixelsPerBar;
            canvas.style.width = width;
            canvas.style.height = RulerHeight + count * RowHeight;

            var corner = new VisualElement();
            corner.style.height = RulerHeight;
            corner.style.justifyContent = Justify.Center;
            corner.style.paddingLeft = 8;
            corner.Add(Label("TRACK", 10, MutedText));
            trackHeaders.Add(corner);

            var ruler = new VisualElement();
            ruler.style.position = Position.Relative;
            ruler.style.height = RulerHeight;
            ruler.style.width = width;
            ruler.style.backgroundColor = new Color(.14f, .17f, .22f);
            // Ruler is an independent, full-width scrub target. Capturing on
            // the stable page leaves clip editing and empty-lane swipes intact.
            // Page receives the gesture first (trickle-down), before ScrollView
            // can convert ruler touches into timeline panning.
            rulerVisual = ruler;
            canvas.Add(ruler);
            for (int bar = 0; bar < renderedBars; ++bar)
            {
                float x = bar * pixelsPerBar;
                var tick = Label((bar + 1).ToString(), 10, MutedText);
                tick.style.position = Position.Absolute;
                tick.style.left = x + 5;
                tick.style.top = 7;
                ruler.Add(tick);
            }

            for (int i = 0; i < count; ++i)
            {
                var track = song.tracks[i];
                if (track == null) continue;
                int index = i;
                Color color = TrackColor(track);
                var info = new VisualElement();
                info.style.height = RowHeight;
                info.style.flexDirection = FlexDirection.Row;
                info.style.alignItems = Align.Center;
                info.style.paddingLeft = 7;
                info.style.paddingRight = 8;
                info.style.backgroundColor = selectedTrackIndex == i ? new Color(.18f,.24f,.30f) :
                    new Color(.105f,.14f,.19f);
                var strip = new VisualElement();
                strip.style.width = 3;
                strip.style.height = RowHeight - 10;
                strip.style.backgroundColor = color;
                strip.style.marginRight = 5;
                info.Add(strip);
                var name = Label((track.name ?? track.kind.ToString()).ToUpperInvariant(), 10, color);
                name.style.flexGrow = 1;
                name.style.minWidth = 0;
                name.RegisterCallback<PointerDownEvent>(evt => { selectedTrackIndex = index; SelectClip(null); evt.StopPropagation(); });
                info.Add(name);
                // Two full-size touch targets instead of the former 23px stacked
                // buttons. Keeping these inline leaves the timeline's row height
                // and five-lane overview unchanged.
                var toggles = new VisualElement();
                toggles.style.flexDirection = FlexDirection.Row;
                toggles.style.alignItems = Align.Center;
                toggles.style.width = 91;
                toggles.style.marginRight = 3;
                toggles.style.flexShrink = 0;
                var m = Button(track.mute ? "M!" : "M", () => ToggleMute(index), 42);
                m.style.minWidth = 42;
                m.style.width = 42;
                m.style.height = 44;
                m.style.fontSize = 12;
                m.style.marginRight = 4;
                m.style.marginBottom = 0;
                m.style.paddingLeft = 0;
                m.style.paddingRight = 0;
                m.style.backgroundColor = track.mute ? new Color(.55f,.21f,.29f) : new Color(.20f,.25f,.32f);
                toggles.Add(m);
                var s = Button(track.solo ? "S!" : "S", () => ToggleSolo(index), 42);
                s.style.minWidth = 42;
                s.style.width = 42;
                s.style.height = 44;
                s.style.fontSize = 12;
                s.style.marginRight = 0;
                s.style.marginBottom = 0;
                s.style.paddingLeft = 0;
                s.style.paddingRight = 0;
                s.style.backgroundColor = track.solo ? new Color(.55f,.42f,.14f) : new Color(.20f,.25f,.32f);
                toggles.Add(s);
                info.Add(toggles);
                trackHeaders.Add(info);

                var lane = new VisualElement();
                lane.name = "Lane_" + track.id;
                lane.style.position = Position.Relative;
                lane.style.width = width;
                lane.style.height = RowHeight;
                lane.style.overflow = Overflow.Hidden;
                lane.style.backgroundColor = i % 2 == 0 ? new Color(.115f, .145f, .185f) : new Color(.095f, .123f, .165f);
                canvas.Add(lane);
                lane.RegisterCallback<PointerDownEvent>(evt =>
                {
                    if (evt.button != 0) return;
                    selectedTrackIndex = index;
                    if (evt.clickCount >= 2)
                    {
                        float x = lane.WorldToLocal(evt.position).x;
                        CreateAt(index, ArrangementEditing.Snap(PixelToTick(x), snapQuantum));
                    }
                    else SelectClip(null);
                });
                for (int bar = 0; bar < renderedBars; bar++)
                {
                    var line = new VisualElement();
                    line.style.position = Position.Absolute;
                    line.style.left = bar * pixelsPerBar;
                    line.style.top = 0;
                    line.style.height = RowHeight;
                    line.style.width = 1;
                    line.style.backgroundColor = new Color(.25f, .29f, .36f, .48f);
                    line.pickingMode = PickingMode.Ignore;
                    lane.Add(line);
                }
                if (track.clips == null) continue;
                foreach (var clip in track.clips)
                    if (clip != null && clip.lengthTicks > 0) AddClipView(lane, track, clip, color);
            }
            playhead = new VisualElement();
            playhead.style.position = Position.Absolute;
            playhead.style.top = 0;
            playhead.style.width = 2;
            // Global playhead spans the arrangement; the existing neon clip
            // markers still show only the clips actually being played.
            playhead.style.height = RulerHeight + count * RowHeight;
            playhead.style.backgroundColor = new Color(Highlight.r, Highlight.g, Highlight.b, .72f);
            playhead.pickingMode = PickingMode.Ignore;
            canvas.Add(playhead);
            playheadHandle = new Label("HEAD") { name = "ZPunkArrangementPlayheadHandle" };
            playheadHandle.style.position = Position.Absolute;
            playheadHandle.style.top = 1;
            playheadHandle.style.width = 44;
            playheadHandle.style.height = RulerHeight - 1;
            playheadHandle.style.fontSize = 10;
            playheadHandle.style.unityFontStyleAndWeight = FontStyle.Bold;
            playheadHandle.style.unityTextAlign = TextAnchor.MiddleCenter;
            playheadHandle.style.color = new Color(.035f, .075f, .075f);
            playheadHandle.style.backgroundColor = Highlight;
            playheadHandle.style.borderTopLeftRadius = 4;
            playheadHandle.style.borderTopRightRadius = 4;
            playheadHandle.style.borderBottomLeftRadius = 4;
            playheadHandle.style.borderBottomRightRadius = 4;
            playheadHandle.pickingMode = PickingMode.Position;
            canvas.Add(playheadHandle);
            RefreshPlayhead();
            UpdateDetails();
            if (song.arrangementLoopEnabled && !song.loopLengthCustom)
                song.loopEndTick = AutomaticLoopEnd(song);
            if (loopSongButton != null) loopSongButton.text = song.arrangementLoopEnabled ? "LOOP ON" : "LOOP OFF";
            if (loopBarsLabel != null)
            {
                long loopTicks = song.loopLengthCustom ? song.loopEndTick : AutomaticLoopEnd(song);
                loopBarsLabel.text = Math.Max(1L, (loopTicks + SongData.TicksPerBar4 - 1) / SongData.TicksPerBar4).ToString();
            }
            RefreshTempoRail(song.bpm);
            RestoreTimelineOffset(previousOffset);
        }

        private void RestoreTimelineOffset(Vector2 offset)
        {
            if (timelineViewport == null) return;
            timelineViewport.scrollOffset = new Vector2(Mathf.Max(0f, offset.x), 0f);
            viewStartBar = Mathf.Clamp(Mathf.FloorToInt(
                timelineViewport.scrollOffset.x / pixelsPerBar), 0, Mathf.Max(0, renderedBars - 1));
            if (visibleBarLabel != null) visibleBarLabel.text = "BAR " + (viewStartBar + 1);
        }

        private void AddClipView(VisualElement lane, TrackData track, ClipData clip, Color color)
        {
            string id = clip.id;
            float left = TickToPixel(clip.startTick);
            float width = Mathf.Max(8f, TickToPixel(clip.lengthTicks) - 2f);
            var rect = new VisualElement();
            rect.name = "Clip_" + id;
            rect.style.position = Position.Absolute;
            rect.style.left = left;
            rect.style.top = 7;
            rect.style.width = width;
            rect.style.height = RowHeight - 14;
            rect.style.backgroundColor = color;
            rect.style.borderTopLeftRadius = 4;
            rect.style.borderTopRightRadius = 4;
            rect.style.borderBottomLeftRadius = 4;
            rect.style.borderBottomRightRadius = 4;
            rect.style.borderBottomWidth = id == selectedClipId ? 3 : 0;
            rect.style.borderBottomColor = Color.white;
            rect.pickingMode = PickingMode.Position;
            var label = Label(clip.name ?? "CLIP", 10, new Color(.035f, .055f, .08f));
            label.style.position = Position.Absolute;
            label.style.left = 6;
            label.style.top = 3;
            label.style.right = 10;
            label.style.overflow = Overflow.Hidden;
            label.pickingMode = PickingMode.Ignore;
            rect.Add(label);
            AddClipMiniature(rect, clip, width);
            var resizeGrip = new VisualElement();
            resizeGrip.style.position = Position.Absolute;
            resizeGrip.style.right = 0;
            resizeGrip.style.top = 2;
            resizeGrip.style.bottom = 2;
            resizeGrip.style.width = 9;
            resizeGrip.style.backgroundColor = new Color(.05f, .07f, .09f, .45f);
            // Keep the visual grip slim; its parent provides a larger invisible touch target.
            resizeGrip.pickingMode = PickingMode.Ignore;
            rect.Add(resizeGrip);

            // Draw within the clip instead of across all track lanes.
            var cursor = new VisualElement { name = "ActiveClipCursor" };
            cursor.style.position = Position.Absolute;
            cursor.style.top = 0;
            cursor.style.bottom = 0;
            cursor.style.left = 0;
            cursor.style.width = 3;
            cursor.style.backgroundColor = Highlight;
            cursor.style.opacity = .95f;
            cursor.style.display = DisplayStyle.None;
            cursor.pickingMode = PickingMode.Ignore;
            rect.Add(cursor);
            ClipContentData source = ArrangementEditing.FindContent(song, clip.contentId);
            clipPlaybackCursors.Add(new ClipPlaybackCursor
            {
                track = track,
                clip = clip,
                sourceLengthTicks = Math.Max(1L, source != null ? source.lengthTicks : clip.lengthTicks),
                marker = cursor,
                width = width
            });
            lane.Add(rect);
        }

        // Scrubbing stops scheduled voices once on press, not on every move.
        // If the song was playing it restarts from the chosen tick on release.
        private bool HitPlayheadScrubLane(Vector2 world)
        {
            if (rulerVisual == null || canvas == null ||
                timelineViewport == null) return false;
            // Only capture the visible ruler. Clips/lane gestures remain separate,
            // and touches on the adjacent track column do not seek.
            return rulerVisual.worldBound.Contains(world) &&
                timelineViewport.contentViewport.worldBound.Contains(world);
        }

        private void OnRulerPointerDown(PointerDownEvent evt)
        {
            if (evt.button != 0 || page == null || song == null ||
                canvas == null || gesture != null || playheadPointerId >= 0 ||
                !HitPlayheadScrubLane(evt.position)) return;
            if (transport == null) transport = FindObjectOfType<ZPunkSongTransport>();
            playheadPointerId = evt.pointerId;
            playheadPointerWorld = evt.position;
            if (playheadHandle != null)
            {
                playheadHandle.text = "DRAG";
                playheadHandle.style.backgroundColor = Color.white;
            }
            resumeAfterPlayheadDrag = transport != null && transport.IsPlaying;
            if (resumeAfterPlayheadDrag) transport.StopSong();
            page.CapturePointer(evt.pointerId);
            SeekPlayheadFromPointer();
            evt.StopPropagation();
        }

        private long PlayheadTickAtWorld(Vector2 world)
        {
            if (canvas == null || song == null) return 0;
            float px = Mathf.Clamp(canvas.WorldToLocal(world).x, 0f,
                Mathf.Max(0f, renderedBars * pixelsPerBar));
            return Math.Max(0L, Math.Min(Math.Max(0L, song.lengthTicks), PixelToTick(px)));
        }

        private void SeekPlayheadFromPointer()
        {
            if (playheadPointerId < 0 || transport == null) return;
            long tick = PlayheadTickAtWorld(playheadPointerWorld);
            if (transport.CurrentTick != tick) transport.Seek(tick);
            RefreshPlayhead();
        }

        private void EndPlayheadDrag(bool resume)
        {
            if (playheadPointerId < 0) return;
            int pointerId = playheadPointerId;
            // Clear the gesture BEFORE release: CaptureOut may arrive synchronously.
            playheadPointerId = -1;
            if (page != null && page.HasPointerCapture(pointerId)) page.ReleasePointer(pointerId);
            bool shouldResume = resumeAfterPlayheadDrag && resume;
            resumeAfterPlayheadDrag = false;
            if (playheadHandle != null)
            {
                playheadHandle.text = "HEAD";
                playheadHandle.style.backgroundColor = Highlight;
            }
            if (shouldResume && transport != null)
                transport.PlaySong(transport.CurrentTick);
            RefreshPlayhead();
        }

        private void AutoScrollPlayheadDrag()
        {
            if (playheadPointerId < 0 || timelineViewport == null || canvas == null) return;
            float width = timelineViewport.contentViewport.resolvedStyle.width;
            if (float.IsNaN(width) || width <= 1f) return;
            float screenX = timelineViewport.contentViewport.WorldToLocal(playheadPointerWorld).x;
            float push = screenX < PlayheadEdgePixels ? screenX - PlayheadEdgePixels :
                screenX > width - PlayheadEdgePixels ? screenX - (width - PlayheadEdgePixels) : 0f;
            if (Mathf.Abs(push) < .001f) return;
            float maxOffset = Mathf.Max(0f, renderedBars * pixelsPerBar - width);
            float delta = Mathf.Sign(push) * Mathf.Lerp(0.15f, 1f,
                Mathf.Clamp01(Mathf.Abs(push) / PlayheadEdgePixels)) *
                Mathf.Max(60f, width * .65f) * Time.unscaledDeltaTime;
            float next = Mathf.Clamp(timelineViewport.scrollOffset.x + delta, 0f, maxOffset);
            if (Mathf.Abs(next - timelineViewport.scrollOffset.x) < .01f) return;
            timelineViewport.scrollOffset = new Vector2(next, 0f);
            SeekPlayheadFromPointer();
        }

        private void FollowArrangementPlayhead()
        {
            if (timelineViewport == null || transport == null || !transport.IsPlaying ||
                !transport.SongMode || playheadPointerId >= 0 || gesture != null) return;
            float width = timelineViewport.contentViewport.resolvedStyle.width;
            if (float.IsNaN(width) || width <= 1f) return;
            float x = TickToPixel(transport.CurrentTick);
            float offset = Mathf.Max(0f, timelineViewport.scrollOffset.x);
            if (x >= offset + width * .82f || x < offset + width * .12f)
            {
                float anchor = x >= offset + width * .82f ? .72f : .22f;
                float maxOffset = Mathf.Max(0f, renderedBars * pixelsPerBar - width);
                timelineViewport.scrollOffset = new Vector2(
                    Mathf.Clamp(x - width * anchor, 0f, maxOffset), 0f);
            }
        }

        // Clip interactions are handled in the page's trickle-down phase, before
        // ScrollView receives PointerDown. This prevents native touch panning from
        // stealing a resize when the right clip edge moves left of the finger.
        // Blank lanes continue to use ordinary ScrollView swipes.
        private void OnClipPointerDown(PointerDownEvent evt)
        {
            if (evt.button != 0 || gesture != null || song == null || canvas == null ||
                playheadPointerId >= 0) return;
            // This callback is on the PAGE in trickle-down. It runs before the
            // built-in horizontal ScrollView touch handler, unlike callbacks on
            // the ruler itself. Scrubbing is therefore reliable on mobile.
            if (HitPlayheadScrubLane(evt.position))
            {
                OnRulerPointerDown(evt);
                return;
            }
            var rect = evt.target as VisualElement;
            while (rect != null && rect != page &&
                   (rect.name == null || !rect.name.StartsWith("Clip_", StringComparison.Ordinal)))
                rect = rect.parent;
            if (rect == null || rect == page || rect.parent == null || rect.parent.parent != canvas) return;

            string id = rect.name.Substring(5);
            var clip = ArrangementEditing.FindClip(song, id, out _);
            if (clip == null) return;
            selectedClipId = id;
            selectedVisual = rect;
            selectedTrackIndex = FindTrackIndex(id);
            UpdateDetails();
            float x = rect.WorldToLocal(evt.position).x;
            gesture = new Gesture
            {
                id = id,
                rect = rect,
                position = evt.position,
                pointerId = evt.pointerId,
                originalStart = clip.startTick,
                originalLength = clip.lengthTicks,
                candidateStart = clip.startTick,
                candidateLength = clip.lengthTicks,
                viewportOffset = timelineViewport != null ? timelineViewport.scrollOffset : Vector2.zero,
                resize = x >= rect.layout.width - Mathf.Min(24f, rect.layout.width)
            };
            // Capture on the page: unlike the clip itself, this target never
            // shrinks out from under the finger during a leftward resize.
            page.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        }

        private void OnClipPointerMove(PointerMoveEvent evt)
        {
            if (playheadPointerId == evt.pointerId)
            {
                playheadPointerWorld = evt.position;
                SeekPlayheadFromPointer();
                evt.StopPropagation();
                return;
            }
            if (gesture == null || gesture.pointerId != evt.pointerId) return;
            evt.StopPropagation();
            RestoreTimelineOffset(gesture.viewportOffset);
            float dx = evt.position.x - gesture.position.x;
            if (Mathf.Abs(dx) > 3f) gesture.moved = true;
            if (!gesture.moved) return;
            long offset = ArrangementEditing.SnapDelta(PixelToTick(dx), snapQuantum);
            if (gesture.resize)
            {
                gesture.candidateLength = Math.Max(snapQuantum,
                    Math.Min(ArrangementEditing.MaxSongTicks - gesture.originalStart,
                        gesture.originalLength + offset));
                gesture.rect.style.width = Mathf.Max(8f, TickToPixel(gesture.candidateLength) - 2f);
            }
            else
            {
                gesture.candidateStart = Math.Max(0,
                    Math.Min(ArrangementEditing.MaxSongTicks - gesture.originalLength,
                        gesture.originalStart + offset));
                gesture.rect.style.left = TickToPixel(gesture.candidateStart);
            }
        }

        private void OnClipPointerUp(PointerUpEvent evt)
        {
            if (playheadPointerId == evt.pointerId)
            {
                playheadPointerWorld = evt.position;
                SeekPlayheadFromPointer();
                EndPlayheadDrag(true);
                evt.StopPropagation();
                return;
            }
            if (gesture == null || gesture.pointerId != evt.pointerId) return;
            evt.StopPropagation();
            var drag = gesture;
            gesture = null;
            if (page.HasPointerCapture(evt.pointerId)) page.ReleasePointer(evt.pointerId);
            RestoreTimelineOffset(drag.viewportOffset);
            if (drag.moved && MakeEditable())
            {
                var current = ArrangementEditing.FindClip(song, drag.id, out _);
                if (current != null)
                {
                    if (drag.resize) ArrangementEditing.Resize(song, current, drag.candidateLength);
                    else ArrangementEditing.Move(song, current, drag.candidateStart);
                }
                UpdateAfterChange();
            }
            else if (evt.clickCount >= 2) EditRequested?.Invoke(selectedTrackIndex, selectedClipId);
            else Refresh();
        }

        private void OnClipPointerCancel(PointerCancelEvent evt)
        {
            if (playheadPointerId == evt.pointerId)
            {
                EndPlayheadDrag(false);
                evt.StopPropagation();
                return;
            }
            if (gesture == null || gesture.pointerId != evt.pointerId) return;
            evt.StopPropagation();
            var offset = gesture.viewportOffset;
            gesture = null;
            if (page.HasPointerCapture(evt.pointerId)) page.ReleasePointer(evt.pointerId);
            RestoreTimelineOffset(offset);
            Refresh();
        }

        private void OnClipCaptureLost(PointerCaptureOutEvent evt)
        {
            if (playheadPointerId == evt.pointerId)
            {
                EndPlayheadDrag(false);
                return;
            }
            if (gesture == null || gesture.pointerId != evt.pointerId) return;
            var offset = gesture.viewportOffset;
            gesture = null;
            RestoreTimelineOffset(offset);
            Refresh();
        }

        /// <summary>Compact event preview: shows real note and drum locations,
        /// including repeated content, without adding extra controls or panels.</summary>
        private void AddClipMiniature(VisualElement rect, ClipData clip, float width)
        {
            if (song == null || clip == null || width < 16f) return;
            var content = ArrangementEditing.FindContent(song, clip.contentId);
            if (content == null || content.lengthTicks <= 0) return;
            long contentLength = content.lengthTicks;
            long maxTick = clip.lengthTicks;
            int budget = 96;
            long repeats = clip.loopEnabled ? Math.Min(32L, (maxTick + contentLength - 1L) / contentLength) : 1L;
            // Limit thumbnail visual children for extremely long clips.
            for (long repeat = 0; repeat < repeats && budget > 0; repeat++)
            {
                long baseTick = repeat * contentLength;
                if (content.drums != null)
                    foreach (var hit in content.drums)
                    {
                        if (hit == null || budget <= 0) break;
                        long tick = baseTick + hit.startTick - clip.sourceOffsetTick;
                        if (tick < 0 || tick >= maxTick) continue;
                        float x = 3f + (float)tick / maxTick * (width - 6f);
                        AddMiniMark(rect, x, 18f + (hit.lane % 4) * 4f, 2.5f, 3f);
                        budget--;
                    }
                if (content.notes != null)
                    foreach (var note in content.notes)
                    {
                        if (note == null || budget <= 0) break;
                        long tick = baseTick + note.startTick - clip.sourceOffsetTick;
                        if (tick < 0 || tick >= maxTick) continue;
                        float x = 3f + (float)tick / maxTick * (width - 6f);
                        float length = Mathf.Max(3f, (float)Math.Min(note.durationTicks, maxTick - tick) / maxTick * (width - 6f));
                        AddMiniMark(rect, x, 18f + (note.pitch % 5) * 3f, length, 2.7f);
                        budget--;
                    }
                if (content.chops != null)
                    foreach (var chop in content.chops)
                    {
                        if (chop == null || budget <= 0) break;
                        long tick = baseTick + chop.startTick - clip.sourceOffsetTick;
                        if (tick < 0 || tick >= maxTick) continue;
                        float x = 3f + (float)tick / maxTick * (width - 6f);
                        AddMiniMark(rect, x, 20f + (chop.sliceId % 3) * 4f, 3f, 5f);
                        budget--;
                    }
            }
        }

        private static void AddMiniMark(VisualElement parent, float x, float y, float width, float height)
        {
            var mark = new VisualElement();
            mark.style.position = Position.Absolute;
            mark.style.left = x;
            mark.style.top = y;
            mark.style.width = width;
            mark.style.height = height;
            mark.style.backgroundColor = new Color(.09f, .15f, .19f, .64f);
            mark.style.borderTopLeftRadius = 1;
            mark.style.borderTopRightRadius = 1;
            mark.pickingMode = PickingMode.Ignore;
            parent.Add(mark);
        }

        private long PixelToTick(float pixels) { return (long)Math.Round(pixels / pixelsPerBar * SongData.TicksPerBar4); }
        private float TickToPixel(long ticks) { return (float)((double)ticks / SongData.TicksPerBar4 * pixelsPerBar); }
        private static Color TrackColor(TrackData t)
        {
            if (t != null && !string.IsNullOrEmpty(t.colorHex) &&
                ColorUtility.TryParseHtmlString(t.colorHex, out Color color)) return color;
            return new Color(.38f, .72f, .66f);
        }
        private int FindTrackIndex(string id)
        {
            if (song == null || song.tracks == null) return 0;
            for (int i = 0; i < song.tracks.Count; i++)
            {
                var t = song.tracks[i];
                if (t == null || t.clips == null) continue;
                foreach (var c in t.clips)
                    if (c != null && c.id == id) return i;
            }
            return 0;
        }
        private void SelectClip(string id) { selectedClipId = id; selectedVisual = null; Refresh(); }
        private void UpdateDetails()
        {
            if (details == null || song == null) return;
            var clip = ArrangementEditing.FindClip(song, selectedClipId, out TrackData owner);
            if (clip == null)
            {
                if (song.tracks == null || song.tracks.Count == 0) return;
                selectedTrackIndex = Mathf.Clamp(selectedTrackIndex, 0, song.tracks.Count - 1);
                details.text = "TRACK: " + song.tracks[selectedTrackIndex].name + "  /  Tap + CLIP to create";
                return;
            }
            details.text = (owner != null ? owner.name : "TRACK") + "  /  BAR " +
                (clip.startTick / SongData.TicksPerBar4 + 1) + "  /  " +
                ((double)clip.lengthTicks / SongData.TicksPerBar4).ToString("0.#") + " BARS";
        }
        private void CreateAt(int trackIndex, long atTick)
        {
            if (!MakeEditable() || song.tracks == null || trackIndex < 0 || trackIndex >= song.tracks.Count) return;
            var clip = ArrangementEditing.CreateClip(song, song.tracks[trackIndex], atTick);
            if (clip == null) return;
            selectedClipId = clip.id;
            selectedTrackIndex = trackIndex;
            UpdateAfterChange();
            SetHint("New clip ready. Tap EDIT to program notes and steps.");
        }
        private void EditSelected()
        {
            if (song == null || song.tracks == null || song.tracks.Count == 0) return;
            EditRequested?.Invoke(selectedTrackIndex, selectedClipId);
        }
        private void AddClip()
        {
            if (song == null) return;
            long tick = 0;
            var selected = ArrangementEditing.FindClip(song, selectedClipId, out _);
            if (selected != null) tick = selected.startTick + selected.lengthTicks;
            if (song.tracks != null && selectedTrackIndex >= 0 && selectedTrackIndex < song.tracks.Count)
                tick = ArrangementEditing.NextFreeStart(song.tracks[selectedTrackIndex], tick, SongData.TicksPerBar4);
            CreateAt(selectedTrackIndex, ArrangementEditing.Snap(tick, snapQuantum));
        }
        private void DuplicateSelected()
        {
            var source = ArrangementEditing.FindClip(song, selectedClipId, out TrackData owner);
            if (source == null) { SetHint("Select a clip to duplicate."); return; }
            string id = source.id;
            if (!MakeEditable()) return;
            source = ArrangementEditing.FindClip(song, id, out owner);
            var copy = ArrangementEditing.Duplicate(song, source, owner);
            if (copy == null) { SetHint("Could not duplicate this clip."); return; }
            selectedClipId = copy.id;
            UpdateAfterChange();
        }
        private void DeleteSelected()
        {
            var source = ArrangementEditing.FindClip(song, selectedClipId, out _);
            if (source == null) { SetHint("Select a clip to delete."); return; }
            string id = source.id;
            if (!MakeEditable()) return;
            source = ArrangementEditing.FindClip(song, id, out TrackData owner);
            ArrangementEditing.Delete(song, source, owner);
            selectedClipId = null;
            UpdateAfterChange();
        }
        private void ToggleClipLoop()
        {
            var current = ArrangementEditing.FindClip(song, selectedClipId, out _);
            if (current == null) { SetHint("Select a clip to toggle its repeat setting."); return; }
            string id = current.id;
            if (!MakeEditable()) return;
            current = ArrangementEditing.FindClip(song, id, out _);
            current.loopEnabled = !current.loopEnabled;
            UpdateAfterChange();
        }
        private void ToggleMute(int index)
        {
            if (!MakeEditable() || song.tracks == null || index < 0 || index >= song.tracks.Count) return;
            song.tracks[index].mute = !song.tracks[index].mute;
            UpdateAfterChange();
        }
        private void ToggleSolo(int index)
        {
            if (!MakeEditable() || song.tracks == null || index < 0 || index >= song.tracks.Count) return;
            song.tracks[index].solo = !song.tracks[index].solo;
            UpdateAfterChange();
        }
        private void RefreshTempoRail(float value)
        {
            float t=Mathf.Clamp01((value-60f)/140f);
            if(bpmFill!=null)bpmFill.style.width=new Length(t*100f,LengthUnit.Percent);
            if(bpmThumb!=null)bpmThumb.style.left=new Length(t*100f,LengthUnit.Percent);
            if(bpmValue!=null)bpmValue.text=Mathf.RoundToInt(value)+" BPM";
        }
        private void SetTempo(float tempo)
        {
            if(song==null || projects==null)return;
            float value=Mathf.Round(Mathf.Clamp(tempo,60f,200f));
            RefreshTempoRail(value);
            if(Mathf.Approximately(value,song.bpm))return;
            if(!MakeEditable())return;
            song.bpm=value;
            projects.AcceptArrangementEdit(song);
            if (transport != null) transport.SetLiveTempo(value, song);
            SetHint("Tempo " + value.ToString("0") + " BPM  /  LIVE");
        }

        private static long AutomaticLoopEnd(SongData document)
        {
            long end = SongData.TicksPerBar4;
            if (document != null && document.tracks != null)
                foreach (var track in document.tracks)
                    if (track != null && track.clips != null)
                        foreach (var clip in track.clips)
                            if (clip != null && clip.lengthTicks > 0)
                                end = Math.Max(end, clip.startTick + clip.lengthTicks);
            return end;
        }

        private void AdjustLoopBars(int direction)
        {
            if (song == null || !MakeEditable()) return;
            long baseEnd = song.loopLengthCustom ? song.loopEndTick : AutomaticLoopEnd(song);
            int bars = Mathf.Clamp((int)Math.Max(1L,
                (baseEnd + SongData.TicksPerBar4 - 1) / SongData.TicksPerBar4) + direction,
                1, MaxVisibleBars);
            song.loopLengthCustom = true;
            song.arrangementLoopEnabled = true;
            song.loopStartTick = 0;
            song.loopEndTick = (long)bars * SongData.TicksPerBar4;
            // The planner must span the complete looping window, even when the
            // last portion contains deliberate silence rather than clips.
            song.lengthTicks = Math.Max(song.lengthTicks, song.loopEndTick);
            UpdateAfterChange();
            SetHint("Loop length: " + bars + " bars  /  LIVE");
        }

        private void ToggleSongLoop()
        {
            if (!MakeEditable()) return;
            song.arrangementLoopEnabled = !song.arrangementLoopEnabled;
            song.loopStartTick = 0;
            if (!song.loopLengthCustom) song.loopEndTick = AutomaticLoopEnd(song);
            UpdateAfterChange();
        }
        private void SaveSong()
        {
            if (projects == null) return;
            if (song != null && !ownsSong && MakeEditable()) { /* Save a snapshot, not a live preview. */ }
            projects.SaveProject(projects.CurrentSlot);
            SetHint("Project saved.");
        }
        private void CycleSnap()
        {
            if (snapQuantum == SongData.TicksPerBar4)
            {
                snapQuantum = SongData.TicksPerQuarter;
                snapButton.text = "SNAP 1/4";
            }
            else if (snapQuantum == SongData.TicksPerQuarter)
            {
                snapQuantum = SongData.TicksPerSixteenth;
                snapButton.text = "SNAP 1/16";
            }
            else
            {
                snapQuantum = SongData.TicksPerBar4;
                snapButton.text = "SNAP BAR";
            }
        }
        private void PageBars(int direction)
        {
            if (renderedBars <= 0 || timelineViewport == null) return;
            float width = timelineViewport.resolvedStyle.width;
            int step = Mathf.Max(1, Mathf.FloorToInt(Mathf.Max(1f, width) / pixelsPerBar) - 1);
            // Start from the actual swipe position, not the last button press.
            viewStartBar = Mathf.Clamp(Mathf.FloorToInt(timelineViewport.scrollOffset.x / pixelsPerBar)
                + direction * step, 0, renderedBars - 1);
            ApplyTimelinePage();
        }
        private void ApplyTimelinePage()
        {
            if (canvas == null || timelineViewport == null) return;
            viewStartBar = Mathf.Clamp(viewStartBar, 0, Mathf.Max(0, renderedBars - 1));
            timelineViewport.scrollOffset = new Vector2(viewStartBar * pixelsPerBar, 0f);
            if (visibleBarLabel != null)
                visibleBarLabel.text = "BAR " + (viewStartBar + 1);
        }
        private void Zoom(float delta)
        {
            if (timelineViewport == null || canvas == null || renderedBars <= 0) return;
            float oldScale = pixelsPerBar;
            float newScale = Mathf.Clamp(oldScale + delta, 42f, 154f);
            if (Mathf.Approximately(oldScale, newScale)) return;

            float viewWidth = timelineViewport.contentViewport.resolvedStyle.width;
            if (float.IsNaN(viewWidth) || viewWidth <= 0f)
                viewWidth = timelineViewport.resolvedStyle.width;
            viewWidth = Mathf.Max(1f, viewWidth);
            float oldOffset = Mathf.Max(0f, timelineViewport.scrollOffset.x);

            // Keep the actual playback head at the same on-screen position when
            // zooming, rather than preserving its old pixel coordinate or jumping
            // back to the first bar. When stopped, use the current transport tick
            // if it is visible, otherwise preserve the visible viewport centre.
            float anchorBar = (oldOffset + viewWidth * .5f) / oldScale;
            float anchorScreenX = viewWidth * .5f;
            if (transport == null) transport = FindObjectOfType<ZPunkSongTransport>();
            if (transport != null && transport.SongMode)
            {
                float headBar = (float)((double)Math.Max(0L, transport.CurrentTick) /
                    SongData.TicksPerBar4);
                float headScreenX = headBar * oldScale - oldOffset;
                if (transport.IsPlaying || (headScreenX >= 0f && headScreenX <= viewWidth))
                {
                    anchorBar = headBar;
                    // Keep it near the centre while playing, so zooming is useful
                    // even when the head was near the edge of the viewport.
                    anchorScreenX = viewWidth * .5f;
                }
            }

            pixelsPerBar = newScale;
            Refresh(); // Recreate ruler, grid and clips using the new scale.
            float maxOffset = Mathf.Max(0f, renderedBars * pixelsPerBar - viewWidth);
            float desiredOffset = Mathf.Clamp(anchorBar * pixelsPerBar - anchorScreenX,
                0f, maxOffset);
            RestoreTimelineOffset(new Vector2(desiredOffset, 0f));
            // ScrollView may recalculate its content size in the next layout pass.
            // Restore the anchored offset after geometry has settled, too.
            var viewport = timelineViewport;
            viewport.schedule.Execute(() =>
            {
                if (viewport != timelineViewport || canvas == null) return;
                float readyWidth = viewport.contentViewport.resolvedStyle.width;
                if (float.IsNaN(readyWidth) || readyWidth <= 0f) readyWidth = viewWidth;
                float max = Mathf.Max(0f, renderedBars * pixelsPerBar - readyWidth);
                RestoreTimelineOffset(new Vector2(Mathf.Clamp(desiredOffset, 0f, max), 0f));
            }).ExecuteLater(0);
        }
        private void UpdateAfterChange()
        {
            if (song == null) return;
            Refresh();
            if (transport == null) transport = FindObjectOfType<ZPunkSongTransport>();
            if (transport != null) transport.ApplyLiveSong(song);
            SetHint("Arrangement updated  /  LIVE");
        }
        private void RefreshPlayhead()
        {
            if (playhead == null || song == null) return;
            if (transport == null) transport = FindObjectOfType<ZPunkSongTransport>();
            bool activeTransport = transport != null && transport.SongMode;
            bool moving = activeTransport && transport.IsPlaying;
            // Show stopped/seeked position, too. Clip-local cursors still need
            // active playback so they cannot light up on silent clips.
            // The cursor MUST be shown before pressing PLAY, even while the
            // transport component is still initializing on the first frame.
            playhead.style.display = DisplayStyle.Flex;
            if (playheadHandle != null) playheadHandle.style.display = DisplayStyle.Flex;
            {
                float x = TickToPixel(transport != null ? transport.CurrentTick : 0L);
                playhead.style.left = x;
                if (playheadHandle != null)
                    playheadHandle.style.left = Mathf.Clamp(x - 22f, 0f,
                        Mathf.Max(0f, renderedBars * pixelsPerBar - 44f));
            }

            long tick = moving ? transport.CurrentTick : -1;
            bool anySolo = false;
            if (moving && song.tracks != null)
                foreach (var track in song.tracks)
                    if (track != null && track.solo) { anySolo = true; break; }

            foreach (var entry in clipPlaybackCursors)
            {
                TrackData track = entry.track;
                ClipData clip = entry.clip;
                bool audible = moving && track != null && !track.mute && track.volume > 0f &&
                    (!anySolo || track.solo);
                long relativeTick = audible && clip != null ? tick - clip.startTick : -1;
                bool active = audible && clip != null && relativeTick >= 0 &&
                    relativeTick < clip.lengthTicks;
                if (active && !clip.loopEnabled)
                {
                    long length = entry.sourceLengthTicks;
                    long offset = ((clip.sourceOffsetTick % length) + length) % length;
                    active = relativeTick + offset < length;
                }
                entry.marker.style.display = active ? DisplayStyle.Flex : DisplayStyle.None;
                if (active)
                    entry.marker.style.left = Mathf.Clamp(TickToPixel(relativeTick), 0f,
                        Mathf.Max(0f, entry.width - 3f));
            }
        }
        private void Update()
        {
            if (!initialized || page == null || page.resolvedStyle.display == DisplayStyle.None) return;
            if (projects != null && (projects.CurrentSlot != projectSlot ||
                (ownsSong && !ReferenceEquals(projects.CurrentSong, song)))) { OnShown(); return; }
            RefreshPlayhead();
            AutoScrollPlayheadDrag();
            FollowArrangementPlayhead();
            // Defend against ScrollView touch inertia during a captured clip gesture.
            if (gesture != null) RestoreTimelineOffset(gesture.viewportOffset);
            // Reflect touch scrolling in the existing bar counter. Do not assign
            // scrollOffset here: that would interfere with native drag inertia.
            if (timelineViewport != null && visibleBarLabel != null)
            {
                int firstBar = Mathf.Clamp(Mathf.FloorToInt(
                    Mathf.Max(0f, timelineViewport.scrollOffset.x) / pixelsPerBar), 0, renderedBars - 1);
                viewStartBar = firstBar;
                string label = "BAR " + (firstBar + 1);
                if (visibleBarLabel.text != label) visibleBarLabel.text = label;
            }
        }
    }
}
