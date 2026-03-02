// Scripts/KMusicPlayerUI.cs
// Player page: load a test AudioClip from Resources, render waveform, show playhead + chop markers.
// NOTE: Loading arbitrary mp3/wav from device storage (Android Music folder) requires
// runtime permission + MediaStore / file picker + an audio decoder. We'll wire that later.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using KMusic.UI;

public class KMusicPlayerUI : MonoBehaviour
{
    [SerializeField] private UIDocument doc;
    [SerializeField] private AudioSource audioSource;

    [Header("Resources test clip")]
    [Tooltip("AudioClip placed under Assets/Resources/<folder>/<name>.wav (or mp3 as an imported asset).")]
    [SerializeField] private string resourcesFolder = "Tracks";

    [Tooltip("Name of the clip asset (no extension). Example: 'test' loads Resources/Tracks/test")]
    [SerializeField] private string defaultTestClipName = "test";

    private VisualElement _root;
    private Button _btnLoad, _btnChop, _btnApply;
    private Label _timeLabel, _durLabel;

    private WaveformView _wave;           // custom element (preferred)
    private VisualElement _waveHost;      // fallback host if UXML uses a plain VisualElement

    private bool _chopArmed = false;
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

        var page = _root.Q<VisualElement>("PagePlayer");
        if (page == null) return; // player page not present

        _btnLoad = page.Q<Button>("TrackLoad");
        _btnChop = page.Q<Button>("ChopToggle");
        _btnApply = page.Q<Button>("ChopApply");

        _timeLabel = page.Q<Label>("TimeLabel");
        _durLabel = page.Q<Label>("DurLabel");

        // 1) Prefer direct WaveformView in UXML
        _wave = page.Q<WaveformView>("Waveform");

        // 2) Fallback: if UXML has a placeholder element, swap in WaveformView at runtime
        if (_wave == null)
        {
            _waveHost = page.Q<VisualElement>("WaveformHost") ?? page.Q<VisualElement>("Waveform");
            if (_waveHost == null)
            {
                Debug.LogError("KMusicPlayerUI: Could not find WaveformHost (or Waveform) in UXML.");
                return;
            }

            _wave = new WaveformView { name = "Waveform" };
            // Keep styling consistent
            _wave.AddToClassList("km-waveform");

            // Replace placeholder
            var parent = _waveHost.parent;
            int idx = parent.IndexOf(_waveHost);
            parent.Remove(_waveHost);
            parent.Insert(idx, _wave);
        }

        if (_btnLoad != null) _btnLoad.clicked += OnLoadClicked;
        if (_btnChop != null) _btnChop.clicked += ToggleChop;
        if (_btnApply != null) _btnApply.clicked += ApplyChops;

        // Keyboard: Enter drops chop marker while armed
        page.RegisterCallback<KeyDownEvent>(OnKeyDown);

        // Update playhead ~30fps
        page.schedule.Execute(UpdatePlayheadUI).Every(33);

#if UNITY_EDITOR || UNITY_STANDALONE
        // auto-load in editor/desktop so you can see waveform immediately
        if (_clip == null && !string.IsNullOrEmpty(defaultTestClipName))
            LoadFromResources(defaultTestClipName);
#endif
    }

    private void OnDisable()
    {
        if (_btnLoad != null) _btnLoad.clicked -= OnLoadClicked;
        if (_btnChop != null) _btnChop.clicked -= ToggleChop;
        if (_btnApply != null) _btnApply.clicked -= ApplyChops;
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

    private void OnLoadClicked()
    {
        if (string.IsNullOrEmpty(defaultTestClipName))
        {
            Debug.LogWarning("KMusicPlayerUI: defaultTestClipName is empty.");
            return;
        }

        LoadFromResources(defaultTestClipName);
    }

    private void LoadFromResources(string clipNameNoExt)
    {
        _chops01.Clear();
        SyncMarkersToWave();

        string path = string.IsNullOrEmpty(resourcesFolder) ? clipNameNoExt : $"{resourcesFolder}/{clipNameNoExt}";
        _clip = Resources.Load<AudioClip>(path);

        if (_clip == null)
        {
            Debug.LogError($"KMusicPlayerUI: Could not load AudioClip at Resources/{path}. " +
                           "Put a .wav/.mp3 there (imported as an AudioClip), e.g. Assets/Resources/Tracks/test.wav");
            return;
        }

        audioSource.clip = _clip;
        audioSource.time = 0f;

        _wave.SetClip(_clip);
        UpdateTimeLabels(0f, _clip.length);
        _wave.SetPlayhead01(0f);

        Debug.Log($"KMusicPlayerUI: Loaded {_clip.name} len={_clip.length:0.00}s");
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

        // Here is where you’ll map slices into Chop01..Chop16 in your sampler system.
        // For now, we just log ranges.
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
        if (_durLabel != null) _durLabel.text = FormatTime(dur);
    }

    private static string FormatTime(float seconds)
    {
        seconds = Mathf.Max(0f, seconds);
        int m = (int)(seconds / 60f);
        int s = (int)(seconds % 60f);
        return $"{m}:{s:00}";
    }
}
