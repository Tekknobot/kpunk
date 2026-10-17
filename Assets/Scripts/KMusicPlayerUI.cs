// Assets/Scripts/KMusicPlayerUI.cs
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;
using KMusic.UI;
using KMusic;
using KMusic.Core;
using ZPunk.DAW;
using KMusic.Android;
#if UNITY_IOS && !UNITY_EDITOR
using KMusic.iOS;
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
    private Button _btnImport;
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

    // Markers: normalized [0..1); zero is a valid explicit S01 start.
    private readonly List<float> _chops01 = new();

    private AudioClip[] _clips = Array.Empty<AudioClip>();
    private int _clipIndex = -1;

    private AudioClip _clip;
    private AudioClip _warpedClip;
    private string _clipResourcesPath = null;
    private bool _autoWarpEnabled = true;
    private float _sourceBpm;
    private float _lastSongBpm;
    private float _nextWarpCheck;
    private float _warpPendingAt = -1f;
    private string _warpStatus = "NO SAMPLE";
    private KMusicProjectManager _warpProjects;
    private KMusicApp _pitchApp;
    private KMusicDrumSequencer _chopEngine;
    private const string WarpEnabledKey = "zpunk.sampler.warp.enabled";

    // Last known playhead position from scrubbing/UI.
    // Used when dropping markers via buttons even if audio isn't playing.
    private float _scrub01 = 0f;

#if UNITY_ANDROID && !UNITY_EDITOR
    private TrackInfo[] _deviceTracks = Array.Empty<TrackInfo>();
    private int _deviceTrackIndex = -1;
    private Coroutine _loadDeviceCoroutine;
    private bool _deviceReady = false;
#endif

    private const string PrefKey_LastTrack = "kmusic.player.lastTrack.v1";
    public event Action<string> SampleRestoreError;
    private int _sampleRequestSerial;
    private string _pendingProjectId;

    // App-private samples are stored as portable relative IDs, not absolute iOS
    // sandbox paths (the sandbox UUID can change between app updates).
    private static string SampleIdForFile(string path)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetFullPath(Application.persistentDataPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (full.StartsWith(root, StringComparison.Ordinal))
            return "local:" + full.Substring(root.Length).Replace('\\','/');
        return "file:" + full;
    }

    public static string CanonicalSampleId(string id)
    {
        if (string.IsNullOrEmpty(id)) return id;
        // Resource identifiers stored for CHOPS omit the preview-only "res:" prefix.
        if (id.StartsWith("res:", StringComparison.Ordinal)) return id.Substring(4);
        if (!id.StartsWith("file:", StringComparison.Ordinal)) return id;
        string path = id.Substring(5);
        if (string.IsNullOrEmpty(path)) return id;
        try
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetFullPath(Application.persistentDataPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(root, StringComparison.Ordinal))
                return SampleIdForFile(full);
#if UNITY_IOS && !UNITY_EDITOR
            // Match the full DOCUMENTS/ZPunk prefix, not just a filename.
            string normal = path.Replace('\\','/');
            int marker = normal.LastIndexOf("/Documents/ZPunk/", StringComparison.Ordinal);
            if (marker >= 0)
                return "local:ZPunk/" + normal.Substring(marker + "/Documents/ZPunk/".Length);
#endif
        }
        catch (Exception) { }
        return id;
    }

    private static string ResolveLocalSample(string id)
    {
        if (string.IsNullOrEmpty(id) || !id.StartsWith("local:", StringComparison.Ordinal)) return null;
        string relative = id.Substring(6).Replace('/', Path.DirectorySeparatorChar);
        string root = Path.GetFullPath(Application.persistentDataPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string absolute = Path.GetFullPath(Path.Combine(root, relative));
        // Prevent crafted or corrupted projects from escaping the private folder.
        return absolute.StartsWith(root, StringComparison.Ordinal) ? absolute : null;
    }

    private void MissingProjectSample(string id)
    {
        string safeName = id;
        if (id != null && id.StartsWith("file:", StringComparison.Ordinal))
            safeName = Path.GetFileName(id.Substring(5));
        else if (id != null && id.StartsWith("local:", StringComparison.Ordinal))
            safeName = id.Substring(6);
        string message = "SAMPLE MISSING OR UNREADABLE: " + (string.IsNullOrEmpty(safeName) ? "unknown" : safeName) +
            ". Import the same file again, or choose another sample. CHOPS are silent until the correct file is available.";
        Debug.LogWarning("[Player] " + message);
        UpdateTrackTitle("MISSING SAMPLE");
        SampleRestoreError?.Invoke(message);
    }

    // Explicitly empty selection: never retain another project's cached sample.
    public void ClearSampleSelection()
    {
        _sampleRequestSerial++;
        _pendingProjectId = null;
#if UNITY_ANDROID && !UNITY_EDITOR
        if (_loadDeviceCoroutine != null) { StopCoroutine(_loadDeviceCoroutine); _loadDeviceCoroutine = null; }
#endif
        _clip = null;
        _warpedClip = null;
        _clipResourcesPath = null;
        _clipIndex = -1;
        _chops01.Clear();
        if (audioSource != null) { audioSource.Stop(); audioSource.clip = null; }
        if (_wave != null) { _wave.SetClip(null); _wave.SetPlayhead01(0); }
        KMusicChopState.SetCachedClip(null, null);
        UpdateTrackTitle("NO SAMPLE");
    }


    private sealed class ImportedTrackInfo
    {
        public string absolutePath;
        public string title;
        public string subtitle;
    }

    private readonly List<ImportedTrackInfo> _importedTracks = new();

    // Unity Editor: Assets/ZPunkSamples; iOS: Files > On My iPhone > ZPunk;
    // Android: public Music folder is queried through scoped-storage MediaStore.
    private static string PreferredSamplesDir
    {
        get
        {
#if UNITY_EDITOR
            return Path.Combine(Application.dataPath, "ZPunkSamples");
#elif UNITY_IOS
            return Path.Combine(Application.persistentDataPath, "ZPunk");
#else
            return Path.Combine(Application.persistentDataPath, "ZPunk");
#endif
        }
    }
    private static string LegacySamplesDir => Path.Combine(Application.persistentDataPath, "ImportedSamples");
    private static string ImportedSamplesDir => PreferredSamplesDir;
    private static string SharedSamplesDir => Application.persistentDataPath;

    private static void EnsureImportFolders()
    {
        if (!Directory.Exists(SharedSamplesDir))
            Directory.CreateDirectory(SharedSamplesDir);

        if (!Directory.Exists(ImportedSamplesDir))
            Directory.CreateDirectory(ImportedSamplesDir);
    }

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

    private KMusicApp PitchApp()
    {
        if (_pitchApp == null) _pitchApp = FindObjectOfType<KMusicApp>();
        return _pitchApp;
    }

    private float GetCurrentSamplePitchSemitones()
    {
        var app = PitchApp();
        return app != null && app.Bus != null
            ? Mathf.Round(Mathf.Clamp(app.Bus.GetValue("sample.pitch"), -12f, 12f))
            : 0f;
    }

    private float GetCurrentSamplePitchRate()
    {
        return Mathf.Pow(2f, GetCurrentSamplePitchSemitones() / 12f);
    }

    public void SetLastTrackId(string id)
    {
        // Project loader calls this with null for projects that never selected a sample.
        ClearSampleSelection();
        ClearLastTrackId();
        if (string.IsNullOrEmpty(id)) return;
        id = CanonicalSampleId(id);
        _pendingProjectId = id;
        SaveLastTrackId(id);
        // The manager starts before the player UI. The player will restore the
        // exact sample when BootAudioSources runs, not a random/default sample.
        if (_wave == null || audioSource == null) return;
#if UNITY_ANDROID && !UNITY_EDITOR
        if (id.StartsWith("uri:", StringComparison.Ordinal) && !_deviceReady) return;
#endif
        TryRestoreTrackById(id);
    }

    public void ClearLastTrackId()
    {
        ProjectPrefs.DeleteKey(PrefKey_LastTrack);
        ProjectPrefs.Save();
    }

    public void RefreshAppliedMarkersFromState()
    {
        RestoreMarkersFromAppliedState();
    }

    private bool TryRestoreTrackById(string id)
    {
        if (string.IsNullOrEmpty(id)) return true;
        if (audioSource == null || _wave == null) return false;
#if UNITY_ANDROID && !UNITY_EDITOR
        if (id.StartsWith("uri:", StringComparison.Ordinal))
        {
            string want = id.Substring(4);
            if (_deviceTracks != null)
            {
                for (int i = 0; i < _deviceTracks.Length; i++)
                    if (_deviceTracks[i] != null && _deviceTracks[i].uri == want)
                    { LoadDeviceTrackByIndex(i); return true; }
            }
            // The user's file can exist beyond the first 250 indexed tracks.
            // Restore directly by its exact saved content URI; failed access
            // is reported by CoLoadDeviceTrack, never replaced with track #1.
            if (_deviceReady)
            {
                _deviceTrackIndex = -1;
                LoadDeviceTrackByInfo(new TrackInfo { uri = want, title = "PROJECT SAMPLE" });
                return true;
            }
            MissingProjectSample(id);
            return false;
        }
#endif
        if (id.StartsWith("res:", StringComparison.Ordinal) ||
            (!id.Contains(":") && id.Contains("/")))
        {
            string wanted = id.StartsWith("res:", StringComparison.Ordinal) ? id.Substring(4) : id;
            AudioClip exact = Resources.Load<AudioClip>(wanted);
            if (exact == null) { MissingProjectSample(id); return false; }
            RefreshClipList();
            for (int i = 0; i < _clips.Length; i++)
                if (_clips[i] == exact) { LoadClipByIndex(i); return true; }
            MissingProjectSample(id);
            return false;
        }
        if (id.StartsWith("local:", StringComparison.Ordinal) || id.StartsWith("file:", StringComparison.Ordinal))
        {
            string filePath = id.StartsWith("local:", StringComparison.Ordinal)
                ? ResolveLocalSample(id) : id.Substring(5);
            // iOS upgrade: older 2.0 builds recorded absolute Documents/ZPunk paths.
            // Only relocate within the same known private ZPunk directory, never
            // select an arbitrary same-named file in the user's Music library.
#if UNITY_IOS && !UNITY_EDITOR
            if (id.StartsWith("file:", StringComparison.Ordinal) && !File.Exists(filePath))
            {
                string normalized = filePath.Replace('\\','/');
                int marker = normalized.LastIndexOf("/Documents/ZPunk/", StringComparison.Ordinal);
                if (marker >= 0)
                    filePath = ResolveLocalSample("local:ZPunk/" + normalized.Substring(marker + "/Documents/ZPunk/".Length));
            }
#endif
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                int serial = ++_sampleRequestSerial;
                StartCoroutine(CoLoadImportedTrackByPath(filePath, true, serial));
                return true;
            }
            MissingProjectSample(id);
            return false;
        }
        MissingProjectSample(id);
        return false;
    }

    private void RefreshImportedTrackList()
    {
        _importedTracks.Clear();

        string[] dirs = { ImportedSamplesDir, LegacySamplesDir,
#if UNITY_EDITOR
            Path.Combine(Application.streamingAssetsPath, "Tracks"),
#endif
        };
        var candidates = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (string file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                candidates.Add(file);
        }
        foreach (string path in candidates)
        {
            string ext = Path.GetExtension(path)?.ToLowerInvariant();
            if (ext != ".wav" && ext != ".mp3" && ext != ".ogg" && ext != ".m4a" && ext != ".aac" && ext != ".mp4")
                continue;

            _importedTracks.Add(new ImportedTrackInfo
            {
                absolutePath = path,
                title = Path.GetFileNameWithoutExtension(path),
                subtitle = "ZPUNK SAMPLE  /  " + Path.GetFileName(path)
            });
        }
    }

    private void LoadImportedTrackByIndex(int index)
    {
        RefreshImportedTrackList();
        if (_importedTracks.Count == 0)
            return;

        index = Mathf.Clamp(index, 0, _importedTracks.Count - 1);
        int serial = ++_sampleRequestSerial;
        StartCoroutine(CoLoadImportedTrackByPath(_importedTracks[index].absolutePath, true, serial));
    }

    private IEnumerator CoLoadImportedTrackByPath(string absolutePath, bool persistAsLastTrack, int requestSerial)
    {
        if (requestSerial != _sampleRequestSerial || string.IsNullOrEmpty(absolutePath) || !File.Exists(absolutePath))
            yield break;

        _chops01.Clear();
        SyncMarkersToWave();

        // Handles imported filenames containing spaces or other URL characters.
        string url = new Uri(Path.GetFullPath(absolutePath)).AbsoluteUri;
        AudioType type = GuessAudioTypeFromPath(absolutePath);

        using UnityWebRequest req = UnityWebRequestMultimedia.GetAudioClip(url, type);
        if (req.downloadHandler is DownloadHandlerAudioClip dh)
        {
            dh.streamAudio = false;
            dh.compressed = false;
        }

        yield return req.SendWebRequest();

        if (requestSerial != _sampleRequestSerial) yield break;
        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogWarning("[Player] Imported sample load failed: " + req.error + " path=" + absolutePath);
            MissingProjectSample(SampleIdForFile(absolutePath));
            yield break;
        }

        var loaded = DownloadHandlerAudioClip.GetContent(req);
        if (loaded == null)
        {
            MissingProjectSample(SampleIdForFile(absolutePath));
            yield break;
        }

        _clip = loaded;
        _pendingProjectId = null;
        _clipResourcesPath = SampleIdForFile(absolutePath);
        _clipIndex = -1;

        if (persistAsLastTrack)
            SaveLastTrackId(_clipResourcesPath);

        KMusicChopState.SetCachedClip(_clipResourcesPath, _clip);

        audioSource.Stop();
        audioSource.clip = _clip;
        audioSource.time = 0f;

        _scrub01 = 0f;

        _wave.SetClip(_clip);
        _wave.SetPlayhead01(0f);
        RestoreMarkersFromAppliedState();
        UpdateTimeLabels(0f, _clip.length);
        UpdateTrackTitle(Path.GetFileNameWithoutExtension(absolutePath));
        OnSourceSampleReady();
        RefreshTrackBrowserList();
    }

    private static AudioType GuessAudioTypeFromPath(string path)
    {
        string ext = Path.GetExtension(path)?.ToLowerInvariant();
        switch (ext)
        {
            case ".mp3": return AudioType.MPEG;
            case ".wav": return AudioType.WAV;
            case ".ogg": return AudioType.OGGVORBIS;
            case ".m4a":
            case ".aac":
            case ".mp4": return AudioType.MPEG;
            default: return AudioType.UNKNOWN;
        }
    }

    private void Awake()
    {
        if (!doc) doc = GetComponent<UIDocument>();
        EnsureDedicatedPlayerAudioSource();
        // A restored/serialized AudioSource may still start with its clip playing.
        // Loading a project or a saved sample must never audition it automatically.
        if (audioSource != null) audioSource.Stop();
        EnsureImportFolders();
        _autoWarpEnabled = ProjectPrefs.GetInt(WarpEnabledKey, 1) != 0;
    }

    private void EnsureDedicatedPlayerAudioSource()
    {
        // Even an AudioSource assigned in the Inspector must be configured:
        // the early return previously left its playOnAwake state unchanged.
        if (audioSource == null)
        {
            var child = transform.Find("PlayerAudioSource");
            if (child == null)
            {
                var go = new GameObject("PlayerAudioSource");
                go.transform.SetParent(transform, false);
                child = go.transform;
            }

            audioSource = child.GetComponent<AudioSource>();
            if (audioSource == null)
                audioSource = child.gameObject.AddComponent<AudioSource>();
        }

        audioSource.playOnAwake = false;
        audioSource.loop = false;
        audioSource.spatialBlend = 0f;
        audioSource.volume = 1f;
    }

    private void OnEnable()
    {
        EnsureDedicatedPlayerAudioSource();
        // UI initialization and automatic project restore must be silent.
        if (audioSource != null) audioSource.Stop();

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

        _wave.UserPointer01 -= OnWavePointer01;
        _wave.UserPointer01 += OnWavePointer01;
        _wave.UserTap01 -= OnWaveTap01;
        _wave.UserTap01 += OnWaveTap01;
        _wave.MarkerDrag01 -= OnWaveMarkerDrag01;
        _wave.MarkerDrag01 += OnWaveMarkerDrag01;
        _wave.MarkerSelected -= OnWaveMarkerSelected;
        _wave.MarkerSelected += OnWaveMarkerSelected;
        _wave.MarkersDraggable = _chopArmed;

        if (_btnPlay != null) _btnPlay.clicked += OnPlayClicked;
        if (_btnStop != null) _btnStop.clicked += OnStopClicked;
        if (_btnPrev != null) _btnPrev.clicked += CyclePrevTrack;
        if (_btnNext != null) _btnNext.clicked += CycleNextTrack;
        if (_btnLoad != null) _btnLoad.clicked += OpenTrackBrowser;

        if (_btnChop != null) _btnChop.clicked += ToggleChop;
        if (_btnApply != null) _btnApply.clicked += ApplyChops;
        if (_btnSend != null) _btnSend.clicked += SendChops;

        // Auto slice generation is retired from the DAW workflow. Existing
        // legacy buttons are hidden rather than triggering automatic markers.
        if (_btnAuto4 != null) _btnAuto4.style.display = DisplayStyle.None;
        if (_btnAuto8 != null) _btnAuto8.style.display = DisplayStyle.None;
        if (_btnAuto16 != null) _btnAuto16.style.display = DisplayStyle.None;

        WirePointerDownButton(_btnAddMode, OnAddMarkerButton, "km-add-btn--pointerdown");

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

                // Never replace a project's absent/deleted sample with first
                // track from the device library. Exact URI or explicit selection.
                var last = LoadLastTrackId();
                if (!string.IsNullOrEmpty(last))
                    TryRestoreTrackById(last);
                else
                    UpdateTrackTitle("NO SAMPLE SELECTED");

                RefreshTrackBrowserList();
            }, deviceTrackLimit);

            return;
        }
#endif
        RefreshImportedTrackList();
        RefreshClipList();

        var lastTrackId = LoadLastTrackId();
        if (!string.IsNullOrEmpty(lastTrackId))
            TryRestoreTrackById(lastTrackId);
        else
            UpdateTrackTitle("NO SAMPLE SELECTED");

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

        // Prefer the exact saved marker positions, which distinguish an explicit
        // marker at 0.0 from legacy slice arrays with an implicit 0.0 boundary.
        if (KMusicChopState.TryLoadAppliedMarkerPositions(out float[] savedMarkers))
        {
            for (int i = 0; i < savedMarkers.Length; i++)
            {
                float marker = Mathf.Clamp01(savedMarkers[i]);
                if (marker >= 0.9999f) continue;
                if (_chops01.Count > 0 && Mathf.Abs(_chops01[_chops01.Count-1] - marker) < 0.0001f)
                    continue;
                _chops01.Add(marker);
            }
            _chops01.Sort();
            ClampSelectedMarkerIndex();
            SyncMarkersToWave();
            UpdateMarkerNavigatorUI();
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
            _wave.UserTap01 -= OnWaveTap01;
            _wave.MarkerDrag01 -= OnWaveMarkerDrag01;
            _wave.MarkerSelected -= OnWaveMarkerSelected;
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
        _sampleRequestSerial++;
        _pendingProjectId = null;
        index = Mathf.Clamp(index, 0, _clips.Length - 1);

        _chops01.Clear();
        SyncMarkersToWave();

        _clipIndex = index;
        _clip = _clips[_clipIndex];
        _clipResourcesPath = _clip != null ? ($"{resourcesFolder}/" + _clip.name) : null;

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
        OnSourceSampleReady();
        RefreshTrackBrowserList();
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    private void LoadDeviceTrackByIndex(int index)
    {
        if (_deviceTracks == null || _deviceTracks.Length == 0) return;
        index = Mathf.Clamp(index, 0, _deviceTracks.Length - 1);
        _deviceTrackIndex = index;
        LoadDeviceTrackByInfo(_deviceTracks[index]);
    }

    private void LoadDeviceTrackByInfo(TrackInfo t)
    {
        if (t == null || string.IsNullOrEmpty(t.uri)) return;
        int serial = ++_sampleRequestSerial;
        _pendingProjectId = null;
        _chops01.Clear();
        SyncMarkersToWave();
        string title = string.IsNullOrEmpty(t.title) ? "TRACK" : t.title;
        if (!string.IsNullOrEmpty(t.artist)) title += " - " + t.artist;
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
        _loadDeviceCoroutine = StartCoroutine(CoLoadDeviceTrack(t, serial));
    }

    private IEnumerator CoLoadDeviceTrack(TrackInfo t, int requestSerial)
    {
        bool done = false;
        AudioClip loaded = null;

        yield return AndroidMusicLibrary.LoadTrackToClip(t, clip =>
        {
            loaded = clip;
            done = true;
        });

        while (!done) yield return null;
        if (requestSerial != _sampleRequestSerial) yield break;
        if (loaded == null)
        {
            MissingProjectSample("uri:" + t.uri);
            yield break;
        }

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
        OnSourceSampleReady();
        RefreshTrackBrowserList();
        _loadDeviceCoroutine = null;
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

    // Clean DAW sampler surface: retain the existing player, waveform and chop engine.
    public string DawSampleName => _clip != null ? _clip.name : "NO SAMPLE";
    public int DawPitchSemitones => Mathf.RoundToInt(GetCurrentSamplePitchSemitones());
    public void DawAdjustPitchSemitones(int steps)
    {
        DawSetPitchSemitones(DawPitchSemitones + steps);
    }
    public void DawSetPitchSemitones(int semitones)
    {
        var app = PitchApp();
        if (app == null || app.Bus == null) return;
        int snapped = Mathf.Clamp(semitones, -12, 12);
        // The shared sample.pitch bus also transposes live CHOPS voice pools.
        app.Bus.SetValue("sample.pitch", snapped);
        // AudioSource.pitch must be pushed while preview audio is playing;
        // otherwise the new setting is only heard after tapping PLAY again.
        if (audioSource != null && audioSource.isPlaying)
            audioSource.pitch = Mathf.Pow(2f, snapped / 12f);
    }
    public bool DawAutoWarp => _autoWarpEnabled;
    public string DawWarpStatus => _warpStatus;
    public float DawSourceBpm => _sourceBpm;
    public string DawSampleFolder
    {
        get
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return "ANDROID / Music (MediaStore)";
#elif UNITY_IOS && !UNITY_EDITOR
            return "FILES / On My iPhone / ZPunk";
#else
            return "UNITY / Assets/ZPunkSamples";
#endif
        }
    }
    public void DawToggleAutoWarp()
    {
        _autoWarpEnabled = !_autoWarpEnabled;
        ProjectPrefs.SetInt(WarpEnabledKey, _autoWarpEnabled ? 1 : 0);
        ProjectPrefs.Save();
        QueueWarp(true);
    }
    public void DawAdjustSourceBpm(float delta)
    {
        if (_clip == null) return;
        _sourceBpm = Mathf.Clamp((_sourceBpm > 0 ? _sourceBpm : CurrentSongTempo()) + delta, 40f, 240f);
        _sourceBpm = Mathf.Round(_sourceBpm * 10f) / 10f;
        QueueWarp(true);
    }
    private float CurrentSongTempo()
    {
        if (_warpProjects == null) _warpProjects = FindObjectOfType<KMusicProjectManager>();
        var song = _warpProjects != null ? _warpProjects.CurrentSong : null;
        if (song != null && song.bpm > 0f) return song.bpm;
        var app = FindObjectOfType<KMusic.KMusicApp>();
        return app != null && app.Bus != null ? Mathf.Max(1f, app.Bus.GetValue("tempo")) : 120f;
    }
    private void OnSourceSampleReady()
    {
        // A sample selection is a load, not a play command. This also prevents
        // a pending auto-warp completion from resuming an old preview.
        if (audioSource != null) audioSource.Stop();
        if (_markerPreviewCoroutine != null)
        {
            StopCoroutine(_markerPreviewCoroutine);
            _markerPreviewCoroutine = null;
        }
        // Cancel any unfinished conversion of the previous sample; an old
        // stretch must never replace the newly selected source.
        _warpGeneration++;
        // Let the old coroutine notice its generation change and release its
        // in-progress AudioClip. StopCoroutine bypasses its cleanup and leaked
        // megabytes of native audio memory when tempo/source changed repeatedly.
        _warpJob = null;
        if (_warpedClip != null && _warpedClip != _clip) Destroy(_warpedClip, 5f);
        _warpedClip = null;
        _sourceBpm = ZPunkSampleWarp.EstimateLoopBpm(_clip, CurrentSongTempo());
        _wave?.SetMusicalRuler(CurrentSongTempo(), _clip != null ? _clip.length : 0f);
        _lastSongBpm = -1f;
        QueueWarp(true);
    }
    private void QueueWarp(bool immediate = false)
    {
        _warpPendingAt = Time.unscaledTime + (immediate ? 0.05f : 0.35f);
    }
    private void Update()
    {
        if (_clip == null) return;
        if (Time.unscaledTime >= _nextWarpCheck)
        {
            _nextWarpCheck = Time.unscaledTime + .40f;
            float tempo = CurrentSongTempo();
            if (Mathf.Abs(tempo - _lastSongBpm) > .5f)
            {
                _lastSongBpm = tempo;
                _wave?.SetMusicalRuler(tempo, _clip.length);
                QueueWarp();
            }
        }
        if (_warpPendingAt > 0 && Time.unscaledTime >= _warpPendingAt)
        {
            _warpPendingAt = -1f;
            ApplyWarp();
        }
    }
    private Coroutine _warpJob;
    private int _warpGeneration;

    private void ApplyWarp()
    {
        if (_clip == null) return;
        int generation = ++_warpGeneration;
        // A stale worker aborts on its next chunk and destroys its temporary clip.
        // Do NOT hard-stop it here: that skips StretchAsync's native memory cleanup.
        _warpJob = StartCoroutine(RunWarp(generation));
    }

    private IEnumerator RunWarp(int generation)
    {
        AudioClip next = null;
        string result = "OFF / ORIGINAL AUDIO";
        if (_autoWarpEnabled)
        {
            _warpStatus = "WARPING / PLEASE WAIT";
            yield return StartCoroutine(ZPunkSampleWarp.StretchAsync(_clip,
                _sourceBpm, CurrentSongTempo(), (clip, status) =>
                {
                    next = clip;
                    result = status;
                }, () => generation != _warpGeneration || this == null));
            if (generation != _warpGeneration)
            {
                if (next != null && next != _clip) Destroy(next);
                yield break;
            }
            if (next == null) result = "ORIGINAL / " + result;
        }
        AudioClip oldWarp = _warpedClip;
        _warpedClip = next != _clip ? next : null;
        _warpStatus = result;
        AudioClip active = _warpedClip != null ? _warpedClip : _clip;
        if (audioSource != null && active != null)
        {
            bool wasPlaying = audioSource.isPlaying;
            float ratio = audioSource.clip != null && audioSource.clip.length > 0f
                ? audioSource.time / audioSource.clip.length : 0f;
            audioSource.clip = active;
            audioSource.time = Mathf.Clamp01(ratio) * Mathf.Max(0f, active.length - .001f);
            if (wasPlaying) audioSource.Play();
        }
        if (!string.IsNullOrEmpty(_clipResourcesPath))
            KMusicChopState.SetCachedClip(_clipResourcesPath, active);
        if (_wave != null)
        {
            float start01 = _wave.ViewStart01, len01 = _wave.ViewLen01;
            _wave.SetClip(active);
            _wave.RestoreWindow(start01, len01);
            _wave.SetMusicalRuler(CurrentSongTempo(), active != null ? active.length : 0f);
            SyncMarkersToWave();
        }
        if (oldWarp != null && oldWarp != _clip && oldWarp != active)
            Destroy(oldWarp, 5f);
        if (generation == _warpGeneration) _warpJob = null;
    }

    public void DawPreviousSample() { CyclePrevTrack(); }
    public void DawNextSample() { CycleNextTrack(); }
    public void DawPlaySample() { OnPlayClicked(); }
    public void DawStopSample() { OnStopClicked(); }
    // Manual marker editing is authoritative; automatic slice counts are retired
    // from the new Create workspace. Snap happens on actual beat subdivisions.
    public void DawEnableManualMarkers()
    {
        _chopArmed = true;
        _markerAddMode = true;
        _snapEnabled = true;
        if (_wave != null)
        {
            _wave.MarkersDraggable = true;
            _wave.SetMusicalRuler(CurrentSongTempo(), _clip != null ? _clip.length : 0f);
        }
    }
    public void DawAddMarker() { DawEnableManualMarkers(); DropChopMarkerAt01(_scrub01); }
    public void DawDeleteMarker()
    {
        if (_chops01.Count == 0) return;
        if (_selectedMarkerIndex >= 0 && _selectedMarkerIndex < _chops01.Count)
            DeleteNearestMarkerTo01(_chops01[_selectedMarkerIndex]);
        else
            DeleteNearestMarkerTo01(_scrub01);
    }
    public void DawSelectMarker(int direction)
    {
        if (direction < 0) SelectPreviousMarker();
        else SelectNextMarker();
    }
    public void DawNudgeSelectedMarker(int direction)
    {
        if (direction != 0) NudgeSelectedMarkerSeconds(direction < 0 ? -.01f : .01f);
    }
    public string DawSelectedMarkerLabel
    {
        get
        {
            if (_selectedMarkerIndex < 0 || _selectedMarkerIndex >= _chops01.Count || _clip == null)
                return "TAP A MARKER TO SELECT / DRAG TO ADJUST";
            return "SELECTED M" + (_selectedMarkerIndex + 1).ToString("00") +
                "  /  " + (_chops01[_selectedMarkerIndex] * _clip.length).ToString("0.000") +
                "s  /  DRAG OR NUDGE 10 MS";
        }
    }
    public void DawZoomSample(bool zoomIn)
    {
        if (_wave == null) return;
        if (zoomIn) _wave.ZoomIn();
        else _wave.ZoomOut();
    }
    public void DawApplySlices() { ApplyChops(); }
    private void ApplyBrowserSafeArea()
    {
        if (_trackBrowserOverlay == null || doc == null || doc.rootVisualElement == null ||
            Screen.width <= 0 || Screen.height <= 0) return;
        var root = doc.rootVisualElement;
        float width = root.resolvedStyle.width;
        float height = root.resolvedStyle.height;
        if (float.IsNaN(width) || width <= 1) width = Screen.width;
        if (float.IsNaN(height) || height <= 1) height = Screen.height;
        Rect safe = Screen.safeArea;
        float scaleX = width / Screen.width;
        float scaleY = height / Screen.height;
        _trackBrowserOverlay.style.left = 6f + safe.xMin * scaleX;
        _trackBrowserOverlay.style.right = 6f + (Screen.width - safe.xMax) * scaleX;
        _trackBrowserOverlay.style.top = 6f + (Screen.height - safe.yMax) * scaleY;
        _trackBrowserOverlay.style.bottom = 6f + safe.yMin * scaleY;
    }

    public void DawOpenBrowser()
    {
        if (_trackBrowserOverlay != null && doc != null && doc.rootVisualElement != null)
        {
            // Original browser lived inside hidden PagePlayer. Move it to the root
            // so it is genuinely visible when the new sampler is open.
            _trackBrowserOverlay.RemoveFromHierarchy();
            doc.rootVisualElement.Add(_trackBrowserOverlay);
        }
        ApplyBrowserSafeArea();
        OpenTrackBrowser();
    }

    private void OpenTrackBrowser()
    {
        if (_trackBrowserOverlay == null)
            return;

        ApplyBrowserSafeArea();
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
        _trackBrowserOverlay.style.alignItems = Align.Center;
        _trackBrowserOverlay.style.justifyContent = Justify.Center;
        _trackBrowserOverlay.style.paddingTop = 12;
        _trackBrowserOverlay.style.paddingBottom = 12;
        _trackBrowserOverlay.style.paddingLeft = 12;
        _trackBrowserOverlay.style.paddingRight = 12;
        _trackBrowserOverlay.style.backgroundColor = new Color(.018f,.033f,.057f,.95f);

        var card = new VisualElement();
        card.name = "TrackBrowserCard";
        card.AddToClassList("km-card");
        card.AddToClassList("km-project-card");
        card.AddToClassList("km-track-card");
        card.style.position = Position.Relative;
        card.style.left = StyleKeyword.Auto;
        card.style.top = StyleKeyword.Auto;
        card.style.right = StyleKeyword.Auto;
        card.style.bottom = StyleKeyword.Auto;
        card.style.flexGrow = 0;
        card.style.flexShrink = 1;
        card.style.width = Length.Percent(100);
        card.style.maxWidth = 520;
        card.style.height = Length.Percent(88);
        card.style.maxHeight = 750;
        card.style.minHeight = 220;
        card.style.paddingLeft = 12;
        card.style.paddingRight = 12;
        card.style.paddingTop = 12;
        card.style.paddingBottom = 12;
        card.style.backgroundColor = new Color(.075f,.112f,.160f);
        card.style.borderTopLeftRadius = 6;
        card.style.borderTopRightRadius = 6;
        card.style.borderBottomLeftRadius = 6;
        card.style.borderBottomRightRadius = 6;

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

        _btnImport = new Button(BeginImportSample) { text = "IMPORT" };
        _btnImport.AddToClassList("km-pillbtn");
        _btnImport.AddToClassList("km-project-toolbar-btn");
        _btnImport.AddToClassList("km-track-import");

        header.style.flexDirection = FlexDirection.Row;
        header.style.alignItems = Align.Center;
        header.style.height = 48;
        header.style.flexShrink = 0;
        title.style.fontSize = 16;
        title.style.color = new Color(.91f,.95f,.99f);
        _btnImport.style.height = 36;
        _btnImport.style.minWidth = 66;
        _btnImport.style.flexShrink = 0;
        _btnImport.style.marginRight = 5;
        closeBtn.style.height = 36;
        closeBtn.style.minWidth = 65;
        closeBtn.style.flexShrink = 0;
        header.Add(title);
        header.Add(_btnImport);
        header.Add(closeBtn);

        _trackBrowserSearch = new TextField();
        _trackBrowserSearch.name = "TrackSearchField";
        _trackBrowserSearch.label = "";
        _trackBrowserSearch.value = "";
        _trackBrowserSearch.AddToClassList("km-project-rename-field");
        _trackBrowserSearch.AddToClassList("km-track-search");
        // Use dark text on a light input surface, independent of Unity's theme.
        var searchInk = new Color(.87f,.94f,.98f);
        var searchSurface = new Color(.105f,.165f,.235f);
        _trackBrowserSearch.style.color = searchInk;
        _trackBrowserSearch.style.backgroundColor = searchSurface;
        _trackBrowserSearch.RegisterCallback<AttachToPanelEvent>(_ =>
        {
            var inner = _trackBrowserSearch.Q<VisualElement>(className: "unity-text-input");
            if (inner != null)
            {
                inner.style.color = searchInk;
                inner.style.backgroundColor = searchSurface;
            }
        });
        _trackBrowserSearch.style.height = 40;
        _trackBrowserSearch.style.minHeight = 40;
        _trackBrowserSearch.style.maxHeight = 40;
        _trackBrowserSearch.style.marginTop = 5;
        _trackBrowserSearch.style.marginBottom = 8;
        _trackBrowserSearch.RegisterValueChangedCallback(_ => RefreshTrackBrowserList());

        _trackBrowserCountLabel = new Label("0 items");
        _trackBrowserCountLabel.AddToClassList("km-track-count");
        _trackBrowserCountLabel.style.fontSize = 10;
        _trackBrowserCountLabel.style.color = new Color(.57f,.76f,.84f);
        _trackBrowserCountLabel.style.marginBottom = 7;

        _trackBrowserList = new ScrollView();
        _trackBrowserList.name = "TrackScroll";
        _trackBrowserList.AddToClassList("km-project-scroll");
        _trackBrowserList.AddToClassList("km-track-scroll");
        _trackBrowserList.style.flexGrow = 1f;
        _trackBrowserList.style.minHeight = 0;
        _trackBrowserList.verticalScrollerVisibility = ScrollerVisibility.Auto;
        _trackBrowserList.style.flexShrink = 1;

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
            row.style.minHeight = 64;
            row.style.height = StyleKeyword.Auto;
            row.style.marginBottom = 6;
            row.style.paddingLeft = 10;
            row.style.paddingRight = 10;
            row.style.paddingTop = 7;
            row.style.paddingBottom = 7;
            row.style.borderTopLeftRadius = 4;
            row.style.borderTopRightRadius = 4;
            row.style.borderBottomLeftRadius = 4;
            row.style.borderBottomRightRadius = 4;
            row.style.backgroundColor = new Color(.125f,.175f,.237f);
            if (item.IsCurrent)
                row.AddToClassList("km-track-row--current");

            var title = new Label(item.IsCurrent ? "● " + item.Title : item.Title);
            title.AddToClassList("km-track-row-title");
            title.style.fontSize = 13;
            title.style.color = new Color(.91f,.95f,.99f);
            title.style.whiteSpace = WhiteSpace.Normal;

            var subtitle = new Label(item.Subtitle ?? string.Empty);
            subtitle.AddToClassList("km-track-row-subtitle");
            subtitle.style.fontSize = 10;
            subtitle.style.color = new Color(.61f,.73f,.81f);

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

        RefreshImportedTrackList();
        for (int i = 0; i < _importedTracks.Count; i++)
        {
            var track = _importedTracks[i];
            if (track == null || string.IsNullOrEmpty(track.absolutePath))
                continue;

            int idx = i;
            string id = SampleIdForFile(track.absolutePath);
            items.Add(new TrackBrowserItem
            {
                Title = track.title,
                Subtitle = track.subtitle,
                SearchText = track.title + " " + track.subtitle + " " + track.absolutePath,
                IsCurrent = string.Equals(_clipResourcesPath, id, StringComparison.Ordinal),
                OnPick = () => LoadImportedTrackByIndex(idx)
            });
        }

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


    private void BeginImportSample()
    {
#if UNITY_IOS && !UNITY_EDITOR
        IOSAudioFileImporter.PickAudioFile(gameObject.name, nameof(OnIOSSampleImported), nameof(OnIOSSampleImportCancelled));
#else
        UpdateTrackTitle("IMPORT AVAILABLE ON iPHONE BUILD");
#endif
    }

    public void OnIOSSampleImported(string importedAbsolutePath)
    {
        if (string.IsNullOrEmpty(importedAbsolutePath))
        {
            UpdateTrackTitle("IMPORT FAILED");
            return;
        }

        RefreshImportedTrackList();
        RefreshTrackBrowserList();
        int serial = ++_sampleRequestSerial;
        StartCoroutine(CoLoadImportedTrackByPath(importedAbsolutePath, true, serial));
        UpdateTrackTitle("IMPORTED SAMPLE");
    }

    public void OnIOSSampleImportCancelled(string _)
    {
        UpdateTrackTitle("IMPORT CANCELLED");
    }

    private void OnPlayClicked()
    {
        if (_clip == null || audioSource == null) return;

        audioSource.Stop();
        audioSource.clip = _warpedClip != null ? _warpedClip : _clip;
        audioSource.loop = false;
        audioSource.pitch = Mathf.Max(0.01f, GetCurrentSamplePitchRate());
        audioSource.Play();
    }

    private void OnStopClicked()
    {
        if (audioSource != null)
        {
            audioSource.Stop();
            audioSource.pitch = 1f;
        }
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

    private void OnWavePointer01(float t01, bool isDrag)
    {
        if (_clip == null || _clip.length <= 0f || audioSource == null) return;

        // Markers are committed only on pointer-up taps. Swipes and pinch zoom
        // must never create/delete slice boundaries.
        SeekTo01(t01);
    }

    private void OnWaveTap01(float t01)
    {
        if (!_chopArmed || _clip == null) return;
        if (_markerAddMode) DropChopMarkerAt01(t01);
        else DeleteNearestMarkerTo01(t01);
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

    private void OnWaveMarkerSelected(int index)
    {
        if (index < 0 || index >= _chops01.Count) return;
        _selectedMarkerIndex = index;
        _scrub01 = _chops01[index];
        _wave?.SetSelectedMarker(index);
        _wave?.SetPlayhead01(_scrub01);
        UpdateMarkerNavigatorUI();
    }

    private void OnWaveMarkerDrag01(int markerIndex, float t01, bool isDrag)
    {
        if (_clip == null || _clip.length <= 0f || audioSource == null) return;
        if (!_chopArmed) return;
        if (markerIndex < 0 || markerIndex >= _chops01.Count) return;

        float newT01 = GetClampedMarkerPosition01(markerIndex, t01);
        if (_snapEnabled) newT01 = GetClampedMarkerPosition01(markerIndex, SnapMusical01(newT01));

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
        // The first start marker may land precisely on the first sample frame.
        float min = (markerIndex > 0) ? (_chops01[markerIndex - 1] + minGap01) : 0f;
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

        float pitchRate = Mathf.Max(0.01f, GetCurrentSamplePitchRate());
        float activeLength = (_warpedClip != null ? _warpedClip : _clip).length;
        startTime = Mathf.Clamp(start01 * activeLength, 0f, Mathf.Max(0f, activeLength - .001f));
        float rawSliceDur = Mathf.Clamp((end01 - start01) * activeLength, 0.03f, activeLength);
        // Preview the same bounded duration heard in the sequenced chop engine.
        if (_chopEngine == null) _chopEngine = FindObjectOfType<KMusicDrumSequencer>();
        float cap = _chopEngine != null ? _chopEngine.MaxChopDurationSeconds : 4f;
        float pitchedDur = Mathf.Clamp(rawSliceDur / pitchRate, 0.02f, cap);

        if (_markerPreviewCoroutine != null)
            StopCoroutine(_markerPreviewCoroutine);

        _markerPreviewCoroutine = StartCoroutine(CoPreviewSlice(startTime, pitchedDur));
    }

    private IEnumerator CoPreviewSlice(float startTime, float duration)
    {
        if (_clip == null || audioSource == null)
            yield break;

        float pitchRate = Mathf.Max(0.01f, GetCurrentSamplePitchRate());

        audioSource.Stop();
        AudioClip sampleToPlay = _warpedClip != null ? _warpedClip : _clip;
        audioSource.clip = sampleToPlay;
        audioSource.loop = false;
        audioSource.pitch = pitchRate;
        audioSource.time = Mathf.Clamp(startTime, 0f, Mathf.Max(0f, _clip.length - 0.001f));
        audioSource.Play();

        yield return new WaitForSecondsRealtime(duration);

        if (audioSource != null && (audioSource.clip == _clip || audioSource.clip == _warpedClip))
        {
            audioSource.Stop();
            audioSource.pitch = 1f;
        }

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

    private void OnAddMarkerButton()
    {
        SetMarkerMode(true);

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

    private float SnapMusical01(float position)
    {
        if (_clip == null || _clip.length <= 0f) return Mathf.Clamp01(position);
        float secondsPerSixteenth = 15f / Mathf.Max(1f, CurrentSongTempo());
        float time = Mathf.Clamp01(position) * _clip.length;
        float snapped = Mathf.Round(time / secondsPerSixteenth) * secondsPerSixteenth;
        return Mathf.Clamp01(snapped / _clip.length);
    }

    private void DropChopMarkerAt01(float t01)
    {
        if (_clip == null || _clip.length <= 0f) return;
        if (_chops01.Count >= 17) return;

        t01 = Mathf.Clamp01(t01);
        if (_snapEnabled) t01 = SnapMusical01(t01);

        float minSlice01 = MinMarkerGapSeconds / _clip.length;
        // All markers stay separated, even with snap disabled; never push 0.0
        // toward the next grid division or a percentage of the sample length.
        if (t01 > 1f - minSlice01) t01 = Mathf.Max(0f, 1f - minSlice01);
        for (int i = 0; i < _chops01.Count; i++)
            if (Mathf.Abs(_chops01[i] - t01) < minSlice01)
                return;

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

    private void ApplyChops()
    {
        if (_clip == null || _clip.length <= 0f) return;

        var boundaries = new List<float>(_chops01.Count + 2) { 0f };
        boundaries.AddRange(_chops01);
        boundaries.Add(1f);
        boundaries.Sort();

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
        _wave?.SetSelectedMarker(_selectedMarkerIndex);

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
        if (_wave == null) return;
        _wave.SetMarkers01(_chops01);
        _wave.SetSelectedMarker(_selectedMarkerIndex);
    }

    private int _lastTimeLabelSecond = -1, _lastDurationLabelSecond = -1;

    private void UpdatePlayheadUI()
    {
        AudioClip active = _warpedClip != null ? _warpedClip : _clip;
        if (active == null || active.length <= 0f || audioSource == null || audioSource.clip != active)
        {
            _wave?.SetPlayhead01(_scrub01);
            UpdateTimeLabels(active != null ? (_scrub01 * active.length) : 0f,
                active != null ? active.length : 0f);
            return;
        }
        float t = audioSource.time;
        float t01 = Mathf.Clamp01(t / active.length);
        _scrub01 = t01;
        _wave?.SetPlayhead01(t01);
        UpdateTimeLabels(t, active.length);
    }

    private void UpdateTimeLabels(float t, float dur)
    {
        // Scheduled at 30Hz, but clock text changes at most once per second.
        // Avoid two new strings and two UI text/layout invalidations per tick.
        int tSecond = Mathf.FloorToInt(Mathf.Max(0f,t));
        int dSecond = Mathf.FloorToInt(Mathf.Max(0f,dur));
        if (_timeLabel != null && tSecond != _lastTimeLabelSecond)
        {
            _lastTimeLabelSecond = tSecond;
            _timeLabel.text = FormatTime(t);
        }
        if (_durLabel != null && dSecond != _lastDurationLabelSecond)
        {
            _lastDurationLabelSecond = dSecond;
            _durLabel.text = FormatTime(dur);
        }
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

        KMusicChopState.ClearApplied();

        UpdateTrackTitle("MARKERS RESET");
    }

    private void AutoChop(int divisions)
    {
        if (_clip == null || _clip.length <= 0f) return;

        _chops01.Clear();

        divisions = Mathf.Clamp(divisions, 1, 16);

        _chops01.Add(0f);

        for (int i = 1; i < divisions; i++)
        {
            float t01 = i / (float)divisions;
            t01 = Mathf.Clamp(t01, 0f, 0.999f);
            _chops01.Add(t01);
        }

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
