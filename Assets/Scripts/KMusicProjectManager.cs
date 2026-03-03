using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;
using KMusic;
using KMusic.Core;

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
    private const int ProjectVer = 1;
    private const string PrefKey_CurrentSlot = "kmusic.project.slot.v1";

    [SerializeField] private UIDocument doc;

    // UI
    private Label _presetLabel;
    private Button _newBtn;
    private Button _loadBtn;

    // Modal
    private VisualElement _modal;
    private VisualElement _modalList;
    private Label _modalTitle;

    // Runtime refs
    private KMusicApp _app;
    private KMusicDrumSequencer _drums;
    private KMusicSampleSequencerUI _samplerUi;
    private KMusic.UI.KMusicPianoRollStepPainter _keys;
    private KMusicHelmPresetPickerUI _helm;
    private KMusicPlayerUI _player;
    private KMusicChainUI _chainUi;

    public int CurrentSlot { get; private set; }
    private string ProjectsDir => Path.Combine(Application.persistentDataPath, "Projects");

    private VisualElement _presetBrowser;
    private ScrollView _presetBrowserList;

    private void Awake()
    {
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
    }

    private void FindRuntimeRefs()
    {
        if (_app == null) _app = FindObjectOfType<KMusicApp>();
        if (_drums == null) _drums = FindObjectOfType<KMusicDrumSequencer>();
        if (_samplerUi == null) _samplerUi = FindObjectOfType<KMusicSampleSequencerUI>();
        if (_keys == null) _keys = FindObjectOfType<KMusic.UI.KMusicPianoRollStepPainter>();
        if (_helm == null) _helm = FindObjectOfType<KMusicHelmPresetPickerUI>();
        if (_player == null) _player = FindObjectOfType<KMusicPlayerUI>();
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

        EnsureModal(root);
    }

    private void EnsureModal(VisualElement root)
    {
        if (_modal != null) return;

        _modal = new VisualElement { name = "ProjectModal" };
        _modal.style.position = Position.Absolute;
        _modal.style.left = 0;
        _modal.style.right = 0;
        _modal.style.top = 0;
        _modal.style.bottom = 0;
        _modal.style.display = DisplayStyle.None;
        _modal.style.backgroundColor = new Color(0f, 0f, 0f, 0.55f);
        _modal.pickingMode = PickingMode.Position;

        var card = new VisualElement();
        card.style.position = Position.Absolute;
        card.style.left = 32;
        card.style.right = 32;
        card.style.top = 180;
        card.style.bottom = 180;
        card.style.backgroundColor = new Color(0.10f, 0.11f, 0.14f, 1f);
        card.style.borderTopLeftRadius = 18;
        card.style.borderTopRightRadius = 18;
        card.style.borderBottomLeftRadius = 18;
        card.style.borderBottomRightRadius = 18;
        card.style.paddingLeft = 18;
        card.style.paddingRight = 18;
        card.style.paddingTop = 18;
        card.style.paddingBottom = 18;
        card.style.flexDirection = FlexDirection.Column;

        _modalTitle = new Label("PROJECTS");
        _modalTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
        _modalTitle.style.fontSize = 26;
        _modalTitle.style.marginBottom = 12;
        card.Add(_modalTitle);

        var row = new VisualElement();
        row.style.flexDirection = FlexDirection.Row;
        row.style.justifyContent = Justify.SpaceBetween;
        row.style.alignItems = Align.Center;
        row.style.marginBottom = 12;

        var saveBtn = new Button(() => { SaveProject(CurrentSlot); HideModal(); }) { text = "SAVE" };
        saveBtn.AddToClassList("km-pillbtn");
        row.Add(saveBtn);

        var closeBtn = new Button(HideModal) { text = "CLOSE" };
        closeBtn.AddToClassList("km-pillbtn");
        row.Add(closeBtn);
        card.Add(row);

        _modalList = new ScrollView(ScrollViewMode.Vertical);
        _modalList.style.flexGrow = 1;
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

    private void OnNewClicked()
    {
        SaveProject(CurrentSlot);

        int next = FindNextFreeSlot(CurrentSlot);
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

    private int FindNextFreeSlot(int start)
    {
        for (int i = 1; i <= 200; i++)
        {
            int slot = Mathf.Clamp(start + i, 0, 999);
            if (!File.Exists(SlotPath(slot)))
                return slot;
        }
        return Mathf.Clamp(start + 1, 0, 999);
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
            _presetLabel.text = $"Preset: PROJECT {CurrentSlot}";
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

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Column;
            row.style.marginBottom = 12;
            row.style.paddingLeft = 14;
            row.style.paddingRight = 14;
            row.style.paddingTop = 12;
            row.style.paddingBottom = 12;
            row.style.borderTopLeftRadius = 14;
            row.style.borderTopRightRadius = 14;
            row.style.borderBottomLeftRadius = 14;
            row.style.borderBottomRightRadius = 14;
            row.style.backgroundColor = new Color(0.12f, 0.13f, 0.16f, 1f);

            // Title
            var label = new Label(has ? $"PROJECT {slot:000}" : $"PROJECT {slot:000} (empty)");
            label.style.fontSize = 22;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.color = Color.white;
            row.Add(label);

            // Buttons row (THIS is fix 2)
            var btnRow = new VisualElement();
            btnRow.style.flexDirection = FlexDirection.Row;
            btnRow.style.marginTop = 10;
            row.Add(btnRow);

            if (has)
            {
                // ✅ LOAD (saves current first)
                var btnLoad = new Button(() =>
                {
                    SaveProject(CurrentSlot);
                    SwitchToSlot(slot);
                    LoadProject(slot);
                    HideModal();
                })
                { text = "LOAD" };
                btnLoad.AddToClassList("km-pillbtn");
                btnLoad.style.flexGrow = 1;
                btnLoad.style.height = 54;
                btnRow.Add(btnLoad);

                // ✅ LOAD (NO SAVE)
                var btnNoSave = new Button(() =>
                {
                    SwitchToSlot(slot);
                    LoadProject(slot);
                    HideModal();
                })
                { text = "LOAD (NO SAVE)" };
                btnNoSave.AddToClassList("km-pillbtn");
                btnNoSave.style.flexGrow = 1;
                btnNoSave.style.height = 54;
                btnRow.Add(btnNoSave);
            }
            else
            {
                // ✅ CREATE (saves current, switches slot, blanks, saves new file)
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
                btnCreate.style.flexGrow = 1;
                btnCreate.style.height = 54;
                btnRow.Add(btnCreate);
            }

            _modalList.Add(row);
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
        public int slot;
        public long utc;

        public List<BusPair> bus = new();
        public DrumState drums = new();
        public SampleState sampler = new();
        public SeqState seq = new();
        public SynthState synth = new();
        public PlayerState player = new();
        public ChopState chops = new();
        public PatternBank.PatternBankSave patterns = new();
        public ChainStateSave chain = new();
    }

    [Serializable] private class BusPair { public string id; public float v; }

    [Serializable]
    private class DrumState
    {
        public string stepMaskB64;
        public bool[] mutes;
        public int activeDrumId;
        public int kitIndex;
        public int[] sampleSteps;
    }

    [Serializable] private class SampleState { public int[] stepGrid; }
    [Serializable] private class SeqState { public int[] stepGrid; }
    [Serializable] private class SynthState { public string presetRelPath; public int presetIndex; }
    [Serializable] private class PlayerState { public string lastTrackId; }
    [Serializable] private class ChopState { public string resourcesPath; public float[] sliceStart01; public float[] sliceEnd01; }

    public void SaveProject(int slot)
    {
        EnsureProjectsDir();
        FindRuntimeRefs();

        var data = new ProjectData
        {
            ver = ProjectVer,
            slot = slot,
            utc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };

        if (_app != null && _app.Bus != null)
        {
            foreach (var p in _app.Bus.All)
                data.bus.Add(new BusPair { id = p.Id, v = p.Value });
        }

        if (_drums != null)
        {
            var mask = _drums.CaptureDrumMask();
            data.drums.stepMaskB64 = mask != null ? Convert.ToBase64String(mask) : "";
            data.drums.sampleSteps = _drums.CaptureSampleStepsFlat();
            data.drums.mutes = _drums.CaptureDrumMutes();
            data.drums.activeDrumId = _drums.CaptureActiveDrumId();
            data.drums.kitIndex = _drums.CaptureKitIndex();
        }

        if (_samplerUi != null)
            data.sampler.stepGrid = _samplerUi.CaptureSampleStepsFlat();

        if (_keys != null)
            data.seq.stepGrid = _keys.CaptureSeqStepsFlat();

        if (_helm != null)
        {
            data.synth.presetRelPath = _helm.CurrentPresetRelPath;
            data.synth.presetIndex = _helm.CurrentPresetIndex;
        }

        if (_player != null)
            data.player.lastTrackId = _player.GetLastTrackId();

        if (KMusicChopState.TryLoadApplied(out string rp, out var ss, out var ee))
        {
            data.chops.resourcesPath = rp;
            data.chops.sliceStart01 = ss;
            data.chops.sliceEnd01 = ee;
        }

        data.patterns = PatternBank.ExportAll();
        data.chain = ChainStateSave.From(ChainState.LoadOrCreate());

        try
        {
            File.WriteAllText(SlotPath(slot), JsonUtility.ToJson(data));
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
            return;
        }

        if (data == null) return;

        if (_drums != null) _drums.SetAllowSaving(false);
        if (_samplerUi != null) _samplerUi.SetAllowSaving(false);
        if (_keys != null) _keys.SetAllowSaving(false);

        if (_app != null && _app.Bus != null && data.bus != null)
        {
            for (int i = 0; i < data.bus.Count; i++)
            {
                var it = data.bus[i];
                if (it == null || string.IsNullOrEmpty(it.id)) continue;
                _app.Bus.SetValue(it.id, it.v);
            }
        }

        if (data.patterns != null)
            PatternBank.ImportAll(data.patterns);

        if (data.chain != null)
            data.chain.ApplyToPrefs();

        if (_drums != null && data.drums != null)
        {
            byte[] mask = null;
            try
            {
                if (!string.IsNullOrEmpty(data.drums.stepMaskB64))
                    mask = Convert.FromBase64String(data.drums.stepMaskB64);
            }
            catch { mask = null; }

            _drums.ApplyDrumMask(mask);
            _drums.ApplySampleStepsFlat(data.drums.sampleSteps);
            _drums.ApplyDrumMutes(data.drums.mutes);
            _drums.ApplyKitIndex(data.drums.kitIndex);
            _drums.ApplyActiveDrumId(data.drums.activeDrumId);
        }

        if (_samplerUi != null && data.sampler != null)
            _samplerUi.ApplySampleStepsFlat(data.sampler.stepGrid);

        if (_keys != null && data.seq != null)
        {
            _keys.ApplySeqStepsFlat(data.seq.stepGrid);
            _keys.RebuildSynthSequenceNow();
        }

        if (_helm != null && data.synth != null)
            _helm.ApplyPresetChoice(data.synth.presetIndex, data.synth.presetRelPath);

        if (_player != null && data.player != null)
            _player.SetLastTrackId(data.player.lastTrackId);

        if (data.chops != null && !string.IsNullOrEmpty(data.chops.resourcesPath))
            KMusicChopState.SetAppliedRaw(data.chops.resourcesPath, data.chops.sliceStart01, data.chops.sliceEnd01);

        if (_chainUi != null)
            _chainUi.ReloadFromSaved();

        if (_drums != null) _drums.SetAllowSaving(true);
        if (_samplerUi != null) _samplerUi.SetAllowSaving(true);
        if (_keys != null) _keys.SetAllowSaving(true);
    }

    private void ResetToBlank()
    {
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

        PatternBank.ResetAll();
        new ChainState().Save();
        if (_chainUi != null) _chainUi.ReloadFromSaved();

        KMusicChopState.ClearApplied();
    }
}
