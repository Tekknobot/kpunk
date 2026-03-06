using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.UIElements;
using KMusic;
using KMusic.Core;
using KMusic.UI;
#if UNITY_EDITOR
using UnityEditor;
#endif
using AudioHelm;

/// <summary>
/// Mobile-friendly drum step sequencer (16 steps = 2x8 StepGrid).
/// MULTI-LAYER (bitmask) playback with SINGLE-LAYER UI view:
/// - Internally: each step stores a bitmask (Kick+Snare+Hat can stack).
/// - UI StepGrid: shows ONLY the currently selected drum layer.
///   (So switching drums doesn't "turn snares into kicks" visually.)
/// - Painting toggles the selected drum's bit for that step.
/// - Playback schedules ALL drums set in the bitmask.
///
/// Drum sample discovery:
/// - Editor: can scan Assets/Audio/Kit Samples HMA/<kit folders> (AssetDatabase).
/// - Runtime (Android/iOS/Builds): loads from Resources/KMusic/Drums via Resources.LoadAll<AudioClip>().
/// </summary>
[RequireComponent(typeof(UIDocument))]
public class KMusicDrumSequencer : MonoBehaviour
{
    public double PlayStartDspTime => _playStartDspTime;
    public double NextStepDspTime => _nextStepDspTime;
    public double StepDurSeconds => _stepDur;
    public int Steps => steps;
    private bool _allowSaving = false;

    private const string PrefKey_DrumStepMask = "kmusic.drum.stepmask";
    private const string PrefKey_DrumMutes = "kmusic.drum.mutes";
    private const string PrefKey_DrumActive = "kmusic.drum.active";
    private const string PrefKey_DrumKitIndex = "kmusic.drum.kitIndex";
    private const string PrefKey_SampleStepGrid = "kmusic.sample.stepgrid";


    // NEW: PlayerPrefs fallback key for stepmask (base64) so lane patterns survive even if KMusicSaveState isn’t available in builds.
    private const string PrefKey_DrumStepMask_B64 = "kmusic.drum.stepmask.b64";

    private StepGrid _drumGrid;
    private IVisualElementScheduledItem _rebindLoop;
    private StepGrid _lastBoundDrumGrid;

    // When routeDrumsToMixer is true, we use per-drum AudioSource voice pools (for mixer routing + meters).
    private readonly Dictionary<int, List<AudioSource>> _drumVoicePools = new();
    private readonly Dictionary<int, int> _drumVoiceCursor = new();

    private KMusicApp _app;
    private ParameterBus _bus;

    private readonly bool[] _drumMute = new bool[8];

    private bool _didLoadState = false;

    [Header("Unity AudioMixer (optional)")]
    [Tooltip("If assigned, each drum AudioSource will be routed into these mixer groups so you can push above 0 dB and see meters in the mixer.")]
    [SerializeField] private AudioMixer drumMixer = null;

    [Tooltip("If true, drum.volXX from ParameterBus drives the AudioMixer exposed params. If false, you can mix directly in the AudioMixer.")]
    [SerializeField] private bool driveMixerFromBus = true;

    [Tooltip("Optional: a master group for drums (used if a per-drum group is not set).")]
    [SerializeField] private AudioMixerGroup drumMasterGroup = null;

    [Tooltip("Optional: per-drum output groups (size 8). Index 0 = Drum 1 (Kick).")]
    [SerializeField] private AudioMixerGroup[] drumGroups = new AudioMixerGroup[8];

    [Tooltip("Exposed parameter name prefix for per-drum volumes in dB. Example param names: Drum01VolDb, Drum02VolDb, ...")]
    [SerializeField] private string drumVolParamPrefix = "Drum";

    [Tooltip("Exposed parameter name suffix for per-drum volumes in dB.")]
    [SerializeField] private string drumVolParamSuffix = "VolDb";

    [Tooltip("Enable routing drums through per-drum AudioSources (instead of AudioHelm Sampler) so mixer meters/gain work per channel.")]
    [SerializeField] private bool routeDrumsToMixer = true;

    [Tooltip("Polyphony per drum lane when routing to mixer. Prevents cut-offs when hits overlap.")]
    [Range(1, 8)]
    [SerializeField] private int drumVoicesPerLane = 4;

    private Action<string, float> _onBusChanged;

    [Header("Sampler Volume")]
    [Range(0f, 1f)] public float sampleMasterVolume = 1f;

    // Optional UI control name (Slider or KnobElement root name)
    public string sampleMasterSliderName = "SampleMasterVol";

    private void EnsureStateLoaded()
    {
        if (_didLoadState) return;

        LoadDrumState();
        _didLoadState = true;

        // If UI is bound, redraw what we loaded.
        RefreshGridForActiveDrum();
        WireMuteButtons(_root);
    }

    private AudioSource GetOrCreateDrumVoice(int drumId, AudioClip clip)
    {
        drumId = Mathf.Clamp(drumId, 1, 8);

        if (!_drumVoicePools.TryGetValue(drumId, out var pool) || pool == null)
        {
            pool = new List<AudioSource>(Mathf.Max(1, drumVoicesPerLane));
            _drumVoicePools[drumId] = pool;
            _drumVoiceCursor[drumId] = 0;
        }

        // Lazily create voices for this drum
        while (pool.Count < Mathf.Max(1, drumVoicesPerLane))
        {
            var go = new GameObject($"Drum{drumId:00}_Voice{pool.Count}");
            go.transform.SetParent(this.transform, false);

            var src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.loop = false;
            src.spatialBlend = 0f;
            src.volume = 1f;

            // Route into mixer if provided
            var g = GetMixerGroupForDrum(drumId);
            if (g != null) src.outputAudioMixerGroup = g;

            pool.Add(src);
        }

        int cur = _drumVoiceCursor.TryGetValue(drumId, out var c) ? c : 0;
        if (cur < 0 || cur >= pool.Count) cur = 0;

        var voice = pool[cur];
        _drumVoiceCursor[drumId] = (cur + 1) % pool.Count;

        if (voice != null)
        {
            voice.clip = clip; // clip can change with kit
            // keep output group in sync in case inspector changed
            var g = GetMixerGroupForDrum(drumId);
            if (g != null) voice.outputAudioMixerGroup = g;
        }

        return voice;
    }

    private AudioMixerGroup GetMixerGroupForDrum(int drumId)
    {
        if (drumGroups != null && drumGroups.Length >= drumId && drumGroups[drumId - 1] != null)
            return drumGroups[drumId - 1];
        return drumMasterGroup;
    }


    private void ScheduleDrum(int drumId, double dspTime)
    {
        if (!_clipByDrumId.TryGetValue(drumId, out var clip) || clip == null)
            return;

        var src = GetOrCreateDrumVoice(drumId, clip);
        if (src == null) return;

        // Scheduled playback (tight timing)
        src.Stop();
        src.volume = 1f;
        src.PlayScheduled(dspTime);
    }

    private void PreviewDrum(int drumId)
    {
        if (!_clipByDrumId.TryGetValue(drumId, out var clip) || clip == null)
            return;

        var src = GetOrCreateDrumVoice(drumId, clip);
        if (src == null) return;

        src.Stop();
        src.volume = 1f;
        src.PlayOneShot(clip, 1f);
    }

    [SerializeField] private UIDocument uiDocument;

    [Header("UI Names")]
    public string playButtonName = "PlayButton";
    public string stopButtonName = "StopButton";
    public string drumGridName = "DrumStepGrid";

    [Header("Drum Buttons (Sampler page)")]
    public string btnKick = "DrumKick";
    public string btnSnare = "DrumSnare";
    public string btnClap = "DrumClap";
    public string btnHatClosed = "DrumHatC";
    public string btnHatOpen = "DrumHatO";
    public string btnRide = "DrumRide";
    public string btnRim = "DrumPerc";
    public string btnCrash = "DrumCrash";

    public int CurrentStepIndex => _stepIndex;
    /// <summary>
    /// Fired right before scheduling step 0 of each bar (quantized hook for CHAIN).
    /// The int parameter is a 1-based bar counter.
    /// </summary>
    public event Action<int> OnBarStart;
    public bool IsPlaying => _playing;

    [Header("Timing")]
    [Range(40, 200)] public float fallbackBpm = 107f;
    [Tooltip("How far ahead (seconds) to schedule notes.")]
    public float lookaheadSeconds = 0.10f;
    [Tooltip("Small start delay (seconds) when pressing Play so scheduling window can fill.")]
    public float startDelaySeconds = 0.05f;

    [Header("Runtime Sample Loading")]
    [Tooltip("Resources folder path to load drum clips from at runtime. Example: Assets/Resources/KMusic/Drums/*.wav")]
    public string resourcesDrumPath = "KMusic/Drums";

    [Header("Debug")]
    public bool verbose = false;

    [Tooltip("Sampler polyphony / voices. Higher prevents drums cutting off when many hits overlap.")]
    public int numVoices = 32;

    // 16 steps total in the 2x8 grid
    [SerializeField] private int steps = 16;

    [SerializeField] private string drumKitResourcesPath = "DrumKits";

    private DrumKit[] _kits;
    private int _kitIndex = 0;

    // UI
    private UnityEngine.UIElements.Label _kitNameLabel;
    private UnityEngine.UIElements.Button _kitPrevBtn;
    private UnityEngine.UIElements.Button _kitNextBtn;

    private const int LANES = 8;                        // 8 drums max in this UI
    private byte[] _stepMask = new byte[16];

    // --- Sampler (chop sequencer) ---
    private int[] _sampleChopByStep = new int[16]; // 0 = none, 1..16 = chop id

    [Header("Sampler Playback")]
    [Tooltip("How many overlapping sample voices we allow for chops.")]
    public int sampleVoices = 8;

    private readonly List<AudioSource> _sampleVoicePool = new();
    private int _sampleVoiceCursor = 0;

    private bool _didLoadSamplePattern = false;
    private bool _didLoadAppliedChops = false;
    
    // Tracks whether applied chops changed since last load
    private int _appliedRevisionSeen = -1;
private string _appliedResourcesPath = null;
    private AudioClip _appliedClip = null;
    private float[] _sliceStart01 = new float[16];
    private float[] _sliceEnd01 = new float[16];


    private UIDocument _doc;
    private VisualElement _root;
    private Button _playBtn;
    private Button _stopBtn;

    private StepGrid _grid;
    private StepGrid _gridSeq;
    private StepGrid _gridSample;
    private StepGrid _gridDrum;
    private Label _bpmLabel;

    private int _activeDrumId = 1; // current "view/brush" drum id 1..8

    // engine
    private AudioHelmClock _clock;
    private Sampler _sampler;

    // kit clips per drum id
    private readonly Dictionary<int, AudioClip> _clipByDrumId = new();

    // scheduling state (absolute step number since play start)
    private long _nextStepNumber = 0;      // monotonic step counter

    private bool _playing = false;
    private float _playBpm = -1f;          // cached bpm at Play
    private double _stepDur = 0.0;         // cached seconds per 16th note
    private int _stepIndex = 0; // 0..15
    private int _barCounter = 0;
    private double _nextStepDspTime = 0.0;
    private double _playStartDspTime = 0.0;
    private int _lastVisualStep = -999;
    private float _lastBpmShown = -1f;

    private int _lastPlayhead = -1;

    private static int DrumBit(int drumId) => 1 << (drumId - 1);

    private static bool IsAllZero(byte[] a)
    {
        if (a == null || a.Length == 0) return true;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != 0) return false;
        return true;
    }

    private void ToggleStepForActiveDrum(int stepIndex)
    {
        int bit = DrumBit(_activeDrumId);

        // toggle bit
        _stepMask[stepIndex] = (byte)(_stepMask[stepIndex] ^ bit);

        // update UI view for the currently selected drum
        RefreshDrumGridView();

        PreviewDrum(_activeDrumId);
    }

    private void RefreshDrumGridView()
    {
        // use the grid you actually bound
        var g = _gridDrum != null ? _gridDrum : _grid;
        if (g == null) return;

        int bit = DrumBit(_activeDrumId);

        _suppressGridCallbacks = true;
        try
        {
            for (int step = 0; step < 16; step++)
            {
                int r = step / 8;
                int c = step % 8;

                bool onForThisDrum = (_stepMask[step] & bit) != 0;
                g.SetValue(r, c, onForThisDrum ? _activeDrumId : 0);
            }
        }
        finally
        {
            _suppressGridCallbacks = false;
        }
    }

    private void PlayStep(int stepIndex, double dspTime)
    {
        int mask = _stepMask[stepIndex];
        if (mask == 0) return;

        for (int drumId = 1; drumId <= 8; drumId++)
        {
            int bit = DrumBit(drumId);
            if ((mask & bit) != 0)
                ScheduleDrum(drumId, dspTime);
        }
    }

    private static string DrumLabelForValue(int v)
    {
        if (v <= 0) return "";
        switch (v)
        {
            case 1: return "K";
            case 2: return "S";
            case 3: return "C";
            case 4: return "HC";
            case 5: return "HO";
            case 6: return "RD";
            case 7: return "RM";
            case 8: return "CR";
            default: return v.ToString();
        }
    }

    private bool _suppressGridEvents = false;

    private void UpdateVisualPlayhead(double now, double stepDur)
    {
        if (!_playing)
        {
            if (_lastPlayhead != -1)
            {
                _lastPlayhead = -1;

                _gridSeq?.SetPlayheadStep(-1);
                _gridSample?.SetPlayheadStep(-1);
                _gridDrum?.SetPlayheadStep(-1);
                _grid?.SetPlayheadStep(-1);
            }
            return;
        }

        // If you start playback with a scheduled offset, don’t show playhead until it actually begins
        if (now < _playStartDspTime)
            return;

        // Visual playhead step based on elapsed DSP time since play start
        double elapsed = now - _playStartDspTime;
        int current = ((int)Math.Floor(elapsed / stepDur)) % steps;

        if (current == _lastPlayhead)
            return;

        _lastPlayhead = current;

        _gridSeq?.SetPlayheadStep(current);
        _gridSample?.SetPlayheadStep(current);
        _gridDrum?.SetPlayheadStep(current);
        _grid?.SetPlayheadStep(current);
    }

    private void SetAllPlayheads(int step)
    {
        // your main SEQ step grid
        _gridSeq?.SetPlayheadStep(step);

        // sampler grids
        _gridSample?.SetPlayheadStep(step);
        _gridDrum?.SetPlayheadStep(step);

        // if you also track _grid as drumGrid alias
        _grid?.SetPlayheadStep(step);
    }

    // prevent recursion while we programmatically refresh grid
    private bool _suppressGridCallbacks = false;

    // mapping drum id -> midi note
    private readonly Dictionary<int, int> _noteByDrumId = new()
    {
        {1, 36}, // kick
        {2, 38}, // snare
        {3, 39}, // clap
        {4, 42}, // hh closed
        {5, 46}, // hh open
        {6, 51}, // ride
        {7, 37}, // rim
        {8, 49}, // crash
    };

    private void Awake()
    {
        _doc = GetComponent<UIDocument>();
        _app = GetComponent<KMusicApp>();
        if (_app == null)
            _app = FindObjectOfType<KMusicApp>();

        EnsureAudioHelmClock();
        EnsureSamplerEngine();

        // Restore saved kit index before loading kits.
        if (ProjectPrefs.HasKey(PrefKey_DrumKitIndex))
            _kitIndex = Mathf.Max(0, ProjectPrefs.GetInt(PrefKey_DrumKitIndex, 0));

        LoadKits();

#if UNITY_EDITOR
        // Editor convenience: try to scan the first kit folder if present.
        // NOTE: This does not exist in builds.
        TryLoadFirstKitFromAssets();
#endif

        // Always ensure runtime drums are available (Android/iOS/Standalone).
        // If the editor scan loaded something, we keep it.
        if (_clipByDrumId.Count == 0)
        {
            LoadKitFromResources();
        }

        if (_clipByDrumId.Count > 0)
        {
            BuildSamplerKeyzonesFromLoadedClips();
        }
        else
        {
            Debug.LogWarning("[KMusicDrumSequencer] No drum clips loaded. For runtime builds, put clips under Assets/Resources/KMusic/Drums/ and name them with keywords (kick/snare/clap/closed/open/ride/rim/crash).");
        }
    }

    private void OnEnable()
    {
        if (uiDocument == null)
            uiDocument = GetComponent<UIDocument>();

        if (uiDocument == null)
        {
            Debug.LogError("[DrumSequencer] UIDocument missing");
            return;
        }

        StartCoroutine(BindWhenReady()); // ✅ THIS WAS MISSING

        var root = uiDocument.rootVisualElement;

        root.schedule.Execute(() =>
        {
            var playBtn = root.Q<Button>("PlayButton");
            var stopBtn = root.Q<Button>("StopButton");
            var kitPrev = root.Q<Button>("KitPrev");
            var kitNext = root.Q<Button>("KitNext");

            if (playBtn != null && playBtn.text == "PLAY") { /* leave it */ }
            else if (playBtn != null) playBtn.text = "\u25B6";

            if (stopBtn != null && stopBtn.text == "STOP") { /* leave it */ }
            else if (stopBtn != null) stopBtn.text = "\u25A0";

            if (kitPrev != null && kitPrev.text == "PREV") { /* leave it */ }
            else if (kitPrev != null) kitPrev.text = "\u25C0";

            if (kitNext != null && kitNext.text == "NEXT") { /* leave it */ }
            else if (kitNext != null) kitNext.text = "\u25B6";

            Debug.Log($"[DrumSequencer] playBtn={playBtn!=null} stopBtn={stopBtn!=null}");

            if (playBtn != null)
                playBtn.clicked += () =>
                {
                    Debug.Log("PLAY CLICK");
                    OnPlayClicked(); // ✅ call the real play that sets dsp start etc
                };

            if (stopBtn != null)
                stopBtn.clicked += () =>
                {
                    Debug.Log("STOP CLICK");
                    OnStopClicked();
                };
        });
    
    }

    private void HookKitUI(VisualElement root)
    {
        _kitPrevBtn = root.Q<Button>("KitPrev");
        _kitNextBtn = root.Q<Button>("KitNext");
        _kitNameLabel = root.Q<Label>("KitName");

        Debug.Log($"[KIT UI] prev={(_kitPrevBtn!=null)} next={(_kitNextBtn!=null)} label={(_kitNameLabel!=null)}");

        if (_kitPrevBtn != null)
        {
            _kitPrevBtn.clickable = new Clickable(() => { Debug.Log("[KIT UI] Prev clicked"); PrevKit(); });
        }
        if (_kitNextBtn != null)
        {
            _kitNextBtn.clickable = new Clickable(() => { Debug.Log("[KIT UI] Next clicked"); NextKit(); });
        }

        // Force an initial label refresh so you can SEE it’s wired
        RefreshKitLabel();
    }

    private void RefreshKitLabel()
    {
        if (_kitNameLabel == null) return;
        if (_kits == null || _kits.Length == 0)
            _kitNameLabel.text = "NO KITS";
        else
            _kitNameLabel.text = _kits[_kitIndex] != null ? _kits[_kitIndex].kitName : $"KIT {_kitIndex+1}";
    }

    private System.Collections.IEnumerator BindWhenReady()
    {
        yield return null;
        yield return null;
        while (!BindUI())
            yield return null;

        _gridSeq?.SetPlayheadStep(-1);
        _gridSample?.SetPlayheadStep(-1);
        _gridDrum?.SetPlayheadStep(-1);
    }

    private bool BindUI()
    {
        if (_doc == null) return false;

        _root = _doc.rootVisualElement;
        if (_root == null) return false;

        // Block saves until we're fully bound + we've rendered the loaded pattern
        _allowSaving = false;

        // --- Find UI ---
        _playBtn = _root.Q<Button>(playButtonName);
        _stopBtn = _root.Q<Button>(stopButtonName);

        _gridDrum   = _root.Q<StepGrid>("DrumStepGrid") ?? _root.Q<StepGrid>(drumGridName);
        _gridSeq    = _root.Q<StepGrid>("StepGrid");
        _gridSample = _root.Q<StepGrid>("SampleStepGrid") ?? _root.Q<StepGrid>("SamplerStepGrid");

        // Back-compat alias (only if other code expects _grid)
        _grid = _gridDrum;

        _drumGrid = _gridDrum;

        _bpmLabel = _root.Q<Label>("BpmLabel");

        if (verbose)
            Debug.Log($"[KMusicDrumSequencer] bind play={(_playBtn != null)} stop={(_stopBtn != null)} drumGrid={(_gridDrum != null)}");

        if (_playBtn == null || _stopBtn == null || _gridDrum == null)
            return false;

        // Cache bus for drum volume faders.
        if (_app == null) _app = FindObjectOfType<KMusicApp>();
        _bus = _app != null ? _app.Bus : null;
        ApplySampleMasterVolume();
        // Keep mixer params in sync with ParameterBus changes.
        if (_bus != null)
        {
            if (_onBusChanged == null)
            {
                _onBusChanged = (id, v) =>
                {
                    if (id == "sample.master")
                        ApplySampleMasterVolume();

                    // Only drive mixer automatically if enabled
                    if (driveMixerFromBus && id != null && id.StartsWith("drum.vol", StringComparison.OrdinalIgnoreCase))
                        ApplyDrumMixerFromBus();
                };
            }

            _bus.OnChanged -= _onBusChanged;
            _bus.OnChanged += _onBusChanged;
        }

        // Push current drum faders into the mixer right away (if assigned).
        ApplyDrumMixerFromBus();


        // --- Kit UI ---
        _kitNameLabel = _root.Q<Label>("KitName");
        _kitPrevBtn   = _root.Q<Button>("KitPrev");
        _kitNextBtn   = _root.Q<Button>("KitNext");

        if (_kitPrevBtn != null) _kitPrevBtn.clicked += PrevKit;
        if (_kitNextBtn != null) _kitNextBtn.clicked += NextKit;

        WireDrumButtons();
        WireMuteButtons(_root);

        // Restore saved pattern / mutes / active drum.
        LoadDrumState();
        ApplyDrumMixerFromBus();
        _didLoadState = true;

        // Ensure currently loaded kit matches restored index.
        if (_kits != null && _kits.Length > 0)
            _kitIndex = Mathf.Clamp(_kitIndex, 0, _kits.Length - 1);
        if (_kits != null && _kits.Length > 0)
            ApplyKit(_kitIndex);

        // --- Grid visuals ---
        _gridDrum.EnableValueLabels(true, DrumLabel);
        _gridDrum.EnableValueTint(true, DrumTint);

        // IMPORTANT: start in a clean state, but do NOT accidentally write to _stepMask here
        _suppressGridEvents = true;
        _gridDrum.ClearAll();
        _gridDrum.SetPaintValue(_activeDrumId);
        _gridDrum.SetPlayheadStep(-1);
        _suppressGridEvents = false;

        // --- Events ---
        _gridDrum.OnCellValueChanged -= OnGridValueChanged;
        _gridDrum.OnCellValueChanged += OnGridValueChanged;

        _gridDrum.OnCellClicked -= OnGridCellClicked;
        _gridDrum.OnCellClicked += OnGridCellClicked;

        // --- Initial draw from existing mask ---
        RefreshGridForActiveDrum();

        // Make sure mute visuals match restored data.
        WireMuteButtons(_root);

        UpdateBpmLabel();

        // Now that load+draw is complete, allow saving.
        _allowSaving = true;

        return true;
    }

    private void ApplySampleMasterVolume()
    {
        // ✅ KnobElement drives ParameterBus, so read from bus
        float v = 1f;

        if (_bus != null)
            v = Mathf.Clamp01(_bus.GetValue("sample.master"));
        else
            v = Mathf.Clamp01(sampleMasterVolume); // fallback if bus not ready yet

        float lin = Mathf.Pow(v, 2.2f);

        for (int i = 0; i < _sampleVoicePool.Count; i++)
        {
            var s = _sampleVoicePool[i];
            if (s != null) s.volume = lin;
        }

        if (verbose)
            Debug.Log($"[SampleVol] bus={( _bus != null)} sample.master={v:0.00} voices={_sampleVoicePool.Count}");
    }

    private void WireMuteButtons(VisualElement root)
    {
        if (root == null) return;

        for (int i = 0; i < 8; i++)
        {
            int idx = i;
            string name = $"DrumMute{(i + 1):00}";
            var b = root.Q<Button>(name);
            if (b == null) continue;

            const string wiredClass = "km-drum-mute--wired";
            if (!b.ClassListContains(wiredClass))
            {
                b.AddToClassList(wiredClass);
                b.clicked += () =>
                {
                    _drumMute[idx] = !_drumMute[idx];
                    ApplyMuteButtonVisual(b, _drumMute[idx]);
                    SaveDrumState();
                    ApplyDrumMixerFromBus();
                };
            }

            ApplyMuteButtonVisual(b, _drumMute[idx]);
        }
    }

    private static void ApplyMuteButtonVisual(Button b, bool muted)
    {
        if (b == null) return;
        if (muted) b.AddToClassList("is-muted");
        else b.RemoveFromClassList("is-muted");
    }

    private void RefreshMuteUI()
    {
        try { WireMuteButtons(_root); } catch { }
    }

    private float GetDrumVolume01To08(int drumId)
    {
        drumId = Mathf.Clamp(drumId, 1, 8);
        if (_bus == null) return 1f;
        string id = $"drum.vol{drumId:00}";
        return Mathf.Clamp01(_bus.GetValue(id));
    }

    private bool IsDrumMuted(int drumId)
    {
        drumId = Mathf.Clamp(drumId, 1, 8);
        return _drumMute[drumId - 1];
    }

    // ---------------- SAVE/LOAD (PATCHED) ----------------
    // Goal: lane patterns must reliably persist.
    // - Primary: KMusicSaveState.SaveBytes/LoadBytes (your original path)
    // - Fallback: PlayerPrefs base64 string (works on Android even if SaveState path breaks)
    private void SaveDrumState()
    {
        // IMPORTANT: during startup/bind, we must NOT overwrite saved patterns with blank masks.
        if (!_allowSaving) return;

        try
        {
            // Namespaced keys for SaveState path
            string kMask  = ProjectPrefs.Key(PrefKey_DrumStepMask);
            string kMutes = ProjectPrefs.Key(PrefKey_DrumMutes);

            // --- step mask (16 bytes bitmask; each bit = lane on/off) ---
            if (_stepMask != null)
            {
                var b = new byte[_stepMask.Length];
                for (int i = 0; i < _stepMask.Length; i++)
                    b[i] = _stepMask[i];

                // Primary save (your existing system) — NOW namespaced
                try { KMusicSaveState.SaveBytes(kMask, b); } catch { }

                // Fallback save (base64 in PlayerPrefs) — already namespaced by ProjectPrefs
                try
                {
                    string b64 = Convert.ToBase64String(b);
                    ProjectPrefs.SetString(PrefKey_DrumStepMask_B64, b64);
                }
                catch { }
            }

            // --- mutes / ui state ---
            try { KMusicSaveState.SaveBools(kMutes, _drumMute); } catch { }

            ProjectPrefs.SetInt(PrefKey_DrumActive, _activeDrumId);
            ProjectPrefs.SetInt(PrefKey_DrumKitIndex, _kitIndex);
            ProjectPrefs.Save();

            if (verbose)
                Debug.Log($"[DRUM SAVE] b64Len={ProjectPrefs.GetString(PrefKey_DrumStepMask_B64, "").Length} mask0=0x{_stepMask[0]:X2} allowSaving={_allowSaving}");
        }
        catch { }
    }

    private void LoadDrumState()
    {
        // --- step mask (prefer KMusicSaveState, fallback to PlayerPrefs base64) ---
        byte[] loaded = null;

        string kMask = ProjectPrefs.Key(PrefKey_DrumStepMask);
        string kMutes = ProjectPrefs.Key(PrefKey_DrumMutes);

        // 1) Try SaveState
        try
        {
            loaded = KMusicSaveState.LoadBytes(kMask, 16);
        }
        catch
        {
            loaded = null;
        }

        // IMPORTANT: if SaveState returns blank-but-valid, treat as missing
        bool treatAsMissing = (loaded == null || loaded.Length == 0 || IsAllZero(loaded));

        // 2) Fallback to PlayerPrefs base64
        if (treatAsMissing)
        {
            try
            {
                if (ProjectPrefs.HasKey(PrefKey_DrumStepMask_B64))
                {
                    string b64 = ProjectPrefs.GetString(PrefKey_DrumStepMask_B64, "");
                    if (!string.IsNullOrEmpty(b64))
                        loaded = Convert.FromBase64String(b64);
                }
            }
            catch
            {
                loaded = null;
            }
        }

        if (loaded != null && loaded.Length > 0)
        {
            if (_stepMask == null || _stepMask.Length != 16)
                _stepMask = new byte[16];

            int n = Mathf.Min(_stepMask.Length, loaded.Length);
            Array.Copy(loaded, _stepMask, n);

            for (int i = n; i < _stepMask.Length; i++)
                _stepMask[i] = 0;
        }
        else
        {
            if (_stepMask != null)
                Array.Clear(_stepMask, 0, _stepMask.Length);
        }

        // --- restore mutes ---
        try
        {
            var loadedMutes = KMusicSaveState.LoadBools(kMutes, 8);
            if (loadedMutes != null)
            {
                for (int i = 0; i < _drumMute.Length; i++)
                    _drumMute[i] = (i < loadedMutes.Length) ? loadedMutes[i] : false;
            }
            else
            {
                Array.Clear(_drumMute, 0, _drumMute.Length);
            }
        }
        catch
        {
            Array.Clear(_drumMute, 0, _drumMute.Length);
        }

        // --- restore active drum ---
        _activeDrumId = Mathf.Clamp(ProjectPrefs.GetInt(PrefKey_DrumActive, 1), 1, 8);

        // --- restore kit index ---
        _kitIndex = Mathf.Max(0, ProjectPrefs.GetInt(PrefKey_DrumKitIndex, 0));

        if (verbose)
        {
            Debug.Log(
                $"[DRUM LOAD] mask0=0x{_stepMask[0]:X2} " +
                $"mutes=[{string.Join(",", _drumMute)}] active={_activeDrumId} kit={_kitIndex}"
            );
        }
    } 
    
    // ---------------- END SAVE/LOAD (PATCHED) ----------------
    private void OnApplicationPause(bool pause)
    {
        if (pause) SaveDrumState();
    }

    private void OnApplicationQuit()
    {
        SaveDrumState();
    }

    private void OnDestroy()
    {
        SaveDrumState();
    }

    public void SetStepLane(int step, int lane, bool on)
    {
        // steps should match your sequencer length (usually 16)
        if (_stepMask == null) return;
        if (step < 0 || step >= _stepMask.Length) return;
        if (lane < 0 || lane >= LANES) return;

        byte bit = (byte)(1 << lane);

        if (on) _stepMask[step] |= bit;
        else    _stepMask[step] &= (byte)~bit;
    }

    private void LoadKits()
    {
        _kits = Resources.LoadAll<DrumKit>(drumKitResourcesPath);
        if (_kits == null || _kits.Length == 0)
        {
            Debug.LogWarning($"[KMusicDrumSequencer] No DrumKits found in Resources/{drumKitResourcesPath}");
            return;
        }

        _kitIndex = Mathf.Clamp(_kitIndex, 0, _kits.Length - 1);
        ApplyKit(_kitIndex);
    }

    private void NextKit()
    {
        if (_kits == null || _kits.Length == 0) return;
        _kitIndex = (_kitIndex + 1) % _kits.Length;
        ApplyKit(_kitIndex);
    }

    private void PrevKit()
    {
        if (_kits == null || _kits.Length == 0) return;
        _kitIndex = (_kitIndex - 1 + _kits.Length) % _kits.Length;
        ApplyKit(_kitIndex);
    }

    private void ApplyKit(int index)
    {
        if (_kits == null || _kits.Length == 0) return;
        var kit = _kits[index];
        if (kit == null) return;

        // (Optional) stop currently playing voices so you don’t hear old kit tails
        _sampler.AllNotesOff();

        // Update clip map used by your scheduler
        for (int drumId = 1; drumId <= LANES; drumId++)
            _clipByDrumId[drumId] = kit.GetClipByDrumId(drumId);

        // Rebuild sampler keyzones so NoteOnScheduled plays the new clips
        RebuildSamplerKeyzonesFromClipMap();

        if (_kitNameLabel != null)
            _kitNameLabel.text = string.IsNullOrEmpty(kit.kitName) ? $"KIT {index + 1}" : kit.kitName;

        if (verbose)
            Debug.Log($"[KMusicDrumSequencer] Applied kit {index + 1}/{_kits.Length}: {kit.kitName}");

        // DO NOT save patterns during startup/bind unless saving is enabled.
        if (_allowSaving)
            SaveDrumState();
    }

    private void RebuildSamplerKeyzonesFromClipMap()
    {
        if (_sampler == null) return;

        if (_sampler.keyzones == null)
            _sampler.keyzones = new List<AudioHelm.Keyzone>();
        else
            _sampler.keyzones.Clear();

        for (int drumId = 1; drumId <= LANES; drumId++)
        {
            if (!_clipByDrumId.TryGetValue(drumId, out var clip) || clip == null)
                continue;

            int note = _noteByDrumId.TryGetValue(drumId, out var n) ? n : 36;

            var kz = new AudioHelm.Keyzone
            {
                minKey = note,
                maxKey = note,
                rootKey = note,
                audioClip = clip,
                // leave mixer null unless you’re using Unity AudioMixerGroups
            };

            _sampler.keyzones.Add(kz);
        }
    }

    private void OnDisable()
    {
        SaveDrumState();
        if (_grid != null)
        {
            _grid.OnCellValueChanged -= OnGridValueChanged;
            _grid.OnCellClicked -= OnGridCellClicked;
        }
    }

    private void WireDrumButtons()
    {
        WireDrumButton(btnKick, 1);
        WireDrumButton(btnSnare, 2);
        WireDrumButton(btnClap, 3);
        WireDrumButton(btnHatClosed, 4);
        WireDrumButton(btnHatOpen, 5);
        WireDrumButton(btnRide, 6);
        WireDrumButton(btnRim, 7);
        WireDrumButton(btnCrash, 8);

        SelectDrum(1);
    }

    private void WireDrumButton(string name, int drumId)
    {
        var b = _root?.Q<Button>(name);
        if (b == null) return;

        const string wiredClass = "km-drum-btn--wired";
        if (b.ClassListContains(wiredClass))
            return;

        b.AddToClassList(wiredClass);
        b.clicked += () => SelectDrum(drumId);
    }

    private void SelectDrum(int drumId)
    {
        EnsureStateLoaded();

        _activeDrumId = (byte)Mathf.Clamp(drumId, 1, 8);

        if (_grid != null)
        {
            _grid.SetPaintValue(_activeDrumId);
            RefreshGridForActiveDrum(); // prevents “snare turns into kick” in the UI
        }

        // update button highlight class
        string[] names = { btnKick, btnSnare, btnClap, btnHatClosed, btnHatOpen, btnRide, btnRim, btnCrash };
        for (int i = 0; i < names.Length; i++)
        {
            var b = _root?.Q<Button>(names[i]);
            if (b == null) continue;

            if (i + 1 == _activeDrumId) b.AddToClassList("km-drum-btn--active");
            else b.RemoveFromClassList("km-drum-btn--active");
        }

        // Audition when selecting a drum, so the user can hear it before painting.
        TriggerDrumNow(_activeDrumId);

        // DO NOT save patterns during startup/bind unless saving is enabled.
        if (_allowSaving)
            SaveDrumState();
    }

    private void RefreshGridView()
    {
        if (_grid == null) return;

        int lane = _activeDrumId - 1;
        int bit = 1 << lane;

        _suppressGridCallbacks = true;

        for (int r = 0; r < 2; r++)
        for (int c = 0; c < 8; c++)
        {
            int step = (r * 8) + c;
            int mask = _stepMask[step];

            bool active = (mask & bit) != 0;

            _grid.SetValue(r, c, active ? _activeDrumId : 0);
        }

        _suppressGridCallbacks = false;
    }

    private void OnGridValueChanged(int r, int c, int v)
    {
        // Ignore programmatic updates
        if (_suppressGridCallbacks || _suppressGridEvents) return;

        // First real user edit => allow saving from now on
        _allowSaving = true;

        EnsureStateLoaded();

        if (r < 0 || r >= 2 || c < 0 || c >= 8) return;

        int step = (r * 8) + c;
        if (step < 0 || step >= steps) return;

        int lane = _activeDrumId - 1;
        byte bit = (byte)(1 << lane);

        // StepGrid sends v==0 when clearing, v>0 when painting.
        // We treat it as ON/OFF for the current lane.
        if (v > 0) _stepMask[step] = (byte)(_stepMask[step] | bit);
        else       _stepMask[step] = (byte)(_stepMask[step] & ~bit);

        // Keep the UI cell consistent with the current view:
        // show active drum ID if bit is set, else 0.
        int shown = 0;

        _suppressGridCallbacks = true;
        try
        {
            shown = ((_stepMask[step] & bit) != 0) ? _activeDrumId : 0;
            _grid.SetValue(r, c, shown);
        }
        finally
        {
            _suppressGridCallbacks = false;
        }

        // ✅ Audition on edit (even while playing)
        if (v > 0)
            TriggerDrumNow(_activeDrumId);

        SaveDrumState();

        if (verbose)
            Debug.Log($"[DRUM GRID] r={r} c={c} step={step} active={_activeDrumId} v={v} shown={shown} mask=0x{_stepMask[step]:X2}");
    }

    private void OnGridCellClicked(int r, int c)
    {
        if (_suppressGridCallbacks || _suppressGridEvents) return;

        EnsureStateLoaded();
        
        if (_sampler == null) return;
        if (r < 0 || r >= 2 || c < 0 || c >= 8) return;

        int step = (r * 8) + c;
        if (step < 0 || step >= steps) return;

        // audition: if the active layer has a hit here, play it; otherwise play brush anyway
        int lane = _activeDrumId - 1;
        byte bit = (byte)(1 << lane);
        bool on = (_stepMask[step] & bit) != 0;

        TriggerDrumNow(_activeDrumId);
    }

    private void RefreshGridForActiveDrum()
    {
        if (_gridDrum == null || _stepMask == null) return;

        int lane = Mathf.Clamp(_activeDrumId - 1, 0, 7);
        byte bit = (byte)(1 << lane);

        _suppressGridCallbacks = true;
        try
        {
            for (int step = 0; step < 16; step++)
            {
                int r = step / 8;
                int c = step % 8;

                bool on = (_stepMask[step] & bit) != 0;
                int cellValue = on ? _activeDrumId : 0;

                _gridDrum.SetValue(r, c, cellValue);
            }

            _gridDrum.SetPaintValue(_activeDrumId);
        }
        finally
        {
            _suppressGridCallbacks = false;
        }
    }


    // ----------------------------
    // Sampler chop scheduling
    // ----------------------------

    public void SetSampleStep(int stepIndex, int chopId)
    {
        if (stepIndex < 0 || stepIndex >= steps) return;
        _sampleChopByStep[stepIndex] = Mathf.Clamp(chopId, 0, 16);
    }

    private void EnsureSamplePatternLoaded()
    {
        if (_didLoadSamplePattern) return;

        // KMusicSaveState stores a flat int array for the 2x8 grid.
        var flat = KMusicSaveState.LoadIntArray(ProjectPrefs.Key(PrefKey_SampleStepGrid), 16);
        if (flat != null && flat.Length >= 16)
        {
            for (int i = 0; i < 16; i++)
                _sampleChopByStep[i] = Mathf.Clamp(flat[i], 0, 16);
        }
        else
        {
            for (int i = 0; i < 16; i++)
                _sampleChopByStep[i] = 0;
        }

        _didLoadSamplePattern = true;
    }

    private void EnsureAppliedChopsLoaded()
    {
        int rev = KMusicChopState.AppliedRevision;
        if (_didLoadAppliedChops && _appliedRevisionSeen == rev) return;
if (!KMusicChopState.TryLoadApplied(out var resPath, out var s01, out var e01))
        {
            _appliedResourcesPath = null;
            _appliedClip = null;
            _didLoadAppliedChops = true;
            _appliedRevisionSeen = rev;
            return;
        }

        _appliedResourcesPath = resPath;
        _appliedClip = null;

        // If this is a Resources path, try Resources.Load
        if (!string.IsNullOrEmpty(resPath) && !resPath.StartsWith("cached:"))
        {
            _appliedClip = Resources.Load<AudioClip>(resPath);
        }

        // If Resources failed (Android device track), fall back to runtime cached clip
        if (_appliedClip == null)
        {
            if (KMusicChopState.TryGetCachedClip(resPath, out var cachedForId))
                _appliedClip = cachedForId;
            else if (KMusicChopState.TryGetCachedClip(out var cachedAny))
                _appliedClip = cachedAny;
        }

        if (_appliedClip == null)
        {
            Debug.LogWarning($"[Sampler] Applied chops refer to missing AudioClip (resPath='{resPath}').");
        }

        // Copy arrays (defensive)
        for (int i = 0; i < 16; i++)
        {
            _sliceStart01[i] = Mathf.Clamp01(s01[i]);
            _sliceEnd01[i] = Mathf.Clamp01(e01[i]);
            if (_sliceEnd01[i] < _sliceStart01[i]) _sliceEnd01[i] = _sliceStart01[i];
        }

        EnsureSampleVoices();

        Debug.Log($"[Sampler] Loaded applied chops for '{resPath}' (clip={( _appliedClip != null ? _appliedClip.name : "null")}).");

        _didLoadAppliedChops = true;
        _appliedRevisionSeen = rev;
    }

    private void EnsureSampleVoices()
    {
        int target = Mathf.Clamp(sampleVoices, 1, 32);

        // If pool already exists, top up if needed.
        while (_sampleVoicePool.Count < target)
        {
            var go = new GameObject($"SampleVoice_{_sampleVoicePool.Count + 1}");
            go.transform.SetParent(this.transform, false);

            var src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.loop = false;
            src.spatialBlend = 0f;
            src.volume = 1f;

            _sampleVoicePool.Add(src);
        }

        ApplySampleMasterVolume();
    }

    private void ScheduleSampleChop(int chopId, double dspTime, double stepDurSeconds)
    {
        if (chopId <= 0 || chopId > 16) return;

        EnsureAppliedChopsLoaded();
        if (_appliedClip == null) return;

        float s01 = _sliceStart01[chopId - 1];
        float e01 = _sliceEnd01[chopId - 1];
        if (e01 <= s01) return;

        EnsureSampleVoices();

        // ✅ sequencer voice: always use voice 0.
        // (Using index 1 breaks when sampleVoices==1 on Android: pool.Count==1 => NULL => no playback.)
        var src = (_sampleVoicePool.Count > 0) ? _sampleVoicePool[0] : null;
        if (src == null) return;

        // Compute slice timing
        double sliceDur = (e01 - s01) * _appliedClip.length;

        // ✅ play full chop length (to next marker)
        double dur = Math.Max(0.01, Math.Min(sliceDur, 4.0)); // cap at 4s (tweak)

        // Compute start sample
        int startSample = Mathf.Clamp(
            (int)(s01 * _appliedClip.samples),
            0,
            Mathf.Max(0, _appliedClip.samples - 1)
        );

        // ✅ IMPORTANT ORDER:
        // Stop first, then set seek, then schedule start/end.
        src.Stop();
        src.clip = _appliedClip;
        src.loop = false;
        src.timeSamples = startSample;

        // schedule
        double start = dspTime;
        double end = start + dur;

        src.PlayScheduled(start);
        src.SetScheduledEndTime(end);

        if (verbose)
            Debug.Log($"[Sampler] SCHED chop={chopId} t={start:0.000} dur={dur:0.000} slice={s01:0.000}->{e01:0.000} sliceDur={sliceDur:0.000}");
    }

    private void OnPlayClicked()
    {
        if (_playing) return;

        _playBpm = GetBpm();
        _stepDur = 60.0 / Math.Max(1.0, _playBpm) / 4.0; // 16th
        _nextStepNumber = 0;
        _stepIndex = 0; // ✅ reset so audio + visuals start on step 0
        _lastVisualStep = -999;
        _barCounter = 0;

        _playing = true;

        // Sampler: load latest sample grid + applied chops.
        // Sampler: only load from prefs if nothing has been applied in-memory (eg first launch).
        if (!_didLoadSamplePattern)
            EnsureSamplePatternLoaded();

        // Always allow chops to refresh if revision changed (project load bumps AppliedRevision).
        EnsureAppliedChopsLoaded();

        _playStartDspTime = AudioSettings.dspTime + startDelaySeconds;
        _nextStepDspTime = _playStartDspTime;

        if (_clock != null)
        {
            _clock.bpm = _playBpm;
            _clock.pause = false;
            _clock.StartScheduled(_playStartDspTime);
        }

        if (_grid != null)
            _grid.SetPlayheadStep(-1);

        if (verbose)
            Debug.Log($"[KMusicDrumSequencer] PLAY bpm={_playBpm:0.##} stepDur={_stepDur:0.0000} start={_playStartDspTime:0.000}");
    }

    private void OnStopClicked()
    {
        if (!_playing) return;

        _playing = false;

        _stepIndex = 0;
        _lastPlayhead = -1;

        if (_clock != null)
            _clock.pause = true;

        if (_sampler != null)
            _sampler.AllNotesOff();

        if (_grid != null)
            _grid.SetPlayheadStep(-1);

        if (verbose) Debug.Log("[KMusicDrumSequencer] STOP");
    }

    private void Update()
    {
        if (!_playing || _sampler == null) return;

        float bpm = GetBpm();
        if (_clock != null) _clock.bpm = bpm;

        // ✅ realtime BPM: recompute step duration from current bpm
        double stepDurLive = 60.0 / Math.Max(1.0, (double)bpm) / 4.0; // 16th note

        double now = AudioSettings.dspTime;
        double windowEnd = now + lookaheadSeconds;

        // ✅ if tempo changed, apply immediately by re-anchoring next step
        // (prevents “must stop/play”)
        if (Math.Abs(stepDurLive - _stepDur) > 0.000001)
        {
            _stepDur = stepDurLive;
            _nextStepDspTime = now + _stepDur;
        }

        // Use the actual step duration we’re scheduling with
        double stepDur = _stepDur;

        UpdateVisualPlayhead(now, stepDur);
        UpdateBpmLabel();

        while (_nextStepDspTime < windowEnd)
        {
            int step = _stepIndex;

            // Quantized bar boundary (before reading masks for step 0)
            if (step == 0)
            {
                _barCounter++;
                try { OnBarStart?.Invoke(_barCounter); } catch { }
            }

            byte mask = _stepMask[step];

            if (mask != 0)
            {
                for (int lane = 0; lane < LANES; lane++)
                {
                    byte bit = (byte)(1 << lane);
                    if ((mask & bit) == 0) continue;

                    int drumId = lane + 1;

                    if (_clipByDrumId.TryGetValue(drumId, out var clip) && clip != null)
                    {
                        int note = _noteByDrumId.TryGetValue(drumId, out var n) ? n : 36;
                        double end = _nextStepDspTime + Math.Max(0.05, clip.length);

                        if (verbose)
                            Debug.Log($"[KMusicDrumSequencer] SCHED drumId={drumId} note={note} clip={clip.name} t={_nextStepDspTime:0.000}");

                        if (routeDrumsToMixer)
                        {
                            // Use per-drum AudioSources so Unity AudioMixer meters/gain work per channel.
                            if (!IsDrumMuted(drumId))
                                ScheduleDrum(drumId, _nextStepDspTime);
                        }
                        else
                        {
                            float vel = GetDrumGain(drumId);
                            if (vel > 0f)
                                _sampler.NoteOnScheduled(note, vel, _nextStepDspTime, end);
                        }
                    }
                    else if (verbose)
                    {
                        Debug.LogWarning($"[KMusicDrumSequencer] Missing clip for drumId={drumId} (lane={lane}).");
                    }
                }
            }

            // Sampler chop at this step (if any)
            int chopId = _sampleChopByStep[step];
            if (chopId != 0)
                ScheduleSampleChop(chopId, _nextStepDspTime, stepDur);

            _stepIndex = (_stepIndex + 1) % steps;
            _nextStepDspTime += stepDur;
        }

        ApplySampleMasterVolume();
    }

    // ----------------------------
    // CHAIN support helpers
    // ----------------------------

    public void SetAllowSaving(bool on)
    {
        _allowSaving = on;
    }

    // Project save/load helpers
    public bool[] CaptureDrumMutes()
    {
        var b = new bool[_drumMute.Length];
        Array.Copy(_drumMute, b, _drumMute.Length);
        return b;
    }

    public void ApplyDrumMutes(bool[] m)
    {
        Array.Clear(_drumMute, 0, _drumMute.Length);
        if (m != null)
            Array.Copy(m, _drumMute, Mathf.Min(m.Length, _drumMute.Length));
        RefreshMuteUI();
    }

    public int CaptureActiveDrumId() => _activeDrumId;

    public void ApplyActiveDrumId(int drumId)
    {
        _activeDrumId = Mathf.Clamp(drumId, 1, 8);

        try
        {
            if (_gridDrum != null)
                _gridDrum.SetPaintValue(_activeDrumId);

            RefreshGridForActiveDrum();
        }
        catch { }
    }

    public int CaptureKitIndex() => _kitIndex;

    public void ApplyKitIndex(int kitIndex)
    {
        _kitIndex = Mathf.Max(0, kitIndex);
        try { ApplyKit(_kitIndex); } catch { }
    }

    public byte[] CaptureDrumMask()
    {
        var b = new byte[steps];
        if (_stepMask != null)
            Array.Copy(_stepMask, b, Mathf.Min(_stepMask.Length, b.Length));
        return b;
    }

    public void ApplyDrumMask(byte[] mask)
    {
        if (_stepMask == null || _stepMask.Length != steps)
            _stepMask = new byte[steps];

        // 1) clear internal data
        Array.Clear(_stepMask, 0, _stepMask.Length);

        // 2) clear UI hard (prevents leftover lit cells)
        if (_gridDrum != null)
        {
            _suppressGridEvents = true;
            _suppressGridCallbacks = true;
            try
            {
                _gridDrum.ClearAll();
                _gridDrum.SetPlayheadStep(-1);
            }
            finally
            {
                _suppressGridCallbacks = false;
                _suppressGridEvents = false;
            }
        }

        // 3) copy loaded data
        if (mask != null)
            Array.Copy(mask, _stepMask, Mathf.Min(mask.Length, _stepMask.Length));

        // 4) redraw using your real drawer
        try { RefreshGridForActiveDrum(); } catch { }
    }

    public int[] CaptureSampleStepsFlat()
    {
        var v = new int[steps];
        if (_sampleChopByStep != null)
            Array.Copy(_sampleChopByStep, v, Mathf.Min(_sampleChopByStep.Length, v.Length));
        return v;
    }

    public void ApplySampleStepsFlat(int[] flat)
    {
        if (_sampleChopByStep == null || _sampleChopByStep.Length != steps)
            _sampleChopByStep = new int[steps];

        Array.Clear(_sampleChopByStep, 0, _sampleChopByStep.Length);
        if (flat != null)
            Array.Copy(flat, _sampleChopByStep, Mathf.Min(flat.Length, _sampleChopByStep.Length));

        _didLoadSamplePattern = true; // ✅ critical: prevents Play from overwriting from prefs    
    }
    
    private int _auditionToken = 0;

    public void AuditionChop(int chopId)
    {
        if (chopId <= 0 || chopId > 16) return;

        EnsureAppliedChopsLoaded();
        if (_appliedClip == null) return;

        float s01 = _sliceStart01[chopId - 1];
        float e01 = _sliceEnd01[chopId - 1];
        if (e01 <= s01) return;

        EnsureSampleVoices();

        // ✅ PO-style monophonic: audition kills ALL sample voices (seq + audition)
        for (int i = 0; i < _sampleVoicePool.Count; i++)
        {
            var v = _sampleVoicePool[i];
            if (v != null) v.Stop();
        }

        // reserve voice 0 for audition
        var src = _sampleVoicePool.Count > 0 ? _sampleVoicePool[0] : null;
        if (src == null) return;

        double sliceDur = (e01 - s01) * _appliedClip.length;
        float dur = (float)Math.Max(0.02, sliceDur);

        int startSample = Mathf.Clamp(
            (int)(s01 * _appliedClip.samples),
            0,
            Mathf.Max(0, _appliedClip.samples - 1)
        );

        // hard-cancel anything on this voice
        _auditionToken++;
        int token = _auditionToken;

        src.Stop();
        src.clip = _appliedClip;
        src.loop = false;
        src.timeSamples = startSample;

        // ✅ immediate start
        src.Play();

        // ✅ stop after slice duration (realtime, not affected by timescale)
        StartCoroutine(StopAuditionAfter(src, dur, token));

        if (verbose)
            Debug.Log($"[Sampler] AUDITION_IMMEDIATE chop={chopId} dur={dur:0.000} slice={s01:0.000}->{e01:0.000}");
    }

    private System.Collections.IEnumerator StopAuditionAfter(AudioSource src, float dur, int token)
    {
        yield return new WaitForSecondsRealtime(dur);

        // only stop if nothing newer started since
        if (token != _auditionToken) yield break;

        if (src != null) src.Stop();
    }

    private void UpdateBpmLabel()
    {
        if (_bpmLabel == null) return;

        float bpm = GetBpm();
        if (Mathf.Abs(bpm - _lastBpmShown) < 0.01f) return;

        _lastBpmShown = bpm;
        _bpmLabel.text = $"{Mathf.RoundToInt(bpm)} BPM";
    }

    private void TriggerDrumNow(int drumId)
    {
        if (_sampler == null) return;
        if (!_noteByDrumId.TryGetValue(drumId, out var note)) return;
        if (!_clipByDrumId.TryGetValue(drumId, out var clip) || clip == null) return;

        double start = AudioSettings.dspTime;
        double end = start + Math.Max(0.05, clip.length);

        if (verbose)
            Debug.Log($"[KMusicDrumSequencer] TRIG drumId={drumId} note={note} clip={clip.name}");

        float vel = GetDrumGain(drumId);
        if (vel > 0f)
            _sampler.NoteOnScheduled(note, vel, start, end);
    }

    private float GetBpm()
    {
        if (_app != null && _app.Bus != null && _app.Bus.TryGet("tempo", out var p))
            return p.Value;
        return fallbackBpm;
    }

    private string DrumLabel(int v)
    {
        return v switch
        {
            1 => "K",
            2 => "S",
            3 => "C",
            4 => "HC",
            5 => "HO",
            6 => "RD",
            7 => "RM",
            8 => "X",
            _ => ""
        };
    }

    private Color DrumTint(int v)
    {
        return v switch
        {
            1 => new Color(0.90f, 0.22f, 0.27f, 1f), // kick
            2 => new Color(0.96f, 0.50f, 0.00f, 1f), // snare
            3 => new Color(1.00f, 0.30f, 0.60f, 1f), // clap
            4 => new Color(0.00f, 0.90f, 1.00f, 1f), // hat c
            5 => new Color(0.30f, 0.76f, 0.97f, 1f), // hat o
            6 => new Color(0.60f, 0.36f, 0.90f, 1f), // ride
            7 => new Color(0.50f, 0.93f, 0.60f, 1f), // rim
            8 => new Color(1.00f, 0.84f, 0.04f, 1f), // crash
            _ => Color.white
        };
    }

    private void EnsureAudioHelmClock()
    {
        _clock = FindObjectOfType<AudioHelmClock>();
        if (_clock == null)
        {
            var go = new GameObject("AudioHelmClock");
            _clock = go.AddComponent<AudioHelmClock>();
            _clock.bpm = fallbackBpm;
            _clock.pause = true;
        }
        else
        {
            _clock.pause = true;
        }
    }

    private void EnsureSamplerEngine()
    {
        _sampler = FindObjectOfType<Sampler>();

        if (_sampler == null)
        {
            var go = new GameObject("KMusicDrumSampler");
            var src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.mute = false;
            src.volume = 1f;

            _sampler = go.AddComponent<Sampler>();
        }

        // Ensure these apply whether we created or found it.
        _sampler.velocityTracking = 1.0f;   // ✅ moved here
        _sampler.numVoices = Mathf.Max(1, numVoices);

        if (_sampler.keyzones == null)
            _sampler.keyzones = new List<Keyzone>();
        else
            _sampler.keyzones.Clear();

        if (verbose)
            Debug.Log($"[KMusicDrumSequencer] Sampler ready. voices={_sampler.numVoices}");
    }

    // -------- Runtime drum loading (Android-safe) --------

    private void LoadKitFromResources()
    {
        _clipByDrumId.Clear();

        var clips = Resources.LoadAll<AudioClip>(resourcesDrumPath);

        if (verbose)
            Debug.Log($"[KMusicDrumSequencer] Resources.LoadAll('{resourcesDrumPath}') => {clips?.Length ?? 0} clips");

        if (clips == null || clips.Length == 0)
        {
            Debug.LogWarning($"[KMusicDrumSequencer] No clips found in Resources at '{resourcesDrumPath}'. Put files in Assets/Resources/{resourcesDrumPath}/");
            return;
        }

        // Prefer explicit keywords in clip *name* (Unity uses asset name without extension).
        AudioClip kick = FindClipByKeyword(clips, "kick");
        AudioClip snare = FindClipByKeyword(clips, "snare");
        AudioClip clap = FindClipByKeyword(clips, "clap");
        AudioClip hatc = FindClipByKeyword(clips, "hh closed") ?? FindClipByKeyword(clips, "hat closed") ?? FindClipByKeyword(clips, "closed");
        AudioClip hato = FindClipByKeyword(clips, "hh open") ?? FindClipByKeyword(clips, "hat open") ?? FindClipByKeyword(clips, "open");
        AudioClip ride = FindClipByKeyword(clips, "ride") ?? FindClipByKeyword(clips, "perc");
        AudioClip rim = FindClipByKeyword(clips, "rim");
        AudioClip crash = FindClipByKeyword(clips, "crash");

        _clipByDrumId[1] = kick;
        _clipByDrumId[2] = snare;
        _clipByDrumId[3] = clap;
        _clipByDrumId[4] = hatc;
        _clipByDrumId[5] = hato;
        _clipByDrumId[6] = ride;
        _clipByDrumId[7] = rim;
        _clipByDrumId[8] = crash;

        // Helpful warnings so device logs instantly show what's missing.
        WarnIfMissing(1, "kick");
        WarnIfMissing(2, "snare");
        WarnIfMissing(3, "clap");
        WarnIfMissing(4, "closed hat");
        WarnIfMissing(5, "open hat");
        WarnIfMissing(6, "ride/perc");
        WarnIfMissing(7, "rim");
        WarnIfMissing(8, "crash");

        if (verbose)
        {
            for (int id = 1; id <= 8; id++)
            {
                _clipByDrumId.TryGetValue(id, out var clip);
                Debug.Log($"[KMusicDrumSequencer] drumId={id} clip={(clip ? clip.name : "NULL")}");
            }
        }
    }

    private void ApplyDrumMixerFromBus()
    {
        if (drumMixer == null) return;
        if (!driveMixerFromBus) return; // ✅ allow manual mixer control

        for (int drumId = 1; drumId <= 8; drumId++)
        {
            float t01 = 1f;
            if (_bus != null) t01 = Mathf.Clamp01(_bus.GetValue($"drum.vol{drumId:00}"));

            float db = Vol01ToDbWithPlus6(t01);
            if (IsDrumMuted(drumId)) db = -80f;

            string param = $"{drumVolParamPrefix}{drumId:00}{drumVolParamSuffix}";
            drumMixer.SetFloat(param, db);
        }
    }

    /// <summary>
    /// Maps your existing 0..1 drum fader into a mixer-style dB range:
    /// - 0.00 -> -80 dB (effectively silent)
    /// - 0.80 -> 0 dB (unity, matches your old "default ~ loud enough")
    /// - 1.00 -> +6 dB (headroom above unity)
    /// </summary>
    private static float Vol01ToDbWithPlus6(float t01)
    {
        t01 = Mathf.Clamp01(t01);

        if (t01 <= 0.0001f)
            return -80f;

        // Normalize so the UI range actually reaches +6
        float normalized = Mathf.InverseLerp(0f, 1f, t01);

        // Mixer-style taper
        float db;

        if (normalized < 0.8f)
        {
            float u = normalized / 0.8f;
            db = Mathf.Lerp(-80f, 0f, u);
        }
        else
        {
            float u = (normalized - 0.8f) / 0.2f;
            db = Mathf.Lerp(0f, 6f, u);
        }

        return db;
    }

    private float GetDrumGain(int drumId)
    {
        if (IsDrumMuted(drumId))
            return 0f;

        float linear = 1f;

        if (_app != null && _app.Bus != null)
            linear = _app.Bus.GetValue($"drum.vol{drumId:00}");

        linear = Mathf.Clamp01(linear);
        if (linear <= 0f) return 0f;

        // pro mixer curve
        return Mathf.Pow(linear, 2.2f);
    }

    private void WarnIfMissing(int drumId, string label)
    {
        if (!_clipByDrumId.TryGetValue(drumId, out var clip) || clip == null)
            Debug.LogWarning($"[KMusicDrumSequencer] Missing '{label}' clip (drumId={drumId}) from Resources/{resourcesDrumPath}. Ensure the asset name contains the keyword.");
    }

    private AudioClip FindClipByKeyword(AudioClip[] clips, string keyword)
    {
        keyword = keyword.ToLowerInvariant();

        foreach (var c in clips)
        {
            if (c == null) continue;

            var name = c.name.ToLowerInvariant();
            if (name.Contains(keyword))
                return c;
        }

        return null;
    }

    private void BuildSamplerKeyzonesFromLoadedClips()
    {
        if (_sampler == null) return;

        if (_sampler.keyzones == null)
        {
            Debug.LogWarning("[KMusicDrumSequencer] Sampler.keyzones is null.");
            return;
        }

        _sampler.keyzones.Clear();

        for (int id = 1; id <= 8; id++)
        {
            if (!_clipByDrumId.TryGetValue(id, out var clip) || clip == null)
                continue;

            int note = _noteByDrumId[id];

            var kz = new Keyzone
            {
                minKey = note,
                maxKey = note,
                rootKey = note,
                audioClip = clip
            };

            _sampler.keyzones.Add(kz);

            if (verbose)
                Debug.Log($"[KMusicDrumSequencer] Keyzone added drumId={id} note={note} clip={clip.name}");
        }

        if (verbose)
            Debug.Log($"[KMusicDrumSequencer] Keyzones built: {_sampler.keyzones.Count}");
    }

#if UNITY_EDITOR
    // -------- Editor-only kit scan (does NOT run on Android builds) --------

    private void TryLoadFirstKitFromAssets()
    {
        string root = "Assets/Audio/Kit Samples HMA";
        if (!AssetDatabase.IsValidFolder(root))
        {
            if (verbose)
                Debug.LogWarning($"[KMusicDrumSequencer] Folder not found: {root}");
            return;
        }

        string[] sub = AssetDatabase.GetSubFolders(root);
        if (sub == null || sub.Length == 0)
        {
            if (verbose)
                Debug.LogWarning($"[KMusicDrumSequencer] No kit folders under: {root}");
            return;
        }

        string kitFolder = sub[0];
        LoadKitFolder(kitFolder);

        if (verbose) Debug.Log($"[KMusicDrumSequencer] Loaded kit (editor scan): {kitFolder}");
    }

    private void LoadKitFolder(string kitFolder)
    {
        _clipByDrumId.Clear();

        string[] guids = AssetDatabase.FindAssets("t:AudioClip", new[] { kitFolder });
        foreach (string g in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(g);
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            if (clip == null) continue;

            string lower = System.IO.Path.GetFileNameWithoutExtension(path).ToLowerInvariant();

            // NOTE: Your kit zips are clean: "... Kick", "... Snare", "... HH Closed", etc.
            if (lower.Contains("kick")) _clipByDrumId[1] = clip;
            else if (lower.Contains("snare")) _clipByDrumId[2] = clip;
            else if (lower.Contains("clap")) _clipByDrumId[3] = clip;
            else if (lower.Contains("hh closed") || lower.Contains("hat closed") || lower.Contains("hhc")) _clipByDrumId[4] = clip;
            else if (lower.Contains("hh open") || lower.Contains("hat open") || lower.Contains("hho")) _clipByDrumId[5] = clip;
            else if (lower.Contains("ride") || lower.Contains("perc")) _clipByDrumId[6] = clip;
            else if (lower.Contains("rim")) _clipByDrumId[7] = clip;
            else if (lower.Contains("crash")) _clipByDrumId[8] = clip;
        }
    }
#endif
}