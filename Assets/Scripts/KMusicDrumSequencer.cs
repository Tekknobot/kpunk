using System;
using System.Collections.Generic;
using UnityEngine;
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
    private readonly byte[] _stepMask = new byte[16];   // step -> bitmask (bit0=kick ... bit7=crash)

    private UIDocument _doc;
    private VisualElement _root;
    private Button _playBtn;
    private Button _stopBtn;

    private StepGrid _grid;
    private StepGrid _gridSeq;
    private StepGrid _gridSample;
    private StepGrid _gridDrum;
    private Label _bpmLabel;
    private bool[] _drumMute = new bool[9]; // index 1..8

    private int _activeDrumId = 1; // current "view/brush" drum id 1..8

    // engine
    private AudioHelmClock _clock;
    private Sampler _sampler;

    // kit clips per drum id
    private readonly Dictionary<int, AudioClip> _clipByDrumId = new();

    // scheduling state
    private bool _playing = false;
    private int _stepIndex = 0; // 0..15
    private double _nextStepDspTime = 0.0;
    private double _playStartDspTime = 0.0;
    private int _lastVisualStep = -999;
    private float _lastBpmShown = -1f;

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

    private KMusicApp _app;

    private void Awake()
    {
        _doc = GetComponent<UIDocument>();
        _app = GetComponent<KMusicApp>();

        EnsureAudioHelmClock();
        EnsureSamplerEngine();
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
        {
            Debug.LogWarning("[KIT UI] UIDocument not assigned.");
            return;
        }

        var root = uiDocument.rootVisualElement;

        root.schedule.Execute(() =>
        {
            HookKitUI(root);
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
    }

    private bool BindUI()
    {
        if (_doc == null) return false;
        _root = _doc.rootVisualElement;
        if (_root == null) return false;

        _playBtn = _root.Q<Button>(playButtonName);
        _stopBtn = _root.Q<Button>(stopButtonName);

        _grid = _root.Q<StepGrid>("DrumStepGrid") ?? _root.Q<StepGrid>(drumGridName);
        _gridSeq = _root.Q<StepGrid>("StepGrid");
        _gridSample = _root.Q<StepGrid>("SampleStepGrid") ?? _root.Q<StepGrid>("SamplerStepGrid");
        _gridDrum = _root.Q<StepGrid>("DrumStepGrid");

        _bpmLabel = _root.Q<Label>("BpmLabel");

        if (verbose)
            Debug.Log($"[KMusicDrumSequencer] bind play={(_playBtn != null)} stop={(_stopBtn != null)} drumGrid found={(_grid != null)}");

        if (_playBtn == null || _stopBtn == null || _grid == null)
            return false;

        _playBtn.clicked -= OnPlayClicked;
        _stopBtn.clicked -= OnStopClicked;
        _playBtn.clicked += OnPlayClicked;
        _stopBtn.clicked += OnStopClicked;

        _kitNameLabel = _root.Q<UnityEngine.UIElements.Label>("KitName");
        _kitPrevBtn = _root.Q<UnityEngine.UIElements.Button>("KitPrev");
        _kitNextBtn = _root.Q<UnityEngine.UIElements.Button>("KitNext");

        if (_kitPrevBtn != null) _kitPrevBtn.clicked += PrevKit;
        if (_kitNextBtn != null) _kitNextBtn.clicked += NextKit;

        WireDrumButtons();

        // Grid renders values as drum IDs (0 or 1..8) — but ONLY for the current active drum layer.
        _grid.EnableValueLabels(true, DrumLabel);
        _grid.EnableValueTint(true, DrumTint);
        _grid.ClearAll();

        // Make StepGrid paint use the active drum ID (so its own label/tint works)
        _grid.SetPaintValue(_activeDrumId);
        _grid.SetPlayhead(-1);

        _grid.OnCellValueChanged -= OnGridValueChanged;
        _grid.OnCellValueChanged += OnGridValueChanged;

        _grid.OnCellClicked -= OnGridCellClicked;
        _grid.OnCellClicked += OnGridCellClicked;

        // Sync UI to current layer
        RefreshGridForActiveDrum();

        UpdateBpmLabel();
        return true;
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
            _kitNameLabel.text = string.IsNullOrEmpty(kit.kitName) ? $"KIT {index+1}" : kit.kitName;

        if (verbose)
            Debug.Log($"[KMusicDrumSequencer] Applied kit {index+1}/{_kits.Length}: {kit.kitName}");
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
        _activeDrumId = Mathf.Clamp(drumId, 1, 8);

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
    }

    private void OnGridValueChanged(int r, int c, int v)
    {
        if (_suppressGridCallbacks) return;
        if (r < 0 || r >= 2 || c < 0 || c >= 8) return;

        int step = (r * 8) + c;
        if (step < 0 || step >= steps) return;

        int lane = _activeDrumId - 1;
        byte bit = (byte)(1 << lane);

        // StepGrid sends v==0 when clearing, v>0 when painting.
        // We treat it as ON/OFF for the current lane.
        if (v > 0) _stepMask[step] = (byte)(_stepMask[step] | bit);
        else _stepMask[step] = (byte)(_stepMask[step] & ~bit);

        // Keep the UI cell consistent with the current view:
        // show active drum ID if bit is set, else 0.
        _suppressGridCallbacks = true;
        try
        {
            int shown = ((_stepMask[step] & bit) != 0) ? _activeDrumId : 0;
            _grid.SetValue(r, c, shown);
        }
        finally
        {
            _suppressGridCallbacks = false;
        }

        // Audition on edit (only when stopped)
        if (!_playing && v > 0)
            TriggerDrumNow(_activeDrumId);
    }

    private void OnGridCellClicked(int r, int c)
    {
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
        if (_grid == null) return;

        int lane = _activeDrumId - 1;
        byte bit = (byte)(1 << lane);

        _suppressGridCallbacks = true;
        try
        {
            for (int step = 0; step < steps; step++)
            {
                int r = step / 8;
                int c = step % 8;
                int shown = ((_stepMask[step] & bit) != 0) ? _activeDrumId : 0;
                _grid.SetValue(r, c, shown);
            }
        }
        finally
        {
            _suppressGridCallbacks = false;
        }
    }

    private void OnPlayClicked()
    {
        if (_playing) return;

        if (_sampler == null || _sampler.keyzones == null || _sampler.keyzones.Count == 0)
            Debug.LogWarning("[KMusicDrumSequencer] Sampler has no keyzones; drum kit may not be loaded.");
        if (_clipByDrumId.Count == 0)
            Debug.LogWarning("[KMusicDrumSequencer] No drum clips loaded; for runtime builds, place clips under Assets/Resources/KMusic/Drums/.");

        _playing = true;

        _stepIndex = 0;
        _nextStepDspTime = AudioSettings.dspTime + startDelaySeconds;
        _playStartDspTime = _nextStepDspTime;

        if (_clock != null)
        {
            _clock.bpm = GetBpm();
            _clock.pause = false;
            _clock.StartScheduled(_nextStepDspTime);
        }

        if (verbose) Debug.Log("[KMusicDrumSequencer] PLAY");
    }

    private void OnStopClicked()
    {
        if (!_playing) return;

        _playing = false;

        if (_clock != null)
            _clock.pause = true;

        if (_sampler != null)
            _sampler.AllNotesOff();

        if (_grid != null)
            _grid.SetPlayhead(-1);

        if (verbose) Debug.Log("[KMusicDrumSequencer] STOP");
    }

    private void Update()
    {
        if (!_playing || _sampler == null) return;

        float bpm = GetBpm();
        if (_clock != null) _clock.bpm = bpm;

        double stepDur = 60.0 / Math.Max(1.0, bpm) / 4.0; // 16th
        double now = AudioSettings.dspTime;
        double windowEnd = now + lookaheadSeconds;

        UpdateVisualPlayhead(now, stepDur);
        UpdateBpmLabel();

        while (_nextStepDspTime < windowEnd)
        {
            int step = _stepIndex;
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

                        float vel = GetDrumGain(drumId);
                        if (vel > 0f)
                            _sampler.NoteOnScheduled(note, vel, _nextStepDspTime, end);
                    }
                    else if (verbose)
                    {
                        Debug.LogWarning($"[KMusicDrumSequencer] Missing clip for drumId={drumId} (lane={lane}).");
                    }
                }
            }

            _stepIndex = (_stepIndex + 1) % steps;
            _nextStepDspTime += stepDur;
        }
    }

    private void UpdateVisualPlayhead(double nowDsp, double stepDur)
    {
        if (_grid == null) return;

        if (nowDsp < _playStartDspTime)
        {
            if (_lastVisualStep != -1)
            {
                _lastVisualStep = -1;
                _grid.SetPlayhead(-1);
            }
            return;
        }

        int step = (int)Math.Floor((nowDsp - _playStartDspTime) / Math.Max(1e-6, stepDur));
        step = ((step % steps) + steps) % steps;

        if (step != _lastVisualStep)
        {
            _lastVisualStep = step;

            if (_gridSeq != null) _gridSeq.SetPlayhead(step);
            if (_gridSample != null) _gridSample.SetPlayhead(step);
            if (_gridDrum != null) _gridDrum.SetPlayhead(step);
        }
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

        _sampler.NoteOnScheduled(note, 1.0f, start, end);
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

    private float GetDrumGain(int drumId)
    {
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