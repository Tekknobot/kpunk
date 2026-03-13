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
    private const string PrefKey_DrumVelocity_B64 = "kmusic.drum.velocity.b64";

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
    private KMusicChainUI _chainUI;

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

    private const int RandomizeLongPressMs = 650;

    private enum RandomBeatStyle
    {
        House = 0,
        BoomBap = 1,
    }

    private readonly Dictionary<Button, IVisualElementScheduledItem> _buttonLongPressJobs = new();
    private readonly HashSet<Button> _buttonLongPressFired = new();
    private RandomBeatStyle _nextWholeBeatStyle = RandomBeatStyle.House;
    private System.Random _rng;

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


    private void ScheduleDrum(int drumId, double dspTime, float velocityScale = 1f)
    {
        if (!_clipByDrumId.TryGetValue(drumId, out var clip) || clip == null)
            return;

        var src = GetOrCreateDrumVoice(drumId, clip);
        if (src == null) return;

        // Scheduled playback (tight timing)
        src.Stop();
        src.volume = Mathf.Clamp01(velocityScale);
        src.PlayScheduled(dspTime);
    }

    private void PreviewDrum(int drumId, float velocityScale = 1f)
    {
        if (!_clipByDrumId.TryGetValue(drumId, out var clip) || clip == null)
            return;

        var src = GetOrCreateDrumVoice(drumId, clip);
        if (src == null) return;

        src.Stop();
        src.volume = 1f;
        src.PlayOneShot(clip, Mathf.Clamp01(velocityScale));
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
    private ushort[] _stepVelocityPacked = new ushort[16];

    // --- Sampler (chop sequencer) ---
    private int[] _sampleChopByStep = new int[16]; // 0 = none, 1..16 = chop id

    [Header("Sampler Playback")]
    [Tooltip("How many overlapping sample voices we allow for chops.")]
    public int sampleVoices = 8;

    private readonly List<AudioSource> _sampleVoicePool = new();
    private int _sampleVoiceCursor = 0;

    private bool _didLoadSamplePattern = false;
    private bool _didLoadAppliedChops = false;

    private bool _velocityGestureArmed = false;
    private bool _velocityGestureMoved = false;
    private bool _velocityGestureStartedOn = false;
    private int _velocityGestureStep = -1;
    private int _velocityGestureLane = -1;
    private Vector2 _velocityGestureStartLocal = Vector2.zero;
    private int _velocityGestureTier = 1;
    private const float DrumVelocityDragThresholdPx = 18f;
    
    // Tracks whether applied chops changed since last load
    private int _appliedRevisionSeen = -1;
    private string _appliedResourcesPath = null;
    private AudioClip _appliedClip = null;
    private float[] _sliceStart01 = new float[16];
    private float[] _sliceEnd01 = new float[16];

    private static string GetDrumShortLabel(int drumId)
    {
        return drumId switch
        {
            1 => "K",
            2 => "S",
            3 => "C",
            4 => "HC",
            5 => "HO",
            6 => "RD",
            7 => "RM",
            8 => "CR",
            _ => ""
        };
    }

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
        _rng = new System.Random(Environment.TickCount);

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

        StartCoroutine(BindWhenReady());

        var root = uiDocument.rootVisualElement;

        root.schedule.Execute(() =>
        {
            var playBtn = root.Q<Button>("PlayButton");
            var stopBtn = root.Q<Button>("StopButton");
            var kitPrev = root.Q<Button>("KitPrev");
            var kitNext = root.Q<Button>("KitNext");

            if (playBtn != null && playBtn.text == "PLAY") { }
            else if (playBtn != null) playBtn.text = "\u25B6";

            if (stopBtn != null && stopBtn.text == "STOP") { }
            else if (stopBtn != null) stopBtn.text = "\u25A0";

            if (kitPrev != null && kitPrev.text == "PREV") { }
            else if (kitPrev != null) kitPrev.text = "\u25C0";

            if (kitNext != null && kitNext.text == "NEXT") { }
            else if (kitNext != null) kitNext.text = "\u25B6";

            Debug.Log($"[DrumSequencer] playBtn={playBtn!=null} stopBtn={stopBtn!=null}");

            if (playBtn != null && !playBtn.ClassListContains("km-play-btn--wired"))
            {
                playBtn.AddToClassList("km-play-btn--wired");
                playBtn.clicked += () =>
                {
                    Debug.Log("PLAY CLICK");
                    OnPlayClicked();
                };
            }

            // STOP long-press is now wired in BindUI() against _stopBtn,
            // not here against a potentially stale queried button.
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

        // STOP button: short tap stops, long press randomizes whole beat.
        if (!_stopBtn.ClassListContains("km-stop-btn--wired"))
        {
            _stopBtn.AddToClassList("km-stop-btn--wired");

            _stopBtn.clicked += () =>
            {
                if (_buttonLongPressFired.Contains(_stopBtn))
                {
                    _buttonLongPressFired.Remove(_stopBtn);
                    return;
                }

                Debug.Log("STOP CLICK");
                OnStopClicked();
            };

            _stopBtn.RegisterCallback<PointerDownEvent>(_ =>
            {
                Debug.Log("[Random Beat] STOP pointer down");
                BeginButtonLongPress(_stopBtn, RandomizeWholeBeatFromLongPress);
            }, TrickleDown.TrickleDown);

            _stopBtn.RegisterCallback<PointerUpEvent>(_ =>
            {
                Debug.Log("[Random Beat] STOP pointer up");
                CancelButtonLongPress(_stopBtn);
            }, TrickleDown.TrickleDown);

            _stopBtn.RegisterCallback<PointerCancelEvent>(_ =>
            {
                Debug.Log("[Random Beat] STOP pointer cancel");
                CancelButtonLongPress(_stopBtn);
            }, TrickleDown.TrickleDown);
        }
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
                    else if (id == "sample.pitch")
                        ApplySamplePitch();

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
        _gridDrum.EnableOccupiedLeftClickErase(false);
        _gridDrum.EnableDeferredOccupiedLeftClick(true);

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

        _gridDrum.OnStrokePressed -= OnGridStrokePressed;
        _gridDrum.OnStrokePressed += OnGridStrokePressed;

        _gridDrum.OnStrokePointerMoved -= OnGridStrokePointerMoved;
        _gridDrum.OnStrokePointerMoved += OnGridStrokePointerMoved;

        _gridDrum.OnStrokeReleased -= OnGridStrokeReleased;
        _gridDrum.OnStrokeReleased += OnGridStrokeReleased;

        // --- Initial draw from existing mask ---
        RefreshGridForActiveDrum();

        // Make sure mute visuals match restored data.
        WireMuteButtons(_root);

        UpdateBpmLabel();

        // Now that load+draw is complete, allow saving.
        _allowSaving = true;

        return true;
    }

    private float GetSamplePitchSemitones()
    {
        if (_bus != null)
        {
            float v = Mathf.Clamp(_bus.GetValue("sample.pitch"), -12f, 12f);

            // snap strictly to whole semitones
            return Mathf.Round(v);
        }

        return 0f;
    }

private float GetSamplePitchRate()
{
    return Mathf.Pow(2f, GetSamplePitchSemitones() / 12f);
}

private void ApplySamplePitch()
{
    float rate = GetSamplePitchRate();

    for (int i = 0; i < _sampleVoicePool.Count; i++)
    {
        var s = _sampleVoicePool[i];
        if (s != null) s.pitch = rate;
    }

    if (verbose)
        Debug.Log($"[SamplePitch] bus={(_bus != null)} semitones={GetSamplePitchSemitones():0.##} rate={rate:0.###} voices={_sampleVoicePool.Count}");
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

            try
            {
                string velB64 = Convert.ToBase64String(PackVelocityData());
                ProjectPrefs.SetString(PrefKey_DrumVelocity_B64, velB64);
            }
            catch { }

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

        bool loadedVelocity = false;
        try
        {
            if (ProjectPrefs.HasKey(PrefKey_DrumVelocity_B64))
            {
                string velB64 = ProjectPrefs.GetString(PrefKey_DrumVelocity_B64, "");
                if (!string.IsNullOrEmpty(velB64))
                {
                    byte[] velBytes = Convert.FromBase64String(velB64);
                    ApplyPackedVelocityDataInternal(velBytes);
                    loadedVelocity = true;
                }
            }
        }
        catch
        {
            loadedVelocity = false;
        }

        if (!loadedVelocity)
            InitializeDefaultVelocitiesFromMask();

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

    private void NotifyChainLiveEdited()
    {
        if (!_allowSaving) return;
        if (_chainUI == null) _chainUI = FindObjectOfType<KMusicChainUI>();
        _chainUI?.NotifyLiveEdited();
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

        if (_allowSaving)
        {
            SaveDrumState();
            NotifyChainLiveEdited();
        }
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
        foreach (var kv in _buttonLongPressJobs)
        {
            try { kv.Value?.Pause(); } catch { }
        }
        _buttonLongPressJobs.Clear();
        _buttonLongPressFired.Clear();

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

        b.clicked += () =>
        {
            if (_buttonLongPressFired.Contains(b))
            {
                _buttonLongPressFired.Remove(b);
                return;
            }

            SelectDrum(drumId);
        };

        b.RegisterCallback<PointerDownEvent>(_ =>
        {
            Debug.Log($"[Random Beat] Drum {drumId} pointer down");
            BeginButtonLongPress(b, () => RandomizeSingleDrumLaneFromLongPress(drumId));
        }, TrickleDown.TrickleDown);

        b.RegisterCallback<PointerUpEvent>(_ =>
        {
            Debug.Log($"[Random Beat] Drum {drumId} pointer up");
            CancelButtonLongPress(b);
        }, TrickleDown.TrickleDown);

        b.RegisterCallback<PointerCancelEvent>(_ =>
        {
            Debug.Log($"[Random Beat] Drum {drumId} pointer cancel");
            CancelButtonLongPress(b);
        }, TrickleDown.TrickleDown);
    }

    private void BeginButtonLongPress(Button button, Action longPressAction)
    {
        if (button == null || longPressAction == null) return;

        CancelButtonLongPress(button);

        var job = button.schedule.Execute(() =>
        {
            CancelButtonLongPress(button);
            _buttonLongPressFired.Add(button);
            longPressAction();
        });

        job.ExecuteLater(RandomizeLongPressMs);
        _buttonLongPressJobs[button] = job;
    }

    private void CancelButtonLongPress(Button button)
    {
        if (button == null) return;

        if (_buttonLongPressJobs.TryGetValue(button, out var job) && job != null)
        {
            try { job.Pause(); } catch { }
        }

        _buttonLongPressJobs.Remove(button);
    }

    private void RandomizeWholeBeatFromLongPress()
    {
        var style = _nextWholeBeatStyle;
        GenerateRandomBeat(style);
        _nextWholeBeatStyle = style == RandomBeatStyle.House ? RandomBeatStyle.BoomBap : RandomBeatStyle.House;

        Debug.Log($"[Random Beat] Generated {style} groove. Next long-press STOP style: {_nextWholeBeatStyle}.");
    }

    private void RandomizeSingleDrumLaneFromLongPress(int drumId)
    {
        Debug.Log($"[Random Beat] Lane long press fired drumId={drumId}");

        RandomizeSingleDrumLane(drumId, _nextWholeBeatStyle);
        SelectDrum(drumId);

        Debug.Log($"[Random Beat] Randomized drum {drumId} using {_nextWholeBeatStyle} lane rules.");
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
        TriggerDrumNow(_activeDrumId, 1);

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
        if (_suppressGridCallbacks || _suppressGridEvents) return;

        _allowSaving = true;
        EnsureStateLoaded();

        if (r < 0 || r >= 2 || c < 0 || c >= 8) return;

        int step = (r * 8) + c;
        if (step < 0 || step >= steps) return;

        int lane = _activeDrumId - 1;
        byte bit = (byte)(1 << lane);

        if (v > 0)
        {
            _stepMask[step] = (byte)(_stepMask[step] | bit);
            SetStepVelocityTier(step, lane, 1); // default medium
        }
        else
        {
            _stepMask[step] = (byte)(_stepMask[step] & ~bit);
            SetStepVelocityTier(step, lane, 1);
        }

        int shown = ((_stepMask[step] & bit) != 0)
            ? GetVisibleVelocityCellValue(_activeDrumId, GetStepVelocityTier(step, lane))
            : 0;

        _suppressGridCallbacks = true;
        try
        {
            _grid.SetValue(r, c, 0);
            _grid.SetValue(r, c, shown);
        }
        finally
        {
            _suppressGridCallbacks = false;
        }

        if (v > 0)
            TriggerDrumNow(_activeDrumId, GetStepVelocityTier(step, lane));

        SaveDrumState();
        NotifyChainLiveEdited();

        if (verbose)
            Debug.Log($"[DRUM GRID] r={r} c={c} step={step} active={_activeDrumId} v={v} shown={shown} tier={GetStepVelocityTier(step, lane)} mask=0x{_stepMask[step]:X2}");
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

        TriggerDrumNow(_activeDrumId, on ? GetStepVelocityTier(step, lane) : 1);
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
                int tier = GetStepVelocityTier(step, lane);
                int cellValue = on ? GetVisibleVelocityCellValue(_activeDrumId, tier) : 0;

                _gridDrum.SetValue(r, c, cellValue, false);
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

        if (_allowSaving)
        {
            SaveDrumState();
            NotifyChainLiveEdited();
        }
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
        ApplySamplePitch();
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

        float pitchRate = Mathf.Max(0.01f, GetSamplePitchRate());

        // Compute slice timing
        double sliceDur = (e01 - s01) * _appliedClip.length;

        // ✅ play full chop length (to next marker)
        double dur = Math.Max(0.01, Math.Min(sliceDur / pitchRate, 4.0)); // cap at 4s (tweak)

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
        src.pitch = pitchRate;
        src.timeSamples = startSample;

        // schedule
        double start = dspTime;
        double end = start + dur;

        src.PlayScheduled(start);
        src.SetScheduledEndTime(end);

        if (verbose)
            Debug.Log($"[Sampler] SCHED chop={chopId} t={start:0.000} dur={dur:0.000} slice={s01:0.000}->{e01:0.000} sliceDur={sliceDur:0.000}");
    }

    private void GenerateRandomBeat(RandomBeatStyle style)
    {
        EnsureStateLoaded();

        if (_stepMask == null || _stepMask.Length != steps)
            _stepMask = new byte[steps];

        Array.Clear(_stepMask, 0, _stepMask.Length);

        if (_stepVelocityPacked == null || _stepVelocityPacked.Length != steps)
            _stepVelocityPacked = new ushort[steps];

        Array.Clear(_stepVelocityPacked, 0, _stepVelocityPacked.Length);

        for (int drumId = 1; drumId <= LANES; drumId++)
            RandomizeDrumLaneInternal(drumId, style, clearExistingLane: false);

        GenerateRandomSamplePattern(style);

        // Force a visible lane so the user always sees something after randomize.
        ApplyActiveDrumId(1);

        RefreshGridForActiveDrum();
        RefreshSampleGrid();

        SaveDrumState();
        SaveSamplePatternState();
        NotifyChainLiveEdited();
    }

    private void RandomizeSingleDrumLane(int drumId, RandomBeatStyle style)
    {
        EnsureStateLoaded();
        RandomizeDrumLaneInternal(drumId, style, clearExistingLane: true);
        RefreshGridForActiveDrum();
        SaveDrumState();
        NotifyChainLiveEdited();
    }

    private void RandomizeDrumLaneInternal(int drumId, RandomBeatStyle style, bool clearExistingLane)
    {
        drumId = Mathf.Clamp(drumId, 1, LANES);
        int lane = drumId - 1;
        byte bit = (byte)(1 << lane);

        if (clearExistingLane)
        {
            for (int step = 0; step < steps; step++)
            {
                _stepMask[step] = (byte)(_stepMask[step] & ~bit);
                SetStepVelocityTier(step, lane, 1);
            }
        }

        for (int step = 0; step < steps; step++)
        {
            float chance = GetRandomLaneChance(style, drumId, step);
            if (NextFloat01() > chance)
                continue;

            int tier = GetRandomVelocityTier(style, drumId, step);
            SetStepStateAndVisual(step, lane, true, tier);
        }

        ApplyLaneAnchors(style, drumId);
    }

    private void ApplyLaneAnchors(RandomBeatStyle style, int drumId)
    {
        if (style == RandomBeatStyle.House)
        {
            if (drumId == 1)
            {
                ForceLaneStep(drumId, 0, 2);
                ForceLaneStep(drumId, 4, 1);
                ForceLaneStep(drumId, 8, 2);
                ForceLaneStep(drumId, 12, 1);
            }
            else if (drumId == 2)
            {
                ForceLaneStep(drumId, 4, 2);
                ForceLaneStep(drumId, 12, 2);
            }
            else if (drumId == 4)
            {
                ForceLaneStep(drumId, 2, 1);
                ForceLaneStep(drumId, 6, 1);
                ForceLaneStep(drumId, 10, 1);
                ForceLaneStep(drumId, 14, 1);
            }
        }
        else
        {
            if (drumId == 1)
            {
                ForceLaneStep(drumId, 0, 2);
                ForceLaneStep(drumId, 7, 1);
                ForceLaneStep(drumId, 10, 1);
            }
            else if (drumId == 2)
            {
                ForceLaneStep(drumId, 4, 2);
                ForceLaneStep(drumId, 12, 2);
            }
            else if (drumId == 4)
            {
                ForceLaneStep(drumId, 2, 1);
                ForceLaneStep(drumId, 6, 0);
                ForceLaneStep(drumId, 10, 1);
                ForceLaneStep(drumId, 14, 0);
            }
        }
    }

    private void ForceLaneStep(int drumId, int step, int tier)
    {
        if (step < 0 || step >= steps) return;
        int lane = Mathf.Clamp(drumId - 1, 0, LANES - 1);
        SetStepStateAndVisual(step, lane, true, tier);
    }

    private void GenerateRandomSamplePattern(RandomBeatStyle style)
    {
        if (_sampleChopByStep == null || _sampleChopByStep.Length != steps)
            _sampleChopByStep = new int[steps];

        Array.Clear(_sampleChopByStep, 0, _sampleChopByStep.Length);
        _didLoadSamplePattern = true;

        EnsureAppliedChopsLoaded();
        if (_appliedClip == null)
            return;

        var available = GetAvailableChopIds();
        if (available.Count == 0)
            return;

        if (style == RandomBeatStyle.House)
        {
            int[] anchors = { 0, 4, 8, 12 };
            for (int i = 0; i < anchors.Length; i++)
            {
                if (NextFloat01() <= 0.72f)
                    _sampleChopByStep[anchors[i]] = PickChopId(available, i);
            }

            int[] extras = { 2, 6, 10, 14, 15 };
            for (int i = 0; i < extras.Length; i++)
            {
                if (_sampleChopByStep[extras[i]] != 0) continue;
                if (NextFloat01() <= 0.22f)
                    _sampleChopByStep[extras[i]] = PickChopId(available, i + 4);
            }
        }
        else
        {
            int[] phrase = { 0, 3, 6, 8, 11, 14 };
            int motifA = PickChopId(available, 0);
            int motifB = PickChopId(available, 1);

            for (int i = 0; i < phrase.Length; i++)
            {
                if (NextFloat01() > 0.68f) continue;
                _sampleChopByStep[phrase[i]] = (i % 2 == 0) ? motifA : motifB;
            }

            int[] fillSteps = { 5, 7, 13, 15 };
            for (int i = 0; i < fillSteps.Length; i++)
            {
                if (_sampleChopByStep[fillSteps[i]] != 0) continue;
                if (NextFloat01() <= 0.25f)
                    _sampleChopByStep[fillSteps[i]] = PickChopId(available, i + 2);
            }
        }
    }

    private List<int> GetAvailableChopIds()
    {
        var ids = new List<int>(16);
        for (int i = 0; i < 16; i++)
        {
            if (_sliceEnd01 != null && _sliceStart01 != null && i < _sliceEnd01.Length && i < _sliceStart01.Length)
            {
                if (_sliceEnd01[i] > _sliceStart01[i] + 0.0001f)
                    ids.Add(i + 1);
            }
        }
        return ids;
    }

    private int PickChopId(List<int> available, int seedOffset)
    {
        if (available == null || available.Count == 0)
            return 0;

        if (_rng == null)
            _rng = new System.Random(Environment.TickCount);

        seedOffset = Mathf.Abs(seedOffset);
        int index = seedOffset % available.Count;
        if (NextFloat01() > 0.35f)
            index = _rng.Next(available.Count);

        return available[index];
    }

    private float GetRandomLaneChance(RandomBeatStyle style, int drumId, int step)
    {
        bool quarter = (step % 4) == 0;
        bool offbeat8 = (step % 4) == 2;
        bool even16 = (step % 2) == 0;

        if (style == RandomBeatStyle.House)
        {
            return drumId switch
            {
                1 => quarter ? 0.92f : (offbeat8 ? 0.10f : 0.04f),
                2 => (step == 4 || step == 12) ? 0.95f : (step == 15 ? 0.10f : 0.03f),
                3 => (step == 4 || step == 12) ? 0.28f : (offbeat8 ? 0.08f : 0.02f),
                4 => offbeat8 ? 0.88f : (even16 ? 0.18f : 0.10f),
                5 => (step == 7 || step == 15) ? 0.34f : 0.02f,
                6 => (step == 0 || step == 8) ? 0.22f : (offbeat8 ? 0.08f : 0.02f),
                7 => (step == 3 || step == 11) ? 0.18f : 0.02f,
                8 => (step == 0) ? 0.18f : (step == 15 ? 0.10f : 0.01f),
                _ => 0f,
            };
        }

        return drumId switch
        {
            1 => (step == 0) ? 0.95f : ((step == 7 || step == 10 || step == 15) ? 0.48f : ((step == 3 || step == 8) ? 0.20f : 0.03f)),
            2 => (step == 4 || step == 12) ? 0.95f : 0.02f,
            3 => (step == 12) ? 0.30f : ((step == 4) ? 0.18f : 0.02f),
            4 => offbeat8 ? 0.72f : (even16 ? 0.15f : 0.05f),
            5 => (step == 6 || step == 14) ? 0.18f : 0.01f,
            6 => (step == 2 || step == 10) ? 0.10f : 0.01f,
            7 => (step == 3 || step == 15) ? 0.20f : 0.02f,
            8 => (step == 0) ? 0.08f : (step == 15 ? 0.12f : 0.01f),
            _ => 0f,
        };
    }

    private int GetRandomVelocityTier(RandomBeatStyle style, int drumId, int step)
    {
        float roll = NextFloat01();

        if (style == RandomBeatStyle.House)
        {
            if (drumId == 1 && step % 4 == 0) return roll < 0.60f ? 2 : 1;
            if (drumId == 2 && (step == 4 || step == 12)) return 2;
            if (drumId == 4) return roll < 0.20f ? 0 : 1;
            if (drumId == 5 || drumId == 7) return roll < 0.60f ? 0 : 1;
            if (drumId == 8) return 2;
            return roll < 0.18f ? 0 : (roll > 0.82f ? 2 : 1);
        }

        if (drumId == 1 && step == 0) return 2;
        if (drumId == 2 && (step == 4 || step == 12)) return 2;
        if (drumId == 4) return roll < 0.34f ? 0 : 1;
        if (drumId == 7) return roll < 0.50f ? 0 : 1;
        return roll < 0.20f ? 0 : (roll > 0.86f ? 2 : 1);
    }

    private float NextFloat01()
    {
        if (_rng == null)
            _rng = new System.Random(Environment.TickCount);

        return (float)_rng.NextDouble();
    }

    private void SaveSamplePatternState()
    {
        try
        {
            var flat = new int[steps];
            Array.Copy(_sampleChopByStep, flat, Mathf.Min(_sampleChopByStep.Length, flat.Length));

            KMusicSaveState.SaveIntArray(ProjectPrefs.Key(PrefKey_SampleStepGrid), flat);
            KMusicSaveState.SaveIntArray(PrefKey_SampleStepGrid, flat);
        }
        catch { }
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

    public void StopPlayback()
    {
        OnStopClicked();
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

        // ✅ Live BPM changes should keep already-playing audio/tails alive and
        // only retime the transport phase + future step scheduling.
        if (Math.Abs(stepDurLive - _stepDur) > 0.000001)
        {
            double oldStepDur = _stepDur;
            if (oldStepDur <= 0.000001)
                oldStepDur = stepDurLive;

            // Preserve the current musical phase so the playhead does not jump
            // and future steps continue from the current position instead of
            // restarting a full step from "now".
            double stepPos = 0.0;
            if (now >= _playStartDspTime)
                stepPos = (now - _playStartDspTime) / oldStepDur;

            // Which absolute step number is the next unscheduled one?
            long nextStepNumber = 0;
            if (_nextStepDspTime > _playStartDspTime)
                nextStepNumber = Math.Max(0L, (long)Math.Round((_nextStepDspTime - _playStartDspTime) / oldStepDur));
            else
                nextStepNumber = Math.Max(0L, (long)Math.Ceiling(stepPos));

            _stepDur = stepDurLive;
            _playStartDspTime = now - (stepPos * _stepDur);
            _nextStepDspTime = _playStartDspTime + (nextStepNumber * _stepDur);

            // Safety: keep the next unscheduled step in the future.
            if (_nextStepDspTime < now)
                _nextStepDspTime = now + Math.Min(_stepDur, lookaheadSeconds);
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
                                ScheduleDrum(drumId, _nextStepDspTime, GetDrumGain(drumId) * GetVelocityGain(step, lane));
                        }
                        else
                        {
                            float vel = GetDrumGain(drumId) * GetVelocityGain(step, lane);
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

    public byte[] CaptureDrumVelocityData()
    {
        return PackVelocityData();
    }

    public void ApplyDrumVelocityData(byte[] data)
    {
        ApplyPackedVelocityDataInternal(data);
        try { RefreshGridForActiveDrum(); } catch { }
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

        InitializeDefaultVelocitiesFromMask();

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

        // 1) clear internal data
        Array.Clear(_sampleChopByStep, 0, _sampleChopByStep.Length);

        // 2) clear UI hard
        if (_gridSample != null)
        {
            _suppressGridEvents = true;
            _suppressGridCallbacks = true;
            try
            {
                _gridSample.ClearAll();
                _gridSample.SetPlayheadStep(-1);
            }
            finally
            {
                _suppressGridCallbacks = false;
                _suppressGridEvents = false;
            }
        }

        // 3) copy loaded data
        if (flat != null)
            Array.Copy(flat, _sampleChopByStep, Mathf.Min(flat.Length, _sampleChopByStep.Length));

        _didLoadSamplePattern = true;

        // 4) redraw UI from loaded data
        try
        {
            RefreshSampleGrid();
        }
        catch { }
    }

    private void RefreshSampleGrid()
    {
        if (_gridSample == null || _sampleChopByStep == null) return;

        _suppressGridCallbacks = true;
        try
        {
            for (int step = 0; step < steps; step++)
            {
                int r = step / 8;
                int c = step % 8;

                int chopId = _sampleChopByStep[step];
                _gridSample.SetValue(r, c, chopId);
            }
        }
        finally
        {
            _suppressGridCallbacks = false;
        }
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

        float pitchRate = Mathf.Max(0.01f, GetSamplePitchRate());
        double sliceDur = (e01 - s01) * _appliedClip.length;
        float dur = (float)Math.Max(0.02, sliceDur / pitchRate);

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
        src.pitch = pitchRate;
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

    private void TriggerDrumNow(int drumId, int velocityTier = 1)
    {
        if (_sampler == null) return;
        if (!_noteByDrumId.TryGetValue(drumId, out var note)) return;
        if (!_clipByDrumId.TryGetValue(drumId, out var clip) || clip == null) return;

        double start = AudioSettings.dspTime;
        double end = start + Math.Max(0.05, clip.length);

        if (verbose)
            Debug.Log($"[KMusicDrumSequencer] TRIG drumId={drumId} note={note} clip={clip.name}");

        float vel = GetDrumGain(drumId) * VelocityTierToGain(velocityTier);
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
        if (v <= 0) return "";
        int drumId = DecodeDrumIdFromCellValue(v);
        return GetDrumShortLabel(drumId);
    }

    private Color DrumTint(int v)
    {
        if (v <= 0) return new Color(0f, 0f, 0f, 0f);

        int drumId = DecodeDrumIdFromCellValue(v);
        int tier = DecodeVelocityTierFromCellValue(v);

        Color baseColor = drumId switch
        {
            1 => new Color(0.90f, 0.22f, 0.27f, 1f),
            2 => new Color(0.96f, 0.50f, 0.00f, 1f),
            3 => new Color(1.00f, 0.30f, 0.60f, 1f),
            4 => new Color(0.00f, 0.90f, 1.00f, 1f),
            5 => new Color(0.30f, 0.76f, 0.97f, 1f),
            6 => new Color(0.60f, 0.36f, 0.90f, 1f),
            7 => new Color(0.50f, 0.93f, 0.60f, 1f),
            8 => new Color(1.00f, 0.84f, 0.04f, 1f),
            _ => Color.white
        };

        float brightness = tier switch
        {
            2 => 1.20f, // high
            1 => 1.00f, // medium
            _ => 0.60f  // low
        };

        return new Color(
            Mathf.Clamp01(baseColor.r * brightness),
            Mathf.Clamp01(baseColor.g * brightness),
            Mathf.Clamp01(baseColor.b * brightness),
            1f
        );
    }
    private static string GetVelocityGlyph(int tier)
    {
        return tier switch
        {
            2 => "■",
            1 => "▣",
            _ => "□"
        };
    }

    private static float VelocityTierToGain(int tier)
    {
        return tier switch
        {
            2 => 1.00f, // high
            1 => 0.58f, // medium
            _ => 0.25f  // low
        };
    }

    private static int GetVisibleVelocityCellValue(int drumId, int tier)
    {
        drumId = Mathf.Clamp(drumId, 1, 8);
        tier = Mathf.Clamp(tier, 0, 2);
        return tier switch
        {
            2 => drumId + 8,
            1 => drumId,
            _ => drumId + 16
        };
    }

    private static int DecodeDrumIdFromCellValue(int v)
    {
        if (v <= 0) return 0;
        if (v <= 8) return v;
        if (v <= 16) return v - 8;
        if (v <= 24) return v - 16;
        return Mathf.Clamp(((v - 1) % 8) + 1, 1, 8);
    }

    private static int DecodeVelocityTierFromCellValue(int v)
    {
        if (v <= 0) return 1;
        if (v <= 8) return 1;
        if (v <= 16) return 2;
        if (v <= 24) return 0;
        return 1;
    }

    private int GetStepVelocityTier(int step, int lane)
    {
        if (_stepVelocityPacked == null || step < 0 || step >= _stepVelocityPacked.Length) return 1;
        lane = Mathf.Clamp(lane, 0, 7);
        int shift = lane * 2;
        return (_stepVelocityPacked[step] >> shift) & 0x3;
    }

    private void SetStepVelocityTier(int step, int lane, int tier)
    {
        if (_stepVelocityPacked == null || _stepVelocityPacked.Length != steps)
            _stepVelocityPacked = new ushort[steps];

        if (step < 0 || step >= _stepVelocityPacked.Length) return;
        lane = Mathf.Clamp(lane, 0, 7);
        tier = Mathf.Clamp(tier, 0, 2);

        int shift = lane * 2;
        ushort mask = (ushort)(0x3 << shift);
        ushort packed = _stepVelocityPacked[step];
        packed = (ushort)(packed & ~mask);
        packed = (ushort)(packed | (ushort)(tier << shift));
        _stepVelocityPacked[step] = packed;
    }

    private float GetVelocityGain(int step, int lane)
    {
        return VelocityTierToGain(GetStepVelocityTier(step, lane));
    }

    private void InitializeDefaultVelocitiesFromMask()
    {
        if (_stepVelocityPacked == null || _stepVelocityPacked.Length != steps)
            _stepVelocityPacked = new ushort[steps];

        Array.Clear(_stepVelocityPacked, 0, _stepVelocityPacked.Length);

        for (int step = 0; step < steps; step++)
        {
            byte mask = (step < _stepMask.Length) ? _stepMask[step] : (byte)0;
            for (int lane = 0; lane < LANES; lane++)
            {
                if ((mask & (1 << lane)) != 0)
                    SetStepVelocityTier(step, lane, 1);
            }
        }
    }

    private byte[] PackVelocityData()
    {
        if (_stepVelocityPacked == null || _stepVelocityPacked.Length != steps)
            InitializeDefaultVelocitiesFromMask();

        byte[] data = new byte[steps * 2];
        for (int i = 0; i < steps; i++)
        {
            ushort packed = _stepVelocityPacked[i];
            data[i * 2] = (byte)(packed & 0xFF);
            data[i * 2 + 1] = (byte)((packed >> 8) & 0xFF);
        }
        return data;
    }

    private void ApplyPackedVelocityDataInternal(byte[] data)
    {
        if (_stepVelocityPacked == null || _stepVelocityPacked.Length != steps)
            _stepVelocityPacked = new ushort[steps];

        Array.Clear(_stepVelocityPacked, 0, _stepVelocityPacked.Length);

        if (data == null || data.Length == 0)
        {
            InitializeDefaultVelocitiesFromMask();
            return;
        }

        int count = Mathf.Min(steps, data.Length / 2);
        for (int i = 0; i < count; i++)
            _stepVelocityPacked[i] = (ushort)(data[i * 2] | (data[i * 2 + 1] << 8));
    }

    private void SetStepStateAndVisual(int step, int lane, bool on, int tier)
    {
        if (step < 0 || step >= steps) return;
        lane = Mathf.Clamp(lane, 0, 7);
        tier = Mathf.Clamp(tier, 0, 2);

        byte bit = (byte)(1 << lane);

        if (on) _stepMask[step] = (byte)(_stepMask[step] | bit);
        else _stepMask[step] = (byte)(_stepMask[step] & ~bit);

        SetStepVelocityTier(step, lane, tier);

        if (_gridDrum != null && lane == Mathf.Clamp(_activeDrumId - 1, 0, 7))
        {
            int r = step / 8;
            int c = step % 8;
            int cellValue = on ? GetVisibleVelocityCellValue(_activeDrumId, tier) : 0;

            _suppressGridCallbacks = true;
            try
            {
                _gridDrum.SetValue(r, c, cellValue, false);
            }
            finally
            {
                _suppressGridCallbacks = false;
            }
        }
    }
    private void OnGridStrokePressed(int r, int c, Vector2 localPos, bool isErase)
    {
        if (_suppressGridCallbacks || _suppressGridEvents || isErase)
        {
            _velocityGestureArmed = false;
            return;
        }

        EnsureStateLoaded();

        int step = (r * 8) + c;
        if (step < 0 || step >= steps)
        {
            _velocityGestureArmed = false;
            return;
        }

        int lane = Mathf.Clamp(_activeDrumId - 1, 0, 7);
        bool on = (_stepMask[step] & (1 << lane)) != 0;

        _velocityGestureArmed = true;
        _velocityGestureMoved = false;
        _velocityGestureStartedOn = on;
        _velocityGestureStep = step;
        _velocityGestureLane = lane;

        // IMPORTANT:
        // Use the exact same coordinate space that OnStrokePointerMoved gives us.
        _velocityGestureStartLocal = localPos;

        _velocityGestureTier = on ? GetStepVelocityTier(step, lane) : 1;
    }
    private void OnGridStrokePointerMoved(Vector2 localPos)
    {
        if (!_velocityGestureArmed) return;
        if (_velocityGestureStep < 0 || _velocityGestureLane < 0) return;

        // Same coordinate space as press event
        float dy = _velocityGestureStartLocal.y - localPos.y;

        int newTier = 1; // medium dead zone

        if (dy >= DrumVelocityDragThresholdPx)
            newTier = 2; // high
        else if (dy <= -DrumVelocityDragThresholdPx)
            newTier = 0; // low

        // Do not convert a normal tap into a drag unless user actually leaves the dead zone
        if (!_velocityGestureMoved && Mathf.Abs(dy) < DrumVelocityDragThresholdPx)
            return;

        // Once dragging has started, allow returning to medium
        if (_velocityGestureMoved && newTier == _velocityGestureTier)
            return;

        _velocityGestureMoved = true;
        _velocityGestureTier = newTier;

        SetStepStateAndVisual(_velocityGestureStep, _velocityGestureLane, true, _velocityGestureTier);
        TriggerDrumNow(_velocityGestureLane + 1, _velocityGestureTier);
    }
    private void OnGridStrokeReleased(Vector2 localPos, List<Vector2Int> cells, bool isErase)
    {
        if (!_velocityGestureArmed) return;

        int step = _velocityGestureStep;
        int lane = _velocityGestureLane;
        bool startedOn = _velocityGestureStartedOn;
        bool moved = _velocityGestureMoved;

        _velocityGestureArmed = false;
        _velocityGestureMoved = false;
        _velocityGestureStartedOn = false;
        _velocityGestureStep = -1;
        _velocityGestureLane = -1;

        if (isErase || step < 0 || lane < 0)
            return;

        // If this was a drag, keep the final tier and save it
        if (moved)
        {
            SaveDrumState();
            NotifyChainLiveEdited();
            return;
        }

        // Normal tap on existing note = erase
        if (startedOn)
        {
            SetStepStateAndVisual(step, lane, false, 1);
            SaveDrumState();
            NotifyChainLiveEdited();
        }
        // tap empty step is still handled by OnGridValueChanged
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