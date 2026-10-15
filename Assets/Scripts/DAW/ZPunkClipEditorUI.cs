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
        private VisualElement root, canvas, gridScrollContainer, chips;
        private VisualElement editorPlayhead;
        private VisualElement samplerPage, samplerWave;
        private Label samplerTitle;
        private bool showingSampler;
        private KMusicPlayerUI samplePlayer;
        private ZPunkSongTransport songTransport;
        private ScrollView scroller;
        private Label title, info, selectedInfo;
        private KMusicProjectManager projects;
        private SongData song;
        private ClipData clip;
        private ClipContentData content;
        private int trackIndex;
        private string clipId;
        private int octave = 4;
        private long quantum = SongData.TicksPerSixteenth;
        private int selectedNote = -1;
        private int selectedEventRow = -1;
        private long selectedEventTick = -1;
        private int velocity = 110;
        private const float StepW = 24f;
        private const float RowH = 27f;
        // Render every MIDI pitch (C-1 through G9). The user scrolls the same
        // continuous 128-note keyboard; octave controls only move the viewport.
        private const int MidiRows = 128;
        private int steps;
        private bool ready;

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
            label.style.fontSize = size;
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
            b.style.height = 37;
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
            b.style.fontSize = 11;
            b.style.unityFontStyleAndWeight = FontStyle.Bold;
            return b;
        }
        private void BuildShell()
        {
            root.Clear();
            root.style.flexDirection = FlexDirection.Column;
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
            heading.Add(title);
            var samplerShortcut = Btn("SAMPLER", OpenSampler, 74);
            samplerShortcut.style.color = Accent;
            heading.Add(samplerShortcut);
            heading.Add(Btn("+ NEW CLIP", NewClip, 98));
            root.Add(heading);

            var tracks = new ScrollView(ScrollViewMode.Horizontal);
            tracks.style.height = 49;
            tracks.style.flexShrink = 0;
            tracks.style.marginTop = 6;
            tracks.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            tracks.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            chips = tracks.contentContainer;
            chips.style.flexDirection = FlexDirection.Row;
            chips.style.flexWrap = Wrap.NoWrap;
            root.Add(tracks);

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
            row.Add(Btn("SAVE", Save, 53));
            var controls = new ScrollView(ScrollViewMode.Horizontal);
            controls.style.height = 48;
            controls.style.flexShrink = 0;
            controls.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            controls.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            controls.Add(row);
            root.Add(controls);

            info = Txt("Choose a track, then create or select a clip.",11,Muted);
            info.style.height = 28;
            info.style.flexShrink = 0;
            root.Add(info);

            scroller = new ScrollView(ScrollViewMode.VerticalAndHorizontal) { name = "ZPunkNoteScroll" };
            scroller.style.flexGrow = 1;
            scroller.style.minHeight = 170;
            scroller.style.maxHeight = 655;
            scroller.style.backgroundColor = new Color(.047f,.068f,.105f);
            scroller.style.borderTopLeftRadius = 8;
            scroller.style.borderTopRightRadius = 8;
            scroller.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            scroller.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            gridScrollContainer = new VisualElement();
            gridScrollContainer.style.flexDirection = FlexDirection.Row;
            scroller.Add(gridScrollContainer);
            root.Add(scroller);

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
            Action<Vector2> setVelocity=(position)=>
            {
                float normalized=Mathf.Clamp01(velRail.WorldToLocal(position).x/Mathf.Max(1,velRail.contentRect.width));
                velocity=Mathf.Clamp(Mathf.RoundToInt(1+126*normalized),1,127);
                velFill.style.width=new Length(normalized*100,LengthUnit.Percent);
                velThumb.style.left=new Length(normalized*100,LengthUnit.Percent);
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
            var footer = Txt("TAP to place/remove  |  DRAG notes to move  |  DRAG right edge to resize",10,Muted);
            footer.style.whiteSpace = WhiteSpace.Normal;
            footer.style.marginTop = 5;
            footer.style.marginBottom = 8;
            root.Add(footer);
            BuildSamplerPage();
        }

        private void BuildSamplerPage()
        {
            samplerPage = new VisualElement { name = "ZPunkSamplerPage" };
            samplerPage.style.display = DisplayStyle.None;
            samplerPage.style.flexDirection = FlexDirection.Column;
            samplerPage.style.flexGrow = 1;
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
            samplerWave = Box(new Color(.049f,.073f,.105f),4);
            samplerWave.style.height = 145;
            samplerWave.style.flexShrink = 0;
            samplerWave.style.marginTop = 10;
            samplerPage.Add(samplerWave);
            var help = Txt("SLICE POINTS   /   TAP WAVEFORM TO ADJUST",10,Muted);
            help.style.marginTop = 7;
            help.style.marginBottom = 8;
            samplerPage.Add(help);
            var slices = new VisualElement();
            slices.style.flexDirection = FlexDirection.Row;
            slices.style.height = 46;
            slices.Add(Btn("4 SLICES",()=>AutoSlice(4),83));
            slices.Add(Btn("8 SLICES",()=>AutoSlice(8),83));
            slices.Add(Btn("16 SLICES",()=>AutoSlice(16),92));
            slices.Add(Btn("APPLY",()=>{FindSampler();samplePlayer?.DawApplySlices();RefreshSampler();},75));
            var sliceActions = new ScrollView(ScrollViewMode.Horizontal);
            sliceActions.style.height = 49;
            sliceActions.style.flexShrink = 0;
            sliceActions.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            sliceActions.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            sliceActions.Add(slices);
            samplerPage.Add(sliceActions);
            var padScroll = new ScrollView(ScrollViewMode.Vertical);
            padScroll.style.flexGrow = 1;
            padScroll.style.minHeight = 0;
            padScroll.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            for (int group = 0; group < 4; group++)
            {
                var padRow = new VisualElement();
                padRow.style.flexDirection = FlexDirection.Row;
                padRow.style.height = 55;
                for (int j = 0; j < 4; j++)
                {
                    int slice = group * 4 + j + 1;
                    var button = Btn("S"+slice.ToString("00"),()=>{
                        var drum = FindObjectOfType<KMusicDrumSequencer>();
                        if (drum != null) drum.AuditionChop(slice);
                    },50);
                    button.style.flexGrow = 1;
                    button.style.minWidth = 0;
                    button.style.marginRight = 5;
                    button.style.height = 48;
                    button.style.backgroundColor = Raised;
                    padRow.Add(button);
                }
                padScroll.Add(padRow);
            }
            samplerPage.Add(padScroll);
            root.Add(samplerPage);
        }

        private void FindSampler()
        {
            if (samplePlayer == null) samplePlayer = FindObjectOfType<KMusicPlayerUI>();
        }
        private void AutoSlice(int n)
        {
            FindSampler();
            samplePlayer?.DawAutoSlice(n);
            RefreshSampler();
        }
        private void RefreshSampler()
        {
            FindSampler();
            if (samplerTitle != null)
                samplerTitle.text = samplePlayer != null ? samplePlayer.DawSampleName : "SAMPLER UNAVAILABLE";
        }
        private void ShowSampler(bool value)
        {
            showingSampler = value;
            if (root == null || samplerPage == null) return;
            foreach (var child in root.Children())
                child.style.display = child == samplerPage ?
                    (value ? DisplayStyle.Flex : DisplayStyle.None) :
                    (value ? DisplayStyle.None : DisplayStyle.Flex);
            if (!value) return;
            FindSampler();
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
                }
            }
            RefreshSampler();
        }
        private void OpenSampler() { ShowSampler(true); }

        public void OnShown(int requestedTrack=-1, string requestedClip=null)
        {
            if (!ready) return;
            ShowSampler(false);
            if (projects == null) projects = FindObjectOfType<KMusicProjectManager>();
            if (projects == null) return;
            song = projects.GetSongForArrangementEditor();
            if (song == null || song.tracks == null || song.tracks.Count == 0) return;
            if (requestedTrack >= 0 && requestedTrack < song.tracks.Count) trackIndex = requestedTrack;
            trackIndex = Mathf.Clamp(trackIndex,0,song.tracks.Count-1);
            if (!string.IsNullOrEmpty(requestedClip)) clipId = requestedClip;
            LoadClip();
            // Open existing melodies near their actual pitch rather than at the
            // top of the 128-row keyboard where most tracks have no notes.
            if (content != null && content.kind == ClipKind.Midi &&
                content.notes != null && content.notes.Count > 0 && content.notes[0] != null)
                octave = Mathf.Clamp(content.notes[0].pitch / 12 - 1, -1, 9);
            Redraw();
            ScrollToOctave();
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
        private void Changed(bool rebuild=true)
        {
            if (rebuild) Redraw();
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
            Changed();
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
            Redraw();
            ScrollToOctave();
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
        private void ChangeVelocity()
        {
            if (selectedInfo != null) selectedInfo.text = velocity.ToString();
            if (content == null) return;
            if(content.kind==ClipKind.Midi)
            {
                if(selectedNote<0 || content.notes==null || selectedNote>=content.notes.Count || !Writable())return;
                content.notes[selectedNote].velocity=velocity;
            }
            else if(content.kind==ClipKind.Drums)
            {
                if(content.drums==null || selectedEventRow<0 || !Writable())return;
                var hit=content.drums.Find(d=>d!=null && d.lane==selectedEventRow && d.startTick==selectedEventTick);
                if(hit==null)return;
                hit.velocity=velocity;
            }
            else
            {
                if(content.chops==null || selectedEventRow<0 || !Writable())return;
                var hit=content.chops.Find(d=>d!=null && d.sliceId==selectedEventRow+1 && d.startTick==selectedEventTick);
                if(hit==null)return;
                hit.velocity=velocity;
            }
            OnClipChanged?.Invoke();
        }
        private bool IsMidi => content != null && content.kind == ClipKind.Midi;
        private int RowCount => IsMidi ? MidiRows : content != null && content.kind == ClipKind.Chops ? 16 : 8;
        private int PitchForRow(int row) => Mathf.Clamp(127 - row, 0, 127);
        private int RowForPitch(int pitch) => 127 - pitch;

        // Scroll both the pitch names and notes together in their shared ScrollView.
        // This provides the full MIDI 0..127 range without truncating note events.
        private void ScrollToOctave()
        {
            if (scroller == null || !IsMidi) return;
            int desiredTopPitch = Mathf.Clamp((octave + 2) * 12 - 1, 0, 127);
            float y = 25f + (127 - desiredTopPitch) * RowH;
            scroller.schedule.Execute(() =>
            {
                if (scroller != null && IsMidi)
                    scroller.scrollOffset = new Vector2(scroller.scrollOffset.x, y);
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
            selectedEventRow=row;
            selectedEventTick=tick;
            if (content.kind == ClipKind.Drums)
            {
                if (content.drums == null) content.drums = new List<DrumEvent>();
                int lane = row;
                int i = content.drums.FindIndex(d=>d != null && d.lane==lane && d.startTick==tick);
                if (i>=0) content.drums.RemoveAt(i);
                else content.drums.Add(new DrumEvent { lane=lane,startTick=tick,velocity=velocity,legacyVelocityTier=2 });
            }
            else if (content.kind == ClipKind.Chops)
            {
                if (content.chops == null) content.chops = new List<ChopEvent>();
                int slice = row + 1;
                int i = content.chops.FindIndex(d=>d != null && d.sliceId==slice && d.startTick==tick);
                if (i>=0) content.chops.RemoveAt(i);
                else content.chops.Add(new ChopEvent { sliceId=slice,startTick=tick,velocity=velocity });
            }
            else
            {
                if (content.notes == null) content.notes = new List<NoteEvent>();
                int pitch=PitchForRow(row);
                int existing=FindNote(pitch,tick);
                if (existing>=0) { content.notes.RemoveAt(existing); selectedNote=-1; }
                else
                {
                    content.notes.Add(new NoteEvent{pitch=pitch,startTick=tick,durationTicks=quantum,velocity=velocity});
                    selectedNote=content.notes.Count-1;
                }
            }
            content.lengthTicks=Math.Max(content.lengthTicks,tick+quantum);
            Changed();
        }

        private void Redraw()
        {
            if (!ready || root == null || chips == null || gridScrollContainer == null) return;
            // Refreshing note rectangles must not throw the user back to MIDI 127.
            // OCT +/- and initial track selection can still reposition afterwards.
            Vector2 previousOffset = scroller != null ? scroller.scrollOffset : Vector2.zero;
            chips.Clear();
            if (song == null || song.tracks == null) return;
            for (int i=0;i<song.tracks.Count;i++)
            {
                int index=i;
                var item=Btn(song.tracks[i].name.ToUpperInvariant(),()=>SelectTrack(index),72);
                item.style.backgroundColor=i==trackIndex ? colors[Mathf.Min(i,4)] : Raised;
                item.style.color=i==trackIndex ? new Color(.03f,.07f,.10f) : Ink;
                chips.Add(item);
            }
            chips.Add(Btn("SAMPLER", OpenSampler, 90));
            if (title != null) title.text = song.tracks[trackIndex].name.ToUpperInvariant()+"  /  " + (clip != null ? clip.name : "NO CLIP");
            if (info != null) info.text= clip == null ? "NO CLIP  /  Tap + NEW CLIP to begin" :
                (IsMidi ? "PIANO ROLL" : content != null && content.kind==ClipKind.Drums ? "DRUM STEP EDITOR" : "SAMPLE STEP EDITOR")+
                "  /  Bar " + (clip.startTick / SongData.TicksPerBar4 + 1) + "  /  LIVE EDIT";
            gridScrollContainer.Clear();
            editorPlayhead = null;
            if (clip==null || content==null) return;
            long editLength=Math.Max(clip.lengthTicks,content.lengthTicks);
            steps=Mathf.Clamp(Mathf.CeilToInt((float)editLength/quantum),16,128);
            var names=Box(new Color(.095f,.13f,.18f));
            names.style.width=IsMidi ? 57f : 83f;
            names.style.flexShrink=0;
            names.style.flexGrow=0;
            names.style.borderTopLeftRadius=0;
            names.style.borderTopRightRadius=0;
            names.style.borderBottomLeftRadius=0;
            names.style.borderBottomRightRadius=0;
            var rulerGutter=Txt("",10);
            rulerGutter.style.height=25;
            rulerGutter.style.minHeight=25;
            rulerGutter.style.maxHeight=25;
            rulerGutter.style.flexShrink=0;
            names.Add(rulerGutter);
            // Exact DrumKit.GetClipByDrumId() order: 1..8. No Tom/FX lanes.
            string[] drums={"KICK","SNARE","CLAP","CLOSED HAT","OPEN HAT","RIDE","RIM","CRASH"};
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
            }
            gridScrollContainer.Add(names);
            var grid=new VisualElement();
            canvas=grid;
            grid.style.position=Position.Relative;
            grid.style.flexShrink=0;
            grid.style.width=steps*StepW;
            grid.style.height=25+EditorHeight;
            grid.style.minHeight=25+EditorHeight;
            grid.style.backgroundColor=new Color(.055f,.075f,.112f);
            gridScrollContainer.Add(grid);
            for (int beat=0;beat<steps;beat+=4)
            {
                var num=Txt(((beat/16)+1)+"."+((beat%16)/4+1),9,Muted);
                num.style.position=Position.Absolute;
                num.style.left=beat*StepW+3;
                num.style.top=3;
                grid.Add(num);
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
            }
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
                    DrawEvent(plane,n.startTick,n.durationTicks,row,i,selectedNote==i);
                }
            }
            else if(content.kind==ClipKind.Drums && content.drums != null)
                foreach(var hit in content.drums)
                    if(hit != null && hit.lane >=0 && hit.lane<RowCount)
                        DrawEvent(plane,hit.startTick,quantum,hit.lane,-1,false);
            else if(content.kind==ClipKind.Chops && content.chops != null)
                foreach(var chop in content.chops)
                    if(chop != null && chop.sliceId>=1 && chop.sliceId<=RowCount)
                        DrawEvent(plane,chop.startTick,quantum,chop.sliceId-1,-1,false);
            editorPlayhead = new VisualElement { name = "ZPunkEditorPlayhead" };
            editorPlayhead.style.position = Position.Absolute;
            editorPlayhead.style.top = 0;
            editorPlayhead.style.width = 2;
            editorPlayhead.style.height = EditorHeight;
            editorPlayhead.style.backgroundColor = Accent;
            editorPlayhead.pickingMode = PickingMode.Ignore;
            editorPlayhead.style.display = DisplayStyle.None;
            plane.Add(editorPlayhead);
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
                        selectedNote = index;
                        var n = content.notes[index];
                        float right = (float)(n.startTick + n.durationTicks) / quantum * StepW;
                        drag = new Drag { index=index, start=p, startTick=n.startTick, duration=n.durationTicks,
                            pitch=n.pitch, resizing=(right-p.x)<10f, pointerId=evt.pointerId };
                        plane.CapturePointer(evt.pointerId);
                        if (selectedInfo != null) selectedInfo.text = n.velocity.ToString();
                        evt.StopPropagation();
                        return;
                    }
                }
                // A finger may be scrolling the piano roll. Defer creating a new
                // note/step until pointer up, and never create one during a swipe.
                pendingTap = new Tap { pointerId=evt.pointerId, start=evt.position, row=row, step=step };
            });
            plane.RegisterCallback<PointerMoveEvent>(evt=>
            {
                if (pendingTap != null && pendingTap.pointerId == evt.pointerId &&
                    ((Vector2)evt.position - pendingTap.start).sqrMagnitude > 49f)
                    pendingTap = null;
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
                    if (((Vector2)evt.position - tap.start).sqrMagnitude <= 49f)
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
            plane.RegisterCallback<PointerCancelEvent>(_=>{ drag=null; pendingTap=null; });
            if (scroller != null)
                scroller.schedule.Execute(() =>
                {
                    if (scroller != null) scroller.scrollOffset = previousOffset;
                }).ExecuteLater(0);
        }
        private void Update()
        {
            if (!ready || editorPlayhead == null || clip == null || content == null ||
                songTransport == null || root == null ||
                root.resolvedStyle.display == DisplayStyle.None) return;
            if (!songTransport.IsPlaying)
            {
                editorPlayhead.style.display = DisplayStyle.None;
                return;
            }
            long relative = songTransport.CurrentTick - clip.startTick;
            if (relative < 0 || relative >= clip.lengthTicks)
            {
                editorPlayhead.style.display = DisplayStyle.None;
                return;
            }
            if (clip.loopEnabled && content.lengthTicks > 0)
                relative = (relative + clip.sourceOffsetTick) % content.lengthTicks;
            else relative += clip.sourceOffsetTick;
            editorPlayhead.style.display = DisplayStyle.Flex;
            editorPlayhead.style.left = (float)relative / quantum * StepW;
        }

        private void DrawEvent(VisualElement plane,long tick,long duration,int row,int index,bool selected)
        {
            float x=(float)tick/quantum*StepW;
            if(x>=steps*StepW) return;
            float w=Mathf.Max(13,(float)duration/quantum*StepW-2);
            var e=Box(selected?Accent:colors[Mathf.Min(trackIndex,4)],3);
            e.style.position=Position.Absolute;
            e.style.left=x+1;
            e.style.top=row*RowH+4;
            e.style.width=w;
            e.style.height=RowH-7;
            e.pickingMode=PickingMode.Ignore; // Events handled by one canvas for touch and mouse.
            if(IsMidi && w>20)
            {
                var grip=Box(new Color(.08f,.18f,.21f,.55f),1);
                grip.style.position=Position.Absolute;
                grip.style.right=2;
                grip.style.top=2;
                grip.style.width=3;
                grip.style.height=RowH-11;
                grip.pickingMode=PickingMode.Ignore;
                e.Add(grip);
            }
            plane.Add(e);
        }
        private static string NoteName(int pitch)
        {
            string[] names={"C","C#","D","D#","E","F","F#","G","G#","A","A#","B"};
            return names[pitch%12]+(pitch/12-1);
        }
    }
}
