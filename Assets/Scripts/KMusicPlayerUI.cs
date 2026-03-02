// Assets/Scripts/KMusicPlayerUI.cs
// Player page: load AudioClip(s) from Resources/<resourcesFolder>/, render waveform,
// show playhead + chop markers, and hook global PLAY/STOP buttons.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using KMusic.UI;

public class KMusicPlayerUI : MonoBehaviour
{
    [SerializeField] private UIDocument doc;
    [SerializeField] private AudioSource audioSource;

    [Header("Resources audio folder")]
    [Tooltip("AudioClips placed under Assets/Resources/<folder>/ (ex: Assets/Resources/Tracks/*.mp3 or *.wav)")]
    [SerializeField] private string resourcesFolder = "Tracks";

    private VisualElement _root;

    // Global topbar transport
    private Button _globalPlay;
    private Button _globalStop;

    // Player page UI
    private Button _btnLoad, _btnChop, _btnApply;
    private Label _timeLabel, _durLabel, _trackNameLabel;
    private WaveformView _wave;

    private bool _chopArmed = false;

    // Available clips in Resources/<resourcesFolder>
    private AudioClip[] _clips = Array.Empty<AudioClip>();
    private int _clipIndex = -1;
    private AudioClip _clip;

    // Store ONLY user chop markers (max 16). 0 and 1 are implied boundaries when applying.
    private readonly List<float> _chops01 = new();

    private void Awake()
    {
        if (!doc) doc = GetComponent<UIDocument>();
        if (!audioSource) audioSource = GetComponent<AudioSource>();
        if (!audioSource) audioSource = gameObject.AddComponent<AudioSource>();
    }

    private void OnEnable()
    {
        _root = doc != null ? doc.rootVisualElement : null;
        if (_root == null) return;

        // --- GLOBAL PLAY/STOP (topbar) ---
        _globalPlay = _root.Q<Button>("PlayButton");
        _globalStop = _root.Q<Button>("StopButton");

        if (_globalPlay != null) _globalPlay.clicked += OnGlobalPlayClicked;
        if (_globalStop != null) _globalStop.clicked += OnGlobalStopClicked;

        // --- PLAYER PAGE ---
        var page = _root.Q<VisualElement>("PagePlayer");
        if (page == null) return;

        _btnLoad  = page.Q<Button>("TrackLoad");
        _btnChop  = page.Q<Button>("ChopToggle");
        _btnApply = page.Q<Button>("ChopApply");

        _timeLabel = page.Q<Label>("TimeLabel");
        _durLabel  = page.Q<Label>("DurLabel");
        _trackNameLabel = page.Q<Label>("TrackName");

        // --- Waveform host (UXML is a plain VisualElement) ---
        var host = page.Q<VisualElement>("Waveform");
        if (host == null)
        {
            Debug.LogError("KMusicPlayerUI: Could not find Waveform host VisualElement (name='Waveform') in UXML.");
            return;
        }

        _wave = host.Q<WaveformView>("WaveformView");
        if (_wave == null)
        {
            _wave = new WaveformView { name = "WaveformView" };
            _wave.AddToClassList("km-waveform"); // keep your styling
            _wave.style.width = Length.Percent(100);
            _wave.style.height = Length.Percent(100);
            host.Clear();
            host.Add(_wave);
        }
        if (_btnLoad != null)  _btnLoad.clicked += CycleNextTrack;
        if (_btnChop != null)  _btnChop.clicked += ToggleChop;
        if (_btnApply != null) _btnApply.clicked += ApplyChops;

        // Keyboard: Enter drops chop marker while armed (mainly editor/desktop)
        page.RegisterCallback<KeyDownEvent>(OnKeyDown);

        // Update playhead ~30fps
        page.schedule.Execute(UpdatePlayheadUI).Every(33);

        // Load all clips in folder and auto-load first
        RefreshClipList();
        if (_clips.Length > 0 && _clip == null)
            LoadClipByIndex(0);
        else if (_clips.Length == 0)
            Debug.LogWarning($"KMusicPlayerUI: No AudioClips found in Resources/{resourcesFolder}/");
    }

    private void OnDisable()
    {
        if (_globalPlay != null) _globalPlay.clicked -= OnGlobalPlayClicked;
        if (_globalStop != null) _globalStop.clicked -= OnGlobalStopClicked;

        if (_btnLoad != null)  _btnLoad.clicked -= CycleNextTrack;
        if (_btnChop != null)  _btnChop.clicked -= ToggleChop;
        if (_btnApply != null) _btnApply.clicked -= ApplyChops;
    }

    private void RefreshClipList()
    {
        // Loads everything under Resources/<resourcesFolder> as AudioClip
        _clips = Resources.LoadAll<AudioClip>(resourcesFolder) ?? Array.Empty<AudioClip>();

        // Keep index valid
        if (_clips.Length == 0) _clipIndex = -1;
        else _clipIndex = Mathf.Clamp(_clipIndex, 0, _clips.Length - 1);

        // Optional: log what we found
        if (_clips.Length > 0)
        {
            var names = new string[_clips.Length];
            for (int i = 0; i < _clips.Length; i++) names[i] = _clips[i] != null ? _clips[i].name : "null";
            Debug.Log($"KMusicPlayerUI: Found {_clips.Length} clip(s) in Resources/{resourcesFolder}: {string.Join(", ", names)}");
        }
    }

    private void CycleNextTrack()
    {
        RefreshClipList();
        if (_clips.Length == 0) return;

        int next = (_clipIndex + 1) % _clips.Length;
        LoadClipByIndex(next);
    }

    private void LoadClipByIndex(int index)
    {
        if (_clips == null || _clips.Length == 0) return;
        index = Mathf.Clamp(index, 0, _clips.Length - 1);

        _chops01.Clear();
        SyncMarkersToWave();

        _clipIndex = index;
        _clip = _clips[_clipIndex];

        if (_clip == null)
        {
            Debug.LogError("KMusicPlayerUI: Selected clip is null.");
            return;
        }

        audioSource.clip = _clip;
        audioSource.time = 0f;

        _wave.SetClip(_clip);
        _wave.SetPlayhead01(0f);
        UpdateTimeLabels(0f, _clip.length);
        UpdateTrackTitle(_clip.name);

        Debug.Log($"KMusicPlayerUI: Loaded {_clip.name} len={_clip.length:0.00}s");
    }

    private void UpdateTrackTitle(string title)
    {
        if (_trackNameLabel == null) return;
        // You can format however you like. Keeping it simple:
        _trackNameLabel.text = string.IsNullOrEmpty(title) ? "TRACK" : title;
    }

    private void OnGlobalPlayClicked()
    {
        if (_clip == null)
        {
            RefreshClipList();
            if (_clips.Length > 0) LoadClipByIndex(0);
        }

        if (audioSource.clip == null) return;

        // If at end, restart
        if (audioSource.time >= audioSource.clip.length - 0.01f)
            audioSource.time = 0f;

        audioSource.Play();
    }

    private void OnGlobalStopClicked()
    {
        if (audioSource == null) return;
        audioSource.Stop();
        audioSource.time = 0f;
        UpdatePlayheadUI(); // snap UI back to 0
    }

    private void OnKeyDown(KeyDownEvent evt)
    {
        if (!_chopArmed) return;

        if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
        {
            DropChopMarkerAtPlayhead();
            evt.StopPropagation();
        }
    }

    private void ToggleChop()
    {
        _chopArmed = !_chopArmed;
        if (_btnChop != null)
        {
            if (_chopArmed) _btnChop.AddToClassList("km-pillbtn--active");
            else _btnChop.RemoveFromClassList("km-pillbtn--active");
        }
    }

    private void DropChopMarkerAtPlayhead()
    {
        if (_clip == null || _clip.length <= 0f) return;

        if (_chops01.Count >= 16)
        {
            Debug.Log("KMusicPlayerUI: Max 16 chop markers reached.");
            return;
        }

        float t01 = Mathf.Clamp01(audioSource.time / _clip.length);

        // Avoid duplicates (within ~0.5% of length)
        for (int i = 0; i < _chops01.Count; i++)
            if (Mathf.Abs(_chops01[i] - t01) < 0.005f) return;

        _chops01.Add(t01);
        _chops01.Sort();
        SyncMarkersToWave();
    }

    private void ApplyChops()
    {
        // Boundaries = [0] + chops + [1]
        var boundaries = new List<float>(18) { 0f };
        boundaries.AddRange(_chops01);
        boundaries.Add(1f);

        for (int i = 0; i < 16; i++)
        {
            if (i + 1 >= boundaries.Count) break;
            float a = boundaries[i];
            float b = boundaries[i + 1];
            Debug.Log($"CHOP {i + 1:00}: {a:0.000} -> {b:0.000}");
        }
    }

    private void SyncMarkersToWave()
    {
        _wave.SetMarkers01(_chops01);
    }

    private void UpdatePlayheadUI()
    {
        if (_clip == null || _clip.length <= 0f)
        {
            _wave?.SetPlayhead01(0f);
            UpdateTimeLabels(0f, 0f);
            return;
        }

        float t = audioSource.time;
        float t01 = Mathf.Clamp01(t / _clip.length);

        _wave.SetPlayhead01(t01);
        UpdateTimeLabels(t, _clip.length);
    }

    private void UpdateTimeLabels(float t, float dur)
    {
        if (_timeLabel != null) _timeLabel.text = FormatTime(t);
        if (_durLabel != null)  _durLabel.text  = FormatTime(dur);
    }

    private static string FormatTime(float seconds)
    {
        seconds = Mathf.Max(0f, seconds);
        int m = (int)(seconds / 60f);
        int s = (int)(seconds % 60f);
        return $"{m}:{s:00}";
    }
}