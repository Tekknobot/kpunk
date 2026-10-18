using System;
using System.Collections;
using System.IO;
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
    private VisualElement uiRoot, pages, shell, header, headerRow, menu, dock;
    private VisualElement pageArrangement, pageEditor, pageSound, pageMixer;
    private VisualElement context, content;
    private Label heading, subheading, projectInfo, headerTitle, headerSignature;
    private Label tempoDisplay;
    private Coroutine menuAnimation;
    private Coroutine wavExportRoutine;
    private Label exportStatus;
    private Button exportWavButton, exportMidiButton;
    private bool menuIsOpen;
    private Button arrangeButton, createButton, soundButton, mixButton, menuToggle, headerSave, headerPlay, headerStop;
    private ZPunkArrangementUI arrangement;
    private ZPunkClipEditorUI editor;
    private ZPunkSoundUI sound;
    private ZPunkMixerUI mixer;
    private ZPunkSongTransport transport;
    private KMusicProjectManager projects;
    private string workspace="arrange";
    private bool isReady, compact;
    private float previousWidth=-1f, previousHeight=-1f;
    private bool pendingLiveEdit;
    private float nextLiveEditApply;

    // PanelSettings belong to KMusicApp. Never overwrite its phone/tablet
    // reference resolution from another component: both behaviours previously
    // fought over the same shared panel and caused intermittent tablet scaling.

    private int lastSlot=-1;
    private static readonly Color Ink=new Color(.91f,.95f,.98f);
    private static readonly Color Dim=new Color(.58f,.66f,.76f);
    private static readonly Color Accent=new Color(.30f,.88f,.74f);
    private static readonly Color NavBg=new Color(.080f,.115f,.165f);
    private static readonly Color Selected=new Color(.12f,.32f,.33f);

    // Rendered icons rather than Unicode glyphs. Mobile platform fonts do not
    // consistently contain the play/stop symbols that appear in the Editor.
    private static Texture2D playTransportTexture;
    private static Texture2D stopTransportTexture;

    private void Awake(){if(doc==null)doc=GetComponent<UIDocument>();}
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
        if(headerStop!=null)
        {
            headerStop.clicked -= StopFromHeader;
            headerStop.UnregisterCallback<PointerDownEvent>(StopOnHeaderPointerDown, TrickleDown.TrickleDown);
        }
        if(wavExportRoutine!=null)
        {
            StopCoroutine(wavExportRoutine);
            wavExportRoutine=null;
            var capture=FindObjectOfType<ZPunkWavCapture>();
            if(capture!=null)capture.Abort();
            if(transport!=null && transport.IsPlaying)transport.StopSong();
        }
        if(uiRoot!=null)uiRoot.UnregisterCallback<GeometryChangedEvent>(GeometryChanged);
        previousWidth=-1f;
        previousHeight=-1f;
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
        l.style.fontSize=Mathf.Max(12,size);
        l.style.color=color??Ink;
        l.style.unityTextAlign=TextAnchor.MiddleLeft;
        return l;
    }
    private static Button Button(string text,Action onClick)
    {
        var b=new Button(onClick){text=text};
        b.style.backgroundColor=NavBg;
        b.style.color=Ink;
        b.style.fontSize=14;
        b.style.height=46;
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
        header.style.height=61;
        header.style.minHeight=61;
        header.style.flexShrink=0;
        header.style.paddingLeft=10;
        header.style.paddingRight=10;
        header.style.paddingTop=7;
        header.style.paddingBottom=7;
        header.style.marginBottom=3;
        header.style.flexDirection=FlexDirection.Row;
        header.style.alignItems=Align.Center;
        header.style.backgroundColor=new Color(.052f,.080f,.124f);
        header.style.borderTopLeftRadius=10;
        header.style.borderTopRightRadius=10;
        header.style.borderBottomLeftRadius=10;
        header.style.borderBottomRightRadius=10;
        uiRoot.Insert(0,header);

        // The entire transport lives in one permanently non-wrapping row.
        // Named Play/Stop buttons are reused, so their existing bindings survive.
        headerRow=new VisualElement{name="ZPunkHeaderRow"};
        headerRow.style.flexDirection=FlexDirection.Row;
        headerRow.style.flexWrap=Wrap.NoWrap;
        headerRow.style.alignItems=Align.Center;
        headerRow.style.width=new Length(100,LengthUnit.Percent);
        headerRow.style.minWidth=0;
        header.Add(headerRow);
        headerTitle=existingTitle!=null?existingTitle:Label("ZPUNK 2.0",18);
        headerTitle.text="ZPUNK 2.0";
        headerTitle.style.fontSize=18;
        headerTitle.style.unityFontStyleAndWeight=FontStyle.Bold;
        headerTitle.style.color=Ink;
        headerTitle.style.flexShrink=1;
        headerTitle.style.flexGrow=1;
        headerTitle.style.minWidth=0;
        headerTitle.style.overflow=Overflow.Hidden;
        headerTitle.style.whiteSpace=WhiteSpace.NoWrap;
        headerTitle.style.textOverflow=TextOverflow.Ellipsis;
        headerRow.Add(headerTitle);

        headerPlay=play;
        headerStop=stop;
        if(headerPlay!=null)
        {
            headerPlay.tooltip="Play";
            SetTransport(headerPlay);
            headerPlay.style.backgroundColor=Accent;
            headerPlay.style.color=new Color(.02f,.08f,.10f);
            SetTransportIcon(headerPlay,true);
            headerRow.Add(headerPlay);
        }
        if(headerStop!=null)
        {
            headerStop.tooltip="Stop";
            SetTransport(headerStop);
            SetTransportIcon(headerStop,false);
            headerRow.Add(headerStop);
            // The top-bar button is reparented from legacy UXML during setup.
            // Bind the *visible* button directly here: the legacy drum binder
            // may have run against a different/old tree in Unity 6.
            headerStop.clicked -= StopFromHeader;
            headerStop.clicked += StopFromHeader;
            headerStop.UnregisterCallback<PointerDownEvent>(StopOnHeaderPointerDown, TrickleDown.TrickleDown);
            headerStop.RegisterCallback<PointerDownEvent>(StopOnHeaderPointerDown, TrickleDown.TrickleDown);
        }
        headerSignature=Label("4/4",12,Dim);
        headerSignature.style.flexShrink=0;
        headerSignature.style.marginLeft=3;
        headerSignature.style.marginRight=5;
        headerRow.Add(headerSignature);

        headerSave=Button("SAVE",Save);
        headerSave.style.height=46;
        headerSave.style.minWidth=61;
        headerSave.style.fontSize=13;
        headerSave.style.flexShrink=0;
        headerSave.style.marginRight=6;
        headerSave.style.backgroundColor=new Color(.105f,.188f,.230f);
        headerRow.Add(headerSave);

        menuToggle=Button("",ToggleMenu);
        menuToggle.name="ZPunkMenuToggle";
        menuToggle.tooltip="Project menu";
        menuToggle.style.width=46;
        menuToggle.style.height=46;
        menuToggle.style.flexShrink=0;
        menuToggle.style.alignItems=Align.Center;
        menuToggle.style.justifyContent=Justify.Center;
        for(int line=0;line<3;line++)
        {
            var bar=new VisualElement();
            bar.style.width=19;
            bar.style.height=2;
            bar.style.marginBottom=line==2?0:4;
            bar.style.backgroundColor=Ink;
            bar.pickingMode=PickingMode.Ignore;
            menuToggle.Add(bar);
        }
        headerRow.Add(menuToggle);

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
        var exportRow=new VisualElement{name="ZPunkExportActions"};
        exportRow.style.flexDirection=FlexDirection.Row;
        exportRow.style.height=46;
        exportRow.style.marginBottom=6;
        exportWavButton=Button("EXPORT WAV",ExportWav);
        exportMidiButton=Button("EXPORT MIDI",ExportMidi);
        StyleMenuItem(exportWavButton); StyleMenuItem(exportMidiButton);
        exportWavButton.style.flexGrow=1;
        exportMidiButton.style.flexGrow=1;
        exportWavButton.style.flexShrink=1;
        exportMidiButton.style.flexShrink=1;
        exportWavButton.style.minWidth=0;
        exportMidiButton.style.minWidth=0;
        exportWavButton.style.marginRight=5;
        exportWavButton.style.marginBottom=0;
        exportMidiButton.style.marginBottom=0;
        exportWavButton.style.fontSize=12;
        exportMidiButton.style.fontSize=12;
        exportRow.Add(exportWavButton); exportRow.Add(exportMidiButton);
        menu.Add(exportRow);
        exportStatus=Label("WAV: real-time playback  /  MIDI: arrangement notes",11,Dim);
        exportStatus.style.height=27;
        exportStatus.style.whiteSpace=WhiteSpace.Normal;
        exportStatus.style.flexShrink=0;
        menu.Add(exportStatus);
        var closeProject=Button("CLOSE MENU",()=>SetMenuOpen(false));
        StyleMenuItem(closeProject);
        menu.Add(closeProject);
        // Menu expands below the one-row header, never inside it.
        uiRoot.Insert(1,menu);

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
        b.style.height=46;
        b.style.marginBottom=6;
        b.style.backgroundColor=NavBg;
        b.style.fontSize=13;
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
        float target=open?318f:0f;
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
    // Keep the original named Button elements and their transport callbacks.
    // Only their visual content is replaced; the icons ignore pointer input.
    private static void SetTransportIcon(Button button,bool play)
    {
        button.text=string.Empty;
        var previous=button.Q<Image>("ZPunkTransportIcon");
        if(previous!=null)previous.RemoveFromHierarchy();
        button.style.alignItems=Align.Center;
        button.style.justifyContent=Justify.Center;
        button.style.paddingLeft=0;
        button.style.paddingRight=0;
        button.style.paddingTop=0;
        button.style.paddingBottom=0;

        var icon=new Image
        {
            name="ZPunkTransportIcon",
            image=GetTransportTexture(play),
            scaleMode=ScaleMode.ScaleToFit,
            pickingMode=PickingMode.Ignore
        };
        icon.style.width=28;
        icon.style.height=28;
        icon.style.flexShrink=0;
        button.Add(icon);
    }

    // Tiny runtime-generated RGBA shapes require no font glyphs, packages,
    // imported sprites or platform-specific resources.
    private static Texture2D GetTransportTexture(bool play)
    {
        var cached=play?playTransportTexture:stopTransportTexture;
        if(cached!=null)return cached;

        const int size=32;
        var pixels=new Color32[size*size];
        Color32 ink=play?new Color32(5,20,26,255):new Color32(232,242,250,255);
        for(int y=0;y<size;y++)
        {
            for(int x=0;x<size;x++)
            {
                bool filled;
                if(play)
                {
                    // Right-pointing triangle: left edge at x=8, tip at x=27.
                    float reach=27f-Mathf.Abs(y-15.5f)*1.95f;
                    filled=y>=6 && y<=25 && x>=8 && x<=reach;
                }
                else
                {
                    filled=x>=8 && x<24 && y>=8 && y<24;
                }
                pixels[y*size+x]=filled?ink:new Color32(0,0,0,0);
            }
        }

        var texture=new Texture2D(size,size,TextureFormat.RGBA32,false);
        texture.name=play?"ZPunk Play Icon":"ZPunk Stop Icon";
        texture.hideFlags=HideFlags.DontSave;
        texture.wrapMode=TextureWrapMode.Clamp;
        texture.filterMode=FilterMode.Bilinear;
        texture.SetPixels32(pixels);
        texture.Apply(false,true);
        if(play)playTransportTexture=texture;
        else stopTransportTexture=texture;
        return texture;
    }

    private void StopOnHeaderPointerDown(PointerDownEvent evt)
    {
        // On touchscreens and under pointer capture, Unity UI Toolkit can drop
        // the eventual ClickEvent. Never wait for a release to stop audio.
        if (evt.button == 0) StopFromHeader();
    }

    private void StopFromHeader()
    {
        // Idempotent: pointer-down and click may both arrive for one gesture.
        if (transport == null) transport = GetComponent<ZPunkSongTransport>();
        if (transport == null) transport = FindObjectOfType<ZPunkSongTransport>();
        if (transport != null) transport.StopSong();
        var drumEngine = GetComponent<KMusicDrumSequencer>();
        if (drumEngine == null) drumEngine = FindObjectOfType<KMusicDrumSequencer>();
        if (drumEngine != null) drumEngine.StopPlayback();
        // The sample preview is an independent AudioSource, not a DAW voice.
        var preview = GetComponent<KMusicPlayerUI>();
        if (preview != null) preview.DawStopSample();
        Debug.Log("[ZPunk STOP] Transport and audio voices stopped from visible header.");
    }

    private void SetTransport(Button b)
    {
        b.style.width=47;
        b.style.height=46;
        b.style.flexShrink=0;
        b.style.fontSize=19;
        b.style.marginRight=5;
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
        b.style.height=49;
        b.style.marginRight=3;
        b.style.marginLeft=3;
        b.style.fontSize=13;
        b.style.unityFontStyleAndWeight=FontStyle.Bold;
        dock.Add(b);
        return b;
    }
    private void Save()
    {
        if(projects==null)projects=FindObjectOfType<KMusicProjectManager>();
        if(projects!=null)projects.SaveProject(projects.CurrentSlot);
    }
    private void ExportMidi()
    {
        if(wavExportRoutine!=null){ExportMessage("Wait until WAV export finishes.",true);return;}
        try
        {
            if(projects==null)projects=FindObjectOfType<KMusicProjectManager>();
            if(projects==null)throw new InvalidOperationException("Project manager not ready.");
            var song=projects.GetSongForPlayback();
            if(song==null)throw new InvalidOperationException("No song to export.");
            var bytes=ZPunkArrangementMidiExport.Create(song);
            string file=ZPunkMobileFileExport.CreatePrivateFilePath(song.title,".mid");
            File.WriteAllBytes(file,bytes);
            ExportMessage("MIDI: "+ZPunkMobileFileExport.Publish(file),false);
        }
        catch(Exception e) {Debug.LogWarning("[ZPunk MIDI] "+e);ExportMessage("MIDI: "+e.Message,true);}
    }
    private void ExportWav()
    {
        if(wavExportRoutine!=null){ExportMessage("WAV export already in progress.",true);return;}
        wavExportRoutine=StartCoroutine(ExportWavRoutine());
    }
    private IEnumerator ExportWavRoutine()
    {
        // Avoid StartCoroutine's synchronous first MoveNext completing before
        // wavExportRoutine gets assigned on a validation failure.
        yield return null;
        ZPunkWavCapture recorder=null;
        bool started=false;
        string file=null;
        double endAt=0;
        try
        {
            if(projects==null)projects=FindObjectOfType<KMusicProjectManager>();
            if(transport==null)transport=FindObjectOfType<ZPunkSongTransport>();
            if(projects==null || transport==null)
                throw new InvalidOperationException("DAW is not ready.");
            if(transport.IsPlaying)
                throw new InvalidOperationException("Stop playback before exporting WAV.");
            var song=projects.GetSongForPlayback();
            if(song==null || !SongEventPlanner.HasEvents(song))
                throw new InvalidOperationException("Create some notes before exporting.");
            double duration=Math.Max(0.1, song.lengthTicks * 60.0 /
                (Mathf.Clamp(song.bpm,1f,400f) * SongData.TicksPerQuarter));
            if(duration > 900)throw new InvalidOperationException("WAV bounce is limited to 15 minutes.");
            var listener=FindObjectOfType<AudioListener>();
            if(listener==null || !listener.isActiveAndEnabled)
                throw new InvalidOperationException("No active AudioListener is available.");
            recorder=listener.GetComponent<ZPunkWavCapture>();
            if(recorder==null)recorder=listener.gameObject.AddComponent<ZPunkWavCapture>();
            file=ZPunkMobileFileExport.CreatePrivateFilePath(song.title,".wav");
            if(!transport.PlaySong(0,true))
                throw new InvalidOperationException("Could not start full song playback.");
            started=true;
            // Track exactly one traversal, with a small tail for the output mixer.
            endAt=transport.SongAnchorDsp+duration;
            recorder.Begin(file,transport.SongAnchorDsp,duration+0.25);
            if(exportWavButton!=null)exportWavButton.SetEnabled(false);
            if(exportMidiButton!=null)exportMidiButton.SetEnabled(false);
            ExportMessage("WAV: recording in real time. Keep ZPUNK open.",false);
        }
        catch(Exception e)
        {
            Debug.LogWarning("[ZPunk WAV] "+e);
            ExportMessage("WAV: "+e.Message,true);
            if(recorder!=null)recorder.Abort();
            if(started && transport!=null)transport.StopSong();
        }
        if(!started || recorder==null || !recorder.IsActive)
        {
            if(exportWavButton!=null)exportWavButton.SetEnabled(true);
            if(exportMidiButton!=null)exportMidiButton.SetEnabled(true);
            wavExportRoutine=null;
            yield break;
        }
        double lastMessage=0;
        // Never wait forever if the mobile audio engine stops advancing.
        float deadline=Time.realtimeSinceStartup+(float)Math.Max(1,endAt-transport.SongAnchorDsp)+20f;
        while(transport.IsPlaying && !recorder.HasOverflow && AudioSettings.dspTime < endAt + 3 &&
              Time.realtimeSinceStartup < deadline)
        {
            if(AudioSettings.dspTime-lastMessage>1.0)
            {
                lastMessage=AudioSettings.dspTime;
                int progress=Mathf.Clamp(Mathf.RoundToInt((float)((AudioSettings.dspTime-
                    transport.SongAnchorDsp) / Math.Max(0.1,endAt-transport.SongAnchorDsp)*100)),0,100);
                ExportMessage("WAV: recording "+progress+"% - keep ZPUNK open.",false);
            }
            yield return null;
        }
        bool reachedEnd=AudioSettings.dspTime >= endAt-0.05 &&
                        Time.realtimeSinceStartup < deadline;
        if(reachedEnd && !recorder.HasOverflow)
        {
            while(AudioSettings.dspTime<endAt+0.30)yield return null;
        }
        try
        {
            if(!reachedEnd || recorder.HasOverflow)
            {
                recorder.Abort();
                ExportMessage(recorder.HasOverflow?"WAV: audio capture overrun; try again.":"WAV: canceled. No file saved.",true);
            }
            else
            {
                if(!recorder.Complete())throw new IOException("No audio was recorded.");
                ExportMessage("WAV: "+ZPunkMobileFileExport.Publish(file),false);
            }
        }
        catch(Exception e)
        {
            recorder.Abort();
            Debug.LogWarning("[ZPunk WAV] "+e);
            ExportMessage("WAV: "+e.Message,true);
        }
        finally
        {
            if(transport!=null && transport.IsPlaying)transport.StopSong();
            if(exportWavButton!=null)exportWavButton.SetEnabled(true);
            if(exportMidiButton!=null)exportMidiButton.SetEnabled(true);
            wavExportRoutine=null;
        }
    }
    private void ExportMessage(string message,bool error)
    {
        if(exportStatus!=null)
        {
            exportStatus.text=message;
            exportStatus.style.color=error?new Color(1f,.59f,.45f):Accent;
        }
        Debug.Log("[ZPunk Export] "+message);
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

        // Keep the header legible and single-line down to narrow phone widths.
        // Only the low-value signature disappears; the title and core actions stay.
        bool narrow=width<400;
        if(headerTitle!=null)
        {
            headerTitle.text=narrow?"ZPUNK":"ZPUNK 2.0";
            headerTitle.style.fontSize=narrow?16:18;
        }
        if(headerSignature!=null)
            headerSignature.style.display=width<440?DisplayStyle.None:DisplayStyle.Flex;
        if(headerPlay!=null)headerPlay.style.width=narrow?43:47;
        if(headerStop!=null)headerStop.style.width=narrow?43:47;
        if(headerSave!=null)
        {
            headerSave.style.minWidth=narrow?56:61;
            headerSave.style.paddingLeft=narrow?6:9;
            headerSave.style.paddingRight=narrow?6:9;
        }
        if(menuToggle!=null)menuToggle.style.width=narrow?43:46;
        if(header!=null)
        {
            header.style.height=shortViewport?56:61;
            header.style.minHeight=shortViewport?56:61;
        }
        if(context!=null) context.style.height=shortViewport?37:43;

        shell.style.flexDirection=compact?FlexDirection.Column:FlexDirection.Row;
        dock.RemoveFromHierarchy();
        if(compact)shell.Add(dock);
        else shell.Insert(0,dock);
        dock.style.flexDirection=compact?FlexDirection.Row:FlexDirection.Column;
        if(compact){dock.style.width=StyleKeyword.Auto;dock.style.height=shortViewport?57:64;}
        else {dock.style.width=118;dock.style.height=StyleKeyword.Auto;}
        dock.style.justifyContent=compact?Justify.SpaceAround:Justify.FlexStart;
        dock.style.flexShrink=0;
        dock.style.overflow=Overflow.Visible;
        foreach(var button in new[]{arrangeButton,createButton,soundButton,mixButton})
        {
            button.style.flexGrow=compact?1:0;
            button.style.height=compact?(shortViewport?46:51):53;
            button.style.flexShrink=0;
            button.style.minWidth=0;
            button.style.marginBottom=compact?0:8;
            button.style.fontSize=13;
        }
    }
    private void Update()
    {
        if(!isReady)return;
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
