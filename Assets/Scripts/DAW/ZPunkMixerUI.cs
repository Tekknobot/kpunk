using System;
using UnityEngine;
using UnityEngine.UIElements;
using KMusic;

namespace ZPunk.DAW
{
    /// <summary>DAW mixer with live track gains and per-drum AudioMixer/ParameterBus faders.</summary>
    public sealed class ZPunkMixerUI : MonoBehaviour
    {
        private static readonly Color Bg = new Color(.073f,.101f,.146f);
        private static readonly Color Card = new Color(.108f,.147f,.204f);
        private static readonly Color Ink = new Color(.91f,.94f,.98f);
        private static readonly Color Dim = new Color(.58f,.67f,.76f);
        private static readonly Color Aqua = new Color(.30f,.87f,.75f);
        private VisualElement root, lanes;
        private KMusicProjectManager projects;
        private KMusicApp app;
        private KMusicDrumSequencer drums;
        private ZPunkSongTransport transport;
        private int displayedSlot = -1;
        private static readonly string[] DrumNames =
        {
            "KICK",
            "SNARE",
            "CLAP",
            "CLOSED HAT",
            "OPEN HAT",
            "RIDE",
            "RIM",
            "CRASH"
        };        
        private static readonly Color[] TrackColors = {
            new Color(.98f,.66f,.28f),new Color(.96f,.36f,.48f),new Color(.35f,.60f,.98f),
            new Color(.66f,.45f,.94f),new Color(.24f,.82f,.69f)
        };

        public void Initialize(VisualElement host)
        {
            root = host;
            if (root == null) return;
            root.Clear();
            root.style.flexDirection = FlexDirection.Column;
            root.style.flexGrow = 1;
            root.style.minHeight = 0;
            root.style.paddingLeft = 10;
            root.style.paddingRight = 10;
            root.style.paddingTop = 9;
            root.style.backgroundColor = Bg;
            var heading = Text("MIXER  /  LEVELS",16,Ink);
            heading.style.unityFontStyleAndWeight = FontStyle.Bold;
            heading.style.marginBottom = 3;
            root.Add(heading);
            var sub = Text("LIVE AUDIO GAIN  /  HELD NOTES RESPOND",10,Dim);
            sub.style.marginBottom = 10;
            root.Add(sub);
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            scroll.style.minHeight = 0;
            scroll.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            lanes = scroll.contentContainer;
            root.Add(scroll);
            OnShown();
        }
        private void Resolve()
        {
            if (projects == null) projects = FindObjectOfType<KMusicProjectManager>();
            if (app == null) app = FindObjectOfType<KMusicApp>();
            if (drums == null) drums = FindObjectOfType<KMusicDrumSequencer>();
            if (transport == null) transport = FindObjectOfType<ZPunkSongTransport>();
        }
        private static Label Text(string s,int size,Color color)
        {
            var label = new Label(s);
            label.style.color = color;
            label.style.fontSize = size;
            label.style.unityTextAlign = TextAnchor.MiddleLeft;
            return label;
        }
        private static VisualElement Panel()
        {
            var element = new VisualElement();
            element.style.backgroundColor = Card;
            element.style.paddingLeft = 10;
            element.style.paddingRight = 10;
            element.style.paddingTop = 9;
            element.style.paddingBottom = 9;
            element.style.marginBottom = 7;
            element.style.borderTopLeftRadius = 5;
            element.style.borderTopRightRadius = 5;
            element.style.borderBottomLeftRadius = 5;
            element.style.borderBottomRightRadius = 5;
            return element;
        }
        private static Button Action(string label,Action action)
        {
            var button = new Button(action) { text = label };
            button.style.height = 31;
            button.style.minWidth = 39;
            button.style.flexShrink = 0;
            button.style.color = Ink;
            button.style.backgroundColor = new Color(.16f,.22f,.30f);
            button.style.marginLeft = 5;
            button.style.fontSize = 10;
            return button;
        }
        private static void AddFader(VisualElement parent,float value,Color color,Action<float> onChange)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.height = 42;
            parent.Add(row);
            var rail = new VisualElement();
            rail.style.flexGrow = 1;
            rail.style.height = 35;
            rail.style.position = Position.Relative;
            rail.style.justifyContent = Justify.Center;
            var bg = new VisualElement();
            bg.style.height = 7;
            bg.style.backgroundColor = new Color(.055f,.077f,.105f);
            bg.pickingMode = PickingMode.Ignore;
            rail.Add(bg);
            var fill = new VisualElement();
            fill.style.position = Position.Absolute;
            fill.style.left = 0;
            fill.style.top = 14;
            fill.style.height = 7;
            fill.style.backgroundColor = color;
            fill.pickingMode = PickingMode.Ignore;
            rail.Add(fill);
            var thumb = new VisualElement();
            thumb.style.position = Position.Absolute;
            thumb.style.top = 7;
            thumb.style.width = 12;
            thumb.style.height = 21;
            thumb.style.backgroundColor = Ink;
            thumb.pickingMode = PickingMode.Ignore;
            rail.Add(thumb);
            var percent = Text("",10,Ink);
            percent.style.width = 45;
            percent.style.marginLeft = 10;
            percent.style.unityTextAlign = TextAnchor.MiddleRight;
            Action<float> show = amount =>
            {
                amount = Mathf.Clamp01(amount);
                fill.style.width = new Length(amount*100,LengthUnit.Percent);
                thumb.style.left = new Length(amount*100,LengthUnit.Percent);
                percent.text = Mathf.RoundToInt(amount*100) + "%";
            };
            show(value);
            Action<Vector2> set = pos =>
            {
                float next = Mathf.Clamp01(rail.WorldToLocal(pos).x / Mathf.Max(1,rail.contentRect.width));
                show(next);
                onChange(next);
            };
            rail.RegisterCallback<PointerDownEvent>(evt => {
                if (evt.button != 0) return;
                rail.CapturePointer(evt.pointerId);
                set(evt.position);evt.StopPropagation();
            });
            rail.RegisterCallback<PointerMoveEvent>(evt => {
                if (!rail.HasPointerCapture(evt.pointerId)) return;
                set(evt.position);evt.StopPropagation();
            });
            rail.RegisterCallback<PointerUpEvent>(evt => {
                if (!rail.HasPointerCapture(evt.pointerId)) return;
                set(evt.position);
                rail.ReleasePointer(evt.pointerId);evt.StopPropagation();
            });
            row.Add(rail);
            row.Add(percent);
        }
        private void ChangeTrack(int index,Action<TrackData> edit,bool cutSustains = false)
        {
            Resolve();
            if (projects == null) return;
            var song = projects.GetSongForArrangementEditor();
            if (song == null || song.tracks == null || index < 0 || index >= song.tracks.Count) return;
            if (!ReferenceEquals(projects.CurrentSong,song)) song = ArrangementEditing.IndependentCopy(song);
            if (song == null || song.tracks[index] == null) return;
            edit(song.tracks[index]);
            projects.AcceptArrangementEdit(song);
            if (transport != null) transport.ApplyLiveMix(song,cutSustains);
        }
        public void OnShown()
        {
            if (root == null || lanes == null) return;
            Resolve();
            lanes.Clear();
            var song = projects != null ? projects.GetSongForArrangementEditor() : null;
            if (song != null && song.tracks != null)
            {
                lanes.Add(Text("TRACKS",12,Aqua));
                for (int i=0; i<song.tracks.Count; ++i)
                {
                    int index = i;
                    TrackData track = song.tracks[i];
                    if (track == null) continue;
                    var panel = Panel();
                    var top = new VisualElement();
                    top.style.flexDirection = FlexDirection.Row;
                    top.style.alignItems = Align.Center;
                    panel.Add(top);
                    var name = Text(track.name.ToUpperInvariant(),12,TrackColors[Mathf.Min(i,4)]);
                    name.style.flexGrow = 1;
                    name.style.unityFontStyleAndWeight = FontStyle.Bold;
                    top.Add(name);
                    Button mute=null, solo=null;
                    mute=Action(track.mute?"MUTE ON":"M",()=>{
                        ChangeTrack(index,t=>t.mute=!t.mute);
                        var t=projects.CurrentSong.tracks[index];
                        mute.text=t.mute?"MUTE ON":"M";
                    });
                    solo=Action(track.solo?"SOLO ON":"S",()=>{
                        ChangeTrack(index,t=>t.solo=!t.solo);
                        var t=projects.CurrentSong.tracks[index];
                        solo.text=t.solo?"SOLO ON":"S";
                    });
                    top.Add(mute);top.Add(solo);
                    AddFader(panel,track.volume,TrackColors[Mathf.Min(i,4)],v=>ChangeTrack(index,t=>t.volume=v));
                    lanes.Add(panel);
                }
            }
            lanes.Add(Text("DRUM CHANNELS  /  KIT 1-8",12,Aqua));
            if (app == null || app.Bus == null || drums == null)
            {
                lanes.Add(Text("Waiting for drum mixer and parameter bus.",11,Dim));
                return;
            }
            bool[] mutes = drums.CaptureDrumMutes();
            for (int i=0;i<8;++i)
            {
                int lane=i;
                string id="drum.vol"+(i+1).ToString("00");
                var panel=Panel();
                var top=new VisualElement();
                top.style.flexDirection=FlexDirection.Row;
                top.style.alignItems=Align.Center;
                var name=Text(DrumNames[i],11,new Color(.98f,.68f,.31f));
                name.style.flexGrow=1;
                top.Add(name);
                bool muted=i<mutes.Length&&mutes[i];
                Button m=null;
                m=Action(muted?"MUTED":"M",()=>{
                    bool[] state=drums.CaptureDrumMutes();
                    bool target=!(lane<state.Length&&state[lane]);
                    drums.SetDAWDrumMute(lane,target);
                    m.text=target?"MUTED":"M";
                });
                top.Add(m);
                panel.Add(top);
                float value = app.Bus.GetNormalized(id);
                AddFader(panel,value,new Color(.98f,.68f,.31f),v=>{
                    if (app != null && app.Bus != null) app.Bus.SetNormalized(id,v);
                });
                lanes.Add(panel);
            }
            lanes.Add(Text("Levels update immediately. Song notes can be edited while playing.",10,Dim));
            displayedSlot = projects != null ? projects.CurrentSlot : -1;
        }
        private void Update()
        {
            if (root == null || root.resolvedStyle.display == DisplayStyle.None || projects == null) return;
            if (projects.CurrentSlot != displayedSlot) OnShown();
        }
    }
}
