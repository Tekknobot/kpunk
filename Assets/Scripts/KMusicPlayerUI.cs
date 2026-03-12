// Assets/Scripts/KMusicPlayerUI.cs
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using KMusic.UI;
using KMusic;
using KMusic.Core;

#if UNITY_ANDROID && !UNITY_EDITOR
using KMusic.Android;
using KMusic.Core;
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

    private VisualElement _trackBrowserOverlay;
    private TextField _trackBrowserSearch;
    private ScrollView _trackBrowserList;
    private Label _trackBrowserCountLabel;

    private bool _chopArmed = false;

    private Button _btnAuto4, _btnAuto8, _btnAuto16;
    private Button _btnAddMode, _btnDelMode, _btnSnap, _btnReset;
    private Button _btnMarkerPrev, _btnMarkerNext, _btnNudgeNeg10, _btnNudgeNeg01, _btnNudgePos01, _btnNudgePos10;
    private readonly List<Button> _chopPickerButtons = new();
    private Label _markerNavLabel;

    private bool _markerAddMode = true;
    private bool _snapEnabled = true;
    private int _snapDiv = 16;

    private const float MinMarkerGapSeconds = 0.05f;
    private const float MarkerAuditionThrottle = 0.075f;
    private float _lastMarkerAuditionRealtime = -999f;
    private Coroutine _markerPreviewCoroutine;
    private int _selectedMarkerIndex = -1;

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

        ProjectPrefs.SetString(PrefKey_LastTrack, id);
        ProjectPrefs.Save();
    }

    private static string LoadLastTrackId()
    {
        return ProjectPrefs.GetString(PrefKey_LastTrack, "");
    }

    // Project save/load hooks
    public string GetLastTrackId() => LoadLastTrackId();

    public void SetLastTrackId(string id)
    {
        if (string.IsNullOrEmpty(id))
            return;

        SaveLastTrackId(id);

        try
        {
            if (audioSource == null)
            {
                Debug.Log("[Player] SetLastTrackId deferred: audioSource not ready");
                return;
            }

            LoadTrackById(id);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[Player] SetLastTrackId load failed: " + e.Message);
        }
    }

    public void RefreshAppliedMarkersFromState()
    {
        RestoreMarkersFromAppliedState();
    }

    private void LoadTrackById(string id)
    {
        if (string.IsNullOrEmpty(id))
            return;

        if (audioSource == null)
        {
            Debug.Log("[Player] LoadTrackById aborted: audioSource not ready");
            return;
        }

    #if UNITY_ANDROID && !UNITY_EDITOR
        if (id.StartsWith("uri:"))
        {
            string want = id.Substring(4);
            if (_deviceTracks != null && _deviceTracks.Length > 0)
            {
                for (int i = 0; i < _deviceTracks.Length; i++)
                {
                    if (_deviceTracks[i] != null && _deviceTracks[i].uri == want)
                    {
                        LoadDeviceTrackByIndex(i);
                        return;
                    }
                }
            }
            return;
        }
    #endif

        if (id.StartsWith("res:"))
        {
            string wantPath = id.Substring(4);
            RefreshClipList();

            if (_clips != null && _clips.Length > 0)
            {
                string wantName = wantPath.Contains("/") 
                    ? wantPath.Substring(wantPath.LastIndexOf('/') + 1) 
                    : wantPath;

                for (int i = 0; i < _clips.Length; i++)
                {
                    if (_clips[i] != null &&
                        (_clips[i].name == wantName ||
                        ($"{resourcesFolder}/" + _clips[i].name) == wantPath))
                    {
                        LoadClipByIndex(i);
                        return;
                    }
                }
            }
        }
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
        _btnMarkerPrev = page.Q<Button>("MarkerPrev");
        _btnMarkerNext = page.Q<Button>("MarkerNext");
        _btnNudgeNeg10 = page.Q<Button>("MarkerNudgeNeg10");
        _btnNudgeNeg01 = page.Q<Button>("MarkerNudgeNeg01");
        _btnNudgePos01 = page.Q<Button>("MarkerNudgePos01");
        _btnNudgePos10 = page.Q<Button>("MarkerNudgePos10");
        _markerNavLabel = page.Q<Label>("MarkerNavLabel");
        BindPlayerChopPicker(page);

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
        _wave.MarkerDrag01 -= OnWaveMarkerDrag01;
        _wave.MarkerDrag01 += OnWaveMarkerDrag01;
        _wave.MarkersDraggable = _chopArmed;

        // Wire buttons
        if (_btnPlay != null) _btnPlay.clicked += OnPlayClicked;
        if (_btnStop != null) _btnStop.clicked += OnStopClicked;
        if (_btnPrev != null) _btnPrev.clicked += CyclePrevTrack;
        if (_btnNext != null) _btnNext.clicked += CycleNextTrack;
        if (_btnLoad != null) _btnLoad.clicked += OpenTrackBrowser;

        if (_btnChop != null) _btnChop.clicked += ToggleChop;
        if (_btnApply != null) _btnApply.clicked += ApplyChops;
        if (_btnSend != null) _btnSend.clicked += SendChops;

        if (_btnAuto4 != null) _btnAuto4.clicked += () => AutoChop(4);
        if (_btnAuto8 != null) _btnAuto8.clicked += () => AutoChop(8);
        if (_btnAuto16 != null) _btnAuto16.clicked += () => AutoChop(16);

        // Fire on press-down so it works immediately on mobile too.
        WirePointerDownButton(_btnAddMode, OnAddMarkerButton, "km-add-btn--pointerdown");

        // Del button: set delete mode (and also delete nearest at current timestamp, consistent with mobile)
        if (_btnDelMode != null) _btnDelMode.clicked += OnDeleteMarkerButton;

        if (_btnSnap != null) _btnSnap.clicked += () => ToggleSnap();
        if (_btnReset != null) _btnReset.clicked += ResetAllMarkers;
        if (_btnMarkerPrev != null) _btnMarkerPrev.clicked += SelectPreviousMarker;
        if (_btnMarkerNext != null) _btnMarkerNext.clicked += SelectNextMarker;
        if (_btnNudgeNeg10 != null) _btnNudgeNeg10.clicked += () => NudgeSelectedMarkerSeconds(-0.10f);
        if (_btnNudgeNeg01 != null) _btnNudgeNeg01.clicked += () => NudgeSelectedMarkerSeconds(-0.01f);
        if (_btnNudgePos01 != null) _btnNudgePos01.clicked += () => NudgeSelectedMarkerSeconds(0.01f);
        if (_btnNudgePos10 != null) _btnNudgePos10.clicked += () => NudgeSelectedMarkerSeconds(0.10f);

        SetMarkerMode(true);
        SetSnapState(_snapEnabled);
        UpdateMarkerNavigatorUI();

        BuildTrackBrowserOverlay(page);
        RefreshTrackBrowserList();

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

                RefreshTrackBrowserList();
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

        RefreshTrackBrowserList();
    }

    private void RestoreMarkersFromAppliedState()
    {
        _chops01.Clear();

        if (_clip == null || string.IsNullOrEmpty(_clipResourcesPath))
        {
            SyncMarkersToWave();
            return;
        }

        if (!KMusicChopState.TryLoadApplied(out string appliedPath, out var sliceStart01, out var sliceEnd01))
        {
            SyncMarkersToWave();
            return;
        }

        if (!string.Equals(appliedPath, _clipResourcesPath, StringComparison.Ordinal))
        {
            SyncMarkersToWave();
            return;
        }

        bool hasStartMarkers = false;
        if (sliceStart01 != null)
        {
            for (int i = 0; i < 16 && i < sliceStart01.Length; i++)
            {
                float s = sliceStart01[i];
                float e = (sliceEnd01 != null && i < sliceEnd01.Length) ? sliceEnd01[i] : 0f;
                if (s > 0.0001f && e > s)
                {
                    hasStartMarkers = true;
                    break;
                }
            }
        }

        if (hasStartMarkers)
        {
            for (int i = 0; i < 16 && i < sliceStart01.Length; i++)
            {
                float s = sliceStart01[i];
                float e = (sliceEnd01 != null && i < sliceEnd01.Length) ? sliceEnd01[i] : 0f;
                if (s <= 0.0001f || s >= 0.9999f)
                    continue;
                if (e <= s)
                    continue;
                if (_chops01.Count > 0 && Mathf.Abs(_chops01[_chops01.Count - 1] - s) <= 0.0001f)
                    continue;
                _chops01.Add(Mathf.Clamp01(s));
            }

            if (sliceEnd01 != null && sliceEnd01.Length >= 16)
            {
                float finalStart = sliceStart01[15];
                float finalEnd = sliceEnd01[15];
                if (finalEnd > finalStart + 0.0001f && finalEnd < 0.9999f)
                {
                    if (_chops01.Count == 0 || Mathf.Abs(_chops01[_chops01.Count - 1] - finalEnd) > 0.0001f)
                        _chops01.Add(Mathf.Clamp01(finalEnd));
                }
            }
        }
        else if (sliceEnd01 != null)
        {
            // Legacy fallback for older save format where markers were restored from slice ends.
            for (int i = 0; i < 16 && i < sliceEnd01.Length; i++)
            {
                float s = (sliceStart01 != null && i < sliceStart01.Length) ? sliceStart01[i] : 0f;
                float e = sliceEnd01[i];
                if (e <= 0f || e >= 0.9999f)
                    continue;
                if (e <= s)
                    continue;
                if (_chops01.Count > 0 && Mathf.Abs(_chops01[_chops01.Count - 1] - e) <= 0.0001f)
                    continue;
                _chops01.Add(Mathf.Clamp01(e));
            }
        }

        _chops01.Sort();
        ClampSelectedMarkerIndex();
        SyncMarkersToWave();
        UpdateMarkerNavigatorUI();
    }

    private void OnDisable()
    {
        if (_wave != null)
        {
            _wave.UserPointer01 -= OnWavePointer01;
            _wave.MarkerDrag01 -= OnWaveMarkerDrag01;
        }

        if (_markerPreviewCoroutine != null)
        {
            StopCoroutine(_markerPreviewCoroutine);
            _markerPreviewCoroutine = null;
        }

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
        RestoreMarkersFromAppliedState();
        UpdateTimeLabels(0f, _clip.length);
        UpdateTrackTitle(_clip.name);
        RefreshTrackBrowserList();
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
        RestoreMarkersFromAppliedState();
        UpdateTimeLabels(0f, _clip.length);
        RefreshTrackBrowserList();
    }
#endif

    private sealed class TrackBrowserItem
    {
        public string Title;
        public string Subtitle;
        public string SearchText;
        public bool IsCurrent;
        public Action OnPick;
    }

    private void OpenTrackBrowser()
    {
        if (_trackBrowserOverlay == null)
            return;

        RefreshTrackBrowserList();
        _trackBrowserOverlay.style.display = DisplayStyle.Flex;
        _trackBrowserSearch?.Focus();
    }

    private void CloseTrackBrowser()
    {
        if (_trackBrowserOverlay == null)
            return;

        _trackBrowserOverlay.style.display = DisplayStyle.None;
    }

    private void BuildTrackBrowserOverlay(VisualElement page)
    {
        if (page == null || _trackBrowserOverlay != null)
            return;

        _trackBrowserOverlay = new VisualElement();
        _trackBrowserOverlay.name = "TrackBrowserOverlay";
        _trackBrowserOverlay.AddToClassList("km-project-modal");
        _trackBrowserOverlay.AddToClassList("km-track-modal");
        _trackBrowserOverlay.style.position = Position.Absolute;
        _trackBrowserOverlay.style.left = 0;
        _trackBrowserOverlay.style.top = 0;
        _trackBrowserOverlay.style.right = 0;
        _trackBrowserOverlay.style.bottom = 0;
        _trackBrowserOverlay.style.display = DisplayStyle.None;

        var card = new VisualElement();
        card.name = "TrackBrowserCard";
        card.AddToClassList("km-card");
        card.AddToClassList("km-project-card");
        card.AddToClassList("km-track-card");
        card.style.flexGrow = 1f;
        card.style.maxHeight = Length.Percent(92);

        var header = new VisualElement();
        header.AddToClassList("km-track-header");

        var title = new Label("LOAD SAMPLE");
        title.AddToClassList("km-project-title");
        title.AddToClassList("km-track-title");
        title.style.flexGrow = 1f;

        var closeBtn = new Button(CloseTrackBrowser) { text = "CLOSE" };
        closeBtn.AddToClassList("km-pillbtn");
        closeBtn.AddToClassList("km-project-toolbar-btn");
        closeBtn.AddToClassList("km-track-close");

        header.Add(title);
        header.Add(closeBtn);

        _trackBrowserSearch = new TextField();
        _trackBrowserSearch.name = "TrackSearchField";
        _trackBrowserSearch.label = "";
        _trackBrowserSearch.value = "";
        _trackBrowserSearch.AddToClassList("km-project-rename-field");
        _trackBrowserSearch.AddToClassList("km-track-search");
        _trackBrowserSearch.RegisterValueChangedCallback(_ => RefreshTrackBrowserList());

        _trackBrowserCountLabel = new Label("0 items");
        _trackBrowserCountLabel.AddToClassList("km-track-count");

        _trackBrowserList = new ScrollView();
        _trackBrowserList.name = "TrackScroll";
        _trackBrowserList.AddToClassList("km-project-scroll");
        _trackBrowserList.AddToClassList("km-track-scroll");
        _trackBrowserList.style.flexGrow = 1f;
        _trackBrowserList.style.minHeight = 220;

        card.Add(header);
        card.Add(_trackBrowserSearch);
        card.Add(_trackBrowserCountLabel);
        card.Add(_trackBrowserList);
        _trackBrowserOverlay.Add(card);
        page.Add(_trackBrowserOverlay);
    }

    private void RefreshTrackBrowserList()
    {
        if (_trackBrowserList == null)
            return;

        _trackBrowserList.Clear();

        string query = (_trackBrowserSearch?.value ?? string.Empty).Trim();
        var items = BuildTrackBrowserItems();
        int shown = 0;

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (!string.IsNullOrEmpty(query) && (item.SearchText == null || item.SearchText.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0))
                continue;

            shown++;

            var row = new Button(() =>
            {
                item.OnPick?.Invoke();
                CloseTrackBrowser();
            });
            row.AddToClassList("km-track-row");
            if (item.IsCurrent)
                row.AddToClassList("km-track-row--current");

            var title = new Label(item.IsCurrent ? "● " + item.Title : item.Title);
            title.AddToClassList("km-track-row-title");

            var subtitle = new Label(item.Subtitle ?? string.Empty);
            subtitle.AddToClassList("km-track-row-subtitle");

            row.Add(title);
            if (!string.IsNullOrEmpty(item.Subtitle))
                row.Add(subtitle);
            _trackBrowserList.Add(row);
        }

        if (_trackBrowserCountLabel != null)
            _trackBrowserCountLabel.text = shown + " / " + items.Count + " items";

        if (shown == 0)
        {
            var empty = new Label("No samples found.");
            empty.AddToClassList("km-track-empty");
            _trackBrowserList.Add(empty);
        }
    }

    private List<TrackBrowserItem> BuildTrackBrowserItems()
    {
        var items = new List<TrackBrowserItem>();

#if UNITY_ANDROID && !UNITY_EDITOR
        if (useDeviceMusicLibraryOnAndroid && _deviceReady)
        {
            if (_deviceTracks != null)
            {
                for (int i = 0; i < _deviceTracks.Length; i++)
                {
                    var track = _deviceTracks[i];
                    if (track == null)
                        continue;

                    int idx = i;
                    string title = string.IsNullOrEmpty(track.title) ? "Track " + (i + 1) : track.title;
                    string artist = string.IsNullOrEmpty(track.artist) ? "Unknown artist" : track.artist;
                    string album = string.IsNullOrEmpty(track.album) ? "" : (" • " + track.album);
                    string subtitle = artist + album;
                    items.Add(new TrackBrowserItem
                    {
                        Title = title,
                        Subtitle = subtitle,
                        SearchText = (title + " " + artist + " " + track.album + " " + track.uri),
                        IsCurrent = idx == _deviceTrackIndex,
                        OnPick = () => LoadDeviceTrackByIndex(idx)
                    });
                }
            }
            return items;
        }
#endif

        RefreshClipList();
        if (_clips != null)
        {
            for (int i = 0; i < _clips.Length; i++)
            {
                var clip = _clips[i];
                if (clip == null)
                    continue;

                int idx = i;
                string title = clip.name;
                string subtitle = resourcesFolder + "/" + clip.name;
                items.Add(new TrackBrowserItem
                {
                    Title = title,
                    Subtitle = subtitle,
                    SearchText = title + " " + subtitle,
                    IsCurrent = idx == _clipIndex,
                    OnPick = () => LoadClipByIndex(idx)
                });
            }
        }

        return items;
    }

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

        if (_wave != null) _wave.MarkersDraggable = _chopArmed;
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

    private void OnWaveMarkerDrag01(int markerIndex, float t01, bool isDrag)
    {
        if (_clip == null || _clip.length <= 0f || audioSource == null) return;
        if (!_chopArmed) return;
        if (markerIndex < 0 || markerIndex >= _chops01.Count) return;

        float newT01 = GetClampedMarkerPosition01(markerIndex, t01);
        if (_snapEnabled) newT01 = GetClampedMarkerPosition01(markerIndex, Snap01(newT01, _snapDiv));

        if (Mathf.Abs(_chops01[markerIndex] - newT01) <= 0.0001f && isDrag)
            return;

        _chops01[markerIndex] = newT01;
        _chops01.Sort();
        _selectedMarkerIndex = FindNearestMarkerIndex(newT01);
        ClampSelectedMarkerIndex();
        SyncMarkersToWave();
        UpdateMarkerNavigatorUI();
        _scrub01 = newT01;
        _wave?.SetPlayhead01(newT01);
        PersistMarkersIfPossible();

        if (ShouldAuditionMarkerMove(isDrag))
            AuditionMarkerBoundary(_selectedMarkerIndex);

        if (!isDrag)
            UpdateTrackTitle($"MARKER MOVE {newT01:0.000}");
    }

    private float GetClampedMarkerPosition01(int markerIndex, float t01)
    {
        if (_clip == null || _clip.length <= 0f) return Mathf.Clamp01(t01);

        float minGap01 = MinMarkerGapSeconds / _clip.length;
        float min = (markerIndex > 0) ? (_chops01[markerIndex - 1] + minGap01) : minGap01;
        float max = (markerIndex < _chops01.Count - 1) ? (_chops01[markerIndex + 1] - minGap01) : (1f - minGap01);
        if (max < min)
        {
            float mid = (min + max) * 0.5f;
            min = mid;
            max = mid;
        }

        return Mathf.Clamp(t01, min, max);
    }

    private bool ShouldAuditionMarkerMove(bool isDrag)
    {
        if (!isDrag)
            return true;

        float now = Time.realtimeSinceStartup;
        if (now - _lastMarkerAuditionRealtime < MarkerAuditionThrottle)
            return false;

        _lastMarkerAuditionRealtime = now;
        return true;
    }

    private void AuditionMarkerBoundary(int markerIndex)
    {
        if (_clip == null || _clip.length <= 0f || audioSource == null) return;
        if (markerIndex < 0 || markerIndex >= _chops01.Count) return;

        float leftStart01 = (markerIndex > 0) ? _chops01[markerIndex - 1] : 0f;
        float leftEnd01 = _chops01[markerIndex];
        float rightStart01 = _chops01[markerIndex];
        float rightEnd01 = (markerIndex < _chops01.Count - 1) ? _chops01[markerIndex + 1] : 1f;

        float start01;
        float end01;

        // Start markers should always audition the chop that starts at that marker.
        // Only the optional closing marker (index 16 / marker 17) should audition left.
        if (markerIndex >= 16)
        {
            start01 = leftStart01;
            end01 = leftEnd01;
        }
        else
        {
            start01 = rightStart01;
            end01 = rightEnd01;
        }

        if (end01 <= start01)
            return;

        float startTime = Mathf.Clamp(start01 * _clip.length, 0f, Mathf.Max(0f, _clip.length - 0.001f));
        float dur = Mathf.Clamp((end01 - start01) * _clip.length, 0.03f, _clip.length);

        if (_markerPreviewCoroutine != null)
            StopCoroutine(_markerPreviewCoroutine);
        _markerPreviewCoroutine = StartCoroutine(CoPreviewSlice(startTime, dur));
    }

    private IEnumerator CoPreviewSlice(float startTime, float duration)
    {
        if (_clip == null || audioSource == null)
            yield break;

        audioSource.Stop();
        audioSource.clip = _clip;
        audioSource.time = Mathf.Clamp(startTime, 0f, Mathf.Max(0f, _clip.length - 0.001f));
        audioSource.Play();

        yield return new WaitForSecondsRealtime(duration);

        if (audioSource != null && audioSource.clip == _clip)
            audioSource.Stop();

        _markerPreviewCoroutine = null;
    }

    private void PersistMarkersIfPossible()
    {
        if (_clip == null || _clip.length <= 0f)
            return;

        if (!string.IsNullOrEmpty(_clipResourcesPath))
            KMusicChopState.SaveApplied(_clipResourcesPath, _chops01);
        else
            KMusicChopState.SaveAppliedFromClip(_clip, resourcesPathOrNull: null, markerPositions01: _chops01);
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
        if (_chops01.Count >= 17) return;

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
        _selectedMarkerIndex = _chops01.FindIndex(v => Mathf.Abs(v - t01) <= 0.0001f);
        ClampSelectedMarkerIndex();
        SyncMarkersToWave();
        UpdateMarkerNavigatorUI();
        PersistMarkersIfPossible();
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
            if (_selectedMarkerIndex == best) _selectedMarkerIndex = Mathf.Clamp(best - 1, 0, _chops01.Count - 1);
            else if (_selectedMarkerIndex > best) _selectedMarkerIndex--;
            ClampSelectedMarkerIndex();
            SyncMarkersToWave();
            UpdateMarkerNavigatorUI();
            PersistMarkersIfPossible();
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

        UpdateTrackTitle($"APPLIED {Mathf.Min(16, _chops01.Count)} SLICES");
    }

    private void SendChops()
    {
        if (_clip == null || _clip.length <= 0f) return;

        if (!string.IsNullOrEmpty(_clipResourcesPath))
            KMusicChopState.SaveApplied(_clipResourcesPath, _chops01);
        else
            KMusicChopState.SaveAppliedFromClip(_clip, resourcesPathOrNull: null, markerPositions01: _chops01);

        UpdateTrackTitle($"SENT {Mathf.Min(16, _chops01.Count)} CHOPS");
    }


    private int FindNearestMarkerIndex(float t01)
    {
        if (_chops01.Count == 0) return -1;
        int best = 0;
        float bestDist = Mathf.Abs(_chops01[0] - t01);
        for (int i = 1; i < _chops01.Count; i++)
        {
            float d = Mathf.Abs(_chops01[i] - t01);
            if (d < bestDist)
            {
                best = i;
                bestDist = d;
            }
        }
        return best;
    }

    private void ClampSelectedMarkerIndex()
    {
        if (_chops01.Count == 0)
        {
            _selectedMarkerIndex = -1;
            return;
        }

        _selectedMarkerIndex = Mathf.Clamp(_selectedMarkerIndex, 0, _chops01.Count - 1);
    }

    private void BindPlayerChopPicker(VisualElement page)
    {
        _chopPickerButtons.Clear();
        if (page == null) return;

        for (int i = 1; i <= 16; i++)
        {
            int chopNumber = i;
            var btn = page.Q<Button>($"Chop{chopNumber:00}");
            if (btn == null) continue;

            _chopPickerButtons.Add(btn);
            WirePointerDownButton(btn, () => SelectMarkerFromChopPicker(chopNumber - 1), $"km-chop-picker-{chopNumber:00}--pointerdown");
        }

        RefreshChopPickerUI();
    }


    private void WirePointerDownButton(Button btn, Action action, string wiredClass)
    {
        if (btn == null || action == null) return;
        if (btn.ClassListContains(wiredClass)) return;

        btn.AddToClassList(wiredClass);
        btn.RegisterCallback<PointerDownEvent>(_ => action(), TrickleDown.TrickleDown);
    }

    private void SelectMarkerFromChopPicker(int markerIndex)
    {
        if (_clip == null || _clip.length <= 0f) return;
        if (markerIndex < 0 || markerIndex >= _chops01.Count) return;
        if (markerIndex >= 16) return;

        _selectedMarkerIndex = markerIndex;
        ClampSelectedMarkerIndex();

        float t01 = _chops01[_selectedMarkerIndex];
        _scrub01 = t01;
        _wave?.SetPlayhead01(t01);

        UpdateMarkerNavigatorUI();
        RefreshChopPickerUI();
        AuditionMarkerBoundary(_selectedMarkerIndex);
    }

    private void RefreshChopPickerUI()
    {
        for (int i = 0; i < _chopPickerButtons.Count; i++)
        {
            var btn = _chopPickerButtons[i];
            if (btn == null) continue;

            bool hasMarker = i < _chops01.Count && i < 16;
            btn.SetEnabled(hasMarker);
            btn.EnableInClassList("km-chop-btn--wired", hasMarker);
            btn.EnableInClassList("km-chop-btn--active", hasMarker && i == _selectedMarkerIndex);
        }
    }

    private void UpdateMarkerNavigatorUI()
    {
        ClampSelectedMarkerIndex();

        if (_markerNavLabel != null)
        {
            if (_chops01.Count == 0 || _selectedMarkerIndex < 0)
            {
                _markerNavLabel.text = "NO MARKER";
            }
            else
            {
                float t = _chops01[_selectedMarkerIndex] * ((_clip != null) ? _clip.length : 0f);
                string role = (_selectedMarkerIndex == 16) ? "END 16" : $"START {_selectedMarkerIndex + 1:00}";
                _markerNavLabel.text = $"M{_selectedMarkerIndex + 1:00}  {role}  {t:0.00}s";
            }
        }

        RefreshChopPickerUI();
    }

    private void SelectPreviousMarker()
    {
        if (_chops01.Count == 0) return;
        ClampSelectedMarkerIndex();
        _selectedMarkerIndex = (_selectedMarkerIndex <= 0) ? (_chops01.Count - 1) : (_selectedMarkerIndex - 1);
        float t01 = _chops01[_selectedMarkerIndex];
        _scrub01 = t01;
        _wave?.SetPlayhead01(t01);
        UpdateMarkerNavigatorUI();
        AuditionMarkerBoundary(_selectedMarkerIndex);
    }

    private void SelectNextMarker()
    {
        if (_chops01.Count == 0) return;
        ClampSelectedMarkerIndex();
        _selectedMarkerIndex = (_selectedMarkerIndex + 1) % _chops01.Count;
        float t01 = _chops01[_selectedMarkerIndex];
        _scrub01 = t01;
        _wave?.SetPlayhead01(t01);
        UpdateMarkerNavigatorUI();
        AuditionMarkerBoundary(_selectedMarkerIndex);
    }

    private void NudgeSelectedMarkerSeconds(float deltaSeconds)
    {
        if (_clip == null || _clip.length <= 0f) return;
        if (_chops01.Count == 0) return;

        ClampSelectedMarkerIndex();
        if (_selectedMarkerIndex < 0) return;

        float delta01 = deltaSeconds / _clip.length;
        float target01 = _chops01[_selectedMarkerIndex] + delta01;
        target01 = GetClampedMarkerPosition01(_selectedMarkerIndex, target01);

        _chops01[_selectedMarkerIndex] = target01;
        _chops01.Sort();
        _selectedMarkerIndex = FindNearestMarkerIndex(target01);
        ClampSelectedMarkerIndex();
        SyncMarkersToWave();
        UpdateMarkerNavigatorUI();
        PersistMarkersIfPossible();

        _scrub01 = target01;
        _wave?.SetPlayhead01(target01);
        AuditionMarkerBoundary(_selectedMarkerIndex);
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
        _selectedMarkerIndex = -1;
        SyncMarkersToWave();
        UpdateMarkerNavigatorUI();

        if (_markerPreviewCoroutine != null)
        {
            StopCoroutine(_markerPreviewCoroutine);
            _markerPreviewCoroutine = null;
        }

        // Also clear any applied chop set used by the sampler.
        KMusicChopState.ClearApplied();

        UpdateTrackTitle("MARKERS RESET");
    }

    private void AutoChop(int divisions)
    {
        if (_clip == null || _clip.length <= 0f) return;

        _chops01.Clear();

        divisions = Mathf.Clamp(divisions, 1, 16);

        // first chop always starts at 0
        _chops01.Add(0f);

        for (int i = 1; i < divisions; i++)
        {
            float t01 = i / (float)divisions;
            t01 = Mathf.Clamp(t01, 0f, 0.999f);
            _chops01.Add(t01);
        }

        // closing marker near end so final chop has an end
        _chops01.Add(0.999f);

        _selectedMarkerIndex = _chops01.Count > 0 ? 0 : -1;
        SyncMarkersToWave();
        UpdateMarkerNavigatorUI();
        PersistMarkersIfPossible();
        UpdateTrackTitle($"AUTO {divisions}");
    }
    
    private static float Snap01(float t01, int div)
    {
        if (div <= 1) return Mathf.Clamp01(t01);
        float step = 1f / div;
        return Mathf.Clamp01(Mathf.Round(t01 / step) * step);
    }
}
