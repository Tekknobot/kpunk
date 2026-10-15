using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UIElements;
using KMusic;
using ZPunk.DAW;

/// <summary>
/// ZPunk DAW workspace: Arrange / Create / Sound. The original UXML pages remain
/// in the tree for the existing playback, preset and project components, but are
/// not exposed in the interface. Small-screen layout uses a bottom navigation bar.
/// </summary>
public sealed class KMusicTabsUI : MonoBehaviour
{
    [SerializeField] private UIDocument doc;
    private VisualElement uiRoot, pages, shell, header, transportRow, menu, dock;
    private VisualElement pageArrangement, pageEditor, pageSound;
    private VisualElement context, content;
    private Label heading, subheading, projectInfo;
    private FloatField bpmField;
    private Button arrangeButton, createButton, soundButton, menuToggle;
    private ZPunkArrangementUI arrangement;
    private ZPunkClipEditorUI editor;
    private ZPunkSoundUI sound;
    private KMusicProjectManager projects;
    private string workspace="arrange";
    private bool isReady, compact;
    private float previousWidth=-1f;
    private int lastSlot=-1;
    private static readonly Color Ink=new Color(.91f,.95f,.98f);
    private static readonly Color Dim=new Color(.58f,.66f,.76f);
    private static readonly Color Accent=new Color(.30f,.88f,.74f);
    private static readonly Color NavBg=new Color(.080f,.115f,.165f);
    private static readonly Color Selected=new Color(.12f,.32f,.33f);

    private void Awake(){if(doc==null)doc=GetComponent<UIDocument>();}
    private void OnEnable()
    {
        // The old workspace created the pad Audio Helm controller dynamically.
        // Keep that runtime service even though its legacy UI is hidden.
        if(GetComponent<KMusicPadSynthRuntime>()==null)
            gameObject.AddComponent<KMusicPadSynthRuntime>();
        StartCoroutine(Setup());
    }
    private void OnDisable()
    {
        if(uiRoot!=null)uiRoot.UnregisterCallback<GeometryChangedEvent>(GeometryChanged);
        isReady=false;
    }
    private IEnumerator Setup()
    {
        if(doc==null)doc=GetComponent<UIDocument>();
        if(doc==null)yield break;
        while(doc.rootVisualElement==null || doc.rootVisualElement.Q<VisualElement>("Pages")==null)
            yield return null;
        if(isReady)yield break;
        uiRoot=doc.rootVisualElement.Q<VisualElement>("Root");
        if(uiRoot==null)yield break;
        projects=FindObjectOfType<KMusicProjectManager>();
        pages=uiRoot.Q<VisualElement>("Pages");
        pageArrangement=uiRoot.Q<VisualElement>("PageArrangement");
        if(pageArrangement==null)
        {
            pageArrangement=new VisualElement{name="PageArrangement"};
            pages.Insert(0,pageArrangement);
        }
        pageEditor=pages.Q<VisualElement>("ZPunkClipPage");
        if(pageEditor==null){pageEditor=new VisualElement{name="ZPunkClipPage"};pages.Add(pageEditor);}
        pageSound=pages.Q<VisualElement>("ZPunkSoundPage");
        if(pageSound==null){pageSound=new VisualElement{name="ZPunkSoundPage"};pages.Add(pageSound);}
        arrangement=GetComponent<ZPunkArrangementUI>();
        if(arrangement==null)arrangement=gameObject.AddComponent<ZPunkArrangementUI>();
        arrangement.Initialize(pageArrangement);
        editor=GetComponent<ZPunkClipEditorUI>();
        if(editor==null)editor=gameObject.AddComponent<ZPunkClipEditorUI>();
        editor.Initialize(pageEditor);
        sound=GetComponent<ZPunkSoundUI>();
        if(sound==null)sound=gameObject.AddComponent<ZPunkSoundUI>();
        sound.Initialize(pageSound,uiRoot);
        arrangement.EditRequested=(index,id)=>NavigateCreate(index,id);
        editor.OnClipChanged=()=>{};
        BuildShell();
        isReady=true;
        Navigate("arrange");
        uiRoot.RegisterCallback<GeometryChangedEvent>(GeometryChanged);
        Responsive(uiRoot.resolvedStyle.width);
    }
    private static Label Label(string text,int size=12,Color? color=null)
    {
        var l=new Label(text);
        l.style.fontSize=size;
        l.style.color=color??Ink;
        l.style.unityTextAlign=TextAnchor.MiddleLeft;
        return l;
    }
    private static Button Button(string text,Action onClick)
    {
        var b=new Button(onClick){text=text};
        b.style.backgroundColor=NavBg;
        b.style.color=Ink;
        b.style.fontSize=12;
        b.style.height=42;
        b.style.paddingLeft=9;
        b.style.paddingRight=9;
        b.style.borderTopLeftRadius=7;
        b.style.borderTopRightRadius=7;
        b.style.borderBottomLeftRadius=7;
        b.style.borderBottomRightRadius=7;
        return b;
    }
    private void BuildShell()
    {
        var old=uiRoot.Q<VisualElement>("ZPunkWorkspaceFrame");
        if(old!=null)
        {
            // Re-enable in Play Mode may leave earlier UI; unwrap Pages first.
            pages.RemoveFromHierarchy();
            old.RemoveFromHierarchy();
        }
        var oldTabs=uiRoot.Q<VisualElement>(className:"km-tabs");
        if(oldTabs!=null)oldTabs.style.display=DisplayStyle.None;
        header=uiRoot.Q<VisualElement>(className:"km-topbar");
        if(header==null)return;
        // Preserve the named, already-bound project/transport elements before
        // clearing the legacy header. Unity UI Toolkit detaches on reparent.
        var realRoot=doc.rootVisualElement;
        var play=realRoot.Q<Button>("PlayButton");
        var stop=realRoot.Q<Button>("StopButton");
        var newer=realRoot.Q<Button>("NewFileButton");
        var open=realRoot.Q<Button>("LoadButton");
        var existingTitle=realRoot.Q<Label>(className:"km-title");
        var preset=realRoot.Q<VisualElement>(className:"km-presetbar");
        projectInfo=realRoot.Q<Label>("PresetLabel");
        foreach (var retained in new VisualElement[]{play,stop,newer,open,existingTitle,preset})
            if(retained!=null) retained.RemoveFromHierarchy();
        var obsoleteMenu=uiRoot.Q<VisualElement>("ZPunkProjectMenu");
        if(obsoleteMenu!=null)obsoleteMenu.RemoveFromHierarchy();
        header.RemoveFromHierarchy();
        header.Clear();
        header.style.height=StyleKeyword.Auto;
        header.style.minHeight=82;
        header.style.paddingLeft=8;
        header.style.paddingRight=8;
        header.style.paddingTop=7;
        header.style.paddingBottom=8;
        header.style.marginBottom=3;
        header.style.flexDirection=FlexDirection.Column;
        header.style.backgroundColor=new Color(.052f,.080f,.124f);
        header.style.borderTopLeftRadius=9;
        header.style.borderTopRightRadius=9;
        header.style.borderBottomLeftRadius=9;
        header.style.borderBottomRightRadius=9;
        uiRoot.Insert(0,header);

        // Named controls are retained, not recreated: original events survive.
        var row1=new VisualElement();
        row1.style.flexDirection=FlexDirection.Row;
        row1.style.alignItems=Align.Center;
        row1.style.justifyContent=Justify.SpaceBetween;
        row1.style.height=36;
        if(existingTitle!=null)
        {
            existingTitle.RemoveFromHierarchy();
            existingTitle.text="ZPUNK 2.0";
            existingTitle.style.fontSize=19;
            existingTitle.style.color=Ink;
            row1.Add(existingTitle);
        }
        else
        {
            var title=Label("ZPUNK 2.0",19);
            title.style.unityFontStyleAndWeight=FontStyle.Bold;
            row1.Add(title);
        }
        menuToggle=Button("",()=>menu.style.display=menu.resolvedStyle.display==DisplayStyle.None?DisplayStyle.Flex:DisplayStyle.None);
        menuToggle.name="ZPunkMenuToggle";
        menuToggle.style.width=42;
        menuToggle.style.height=34;
        menuToggle.style.alignItems=Align.Center;
        menuToggle.style.justifyContent=Justify.Center;
        for(int line=0;line<3;line++)
        {
            var bar=new VisualElement();
            bar.style.width=20;
            bar.style.height=2;
            bar.style.marginBottom=3;
            bar.style.backgroundColor=Ink;
            menuToggle.Add(bar);
        }
        row1.Add(menuToggle);
        header.Add(row1);

        transportRow=new VisualElement();
        transportRow.style.flexDirection=FlexDirection.Row;
        transportRow.style.alignItems=Align.Center;
        transportRow.style.marginTop=5;
        if(play!=null)
        {
            play.RemoveFromHierarchy();
            play.text="▶";
            SetTransport(play);
            play.style.backgroundColor=Accent;
            play.style.color=new Color(.02f,.08f,.10f);
            transportRow.Add(play);
        }
        if(stop!=null)
        {
            stop.RemoveFromHierarchy();
            stop.text="■";
            SetTransport(stop);
            transportRow.Add(stop);
        }
        var bpmLabel=Label("BPM",10,Dim);
        bpmLabel.style.marginLeft=10;
        bpmLabel.style.marginRight=5;
        transportRow.Add(bpmLabel);
        bpmField=new FloatField(){value=120};
        bpmField.name="ZPunkTempo";
        bpmField.style.width=75;
        bpmField.style.height=36;
        bpmField.style.fontSize=14;
        bpmField.style.color=Ink;
        bpmField.style.backgroundColor=NavBg;
        bpmField.RegisterValueChangedCallback(evt=>
        {
            if(projects==null)return;
            float bpm=Mathf.Clamp(evt.newValue,20,300);
            var current=projects.GetSongForArrangementEditor();
            if(current==null || Mathf.Approximately(current.bpm,bpm))return;
            if(!ReferenceEquals(projects.CurrentSong,current))current=ArrangementEditing.IndependentCopy(current);
            current.bpm=bpm;
            projects.AcceptArrangementEdit(current);
            if(arrangement!=null && workspace=="arrange")arrangement.OnShown();
        });
        transportRow.Add(bpmField);
        var signature=Label("4/4",11,Dim);
        signature.style.marginLeft=8;
        transportRow.Add(signature);
        var space=new VisualElement();space.style.flexGrow=1;transportRow.Add(space);
        var save=Button("SAVE",Save);
        save.style.height=35;
        transportRow.Add(save);
        header.Add(transportRow);

        menu=new VisualElement{name="ZPunkProjectMenu"};
        menu.style.display=DisplayStyle.None;
        menu.style.position=Position.Absolute;
        menu.style.right=8;
        menu.style.top=48;
        menu.style.width=228;
        menu.style.paddingLeft=12;
        menu.style.paddingRight=12;
        menu.style.paddingTop=12;
        menu.style.paddingBottom=10;
        menu.style.backgroundColor=new Color(.115f,.153f,.211f);
        menu.style.borderTopLeftRadius=10;
        menu.style.borderTopRightRadius=10;
        menu.style.borderBottomLeftRadius=10;
        menu.style.borderBottomRightRadius=10;
        if(projectInfo!=null)
        {
            projectInfo.RemoveFromHierarchy();
            projectInfo.style.whiteSpace=WhiteSpace.Normal;
            projectInfo.style.color=Ink;
            projectInfo.style.fontSize=11;
            projectInfo.style.height=42;
            menu.Add(projectInfo);
        }
        if(preset!=null){preset.RemoveFromHierarchy();preset.style.display=DisplayStyle.None;}
        if(newer!=null)
        {
            newer.RemoveFromHierarchy();newer.text="NEW PROJECT";
            StyleMenuItem(newer);
            newer.clicked+=()=>menu.style.display=DisplayStyle.None;
            menu.Add(newer);
        }
        if(open!=null)
        {
            open.RemoveFromHierarchy();open.text="OPEN PROJECT";
            StyleMenuItem(open);
            open.clicked+=()=>menu.style.display=DisplayStyle.None;
            menu.Add(open);
        }
        var saveItem=Button("SAVE PROJECT",()=>{Save();menu.style.display=DisplayStyle.None;});
        StyleMenuItem(saveItem);menu.Add(saveItem);
        menu.Add(Button("CLOSE",()=>menu.style.display=DisplayStyle.None));

        shell=new VisualElement{name="ZPunkWorkspaceFrame"};
        shell.style.flexDirection=FlexDirection.Column;
        shell.style.flexGrow=1;
        shell.style.minHeight=0;
        shell.style.minWidth=0;
        shell.style.backgroundColor=new Color(.055f,.075f,.11f);
        uiRoot.Add(shell);
        content=new VisualElement();
        content.style.flexDirection=FlexDirection.Column;
        content.style.flexGrow=1;
        content.style.minWidth=0;
        content.style.minHeight=0;
        shell.Add(content);
        context=new VisualElement();
        context.style.height=42;
        context.style.flexDirection=FlexDirection.Row;
        context.style.alignItems=Align.Center;
        context.style.paddingLeft=11;
        context.style.backgroundColor=new Color(.064f,.095f,.138f);
        heading=Label("ARRANGE",14);
        heading.style.unityFontStyleAndWeight=FontStyle.Bold;
        subheading=Label("  /  SONG TIMELINE",10,Dim);
        context.Add(heading);
        context.Add(subheading);
        content.Add(context);
        pages.RemoveFromHierarchy();
        pages.style.flexGrow=1;
        pages.style.minHeight=0;
        pages.style.minWidth=0;
        pages.style.width=new Length(100,LengthUnit.Percent);
        pages.style.overflow=Overflow.Hidden;
        content.Add(pages);

        dock=new VisualElement{name="ZPunkWorkspaceDock"};
        dock.style.flexDirection=FlexDirection.Row;
        dock.style.backgroundColor=new Color(.065f,.094f,.135f);
        dock.style.justifyContent=Justify.SpaceAround;
        dock.style.paddingLeft=4;
        dock.style.paddingRight=4;
        dock.style.paddingTop=5;
        dock.style.paddingBottom=4;
        arrangeButton=AddDestination("ARRANGE",()=>Navigate("arrange"));
        createButton=AddDestination("CREATE",()=>Navigate("create"));
        soundButton=AddDestination("SOUND",()=>Navigate("sound"));
        shell.Add(dock);
        // Last root child paints the hamburger overlay above the workspace.
        uiRoot.Add(menu);
        // Existing project/preset popups must paint over our new DAW shell.
        var projectModal=uiRoot.Q<VisualElement>("ProjectModal");
        if(projectModal!=null){projectModal.RemoveFromHierarchy();uiRoot.Add(projectModal);}
        var presetBrowser=uiRoot.Q<VisualElement>("PresetBrowser");
        if(presetBrowser!=null){presetBrowser.RemoveFromHierarchy();uiRoot.Add(presetBrowser);}
        uiRoot.style.paddingLeft=4;
        uiRoot.style.paddingRight=4;
        uiRoot.style.paddingBottom=3;
        uiRoot.style.minHeight=0;
    }
    private void StyleMenuItem(Button b)
    {
        b.style.height=41;
        b.style.marginBottom=5;
        b.style.backgroundColor=NavBg;
        b.style.fontSize=12;
        b.style.color=Ink;
    }
    private void SetTransport(Button b)
    {
        b.style.width=52;
        b.style.height=39;
        b.style.fontSize=19;
        b.style.marginRight=6;
        b.style.backgroundColor=NavBg;
        b.style.borderTopLeftRadius=7;
        b.style.borderTopRightRadius=7;
        b.style.borderBottomLeftRadius=7;
        b.style.borderBottomRightRadius=7;
    }
    private Button AddDestination(string title,Action action)
    {
        var b=Button(title,action);
        b.style.flexGrow=1;
        b.style.height=46;
        b.style.marginRight=3;
        b.style.marginLeft=3;
        b.style.fontSize=12;
        b.style.unityFontStyleAndWeight=FontStyle.Bold;
        dock.Add(b);
        return b;
    }
    private void Save()
    {
        if(projects==null)projects=FindObjectOfType<KMusicProjectManager>();
        if(projects!=null)projects.SaveProject(projects.CurrentSlot);
    }
    private void NavigateCreate(int index,string selectedId)
    {
        workspace="create";
        ApplyPages();
        editor.OnShown(index,selectedId);
    }
    private void Navigate(string target)
    {
        workspace=target;
        ApplyPages();
        if(target=="arrange") arrangement.OnShown();
        else if(target=="create") editor.OnShown(arrangement!=null?arrangement.SelectedTrackIndex:0,
                arrangement!=null?arrangement.SelectedClipId:null);
        else if(target=="sound")sound.OnShown();
    }
    private void ApplyPages()
    {
        if(pages==null)return;
        foreach(var child in pages.Children())
            child.style.display=DisplayStyle.None;
        if(pageArrangement!=null)pageArrangement.style.display=workspace=="arrange"?DisplayStyle.Flex:DisplayStyle.None;
        if(pageEditor!=null)pageEditor.style.display=workspace=="create"?DisplayStyle.Flex:DisplayStyle.None;
        if(pageSound!=null)pageSound.style.display=workspace=="sound"?DisplayStyle.Flex:DisplayStyle.None;
        if(heading!=null)heading.text=workspace=="arrange"?"ARRANGE":workspace=="create"?"CREATE":"SOUND";
        if(subheading!=null)subheading.text=workspace=="arrange"?"  /  SONG TIMELINE":
            workspace=="create"?"  /  NOTE EDITOR":"  /  PRESET SHAPER";
        StyleNav(arrangeButton,workspace=="arrange");
        StyleNav(createButton,workspace=="create");
        StyleNav(soundButton,workspace=="sound");
        if(menu!=null)menu.style.display=DisplayStyle.None;
    }
    private void StyleNav(Button b,bool active)
    {
        if(b==null)return;
        b.style.backgroundColor=active?Selected:NavBg;
        b.style.color=active?Accent:Ink;
        b.style.borderBottomWidth=active?3:0;
        b.style.borderBottomColor=Accent;
    }
    private void GeometryChanged(GeometryChangedEvent evt)
    {
        if(Mathf.Abs(previousWidth-evt.newRect.width)>2)Responsive(evt.newRect.width);
    }
    private void Responsive(float width)
    {
        if(width<=0 || shell==null)return;
        previousWidth=width;
        compact=Screen.height>Screen.width || width<620;
        shell.style.flexDirection=compact?FlexDirection.Column:FlexDirection.Row;
        dock.RemoveFromHierarchy();
        if(compact)shell.Add(dock);
        else shell.Insert(0,dock);
        dock.style.flexDirection=compact?FlexDirection.Row:FlexDirection.Column;
        if(compact){dock.style.width=StyleKeyword.Auto;dock.style.height=58;}
        else {dock.style.width=112;dock.style.height=StyleKeyword.Auto;}
        dock.style.justifyContent=compact?Justify.SpaceAround:Justify.FlexStart;
        foreach(var button in new[]{arrangeButton,createButton,soundButton})
        {
            button.style.flexGrow=compact?1:0;
            button.style.height=compact?45:49;
            button.style.marginBottom=compact?0:8;
            button.style.fontSize=compact?12:11;
        }
        header.style.minHeight=compact?86:47;
    }
    private void Update()
    {
        if(!isReady || projects==null)return;
        if(lastSlot!=projects.CurrentSlot)
        {
            lastSlot=projects.CurrentSlot;
            var current=projects.GetSongForArrangementEditor();
            if(current!=null && bpmField!=null)bpmField.SetValueWithoutNotify(current.bpm);
            if(workspace=="arrange")arrangement.OnShown();
            else if(workspace=="create")editor.OnShown();
        }
        // A user-edited BPM is applied to SongData; timeline refreshes on navigation.
    }
}
