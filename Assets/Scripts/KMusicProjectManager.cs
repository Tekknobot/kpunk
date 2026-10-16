using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;
using KMusic;
using KMusic.Core;
using ZPunk.DAW;

/// <summary>
/// Simple project save/load (slot-based) for Zpunk/KMusic.
///
/// Wires to the topbar UXML:
/// - PresetLabel: shows "Preset: PROJECT N"
/// - NewFileButton: creates a new project slot (auto-saves current first)
/// - LoadButton: opens a small overlay to pick a project slot
///
/// Uses Application.persistentDataPath/Projects/project_XXX.json
/// </summary>
[DefaultExecutionOrder(-950)]
public sealed class KMusicProjectManager : MonoBehaviour
{
    private const int ProjectVer = 2;
    private const string NativeFormat = "zpunk-2-arrangement";
    private const string PrefKey_CurrentSlot = "kmusic.project.slot.v1";

    [SerializeField] private UIDocument doc;

    // UI
    private Label _presetLabel;
    private Button _newBtn;
    private Button _loadBtn;
    private Button _deleteBtn;

    // Modal
    private VisualElement _modal;
    private ScrollView _modalList;
    private Label _modalTitle;
    private VisualElement _projectNotice;
    private readonly Dictionary<int, string> _cachedProjectNames = new();

    // Runtime refs
    private KMusicApp _app;
    private KMusicDrumSequencer _drums;
    private KMusicSampleSequencerUI _samplerUi;
    private KMusic.UI.KMusicPianoRollStepPainter _keys;
    private KMusic.KMusicChordTrackUI _pad;
    private KMusicHelmPresetPickerUI _helm;
    private KMusicPlayerUI _player;
    private KMusicChainUI _chainUi;

    // Phase 1 migration mirror. The legacy sequencers still own playback and editing.
    public SongData CurrentSong { get; private set; }
    /// <summary>Provide the currently edited DAW song, or a live legacy preview until the first edit.</summary>
    public SongData GetSongForArrangementEditor()
    {
        if (CurrentSong != null && (CurrentSong.arrangementAuthored ||
            (!CurrentSong.migratedFromLegacy && SongEventPlanner.HasEvents(CurrentSong))))
            return CurrentSong;
        return GetSongForPlayback();
    }

    /// <summary>Promote a copied song to the editable, persistent DAW document.</summary>
    public void AcceptArrangementEdit(SongData edited)
    {
        if (edited == null) return;
        edited.migratedFromLegacy = false;
        edited.arrangementAuthored = true;
        CurrentSong = edited;
    }

    public int CurrentSlot { get; private set; }
    private string ProjectsDir => Path.Combine(Application.persistentDataPath, "Projects");

    private VisualElement _presetBrowser;
    private ScrollView _presetBrowserList;

    private readonly List<BusPair> _pendingSynthBusRestore = new();

    private KMusicPadSynthRuntime _padSynthRuntime;

    private void Awake()
    {
        Screen.sleepTimeout = SleepTimeout.NeverSleep; // Screentimeout: NEVER

        if (!doc) doc = GetComponent<UIDocument>();
        CurrentSlot = Mathf.Clamp(ProjectPrefs.GetGlobalInt(PrefKey_CurrentSlot, 0), 0, 999);
        ProjectPrefs.SetActiveSlot(CurrentSlot);
    }

    private void OnEnable()
    {
        FindRuntimeRefs();
        WireTopbar();
        EnsureProjectsDir();
        UpdatePresetLabel();

        // Startup load if a project file exists.
        string path = SlotPath(CurrentSlot);
        if (File.Exists(path))
            LoadProject(CurrentSlot, silent: true);
        else
            ResetToBlank();
    }

    private void FindRuntimeRefs()
    {
        if (_app == null) _app = FindObjectOfType<KMusicApp>();
        if (_drums == null) _drums = FindObjectOfType<KMusicDrumSequencer>();
        if (_samplerUi == null) _samplerUi = FindObjectOfType<KMusicSampleSequencerUI>();
        if (_keys == null) _keys = FindObjectOfType<KMusic.UI.KMusicPianoRollStepPainter>();
        if (_pad == null) _pad = FindObjectOfType<KMusic.KMusicChordTrackUI>();
        if (_helm == null) _helm = FindObjectOfType<KMusicHelmPresetPickerUI>();
        if (_padSynthRuntime == null) _padSynthRuntime = FindObjectOfType<KMusicPadSynthRuntime>();
        if (_player == null)
        {
            _player = FindObjectOfType<KMusicPlayerUI>();
            if (_player != null) _player.SampleRestoreError += ShowProjectNotice;
        }
        if (_chainUi == null) _chainUi = FindObjectOfType<KMusicChainUI>();
    }

    private void WireTopbar()
    {
        if (!doc) return;
        var root = doc.rootVisualElement;
        if (root == null) return;

        _presetLabel = root.Q<Label>("PresetLabel");
        _presetBrowser = root.Q<VisualElement>("PresetBrowser");
        _newBtn = root.Q<Button>("NewFileButton");
        _loadBtn = root.Q<Button>("LoadButton");
        _deleteBtn = root.Q<Button>("DeleteProjectButton");

        if (_newBtn != null)
        {
            _newBtn.clicked -= OnNewClicked;
            _newBtn.clicked += OnNewClicked;
        }

        if (_loadBtn != null)
        {
            _loadBtn.clicked -= OnLoadClicked;
            _loadBtn.clicked += OnLoadClicked;
        }

        if (_deleteBtn != null)
        {
            _deleteBtn.clicked -= OnDeleteClicked;
            _deleteBtn.clicked += OnDeleteClicked;
        }

        EnsureModal(root);
    }

    private void EnsureModal(VisualElement root)
    {
        if (_modal != null) return;

        _modal = new VisualElement { name = "ProjectModal" };
        _modal.AddToClassList("km-project-modal");
        _modal.style.position = Position.Absolute;
        _modal.style.left = 0;
        _modal.style.right = 0;
        _modal.style.top = 0;
        _modal.style.bottom = 0;
        _modal.style.display = DisplayStyle.None;
        _modal.style.backgroundColor = new Color(.018f, .027f, .047f, .89f);
        _modal.pickingMode = PickingMode.Position;

        var card = new VisualElement();
        card.AddToClassList("km-project-card");
        card.style.position = Position.Absolute;
        card.style.left = 12;
        card.style.right = 12;
        card.style.minWidth = 0;
        card.style.overflow = Overflow.Hidden;
        card.style.top = new Length(7,LengthUnit.Percent);
        card.style.bottom = new Length(7,LengthUnit.Percent);
        card.style.backgroundColor = new Color(.072f, .106f, .15f, 1f);
        card.style.borderTopWidth = 1;
        card.style.borderBottomWidth = 1;
        card.style.borderLeftWidth = 1;
        card.style.borderRightWidth = 1;
        card.style.borderTopColor = new Color(.23f,.32f,.41f);
        card.style.borderBottomColor = new Color(.23f,.32f,.41f);
        card.style.borderLeftColor = new Color(.23f,.32f,.41f);
        card.style.borderRightColor = new Color(.23f,.32f,.41f);
        card.style.borderTopLeftRadius = 7;
        card.style.borderTopRightRadius = 7;
        card.style.borderBottomLeftRadius = 7;
        card.style.borderBottomRightRadius = 7;
        card.style.paddingLeft = 13;
        card.style.paddingRight = 13;
        card.style.paddingTop = 15;
        card.style.paddingBottom = 12;
        card.style.flexDirection = FlexDirection.Column;

        _modalTitle = new Label("PROJECTS");
        _modalTitle.AddToClassList("km-project-title");
        _modalTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
        _modalTitle.style.fontSize = 20;
        _modalTitle.style.color = new Color(.89f,.94f,.99f);
        _modalTitle.style.marginBottom = 10;
        card.Add(_modalTitle);

        var row = new VisualElement();
        row.AddToClassList("km-project-toolbar");
        row.style.flexDirection = FlexDirection.Row;
        row.style.justifyContent = Justify.SpaceBetween;
        row.style.alignItems = Align.Center;
        row.style.marginBottom = 8;
        row.style.minHeight = 40;

        var saveBtn = new Button(() => { SaveProject(CurrentSlot); HideModal(); }) { text = "SAVE" };
        saveBtn.AddToClassList("km-pillbtn");
        saveBtn.AddToClassList("km-project-toolbar-btn");
        saveBtn.style.height=40;
        saveBtn.style.flexGrow=1;
        saveBtn.style.marginRight=6;
        saveBtn.style.borderTopLeftRadius=3;
        saveBtn.style.borderTopRightRadius=3;
        saveBtn.style.borderBottomLeftRadius=3;
        saveBtn.style.borderBottomRightRadius=3;
        row.Add(saveBtn);

        var closeBtn = new Button(HideModal) { text = "CLOSE" };
        closeBtn.AddToClassList("km-pillbtn");
        closeBtn.AddToClassList("km-project-toolbar-btn");
        closeBtn.AddToClassList("km-project-toolbar-btn--secondary");
        closeBtn.style.height=40;
        closeBtn.style.flexGrow=1;
        closeBtn.style.borderTopLeftRadius=3;
        closeBtn.style.borderTopRightRadius=3;
        closeBtn.style.borderBottomLeftRadius=3;
        closeBtn.style.borderBottomRightRadius=3;
        row.Add(closeBtn);
        card.Add(row);

        _modalList = new ScrollView(ScrollViewMode.Vertical);
        _modalList.AddToClassList("km-project-scroll");
        _modalList.style.flexGrow = 1;
        _modalList.style.minHeight = 0;
        _modalList.style.minWidth = 0;
        _modalList.style.width = Length.Percent(100);
        _modalList.contentContainer.style.width = Length.Percent(100);
        _modalList.contentContainer.style.minWidth = 0;
        _modalList.style.overflow = Overflow.Hidden;
        _modalList.verticalScrollerVisibility=ScrollerVisibility.Hidden;
        card.Add(_modalList);

        _modal.Add(card);
        root.Add(_modal);

        // Clicking backdrop closes
        _modal.RegisterCallback<PointerDownEvent>(evt =>
        {
            if (evt.target == _modal)
                HideModal();
        });
    }

    // Non-destructive, readable warning on phones and tablets. No alerts are dismissed
    // automatically: a missing sample must never appear as a successful load.
    private void ShowProjectNotice(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        Debug.LogWarning("[ZPunk Projects] " + message);
        if (doc == null || doc.rootVisualElement == null) return;
        var root = doc.rootVisualElement;
        if (_projectNotice == null)
        {
            _projectNotice = new VisualElement { name = "ZPunkProjectWarning" };
            _projectNotice.style.position = Position.Absolute;
            _projectNotice.style.left = 8;
            _projectNotice.style.right = 8;
            _projectNotice.style.bottom = 9;
            _projectNotice.style.paddingLeft = 12;
            _projectNotice.style.paddingRight = 10;
            _projectNotice.style.paddingTop = 10;
            _projectNotice.style.paddingBottom = 10;
            _projectNotice.style.backgroundColor = new Color(.19f,.13f,.12f,.98f);
            _projectNotice.style.borderTopLeftRadius = 7;
            _projectNotice.style.borderTopRightRadius = 7;
            _projectNotice.style.borderBottomLeftRadius = 7;
            _projectNotice.style.borderBottomRightRadius = 7;
            _projectNotice.style.flexDirection = FlexDirection.Row;
            _projectNotice.style.alignItems = Align.Center;
            var label = new Label { name = "ZPunkWarningText" };
            label.style.flexGrow = 1;
            label.style.flexShrink = 1;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.fontSize = 12;
            label.style.color = new Color(.99f,.91f,.82f);
            _projectNotice.Add(label);
            var dismiss = new Button(() => _projectNotice.style.display = DisplayStyle.None) { text = "OK" };
            dismiss.style.minWidth = 52;
            dismiss.style.height = 42;
            dismiss.style.marginLeft = 8;
            _projectNotice.Add(dismiss);
        }
        _projectNotice.Q<Label>("ZPunkWarningText").text = message;
        _projectNotice.style.display = DisplayStyle.Flex;
        // Keep above any other modal and all regular workspace elements.
        if (_projectNotice.parent != null) _projectNotice.RemoveFromHierarchy();
        root.Add(_projectNotice);
    }

    private static bool IsNativeProject(ProjectData data)
    {
        if (data == null || data.song == null || data.song.schemaVersion != SongData.CurrentSchema ||
            data.song.migratedFromLegacy || data.song.tracks == null || data.song.contents == null ||
            data.song.tracks.Count != 5) return false;
        // Current-format saves carry an explicit marker. Also accept native 2.0
        // arrangements saved by early preview builds, before the marker existed.
        if (data.ver >= ProjectVer && data.projectFormat != NativeFormat) return false;
        // Old 1.x saves may already contain a generated conversion preview.
        // That is NOT proof of a native 2.0 project: only authored 2.0 clips qualify.
        if (data.ver < ProjectVer && !data.song.arrangementAuthored) return false;
        for (int i = 0; i < 5; i++)
        {
            var track = data.song.tracks[i];
            if (track == null || (int)track.kind != i || track.clips == null || track.instrument == null)
                return false;
            foreach (var c in track.clips)
                if (c == null || string.IsNullOrEmpty(c.contentId) || c.lengthTicks <= 0 ||
                    c.startTick < 0 || ArrangementEditing.FindContent(data.song, c.contentId) == null)
                    return false;
        }
        foreach (var content in data.song.contents)
            if (content == null || content.lengthTicks <= 0) return false;
        return true;
    }

    private void StartFreshAfterIncompatible(int rejectedSlot)
    {
        // DO NOT overwrite/delete an old project, any imported samples, or exports.
        // Always move to a free slot first; old files can be backed up externally.
        int next = FindNextFreeSlot(rejectedSlot);
        if (File.Exists(SlotPath(next)))
        {
            ShowProjectNotice("Incompatible project. No free slot available. Your previous file was left untouched.");
            return;
        }
        SwitchToSlot(next);
        ResetToBlank();
        ShowProjectNotice("PROJECT " + rejectedSlot.ToString("000") +
            " was saved by an incompatible ZPUNK version. It was NOT deleted. " +
            "A fresh ZPUNK 2.0 workspace is now open in slot " + next.ToString("000") + ".");
    }

    private void OnNewClicked()
    {
        SaveProject(CurrentSlot);

        int next = FindNextFreeSlot(CurrentSlot);
        if (File.Exists(SlotPath(next)))
        {
            ShowProjectNotice("All project slots are full. No files were overwritten.");
            return;
        }
        CurrentSlot = next;
        ProjectPrefs.SetActiveSlot(CurrentSlot);

        ResetToBlank();
        SaveProject(CurrentSlot);
        UpdatePresetLabel();
    }

    private void OnLoadClicked()
    {
        ShowModal();
    }

    private void OnDeleteClicked()
    {
        DeleteProject(CurrentSlot, closeModal: false);
    }

    private int FindNextFreeSlot(int start)
    {
        for (int i = 1; i < 1000; i++)
        {
            int slot = (start + i) % 1000;
            if (!File.Exists(SlotPath(slot))) return slot;
        }
        return start; // callers must check before writing
    }

    private void EnsureProjectsDir()
    {
        try
        {
            if (!Directory.Exists(ProjectsDir))
                Directory.CreateDirectory(ProjectsDir);
        }
        catch { }
    }

    private string SlotPath(int slot) => Path.Combine(ProjectsDir, $"project_{slot:000}.json");

    private void UpdatePresetLabel()
    {
        if (_presetLabel != null)
            _presetLabel.text = $"Preset: {GetProjectDisplayName(CurrentSlot)}";
    }

    private string DefaultProjectName(int slot) => $"PROJECT {slot:000}";

    private string GetProjectDisplayName(int slot)
    {
        string raw = GetStoredProjectName(slot);
        return string.IsNullOrWhiteSpace(raw) ? DefaultProjectName(slot) : raw.Trim();
    }

    private string GetStoredProjectName(int slot)
    {
        if (_cachedProjectNames.TryGetValue(slot, out var cached))
            return cached;

        string path = SlotPath(slot);
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            var data = JsonUtility.FromJson<ProjectData>(json);
            string value = data != null ? data.projectName : null;
            _cachedProjectNames[slot] = value;
            return value;
        }
        catch
        {
            return null;
        }
    }

    private bool RenameProject(int slot, string newName)
    {
        string clean = string.IsNullOrWhiteSpace(newName) ? DefaultProjectName(slot) : newName.Trim();
        string path = SlotPath(slot);
        if (!File.Exists(path))
            return false;

        try
        {
            var data = JsonUtility.FromJson<ProjectData>(File.ReadAllText(path));
            if (data == null)
                data = new ProjectData();

            data.projectName = clean;
            if (data.slot <= 0 && slot != 0)
                data.slot = slot;
            if (data.ver <= 0)
                data.ver = ProjectVer;
            if (data.utc <= 0)
                data.utc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            File.WriteAllText(path, JsonUtility.ToJson(data));
            _cachedProjectNames[slot] = clean;

            if (slot == CurrentSlot)
                UpdatePresetLabel();

            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[Project] Rename failed: " + e.Message);
            return false;
        }
    }

    private void ShowModal()
    {
        if (_modal == null || _modalList == null) return;
        BuildProjectList();
        _modal.style.display = DisplayStyle.Flex;
    }

    private void HideModal()
    {
        if (_modal != null)
            _modal.style.display = DisplayStyle.None;
    }

    private void BuildProjectList()
    {
        _modalList.Clear();

        var existing = new SortedSet<int>();
        try
        {
            if (Directory.Exists(ProjectsDir))
            {
                foreach (var f in Directory.GetFiles(ProjectsDir, "project_*.json"))
                {
                    var name = Path.GetFileNameWithoutExtension(f);
                    if (name.Length >= 11 && int.TryParse(name.Substring(8), out int slot))
                        existing.Add(Mathf.Clamp(slot, 0, 999));
                }
            }
        }
        catch { }

        for (int i = -2; i <= 6; i++)
            existing.Add(Mathf.Clamp(CurrentSlot + i, 0, 999));

        foreach (int slot in existing)
        {
            string path = SlotPath(slot);
            bool has = File.Exists(path);
            bool compatible = false;
            if (has)
            {
                try { compatible = IsNativeProject(JsonUtility.FromJson<ProjectData>(File.ReadAllText(path))); }
                catch (Exception) { compatible = false; }
            }

            var row = new VisualElement();
            row.AddToClassList("km-project-row");
            if (slot == CurrentSlot)
                row.AddToClassList("km-project-row--current");
            row.style.flexDirection = FlexDirection.Column;
            row.style.width = Length.Percent(100);
            row.style.minWidth = 0;
            row.style.flexShrink = 0;
            row.style.marginBottom = 7;
            row.style.paddingLeft = 9;
            row.style.paddingRight = 9;
            row.style.paddingTop = 9;
            row.style.paddingBottom = 9;
            row.style.borderTopLeftRadius = 4;
            row.style.borderTopRightRadius = 4;
            row.style.borderBottomLeftRadius = 4;
            row.style.borderBottomRightRadius = 4;
            row.style.backgroundColor = slot == CurrentSlot ?
                new Color(.12f,.24f,.26f,1f) : new Color(.10f,.145f,.205f,1f);

            var headerRow = new VisualElement();
            headerRow.AddToClassList("km-project-row-header");
            headerRow.style.flexDirection = FlexDirection.Row;
            headerRow.style.justifyContent = Justify.SpaceBetween;
            headerRow.style.alignItems = Align.Center;
            headerRow.style.minWidth = 0;
            row.Add(headerRow);

            var slotLabel = new Label(GetProjectDisplayName(slot));
            slotLabel.AddToClassList("km-project-slot");
            slotLabel.style.fontSize=13;
            slotLabel.style.flexGrow = 1;
            slotLabel.style.minWidth = 0;
            slotLabel.style.whiteSpace = WhiteSpace.Normal;
            slotLabel.style.color=new Color(.92f,.97f,.99f);
            slotLabel.style.unityFontStyleAndWeight=FontStyle.Bold;
            headerRow.Add(slotLabel);

            string desc;
            if (!has)
                desc = $"Project {slot:000} • Empty slot ready for a new project.";
            else if (!compatible)
                desc = $"Project {slot:000} - Older/incompatible file, preserved. Load starts a fresh workspace.";
            else if (slot == CurrentSlot)
                desc = $"Project {slot:000} - Current project. You can rename, save, or delete it.";
            else
                desc = $"Project {slot:000} - Ready to load or delete.";

            var label = new Label(desc);
            label.AddToClassList("km-project-name");
            label.style.fontSize=10;
            label.style.color=new Color(.60f,.71f,.80f);
            label.style.whiteSpace=WhiteSpace.Normal;
            row.Add(label);

            var btnRow = new VisualElement();
            btnRow.AddToClassList("km-project-actions");
            btnRow.style.flexDirection = FlexDirection.Row;
            btnRow.style.marginTop = 7;
            btnRow.style.flexWrap=Wrap.NoWrap;
            btnRow.style.minWidth=0;
            btnRow.style.width=Length.Percent(100);
            if (has)
            {
                if (slot == CurrentSlot)
                {
                    var renameRow = new VisualElement();
                    renameRow.AddToClassList("km-project-rename-row");
                    renameRow.style.flexDirection = FlexDirection.Row;
                    renameRow.style.alignItems = Align.Center;
                    renameRow.style.justifyContent = Justify.FlexStart;
                    renameRow.style.width = Length.Percent(100);
                    renameRow.style.minWidth=0;
                    renameRow.style.overflow=Overflow.Hidden;
                    renameRow.style.marginTop = 10;

                    var renameField = new TextField();
                    renameField.value = GetProjectDisplayName(slot);
                    renameField.label = string.Empty;
                    renameField.AddToClassList("km-project-rename-field");
                    renameField.style.flexGrow = 1;
                    renameField.style.minWidth = 0;
                    renameField.style.marginRight = 6;
                    renameField.style.height=36;
                    renameField.style.minHeight=36;
                    renameField.style.paddingLeft=5;
                    renameField.style.paddingRight=3;
                    renameField.style.fontSize=12;
                    // TextField contains a nested text-input element. Style both
                    // levels with dark ink on a light field so typed names never
                    // disappear as white text on Unity's default white input.
                    Color inputInk = new Color(.055f,.09f,.14f);
                    Color inputSurface = new Color(.88f,.93f,.97f);
                    renameField.style.color = inputInk;
                    renameField.style.backgroundColor = inputSurface;
                    renameField.RegisterCallback<AttachToPanelEvent>(_ =>
                    {
                        var inner = renameField.Q<VisualElement>(className: "unity-text-input");
                        if (inner != null)
                        {
                            inner.style.color = inputInk;
                            inner.style.backgroundColor = inputSurface;
                        }
                    });
                    renameRow.Add(renameField);

                    var btnRename = new Button(() =>
                    {
                        if (RenameProject(slot, renameField.value))
                            BuildProjectList();
                    })
                    { text = "RENAME" };
                    btnRename.AddToClassList("km-pillbtn");
                    btnRename.AddToClassList("km-project-action-btn");
                    btnRename.AddToClassList("km-project-rename-btn");
                    btnRename.style.flexShrink = 0;
                    btnRename.style.width = 76;
                    btnRename.style.minWidth = 76;
                    btnRename.style.borderTopLeftRadius=3;
                    btnRename.style.borderTopRightRadius=3;
                    btnRename.style.borderBottomLeftRadius=3;
                    btnRename.style.borderBottomRightRadius=3;
                    btnRename.style.height=36;
                    renameRow.Add(btnRename);

                    row.Add(renameRow);
                }

                var btnLoad = new Button(() =>
                {
                    SaveProject(CurrentSlot);
                    SwitchToSlot(slot);
                    LoadProject(slot);
                    HideModal();
                })
                { text = "LOAD" };
                btnLoad.AddToClassList("km-pillbtn");
                btnLoad.AddToClassList("km-project-action-btn");
                btnLoad.style.flexGrow = 1;
                btnLoad.style.height = 37;
                btnLoad.style.minWidth=64;
                btnLoad.style.fontSize=10;
                btnLoad.style.borderTopLeftRadius=3;
                btnLoad.style.borderTopRightRadius=3;
                btnLoad.style.borderBottomLeftRadius=3;
                btnLoad.style.borderBottomRightRadius=3;
                btnRow.Add(btnLoad);

                var btnNoSave = new Button(() =>
                {
                    SwitchToSlot(slot);
                    LoadProject(slot);
                    HideModal();
                })
                { text = "OPEN DIRECT" };
                btnNoSave.AddToClassList("km-pillbtn");
                btnNoSave.AddToClassList("km-project-action-btn");
                btnNoSave.AddToClassList("km-project-action-btn--secondary");
                btnNoSave.style.flexGrow = 1;
                btnNoSave.style.height = 37;
                btnNoSave.style.minWidth=86;
                btnNoSave.style.fontSize=10;
                btnNoSave.style.borderTopLeftRadius=3;
                btnNoSave.style.borderTopRightRadius=3;
                btnNoSave.style.borderBottomLeftRadius=3;
                btnNoSave.style.borderBottomRightRadius=3;
                btnRow.Add(btnNoSave);

                var btnDelete = new Button(() =>
                {
                    DeleteProject(slot);
                })
                { text = "DELETE" };
                btnDelete.AddToClassList("km-pillbtn");
                btnDelete.AddToClassList("km-pillbtn--danger");
                btnDelete.AddToClassList("km-project-action-btn");
                btnDelete.style.flexGrow = 1;
                btnDelete.style.height = 37;
                btnDelete.style.minWidth=58;
                btnDelete.style.fontSize=10;
                btnDelete.style.borderTopLeftRadius=3;
                btnDelete.style.borderTopRightRadius=3;
                btnDelete.style.borderBottomLeftRadius=3;
                btnDelete.style.borderBottomRightRadius=3;
                btnRow.Add(btnDelete);
            }
            else
            {
                var btnCreate = new Button(() =>
                {
                    SaveProject(CurrentSlot);
                    SwitchToSlot(slot);
                    ResetToBlank();
                    SaveProject(slot);
                    HideModal();
                })
                { text = "CREATE" };
                btnCreate.AddToClassList("km-pillbtn");
                btnCreate.AddToClassList("km-project-action-btn");
                btnCreate.style.flexGrow = 1;
                btnCreate.style.height = 37;
                btnCreate.style.borderTopLeftRadius=3;
                btnCreate.style.borderTopRightRadius=3;
                btnCreate.style.borderBottomLeftRadius=3;
                btnCreate.style.borderBottomRightRadius=3;
                btnRow.Add(btnCreate);
            }

            row.Add(btnRow);
            _modalList.Add(row);
        }
    }

    public void DeleteProject(int slot, bool closeModal = true)
    {
        EnsureProjectsDir();

        string path = SlotPath(slot);
        try
        {
            if (File.Exists(path))
                File.Delete(path);
            _cachedProjectNames.Remove(slot);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[Project] Delete failed: " + e.Message);
            return;
        }

        if (slot == CurrentSlot)
        {
            FindRuntimeRefs();
            ResetToBlank();
        }

        UpdatePresetLabel();

        if (_modal != null && _modal.style.display == DisplayStyle.Flex)
        {
            BuildProjectList();
            if (closeModal)
                HideModal();
        }
    }

    private void SwitchToSlot(int slot)
    {
        CurrentSlot = slot;
        ProjectPrefs.SetActiveSlot(CurrentSlot);
        UpdatePresetLabel();
    }
    // ----------------------------
    // Save / Load
    // ----------------------------

    [Serializable]
    private class ProjectData
    {
        public int ver;
        public string projectFormat;
        public int slot;
        public long utc;
        public string projectName;

        public List<BusPair> bus = new();
        public DrumState drums = new();
        public SampleState sampler = new();
        public SeqState seq = new();
        public PadState pad = new();
        public SynthState synth = new();
        public PlayerState player = new();
        public ChopState chops = new();
        public PatternBank.PatternBankSave patterns = new();
        public ChainStateSave chain = new();
        // ZPunk 2.0 song snapshot. Legacy fields remain the source of truth in phase 1.
        public SongData song;
    }

    [Serializable] private class BusPair { public string id; public float v; }

    [Serializable]
    private class DrumState
    {
        public string stepMaskB64;
        public string stepVelocityB64;
        public bool[] mutes;
        public int activeDrumId;
        public int kitIndex;
        public int[] sampleSteps;
    }

    [Serializable] private class SampleState { public int[] stepGrid; }
    [Serializable] private class SeqState { public int[] stepGrid; public int[] runs; }

    [Serializable]
    private class PadState
    {
        public int[] stepGrid;
        public int[] runs;
        public int chordMode;
        public string presetRelPath;
        public int presetIndex;
        public float masterVolume = -1f;
    }

    [Serializable] private class SynthState { public string presetRelPath; public int presetIndex; public float masterVolume = -1f; }
    [Serializable] private class PlayerState { public string lastTrackId; }
    [Serializable] private class ChopState { public string resourcesPath; public float[] sliceStart01; public float[] sliceEnd01; }

    private static bool IsSynthBusId(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;

        return id.StartsWith("osc1.", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("osc2.", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("sub.", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("noise.", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("synth.", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("filter.", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("amp.", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("mod.", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("fx.", StringComparison.OrdinalIgnoreCase)
            || id.Equals("master.vol", StringComparison.OrdinalIgnoreCase)
            || id.Equals("master.porta", StringComparison.OrdinalIgnoreCase)
            || id.Equals("master.poly", StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyBusPairs(List<BusPair> pairs)
    {
        if (_app == null || _app.Bus == null || pairs == null || pairs.Count == 0)
            return;

        for (int i = 0; i < pairs.Count; i++)
        {
            var it = pairs[i];
            if (it == null || string.IsNullOrEmpty(it.id)) continue;
            _app.Bus.SetValue(it.id, it.v);
        }
    }

    private void OnProjectPresetApplied(int _, string __)
    {
        if (_helm != null)
            _helm.PresetApplied -= OnProjectPresetApplied;

        if (!isActiveAndEnabled)
        {
            ApplyBusPairs(_pendingSynthBusRestore);
            _pendingSynthBusRestore.Clear();
            return;
        }

        StartCoroutine(ApplyPendingSynthBusAfterPresetCoroutine());
    }

    private System.Collections.IEnumerator ApplyPendingSynthBusAfterPresetCoroutine()
    {
        // Give Helm/binder one more beat before reapplying the project's saved synth overrides.
        yield return null;
        yield return new WaitForEndOfFrame();
        yield return null;

        ApplyBusPairs(_pendingSynthBusRestore);
        _pendingSynthBusRestore.Clear();
    }

    private float TryGetProjectTempoBpm()
    {
        try
        {
            if (_app != null && _app.Bus != null && _app.Bus.TryGet("tempo", out var p))
                return Mathf.Max(1f, p.Value);
        }
        catch { }

        return 107f;
    }

    private static byte[] DecodeLegacyBytes(string base64, int size)
    {
        byte[] result = new byte[size];
        if (string.IsNullOrEmpty(base64)) return result;
        try
        {
            byte[] input = Convert.FromBase64String(base64);
            Array.Copy(input, result, Mathf.Min(input.Length, result.Length));
        }
        catch (FormatException) { }
        return result;
    }

    private static PatternData CaptureLegacyLiveFrame(ProjectData data)
    {
        var p = new PatternData();
        if (data.drums != null)
        {
            p.drumMask = DecodeLegacyBytes(data.drums.stepMaskB64, PatternData.Steps);
            p.drumVelocityData = DecodeLegacyBytes(data.drums.stepVelocityB64, PatternData.Steps * 2);
            p.sampleSteps = data.drums.sampleSteps ?? p.sampleSteps;
        }
        // The independent sample UI is authoritative where a saved grid exists.
        if (data.sampler != null && data.sampler.stepGrid != null)
            p.sampleSteps = data.sampler.stepGrid;
        if (data.seq != null)
        {
            p.seqSteps = data.seq.stepGrid ?? p.seqSteps;
            p.seqRuns = data.seq.runs ?? p.seqRuns;
        }
        if (data.pad != null)
        {
            p.padSteps = data.pad.stepGrid ?? p.padSteps;
            p.padRuns = data.pad.runs ?? p.padRuns;
            p.padChordMode = data.pad.chordMode;
        }
        return p;
    }

    private SongData BuildSongMirror(ProjectData data)
    {
        float bpm = TryGetProjectTempoBpm();
        // On an old project load, the bus has not yet been restored, so derive tempo
        // from the saved parameter list instead of whichever project was active.
        if (data.bus != null)
        {
            for (int i = 0; i < data.bus.Count; i++)
            {
                if (data.bus[i] != null && data.bus[i].id == "tempo")
                {
                    bpm = Mathf.Max(1f, data.bus[i].v);
                    break;
                }
            }
        }
        var options = new LegacySongConverter.Settings
        {
            title = data.projectName,
            bpm = bpm,
            kitIndex = data.drums != null ? data.drums.kitIndex : 0,
            synthPresetPath = data.synth != null ? data.synth.presetRelPath : null,
            synthPresetIndex = data.synth != null ? data.synth.presetIndex : 0,
            synthVolume = data.synth != null ? data.synth.masterVolume : -1f,
            padPresetPath = data.pad != null ? data.pad.presetRelPath : null,
            padPresetIndex = data.pad != null ? data.pad.presetIndex : 0,
            padVolume = data.pad != null ? data.pad.masterVolume : -1f,
            sampleResourcesPath = data.chops != null ? data.chops.resourcesPath : null,
            sliceStart01 = data.chops != null ? data.chops.sliceStart01 : null,
            sliceEnd01 = data.chops != null ? data.chops.sliceEnd01 : null
        };
        return LegacySongConverter.Convert(data.patterns, data.chain, CaptureLegacyLiveFrame(data), options);
    }

    /// <summary>
    /// Build a DAW playback preview directly from the currently edited legacy grids.
    /// This does not write a project file or change the saved song document.
    /// A real DAW-authored arrangement takes precedence over the legacy preview.
    /// </summary>
    public SongData GetSongForPlayback()
    {
        FindRuntimeRefs();
        // Authored Phase 3 songs are authoritative even when deliberately empty.
        // Historical Phase 2 native snapshots that were empty by accident still
        // fall back to the live pads: this preserves the Phase 2 capture fix.
        if (CurrentSong != null && (CurrentSong.arrangementAuthored ||
            (!CurrentSong.migratedFromLegacy && SongEventPlanner.HasEvents(CurrentSong))))
            return CurrentSong;

        var frame = new ProjectData { projectName = GetProjectDisplayName(CurrentSlot) };
        if (_app != null && _app.Bus != null)
        {
            foreach (var value in _app.Bus.All)
                frame.bus.Add(new BusPair { id = value.Id, v = value.Value });
        }
        if (_drums != null)
        {
            // Snapshot only: if the selected drum lane is visually populated but
            // the legacy mask was never synchronized, recover those lit cells for
            // playback without changing the actual pattern or saved project.
            byte[] mask = _drums.CaptureDrumMaskForDawPreview();
            byte[] velocity = _drums.CaptureDrumVelocityData();
            frame.drums.stepMaskB64 = mask == null ? "" : Convert.ToBase64String(mask);
            frame.drums.stepVelocityB64 = velocity == null ? "" : Convert.ToBase64String(velocity);
            frame.drums.sampleSteps = _drums.CaptureSampleStepsFlat();
            frame.drums.kitIndex = _drums.CaptureKitIndex();
        }
        if (_samplerUi != null)
            frame.sampler.stepGrid = _samplerUi.CaptureSampleStepsFlat();
        if (_keys != null)
        {
            frame.seq.stepGrid = _keys.CaptureSeqStepsFlat();
            frame.seq.runs = _keys.CaptureSeqRunsFlat();
        }
        if (_pad != null)
        {
            frame.pad.stepGrid = _pad.CapturePadStepsFlat();
            frame.pad.runs = _pad.CapturePadRunsFlat();
            frame.pad.chordMode = _pad.CaptureChordMode();
        }
        if (_padSynthRuntime != null)
        {
            frame.pad.presetRelPath = _padSynthRuntime.CurrentPresetRelPath;
            frame.pad.presetIndex = _padSynthRuntime.CurrentPresetIndex;
            frame.pad.masterVolume = _padSynthRuntime.CaptureMasterVolume();
        }
        if (_helm != null)
        {
            frame.synth.presetRelPath = _helm.CurrentPresetRelPath;
            frame.synth.presetIndex = _helm.CurrentPresetIndex;
        }
        if (_app != null && _app.Bus != null)
            frame.synth.masterVolume = Mathf.Clamp01(_app.Bus.GetValue("master.vol"));
        if (KMusicChopState.TryLoadApplied(out string resourcesPath, out var starts, out var ends))
        {
            frame.chops.resourcesPath = resourcesPath;
            frame.chops.sliceStart01 = starts;
            frame.chops.sliceEnd01 = ends;
        }

        // The current grid is the only authoritative source for a live preview.
        // Saving a pattern bank/chain slot is NOT required for auditioning DAW mode.
        frame.patterns = PatternBank.ExportAll();
        var chain = ChainState.LoadOrCreate();
        bool chainEnabled = chain != null && chain.enabled;

        // First compile the currently visible, edited grid. This is also a safe
        // fallback when the chain points at empty/stale saved patterns.
        frame.chain = null;
        SongData livePreview = BuildSongMirror(frame);
        SetLegacyPreviewLoop(livePreview);
        bool liveHasEvents = SongEventPlanner.HasEvents(livePreview);

        if (chainEnabled)
        {
            frame.chain = ChainStateSave.From(chain);
            SongData chainPreview = BuildSongMirror(frame);
            SetLegacyPreviewLoop(chainPreview);
            if (SongEventPlanner.HasEvents(chainPreview))
                return chainPreview;

            if (liveHasEvents)
            {
                Debug.LogWarning("[ZPunk DAW] Selected chain has no playable events; " +
                    "previewing the current live grid instead (no patterns were overwritten).");
                return livePreview;
            }
        }

        if (!liveHasEvents)
        {
            // Diagnostics are intentionally specific: if the user sees lit drum pads,
            // these values distinguish empty runtime state from stale chain contents.
            int drumHits = 0;
            byte[] masks = _drums != null ? _drums.CaptureDrumMask() : null;
            if (masks != null)
                foreach (byte mask in masks)
                    for (int lane = 0; lane < 8; ++lane)
                        if ((mask & (1 << lane)) != 0) ++drumHits;
            int synthSteps = CountFilled(frame.seq.stepGrid);
            int padSteps = CountFilled(frame.pad.stepGrid);
            int chopSteps = CountFilled(frame.sampler.stepGrid ?? frame.drums.sampleSteps);
            int visibleDrumPads = _drums != null ? _drums.CountVisibleDrumSteps() : -1;
            // Also include the actual compiled snapshot values, not just runtime
            // state, so future reports can distinguish capture from conversion.
            int previewDrumClips = 0;
            if (livePreview != null && livePreview.tracks != null)
                foreach (var track in livePreview.tracks)
                    if (track != null && track.kind == TrackKind.Drums && track.clips != null)
                        previewDrumClips += track.clips.Count;
            Debug.LogWarning($"[ZPunk DAW] LIVE CAPTURE EMPTY: drumHits={drumHits}, " +
                $"previewDrumClips={previewDrumClips}, " +
                $"visibleDrumPads={visibleDrumPads}, synthSteps={synthSteps}, " +
                $"padSteps={padSteps}, chopSteps={chopSteps}, chainEnabled={chainEnabled}, " +
                $"drumsBound={_drums != null}, keysBound={_keys != null}, padBound={_pad != null}. " +
                "If visibleDrumPads > 0 but drumHits = 0, the drum grid and sequencer state are out of sync.");
        }
        return livePreview;
    }

    private static void SetLegacyPreviewLoop(SongData song)
    {
        if (song == null) return;
        song.arrangementLoopEnabled = true;
        song.loopStartTick = 0;
        song.loopEndTick = song.lengthTicks;
    }

    private static int CountFilled(int[] steps)
    {
        int count = 0;
        if (steps != null)
            foreach (int step in steps)
                if (step > 0) ++count;
        return count;
    }

    public void SaveProject(int slot)
    {
        EnsureProjectsDir();
        FindRuntimeRefs();

        // Never overwrite an old or malformed project, even via SAVE from the UI.
        string existingPath = SlotPath(slot);
        if (File.Exists(existingPath))
        {
            try
            {
                var existing = JsonUtility.FromJson<ProjectData>(File.ReadAllText(existingPath));
                if (!IsNativeProject(existing))
                {
                    ShowProjectNotice("Cannot overwrite incompatible project " + slot.ToString("000") +
                        ". Open a new project slot instead. Existing data was preserved.");
                    return;
                }
            }
            catch (Exception)
            {
                ShowProjectNotice("Cannot overwrite an unreadable project. Existing file preserved.");
                return;
            }
        }

        if (_chainUi != null)
            _chainUi.FlushLiveStateToPattern();

        var data = new ProjectData
        {
            ver = ProjectVer,
            projectFormat = NativeFormat,
            slot = slot,
            utc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            projectName = slot == CurrentSlot ? GetProjectDisplayName(CurrentSlot) : GetProjectDisplayName(slot),
        };

        if (_app != null && _app.Bus != null)
        {
            foreach (var p in _app.Bus.All)
                data.bus.Add(new BusPair { id = p.Id, v = p.Value });
        }

        if (_drums != null)
        {
            var mask = _drums.CaptureDrumMask();
            var vel = _drums.CaptureDrumVelocityData();
            data.drums.stepMaskB64 = mask != null ? Convert.ToBase64String(mask) : "";
            data.drums.stepVelocityB64 = vel != null ? Convert.ToBase64String(vel) : "";
            data.drums.sampleSteps = _drums.CaptureSampleStepsFlat();
            data.drums.mutes = _drums.CaptureDrumMutes();
            data.drums.activeDrumId = _drums.CaptureActiveDrumId();
            data.drums.kitIndex = _drums.CaptureKitIndex();
        }

        if (_samplerUi != null)
            data.sampler.stepGrid = _samplerUi.CaptureSampleStepsFlat();

        if (_keys != null)
        {
            data.seq.stepGrid = _keys.CaptureSeqStepsFlat();
            data.seq.runs = _keys.CaptureSeqRunsFlat();
        }

        if (_pad != null)
        {
            data.pad.stepGrid = _pad.CapturePadStepsFlat();
            data.pad.runs = _pad.CapturePadRunsFlat();
            data.pad.chordMode = _pad.CaptureChordMode();
        }

        if (_padSynthRuntime != null)
        {
            data.pad.presetRelPath = _padSynthRuntime.CurrentPresetRelPath;
            data.pad.presetIndex = _padSynthRuntime.CurrentPresetIndex;
            data.pad.masterVolume = _padSynthRuntime.CaptureMasterVolume();
        }

        if (_helm != null)
        {
            data.synth.presetRelPath = _helm.CurrentPresetRelPath;
            data.synth.presetIndex = _helm.CurrentPresetIndex;
        }

        if (_app != null && _app.Bus != null)
            data.synth.masterVolume = Mathf.Clamp01(_app.Bus.GetValue("master.vol"));

        if (_player != null)
            data.player.lastTrackId = KMusicPlayerUI.CanonicalSampleId(_player.GetLastTrackId());

        if (KMusicChopState.TryLoadApplied(out string rp, out var ss, out var ee))
        {
            data.chops.resourcesPath = KMusicPlayerUI.CanonicalSampleId(rp);
            data.chops.sliceStart01 = ss;
            data.chops.sliceEnd01 = ee;
        }

        data.patterns = PatternBank.ExportAll();
        data.chain = ChainStateSave.From(ChainState.LoadOrCreate());

        // Non-destructive v1 -> v2 snapshot. Do not change the legacy playback path.
        // This is regenerated on each legacy UI save until the DAW editor becomes canonical.
        try
        {
            // A DAW-owned document must never be overwritten by a legacy pattern snapshot.
            // In groovebox mode, legacy grids are still authoritative and refresh the mirror.
            var songTransport = FindObjectOfType<ZPunkSongTransport>();
            if (CurrentSong != null && CurrentSong.schemaVersion == SongData.CurrentSchema &&
                (songTransport != null && songTransport.SongMode || !CurrentSong.migratedFromLegacy))
                data.song = CurrentSong;
            else
                data.song = BuildSongMirror(data);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[ZPunk DAW] Song mirror failed; legacy project will still save: " + e.Message);
            data.song = null;
        }

        try
        {
            if (data.song == null || data.song.schemaVersion != SongData.CurrentSchema)
                throw new InvalidOperationException("Cannot save a project without a valid ZPUNK 2.0 song.");
            // The current UI still captures some legacy step grids, but the saved
            // format is always an explicitly native, editable 2.0 arrangement.
            data.song.migratedFromLegacy = false;
            data.song.arrangementAuthored = true;
            if (data.song.tracks != null && data.song.tracks.Count > 4 &&
                data.song.tracks[4] != null && data.song.tracks[4].instrument != null)
                data.song.tracks[4].instrument.sampleResourcesPath =
                    KMusicPlayerUI.CanonicalSampleId(data.song.tracks[4].instrument.sampleResourcesPath);
            string jsonPath = SlotPath(slot);
            // Write a temporary file first, then replace: interrupted saves must
            // not truncate the previous project. No sample/export files touched.
            string tempPath = jsonPath + ".writing";
            File.WriteAllText(tempPath, JsonUtility.ToJson(data));
            if (File.Exists(jsonPath))
            {
                string backup = jsonPath + ".bak";
                File.Copy(jsonPath, backup, true);
                File.Delete(jsonPath);
            }
            File.Move(tempPath, jsonPath);
            CurrentSong = data.song;
            KMusicMidiExporter.ExportProjectMidiBundle(jsonPath, data, TryGetProjectTempoBpm());
            _cachedProjectNames[slot] = data.projectName;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[Project] Save failed: " + e.Message);
        }
    }
    public void LoadProject(int slot, bool silent = false)
    {
        EnsureProjectsDir();
        FindRuntimeRefs();

        string path = SlotPath(slot);
        if (!File.Exists(path))
        {
            if (!silent) Debug.LogWarning("[Project] No project file for slot " + slot);
            return;
        }

        ProjectData data;
        try
        {
            data = JsonUtility.FromJson<ProjectData>(File.ReadAllText(path));
        }
        catch (Exception e)
        {
            Debug.LogWarning("[Project] Load failed: " + e.Message);
            StartFreshAfterIncompatible(slot);
            return;
        }

        if (!IsNativeProject(data))
        {
            StartFreshAfterIncompatible(slot);
            return;
        }

        var dawTransport = FindObjectOfType<ZPunkSongTransport>();
        if (dawTransport != null && dawTransport.IsPlaying) dawTransport.StopSong();

        // Version validation completed before changing any playback or pref state.
        CurrentSong = data.song;

        if (_chainUi != null)
            _chainUi.SetSuspendLivePatternWrites(true);

        if (_drums != null) _drums.SetAllowSaving(false);
        if (_samplerUi != null) _samplerUi.SetAllowSaving(false);
        if (_keys != null) _keys.SetAllowSaving(false);
        if (_pad != null) _pad.SetAllowSaving(false);

        try
        {
            if (_drums != null && _drums.IsPlaying)
                _drums.StopPlayback();

            if (_drums != null)
            {
                _drums.ApplyDrumMask(null);
                _drums.ApplySampleStepsFlat(null);
                _drums.ApplyDrumMutes(null);
            }

            if (_samplerUi != null)
                _samplerUi.ApplySampleStepsFlat(null);

            if (_keys != null)
            {
                _keys.ApplySeqStepsFlat(null);
                _keys.RebuildSynthSequenceNow();
            }

            if (_pad != null)
            {
                _pad.ApplyPadStepsFlat(null);
                _pad.RebuildPadSequenceNow();
            }

            KMusicChopState.ClearApplied();

            if (_player != null)
            {
                _player.ClearLastTrackId();
                _player.RefreshAppliedMarkersFromState();
            }

            _pendingSynthBusRestore.Clear();
            if (_helm != null)
                _helm.PresetApplied -= OnProjectPresetApplied;

            if (_app != null && _app.Bus != null && data.bus != null)
            {
                var nonSynthBus = new List<BusPair>();

                for (int i = 0; i < data.bus.Count; i++)
                {
                    var it = data.bus[i];
                    if (it == null || string.IsNullOrEmpty(it.id)) continue;

                    if (IsSynthBusId(it.id))
                        _pendingSynthBusRestore.Add(it);
                    else
                        nonSynthBus.Add(it);
                }

                ApplyBusPairs(nonSynthBus);
            }

            if (data.patterns != null)
                PatternBank.ImportAll(data.patterns);

            if (data.chain != null)
                data.chain.ApplyToPrefs();

            if (_drums != null && data.drums != null)
            {
                byte[] mask = null;
                byte[] vel = null;
                try
                {
                    if (!string.IsNullOrEmpty(data.drums.stepMaskB64))
                        mask = Convert.FromBase64String(data.drums.stepMaskB64);
                }
                catch
                {
                    mask = null;
                }

                try
                {
                    if (!string.IsNullOrEmpty(data.drums.stepVelocityB64))
                        vel = Convert.FromBase64String(data.drums.stepVelocityB64);
                }
                catch
                {
                    vel = null;
                }

                _drums.ApplyDrumMask(mask);
                _drums.ApplyDrumVelocityData(vel);
                _drums.ApplySampleStepsFlat(data.drums.sampleSteps);
                _drums.ApplyDrumMutes(data.drums.mutes);
                _drums.ApplyKitIndex(data.drums.kitIndex);
            }

            if (_samplerUi != null && data.sampler != null)
                _samplerUi.ApplySampleStepsFlat(data.sampler.stepGrid);

            if (_keys != null && data.seq != null)
            {
                _keys.ApplySeqStepsFlat(data.seq.stepGrid, data.seq.runs);
                _keys.RebuildSynthSequenceNow();
            }

            if (_pad != null && data.pad != null)
            {
                _pad.ApplyPadStepsFlat(data.pad.stepGrid, data.pad.runs);
                _pad.ApplyChordMode(data.pad.chordMode);
                _pad.RebuildPadSequenceNow();
            }

            if (_padSynthRuntime != null && data.pad != null)
                _padSynthRuntime.ApplyPresetChoice(data.pad.presetIndex, data.pad.presetRelPath);

            if (_helm != null && data.synth != null)
            {
                if (_pendingSynthBusRestore.Count > 0)
                    _helm.PresetApplied += OnProjectPresetApplied;

                _helm.ApplyPresetChoice(data.synth.presetIndex, data.synth.presetRelPath);
            }
            else
            {
                ApplyBusPairs(_pendingSynthBusRestore);
                _pendingSynthBusRestore.Clear();
            }

            string chopsSampleId = data.chops != null ? data.chops.resourcesPath : null;
            if (string.IsNullOrEmpty(chopsSampleId) && CurrentSong.tracks != null &&
                CurrentSong.tracks.Count > 4 && CurrentSong.tracks[4].instrument != null)
                chopsSampleId = CurrentSong.tracks[4].instrument.sampleResourcesPath;
            chopsSampleId = KMusicPlayerUI.CanonicalSampleId(chopsSampleId);
            if (!string.IsNullOrEmpty(chopsSampleId))
                KMusicChopState.SetAppliedRaw(chopsSampleId,
                    data.chops != null ? data.chops.sliceStart01 : CurrentSong.tracks[4].instrument.sliceStart01,
                    data.chops != null ? data.chops.sliceEnd01 : CurrentSong.tracks[4].instrument.sliceEnd01);
            else
                KMusicChopState.ClearApplied();

            if (_player != null)
            {
                // An applied CHOPS sample takes precedence over a previously browsed
                // preview track. Never restore a mismatched sample into a project.
                string projectSample = !string.IsNullOrEmpty(chopsSampleId) ? chopsSampleId :
                    (data.player != null ? data.player.lastTrackId : null);
                _player.SetLastTrackId(projectSample);
                _player.RefreshAppliedMarkersFromState();
            }

            if (_chainUi != null)
                _chainUi.ReloadFromSaved();

            float mainSynthVol = (data.synth != null) ? data.synth.masterVolume : -1f;
            float padSynthVol = (data.pad != null) ? data.pad.masterVolume : -1f;
            StartCoroutine(ApplySavedSynthVolumesAfterLoad(mainSynthVol, padSynthVol));
        }
        catch (Exception e)
        {
            // Corrupt runtime state is never allowed to crash project switching.
            // Keep the on-disk project intact for a future diagnostic or backup.
            Debug.LogError("[Project] Could not restore project " + slot + ": " + e);
            StartFreshAfterIncompatible(slot);
        }
        finally
        {
            if (_chainUi != null)
                _chainUi.SetSuspendLivePatternWrites(false);

            if (_drums != null) _drums.SetAllowSaving(true);
            if (_samplerUi != null) _samplerUi.SetAllowSaving(true);
            if (_keys != null) _keys.SetAllowSaving(true);
            if (_pad != null) _pad.SetAllowSaving(true);
        }
    }


private System.Collections.IEnumerator ApplySavedSynthVolumesAfterLoad(float mainSynthVolume, float padSynthVolume)
{
    yield return null;
    yield return new WaitForEndOfFrame();
    yield return null;

    if (_app != null && _app.Bus != null && mainSynthVolume >= 0f)
        _app.Bus.SetValue("master.vol", Mathf.Clamp01(mainSynthVolume));

    if (_padSynthRuntime != null && padSynthVolume >= 0f)
        _padSynthRuntime.ApplyMasterVolume(padSynthVolume);
}

    private void ResetToBlank()
    {
        CurrentSong = null;
        if (_drums != null)
        {
            _drums.ApplyDrumMask(null);
            _drums.ApplySampleStepsFlat(null);
            _drums.ApplyDrumMutes(null);
        }

        if (_samplerUi != null)
            _samplerUi.ApplySampleStepsFlat(null);

        if (_keys != null)
        {
            _keys.ApplySeqStepsFlat(null);
            _keys.RebuildSynthSequenceNow();
        }

        if (_pad != null)
        {
            _pad.ApplyPadStepsFlat(null);
            _pad.ApplyChordMode(0);
            _pad.RebuildSequenceNow();
        }

        PatternBank.ResetAll();
        new ChainState().Save();
        if (_chainUi != null) _chainUi.ReloadFromSaved();

        KMusicChopState.ClearApplied();

        if (_player != null)
        {
            _player.ClearLastTrackId();
            _player.ClearSampleSelection();
            _player.RefreshAppliedMarkersFromState();
        }
    }
}
