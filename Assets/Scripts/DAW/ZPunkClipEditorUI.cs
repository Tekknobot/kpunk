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
        private const int MidiRows = 24;
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
        public Action OnClipChanged;

        public void Initialize(VisualElement host)
        {
            if (host == null) return;
            root = host;
            projects = FindObjectOfType<KMusicProjectManager>();
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
            b.style.height = 37;
            b.style.marginRight = 5;
            b.style.marginBottom = 3;
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
            heading.Add(title);
            heading.Add(Btn("+ NEW CLIP", NewClip, 98));
            root.Add(heading);

            var tracks = new ScrollView(ScrollViewMode.Horizontal);
            tracks.style.height = 47;
            tracks.style.marginTop = 6;
            tracks.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            tracks.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            chips = tracks.contentContainer;
            chips.style.flexDirection = FlexDirection.Row;
            root.Add(tracks);

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginTop = 5;
            row.Add(Btn("OCT -", () => { octave = Mathf.Max(0,octave-1); Redraw(); }, 56));
            row.Add(Btn("OCT +", () => { octave = Mathf.Min(7,octave+1); Redraw(); }, 56));
            row.Add(Btn("LEN -", () => ChangeLength(-1), 56));
            row.Add(Btn("LEN +", () => ChangeLength(1), 56));
            row.Add(Btn("NOTE DEL", DeleteSelectedNote, 77));
            row.Add(Btn("SAVE", Save, 53));
            var controls = new ScrollView(ScrollViewMode.Horizontal);
            controls.style.height = 43;
            controls.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            controls.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            controls.Add(row);
            root.Add(controls);

            info = Txt("Choose a track, then create or select a clip.",11,Muted);
            info.style.height = 28;
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
        }

        public void OnShown(int requestedTrack=-1, string requestedClip=null)
        {
            if (!ready) return;
            if (projects == null) projects = FindObjectOfType<KMusicProjectManager>();
            if (projects == null) return;
            song = projects.GetSongForArrangementEditor();
            if (song == null || song.tracks == null || song.tracks.Count == 0) return;
            if (requestedTrack >= 0 && requestedTrack < song.tracks.Count) trackIndex = requestedTrack;
            trackIndex = Mathf.Clamp(trackIndex,0,song.tracks.Count-1);
            if (!string.IsNullOrEmpty(requestedClip)) clipId = requestedClip;
            LoadClip();
            Redraw();
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
            // Song transport intentionally snapshots notes on Play. Stop and Play to hear edits.
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
            if (info != null) info.text = "SAVED  /  Restart Play to hear changes";
        }
        private void SelectTrack(int index)
        {
            trackIndex = index;
            clipId = null;
            LoadClip();
            Redraw();
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
        private int RowCount => IsMidi ? MidiRows : content != null && content.kind == ClipKind.Chops ? 8 : 8;
        private int TopPitch => Mathf.Min(127, (octave+2)*12 - 1);
        private int PitchForRow(int row) => Mathf.Clamp(TopPitch-row,0,127);
        private int RowForPitch(int pitch) => TopPitch-pitch;
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
            if (title != null) title.text = song.tracks[trackIndex].name.ToUpperInvariant()+"  /  " + (clip != null ? clip.name : "NO CLIP");
            if (info != null) info.text= clip == null ? "NO CLIP  /  Tap + NEW CLIP to begin" :
                (IsMidi ? "PIANO ROLL" : content != null && content.kind==ClipKind.Drums ? "DRUM STEP EDITOR" : "SAMPLE STEP EDITOR")+
                "  /  Bar " + (clip.startTick / SongData.TicksPerBar4 + 1) + "  /  edits play after restart";
            gridScrollContainer.Clear();
            if (clip==null || content==null) return;
            long editLength=Math.Max(clip.lengthTicks,content.lengthTicks);
            steps=Mathf.Clamp(Mathf.CeilToInt((float)editLength/quantum),16,128);
            var names=Box(new Color(.095f,.13f,.18f));
            names.style.width=50;
            names.style.flexShrink=0;
            var rulerGutter=Txt("",10);
            rulerGutter.style.height=25;
            names.Add(rulerGutter);
            string[] drums={"KICK","SNARE","HAT","CLAP","PERC","TOM","RIM","FX"};
            for(int row=0;row<RowCount;row++)
            {
                string name=IsMidi? NoteName(PitchForRow(row)) : content.kind==ClipKind.Drums ? drums[row] : "S"+(row+1);
                var label=Txt(name,9,IsMidi && PitchForRow(row)%12==0?Accent:Muted);
                label.style.height=RowH;
                label.style.paddingLeft=3;
                names.Add(label);
            }
            gridScrollContainer.Add(names);
            var grid=new VisualElement();
            canvas=grid;
            grid.style.position=Position.Relative;
            grid.style.flexShrink=0;
            grid.style.width=steps*StepW;
            grid.style.height=25+EditorHeight;
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
            plane.RegisterCallback<PointerDownEvent>(evt=>
            {
                if(evt.button != 0) return;
                Vector2 p=plane.WorldToLocal(evt.position);
                int row=RowAt(p.y), step=QuantizedStep(p.x);
                if(IsMidi)
                {
                    int index=FindNote(PitchForRow(row),step*quantum);
                    if(index>=0)
                    {
                        selectedNote=index;
                        var n=content.notes[index];
                        float right=(float)(n.startTick+n.durationTicks)/quantum*StepW;
                        drag=new Drag { index=index,start=p,startTick=n.startTick,duration=n.durationTicks,
                            pitch=n.pitch,resizing=(right-p.x)<10f,pointerId=evt.pointerId };
                        plane.CapturePointer(evt.pointerId);
                        if(selectedInfo!=null) selectedInfo.text=n.velocity.ToString();
                    }
                    else ToggleGrid(row,step);
                }
                else ToggleGrid(row,step);
                evt.StopPropagation();
            });
            plane.RegisterCallback<PointerMoveEvent>(evt=>
            {
                if(drag==null || drag.pointerId!=evt.pointerId) return;
                Vector2 p=plane.WorldToLocal(evt.position);
                if((p-drag.start).sqrMagnitude>16) drag.moved=true;
                evt.StopPropagation();
            });
            plane.RegisterCallback<PointerUpEvent>(evt=>
            {
                if(drag==null || drag.pointerId!=evt.pointerId) return;
                var d=drag; drag=null;
                if(plane.HasPointerCapture(evt.pointerId)) plane.ReleasePointer(evt.pointerId);
                if(d.moved && Writable() && d.index>=0 && d.index<content.notes.Count)
                {
                    Vector2 p=plane.WorldToLocal(evt.position);
                    int tickChange=Mathf.RoundToInt((p.x-d.start.x)/StepW);
                    var n=content.notes[d.index];
                    if(d.resizing) n.durationTicks=Math.Max(quantum,d.duration+tickChange*quantum);
                    else
                    {
                        n.startTick=Math.Max(0,d.startTick+tickChange*quantum);
                        n.pitch=Mathf.Clamp(d.pitch-Mathf.RoundToInt((p.y-d.start.y)/RowH),0,127);
                    }
                    content.lengthTicks=Math.Max(content.lengthTicks,n.startTick+n.durationTicks);
                }
                Changed();
                evt.StopPropagation();
            });
            plane.RegisterCallback<PointerCancelEvent>(_=>{ drag=null; });
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
