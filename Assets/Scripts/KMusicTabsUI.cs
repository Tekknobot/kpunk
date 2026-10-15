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
    private VisualElement pageArrangement, pageEditor, pageSound, pageMixer;
    private VisualElement context, content;
    private Label heading, subheading, projectInfo;
    private Label tempoDisplay;
    private Coroutine menuAnimation;
    private bool menuIsOpen;
    private Button arrangeButton, createButton, soundButton, mixButton, menuToggle;
    private ZPunkArrangementUI arrangement;
    private ZPunkClipEditorUI editor;
    private ZPunkSoundUI sound;
    private ZPunkMixerUI mixer;
    private ZPunkSongTransport transport;
    private KMusicProjectManager projects;
    private string workspace="arrange";
    private bool isReady, compact;
    private float previousWidth=-1f, previousHeight=-1f;
    private PanelSettings workspacePanel;
    private Vector2Int originalReferenceResolution;
    private float originalPanelMatch;
    private bool panelMetricsSaved, tabletMetrics;
    private bool pendingLiveEdit;
    private float nextLiveEditApply;

    // The stock panel is 1080 x 2280, height-matched for phones. Keeping those
    // metrics on a 4:3 tablet makes the DAW occupy a tiny fraction of the screen.
    // Switch the same panel to tablet-native coordinates, without scaling every
    // child or changing how existing phone builds look.
    private void ConfigureDisplayMetrics()
    {
        if(doc==null || doc.panelSettings==null)return;
        if(workspacePanel!=doc.panelSettings)
        {
            workspacePanel=doc.panelSettings;
            panelMetricsSaved=false;
        }
        if(!panelMetricsSaved)
        {
            originalReferenceResolution=workspacePanel.referenceResolution;
            originalPanelMatch=workspacePanel.match;
            panelMetricsSaved=true;
        }
        bool tablet=Screen.width>=650 && Screen.height>=650;
        if(tablet==tabletMetrics && panelMetricsSaved && previousWidth>=0)return;
        tabletMetrics=tablet;
        workspacePanel.referenceResolution=tablet ? new Vector2Int(1024,768) : originalReferenceResolution;
        workspacePanel.match=tablet ? 0.5f : originalPanelMatch;
    }

    private int lastSlot=-1;
    private static readonly Color Ink=new Color(.91f,.95f,.98f);
    private static readonly Color Dim=new Color(.58f,.66f,.76f);
    private static readonly Color Accent=new Color(.30f,.88f,.74f);
    private static readonly Color NavBg=new Color(.080f,.115f,.165f);
    private static readonly Color Selected=new Color(.12f,.32f,.33f);

    private void Awake(){if(doc==null)doc=GetComponent<UIDocument>(); ConfigureDisplayMetrics();}
    private void OnEnable()
    {
        // The old workspace created the pad Audio Helm controller dynamically.
        // Keep that runtime service even though its legacy UI is hidden.
        if(GetComponent<ZPunkInstrumentRack>()==null)
            gameObject.AddComponent<ZPunkInstrumentRack>();
        if(GetComponent<KMusicPadSynthRuntime>()==null)
            gameObject.AddComponent<KMusicPadSynthRuntime>();
        StartCoroutine(Setup());
    }
    private void OnDisable()
    {
        if(uiRoot!=null)uiRoot.UnregisterCallback<GeometryChangedEvent>(GeometryChanged);
        // PanelSettings is a shared ScriptableObject; do not retain temporary
        // tablet coordinates after the workspace closes or Editor Play stops.
        if(workspacePanel!=null && panelMetricsSaved)
        {
            workspacePanel.referenceResolution=originalReferenceResolution;
            workspacePanel.match=originalPanelMatch;
        }
        panelMetricsSaved=false;
        tabletMetrics=false;
        previousWidth=-1f;
        previousHeight=-1f;
        isReady=false;
    }
    private IEnumerator Setup()
    {
        if(doc==null)doc=GetComponent<UIDocument>();
        if(doc==null)yield break;
        ConfigureDisplayMetrics();
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
        pageMixer=pages.Q<VisualElement>("ZPunkMixerPage");
        if(pageMixer==null){pageMixer=new VisualElement{name="ZPunkMixerPage"};pages.Add(pageMixer);}
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
        mixer=GetComponent<ZPunkMixerUI>();
        if(mixer==null)mixer=gameObject.AddComponent<ZPunkMixerUI>();
        mixer.Initialize(pageMixer);
        transport=FindObjectOfType<ZPunkSongTransport>();
        arrangement.EditRequested=(index,id)=>NavigateCreate(index,id);
        // Coalesce rapid step edits/velocity drags. Rebuilding the entire event
        // plan per pointer event competes with audio scheduling and UI rendering.
        editor.OnClipChanged=()=>{pendingLiveEdit=true;};
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
        menuToggle=Button("",ToggleMenu);
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
        // No BPM input or readout in the transport. Tempo lives in Arrange.
        var signature=Label("4/4",11,Dim);
        signature.style.marginLeft=8;
        transportRow.Add(signature);
        var space=new VisualElement();space.style.flexGrow=1;transportRow.Add(space);
        var save=Button("SAVE",Save);
        save.style.height=35;
        save.style.borderTopLeftRadius=3;
        save.style.borderTopRightRadius=3;
        save.style.borderBottomLeftRadius=3;
        save.style.borderBottomRightRadius=3;
        transportRow.Add(save);
        header.Add(transportRow);

        menu=new VisualElement{name="ZPunkProjectMenu"};
        menu.style.display=DisplayStyle.None;
        menu.style.position=Position.Relative;
        menu.style.width=new Length(100,LengthUnit.Percent);
        menu.style.height=0;
        menu.style.overflow=Overflow.Hidden;
        menu.style.marginTop=0;
        menu.style.paddingLeft=12;
        menu.style.paddingRight=12;
        menu.style.paddingTop=12;
        menu.style.paddingBottom=10;
        menu.style.backgroundColor=new Color(.115f,.153f,.211f);
        menu.style.borderTopLeftRadius=4;
        menu.style.borderTopRightRadius=4;
        menu.style.borderBottomLeftRadius=4;
        menu.style.borderBottomRightRadius=4;
        if(projectInfo!=null)
        {
            projectInfo.RemoveFromHierarchy();
            projectInfo.style.whiteSpace=WhiteSpace.Normal;
            projectInfo.style.color=Ink;
            projectInfo.style.fontSize=11;
            projectInfo.style.height=35;
            projectInfo.style.marginBottom=8;
            menu.Add(projectInfo);
        }
        if(preset!=null){preset.RemoveFromHierarchy();preset.style.display=DisplayStyle.None;}
        var projectActions=new VisualElement { name="ZPunkProjectActions" };
        projectActions.style.flexDirection=FlexDirection.Row;
        projectActions.style.height=43;
        projectActions.style.marginBottom=7;
        projectActions.style.alignItems=Align.Center;
        if(newer!=null)
        {
            newer.RemoveFromHierarchy();newer.text="NEW";
            StyleMenuItem(newer);
            newer.clicked+=()=>SetMenuOpen(false);
            newer.style.flexGrow=1;
            newer.style.minWidth=0;
            newer.style.marginRight=5;
            projectActions.Add(newer);
        }
        if(open!=null)
        {
            open.RemoveFromHierarchy();open.text="OPEN";
            StyleMenuItem(open);
            open.clicked+=()=>SetMenuOpen(false);
            open.style.flexGrow=1;
            open.style.minWidth=0;
            projectActions.Add(open);
        }
        menu.Add(projectActions);
        var saveItem=Button("SAVE PROJECT",()=>{Save();SetMenuOpen(false);});
        StyleMenuItem(saveItem);menu.Add(saveItem);
        // The header grows to reveal this panel. It does not overlay music controls.
        var closeProject=Button("CLOSE MENU",()=>SetMenuOpen(false));
        StyleMenuItem(closeProject);
        menu.Add(closeProject);
        header.Add(menu);

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
        mixButton=AddDestination("MIX",()=>Navigate("mix"));
        shell.Add(dock);
        // Hamburger remains inline in the header, sliding down above Arrange.
        // Existing project/preset popups must paint over our new DAW shell.
        var projectModal=uiRoot.Q<VisualElement>("ProjectModal");
        if(projectModal!=null){projectModal.RemoveFromHierarchy();uiRoot.Add(projectModal);}
        var presetBrowser=uiRoot.Q<VisualElement>("PresetBrowser");
        if(presetBrowser!=null){presetBrowser.RemoveFromHierarchy();uiRoot.Add(presetBrowser);}
        uiRoot.style.paddingLeft=4;
        uiRoot.style.paddingRight=4;
        uiRoot.style.paddingBottom=9;
        uiRoot.style.minHeight=0;
    }
    private void StyleMenuItem(Button b)
    {
        b.style.height=41;
        b.style.marginBottom=5;
        b.style.backgroundColor=NavBg;
        b.style.fontSize=12;
        b.style.color=Ink;
        b.style.borderTopLeftRadius=3;
        b.style.borderTopRightRadius=3;
        b.style.borderBottomLeftRadius=3;
        b.style.borderBottomRightRadius=3;
    }
    private void ToggleMenu(){SetMenuOpen(!menuIsOpen);}
    private void SetMenuOpen(bool isOpen)
    {
        if(menu==null)return;
        menuIsOpen=isOpen;
        if(menuAnimation!=null)StopCoroutine(menuAnimation);
        menuAnimation=StartCoroutine(AnimateMenu(isOpen));
    }
    private IEnumerator AnimateMenu(bool open)
    {
        if(menu==null)yield break;
        float start=menu.resolvedStyle.height;
        if(open)menu.style.display=DisplayStyle.Flex;
        float target=open?225f:0f;
        const float seconds=.20f;
        float elapsed=0f;
        while(elapsed<seconds)
        {
            elapsed+=Time.unscaledDeltaTime;
            float t=Mathf.Clamp01(elapsed/seconds);
            t=t*t*(3f-2f*t);
            menu.style.height=Mathf.Lerp(start,target,t);
            yield return null;
        }
        menu.style.height=target;
        if(!open)menu.style.display=DisplayStyle.None;
        menuAnimation=null;
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
        else if(target=="sound")sound.OnShown(arrangement!=null?arrangement.SelectedTrackIndex:-1);
        else if(target=="mix")mixer.OnShown();
    }
    private void ApplyPages()
    {
        if(pages==null)return;
        foreach(var child in pages.Children())
            child.style.display=DisplayStyle.None;
        if(pageArrangement!=null)pageArrangement.style.display=workspace=="arrange"?DisplayStyle.Flex:DisplayStyle.None;
        if(pageEditor!=null)pageEditor.style.display=workspace=="create"?DisplayStyle.Flex:DisplayStyle.None;
        if(pageSound!=null)pageSound.style.display=workspace=="sound"?DisplayStyle.Flex:DisplayStyle.None;
        if(pageMixer!=null)pageMixer.style.display=workspace=="mix"?DisplayStyle.Flex:DisplayStyle.None;
        if(heading!=null)heading.text=workspace=="arrange"?"ARRANGE":workspace=="create"?"CREATE":workspace=="sound"?"SOUND":"MIX";
        if(subheading!=null)subheading.text=workspace=="arrange"?"  /  SONG TIMELINE":
            workspace=="create"?"  /  NOTE EDITOR":workspace=="sound"?"  /  PRESET SHAPER":"  /  LEVELS";
        StyleNav(arrangeButton,workspace=="arrange");
        StyleNav(createButton,workspace=="create");
        StyleNav(soundButton,workspace=="sound");
        StyleNav(mixButton,workspace=="mix");
        if(menuIsOpen)SetMenuOpen(false);
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
        if(Mathf.Abs(previousWidth-evt.newRect.width)>2 ||
            Mathf.Abs(previousHeight-evt.newRect.height)>2)Responsive(evt.newRect.width);
    }
    private void Responsive(float width)
    {
        if(width<=0 || shell==null)return;
        previousWidth=width;
        previousHeight=uiRoot.resolvedStyle.height;
        compact=Screen.height>Screen.width || width<720;
        bool shortViewport=previousHeight>0 && previousHeight<760;
        if(context!=null) context.style.height=shortViewport?32:42;
        if(header!=null)
        {
            header.style.paddingTop=shortViewport?3:7;
            header.style.paddingBottom=shortViewport?3:8;
        }
        shell.style.flexDirection=compact?FlexDirection.Column:FlexDirection.Row;
        dock.RemoveFromHierarchy();
        if(compact)shell.Add(dock);
        else shell.Insert(0,dock);
        dock.style.flexDirection=compact?FlexDirection.Row:FlexDirection.Column;
        if(compact){dock.style.width=StyleKeyword.Auto;dock.style.height=shortViewport?53:60;}
        else {dock.style.width=112;dock.style.height=StyleKeyword.Auto;}
        dock.style.justifyContent=compact?Justify.SpaceAround:Justify.FlexStart;
        dock.style.flexShrink=0;
        dock.style.overflow=Overflow.Visible;
        foreach(var button in new[]{arrangeButton,createButton,soundButton,mixButton})
        {
            button.style.flexGrow=compact?1:0;
            button.style.height=compact?(shortViewport?41:47):49;
            button.style.flexShrink=0;
            button.style.minWidth=0;
            button.style.marginBottom=compact?0:8;
            button.style.fontSize=compact?11:11;
        }
        header.style.minHeight=compact?(shortViewport?72:82):47;
    }
    private void Update()
    {
        if(!isReady)return;
        // Rotation and split-screen resize must not leave a tall-phone coordinate
        // system on a tablet. Reconfigure only if the device class changes.
        bool tabletNow=Screen.width>=650 && Screen.height>=650;
        if(tabletNow!=tabletMetrics)
        {
            ConfigureDisplayMetrics();
            Responsive(uiRoot.resolvedStyle.width);
        }
        if(projects==null)return;
        if(pendingLiveEdit && Time.unscaledTime>=nextLiveEditApply)
        {
            pendingLiveEdit=false;
            nextLiveEditApply=Time.unscaledTime+0.06f;
            if(transport==null)transport=FindObjectOfType<ZPunkSongTransport>();
            if(transport!=null)transport.ApplyLiveSong(projects.CurrentSong);
        }
        var live=projects.CurrentSong;
        if(live!=null && tempoDisplay!=null)
        {
            string bpmText=Mathf.RoundToInt(live.bpm)+" BPM";
            if(tempoDisplay.text!=bpmText)tempoDisplay.text=bpmText;
        }
        if(lastSlot!=projects.CurrentSlot)
        {
            lastSlot=projects.CurrentSlot;
            var current=projects.GetSongForArrangementEditor();
            if(current!=null && tempoDisplay!=null)tempoDisplay.text=Mathf.RoundToInt(current.bpm)+" BPM";
            if(workspace=="arrange")arrangement.OnShown();
            else if(workspace=="create")editor.OnShown();
            else if(workspace=="sound")sound.OnShown();
            else if(workspace=="mix")mixer.OnShown();
        }
        // BPM is set exclusively with the Arrange slider.
    }
}
