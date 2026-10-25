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

    // Native Helm snapshots are saved AFTER a preset finishes loading.
    // The older BusPair dumps cannot be trusted for sound restoration: they can
    // represent stale UI/default values rather than the actual patch.
    private readonly List<NativeSoundValue> _pendingSynthNativeRestore = new();
    private readonly List<NativeSoundValue> _pendingPadNativeRestore = new();
    private float _pendingSynthMasterVolume = -1f;
    private float _pendingPadMasterVolume = -1f;
    private Coroutine _pendingSynthRestoreJob;
    private Coroutine _pendingPadRestoreJob;
    private int _projectLoadRevision;
    private string _expectedSynthPresetPath;
    private string _expectedPadPresetPath;

    private KMusicPadSynthRuntime _padSynthRuntime;
    private ZPunkInstrumentRack _rack;
    private readonly List<NativeSoundValue> _pendingBassNativeRestore = new();
    private string _expectedBassPresetPath;
    private Coroutine _pendingBassRestoreJob;

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
        if (SlotHasSavedData(CurrentSlot))
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
        if (_rack == null) _rack = FindObjectOfType<ZPunkInstrumentRack>();
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
        _modal.style.backgroundColor = new Color(.012f, .023f, .040f, .95f);
        _modal.style.alignItems = Align.Center;
        _modal.style.justifyContent = Justify.Center;
        _modal.style.paddingLeft = 12;
        _modal.style.paddingRight = 12;
        _modal.style.paddingTop = 12;
        _modal.style.paddingBottom = 12;
        _modal.pickingMode = PickingMode.Position;

        var card = new VisualElement { name = "ZPunkProjectCard" };
        card.AddToClassList("km-project-card");
        ZPunkOverlaySkin.Card(card, 600, 840);

        var section = new Label("FILES  /  PROJECTS");
        section.style.fontSize = 11;
        section.style.color = ZPunkOverlaySkin.Accent;
        section.style.marginBottom = 5;
        section.style.flexShrink = 0;
        card.Add(section);

        _modalTitle = new Label("PROJECT LIBRARY");
        _modalTitle.AddToClassList("km-project-title");
        ZPunkOverlaySkin.Heading(_modalTitle);
        _modalTitle.style.marginBottom = 5;
        card.Add(_modalTitle);

        var subtitle = new Label("Manage saved songs and project slots");
        subtitle.style.fontSize = 12;
        subtitle.style.color = ZPunkOverlaySkin.Muted;
        subtitle.style.marginBottom = 12;
        subtitle.style.flexShrink = 0;
        card.Add(subtitle);

        var row = new VisualElement();
        row.AddToClassList("km-project-toolbar");
        row.style.flexDirection = FlexDirection.Row;
        row.style.justifyContent = Justify.SpaceBetween;
        row.style.alignItems = Align.Center;
        row.style.marginBottom = 8;
        row.style.minHeight = 40;

        var saveBtn = new Button(() => { SaveProject(CurrentSlot); HideModal(); }) { text = "SAVE CURRENT" };
        saveBtn.AddToClassList("km-project-toolbar-btn");
        ZPunkOverlaySkin.Button(saveBtn, true);
        saveBtn.style.flexGrow = 1;
        saveBtn.style.minWidth = 0;
        saveBtn.style.marginRight = 6;
        row.Add(saveBtn);

        var closeBtn = new Button(HideModal) { text = "CLOSE" };
        closeBtn.AddToClassList("km-project-toolbar-btn");
        ZPunkOverlaySkin.Button(closeBtn);
        closeBtn.style.flexGrow = 1;
        closeBtn.style.minWidth = 0;
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
        card.Add(ZPunkOverlaySkin.ScrollWithRail(_modalList));

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
        if (SlotHasSavedData(next))
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
        if (SlotHasSavedData(next))
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
            if (!SlotHasSavedData(slot)) return slot;
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

    private bool SlotHasSavedData(int slot)
    {
        string path = SlotPath(slot);
        return File.Exists(path) || File.Exists(path + ".bak") ||
            File.Exists(path + ".rollback") || File.Exists(path + ".writing");
    }

    private static bool TryReadNativeProject(string path, out ProjectData data)
    {
        data = null;
        if (!File.Exists(path)) return false;
        try
        {
            var parsed = JsonUtility.FromJson<ProjectData>(File.ReadAllText(path));
            if (!IsNativeProject(parsed)) return false;
            data = parsed;
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[Project] Cannot read '" + Path.GetFileName(path) + "': " + e.Message);
            return false;
        }
    }

    // Never initialize an empty slot while a recoverable .bak/.rollback exists.
    // Preserve any damaged primary under a distinct filename for diagnostics.
    private bool TryReadOrRecoverProject(int slot, out ProjectData data)
    {
        string path = SlotPath(slot);
        if (TryReadNativeProject(path, out data)) return true;
        string[] candidates = { path + ".rollback", path + ".bak", path + ".writing" };
        foreach (string candidate in candidates)
        {
            if (!TryReadNativeProject(candidate, out var recovered)) continue;
            try
            {
                if (File.Exists(path))
                {
                    string damaged = path + ".unreadable-" +
                        DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" +
                        Guid.NewGuid().ToString("N").Substring(0, 8);
                    File.Move(path, damaged);
                }
                File.Copy(candidate, path, false);
                Debug.LogWarning("[Project] Restored slot " + slot + " from " +
                    Path.GetFileName(candidate));
                ShowProjectNotice("Recovered PROJECT " + slot.ToString("000") +
                    " from a previous valid save. Any unreadable file was preserved.");
                data = recovered;
                return true;
            }
            catch (Exception e)
            {
                // The valid backup remains readable even if repair cannot
                // replace the primary (e.g., storage is read-only/full).
                Debug.LogWarning("[Project] Recovery copy failed: " + e.Message);
                ShowProjectNotice("PROJECT " + slot.ToString("000") +
                    " loaded from backup. Could not restore the primary file.");
                data = recovered;
                return true;
            }
        }
        data = null;
        return false;
    }

    // Both SAVE and RENAME use the same validated, staged write. Prefer
    // File.Replace (atomic on supported filesystems) with a recoverable
    // rollback fallback for mobile runtimes where Replace is unsupported.
    private static void WriteNativeProjectSafely(string path, ProjectData data)
    {
        if (data == null || !IsNativeProject(data))
            throw new InvalidOperationException("Refusing to write an invalid project.");
        string temp = path + ".writing";
        string backup = path + ".bak";
        string rollback = path + ".rollback";
        string json = JsonUtility.ToJson(data);
        try
        {
            // The temp file is truncated only after checking the primary
            // exists and is compatible. It is never the live project file.
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(true);
            }
            if (!TryReadNativeProject(temp, out _))
                throw new InvalidDataException("Staged project failed JSON validation.");
            if (!File.Exists(path))
            {
                File.Move(temp, path);
                return;
            }
            if (!TryReadNativeProject(path, out _))
                throw new InvalidDataException("Existing project is unreadable; it was preserved.");
            try
            {
                File.Replace(temp, path, backup);
                return;
            }
            catch (PlatformNotSupportedException)
            {
                Debug.Log("[Project] File.Replace unsupported; using recoverable move.");
            }
            catch (IOException e)
            {
                Debug.LogWarning("[Project] File.Replace unavailable: " + e.Message);
            }
            // Fallback: at every point there is either the original JSON or
            // the original .rollback. LoadProject knows how to recover it.
            if (File.Exists(rollback)) File.Delete(rollback);
            File.Move(path, rollback);
            try { File.Move(temp, path); }
            catch
            {
                if (!File.Exists(path) && File.Exists(rollback))
                    File.Move(rollback, path);
                throw;
            }
            try
            {
                File.Copy(rollback, backup, true);
                File.Delete(rollback);
            }
            catch (Exception e)
            {
                // Retain rollback if we could not successfully write a .bak.
                Debug.LogWarning("[Project] Backup refresh incomplete: " + e.Message);
            }
        }
        finally
        {
            // A failed staged write may be investigated or automatically
            // recovered on next startup; only delete a temp after commit.
            if (File.Exists(path) && TryReadNativeProject(path, out _) &&
                File.Exists(temp))
            {
                try { File.Delete(temp); } catch (Exception) { }
            }
        }
    }

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
        string[] candidates = { path, path + ".rollback", path + ".bak", path + ".writing" };
        foreach (string candidate in candidates)
        {
            if (!TryReadNativeProject(candidate, out var data)) continue;
            string value = data.projectName;
            _cachedProjectNames[slot] = value;
            return value;
        }
        return null;
    }

    private bool RenameProject(int slot, string newName)
    {
        string clean = string.IsNullOrWhiteSpace(newName) ? DefaultProjectName(slot) : newName.Trim();
        string path = SlotPath(slot);
        if (!TryReadOrRecoverProject(slot, out var data)) return false;

        try
        {

            data.projectName = clean;
            if (string.IsNullOrEmpty(data.projectId)) data.projectId = Guid.NewGuid().ToString("N");
            if (data.song != null) data.song.title = clean;
            if (data.slot <= 0 && slot != 0)
                data.slot = slot;
            if (data.ver <= 0)
                data.ver = ProjectVer;
            if (data.utc <= 0)
                data.utc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            WriteNativeProjectSafely(path, data);
            _cachedProjectNames[slot] = clean;
            if (slot == CurrentSlot && CurrentSong != null)
                CurrentSong.title = clean;

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
        ApplyProjectSafeArea();
        BuildProjectList();
        _modal.style.display = DisplayStyle.Flex;
    }

    private void ApplyProjectSafeArea()
    {
        if (_modal == null || doc == null || doc.rootVisualElement == null ||
            Screen.width <= 0 || Screen.height <= 0) return;
        var root = doc.rootVisualElement;
        float width = root.resolvedStyle.width;
        float height = root.resolvedStyle.height;
        if (float.IsNaN(width) || width <= 1) width = Screen.width;
        if (float.IsNaN(height) || height <= 1) height = Screen.height;
        Rect safe = Screen.safeArea;
        float sx = width / Screen.width;
        float sy = height / Screen.height;
        _modal.style.left = 6f + safe.xMin * sx;
        _modal.style.right = 6f + (Screen.width - safe.xMax) * sx;
        _modal.style.top = 6f + (Screen.height - safe.yMax) * sy;
        _modal.style.bottom = 6f + safe.yMin * sy;
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
                // Show slots with only a recovery artifact, too. Otherwise a
                // crash during fallback commit could hide a valid project.
                foreach (var f in Directory.GetFiles(ProjectsDir, "project_*.json*"))
                {
                    var name = Path.GetFileName(f);
                    if (name.Length < 16 || !name.StartsWith("project_", StringComparison.Ordinal))
                        continue;
                    if (!int.TryParse(name.Substring(8, 3), out int slot)) continue;
                    string tail = name.Substring(11);
                    if (tail == ".json" || tail == ".json.bak" ||
                        tail == ".json.rollback" || tail == ".json.writing")
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
            bool has = SlotHasSavedData(slot);
            bool compatible = TryReadNativeProject(path, out _) ||
                TryReadNativeProject(path + ".rollback", out _) ||
                TryReadNativeProject(path + ".bak", out _) ||
                TryReadNativeProject(path + ".writing", out _);

            var row = new VisualElement();
            row.AddToClassList("km-project-row");
            if (slot == CurrentSlot)
                row.AddToClassList("km-project-row--current");
            row.style.flexDirection = FlexDirection.Column;
            row.style.width = Length.Percent(100);
            row.style.minWidth = 0;
            row.style.flexShrink = 0;
            row.style.marginBottom = 8;
            row.style.paddingLeft = 11;
            row.style.paddingRight = 11;
            row.style.paddingTop = 12;
            row.style.paddingBottom = 11;
            row.style.borderLeftWidth = slot == CurrentSlot ? 3 : 1;
            row.style.borderLeftColor = slot == CurrentSlot ? ZPunkOverlaySkin.Accent : ZPunkOverlaySkin.Line;
            row.style.borderTopLeftRadius = 7;
            row.style.borderTopRightRadius = 7;
            row.style.borderBottomLeftRadius = 7;
            row.style.borderBottomRightRadius = 7;
            row.style.backgroundColor = slot == CurrentSlot ?
                new Color(.105f,.212f,.225f,1f) : ZPunkOverlaySkin.Panel;

            var headerRow = new VisualElement();
            headerRow.AddToClassList("km-project-row-header");
            headerRow.style.flexDirection = FlexDirection.Row;
            headerRow.style.justifyContent = Justify.SpaceBetween;
            headerRow.style.alignItems = Align.Center;
            headerRow.style.minWidth = 0;
            row.Add(headerRow);

            var slotLabel = new Label(GetProjectDisplayName(slot));
            slotLabel.AddToClassList("km-project-slot");
            slotLabel.style.fontSize=15;
            slotLabel.style.flexGrow = 1;
            slotLabel.style.minWidth = 0;
            slotLabel.style.whiteSpace = WhiteSpace.Normal;
            slotLabel.style.color=new Color(.92f,.97f,.99f);
            slotLabel.style.unityFontStyleAndWeight=FontStyle.Bold;
            headerRow.Add(slotLabel);
            var state = new Label(slot == CurrentSlot ? "CURRENT" : !has ? "EMPTY" : !compatible ? "LEGACY" : "SAVED");
            state.style.fontSize = 10;
            state.style.color = slot == CurrentSlot ? ZPunkOverlaySkin.Accent : ZPunkOverlaySkin.Muted;
            state.style.marginLeft = 8;
            state.style.flexShrink = 0;
            headerRow.Add(state);

            string desc;
            if (!has)
                desc = $"Project {slot:000} - Empty slot ready for a new project.";
            else if (!compatible)
                desc = $"Project {slot:000} - Older/incompatible file, preserved. Load starts a fresh workspace.";
            else if (slot == CurrentSlot)
                desc = $"Project {slot:000} - Current project. You can rename, save, or delete it.";
            else
                desc = $"Project {slot:000} - Ready to load or delete.";

            var label = new Label(desc);
            label.AddToClassList("km-project-name");
            label.style.fontSize=12;
            label.style.marginTop=4;
            label.style.color=ZPunkOverlaySkin.Muted;
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
                    // Dark, legible input styling also applies to the inner Unity text field.
                    ZPunkOverlaySkin.TextInput(renameField);
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
                    ZPunkOverlaySkin.Button(btnRename);
                    btnRename.style.flexShrink = 0;
                    btnRename.style.minWidth = 82;
                    btnRename.style.width = 82;
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
                ZPunkOverlaySkin.Button(btnLoad, true, false);
                btnLoad.style.flexGrow = 1;
                btnLoad.style.minWidth = 0;
                btnLoad.style.flexShrink = 1;
                btnLoad.style.marginRight = 6;
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
                ZPunkOverlaySkin.Button(btnNoSave, false, false);
                btnNoSave.style.flexGrow = 1;
                btnNoSave.style.minWidth = 0;
                btnNoSave.style.flexShrink = 1;
                btnNoSave.style.marginRight = 6;
                btnRow.Add(btnNoSave);

                var btnDelete = new Button(() =>
                {
                    DeleteProject(slot);
                })
                { text = "DELETE" };
                btnDelete.AddToClassList("km-pillbtn");
                btnDelete.AddToClassList("km-pillbtn--danger");
                btnDelete.AddToClassList("km-project-action-btn");
                ZPunkOverlaySkin.Button(btnDelete, false, true);
                btnDelete.style.flexGrow = 1;
                btnDelete.style.minWidth = 0;
                btnDelete.style.flexShrink = 1;
                btnDelete.style.marginRight = 0;
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
                ZPunkOverlaySkin.Button(btnCreate, true, false);
                btnCreate.style.flexGrow = 1;
                btnCreate.style.minWidth = 0;
                btnCreate.style.flexShrink = 1;
                btnCreate.style.marginRight = 0;
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
            if (File.Exists(path)) File.Delete(path);
            foreach (string suffix in new[] { ".bak", ".rollback", ".writing" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
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
        // Persistent identity unrelated to slot number or mutable display name.
        public string projectId;

        public List<BusPair> bus = new();
        public DrumState drums = new();
        public SampleState sampler = new();
        public SeqState seq = new();
        public PadState pad = new();
        public SynthState synth = new();
        public BassState bass = new();
        public PlayerState player = new();
        public ChopState chops = new();
        // Optional in older 2.x saves; restored only for the matching sample.
        public KMusicPlayerUI.WarpProjectState warp;
        public PatternBank.PatternBankSave patterns = new();
        public ChainStateSave chain = new();
        // ZPunk 2.0 song snapshot. Legacy fields remain the source of truth in phase 1.
        public SongData song;
    }

    [Serializable] private class BusPair { public string id; public float v; }

    [Serializable]
    private class NativeSoundValue
    {
        public int param;
        public float normalized;
    }

    // Capture the sound that ACTUALLY plays, not cached control values. A full
    // parameter snapshot preserves user sound edits made on the SOUND page,
    // including distortion, envelopes, filters, modulation and FX switches.
    // Modulations themselves remain owned by the original .helm preset.
    private static List<NativeSoundValue> CaptureNativeSound(AudioHelm.HelmController controller)
    {
        var result = new List<NativeSoundValue>();
        if (controller == null) return result;
        foreach (AudioHelm.Param param in Enum.GetValues(typeof(AudioHelm.Param)))
        {
            if (param == AudioHelm.Param.kNone) continue;
            try
            {
                float n = controller.GetParameterPercent(param);
                if (!float.IsNaN(n) && !float.IsInfinity(n) && n >= 0f && n <= 1f)
                    result.Add(new NativeSoundValue { param = (int)param, normalized = n });
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Project] Skipped native sound parameter " + param + ": " + ex.Message);
            }
        }
        // Merge explicit SOUND gestures after the native scan. Some AudioHelm
        // builds report a stale percent immediately after a SetParameter* call.
        // User edits win only for this preset/controller; the preset loaders
        // discard the cache whenever a different factory patch is selected.
        foreach (var edit in ZPunkSoundUI.CaptureLiveEdits(controller))
        {
            var item = result.Find(x => x.param == (int)edit.Key);
            if (item != null) item.normalized = edit.Value;
            else result.Add(new NativeSoundValue { param = (int)edit.Key, normalized = edit.Value });
        }
        return result;
    }

    private static void ApplyNativeSound(AudioHelm.HelmController controller, List<NativeSoundValue> snapshot)
    {
        if (controller == null || snapshot == null) return;
        foreach (var value in snapshot)
        {
            if (value == null || value.param <= 0 ||
                !Enum.IsDefined(typeof(AudioHelm.Param), value.param) ||
                float.IsNaN(value.normalized) || float.IsInfinity(value.normalized) ||
                value.normalized < 0f || value.normalized > 1f)
                continue;
            try
            {
                var param = (AudioHelm.Param)value.param;
                if (param == AudioHelm.Param.kDistortionOn ||
                    param == AudioHelm.Param.kFilterOn ||
                    param == AudioHelm.Param.kDelayOn ||
                    param == AudioHelm.Param.kReverbOn ||
                    param == AudioHelm.Param.kArpOn)
                    controller.SetParameterValue(param, value.normalized >= 0.5f ? 1f : 0f);
                else if (param == AudioHelm.Param.kDistortionType)
                    controller.SetParameterValue(param, Mathf.RoundToInt(value.normalized * 3f));
                else
                    controller.SetParameterPercent(param, value.normalized);
                // A restored project is now the working sound. Retain its
                // snapshot for the next SAVE even if native read-back lags.
                ZPunkSoundUI.RecordLiveEdit(controller, param, value.normalized);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Project] Couldn't restore native parameter " + value.param + ": " + ex.Message);
            }
        }
    }

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
        // Deprecated legacy UI snapshot; do not replay onto a native Helm patch.
        public List<BusPair> soundBus = new();
        public List<NativeSoundValue> nativeSound = new();
    }

    [Serializable]
    private class BassState
    {
        public string presetRelPath;
        public List<NativeSoundValue> nativeSound = new();
    }

    [Serializable] private class SynthState
    {
        public string presetRelPath;
        public int presetIndex;
        public float masterVolume = -1f;
        public List<NativeSoundValue> nativeSound = new();
    }
    [Serializable] private class PlayerState { public string lastTrackId; }
    [Serializable] private class ChopState
    {
        public string resourcesPath;
        public float[] sliceStart01;
        public float[] sliceEnd01;
        public float[] markerPositions01;
        public bool lastMarkerIsEndOnly;
    }

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
            || id.StartsWith("arp.", StringComparison.OrdinalIgnoreCase)
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

    private static bool PresetMatches(string expected, string loaded)
    {
        return string.IsNullOrEmpty(expected) ||
            string.Equals(expected, loaded, StringComparison.OrdinalIgnoreCase);
    }

    private void OnProjectPresetApplied(int _, string path)
    {
        // A startup auto-load can complete before the project-selected preset.
        // Ignore that unrelated completion without discarding the saved sound.
        if (!PresetMatches(_expectedSynthPresetPath, path)) return;
        if (_helm != null) _helm.PresetApplied -= OnProjectPresetApplied;
        if (_pendingSynthRestoreJob != null) StopCoroutine(_pendingSynthRestoreJob);
        _pendingSynthRestoreJob = StartCoroutine(ApplyPendingSynthSoundAfterPresetCoroutine(
            _projectLoadRevision, path));
    }

    private System.Collections.IEnumerator ApplyPendingSynthSoundAfterPresetCoroutine(int revision, string path)
    {
        yield return null;
        yield return new WaitForEndOfFrame();
        if (revision != _projectLoadRevision || _helm == null ||
            !PresetMatches(path, _helm.CurrentPresetRelPath)) yield break;

        // DO NOT reapply old ProjectData.bus synth values here. That old dump
        // routinely reintroduces distortion / wrong oscillator and filter values.
        var controller = _helm.ActiveHelmController;
        ApplyNativeSound(controller, _pendingSynthNativeRestore);
        // A legacy project's cached 0 volume may have been captured before
        // Helm initialized. Don't turn an otherwise good preset silent on load.
        if (controller != null && _pendingSynthNativeRestore.Count == 0 &&
            _pendingSynthMasterVolume > 0f)
            controller.SetParameterPercent(AudioHelm.Param.kVolume, Mathf.Clamp01(_pendingSynthMasterVolume));

        var binder = FindObjectOfType<KMusicHelmSynthBinder>();
        if (binder != null)
            binder.RequestPullFromHelm();
        Debug.Log($"[ZPUNK Project] Synth '{path}' restored; native parameters={_pendingSynthNativeRestore.Count} (zero means legacy preset only).");
        _pendingSynthNativeRestore.Clear();
        _pendingSynthRestoreJob = null;
    }

    private void OnProjectPadPresetApplied(int _, string path)
    {
        if (!PresetMatches(_expectedPadPresetPath, path)) return;
        if (_padSynthRuntime != null)
            _padSynthRuntime.PresetApplied -= OnProjectPadPresetApplied;
        if (_pendingPadRestoreJob != null) StopCoroutine(_pendingPadRestoreJob);
        _pendingPadRestoreJob = StartCoroutine(ApplyPendingPadSoundAfterPresetCoroutine(
            _projectLoadRevision, path));
    }

    private System.Collections.IEnumerator ApplyPendingPadSoundAfterPresetCoroutine(int revision, string path)
    {
        yield return null;
        yield return new WaitForEndOfFrame();
        if (revision != _projectLoadRevision || _padSynthRuntime == null ||
            !PresetMatches(path, _padSynthRuntime.CurrentPresetRelPath)) yield break;

        var controller = _padSynthRuntime.ActiveHelmController;
        ApplyNativeSound(controller, _pendingPadNativeRestore);
        if (controller != null && _pendingPadNativeRestore.Count == 0 &&
            _pendingPadMasterVolume > 0f)
            controller.SetParameterPercent(AudioHelm.Param.kVolume, Mathf.Clamp01(_pendingPadMasterVolume));
        _padSynthRuntime.RequestPullFromHelm();
        Debug.Log($"[ZPUNK Project] Pads '{path}' restored; native parameters={_pendingPadNativeRestore.Count} (zero means legacy preset only).");
        _pendingPadNativeRestore.Clear();
        _pendingPadRestoreJob = null;
    }

    private System.Collections.IEnumerator RestoreBassSoundAfterPresetCoroutine(int revision, string path)
    {
        // The instrument rack is created after the project manager on cold
        // startup. Wait for its preset index and native Helm channel rather
        // than applying an edited sound to a not-yet-initialized controller.
        float timeout = Time.realtimeSinceStartup + 15f;
        bool requested = false;
        while (revision == _projectLoadRevision && Time.realtimeSinceStartup < timeout)
        {
            if (_rack == null) _rack = FindObjectOfType<ZPunkInstrumentRack>();
            if (_rack != null)
            {
                if (string.IsNullOrEmpty(path))
                {
                    if (_rack.BassPresetsReady) break;
                }
                else
                {
                    if (!requested) requested = _rack.RequestProjectBassPreset(path);
                    if (requested && _rack.BassPresetsReady &&
                        string.Equals(path, _rack.CurrentBassPresetRelPath, StringComparison.OrdinalIgnoreCase))
                        break;
                }
            }
            yield return null;
        }
        if (revision != _projectLoadRevision) yield break;
        if (_rack == null || !_rack.BassPresetsReady ||
            (!string.IsNullOrEmpty(path) &&
             !string.Equals(path, _rack.CurrentBassPresetRelPath, StringComparison.OrdinalIgnoreCase)))
        {
            Debug.LogWarning("[ZPUNK Project] Bass preset was not ready; skipped sound restore to avoid overwriting the wrong patch.");
            _pendingBassRestoreJob = null;
            yield break;
        }
        yield return null;
        yield return new WaitForEndOfFrame();
        if (revision != _projectLoadRevision) yield break;
        ApplyNativeSound(_rack.BassController, _pendingBassNativeRestore);
        Debug.Log($"[ZPUNK Project] Bass '{_rack.CurrentBassPresetRelPath}' restored; native parameters={_pendingBassNativeRestore.Count}.");
        _pendingBassNativeRestore.Clear();
        _pendingBassRestoreJob = null;
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

    // Preserve the existing void signature for scene bindings and legacy callers.
    public void SaveProject(int slot) { TrySaveProject(slot); }

    public bool TrySaveProject(int slot)
    {
        EnsureProjectsDir();
        FindRuntimeRefs();

        // Never overwrite an old or malformed project, even via SAVE from the UI.
        string existingPath = SlotPath(slot);
        if (SlotHasSavedData(slot) && !TryReadOrRecoverProject(slot, out _))
        {
            ShowProjectNotice("Cannot overwrite an unreadable or incompatible project " +
                slot.ToString("000") + ". Existing files were preserved.");
            return false;
        }

        if (_chainUi != null)
            _chainUi.FlushLiveStateToPattern();

        // Retain a stable ID when saving or renaming, even on recovered slots.
        string stableId = null;
        if (TryReadOrRecoverProject(slot, out var previous))
            stableId = previous.projectId;
        if (string.IsNullOrEmpty(stableId)) stableId = Guid.NewGuid().ToString("N");
        var data = new ProjectData
        {
            projectId = stableId,
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
            // Save the *applied* sound rather than a next preset that is still loading.
            if (!string.IsNullOrEmpty(_padSynthRuntime.LastAppliedPresetRelPath))
            {
                data.pad.presetRelPath = _padSynthRuntime.LastAppliedPresetRelPath;
                data.pad.nativeSound = CaptureNativeSound(_padSynthRuntime.ActiveHelmController);
            }
        }

        if (_helm != null)
        {
            data.synth.presetRelPath = _helm.CurrentPresetRelPath;
            data.synth.presetIndex = _helm.CurrentPresetIndex;
            if (!string.IsNullOrEmpty(_helm.LastAppliedPresetRelPath))
            {
                data.synth.presetRelPath = _helm.LastAppliedPresetRelPath;
                data.synth.nativeSound = CaptureNativeSound(_helm.ActiveHelmController);
            }
        }

        if (_helm != null && _helm.ActiveHelmController != null &&
            !string.IsNullOrEmpty(_helm.LastAppliedPresetRelPath))
            data.synth.masterVolume = Mathf.Clamp01(_helm.ActiveHelmController.GetParameterPercent(AudioHelm.Param.kVolume));
        else if (_app != null && _app.Bus != null)
            data.synth.masterVolume = Mathf.Clamp01(_app.Bus.GetValue("master.vol"));

        // Bass edits were previously lost because only the preset path lived
        // in SongData. Preserve the complete native bass patch independently.
        if (_rack != null && _rack.BassPresetsReady)
        {
            data.bass.presetRelPath = _rack.CurrentBassPresetRelPath;
            data.bass.nativeSound = CaptureNativeSound(_rack.BassController);
        }
        else
            Debug.LogWarning("[ZPUNK Project] Bass is still loading; its edited sound could not be captured in this save.");

        if (_player != null)
        {
            data.player.lastTrackId = KMusicPlayerUI.CanonicalSampleId(_player.GetLastTrackId());
            data.warp = _player.CaptureProjectWarpState();
        }

        if (KMusicChopState.TryLoadApplied(out string rp, out var ss, out var ee))
        {
            data.chops.resourcesPath = KMusicPlayerUI.CanonicalSampleId(rp);
            data.chops.sliceStart01 = ss;
            data.chops.sliceEnd01 = ee;
            if (KMusicChopState.TryLoadAppliedMarkerPositions(out var positions,
                out bool lastIsEndOnly))
            {
                data.chops.markerPositions01 = positions;
                data.chops.lastMarkerIsEndOnly = lastIsEndOnly;
            }
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
            if (!string.IsNullOrEmpty(data.bass.presetRelPath) &&
                data.song.tracks != null && data.song.tracks.Count > 1 &&
                data.song.tracks[1] != null && data.song.tracks[1].instrument != null)
            {
                data.song.tracks[1].instrument.presetRelPath = data.bass.presetRelPath;
                data.song.tracks[1].instrument.presetIndex = _rack != null ? _rack.BassPresetIndex :
                    data.song.tracks[1].instrument.presetIndex;
            }
            data.song.title = data.projectName;
            if (data.song.tracks != null && data.song.tracks.Count > 4 &&
                data.song.tracks[4] != null && data.song.tracks[4].instrument != null)
                data.song.tracks[4].instrument.sampleResourcesPath =
                    KMusicPlayerUI.CanonicalSampleId(data.song.tracks[4].instrument.sampleResourcesPath);
            string jsonPath = SlotPath(slot);
            WriteNativeProjectSafely(jsonPath, data);
            Debug.Log($"[ZPUNK Project] Saved sound: BASS={data.bass.nativeSound.Count}, SYNTH={data.synth.nativeSound.Count}, PADS={data.pad.nativeSound.Count} native parameters.");
            CurrentSong = data.song;
            _cachedProjectNames[slot] = data.projectName;
            // 2.2.0: MIDI is created on explicit export, never beside every save.
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[Project] Save failed: " + e.Message);
            return false;
        }
    }

    /// <summary>Freeze a validated, saved copy for export. No edits to the live song.</summary>
    public bool TryCreateCollectedSnapshot(out string projectJson, out SongData song,
        out string title, out string identity, out string error)
    {
        projectJson = null; song = null; title = null; identity = null; error = null;
        if (!TrySaveProject(CurrentSlot))
        {
            error = "Project could not be saved safely. Collection was canceled.";
            return false;
        }
        if (!TryReadOrRecoverProject(CurrentSlot, out var saved) || saved.song == null)
        {
            error = "Saved project could not be verified.";
            return false;
        }
        projectJson = JsonUtility.ToJson(saved, true);
        song = SongData.FromJson(saved.song.ToJson());
        title = string.IsNullOrWhiteSpace(saved.projectName) ? DefaultProjectName(CurrentSlot) : saved.projectName.Trim();
        identity = saved.projectId;
        return true;
    }

    public void LoadProject(int slot, bool silent = false)
    {
        EnsureProjectsDir();
        FindRuntimeRefs();

        if (!TryReadOrRecoverProject(slot, out ProjectData data))
        {
            if (SlotHasSavedData(slot)) StartFreshAfterIncompatible(slot);
            else if (!silent) Debug.LogWarning("[Project] No project file for slot " + slot);
            return;
        }

        var dawTransport = FindObjectOfType<ZPunkSongTransport>();
        if (dawTransport != null && dawTransport.IsPlaying) dawTransport.StopSong();

        // Cancel any outstanding restores from the preceding project. A stale
        // coroutine must never switch FX or volume after a later project loads.
        CancelPendingAudioRestores();
        _expectedSynthPresetPath = data.synth != null ? data.synth.presetRelPath : null;
        _expectedPadPresetPath = data.pad != null ? data.pad.presetRelPath : null;
        _pendingSynthMasterVolume = data.synth != null ? data.synth.masterVolume : -1f;
        _pendingPadMasterVolume = data.pad != null ? data.pad.masterVolume : -1f;
        _expectedBassPresetPath = data.bass != null ? data.bass.presetRelPath : null;
        if (string.IsNullOrEmpty(_expectedBassPresetPath) && data.song != null &&
            data.song.tracks != null && data.song.tracks.Count > 1 &&
            data.song.tracks[1] != null && data.song.tracks[1].instrument != null)
            _expectedBassPresetPath = data.song.tracks[1].instrument.presetRelPath;
        if (data.bass != null && data.bass.nativeSound != null)
            _pendingBassNativeRestore.AddRange(data.bass.nativeSound);
        if (data.synth != null && data.synth.nativeSound != null)
            _pendingSynthNativeRestore.AddRange(data.synth.nativeSound);
        if (data.pad != null && data.pad.nativeSound != null)
            _pendingPadNativeRestore.AddRange(data.pad.nativeSound);

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

            if (_app != null && _app.Bus != null && data.bus != null)
            {
                var nonSynthBus = new List<BusPair>();

                for (int i = 0; i < data.bus.Count; i++)
                {
                    var it = data.bus[i];
                    if (it == null || string.IsNullOrEmpty(it.id)) continue;

                    // These values are only an old UI cache, NOT a faithful
                    // snapshot of a Helm patch. Native sound is restored above.
                    if (!IsSynthBusId(it.id))
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
            {
                _padSynthRuntime.PresetApplied -= OnProjectPadPresetApplied;
                _padSynthRuntime.PresetApplied += OnProjectPadPresetApplied;
                _padSynthRuntime.ApplyPresetChoice(data.pad.presetIndex, data.pad.presetRelPath);
            }

            if (_helm != null && data.synth != null)
            {
                _helm.PresetApplied -= OnProjectPresetApplied;
                _helm.PresetApplied += OnProjectPresetApplied;
                _helm.ApplyPresetChoice(data.synth.presetIndex, data.synth.presetRelPath);
            }

            // Unlike the main synth/pads, Bass loads its StreamingAssets index
            // asynchronously in ZPunkInstrumentRack.Start(). Restore its edited
            // sound only AFTER the matching bass factory preset has applied.
            if (_pendingBassNativeRestore.Count > 0)
                _pendingBassRestoreJob = StartCoroutine(RestoreBassSoundAfterPresetCoroutine(
                    _projectLoadRevision, _expectedBassPresetPath));

            string chopsSampleId = data.chops != null ? data.chops.resourcesPath : null;
            if (string.IsNullOrEmpty(chopsSampleId) && CurrentSong.tracks != null &&
                CurrentSong.tracks.Count > 4 && CurrentSong.tracks[4].instrument != null)
                chopsSampleId = CurrentSong.tracks[4].instrument.sampleResourcesPath;
            chopsSampleId = KMusicPlayerUI.CanonicalSampleId(chopsSampleId);
            if (!string.IsNullOrEmpty(chopsSampleId))
                KMusicChopState.SetAppliedRaw(chopsSampleId,
                    data.chops != null ? data.chops.sliceStart01 : CurrentSong.tracks[4].instrument.sliceStart01,
                    data.chops != null ? data.chops.sliceEnd01 : CurrentSong.tracks[4].instrument.sliceEnd01,
                    data.chops != null ? data.chops.markerPositions01 : null,
                    data.chops != null && data.chops.lastMarkerIsEndOnly);
            else
                KMusicChopState.ClearApplied();

            if (_player != null)
            {
                // An applied CHOPS sample takes precedence over a previously browsed
                // preview track. Never restore a mismatched sample into a project.
                string projectSample = !string.IsNullOrEmpty(chopsSampleId) ? chopsSampleId :
                    (data.player != null ? data.player.lastTrackId : null);
                _player.SetLastTrackId(projectSample);
                _player.SetProjectWarpState(data.warp, projectSample);
                _player.RefreshAppliedMarkersFromState();
            }

            if (_chainUi != null)
                _chainUi.ReloadFromSaved();

            // Volumes are restored in the matching PresetApplied handlers,
            // not after an unrelated fixed delay on startup.
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


    private void CancelPendingAudioRestores()
    {
        ++_projectLoadRevision;
        if (_pendingSynthRestoreJob != null) StopCoroutine(_pendingSynthRestoreJob);
        if (_pendingPadRestoreJob != null) StopCoroutine(_pendingPadRestoreJob);
        if (_pendingBassRestoreJob != null) StopCoroutine(_pendingBassRestoreJob);
        _pendingSynthRestoreJob = null;
        _pendingPadRestoreJob = null;
        _pendingBassRestoreJob = null;
        if (_helm != null) _helm.PresetApplied -= OnProjectPresetApplied;
        if (_padSynthRuntime != null) _padSynthRuntime.PresetApplied -= OnProjectPadPresetApplied;
        _pendingSynthNativeRestore.Clear();
        _pendingPadNativeRestore.Clear();
        _pendingBassNativeRestore.Clear();
        _expectedBassPresetPath = null;
        _pendingSynthMasterVolume = -1f;
        _pendingPadMasterVolume = -1f;
        _expectedSynthPresetPath = null;
        _expectedPadPresetPath = null;
    }

    private void ResetToBlank()
    {
        CancelPendingAudioRestores();
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
