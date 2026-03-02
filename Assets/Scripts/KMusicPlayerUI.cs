// Assets/Scripts/KMusicPlayerUI.cs
// Player page:
// - Default: load AudioClip(s) from Resources/<resourcesFolder>/, render waveform,
//   show playhead + chop markers, and hook PLAYER-local transport/buttons only.
// - Android option: scan the user's real device Music library via MediaStore and load
//   a selected track by copying the content:// URI into app cache, then loading file://... into an AudioClip.

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.Networking;
using KMusic.UI;

#if UNITY_ANDROID && !UNITY_EDITOR
using KMusic.Android;
#endif

public class KMusicPlayerUI : MonoBehaviour
{
    [SerializeField] private UIDocument doc;

    [Header("Dedicated player AudioSource (do NOT point at Helm)")]
    [Tooltip("If null, a child GameObject named 'PlayerAudioSource' will be created with an AudioSource.")]
    [SerializeField] private AudioSource audioSource;

    [Header("Resources audio folder (fallback / editor)")]
    [Tooltip("AudioClips placed under Assets/Resources/<folder>/ (ex: Assets/Resources/Tracks/*.mp3 or *.wav)")]
    [SerializeField] private string resourcesFolder = "Tracks";

    [Header("Android: Device Music Library (MediaStore)")]
    [Tooltip("If true, on Android builds we will query MediaStore.Audio (device music library) instead of Resources.")]
    [SerializeField] private bool useDeviceMusicLibraryOnAndroid = true;

    [Tooltip("Max number of MediaStore tracks to fetch (0 = no limit).")]
    [SerializeField] private int deviceTrackLimit = 250;

    private VisualElement _root;

    // Player page UI (DEDICATED)
    private Button _btnPlay, _btnStop, _btnPrev, _btnNext, _btnLoad, _btnChop, _btnApply;
    private Label _timeLabel, _durLabel, _trackNameLabel;
    private WaveformView _wave;

    private bool _chopArmed = false;

    // CHOP extra UI
    private Button _btnAuto4, _btnAuto8, _btnAuto16;
    private Button _btnAddMode, _btnDelMode, _btnSnap, _btnReset;

    // CHOP state
    private bool _markerAddMode = true;  // true = ADD (place markers), false = DEL (delete nearest)
    private bool _snapEnabled = true;
    private int _snapDiv = 16;           // snap grid division (4/8/16)

    // Resources clips
    private AudioClip[] _clips = Array.Empty<AudioClip>();
    private int _clipIndex = -1;

    // Current playing clip (either from Resources, or dynamically loaded from device)
    private AudioClip _clip;

#if UNITY_ANDROID && !UNITY_EDITOR
    private TrackInfo[] _deviceTracks = Array.Empty<TrackInfo>();
    private int _deviceTrackIndex = -1;
    private Coroutine _loadDeviceCoroutine;
    private bool _deviceReady = false;
#endif

    // Store ONLY user chop markers (max 16). 0 and 1 are implied boundaries when applying.
    private readonly List<float> _chops01 = new();

    private void Awake()
    {
        if (!doc) doc = GetComponent<UIDocument>();

        // ✅ IMPORTANT: ensure the Player uses its own AudioSource (not Helm’s).
        EnsureDedicatedPlayerAudioSource();
    }

    private void EnsureDedicatedPlayerAudioSource()
    {
        if (audioSource != null) return;

        // If the same GO has an AudioSource, it might be Helm’s. Don’t risk it.
        // Create a dedicated child AudioSource for tracks.
        var child = transform.Find("PlayerAudioSource");
        if (child == null)
        {
            var go = new GameObject("PlayerAudioSource");
            go.transform.SetParent(transform, false);
            child = go.transform;
        }

        audioSource = child.GetComponent<AudioSource>();
        if (audioSource == null) audioSource = child.gameObject.AddComponent<AudioSource>();

        // Safe defaults
        audioSource.playOnAwake = false;
        audioSource.loop = false;
        audioSource.spatialBlend = 0f; // 2D
        audioSource.volume = 1f;

        Debug.Log("[PlayerUI] Using dedicated AudioSource: PlayerAudioSource");
    }

    private void OnEnable()
    {
        EnsureDedicatedPlayerAudioSource();

        _root = doc != null ? doc.rootVisualElement : null;
        if (_root == null) return;

        // --- PLAYER PAGE ---
        var page = _root.Q<VisualElement>("PagePlayer");
        if (page == null) return;

        // Dedicated track controls
        _btnPlay = page.Q<Button>("TrackPlay");
        _btnStop = page.Q<Button>("TrackStop");
        _btnPrev = page.Q<Button>("TrackPrev");
        _btnNext = page.Q<Button>("TrackNext");
        _btnLoad = page.Q<Button>("TrackLoad");

        _btnChop  = page.Q<Button>("ChopToggle");
        _btnApply = page.Q<Button>("ChopApply");

        // CHOP extra buttons
        _btnAuto4  = page.Q<Button>("Auto4");
        _btnAuto8  = page.Q<Button>("Auto8");
        _btnAuto16 = page.Q<Button>("Auto16");

        _btnAddMode = page.Q<Button>("MarkerAddMode");
        _btnDelMode = page.Q<Button>("MarkerDelMode");
        _btnSnap    = page.Q<Button>("SnapToggle");
        _btnReset   = page.Q<Button>("ResetMarkers");

        _timeLabel = page.Q<Label>("TimeLabel");
        _durLabel  = page.Q<Label>("DurLabel");
        _trackNameLabel = page.Q<Label>("TrackName");
        UpdateTrackTitle("PLAYERUI ONENABLE HIT");

        Debug.Log($"[PlayerUI] page={(page!=null)} playBtn={(_btnPlay!=null)} stopBtn={(_btnStop!=null)} prevBtn={(_btnPrev!=null)} nextBtn={(_btnNext!=null)} loadBtn={(_btnLoad!=null)}");

        // --- Waveform host ---
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

        // Wire dedicated buttons
        if (_btnPlay != null) _btnPlay.clicked += OnPlayClicked;
        if (_btnStop != null) _btnStop.clicked += OnStopClicked;
        if (_btnPrev != null) _btnPrev.clicked += CyclePrevTrack;
        if (_btnNext != null) _btnNext.clicked += CycleNextTrack;
        if (_btnLoad != null) _btnLoad.clicked += CycleNextTrack; // LOAD cycles to next

        // CHOP main
        if (_btnChop != null)  _btnChop.clicked += ToggleChop;
        if (_btnApply != null) _btnApply.clicked += ApplyChops;

        // CHOP auto divisions
        if (_btnAuto4 != null)  _btnAuto4.clicked += () => AutoChop(4);
        if (_btnAuto8 != null)  _btnAuto8.clicked += () => AutoChop(8);
        if (_btnAuto16 != null) _btnAuto16.clicked += () => AutoChop(16);

        // CHOP modes + snap/reset
        if (_btnAddMode != null) _btnAddMode.clicked += () =>
        {
            SetMarkerMode(true);
            DropChopMarkerAtPlayhead();   // ✅ immediate action on mobile
        };

        if (_btnDelMode != null) _btnDelMode.clicked += () =>
        {
            SetMarkerMode(false);
            DeleteNearestMarkerToPlayhead(); // ✅ immediate action on mobile
        };
        if (_btnSnap != null)    _btnSnap.clicked += () => ToggleSnap();
        if (_btnReset != null)   _btnReset.clicked += ResetAllMarkers;

        // Init CHOP UI state
        SetMarkerMode(true);
        SetSnapState(_snapEnabled);

        // Keyboard: Enter applies ADD/DEL while CHOP is armed (editor/desktop + hardware keyboards)
        page.RegisterCallback<KeyDownEvent>(OnKeyDown);

        // Update playhead ~30fps (schedule from root so it keeps ticking)
        _root.schedule.Execute(UpdatePlayheadUI).Every(33);

        // Load clips + auto-load first
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

                // ok now means: permission granted + query attempted (even if 0 tracks)
                _deviceReady = ok;

                Debug.Log($"[PlayerUI] Device permission/query ok={ok} tracks={_deviceTracks.Length} status={AndroidMusicLibrary.DebugLastStatus}");

                if (_deviceTracks.Length > 0)
                {
                    LoadDeviceTrackByIndex(0);
                }
                else
                {
                    // ✅ SHOW THE REAL REASON ON SCREEN (no logcat needed)
                    UpdateTrackTitle(
                        "ANDROID MUSIC: 0 tracks\n" +
                        AndroidMusicLibrary.DebugLastStatus + "\n" +
                        AndroidMusicLibrary.DebugLastJsonHead
                    );

                    // keep fallback so app still works (but don't overwrite the debug title)
                    RefreshClipList();
                    // user can press Next/Prev to pick Resources manually if needed.
                }
            }, deviceTrackLimit);

            return;
        }
#endif

        // Default (Editor/Desktop/iOS/etc): Resources based
        RefreshClipList();
        if (_clips.Length > 0 && _clip == null)
            LoadClipByIndex(0);
        else if (_clips.Length == 0)
            Debug.LogWarning($"KMusicPlayerUI: No AudioClips found in Resources/{resourcesFolder}/");
    }

    private void OnDisable()
    {
        if (_btnPlay != null) _btnPlay.clicked -= OnPlayClicked;
        if (_btnStop != null) _btnStop.clicked -= OnStopClicked;
        if (_btnPrev != null) _btnPrev.clicked -= CyclePrevTrack;
        if (_btnNext != null) _btnNext.clicked -= CycleNextTrack;
        if (_btnLoad != null) _btnLoad.clicked -= CycleNextTrack;

        if (_btnChop != null)  _btnChop.clicked -= ToggleChop;
        if (_btnApply != null) _btnApply.clicked -= ApplyChops;

        if (_btnAuto4 != null)  _btnAuto4.clicked -= () => AutoChop(4);
        if (_btnAuto8 != null)  _btnAuto8.clicked -= () => AutoChop(8);
        if (_btnAuto16 != null) _btnAuto16.clicked -= () => AutoChop(16);

        if (_btnAddMode != null) _btnAddMode.clicked -= () => SetMarkerMode(true);
        if (_btnDelMode != null) _btnDelMode.clicked -= () => SetMarkerMode(false);
        if (_btnSnap != null)    _btnSnap.clicked -= () => ToggleSnap();
        if (_btnReset != null)   _btnReset.clicked -= ResetAllMarkers;

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

        Debug.Log($"[PlayerUI] RefreshClipList found {_clips.Length} clips in Resources/{resourcesFolder}");

        if (_clips.Length > 0)
        {
            var names = new string[_clips.Length];
            for (int i = 0; i < _clips.Length; i++) names[i] = _clips[i] != null ? _clips[i].name : "null";
            Debug.Log($"KMusicPlayerUI: Found {_clips.Length} clip(s) in Resources/{resourcesFolder}: {string.Join(", ", names)}");
        }
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

        if (_clip == null)
        {
            Debug.LogError("KMusicPlayerUI: Selected clip is null.");
            return;
        }

        // ✅ Force the dedicated player AudioSource to use THIS track clip.
        audioSource.Stop();
        audioSource.clip = _clip;
        audioSource.time = 0f;

        _wave.SetClip(_clip);
        _wave.SetPlayhead01(0f);
        UpdateTimeLabels(0f, _clip.length);
        UpdateTrackTitle(_clip.name);

        Debug.Log($"KMusicPlayerUI: Loaded {_clip.name} len={_clip.length:0.00}s (audioSource.clip={audioSource.clip.name})");
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

        // Stop previous load if still running
        if (_loadDeviceCoroutine != null)
        {
            StopCoroutine(_loadDeviceCoroutine);
            _loadDeviceCoroutine = null;
        }

        // Stop playback while loading
        if (audioSource != null) audioSource.Stop();
        if (_wave != null)
        {
            _wave.SetClip(null);
            _wave.SetPlayhead01(0f);
            _wave.SetMarkers01(_chops01);
        }
        UpdateTimeLabels(0f, (t.durationMs > 0) ? (t.durationMs / 1000f) : 0f);

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

        if (loaded == null)
        {
            Debug.LogWarning("[PlayerUI] Failed to load device track: " + (t != null ? t.uri : "null"));
            yield break;
        }

        _clip = loaded;

        // ✅ Force the dedicated player AudioSource to use THIS track clip.
        audioSource.Stop();
        audioSource.clip = _clip;
        audioSource.time = 0f;

        _wave.SetClip(_clip);
        _wave.SetPlayhead01(0f);
        UpdateTimeLabels(0f, _clip.length);

        Debug.Log($"[PlayerUI] Loaded device clip len={_clip.length:0.00}s uri={t.uri}");
    }
#endif

    private void UpdateTrackTitle(string title)
    {
        if (_trackNameLabel == null) return;
        _trackNameLabel.text = string.IsNullOrEmpty(title) ? "TRACK" : title;
    }

    private void EnsureClipLoaded()
    {
        if (_clip != null && audioSource != null && audioSource.clip == _clip) return;

#if UNITY_ANDROID && !UNITY_EDITOR
        if (useDeviceMusicLibraryOnAndroid && _deviceReady)
        {
            if (_deviceTracks != null && _deviceTracks.Length > 0)
            {
                int idx = (_deviceTrackIndex < 0) ? 0 : Mathf.Clamp(_deviceTrackIndex, 0, _deviceTracks.Length - 1);
                LoadDeviceTrackByIndex(idx);
            }
            return;
        }
#endif

        if (_clip == null || _clips == null || _clips.Length == 0)
            RefreshClipList();

        if (_clips.Length > 0)
        {
            int idx = (_clipIndex < 0) ? 0 : Mathf.Clamp(_clipIndex, 0, _clips.Length - 1);
            LoadClipByIndex(idx);
        }
    }

    private void OnPlayClicked()
    {
        EnsureClipLoaded();
        if (audioSource == null || audioSource.clip == null) return;

        Debug.Log($"[PlayerUI] PLAY clicked. trackClip={(_clip!=null?_clip.name:"null")} audioSource.clip={audioSource.clip.name} isPlaying={audioSource.isPlaying} time={audioSource.time:0.000}");

        // If at end, restart
        if (audioSource.time >= audioSource.clip.length - 0.01f)
            audioSource.time = 0f;

        audioSource.Play();
    }

    private void OnStopClicked()
    {
        if (audioSource == null) return;
        audioSource.Stop();
        audioSource.time = 0f;
        UpdatePlayheadUI();
    }

    private void OnKeyDown(KeyDownEvent evt)
    {
        if (!_chopArmed) return;

        if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
        {
            if (_markerAddMode) DropChopMarkerAtPlayhead();
            else DeleteNearestMarkerToPlayhead();

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

        // When arming CHOP, default to ADD mode (sampler-friendly)
        if (_chopArmed) SetMarkerMode(true);

        // ✅ MOBILE: tapping CHOP should DO something right away
        // Drop a marker at current playhead when turning ON
        if (_chopArmed)
            DropChopMarkerAtPlayhead();
    }

    private void DropChopMarkerAtPlayhead()
    {
        if (_clip == null || _clip.length <= 0f) return;
        if (_chops01.Count >= 16) return;

        float t01 = Mathf.Clamp01(audioSource.time / _clip.length);
        if (_snapEnabled) t01 = Snap01(t01, _snapDiv);

        for (int i = 0; i < _chops01.Count; i++)
            if (Mathf.Abs(_chops01[i] - t01) < 0.005f) return;

        _chops01.Add(t01);
        _chops01.Sort();
        SyncMarkersToWave();

        UpdateTrackTitle($"MARKER + {t01:0.000}");
    }

    private void ApplyChops()
    {
        if (_clip == null || _clip.length <= 0f || audioSource == null || audioSource.clip != _clip)
            return;

        // Boundaries: 0 + markers + 1
        var boundaries = new List<float>(_chops01.Count + 2) { 0f };
        boundaries.AddRange(_chops01);
        boundaries.Add(1f);
        boundaries.Sort();

        float t01 = Mathf.Clamp01(audioSource.time / _clip.length);

        // Find slice containing playhead
        int slice = 0;
        for (int i = 0; i < boundaries.Count - 1; i++)
        {
            if (t01 >= boundaries[i] && t01 < boundaries[i + 1])
            {
                slice = i;
                break;
            }
        }

        float start01 = boundaries[slice];
        float end01 = boundaries[Mathf.Min(slice + 1, boundaries.Count - 1)];
        float startSec = start01 * _clip.length;

        audioSource.time = startSec;
        if (!audioSource.isPlaying) audioSource.Play();

        UpdateTrackTitle($"SLICE {slice + 1}  {start01:0.000}->{end01:0.000}");
    }

    private void SyncMarkersToWave()
    {
        _wave.SetMarkers01(_chops01);
    }

    private void UpdatePlayheadUI()
    {
        if (_clip == null || _clip.length <= 0f || audioSource == null || audioSource.clip != _clip)
        {
            _wave?.SetPlayhead01(0f);
            UpdateTimeLabels(0f, _clip != null ? _clip.length : 0f);
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

    // ----------------------------
    // CHOP helpers
    // ----------------------------

    private void AutoChop(int slices)
    {
        if (_clip == null || _clip.length <= 0f) return;
        slices = Mathf.Clamp(slices, 2, 64);

        _snapDiv = slices;
        _chops01.Clear();

        // Add internal boundaries only (exclude 0 and 1)
        int internalCount = Mathf.Min(16, slices - 1); // respect 16 marker max
        for (int i = 1; i <= internalCount; i++)
        {
            // If slices is large, internalCount caps, so we space within slices anyway
            float t01 = i / (float)slices;
            if (_snapEnabled) t01 = Snap01(t01, _snapDiv);
            _chops01.Add(t01);
        }

        _chops01.Sort();
        SyncMarkersToWave();

        // Arm chop when auto-chopping (feels right)
        _chopArmed = true;
        if (_btnChop != null) _btnChop.AddToClassList("km-pillbtn--active");
        SetMarkerMode(true);

        UpdateTrackTitle($"AUTO {slices}");
    }

    private void ResetAllMarkers()
    {
        _chops01.Clear();
        SyncMarkersToWave();
        UpdateTrackTitle("RESET");
    }

    private void SetMarkerMode(bool addMode)
    {
        _markerAddMode = addMode;

        if (_btnAddMode != null)
        {
            if (_markerAddMode) _btnAddMode.AddToClassList("km-pillbtn--active");
            else _btnAddMode.RemoveFromClassList("km-pillbtn--active");
        }

        if (_btnDelMode != null)
        {
            if (!_markerAddMode) _btnDelMode.AddToClassList("km-pillbtn--active");
            else _btnDelMode.RemoveFromClassList("km-pillbtn--active");
        }

        UpdateTrackTitle(_markerAddMode ? "MODE: ADD" : "MODE: DEL");
    }

    private void ToggleSnap()
    {
        _snapEnabled = !_snapEnabled;
        SetSnapState(_snapEnabled);
        UpdateTrackTitle(_snapEnabled ? $"SNAP ON ({_snapDiv})" : "SNAP OFF");
    }

    private void SetSnapState(bool on)
    {
        if (_btnSnap != null)
        {
            if (on) _btnSnap.AddToClassList("km-pillbtn--active");
            else _btnSnap.RemoveFromClassList("km-pillbtn--active");
        }
    }

    private void DeleteNearestMarkerToPlayhead()
    {
        if (_clip == null || _clip.length <= 0f) return;
        if (_chops01.Count == 0) return;

        float t01 = Mathf.Clamp01(audioSource.time / _clip.length);

        int best = -1;
        float bestDist = float.MaxValue;
        for (int i = 0; i < _chops01.Count; i++)
        {
            float d = Mathf.Abs(_chops01[i] - t01);
            if (d < bestDist)
            {
                bestDist = d;
                best = i;
            }
        }

        // Require playhead reasonably near a marker to delete (prevents accidental deletes)
        if (best >= 0 && bestDist <= 0.06f)
        {
            float v = _chops01[best];
            _chops01.RemoveAt(best);
            SyncMarkersToWave();
            UpdateTrackTitle($"MARKER - {v:0.000}");
        }
        else
        {
            UpdateTrackTitle("NO MARKER NEAR");
        }
    }

    private static float Snap01(float t01, int div)
    {
        div = Mathf.Clamp(div, 2, 64);
        float step = 1f / div;
        int k = Mathf.RoundToInt(t01 / step);
        return Mathf.Clamp01(k * step);
    }
}