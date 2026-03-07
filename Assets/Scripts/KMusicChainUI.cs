using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using KMusic.Core;

/// <summary>
/// CHAIN tab controller (your TabFx / PageFx).
/// - Pattern bank: NEW / SAVE / DUP
/// - Chain grid: place patterns into bars
/// - Song mode: switch patterns on bar boundary (quantized)
///
/// Designed to fit your current architecture:
/// - still 16 steps per pattern
/// - saved via PlayerPrefs using KMusicSaveState
/// </summary>
public class KMusicChainUI : MonoBehaviour
{
    [SerializeField] private UIDocument doc;

    private KMusicDrumSequencer _drums;
    private KMusicSampleSequencerUI _samplerUi;
    private KMusic.UI.KMusicPianoRollStepPainter _keys;

    private ChainState _chain;
    private int _selectedPatternId = 0;

    // UI
    private Toggle _enabled;
    private Label _patternLabel;
    private Label _statusLabel;
    private VisualElement _barsRoot;
    private Button _btnNew, _btnSave, _btnDup, _btnPlace, _btnDupNext;
    private Button _btnClear, _btnLen2, _btnLen4, _btnLen8, _btnLen16, _btnLen32, _btnLen64;

    private readonly List<Button> _barButtons = new();
    private bool _uiWired = false;

    // long-press delete
    private const int LongPressMs = 650;
    private readonly Dictionary<Button, IVisualElementScheduledItem> _longPressJobs = new();
    private readonly HashSet<Button> _longPressFired = new();

    // autosave
    private bool _suppressAutoSave = false;

    // playback
    private int _playBar = 0;
    private int _lastAppliedPattern = int.MinValue;

    private void Awake()
    {
        if (!doc) doc = GetComponent<UIDocument>();
    }

    private void OnEnable()
    {
        PatternBank.EnsureDefaultPatternExists();
        _chain = ChainState.LoadOrCreate();

        FindRuntimeRefs();
        WireUI();

        if (_drums != null)
            _drums.OnBarStart -= OnBarStart;

        if (_drums != null)
            _drums.OnBarStart += OnBarStart;

        // Default selection = pattern 0
        _selectedPatternId = 0;
        RefreshAllUI();
    }

    /// <summary>
    /// Reload chain + pattern bank from saved storage (used by project load).
    /// </summary>
    public void ReloadFromSaved()
    {
        try
        {
            PatternBank.EnsureDefaultPatternExists();
            _chain = ChainState.LoadOrCreate();
            _playBar = 0;
            _lastAppliedPattern = int.MinValue;
            _selectedPatternId = ResolveSelectedPatternId();
            RefreshAllUI();
        }
        catch { }
    }

    public int ResolveSelectedPatternId()
    {
        PatternBank.EnsureDefaultPatternExists();

        if (_chain != null)
        {
            int cursor = Mathf.Clamp(_chain.cursor, 0, 63);

            if (_chain.slots != null && cursor < _chain.slots.Length)
            {
                int pid = _chain.slots[cursor];
                if (pid >= 0)
                    return pid;
            }

            if (_chain.slots != null)
            {
                for (int i = 0; i < _chain.slots.Length; i++)
                {
                    if (_chain.slots[i] >= 0)
                        return _chain.slots[i];
                }
            }
        }

        return 0;
    }

    private void OnDisable()
    {
        if (_drums != null)
            _drums.OnBarStart -= OnBarStart;

        _chain?.Save();
    }

    private void FindRuntimeRefs()
    {
        if (_drums == null) _drums = FindObjectOfType<KMusicDrumSequencer>();
        if (_samplerUi == null) _samplerUi = FindObjectOfType<KMusicSampleSequencerUI>();
        if (_keys == null) _keys = FindObjectOfType<KMusic.UI.KMusicPianoRollStepPainter>();
    }

    private void WireUI()
    {
        if (_uiWired) return;
        if (!doc) return;

        var root = doc.rootVisualElement;
        if (root == null) return;

        _enabled = root.Q<Toggle>("ChainEnabledToggle");
        _patternLabel = root.Q<Label>("ChainPatternLabel");
        _statusLabel = root.Q<Label>("ChainStatusLabel");
        _barsRoot = root.Q<VisualElement>("ChainBars");

        _btnNew = root.Q<Button>("ChainNewPatternButton");
        _btnSave = root.Q<Button>("ChainSavePatternButton");
        _btnDup = root.Q<Button>("ChainDupPatternButton");
        _btnPlace = root.Q<Button>("ChainPlaceHereButton");
        _btnDupNext = root.Q<Button>("ChainDupNextButton");

        _btnClear = root.Q<Button>("ChainClearButton");
        _btnLen2 = root.Q<Button>("ChainLen2Button");
        _btnLen4 = root.Q<Button>("ChainLen4Button");
        _btnLen8 = root.Q<Button>("ChainLen8Button");
        _btnLen16 = root.Q<Button>("ChainLen16Button");
        _btnLen32 = root.Q<Button>("ChainLen32Button");
        _btnLen64 = root.Q<Button>("ChainLen64Button");

        if (_enabled != null)
        {
            _enabled.SetValueWithoutNotify(_chain.enabled);
            _enabled.RegisterValueChangedCallback(evt =>
            {
                _chain.enabled = evt.newValue;
                _chain.Save();
                SetStatus(_chain.enabled ? "Song mode ON" : "Song mode OFF");
            });
        }

        if (_btnNew != null) _btnNew.clicked += OnNewPattern;
        if (_btnSave != null) _btnSave.clicked += OnSavePattern;
        if (_btnDup != null) _btnDup.clicked += OnDuplicatePattern;
        if (_btnPlace != null) _btnPlace.clicked += OnPlaceHere;
        if (_btnDupNext != null) _btnDupNext.clicked += OnDupNext;

        if (_btnClear != null) _btnClear.clicked += () =>
        {
            _chain.Clear();
            _chain.Save();
            RefreshBarsUI();
            SetStatus("Chain cleared");
        };

        if (_btnLen2 != null) _btnLen2.clicked += () => SetChainLength(2);
        if (_btnLen4 != null) _btnLen4.clicked += () => SetChainLength(4);
        if (_btnLen8 != null) _btnLen8.clicked += () => SetChainLength(8);
        if (_btnLen16 != null) _btnLen16.clicked += () => SetChainLength(16);
        if (_btnLen32 != null) _btnLen32.clicked += () => SetChainLength(32);
        if (_btnLen64 != null) _btnLen64.clicked += () => SetChainLength(64);

        BuildBarsIfNeeded();
        _uiWired = true;
    }

    private void BuildBarsIfNeeded()
    {
        if (_barsRoot == null) return;
        if (_barButtons.Count > 0) return;

        _barsRoot.Clear();
        _barButtons.Clear();

        for (int i = 0; i < 64; i++)
        {
            int barIndex = i;
            var b = new Button();
            b.AddToClassList("km-chain-bar");
            b.text = FormatBarText(barIndex);
            
            b.style.fontSize = 32; // or new StyleLength(32) if needed

            b.clicked += () =>
            {
                // if this click is the release after a long-press, ignore the normal click.
                if (_longPressFired.Contains(b))
                {
                    _longPressFired.Remove(b);
                    return;
                }

                _chain.cursor = barIndex;
                _chain.Save();

                // tap selects bar; if it has a pattern, also select it
                int pid = _chain.slots[barIndex];
                if (pid >= 0)
                {
                    _selectedPatternId = pid;
                    LoadPatternToLive(pid);
                    SetStatus($"Loaded {PatternBank.GetName(pid)}");
                }
                RefreshBarsUI();
                RefreshPatternUI();
            };

            // Long-press delete pattern assigned to this bar.
            // (Mobile-friendly: hold the bar cell to delete the referenced pattern.)
            b.RegisterCallback<PointerDownEvent>(_ => BeginLongPress(b, barIndex));
            b.RegisterCallback<PointerUpEvent>(_ => CancelLongPress(b));
            b.RegisterCallback<PointerLeaveEvent>(_ => CancelLongPress(b));
            b.RegisterCallback<PointerCancelEvent>(_ => CancelLongPress(b));

            _barButtons.Add(b);
            _barsRoot.Add(b);
        }
    }

    private void BeginLongPress(Button b, int barIndex)
    {
        if (b == null) return;
        CancelLongPress(b);

        var job = b.schedule.Execute(() =>
        {
            CancelLongPress(b);
            _longPressFired.Add(b);
            TryDeletePatternFromBar(barIndex);
        });

        job.ExecuteLater(LongPressMs);
        _longPressJobs[b] = job;
    }

    private void CancelLongPress(Button b)
    {
        if (b == null) return;
        if (_longPressJobs.TryGetValue(b, out var job) && job != null)
        {
            try { job.Pause(); } catch { }
        }
        _longPressJobs.Remove(b);
    }

    private void TryDeletePatternFromBar(int barIndex)
    {
        if (_chain == null) return;
        if (barIndex < 0 || barIndex >= _chain.slots.Length) return;

        int pid = _chain.slots[barIndex];
        if (pid < 0)
        {
            SetStatus("Nothing to delete");
            return;
        }

        if (pid == 0)
        {
            SetStatus("Pattern P01 can't be deleted");
            return;
        }

        string pname = PatternBank.GetName(pid);
        bool removed = PatternBank.Delete(pid);
        if (!removed)
        {
            SetStatus("Delete failed");
            return;
        }

        // Clear ALL chain references to this pattern.
        for (int i = 0; i < _chain.slots.Length; i++)
            if (_chain.slots[i] == pid)
                _chain.slots[i] = -1;

        _chain.Save();

        // If we were editing that pattern, fall back to 0.
        if (_selectedPatternId == pid)
        {
            _selectedPatternId = 0;
            LoadPatternToLive(_selectedPatternId);
        }

        RefreshAllUI();
        SetStatus($"Deleted {pname}");
    }

    /// <summary>
    /// Called by sequencers when a cell is painted/erased.
    /// Saves the currently selected pattern immediately.
    /// </summary>
    public void NotifyLiveEdited()
    {
        if (_suppressAutoSave) return;

        int targetPatternId = GetActiveEditPatternId();
        if (targetPatternId < 0) return;

        try
        {
            var snap = CaptureLiveToPattern();
            PatternBank.Save(targetPatternId, snap);

            // Keep the chain editor pointed at the live pattern being authored.
            if (_selectedPatternId != targetPatternId)
            {
                _selectedPatternId = targetPatternId;
                RefreshPatternUI();
                RefreshBarsUI();
            }
        }
        catch { }
    }


    private int GetActiveEditPatternId()
    {
        if (_chain != null && _chain.enabled && _drums != null && _drums.IsPlaying && _lastAppliedPattern >= 0)
            return _lastAppliedPattern;

        return _selectedPatternId;
    }

    private void RefreshAllUI()
    {
        if (_enabled != null) _enabled.SetValueWithoutNotify(_chain.enabled);
        RefreshPatternUI();
        RefreshBarsUI();
    }

    private void RefreshPatternUI()
    {
        if (_patternLabel != null)
            _patternLabel.text = PatternBank.GetName(_selectedPatternId);
    }

    private void RefreshBarsUI()
    {
        if (_barButtons.Count == 0) return;

        // hide beyond chain length (still stored, but UI focuses)
        int len = Mathf.Clamp(_chain.length, 1, 64);

        for (int i = 0; i < _barButtons.Count; i++)
        {
            var b = _barButtons[i];
            if (b == null) continue;

            bool visible = i < len;
            b.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;

            if (!visible) continue;

            b.text = FormatBarText(i);

            b.RemoveFromClassList("is-cursor");
            b.RemoveFromClassList("is-playing");

            if (i == _chain.cursor) b.AddToClassList("is-cursor");
            if (_chain.enabled && _drums != null && _drums.IsPlaying)
            {
                int playingIndex = _playBar % len;
                if (i == playingIndex) b.AddToClassList("is-playing");
            }
        }
    }

    private string FormatBarText(int barIndex)
    {
        int pid = _chain.slots[barIndex];
        string p = (pid >= 0) ? PatternBank.GetName(pid) : "--";
        return $"{barIndex + 1:00}  {p}";
    }

    // ----------------------------
    // UI actions
    // ----------------------------

    private void OnNewPattern()
    {
        var snap = CaptureLiveToPattern();
        int id = PatternBank.CreateFrom(snap);
        _selectedPatternId = id;
        SetStatus($"New pattern {PatternBank.GetName(id)}");
        RefreshAllUI();
    }

    private void OnSavePattern()
    {
        var snap = CaptureLiveToPattern();
        PatternBank.Save(_selectedPatternId, snap);
        SetStatus($"Saved {PatternBank.GetName(_selectedPatternId)}");
    }

    private void OnDuplicatePattern()
    {
        int id = PatternBank.Duplicate(_selectedPatternId);
        _selectedPatternId = id;
        LoadPatternToLive(id);
        SetStatus($"Duplicated to {PatternBank.GetName(id)}");
        RefreshAllUI();
    }

    private void OnPlaceHere()
    {
        _chain.SetSlot(_chain.cursor, _selectedPatternId);
        _chain.Save();
        RefreshBarsUI();
        SetStatus($"Placed {PatternBank.GetName(_selectedPatternId)} at bar {_chain.cursor + 1}" );
    }

    private void OnDupNext()
    {
        // Duplicate current pattern, place in next bar, advance cursor, load it.
        int newId = PatternBank.Duplicate(_selectedPatternId);
        int nextBar = Mathf.Clamp(_chain.cursor + 1, 0, 63);

        _chain.SetSlot(_chain.cursor, _selectedPatternId);
        _chain.SetSlot(nextBar, newId);
        _chain.cursor = nextBar;
        _chain.Save();

        _selectedPatternId = newId;
        LoadPatternToLive(newId);

        RefreshAllUI();
        SetStatus($"Dup+Next → {PatternBank.GetName(newId)} at bar {nextBar + 1}");
    }

    private void SetStatus(string msg)
    {
        if (_statusLabel != null) _statusLabel.text = msg ?? "";
    }

    private void SetChainLength(int newLength)
    {
        if (_chain == null) return;

        _chain.length = Mathf.Clamp(newLength, 1, 64);
        _chain.cursor = Mathf.Clamp(_chain.cursor, 0, _chain.length - 1);
        _playBar = Mathf.Clamp(_playBar, 0, _chain.length - 1);

        _chain.Save();
        RefreshBarsUI();
        SetStatus($"Chain length: {_chain.length} bars");
    }

    // ----------------------------
    // Pattern capture/apply (fits your existing scripts)
    // ----------------------------

    private PatternData CaptureLiveToPattern()
    {
        var p = new PatternData();

        if (_drums != null)
        {
            p.drumMask = _drums.CaptureDrumMask();
            p.sampleSteps = _drums.CaptureSampleStepsFlat();
        }

        if (_keys != null)
            p.seqSteps = _keys.CaptureSeqStepsFlat();

        // (optional) also prefer the sampler UI if it exists (keeps UI as source of truth)
        if (_samplerUi != null)
        {
            var flat = _samplerUi.CaptureSampleStepsFlat();
            if (flat != null && flat.Length == PatternData.Steps)
                p.sampleSteps = flat;
        }

        return p;
    }

    private void LoadPatternToLive(int patternId)
    {
        _selectedPatternId = patternId;
        var p = PatternBank.Load(patternId);

        // suppress saving while importing
        _suppressAutoSave = true;
        if (_drums != null) _drums.SetAllowSaving(false);
        if (_samplerUi != null) _samplerUi.SetAllowSaving(false);
        if (_keys != null) _keys.SetAllowSaving(false);

        if (_drums != null)
        {
            _drums.ApplyDrumMask(p.drumMask);
            _drums.ApplySampleStepsFlat(p.sampleSteps);
        }

        if (_samplerUi != null)
            _samplerUi.ApplySampleStepsFlat(p.sampleSteps);

        if (_keys != null)
        {
            _keys.ApplySeqStepsFlat(p.seqSteps);
            _keys.RebuildSynthSequenceNow();
        }

        if (_drums != null) _drums.SetAllowSaving(true);
        if (_samplerUi != null) _samplerUi.SetAllowSaving(true);
        if (_keys != null) _keys.SetAllowSaving(true);

        _suppressAutoSave = false;

        _lastAppliedPattern = patternId;
        RefreshPatternUI();
    }

    // ----------------------------
    // Song mode switching (quantized)
    // ----------------------------

    private void OnBarStart(int barCount)
    {
        if (_chain == null) return;
        if (!_chain.enabled) return;
        if (_drums == null || !_drums.IsPlaying) return;

        int len = Mathf.Clamp(_chain.length, 1, 64);

        // barCount starts at 1 when scheduling step 0; convert to 0-based position
        _playBar = (barCount - 1) % len;
        int pid = _chain.GetSlot(_playBar);
        if (pid < 0) pid = _selectedPatternId; // fallback: keep current

        // Only apply if it changes (avoids extra work)
        if (pid != _lastAppliedPattern)
        {
            LoadPatternToLive(pid);
        }
        else if (_selectedPatternId != pid)
        {
            _selectedPatternId = pid;
            RefreshPatternUI();
        }

        // UI: highlight
        RefreshBarsUI();
    }
}
