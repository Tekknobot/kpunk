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
    /// Three independent Audio Helm instrument surfaces: Bass, Synth, Pads.
    /// Preset change routes to the actual controller for that instrument;
    /// macros write normalized Helm parameters, not decorative UI state.
    /// </summary>
    public sealed class ZPunkSoundUI : MonoBehaviour
    {
        private static readonly Color Bg=new Color(.073f,.101f,.146f);
        private static readonly Color Tile=new Color(.105f,.145f,.207f);
        private static readonly Color Ink=new Color(.91f,.94f,.98f);
        private static readonly Color Dim=new Color(.57f,.66f,.76f);
        private static readonly Color Aqua=new Color(.30f,.87f,.75f);
        private static readonly Color Gold=new Color(.99f,.72f,.35f);
        private static readonly Color Blue=new Color(.43f,.68f,.99f);
        private static readonly Color Pink=new Color(.97f,.48f,.61f);
        private VisualElement root, modules, trackBar;
        private Label subhead, presetName, instrumentStatus;
        private readonly List<Macro> macros=new List<Macro>();
        private readonly Dictionary<TrackKind,Button> selectors=new Dictionary<TrackKind,Button>();
        private KMusicApp app;
        private KMusicPadSynthRuntime padRuntime;
        private KMusicHelmPresetPickerUI mainPicker;
        private ZPunkInstrumentRack rack;
        private TrackKind selected=TrackKind.Synth;
        private float nextRefresh;

        private sealed class Macro
        {
            public string id;
            public Param param;
            public VisualElement fill, thumb;
            public Label value;
            public int lastPercent = -1;
        }
        private static Label T(string value,int size,Color color)
        {
            var l=new Label(value);
            l.style.fontSize=Mathf.Max(12,size);l.style.color=color;
            l.style.unityTextAlign=TextAnchor.MiddleLeft;
            return l;
        }
        private static void Corners(VisualElement e,float r)
        {
            e.style.borderTopLeftRadius=r;e.style.borderTopRightRadius=r;
            e.style.borderBottomLeftRadius=r;e.style.borderBottomRightRadius=r;
        }
        private static Button Action(string name,Action click)
        {
            var b=new Button(click){text=name};
            b.style.height=46;
            b.style.minWidth=46;
            b.style.flexShrink=0;
            b.style.fontSize=14;
            b.style.color=Ink;
            b.style.backgroundColor=Tile;
            Corners(b,4);
            return b;
        }
        public void Initialize(VisualElement host,VisualElement ignoredLegacyTree)
        {
            root=host;
            if(root==null)return;
            root.Clear();macros.Clear();selectors.Clear();
            root.style.flexDirection=FlexDirection.Column;
            root.style.flexGrow=1;root.style.minHeight=0;
            root.style.paddingLeft=10;root.style.paddingRight=10;root.style.paddingTop=9;
            root.style.backgroundColor=Bg;
            var title=T("SOUND  /  INSTRUMENT",16,Ink);
            title.style.unityFontStyleAndWeight=FontStyle.Bold;
            title.style.marginBottom=5;
            root.Add(title);
            subhead=T("SELECT AN INSTRUMENT",10,Dim);
            subhead.style.marginBottom=8;
            root.Add(subhead);

            trackBar=new VisualElement();
            trackBar.style.flexDirection=FlexDirection.Row;
            trackBar.style.height=52;
            trackBar.style.flexShrink=0;
            trackBar.style.marginBottom=9;
            foreach(var kind in new[]{TrackKind.Bass,TrackKind.Synth,TrackKind.Pads})
            {
                TrackKind chosen=kind;
                var button=Action(kind.ToString().ToUpperInvariant(),()=>Select(chosen));
                button.style.flexGrow=1;
                button.style.flexShrink=1;
                button.style.minWidth=0;
                button.style.marginRight=5;
                selectors[kind]=button;
                trackBar.Add(button);
            }
            root.Add(trackBar);

            var presetCard=new VisualElement();
            presetCard.style.flexDirection=FlexDirection.Row;
            presetCard.style.alignItems=Align.Center;
            presetCard.style.height=61;
            presetCard.style.flexShrink=0;
            presetCard.style.paddingLeft=8;
            presetCard.style.paddingRight=8;
            presetCard.style.backgroundColor=Tile;
            Corners(presetCard,5);
            presetCard.style.marginBottom=8;
            var prev=Action("<",()=>ChangePreset(-1));
            prev.style.width=46;prev.style.marginRight=5;
            presetCard.Add(prev);
            presetName=T("LOADING PRESET",12,Ink);
            presetName.style.flexGrow=1;
            presetName.style.minWidth=0;
            presetName.style.unityTextAlign=TextAnchor.MiddleCenter;
            presetName.style.whiteSpace=WhiteSpace.Normal;
            presetCard.Add(presetName);
            var next=Action(">",()=>ChangePreset(1));
            next.style.width=46;next.style.marginLeft=5;
            presetCard.Add(next);
            root.Add(presetCard);

            instrumentStatus=T("",10,Dim);
            instrumentStatus.style.marginBottom=7;
            root.Add(instrumentStatus);
            var scroller=new ScrollView(ScrollViewMode.Vertical);
            scroller.style.flexGrow=1;
            scroller.style.minHeight=0;
            scroller.verticalScrollerVisibility=ScrollerVisibility.Hidden;
            scroller.horizontalScrollerVisibility=ScrollerVisibility.Hidden;
            modules=scroller.contentContainer;
            root.Add(scroller);
            BuildModule("FILTER",Gold,new[] {
                new Macro{id="filter.cutoff",param=Param.kFilterCutoff},
                new Macro{id="filter.res",param=Param.kResonance},
                new Macro{id="filter.drive",param=Param.kFilterDrive}
            });
            BuildModule("ENVELOPE",Blue,new[] {
                new Macro{id="amp.atk",param=Param.kAmplitudeAttack},
                new Macro{id="amp.dec",param=Param.kAmplitudeDecay},
                new Macro{id="amp.sus",param=Param.kAmplitudeSustain},
                new Macro{id="amp.rel2",param=Param.kAmplitudeRelease}
            });
            BuildModule("TONE",Pink,new[] {
                new Macro{id="osc1.vol",param=Param.kOsc1Volume},
                new Macro{id="osc2.vol",param=Param.kOsc2Volume},
                new Macro{id="sub.vol",param=Param.kSubVolume},
                new Macro{id="master.porta",param=Param.kPortamento}
            });
            BuildModule("MOVEMENT",Aqua,new[] {
                new Macro{id="mod.rate",param=Param.kMonoLfo1Frequency},
                new Macro{id="mod.depth",param=Param.kMonoLfo1Amplitude},
                new Macro{id="fx.dist.drive",param=Param.kDistortionDrive},
                new Macro{id="master.vol",param=Param.kVolume}
            });
            var note=T("Changes shape only the selected instrument. Audio Helm presets remain independent.",10,Dim);
            note.style.whiteSpace=WhiteSpace.Normal;
            note.style.marginBottom=12;
            modules.Add(note);
            OnShown();
        }
        private void FindServices()
        {
            if(app==null)app=FindObjectOfType<KMusicApp>();
            if(padRuntime==null)padRuntime=FindObjectOfType<KMusicPadSynthRuntime>();
            if(mainPicker==null)mainPicker=FindObjectOfType<KMusicHelmPresetPickerUI>();
            if(rack==null)rack=FindObjectOfType<ZPunkInstrumentRack>();
        }
        private HelmController Controller()
        {
            FindServices();
            if(rack!=null)return rack.Controller(selected);
            return selected==TrackKind.Pads?padRuntime?.ActiveHelmController:null;
        }
        private ParameterBus CurrentBus()
        {
            FindServices();
            return selected==TrackKind.Synth?app?.Bus:
                selected==TrackKind.Pads?padRuntime?.SoundBus:null;
        }
        private void Select(TrackKind track)
        {
            selected=track;
            OnShown();
        }
        public void OnShown(int requestedTrack=-1)
        {
            if(requestedTrack>=0 && requestedTrack<=4)
            {
                var kind=(TrackKind)requestedTrack;
                if(kind==TrackKind.Bass || kind==TrackKind.Synth || kind==TrackKind.Pads)
                    selected=kind;
            }
            if(root==null)return;
            FindServices();
            foreach(var item in selectors)
            {
                bool active=item.Key==selected;
                item.Value.style.backgroundColor=active?Aqua:Tile;
                item.Value.style.color=active?new Color(.04f,.09f,.12f):Ink;
            }
            if(subhead!=null)subhead.text=selected.ToString().ToUpperInvariant()+"  /  AUDIO HELM PRESET SHAPER";
            Refresh();
        }
        private void Refresh()
        {
            var controller=Controller();
            string state=controller==null?
                "INSTRUMENT INITIALIZING - RETRY IN A MOMENT":
                "CHANNEL "+controller.channel+"  /  INDEPENDENT SYNTH ENGINE";
            if(instrumentStatus!=null && instrumentStatus.text!=state)instrumentStatus.text=state;
            string name="NO PRESET";
            if(selected==TrackKind.Bass && rack!=null)name=rack.BassPresetName;
            else if(selected==TrackKind.Synth && mainPicker!=null)
                name=DisplayPath(mainPicker.CurrentPresetRelPath);
            else if(selected==TrackKind.Pads && padRuntime!=null)
                name=padRuntime.PresetCount > 0
                    ? DisplayPath(padRuntime.CurrentPresetRelPath) + "  (" + (padRuntime.CurrentPresetIndex + 1) + "/" + padRuntime.PresetCount + ")"
                    : "PAD PRESETS UNAVAILABLE";
            if(presetName!=null && presetName.text!=name)presetName.text=name;
            foreach(var m in macros)
            {
                float percent=0f;
                bool okay=false;
                var bus=CurrentBus();
                if(bus!=null)
                {
                    Parameter parm;
                    if(bus.TryGet(m.id,out parm) && parm!=null)
                    {
                        percent=Mathf.Clamp01(parm.Normalized);
                        okay=true;
                    }
                }
                if(!okay && controller!=null)
                {
                    try{percent=Mathf.Clamp01(controller.GetParameterPercent(m.param));okay=true;}
                    catch(Exception){}
                }
                int shown=okay?Mathf.RoundToInt(percent*100f):-1;
                if(shown==m.lastPercent)continue;
                m.lastPercent=shown;
                if(m.value!=null)m.value.text=shown>=0?shown+"%":"--";
                if(m.fill!=null)m.fill.style.width=new Length(okay?percent*100f:0f,LengthUnit.Percent);
                if(m.thumb!=null)m.thumb.style.left=new Length(okay?percent*100f:0f,LengthUnit.Percent);
            }
        }
        private static string DisplayPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return "INIT PRESET";

            string clean = path.Replace('\\', '/');
            int slash = clean.LastIndexOf('/');
            clean = clean.Substring(slash + 1);

            return clean.EndsWith(".helm", StringComparison.OrdinalIgnoreCase)
                ? clean.Substring(0, clean.Length - 5)
                : clean;
        }
        private void ChangePreset(int direction)
        {
            FindServices();
            if(selected==TrackKind.Bass)rack?.StepBassPreset(direction);
            else if(selected==TrackKind.Synth && mainPicker!=null)
                mainPicker.ApplyPresetChoice(mainPicker.CurrentPresetIndex+direction,null);
            else if(selected==TrackKind.Pads && padRuntime!=null)
                padRuntime.StepPreset(direction);
            // Async StreamingAssets preset load; Refresh() in Update picks up the new name.
            Refresh();
        }
        private void SetMacro(Macro m,float t)
        {
            t=Mathf.Clamp01(t);
            var bus=CurrentBus();
            var controller=Controller();
            bool done=false;
            if(bus!=null)
            {
                Parameter param;
                if(bus.TryGet(m.id,out param) && param!=null)
                {
                    bus.SetNormalized(m.id,t);
                    done=true;
                }
            }
            if(!done && controller!=null)
            {
                try{controller.SetParameterPercent(m.param,t);}catch(Exception){}
            }
            m.fill.style.width=new Length(t*100f,LengthUnit.Percent);
            m.thumb.style.left=new Length(t*100f,LengthUnit.Percent);
            m.value.text=(t*100f).ToString("0")+"%";
        }
        private void BuildModule(string heading,Color color,Macro[] controls)
        {
            var panel=new VisualElement();
            Corners(panel,6);
            panel.style.backgroundColor=Tile;
            panel.style.marginBottom=8;
            panel.style.paddingLeft=11;panel.style.paddingRight=11;
            panel.style.paddingTop=9;panel.style.paddingBottom=8;
            var cap=T(heading,12,color);
            cap.style.marginBottom=6;
            cap.style.unityFontStyleAndWeight=FontStyle.Bold;
            panel.Add(cap);
            foreach(var macro in controls)
            {
                var row=new VisualElement();
                row.style.flexDirection=FlexDirection.Row;
                row.style.alignItems=Align.Center;
                row.style.height=47;
                var label=T(macro.id.Substring(macro.id.IndexOf('.')+1).ToUpperInvariant(),10,Dim);
                label.style.width=95;
                label.style.flexShrink=0;
                row.Add(label);
                var rail=new VisualElement();
                rail.style.height=33;
                rail.style.position=Position.Relative;
                rail.style.flexGrow=1;
                rail.style.minWidth=0;
                rail.style.justifyContent=Justify.Center;
                var baseLine=new VisualElement();
                baseLine.style.height=6;
                baseLine.style.backgroundColor=new Color(.06f,.085f,.13f);
                Corners(baseLine,3);
                baseLine.pickingMode=PickingMode.Ignore;
                rail.Add(baseLine);
                macro.fill=new VisualElement();
                macro.fill.style.position=Position.Absolute;
                macro.fill.style.left=0;macro.fill.style.top=13;
                macro.fill.style.height=6;
                macro.fill.style.backgroundColor=color;
                Corners(macro.fill,3);
                macro.fill.pickingMode=PickingMode.Ignore;
                rail.Add(macro.fill);
                macro.thumb=new VisualElement();
                macro.thumb.style.position=Position.Absolute;
                macro.thumb.style.top=6;macro.thumb.style.width=12;macro.thumb.style.height=20;
                macro.thumb.style.backgroundColor=Ink;
                Corners(macro.thumb,4);
                macro.thumb.pickingMode=PickingMode.Ignore;
                rail.Add(macro.thumb);
                Action<Vector2> set=(pos)=>SetMacro(macro,rail.WorldToLocal(pos).x/Mathf.Max(1f,rail.contentRect.width));
                rail.RegisterCallback<PointerDownEvent>(evt=>
                {
                    if(evt.button!=0)return;
                    rail.CapturePointer(evt.pointerId);
                    set(evt.position);evt.StopPropagation();
                });
                rail.RegisterCallback<PointerMoveEvent>(evt=>
                {
                    if(!rail.HasPointerCapture(evt.pointerId))return;
                    set(evt.position);evt.StopPropagation();
                });
                rail.RegisterCallback<PointerUpEvent>(evt=>
                {
                    if(!rail.HasPointerCapture(evt.pointerId))return;
                    set(evt.position);rail.ReleasePointer(evt.pointerId);evt.StopPropagation();
                });
                row.Add(rail);
                macro.value=T("--",10,Ink);
                macro.value.style.width=48;
                macro.value.style.unityTextAlign=TextAnchor.MiddleRight;
                row.Add(macro.value);
                panel.Add(row);
                macros.Add(macro);
            }
            modules.Add(panel);
        }
        private void Update()
        {
            if(root==null || root.resolvedStyle.display==DisplayStyle.None)return;
            if(Time.unscaledTime<nextRefresh)return;
            nextRefresh=Time.unscaledTime+.25f;
            Refresh();
        }
    }
}
