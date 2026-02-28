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
/// - Uses StepGrid values as "drum id" (1..8) per step.
/// - Provides Play/Stop UI buttons (topbar).
/// - Schedules Sampler.NoteOnScheduled against DSP time (tight timing).
/// - Visualizes playhead on the StepGrid.
/// 
/// NOTE: For now, drum samples are discovered in-editor by scanning:
///   Assets/Audio/Kit Samples HMA/<kit folders>
/// This works in Editor immediately. For mobile builds, you will want to
/// bake a DrumKitLibrary asset (we can add next) so builds don't depend on AssetDatabase.
/// </summary>
[RequireComponent(typeof(UIDocument))]
public class KMusicDrumSequencer : MonoBehaviour
{
    [Header("UI Names")]
    public string playButtonName = "PlayButton";
    public string stopButtonName = "StopButton";
    public string drumGridName = "DrumStepGrid"; // currently used for drums on SAMPLER tab
    [Header("Drum Buttons (Sampler page)")]
    public string btnKick = "DrumKick";
    public string btnSnare = "DrumSnare";
    public string btnClap = "DrumClap";
    public string btnHatClosed = "DrumHatC";
    public string btnHatOpen = "DrumHatO";
    public string btnRide = "DrumPerc"; // UXML uses Perc as "ride/perc"
    public string btnRim = "DrumTom";   // UXML uses Tom as extra slot (we map to rim for now)
    public string btnCrash = "DrumCrash";
    [Header("Timing")]
    [Range(40, 200)] public float fallbackBpm = 107f;
    [Tooltip("How far ahead (seconds) to schedule notes.")]
    public float lookaheadSeconds = 0.10f;
    [Tooltip("Small start delay (seconds) when pressing Play so scheduling window can fill.")]
    public float startDelaySeconds = 0.05f;
    [Header("Debug")]
    public bool verbose = false;

    
    private UIDocument _doc;
    private VisualElement _root;
    private Button _playBtn;
    private Button _stopBtn;
    private StepGrid _grid;
    private StepGrid _gridSeq;
    private StepGrid _gridSample;
    private StepGrid _gridDrum;    
    private Label _bpmLabel;
    // 16 steps (2x8) holds drum id (0 empty, 1..8 drum)
    private readonly int[,] _pattern = new int[2, 8];
    private int _activeDrumId = 1;
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
    private int _activeDrum = 1;   // current brush drum id
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
#if UNITY_EDITOR
        TryLoadFirstKitFromAssets();
#else
        Debug.LogWarning("[KMusicDrumSequencer] Drum kit auto-scan is editor-only right now. We'll add a runtime DrumKitLibrary for mobile builds next.");
#endif
    }
    private void OnEnable()
    {
        // UI Toolkit may not be ready immediately (KMusicApp assigns UXML in Awake),
        // so bind after 2 frames.
        StartCoroutine(BindWhenReady());
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
       _grid = _root.Q<StepGrid>("DrumStepGrid");       
        _gridSeq    = _root.Q<StepGrid>("StepGrid");
        _gridSample = _root.Q<StepGrid>("SampleStepGrid") ?? _root.Q<StepGrid>("SamplerStepGrid");
        _gridDrum   = _root.Q<StepGrid>("DrumStepGrid");        
        _bpmLabel = _root.Q<Label>("BpmLabel");
        if (verbose)
            Debug.Log($"[KMusicDrumSequencer] bind play={(_playBtn!=null)} stop={(_stopBtn!=null)} grid='{drumGridName}' found={(_grid!=null)}");
        if (_playBtn == null || _stopBtn == null || _grid == null)
            return false;
        // avoid double wiring
        _playBtn.clicked -= OnPlayClicked;
        _stopBtn.clicked -= OnStopClicked;
        _playBtn.clicked += OnPlayClicked;
        _stopBtn.clicked += OnStopClicked;
        WireDrumButtons();
        // Configure grid behavior: label = drum id (or short text), tint optional later
        _grid.EnableValueLabels(true, DrumLabel);
        _grid.EnableValueTint(true, DrumTint);
        _grid.ClearAll();
        _grid.SetPaintValue(_activeDrumId);
        _grid.SetPlayhead(-1);
        // Capture pattern updates
        _grid.OnCellValueChanged += OnGridValueChanged;
        _grid.OnCellClicked += (r,c) =>
        {
            int v = _grid.GetValue(r,c);

            if (v > 0)
                TriggerDrumNow(v);        // audition existing step
            else if (_activeDrum > 0)
                TriggerDrumNow(_activeDrum); // audition selected drum
        };
        // Initial label refresh
        UpdateBpmLabel();
        return true;
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
        // default active highlight
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
        _activeDrum = _activeDrumId;

        if (_grid != null)
            _grid.SetPaintValue(_activeDrumId);

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
        if (r < 0 || r >= 2 || c < 0 || c >= 8) return;
        _pattern[r, c] = v;

        // Audition on edit (only when stopped)
        if (!_playing && v > 0)
            TriggerDrumNow(v);
    }
    private void OnPlayClicked()
    {
        if (_playing) return;
        if (_sampler == null || _sampler.keyzones == null || _sampler.keyzones.Count == 0)
            Debug.LogWarning("[KMusicDrumSequencer] Sampler has no keyzones; drum kit may not be loaded.");
        if (_clipByDrumId.Count == 0)
            Debug.LogWarning("[KMusicDrumSequencer] No drum clips loaded; check Assets/Audio/Kit Samples HMA and keyword mapping.");
        _playing = true;
        // reset step to 0 on play for now (later: quantized start / song mode)
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
        // stop currently ringing voices
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
        double stepDur = 60.0 / Math.Max(1.0, bpm) / 4.0; // 16th note in seconds
        double now = AudioSettings.dspTime;
        double windowEnd = now + lookaheadSeconds;
        UpdateVisualPlayhead(now, stepDur);
        UpdateBpmLabel();
        // schedule as many steps as fit inside lookahead window
        while (_nextStepDspTime < windowEnd)
        {
            int r = _stepIndex / 8;
            int c = _stepIndex % 8;
            int drumId = _pattern[r, c];
            if (drumId > 0)
            {
                if (_clipByDrumId.TryGetValue(drumId, out var clip) && clip != null)
                {
                    int note = _noteByDrumId.TryGetValue(drumId, out var n) ? n : 36;
                    // schedule note for clip duration (sampler uses note off for looping voices)
                    double end = _nextStepDspTime + Math.Max(0.05, clip.length);
                    _sampler.NoteOnScheduled(note, 1.0f, _nextStepDspTime, end);
                }
            }
            // advance
            _stepIndex = (_stepIndex + 1) % 16;
            _nextStepDspTime += stepDur;
        }
    }

    private void UpdateVisualPlayhead(double nowDsp, double stepDur)
    {
        if (_grid == null) return;
        // Before start delay, show no playhead
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
        step = ((step % 16) + 16) % 16;
        if (step != _lastVisualStep)
        {
            _lastVisualStep = step;
            if (_gridSeq != null)    _gridSeq.SetPlayhead(step);
            if (_gridSample != null) _gridSample.SetPlayhead(step);
            if (_gridDrum != null)   _gridDrum.SetPlayhead(step);
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

    private void OnGridCellClicked(int r, int c)
    {
        // Tap audition: if cell has a value, play that; otherwise play current brush.
        if (_sampler == null) return;
        int v = _grid != null ? _grid.GetValue(r, c) : 0;
        if (v <= 0) v = _activeDrumId;
        TriggerDrumNow(v);
    }

    private void TriggerDrumNow(int drumId)
    {
        if (!_noteByDrumId.TryGetValue(drumId, out var note)) return;
        if (!_clipByDrumId.TryGetValue(drumId, out var clip) || clip == null) return;

        // Immediate audition (not scheduled)
        _sampler.NoteOn(note, 1.0f);
        // Schedule off shortly after clip length (or a small minimum)
        float off = Mathf.Max(0.05f, clip.length);
        StartCoroutine(NoteOffAfter(note, off));
    }

    private System.Collections.IEnumerator NoteOffAfter(int note, float seconds)
    {
        yield return new WaitForSeconds(seconds);
        if (_sampler != null) _sampler.NoteOff(note);
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
            6 => "R",
            7 => "RM",
            8 => "X",
            _ => ""
        };
    }
    private Color DrumTint(int v)
    {
        // subtle tint per drum, alpha handled by StepGrid active background
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
        // If a sampler exists already, use it. Otherwise create one.
        _sampler = FindObjectOfType<Sampler>();
        if (_sampler == null)
        {
            var go = new GameObject("KMusicDrumSampler");
            var src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            _sampler = go.AddComponent<Sampler>();
            _sampler.keyzones.Clear();
        }
        else
        {
            _sampler.keyzones.Clear();
        }
    }
#if UNITY_EDITOR
    private void TryLoadFirstKitFromAssets()
    {
        // Find kit folders under Assets/Audio/Kit Samples HMA
        string root = "Assets/Audio/Kit Samples HMA";
        if (!AssetDatabase.IsValidFolder(root))
        {
            Debug.LogWarning($"[KMusicDrumSequencer] Folder not found: {root}");
            return;
        }
        string[] sub = AssetDatabase.GetSubFolders(root);
        if (sub == null || sub.Length == 0)
        {
            Debug.LogWarning($"[KMusicDrumSequencer] No kit folders under: {root}");
            return;
        }
        // Choose first kit folder by default
        string kitFolder = sub[0];
        LoadKitFolder(kitFolder);
        if (verbose) Debug.Log($"[KMusicDrumSequencer] Loaded kit: {kitFolder}");
    }
    private void LoadKitFolder(string kitFolder)
    {
        _clipByDrumId.Clear();
        // Collect all .wav AudioClips in folder
        string[] guids = AssetDatabase.FindAssets("t:AudioClip", new[] { kitFolder });
        foreach (string g in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(g);
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            if (clip == null) continue;
            string lower = System.IO.Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            // map keywords
            if (lower.Contains("kick")) _clipByDrumId[1] = clip;
            else if (lower.Contains("snare")) _clipByDrumId[2] = clip;
            else if (lower.Contains("clap")) _clipByDrumId[3] = clip;
            else if (lower.Contains("hh closed") || lower.Contains("hat closed") || lower.Contains("hhc")) _clipByDrumId[4] = clip;
            else if (lower.Contains("hh open") || lower.Contains("hat open") || lower.Contains("hho")) _clipByDrumId[5] = clip;
            else if (lower.Contains("ride") || lower.Contains("perc")) _clipByDrumId[6] = clip;
            else if (lower.Contains("rim")) _clipByDrumId[7] = clip;
            else if (lower.Contains("crash")) _clipByDrumId[8] = clip;
        }
        // Build keyzones for sampler
        _sampler.keyzones.Clear();
        for (int id = 1; id <= 8; id++)
        {
            if (!_clipByDrumId.TryGetValue(id, out var clip) || clip == null)
                continue;
            int note = _noteByDrumId[id];
            var kz = new Keyzone();
            kz.minKey = note;
            kz.maxKey = note;
            kz.rootKey = note;
            kz.audioClip = clip;
            _sampler.keyzones.Add(kz);
        }
    }
#endif
}