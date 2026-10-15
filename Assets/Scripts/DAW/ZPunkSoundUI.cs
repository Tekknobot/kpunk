using System;
using System.Collections.Generic;
using KMusic;
using KMusic.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace ZPunk.DAW
{
    /// <summary>
    /// Curated preset-shaping macros. Uses the SAME ParameterBus as the legacy synth
    /// (not a duplicate set of fake controls), with a purpose-built touch slider.
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
        private VisualElement root, modules, patchRow;
        private Label subhead;
        private KMusicApp app;
        private ParameterBus bus;
        private readonly List<Macro> macros=new List<Macro>();
        private bool listening;
        private sealed class Macro
        {
            public string id;
            public VisualElement fill, thumb;
            public Label value;
            public Parameter param;
        }
        private void OnDisable()
        {
            if(listening && bus != null) bus.OnChanged-=OnParameterChanged;
            listening=false;
        }
        private static Label T(string title,int size,Color color)
        {
            var t=new Label(title);
            t.style.fontSize=size;
            t.style.color=color;
            t.style.unityTextAlign=TextAnchor.MiddleLeft;
            return t;
        }
        private static void Rounded(VisualElement el,float radius)
        {
            el.style.borderTopLeftRadius=radius;
            el.style.borderTopRightRadius=radius;
            el.style.borderBottomLeftRadius=radius;
            el.style.borderBottomRightRadius=radius;
        }
        public void Initialize(VisualElement target,VisualElement existingUITree)
        {
            if(target==null) return;
            root=target;
            root.Clear();
            root.style.paddingLeft=11;
            root.style.paddingRight=11;
            root.style.paddingTop=10;
            root.style.backgroundColor=Bg;
            root.style.flexDirection=FlexDirection.Column;
            root.style.flexGrow=1;
            root.style.minHeight=0;
            var heading=T("SOUND  /  INSTRUMENT",16,Ink);
            heading.style.unityFontStyleAndWeight=FontStyle.Bold;
            heading.style.marginBottom=4;
            root.Add(heading);
            subhead=T("PRESET SHAPER  /  AUDIO HELM",10,Dim);
            subhead.style.marginBottom=12;
            root.Add(subhead);
            patchRow=new VisualElement();
            patchRow.style.flexDirection=FlexDirection.Row;
            patchRow.style.alignItems=Align.Center;
            patchRow.style.paddingLeft=8;
            patchRow.style.paddingRight=8;
            patchRow.style.height=56;
            patchRow.style.backgroundColor=Tile;
            Rounded(patchRow,9);
            patchRow.style.marginBottom=10;
            // Reparent live preset widgets to keep the EXISTING callbacks, not proxies.
            var prev=existingUITree?.Q<Button>("PatchPrevButton");
            var next=existingUITree?.Q<Button>("PatchNextButton");
            var name=existingUITree?.Q<Label>("PatchNameLabel");
            if(prev!=null) { prev.RemoveFromHierarchy(); StylePatchButton(prev);prev.text="<";patchRow.Add(prev); }
            if(name!=null)
            {
                name.RemoveFromHierarchy();
                name.style.flexGrow=1;
                name.style.minWidth=0;
                name.style.fontSize=13;
                name.style.color=Ink;
                name.style.unityTextAlign=TextAnchor.MiddleCenter;
                patchRow.Add(name);
            }
            else
            {
                var label=T("AUDIO HELM PATCH",12,Ink);
                label.style.flexGrow=1;
                patchRow.Add(label);
            }
            if(next!=null) {next.RemoveFromHierarchy();StylePatchButton(next);next.text=">";patchRow.Add(next);}
            root.Add(patchRow);
            var scrolling=new ScrollView(ScrollViewMode.Vertical);
            scrolling.style.flexGrow=1;
            scrolling.style.minHeight=0;
            scrolling.verticalScrollerVisibility=ScrollerVisibility.Hidden;
            scrolling.horizontalScrollerVisibility=ScrollerVisibility.Hidden;
            modules=scrolling.contentContainer;
            root.Add(scrolling);
            BuildModule("FILTER",Gold,new []{
                new[]{"CUTOFF","filter.cutoff"}, new[]{"RESONANCE","filter.res"},
                new[]{"DRIVE","filter.drive"}
            });
            BuildModule("ENVELOPE",Blue,new []{
                new[]{"ATTACK","amp.atk"}, new[]{"DECAY","amp.dec"},
                new[]{"SUSTAIN","amp.sus"}, new[]{"RELEASE","amp.rel2"}
            });
            BuildModule("TONE",Pink,new []{
                new[]{"OSC 1 LEVEL","osc1.vol"}, new[]{"OSC 2 LEVEL","osc2.vol"},
                new[]{"SUB BASS","sub.vol"},new[]{"GLIDE","master.porta"}
            });
            BuildModule("MOVEMENT",Aqua,new []{
                new[]{"MOD RATE","mod.rate"},new[]{"MOD DEPTH","mod.depth"},
                new[]{"DISTORTION","fx.dist.drive"},new[]{"MASTER","master.vol"}
            });
            var foot=T("These controls shape the loaded synth preset. Full legacy parameters remain in the project, but are not required for everyday sound design.",10,Dim);
            foot.style.whiteSpace=WhiteSpace.Normal;
            foot.style.marginTop=6;
            foot.style.marginBottom=12;
            modules.Add(foot);
        }
        private static void StylePatchButton(Button b)
        {
            b.style.width=39;
            b.style.minWidth=39;
            b.style.height=38;
            b.style.backgroundColor=new Color(.16f,.22f,.31f);
            b.style.color=Ink;
            b.style.fontSize=17;
            b.style.marginRight=5;
            Rounded(b,6);
        }
        private void BuildModule(string title,Color accent,string[][] values)
        {
            var panel=new VisualElement();
            panel.style.backgroundColor=Tile;
            Rounded(panel,10);
            panel.style.paddingLeft=11;
            panel.style.paddingRight=11;
            panel.style.paddingTop=11;
            panel.style.paddingBottom=8;
            panel.style.marginBottom=9;
            var titleLine=new VisualElement();
            titleLine.style.flexDirection=FlexDirection.Row;
            titleLine.style.alignItems=Align.Center;
            titleLine.style.marginBottom=7;
            var mark=new VisualElement();
            mark.style.width=4;
            mark.style.height=15;
            mark.style.backgroundColor=accent;
            Rounded(mark,3);
            mark.style.marginRight=8;
            titleLine.Add(mark);
            var txt=T(title,12,accent);
            txt.style.unityFontStyleAndWeight=FontStyle.Bold;
            titleLine.Add(txt);
            panel.Add(titleLine);
            foreach(var v in values)
            {
                var id=v[1];
                var row=new VisualElement();
                row.style.flexDirection=FlexDirection.Row;
                row.style.alignItems=Align.Center;
                row.style.height=46;
                var label=T(v[0],10,Dim);
                label.style.width=104;
                label.style.flexShrink=0;
                row.Add(label);
                var rail=new VisualElement();
                rail.style.height=32;
                rail.style.flexGrow=1;
                rail.style.position=Position.Relative;
                rail.style.justifyContent=Justify.Center;
                var bar=new VisualElement();
                bar.style.height=7;
                bar.style.width=new Length(100,LengthUnit.Percent);
                bar.style.backgroundColor=new Color(.045f,.071f,.108f);
                Rounded(bar,6);
                rail.Add(bar);
                var fill=new VisualElement();
                fill.style.position=Position.Absolute;
                fill.style.left=0;
                fill.style.top=12;
                fill.style.height=7;
                fill.style.width=new Length(40,LengthUnit.Percent);
                fill.style.backgroundColor=accent;
                Rounded(fill,5);
                rail.Add(fill);
                var thumb=new VisualElement();
                thumb.style.position=Position.Absolute;
                thumb.style.width=14;
                thumb.style.height=20;
                thumb.style.top=5;
                thumb.style.left=new Length(40,LengthUnit.Percent);
                thumb.style.backgroundColor=Ink;
                Rounded(thumb,6);
                thumb.pickingMode=PickingMode.Ignore;
                rail.Add(thumb);
                row.Add(rail);
                var readout=T("--",10,Ink);
                readout.style.width=48;
                readout.style.unityTextAlign=TextAnchor.MiddleRight;
                row.Add(readout);
                var macro=new Macro{id=id,fill=fill,thumb=thumb,value=readout};
                macros.Add(macro);
                Action<Vector2> setPos=(pos)=>
                {
                    if(bus==null) return;
                    float width=Mathf.Max(1f,rail.contentRect.width);
                    float t=Mathf.Clamp01(rail.WorldToLocal(pos).x/width);
                    bus.SetNormalized(id,t);
                    UpdateMacro(macro);
                };
                rail.RegisterCallback<PointerDownEvent>(evt=>
                {
                    if(evt.button!=0)return;
                    rail.CapturePointer(evt.pointerId);
                    setPos(evt.position);
                    evt.StopPropagation();
                });
                rail.RegisterCallback<PointerMoveEvent>(evt=>
                {
                    if(!rail.HasPointerCapture(evt.pointerId))return;
                    setPos(evt.position);
                    evt.StopPropagation();
                });
                rail.RegisterCallback<PointerUpEvent>(evt=>
                {
                    if(!rail.HasPointerCapture(evt.pointerId))return;
                    setPos(evt.position);
                    rail.ReleasePointer(evt.pointerId);
                    evt.StopPropagation();
                });
                rail.RegisterCallback<PointerCancelEvent>(evt=>
                {
                    if(rail.HasPointerCapture(evt.pointerId)) rail.ReleasePointer(evt.pointerId);
                });
                panel.Add(row);
            }
            modules.Add(panel);
        }
        public void OnShown()
        {
            if(app==null) app=FindObjectOfType<KMusicApp>();
            var available=app!=null?app.Bus:null;
            if(!ReferenceEquals(bus,available))
            {
                if(bus!=null && listening) bus.OnChanged-=OnParameterChanged;
                bus=available;
                listening=false;
            }
            if(bus!=null && !listening){bus.OnChanged+=OnParameterChanged;listening=true;}
            foreach(var m in macros)
            {
                m.param=null;
                if(bus!=null) bus.TryGet(m.id,out m.param);
                UpdateMacro(m);
            }
            if(subhead!=null) subhead.text=bus==null?"WAITING FOR AUDIO HELM":
                "PRESET SHAPER  /  LIVE SYNTH PARAMETERS";
        }
        private void OnParameterChanged(string id,float value)
        {
            foreach(var macro in macros) if(macro.id==id) UpdateMacro(macro);
        }
        private static void UpdateMacro(Macro m)
        {
            if(m.param==null){m.value.text="N/A";m.fill.style.width=0;m.thumb.style.left=0;return;}
            float t=Mathf.Clamp01(m.param.Normalized);
            m.value.text=m.param.Format();
            m.fill.style.width=new Length(100f*t,LengthUnit.Percent);
            m.thumb.style.left=new Length(100f*t,LengthUnit.Percent);
        }
    }
}
