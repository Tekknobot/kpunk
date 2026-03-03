// Assets/Scripts/KMusicPlayerUI.cs
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using KMusic.UI;
using KMusic;

#if UNITY_ANDROID && !UNITY_EDITOR
using KMusic.Android;
#endif

public class KMusicPlayerUI : MonoBehaviour
{
    [SerializeField] private UIDocument doc;

    [Header("Dedicated player AudioSource (do NOT point at Helm)")]
    [SerializeField] private AudioSource audioSource;

    [Header("Resources audio folder (fallback / editor)")]
    [SerializeField] private string resourcesFolder = "Tracks";

    [Header("Android: Device Music Library (MediaStore)")]
    [SerializeField] private bool useDeviceMusicLibraryOnAndroid = true;
    [SerializeField] private int deviceTrackLimit = 250;

    private VisualElement _root;

    private Button _btnPlay, _btnStop, _btnPrev, _btnNext, _btnLoad, _btnChop, _btnApply;
    private Label _timeLabel, _durLabel, _trackNameLabel;
    private WaveformView _wave;
    private Button _btnSend;

    private bool _chopArmed = false;

    private Button _btnAuto4, _btnAuto8, _btnAuto16;
    private Button _btnAddMode, _btnDelMode, _btnSnap, _btnReset;

    private bool _markerAddMode = true;
    private bool _snapEnabled = true;
    private int _snapDiv = 16;

    // Markers: normalized [0..1], 0 and 1 are implied boundaries when applying.
    private readonly List<float> _chops01 = new();

    private AudioClip[] _clips = Array.Empty<AudioClip>();
    private int _clipIndex = -1;

    private AudioClip _clip;
    private string _clipResourcesPath = null;

    // ✅ Last known playhead position from scrubbing/UI.
    // Used when dropping markers via buttons even if audio isn't playing.
    private float _scrub01 = 0f;

#if UNITY_ANDROID && !UNITY_EDITOR
    private TrackInfo[] _deviceTracks = Array.Empty<TrackInfo>();
    private int _deviceTrackIndex = -1;
    private Coroutine _loadDeviceCoroutine;
    private bool _deviceReady = false;
#endif

    
    private const string PrefKey_LastTrack = "kmusic.player.lastTrack.v1";

    private static void SaveLastTrackId(string id)
    {
        if (string.IsNullOrEmpty(id))
            return;

        PlayerPrefs.SetString(PrefKey_LastTrack, id);
        PlayerPrefs.Save();
    }

    private static string LoadLastTrackId()
    {
        return PlayerPrefs.GetString(PrefKey_LastTrack, "");
    }

private void Awake()
    {
        if (!doc) doc = GetComponent<UIDocument>();
        EnsureDedicatedPlayerAudioSource();
    }

    private void EnsureDedicatedPlayerAudioSource()
    {
        if (audioSource != null) return;

        var child = transform.Find("PlayerAudioSource");
        if (child == null)
        {
            var go = new GameObject("PlayerAudioSource");
            go.transform.SetParent(transform, false);
            child = go.transform;
        }

        audioSource = child.GetComponent<AudioSource>();
        if (audioSource == null) audioSource = child.gameObject.AddComponent<AudioSource>();

        audioSource.playOnAwake = false;
        audioSource.loop = false;
        audioSource.spatialBlend = 0f;
        audioSource.volume = 1f;
    }

    private void OnEnable()
    {
        EnsureDedicatedPlayerAudioSource();

        _root = doc != null ? doc.rootVisualElement : null;
        if (_root == null) return;

        var page = _root.Q<VisualElement>("PagePlayer");
        if (page == null) return;

        _btnPlay = page.Q<Button>("TrackPlay");
        _btnStop = page.Q<Button>("TrackStop");
        _btnPrev = page.Q<Button>("TrackPrev");
        _btnNext = page.Q<Button>("TrackNext");
        _btnLoad = page.Q<Button>("TrackLoad");

        _btnChop = page.Q<Button>("ChopToggle");
        _btnApply = page.Q<Button>("ChopApply");
        _btnSend = page.Q<Button>("ChopSend");

        _btnAuto4 = page.Q<Button>("Auto4");
        _btnAuto8 = page.Q<Button>("Auto8");
        _btnAuto16 = page.Q<Button>("Auto16");

        _btnAddMode = page.Q<Button>("MarkerAddMode");
        _btnDelMode = page.Q<Button>("MarkerDelMode");
        _btnSnap = page.Q<Button>("SnapToggle");
        _btnReset = page.Q<Button>("ResetMarkers");

        _timeLabel = page.Q<Label>("TimeLabel");
        _durLabel = page.Q<Label>("DurLabel");
        _trackNameLabel = page.Q<Label>("TrackName");
        UpdateTrackTitle("PLAYER");

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
            _wave.AddToClassList("km-waveform");
            _wave.style.width = Length.Percent(100);
            _wave.style.height = Length.Percent(100);
            host.Clear();
            host.Add(_wave);
        }

        // ✅ Touch/click interaction on waveform.
        _wave.UserPointer01 -= OnWavePointer01;
        _wave.UserPointer01 += OnWavePointer01;

        // Wire buttons
        if (_btnPlay != null) _btnPlay.clicked += OnPlayClicked;
        if (_btnStop != null) _btnStop.clicked += OnStopClicked;
        if (_btnPrev != null) _btnPrev.clicked += CyclePrevTrack;
        if (_btnNext != null) _btnNext.clicked += CycleNextTrack;
        if (_btnLoad != null) _btnLoad.clicked += CycleNextTrack;

        if (_btnChop != null) _btnChop.clicked += ToggleChop;
        if (_btnApply != null) _btnApply.clicked += ApplyChops;
        if (_btnSend != null) _btnSend.clicked += SendChops;

        if (_btnAuto4 != null) _btnAuto4.clicked += () => AutoChop(4);
        if (_btnAuto8 != null) _btnAuto8.clicked += () => AutoChop(8);
        if (_btnAuto16 != null) _btnAuto16.clicked += () => AutoChop(16);

        // ✅ Your request:
        // Add button ALWAYS drops a marker at the current timestamp/playhead.
        if (_btnAddMode != null) _btnAddMode.clicked += OnAddMarkerButton;

        // Del button: set delete mode (and also delete nearest at current timestamp, consistent with mobile)
        if (_btnDelMode != null) _btnDelMode.clicked += OnDeleteMarkerButton;

        if (_btnSnap != null) _btnSnap.clicked += () => ToggleSnap();
        if (_btnReset != null) _btnReset.clicked += ResetAllMarkers;

        SetMarkerMode(true);
        SetSnapState(_snapEnabled);

        _root.schedule.Execute(UpdatePlayheadUI).Every(33);

        BootAudioSources();
    }

    private void BootAudioSources()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (useDeviceMusicLibraryOnAndroid)
        {
            _deviceReady = false;
            UpdateTrackTitle("REQUESTING MUSIC PERMISSION…");

            AndroidMusicLibrary.EnsurePermissionThenRefresh(this, ok =>
            {
                _deviceTracks = AndroidMusicLibrary.Tracks ?? Array.Empty<TrackInfo>();
                _deviceTrackIndex = (_deviceTracks.Length > 0) ? 0 : -1;
                _deviceReady = ok;

                if (_deviceTracks.Length > 0)
                {
                    int idx = 0;
                    var last = LoadLastTrackId();
                    if (!string.IsNullOrEmpty(last) && last.StartsWith("uri:"))
                    {
                        string want = last.Substring(4);
                        for (int i = 0; i < _deviceTracks.Length; i++)
                        {
                            if (_deviceTracks[i] != null && _deviceTracks[i].uri == want) { idx = i; break; }
                        }
                    }
                    LoadDeviceTrackByIndex(idx);
                }
                else
                {
                    UpdateTrackTitle(
                        "ANDROID MUSIC: 0 tracks\n" +
                        AndroidMusicLibrary.DebugLastStatus + "\n" +
                        AndroidMusicLibrary.DebugLastJsonHead
                    );
                    RefreshClipList();
                    if (_clips.Length > 0) LoadClipByIndex(0);
                }
            }, deviceTrackLimit);

            return;
        }
#endif
        RefreshClipList();
        if (_clips.Length > 0 && _clip == null)
        {
            int idx = 0;
            var last = LoadLastTrackId();
            if (!string.IsNullOrEmpty(last) && last.StartsWith("res:"))
            {
                string wantPath = last.Substring(4);
                // wantPath is like "Tracks/MySong"
                string wantName = wantPath.Contains("/") ? wantPath.Substring(wantPath.LastIndexOf('/') + 1) : wantPath;
                for (int i = 0; i < _clips.Length; i++)
                {
                    if (_clips[i] != null && _clips[i].name == wantName) { idx = i; break; }
                }
            }
            LoadClipByIndex(idx);
        }
        else if (_clips.Length == 0) UpdateTrackTitle("NO TRACKS");
    }

    private void OnDisable()
    {
        if (_wave != null) _wave.UserPointer01 -= OnWavePointer01;

#if UNITY_ANDROID && !UNITY_EDITOR
        if (_loadDeviceCoroutine != null)
        {
            StopCoroutine(_loadDeviceCoroutine);
            _loadDeviceCoroutine = null;
        }
#endif
    }

    private void RefreshClipList()
    {
        _clips = Resources.LoadAll<AudioClip>(resourcesFolder) ?? Array.Empty<AudioClip>();
        if (_clips.Length == 0) _clipIndex = -1;
        else _clipIndex = Mathf.Clamp(_clipIndex, 0, _clips.Length - 1);
    }

    private void CyclePrevTrack()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (useDeviceMusicLibraryOnAndroid && _deviceReady)
        {
            if (_deviceTracks == null || _deviceTracks.Length == 0) return;
            int prev = (_deviceTrackIndex <= 0) ? (_deviceTracks.Length - 1) : (_deviceTrackIndex - 1);
            LoadDeviceTrackByIndex(prev);
            return;
        }
#endif
        RefreshClipList();
        if (_clips.Length == 0) return;
        int prevRes = (_clipIndex <= 0) ? (_clips.Length - 1) : (_clipIndex - 1);
        LoadClipByIndex(prevRes);
    }

    private void CycleNextTrack()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (useDeviceMusicLibraryOnAndroid && _deviceReady)
        {
            if (_deviceTracks == null || _deviceTracks.Length == 0) return;
            int next = (_deviceTrackIndex + 1) % _deviceTracks.Length;
            LoadDeviceTrackByIndex(next);
            return;
        }
#endif
        RefreshClipList();
        if (_clips.Length == 0) return;
        int nextRes = (_clipIndex + 1) % _clips.Length;
        LoadClipByIndex(nextRes);
    }

    private void LoadClipByIndex(int index)
    {
        if (_clips == null || _clips.Length == 0) return;
        index = Mathf.Clamp(index, 0, _clips.Length - 1);

        _chops01.Clear();
        SyncMarkersToWave();

        _clipIndex = index;
        _clip = _clips[_clipIndex];
        _clipResourcesPath = _clip != null ? ($"{resourcesFolder}/" + _clip.name) : null;

        // Save last track + cache clip for chop application.
        if (!string.IsNullOrEmpty(_clipResourcesPath))
        {
            SaveLastTrackId("res:" + _clipResourcesPath);
            KMusicChopState.SetCachedClip(_clipResourcesPath, _clip);
        }
        if (_clip == null) return;

        audioSource.Stop();
        audioSource.clip = _clip;
        audioSource.time = 0f;

        _scrub01 = 0f;

        _wave.SetClip(_clip);
        _wave.SetPlayhead01(0f);
        UpdateTimeLabels(0f, _clip.length);
        UpdateTrackTitle(_clip.name);
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    private void LoadDeviceTrackByIndex(int index)
    {
        if (_deviceTracks == null || _deviceTracks.Length == 0) return;
        index = Mathf.Clamp(index, 0, _deviceTracks.Length - 1);
        _deviceTrackIndex = index;

        _chops01.Clear();
        SyncMarkersToWave();

        var t = _deviceTracks[_deviceTrackIndex];
        string title = string.IsNullOrEmpty(t.title) ? "TRACK" : t.title;
        if (!string.IsNullOrEmpty(t.artist)) title += $" — {t.artist}";
        UpdateTrackTitle(title);

        if (_loadDeviceCoroutine != null)
        {
            StopCoroutine(_loadDeviceCoroutine);
            _loadDeviceCoroutine = null;
        }

        audioSource.Stop();
        _wave.SetClip(null);
        _wave.SetPlayhead01(0f);
        _wave.SetMarkers01(_chops01);
        _scrub01 = 0f;

        _loadDeviceCoroutine = StartCoroutine(CoLoadDeviceTrack(t));
    }

    private IEnumerator CoLoadDeviceTrack(TrackInfo t)
    {
        bool done = false;
        AudioClip loaded = null;

        yield return AndroidMusicLibrary.LoadTrackToClip(t, clip =>
        {
            loaded = clip;
            done = true;
        });

        while (!done) yield return null;
        if (loaded == null) yield break;

        _clip = loaded;
        _clipResourcesPath = !string.IsNullOrEmpty(t.uri) ? ("uri:" + t.uri) : null;

        if (!string.IsNullOrEmpty(_clipResourcesPath))
        {
            SaveLastTrackId(_clipResourcesPath);
            KMusicChopState.SetCachedClip(_clipResourcesPath, _clip);
        }
        audioSource.Stop();
        audioSource.clip = _clip;
        audioSource.time = 0f;

        _scrub01 = 0f;

        _wave.SetClip(_clip);
        _wave.SetPlayhead01(0f);
        UpdateTimeLabels(0f, _clip.length);
    }
#endif

    private void OnPlayClicked()
    {
        if (_clip == null || audioSource == null) return;
        audioSource.Stop();
        audioSource.clip = _clip;
        audioSource.Play();
    }

    private void OnStopClicked()
    {
        if (audioSource != null) audioSource.Stop();
    }

    private void ToggleChop()
    {
        _chopArmed = !_chopArmed;

        if (_btnChop != null)
        {
            if (_chopArmed) _btnChop.AddToClassList("km-chop-toggle--active");
            else _btnChop.RemoveFromClassList("km-chop-toggle--active");
        }

        UpdateTrackTitle(_chopArmed ? "CHOP MODE" : (_clip != null ? _clip.name : "TRACK"));
    }

    // ----------------------------
    // Waveform interaction (touch + mouse)
    // ----------------------------
    private void OnWavePointer01(float t01, bool isDrag)
    {
        if (_clip == null || _clip.length <= 0f || audioSource == null) return;

        if (_chopArmed)
        {
            // Drag scrubs regardless; tap adds/deletes depending on mode.
            if (isDrag) { SeekTo01(t01); return; }

            if (_markerAddMode) DropChopMarkerAt01(t01);
            else DeleteNearestMarkerTo01(t01);
            return;
        }

        SeekTo01(t01);
    }

    private void SeekTo01(float t01)
    {
        if (_clip == null || _clip.length <= 0f || audioSource == null) return;

        t01 = Mathf.Clamp01(t01);
        _scrub01 = t01;

        float t = t01 * _clip.length;
        audioSource.time = Mathf.Clamp(t, 0f, Mathf.Max(0f, _clip.length - 0.001f));

        _wave?.SetPlayhead01(t01);
        UpdateTimeLabels(audioSource.time, _clip.length);
    }

    // ----------------------------
    // ✅ Add/Delete buttons behavior
    // ----------------------------
    private void OnAddMarkerButton()
    {
        SetMarkerMode(true);

        if (_clip == null || _clip.length <= 0f || audioSource == null) return;

        float t01;

        // If playing, use real time.
        if (audioSource.isPlaying)
        {
            t01 = Mathf.Clamp01(audioSource.time / _clip.length);
            _scrub01 = t01;
        }
        else
        {
            // Not playing: use last scrubbed playhead.
            t01 = Mathf.Clamp01(_scrub01);
        }

        DropChopMarkerAt01(t01);
    }

    private void OnDeleteMarkerButton()
    {
        SetMarkerMode(false);

        if (_clip == null || _clip.length <= 0f || audioSource == null) return;

        float t01;
        if (audioSource.isPlaying)
        {
            t01 = Mathf.Clamp01(audioSource.time / _clip.length);
            _scrub01 = t01;
        }
        else
        {
            t01 = Mathf.Clamp01(_scrub01);
        }

        DeleteNearestMarkerTo01(t01);
    }

    // ----------------------------
    // Marker ops
    // ----------------------------
    private void DropChopMarkerAt01(float t01)
    {
        if (_clip == null || _clip.length <= 0f) return;
        if (_chops01.Count >= 16) return;

        t01 = Mathf.Clamp01(t01);
        if (_snapEnabled) t01 = Snap01(t01, _snapDiv);

        // avoid duplicates (within ~0.5% of timeline)
        for (int i = 0; i < _chops01.Count; i++)
            if (_snapEnabled)
            {
                float minSliceSeconds = 0.05f;
                float minSlice01 = minSliceSeconds / _clip.length;

                if (Mathf.Abs(_chops01[i] - t01) < minSlice01)
                    return;
            }

        // keep away from boundaries since 0 and 1 are implied
        if (t01 < 0.01f) t01 = 0.01f;
        if (t01 > 0.99f) t01 = 0.99f;

        _chops01.Add(t01);
        _chops01.Sort();
        SyncMarkersToWave();
        _wave?.SetPlayhead01(t01);
        _scrub01 = t01;

        UpdateTrackTitle($"MARKER + {t01:0.000}");

        Debug.Log($"ADD marker t01={t01:0.000} snap={_snapEnabled} div={_snapDiv}");
    }

    private void DeleteNearestMarkerTo01(float t01)
    {
        if (_chops01.Count == 0) return;

        t01 = Mathf.Clamp01(t01);

        int best = -1;
        float bestDist = 999f;

        for (int i = 0; i < _chops01.Count; i++)
        {
            float d = Mathf.Abs(_chops01[i] - t01);
            if (d < bestDist) { bestDist = d; best = i; }
        }

        // must be somewhat close to delete
        if (best >= 0 && bestDist <= 0.03f)
        {
            float removed = _chops01[best];
            _chops01.RemoveAt(best);
            SyncMarkersToWave();
            UpdateTrackTitle($"MARKER - {removed:0.000}");
        }
    }

    // ----------------------------
    // Chop apply/send
    // ----------------------------
    private void ApplyChops()
    {
        if (_clip == null || _clip.length <= 0f) return;

        // Build boundaries [0, markers..., 1]
        var boundaries = new List<float>(_chops01.Count + 2) { 0f };
        boundaries.AddRange(_chops01);
        boundaries.Add(1f);
        boundaries.Sort();

        // If your sampler reads chop state from KMusicChopState, you can store it here too.
        // This keeps Apply and Send consistent.
        if (!string.IsNullOrEmpty(_clipResourcesPath))
            KMusicChopState.SaveApplied(_clipResourcesPath, _chops01);
        else
            KMusicChopState.SaveAppliedFromClip(_clip, resourcesPathOrNull: null, markerPositions01: _chops01);

        UpdateTrackTitle($"APPLIED {boundaries.Count - 1} SLICES");
    }

    private void SendChops()
    {
        if (_clip == null || _clip.length <= 0f) return;

        if (!string.IsNullOrEmpty(_clipResourcesPath))
            KMusicChopState.SaveApplied(_clipResourcesPath, _chops01);
        else
            KMusicChopState.SaveAppliedFromClip(_clip, resourcesPathOrNull: null, markerPositions01: _chops01);

        UpdateTrackTitle($"SENT {_chops01.Count + 1} CHOPS");
    }

    private void SyncMarkersToWave()
    {
        _wave?.SetMarkers01(_chops01);
    }

    private void UpdatePlayheadUI()
    {
        if (_clip == null || _clip.length <= 0f || audioSource == null || audioSource.clip != _clip)
        {
            _wave?.SetPlayhead01(_scrub01);
            UpdateTimeLabels((_clip != null) ? (_scrub01 * _clip.length) : 0f, _clip != null ? _clip.length : 0f);
            return;
        }

        float t = audioSource.time;
        float t01 = Mathf.Clamp01(t / _clip.length);

        // ✅ keep scrub position in sync while playing
        _scrub01 = t01;

        _wave?.SetPlayhead01(t01);
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
        return $"{m:00}:{s:00}";
    }

    private void UpdateTrackTitle(string title)
    {
        if (_trackNameLabel == null) return;
        _trackNameLabel.text = string.IsNullOrEmpty(title) ? "TRACK" : title;
    }

    // ----------------------------
    // UI state helpers
    // ----------------------------
    private void SetMarkerMode(bool add)
    {
        _markerAddMode = add;
        if (_btnAddMode != null) _btnAddMode.EnableInClassList("km-chip--active", add);
        if (_btnDelMode != null) _btnDelMode.EnableInClassList("km-chip--active", !add);
    }

    private void ToggleSnap() => SetSnapState(!_snapEnabled);

    private void SetSnapState(bool on)
    {
        _snapEnabled = on;
        if (_btnSnap != null) _btnSnap.EnableInClassList("km-chip--active", on);
    }

    private void ResetAllMarkers()
    {
        _chops01.Clear();
        SyncMarkersToWave();

        // Also clear any applied chop set used by the sampler.
        KMusicChopState.ClearApplied();

        UpdateTrackTitle("MARKERS RESET");
    }

    private void AutoChop(int divisions)
    {
        if (_clip == null || _clip.length <= 0f) return;

        _chops01.Clear();
        for (int i = 1; i < divisions; i++)
        {
            float t01 = i / (float)divisions;
            if (t01 < 0.01f) t01 = 0.01f;
            if (t01 > 0.99f) t01 = 0.99f;
            _chops01.Add(t01);
        }

        SyncMarkersToWave();
        UpdateTrackTitle($"AUTO {divisions}");
    }

    private static float Snap01(float t01, int div)
    {
        if (div <= 1) return Mathf.Clamp01(t01);
        float step = 1f / div;
        return Mathf.Clamp01(Mathf.Round(t01 / step) * step);
    }
}