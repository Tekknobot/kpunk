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
    private IVisualElementScheduledItem _playheadUiJob;

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

    // Markers: normalized [0..1); zero is a valid explicit S01 start.
    private readonly List<float> _chops01 = new();
    // An automatic hit slice uses the final detected transient as an END,
    // not another pad start. This also works when fewer than 16 hits remain.
    private bool _closingHitMarker;

    // Hints are ephemeral: never write noisy, machine-detected candidates into
    // saved chop marker lists until the user explicitly adds/snaps a marker.
    private readonly List<ZPunkSampleWarp.TransientHint> _transientHints = new();
    // The canonical hit-guide analysis is ALWAYS of the unwarped AudioClip.
    // This survives BPM changes and non-destructive warps in the same session.
    private readonly List<ZPunkSampleWarp.TransientHint> _originalTransientHints = new();
    private AudioClip _originalTransientSource;
    private bool _originalTransientReady;
    private AudioClip _transientAnalysisSource;
    // Exact mapping returned by the warp renderer (not an approximation).
    private List<ZPunkSampleWarp.TimeMapPoint> _activeWarpTimeMap;
    private Coroutine _transientJob;
    private bool _hitAnalysisBusy;
    private float _hitAnalysisStartedAt;
    private float _hitAnalysisProgress;
    private float _hitAnalysisCompletedSeconds;
    private bool _hitAnalysisFromCache;
    public bool DawHitAnalysisRunning => _hitAnalysisBusy;
    public float DawHitAnalysisProgress => _hitAnalysisProgress;
    public float DawHitAnalysisSeconds => DawHitAnalysisRunning
        ? Mathf.Max(0f, Time.unscaledTime - _hitAnalysisStartedAt)
        : _hitAnalysisCompletedSeconds;
    public float DawHitAnalysisEta => _hitAnalysisProgress > .04f && DawHitAnalysisRunning
        ? Mathf.Max(0f, DawHitAnalysisSeconds / _hitAnalysisProgress * (1f - _hitAnalysisProgress))
        : -1f;
    private bool _sourceBpmRhythmic;
    private bool _sourceBpmManual;
    private float _sourceBpmConfidence;
    public string DawTrackBpmLabel => _clip == null ? "TRACK BPM / NO SAMPLE" :
        _sourceBpm <= 0f ? "TRACK BPM / UNKNOWN" :
        "TRACK " + _sourceBpm.ToString("0.#") + " BPM / " +
        (_sourceBpmManual ? "MANUAL" : _sourceBpmRhythmic ?
            "DETECTED " + Mathf.RoundToInt(_sourceBpmConfidence * 100f) + "% EST." :
            "DURATION GUESS");
    public string DawTrackBpmCompact => _clip == null ? "BPM --" :
        _sourceBpm <= 0f ? "BPM ?" :
        _sourceBpm.ToString("0.#") + " BPM" +
        (_sourceBpmManual ? "" : _sourceBpmRhythmic ? " EST." : " ?");
    public bool DawShowWaveGrid => _wave != null && _wave.ShowMusicalGrid;
    public void DawToggleWaveGrid()
    {
        if (_wave == null) return;
        _wave.ShowMusicalGrid = !_wave.ShowMusicalGrid;
    }
    private int _transientRevision;
    private bool _transientSnapEnabled = true;
    // Stable beat-grid phase measured from the displayed audio. Retained for
    // ruler/grid snapping; SLICE HITS itself does not quantize the markers.
    private float _beatPhaseSeconds;
    private bool _beatPhaseFound;
    private bool _sliceHitsPending;
    private float _pendingSliceStart01;
    private string _sliceHitsFeedback = "";
    public string DawHitSliceStatus => _sliceHitsPending ? "WAITING FOR HIT ANALYSIS" :
        _warpProcessing ? "WAIT FOR WARP TO FINISH" :
        _hitAnalysisBusy ? "ANALYZING HITS" :
        !string.IsNullOrEmpty(_sliceHitsFeedback) ? _sliceHitsFeedback :
        _clip == null ? "LOAD A SAMPLE" : _transientHints.Count == 0 ?
        "NO HITS DETECTED" : "READY / " + _transientHints.Count + " HIT GUIDES / " +
        (_hitAnalysisFromCache ? "CACHED" :
            _hitAnalysisCompletedSeconds.ToString("0.0") + "s ANALYSIS");

    private void UpdateBeatGrid()
    {
        _wave?.SetMusicalRuler(CurrentSongTempo(), DawSampleDuration, _beatPhaseSeconds);
    }

    private void DetectBeatPhase(List<ZPunkSampleWarp.TransientHint> hits)
    {
        _beatPhaseSeconds = 0f;
        _beatPhaseFound = false;
        float duration = DawSampleDuration;
        float beat = 60f / Mathf.Max(1f, CurrentSongTempo());
        if (duration < beat * 2f || hits == null || hits.Count < 3) return;
        // A kick/snare phase histogram (weighted toward kick candidates),
        // rather than independently quantizing each marker to the file start.
        // This handles songs with a little silence before the first downbeat.
        const int bins = 64;
        float bestScore = 0f, sumWeights = 0f;
        int bestBin = 0;
        foreach (var hit in hits) sumWeights += hit.kick ? 1.5f : 1f;
        float tolerance = Mathf.Min(.055f, beat * .18f);
        for (int i = 0; i < bins; i++)
        {
            float phase = i * beat / bins;
            float score = 0f;
            foreach (var hit in hits)
            {
                float time = hit.position01 * duration;
                float displacement = Mathf.Abs(Mathf.Repeat(time - phase + beat * .5f, beat) - beat * .5f);
                if (displacement < tolerance)
                    score += (hit.kick ? 1.5f : 1f) * (1f - displacement / tolerance);
            }
            if (score > bestScore) { bestScore = score; bestBin = i; }
        }
        // Never infer a confidently locked groove from a few unrelated hits.
        if (bestScore < 2.1f || bestScore / Mathf.Max(.01f, sumWeights) < .17f) return;
        float coarsePhase = bestBin * beat / bins;
        float weightedOffset = 0f, weightTotal = 0f;
        foreach (var hit in hits)
        {
            float time = hit.position01 * duration;
            float delta = Mathf.Repeat(time - coarsePhase + beat * .5f, beat) - beat * .5f;
            if (Mathf.Abs(delta) > tolerance) continue;
            float weight = (hit.kick ? 1.5f : 1f) * (1f - Mathf.Abs(delta) / tolerance);
            weightedOffset += delta * weight;
            weightTotal += weight;
        }
        _beatPhaseSeconds = Mathf.Repeat(coarsePhase + weightedOffset / Mathf.Max(.001f, weightTotal), beat);
        _beatPhaseFound = true;
    }

    private void ResetTransientAnalysis(bool forgetOriginal = false)
    {
        _transientRevision++;
        if (_transientJob != null) StopCoroutine(_transientJob);
        _transientJob = null;
        _transientAnalysisSource = null;
        _hitAnalysisBusy = false;
        _transientHints.Clear();
        _analysisCompleted = false;
        _beatPhaseSeconds = 0f;
        _beatPhaseFound = false;
        _sliceHitsPending = false;
        _sliceHitsFeedback = "";
        _hitAnalysisProgress = 0f;
        _hitAnalysisCompletedSeconds = 0f;
        _hitAnalysisFromCache = false;
        if (forgetOriginal)
        {
            _originalTransientHints.Clear();
            _originalTransientSource = null;
            _originalTransientReady = false;
            _activeWarpTimeMap = null;
        }
        _wave?.SetTransientGuides(null);
        UpdateBeatGrid();
    }

    private bool _analysisCompleted;

    // The source is decoded/analyzed at most once per load. Subsequent warps
    // remap the original hit positions using the exact rendered timing curve;
    // neither tempo nor project BPM is part of the disk-cache key.
    private void RefreshGuidesFromOriginal(bool reused)
    {
        if (_clip == null || !_originalTransientReady || _originalTransientSource != _clip)
            return;
        _transientHints.Clear();
        if (_warpedClip != null && _activeWarpTimeMap != null)
            _transientHints.AddRange(ZPunkSampleWarp.RemapTransientHints(
                _originalTransientHints, _activeWarpTimeMap));
        else
            _transientHints.AddRange(_originalTransientHints);
        _analysisCompleted = true;
        DetectBeatPhase(_transientHints);
        if (_beatAlignFromHead && _warpedClip != null)
        {
            float beatSeconds = 60f / Mathf.Max(1f, CurrentSongTempo());
            _beatPhaseSeconds = Mathf.Repeat(_beatAlignStart01 * DawSampleDuration, beatSeconds);
            _beatPhaseFound = true;
        }
        _wave?.SetTransientGuides(_transientHints);
        UpdateBeatGrid();
        _hitAnalysisProgress = 1f;
        _hitAnalysisBusy = false;
        _hitAnalysisFromCache = reused;
        if (reused) _hitAnalysisCompletedSeconds = 0f;
        ZPunkSampleAnalysisCache.Trace((_warpedClip != null && _activeWarpTimeMap != null
            ? "REMAP exact warp positions" : "REUSE original positions") +
            " / guides=" + _transientHints.Count);
        if (_sliceHitsPending && !_warpProcessing)
        {
            float start = _pendingSliceStart01;
            _sliceHitsPending = false;
            SliceAtDetectedHits(start);
        }
    }

    private void ApplyOriginalHits(AudioClip source, int revision,
        List<ZPunkSampleWarp.TransientHint> hits, bool fromCache)
    {
        if (this == null || revision != _transientRevision || source != _clip)
            return;
        _originalTransientHints.Clear();
        if (hits != null) _originalTransientHints.AddRange(hits);
        _originalTransientSource = source;
        _originalTransientReady = true;
        _transientJob = null;
        _transientAnalysisSource = null;
        _hitAnalysisCompletedSeconds = fromCache ? 0f :
            Mathf.Max(0f, Time.unscaledTime - _hitAnalysisStartedAt);
        RefreshGuidesFromOriginal(fromCache);
        // Tempo detection uses the original coordinates even after warp switches.
        if (!_sourceBpmManual)
            StartCoroutine(EstimateLoadedSourceTempo(source, revision));
    }

    private void BeginTransientAnalysis()
    {
        if (_clip == null) { ResetTransientAnalysis(true); return; }
        if (_originalTransientReady && _originalTransientSource == _clip)
        {
            // Warp changes and panel re-enables reuse the same in-memory hits.
            RefreshGuidesFromOriginal(true);
            return;
        }
        // Do not cancel and restart source analysis when warp completes while
        // the initial scan is still running.
        if (_hitAnalysisBusy && _transientJob != null && _transientAnalysisSource == _clip)
        {
            ZPunkSampleAnalysisCache.Trace("IN-FLIGHT original scan reused");
            return;
        }
        ResetTransientAnalysis();
        AudioClip source = _clip;
        int revision = _transientRevision;
        if (ZPunkSampleAnalysisCache.TryLoad(_clipResourcesPath, source, source,
            "original", out List<ZPunkSampleWarp.TransientHint> stored))
        {
            ApplyOriginalHits(source, revision, stored, true);
            return;
        }
        _hitAnalysisStartedAt = Time.unscaledTime;
        _hitAnalysisBusy = true;
        _transientAnalysisSource = source;
        ZPunkSampleAnalysisCache.Trace("ANALYZE original audio / once per source");
        _transientJob = StartCoroutine(ZPunkSampleWarp.AnalyzeTransientHints(source, hits =>
        {
            if (this == null || revision != _transientRevision || _clip != source) return;
            ZPunkSampleAnalysisCache.Save(_clipResourcesPath, source, source, "original", hits);
            ApplyOriginalHits(source, revision, hits, false);
        }, () => revision != _transientRevision || _clip != source || this == null,
            progress => { if (revision == _transientRevision) _hitAnalysisProgress = Mathf.Clamp01(progress); }));
    }

    private IEnumerator EstimateLoadedSourceTempo(AudioClip source, int revision)
    {
        // Leave a frame for the hit-guide completion UI before heavier analysis.
        yield return null;
        if (source == null || _clip != source || _transientRevision != revision || _sourceBpmManual)
            yield break;
        float confidence;
        float estimate = ZPunkSampleWarp.EstimateRhythmicBpm(_originalTransientHints,
            source.length, CurrentSongTempo(), out confidence);
        if (source != _clip || _transientRevision != revision || _sourceBpmManual)
            yield break;
        if (estimate <= 0f) yield break;
        _sourceBpm = estimate;
        _sourceBpmRhythmic = true;
        _sourceBpmConfidence = confidence;
        if (_autoWarpEnabled) QueueWarp(true);
    }

    // Beat-grid snapping remains available for manually moved markers.
    private float SnapMarker01(float proposed01)
    {
        if (_transientSnapEnabled && _clip != null && _clip.length > 0f)
        {
            float maxDistance = .055f / Mathf.Max(.001f, DawSampleDuration);
            float distance = maxDistance;
            float nearest = -1f;
            foreach (var hint in _transientHints)
            {
                float d = Mathf.Abs(hint.position01 - proposed01);
                if (d < distance) { nearest = hint.position01; distance = d; }
            }
            if (nearest >= 0f) return nearest;
        }
        return _transientSnapEnabled ? SnapMusical01(proposed01) : Mathf.Clamp01(proposed01);
    }

    private void HighlightSelectedSlice()
    {
        if (_wave == null) return;
        if (DawTryGetChopRange(_selectedMarkerIndex + 1, out float start, out float end))
            _wave.SetActiveSlice01(start, end);
        else
            _wave.SetActiveSlice01(-1f, -1f);
    }

    public bool DawTransientSnapEnabled => _transientSnapEnabled;
    public int DawSelectedChopPad => _selectedMarkerIndex >= 0 &&
        _selectedMarkerIndex < 16 &&
        _selectedMarkerIndex < _chops01.Count - (_closingHitMarker ? 1 : 0)
            ? _selectedMarkerIndex + 1 : 0;
    public float DawSampleDuration => (_warpedClip != null ? _warpedClip : _clip) != null
        ? (_warpedClip != null ? _warpedClip : _clip).length : 0f;
    public bool DawTryGetChopRange(int pad, out float start01, out float end01)
    {
        start01 = end01 = 0f;
        if (_clip == null || pad < 1 || pad > 16 ||
            pad > _chops01.Count - (_closingHitMarker ? 1 : 0)) return false;
        start01 = _chops01[pad - 1];
        end01 = pad < _chops01.Count ? _chops01[pad] : 1f;
        return end01 > start01 + .00001f;
    }
    public void DawSelectChopPad(int pad)
    {
        if (!DawTryGetChopRange(pad, out float start, out float end)) return;
        SelectMarkerFromChopPicker(pad - 1);
        _wave?.FocusSlice01(start, end);
    }
    // Legacy step-sequencer pads have their own audition voice. Focus only
    // so the same tap does not fire both the player and drum-engine previews.
    public void DawFocusChopPad(int pad)
    {
        if (!DawTryGetChopRange(pad, out float start, out float end)) return;
        SelectMarkerFromChopPicker(pad - 1, false);
        _wave?.FocusSlice01(start, end);
    }
    public void DawShowFullSample()
    {
        _wave?.ResetZoom();
    }

    // The HEAD gesture establishes beat one. Detect a recurring kick/snare
    // pulse before proposing near-quarter-note boundaries. The source PCM is
    // never stretched or moved by this operation. We snap each predicted beat
    // to a nearby actual onset and otherwise retain the beat position, giving
    // usable, nearly even slices without chasing every hat or flam.
    private bool TryBuildBeatHitBoundaries(float start01, float duration,
        List<float> boundaries, out float beatSeconds, out int matched)
    {
        beatSeconds = 0f;
        matched = 0;
        if (duration < 2f || _transientHints.Count < 5) return false;
        float anchor = Mathf.Clamp01(start01) * duration;
        // On full songs hundreds of ghost hints may exist. Only the next
        // ~14 seconds are relevant to 16 chops at slower source tempos.
        var nearbyHits = new List<ZPunkSampleWarp.TransientHint>();
        foreach (var hint in _transientHints)
        {
            float seconds = hint.position01 * duration;
            if (seconds >= anchor - .08f && seconds <= anchor + 14.5f)
                nearbyHits.Add(hint);
        }
        if (nearbyHits.Count < 5) return false;
        // Explore roughly 70-190 BPM. Evaluate the FIRST 12 beats instead
        // of a full song: a slightly loose older recording may drift later.
        float favorite = Mathf.Clamp(CurrentSongTempo(), 70f, 190f);
        float bestScore = 0f;
        float bestInterval = 0f;
        int bestMatches = 0;
        for (float interval = .315f; interval <= .858f; interval += .003f)
        {
            int beats = Mathf.Min(12, Mathf.FloorToInt((duration - .055f - anchor) / interval));
            if (beats < 5) continue;
            float tolerance = Mathf.Min(.075f, interval * .17f);
            float score = 0f;
            int matches = 0, kickMatches = 0;
            for (int beat = 1; beat <= beats; ++beat)
            {
                float expected = anchor + interval * beat;
                float quality = 0f;
                bool found = false, kick = false;
                foreach (var hint in nearbyHits)
                {
                    float distance = Mathf.Abs(hint.position01 * duration - expected);
                    if (distance >= tolerance) continue;
                    // An onset's intensity matters: this avoids treating soft
                    // hats as equivalent to strong main drum attacks.
                    float attack = Mathf.Clamp(hint.strength, .15f, 2f);
                    float weight = (hint.kick ? 1.18f : 1.05f) *
                        (.75f + .25f * attack) * (1f - .35f * distance / tolerance);
                    if (weight <= quality) continue;
                    quality = weight;
                    found = true;
                    kick = hint.kick;
                }
                if (!found) continue;
                matches++;
                if (kick) kickMatches++;
                score += quality;
            }
            // A half-time candidate that ignores strong backbeat snares
            // halfway between its beats should not defeat a real 1-2-3-4
            // pulse. Quiet hi-hats are intentionally below this penalty.
            float offbeatPenalty = 0f;
            for (int beat = 0; beat < beats; ++beat)
            {
                float middle = anchor + (beat + .5f) * interval;
                foreach (var hint in nearbyHits)
                {
                    if (hint.strength < .9f ||
                        Mathf.Abs(hint.position01 * duration - middle) > tolerance * .65f)
                        continue;
                    offbeatPenalty += .22f * Mathf.Clamp01(hint.strength);
                    break;
                }
            }
            if (matches < 4 || matches < beats * .52f) continue;
            // Reward rhythmic coverage more than harmonic subdivisions;
            // prefer kick evidence and the song tempo only as weak tie-breaks.
            float coverage = matches / (float)beats;
            float rating = (score - offbeatPenalty) / beats * (.65f + .35f * coverage);
            if (kickMatches == 0) rating *= .83f;
            float tempo = 60f / interval;
            rating *= 1f - .045f * Mathf.Clamp01(Mathf.Abs(
                Mathf.Log(tempo / favorite)) / .7f);
            if (rating > bestScore)
            {
                bestScore = rating;
                bestInterval = interval;
                bestMatches = matches;
            }
        }
        if (bestInterval <= 0f || bestScore < .51f || bestMatches < 4)
            return false;

        beatSeconds = bestInterval;
        boundaries.Add(Mathf.Clamp01(start01));
        matched = 0;
        float previous = anchor;
        for (int beat = 1; beat <= 16; ++beat)
        {
            float expected = anchor + beat * bestInterval;
            if (expected >= duration - MinMarkerGapSeconds) break;
            float target = expected;
            float tolerance = Mathf.Min(.075f, bestInterval * .17f);
            float bestQuality = 0f;
            foreach (var hint in nearbyHits)
            {
                float time = hint.position01 * duration;
                float distance = Mathf.Abs(time - expected);
                if (distance >= tolerance) continue;
                float quality = (.65f + .35f * Mathf.Clamp(hint.strength, .1f, 2f)) *
                    (1f - .5f * distance / tolerance);
                if (quality <= bestQuality) continue;
                bestQuality = quality;
                target = time;
            }
            if (bestQuality > 0f) matched++;
            if (target <= previous + MinMarkerGapSeconds) target = expected;
            if (target <= previous + MinMarkerGapSeconds) break;
            boundaries.Add(Mathf.Clamp01(target / duration));
            previous = target;
        }
        // The final boundary is a slice END, not another playable pad.
        return boundaries.Count >= 2;
    }

    // Beat-guided by default, with a plain transient-following fallback for
    // loops whose rhythm cannot safely be inferred (odd/variable meters).
    public void DawSliceHits()
    {
        if (_clip == null || DawSampleDuration <= 0f)
        { _sliceHitsFeedback = "LOAD A SAMPLE FIRST"; return; }
        if (_warpProcessing)
        { _sliceHitsFeedback = "WAIT FOR WARP TO FINISH"; return; }
        float start01 = Mathf.Clamp01(_scrub01);
        if (_hitAnalysisBusy)
        {
            // Capture the exact playhead at button press; a song can take time
            // to analyze on phones. The action runs when current PCM finishes.
            _pendingSliceStart01 = start01;
            _sliceHitsPending = true;
            _sliceHitsFeedback = "WAITING FOR HIT ANALYSIS";
            UpdateTrackTitle("SLICE HITS / ANALYZING");
            return;
        }
        SliceAtDetectedHits(start01);
    }

    private void SliceAtDetectedHits(float start01)
    {
        float duration = DawSampleDuration;
        if (_clip == null || duration <= 0f || _transientHints.Count == 0)
        {
            _sliceHitsFeedback = "NO TRANSIENTS FOUND";
            UpdateTrackTitle(_sliceHitsFeedback);
            return; // Never destroy existing chops when analysis found no hits.
        }

        var boundaries = new List<float>(17);
        float beatInterval;
        int hitsMatched;
        bool beatGuided = TryBuildBeatHitBoundaries(start01, duration,
            boundaries, out beatInterval, out hitsMatched);
        if (!beatGuided)
        {
            // Conservative fallback: keep the source transients as-is and
            // avoid extremely short chops from ghost duplicates/flams.
            boundaries.Clear();
            float beatSeconds = 60f / Mathf.Max(1f, CurrentSongTempo());
            float minInterval = Mathf.Max(MinMarkerGapSeconds,
                Mathf.Min(.19f, beatSeconds * .34f));
            float lastSeconds = Mathf.Clamp01(start01) * duration;
            boundaries.Add(Mathf.Clamp01(start01));
            foreach (var hit in _transientHints)
            {
                float at = Mathf.Clamp01(hit.position01) * duration;
                if (at < lastSeconds + minInterval ||
                    at > duration - MinMarkerGapSeconds) continue;
                boundaries.Add(hit.position01);
                lastSeconds = at;
                if (boundaries.Count == 17) break;
            }
        }
        if (boundaries.Count < 2)
        {
            _sliceHitsFeedback = "NO FOLLOWING HITS / MOVE HEAD EARLIER";
            UpdateTrackTitle(_sliceHitsFeedback);
            return; // Preserve the user's preexisting chop work.
        }

        // The last boundary is always an END, even if the song offers fewer
        // than 16 usable hits. No unwanted tail pad to the end of a long song.
        _chops01.Clear();
        _chops01.AddRange(boundaries);
        _closingHitMarker = true;
        _selectedMarkerIndex = 0;
        _chopArmed = true;
        _markerAddMode = true;
        if (_wave != null) _wave.MarkersDraggable = true;
        SyncMarkersToWave();
        UpdateMarkerNavigatorUI();
        PersistMarkersIfPossible();
        _scrub01 = start01;
        SeekTo01(start01);
        _wave?.FocusSlice01(boundaries[0], boundaries[1]);
        int pads = boundaries.Count - 1;
        _sliceHitsFeedback = beatGuided
            ? "SLICED " + pads + " BEAT-GUIDED CHOPS / " +
              Mathf.RoundToInt(60f / beatInterval) + " BPM EST. / " +
              hitsMatched + " HIT MATCHES / ORIGINAL AUDIO"
            : "SLICED " + pads + " FREE-HIT CHOPS / BEAT UNCERTAIN";
        UpdateTrackTitle(_sliceHitsFeedback);
    }

    // Following a successful beat alignment, these are real quarter-note
    // divisions in the processed waveform, not arbitrary slices from a BPM
    // guessed from the entire track's duration. A 17th END bounds pad 16.
    public void DawSliceAlignedBeats()
    {
        if (_clip == null || !_beatAlignFromHead)
        { _sliceHitsFeedback = "ALIGN 1-2-3-4 FIRST"; return; }
        if (_warpProcessing)
        { _sliceHitsFeedback = "WAIT FOR BEAT ALIGN TO FINISH"; return; }
        float duration = DawSampleDuration;
        float secondsPerBeat = 60f / Mathf.Max(1f, CurrentSongTempo());
        if (duration <= secondsPerBeat) return;
        float anchorSeconds = _beatAlignStart01 * duration;
        int nearestBeat = Mathf.Max(0, Mathf.RoundToInt(
            (_scrub01 * duration - anchorSeconds) / secondsPerBeat));
        float sliceStart = anchorSeconds + nearestBeat * secondsPerBeat;
        var boundaries = new List<float>(17);
        for (int beat = 0; beat <= 16; beat++)
        {
            float at = sliceStart + beat * secondsPerBeat;
            if (at > duration - MinMarkerGapSeconds) break;
            boundaries.Add(Mathf.Clamp01(at / duration));
        }
        if (boundaries.Count < 2)
        {
            _sliceHitsFeedback = "MOVE HEAD EARLIER FOR BEAT CHOPS";
            return;
        }
        _chops01.Clear();
        _chops01.AddRange(boundaries);
        _closingHitMarker = true;
        _selectedMarkerIndex = 0;
        _chopArmed = true;
        _markerAddMode = true;
        if (_wave != null) _wave.MarkersDraggable = true;
        SyncMarkersToWave();
        UpdateMarkerNavigatorUI();
        PersistMarkersIfPossible();
        _scrub01 = boundaries[0];
        SeekTo01(_scrub01);
        _wave?.FocusSlice01(boundaries[0], boundaries[1]);
        _sliceHitsFeedback = "SLICED " + (boundaries.Count - 1) +
            " EQUAL BEAT CHOPS / FROM HEAD";
        UpdateTrackTitle(_sliceHitsFeedback);
    }

    public void DawToggleTransientSnap()
    {
        _transientSnapEnabled = !_transientSnapEnabled;
        if (_wave != null) _wave.PlayheadSnapsToGuides = _transientSnapEnabled;
        ProjectPrefs.SetInt("zpunk.sampler.snap.transient", _transientSnapEnabled ? 1 : 0);
        ProjectPrefs.Save();
    }

    private AudioClip[] _clips = Array.Empty<AudioClip>();
    private int _clipIndex = -1;

    private AudioClip _clip;
    private AudioClip _warpedClip;
    // The original PCM is analyzed independently of tempo warp and preview pitch.
    private Coroutine _keyAnalysisJob;
    private int _keyAnalysisRevision;
    private bool _keyAnalysisRunning;
    private ZPunkSampleKeyDetector.Result _sampleKey;
    // The browser and project restore must analyze the SAME original PCM.
    // These diagnostics identify the exact source/decoder settings used.
    private string _keyLoadRoute = "UNSPECIFIED";
    private long _loadedFileSize = -1;
    private long _loadedFileWriteUtcTicks = -1;

    private void RememberLoadedFile(string path)
    {
        _loadedFileSize = -1;
        _loadedFileWriteUtcTicks = -1;
        try
        {
            var info = new FileInfo(path);
            if (info.Exists)
            {
                _loadedFileSize = info.Length;
                _loadedFileWriteUtcTicks = info.LastWriteTimeUtc.Ticks;
            }
        }
        catch (Exception) { /* The normal browser load will report an inaccessible file. */ }
    }

    private bool AlreadyLoadedUnchangedFile(string path)
    {
        if (_clip == null || _loadedFileSize < 0 || string.IsNullOrEmpty(_clipResourcesPath) ||
            !string.Equals(CanonicalSampleId(_clipResourcesPath),
                CanonicalSampleId(SampleIdForFile(path)), StringComparison.Ordinal)) return false;
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length == _loadedFileSize &&
                info.LastWriteTimeUtc.Ticks == _loadedFileWriteUtcTicks;
        }
        catch (Exception) { return false; }
    }

    private void LogKeySource(string phase, AudioClip source, string details)
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        if (source == null) return;
        Debug.Log("[ZPUNK KEY] " + phase + " route=" + _keyLoadRoute +
            " id=" + (_clipResourcesPath ?? "(none)") +
            " clip=" + source.name + " instance=" + source.GetEntityId() +
            " samples=" + source.samples + " channels=" + source.channels +
            " hz=" + source.frequency + " state=" + source.loadState +
            " " + details);
#endif
    }

    public bool DawSampleKeyAvailable => _clip != null && !_keyAnalysisRunning && _sampleKey.found;
    public bool DawSampleKeyAnalyzing => _clip != null && _keyAnalysisRunning;
    public int DawSampleKeyTonic => _sampleKey.tonic;
    public bool DawSampleKeyMinor => _sampleKey.minor;
    public int DawPitchedKeyTonic => ((_sampleKey.tonic + DawPitchSemitones) % 12 + 12) % 12;
    public string DawSourceKeyText => _clip == null ? "NO SAMPLE" :
        _keyAnalysisRunning ? "ANALYZING" : _sampleKey.found ?
        ZPunkSampleKeyDetector.NameFor(_sampleKey.tonic, _sampleKey.minor) : _sampleKey.status;
    public string DawPitchedKeyText => !DawSampleKeyAvailable ? "UNKNOWN" :
        ZPunkSampleKeyDetector.NameFor(DawPitchedKeyTonic, _sampleKey.minor);
    public string DawSampleKeyConfidence => _sampleKey.status;

    private void ResetSampleKey()
    {
        _keyAnalysisRevision++;
        if (_keyAnalysisJob != null) StopCoroutine(_keyAnalysisJob);
        _keyAnalysisJob = null;
        _keyAnalysisRunning = false;
        _sampleKey = new ZPunkSampleKeyDetector.Result { status = "NO TONAL KEY" };
    }

    private void BeginSampleKeyAnalysis()
    {
        ResetSampleKey();
        if (_clip == null) return;
        _keyAnalysisRunning = true;
        int revision = _keyAnalysisRevision;
        AudioClip source = _clip;
        LogKeySource("ANALYZE START", source, "");
        // Imported audio can be handed off by a decoder while its AudioClip is
        // still loading. Never analyze partially initialized PCM from Browse.
        _keyAnalysisJob = StartCoroutine(CoAnalyzeReadySampleKey(source, revision));
    }

    private IEnumerator CoAnalyzeReadySampleKey(AudioClip source, int revision)
    {
        Func<bool> cancelled = () => revision != _keyAnalysisRevision || _clip != source || this == null;
        if (source != null && source.loadState == AudioDataLoadState.Unloaded)
            source.LoadAudioData();
        float deadline = Time.realtimeSinceStartup + 10f;
        while (!cancelled() && source != null &&
            source.loadState == AudioDataLoadState.Loading && Time.realtimeSinceStartup < deadline)
            yield return null;
        if (cancelled()) yield break;
        if (source == null || source.loadState != AudioDataLoadState.Loaded)
        {
            LogKeySource("ANALYZE UNAVAILABLE", source, "audio not decoded");
            _sampleKey = new ZPunkSampleKeyDetector.Result { status = "AUDIO NOT READY" };
            _keyAnalysisRunning = false;
            _keyAnalysisJob = null;
            yield break;
        }
        // Keep the exact same algorithm for both Browse and project restore.
        yield return ZPunkSampleKeyDetector.Analyze(source, result =>
        {
            if (cancelled()) return;
            _sampleKey = result;
            _keyAnalysisRunning = false;
            _keyAnalysisJob = null;
            LogKeySource("ANALYZE DONE", source,
                "key=" + result.Name + " confidence=" + result.status);
        }, cancelled);
        if (!cancelled() && _keyAnalysisRunning)
        {
            _keyAnalysisRunning = false;
            _keyAnalysisJob = null;
            _sampleKey = new ZPunkSampleKeyDetector.Result { status = "ANALYSIS INCOMPLETE" };
        }
    }
    private string _clipResourcesPath = null;
    private bool _autoWarpEnabled = true;
    private float _sourceBpm;
    // When set, HEAD establishes beat 1 for quarter-note beat alignment.
    // Position refers to the original, unwarped clip timeline.
    private bool _beatAlignFromHead;
    private float _beatAlignStart01;
    private int _tempoDetectionRevision;
    private bool _tempoDetectionRunning;
    private string _tempoDetectionStatus = "";
    public string DawTempoDetectionStatus => _tempoDetectionRunning ?
        "ANALYZING SOURCE BPM" : _tempoDetectionStatus;
    public void DawDetectSourceBpm()
    {
        if (_clip == null || _tempoDetectionRunning) return;
        int revision = ++_tempoDetectionRevision;
        _tempoDetectionRunning = true;
        _tempoDetectionStatus = "";
        AudioClip source = _clip;
        StartCoroutine(DetectSourceTempoAsync(source, revision));
    }
    private IEnumerator DetectSourceTempoAsync(AudioClip source, int revision)
    {
        List<ZPunkSampleWarp.TransientHint> found = null;
        // Never take display-warp coordinates for source BPM estimation.
        if (_originalTransientReady && _originalTransientSource == source)
            found = new List<ZPunkSampleWarp.TransientHint>(_originalTransientHints);
        else
        {
            // The UI may already be computing these exact original positions.
            // Wait for its result instead of starting a duplicate PCM scan.
            while (_hitAnalysisBusy && _transientAnalysisSource == source &&
                   revision == _tempoDetectionRevision && _clip == source && this != null)
                yield return null;
            if (_originalTransientReady && _originalTransientSource == source)
                found = new List<ZPunkSampleWarp.TransientHint>(_originalTransientHints);
            if (found == null && !ZPunkSampleAnalysisCache.TryLoad(
                _clipResourcesPath, source, source, "original", out found))
            {
                yield return StartCoroutine(ZPunkSampleWarp.AnalyzeTransientHints(source,
                    hits => { found = hits; },
                    () => revision != _tempoDetectionRevision || _clip != source || this == null));
                if (revision == _tempoDetectionRevision && _clip == source && found != null)
                    ZPunkSampleAnalysisCache.Save(_clipResourcesPath, source, source, "original", found);
            }
        }
        if (revision != _tempoDetectionRevision || _clip != source || this == null)
            yield break;
        _tempoDetectionRunning = false;
        float confidence;
        float bpm = ZPunkSampleWarp.EstimateRhythmicBpm(found, source.length,
            CurrentSongTempo(), out confidence);
        if (bpm <= 0f)
        {
            _tempoDetectionStatus = "BPM UNCERTAIN / SET SOURCE BPM MANUALLY";
            yield break;
        }
        _sourceBpm = bpm;
        _sourceBpmRhythmic = true;
        _sourceBpmConfidence = confidence;
        _tempoDetectionStatus = "AUTO BPM " + bpm.ToString("0.0") +
            " / " + Mathf.RoundToInt(confidence * 100f) + "% RHYTHMIC MATCH";
        QueueWarp(true);
    }
    private float _lastSongBpm;
    private float _nextWarpCheck;
    private float _warpPendingAt = -1f;
    private string _warpStatus = "NO SAMPLE";
    private float _warpProgress;
    private string _warpStage = "";
    private bool _warpProcessing;
    public float DawWarpProgress => _warpProgress;
    public bool DawWarpProcessing => _warpProcessing;
    public string DawWarpStage => _warpStage;
    private KMusicProjectManager _warpProjects;
    private KMusicApp _pitchApp;
    private KMusic.Core.ParameterBus _pitchBus;
    private Action<string, float> _onPitchBusChanged;

    // Sampler preview and CHOPS must react to the same pitch parameter.
    // This listener also covers edits from the older sampler knob, not just
    // the DAW's +/- semitone buttons.
    private void BindSamplePitchPreview()
    {
        var app = PitchApp();
        var bus = app != null ? app.Bus : null;
        if (bus == null) return;
        if (_pitchBus != null && _onPitchBusChanged != null)
            _pitchBus.OnChanged -= _onPitchBusChanged;
        _pitchBus = bus;
        if (_onPitchBusChanged == null)
            _onPitchBusChanged = (id, value) =>
            {
                if (id == "sample.pitch" && audioSource != null && audioSource.isPlaying)
                    audioSource.pitch = Mathf.Pow(2f, Mathf.Round(Mathf.Clamp(value, -12f, 12f)) / 12f);
            };
        _pitchBus.OnChanged += _onPitchBusChanged;
    }
    private KMusicDrumSequencer _chopEngine;
    private const string WarpEnabledKey = "zpunk.sampler.warp.enabled";

    // Last known playhead position from scrubbing/UI.
    // Used when dropping markers via buttons even if audio isn't playing.
    private float _scrub01 = 0f;
    private bool _playheadGrabbed;

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

    // A UnityWebRequest-decoded clip is native PCM memory, not managed GC memory.
    // Release it ONLY after all AudioSources and the CHOPS voice pool detach.
    private bool _ownsSourceClip;
    private Coroutine _importedLoadJob;
    private void ReleaseCurrentAudioForReplacement()
    {
        AudioClip oldSource = _clip;
        AudioClip oldWarp = _warpedClip;
        bool ownSource = _ownsSourceClip;
        _warpGeneration++;
        _appliedWarpSignature = null;
        _warpJob = null; // cancelled worker finishes its next chunk and frees its target
        _warpPendingAt = -1f;
        _warpProcessing = false;
        _warpProgress = 0f;
        _tempoDetectionRevision++;
        _tempoDetectionRunning = false;
        ResetSampleKey();
        ResetTransientAnalysis(true);
        if (_markerPreviewCoroutine != null)
        {
            StopCoroutine(_markerPreviewCoroutine);
            _markerPreviewCoroutine = null;
        }
        if (audioSource != null) { audioSource.Stop(); audioSource.clip = null; }
        // Stop and detach every CHOPS voice before destroying native clip memory.
        // Changing only the single cached reference still leaves 8+ AudioSources
        // referring to the previous sample, often across several project loads.
        KMusicChopState.SetCachedClip(null, null);
        var drums = FindObjectOfType<KMusicDrumSequencer>();
        if (drums != null) drums.ReleaseRuntimeSampleReferences();
        if (_wave != null) { _wave.SetClip(null); _wave.SetPlayhead01(0f); }
        _clip = null;
        _warpedClip = null;
        _ownsSourceClip = false;
        _clipResourcesPath = null;
        _loadedFileSize = -1;
        _loadedFileWriteUtcTicks = -1;
        if (oldWarp != null && oldWarp != oldSource) Destroy(oldWarp);
        if (ownSource && oldSource != null) Destroy(oldSource);
    }

    public void ClearSampleSelection()
    {
        _sampleRequestSerial++;
        _pendingProjectId = null;
        if (_importedLoadJob != null)
        {
            StopCoroutine(_importedLoadJob);
            _importedLoadJob = null;
        }
#if UNITY_ANDROID && !UNITY_EDITOR
        if (_loadDeviceCoroutine != null)
        {
            StopCoroutine(_loadDeviceCoroutine);
            _loadDeviceCoroutine = null;
        }
#endif
        ReleaseCurrentAudioForReplacement();
        _clipIndex = -1;
        _chops01.Clear();
        _closingHitMarker = false;
        SyncMarkersToWave();
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
        // The project may restore the bus after this component is enabled.
        // Attach on first access if it was not ready during OnEnable.
        if (_pitchBus == null) BindSamplePitchPreview();
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
        // Reusing the same source across projects must not reload PCM or redo markers.
        id = CanonicalSampleId(id);
        if (!string.IsNullOrEmpty(id) && _clip != null &&
            string.Equals(id, CanonicalSampleId(_clipResourcesPath), StringComparison.Ordinal))
        {
            _keyLoadRoute = "PROJECT RESTORE (REUSE)";
            SaveLastTrackId(id);
            RestoreMarkersFromAppliedState();
            LogKeySource("REUSE", _clip, "project retained current sample and key");
            return;
        }
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
        // BootAudioSources can run each time the player UI is re-enabled.
        // Never decode the identical sample a second time just to redraw it.
        if (_clip != null && string.Equals(CanonicalSampleId(id),
            CanonicalSampleId(_clipResourcesPath), StringComparison.Ordinal))
        {
            RestoreMarkersFromAppliedState();
            return true;
        }
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
                _keyLoadRoute = "PROJECT RESTORE (DECODE)";
                int serial = ++_sampleRequestSerial;
                StartImportedLoad(filePath, serial);
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
        string path = _importedTracks[index].absolutePath;
        if (AlreadyLoadedUnchangedFile(path))
        {
            // Project restore already has this original PCM and its key result.
            // Selecting it again in Browse must not destroy/redecode/rescore it.
            _keyLoadRoute = "BROWSE (REUSE)";
            SaveLastTrackId(_clipResourcesPath);
            LogKeySource("REUSE", _clip, "keeping completed/running key analysis");
            if (!_keyAnalysisRunning && !_sampleKey.found) BeginSampleKeyAnalysis();
            RefreshTrackBrowserList();
            return;
        }
        _keyLoadRoute = "BROWSE (DECODE)";
        int serial = ++_sampleRequestSerial;
        StartImportedLoad(path, serial);
    }

    private void StartImportedLoad(string path, int serial)
    {
        if (_importedLoadJob != null) StopCoroutine(_importedLoadJob);
        _importedLoadJob = StartCoroutine(CoLoadImportedTrackByPath(path, true, serial));
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

        ReleaseCurrentAudioForReplacement();
        _clip = loaded;
        _ownsSourceClip = true;
        _pendingProjectId = null;
        _clipResourcesPath = SampleIdForFile(absolutePath);
        RememberLoadedFile(absolutePath);
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
        _importedLoadJob = null;
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
        _autoWarpEnabled = ProjectPrefs.GetInt(WarpEnabledKey, 0) != 0;
        _transientSnapEnabled = ProjectPrefs.GetInt("zpunk.sampler.snap.transient", 1) != 0;
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
        BindSamplePitchPreview();
        // Re-enable may follow a temporary panel/app disable while a sample
        // remains selected. Restart the cancelled key analysis in that case.
        if (_clip != null && !_keyAnalysisRunning && !_sampleKey.found)
            BeginSampleKeyAnalysis();
        if (_clip != null && !_analysisCompleted && _transientJob == null)
            BeginTransientAnalysis();
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
        _wave.PlayheadMoved01 -= OnWavePlayheadMoved01;
        _wave.PlayheadMoved01 += OnWavePlayheadMoved01;
        _wave.PlayheadGrabbed -= OnWavePlayheadGrabbed;
        _wave.PlayheadGrabbed += OnWavePlayheadGrabbed;
        _wave.PlayheadSnapsToGuides = _transientSnapEnabled;
        _wave.MarkersDraggable = _chopArmed;

        UnwirePlayerButtons();
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

        if (_btnSnap != null) _btnSnap.clicked += ToggleSnap;
        if (_btnReset != null) _btnReset.clicked += ResetAllMarkers;
        if (_btnMarkerPrev != null) _btnMarkerPrev.clicked += SelectPreviousMarker;
        if (_btnMarkerNext != null) _btnMarkerNext.clicked += SelectNextMarker;
        if (_btnNudgeNeg10 != null) _btnNudgeNeg10.clicked += NudgeNegativeTen;
        if (_btnNudgeNeg01 != null) _btnNudgeNeg01.clicked += NudgeNegativeOne;
        if (_btnNudgePos01 != null) _btnNudgePos01.clicked += NudgePositiveOne;
        if (_btnNudgePos10 != null) _btnNudgePos10.clicked += NudgePositiveTen;

        SetMarkerMode(true);
        SetSnapState(_snapEnabled);
        UpdateMarkerNavigatorUI();

        BuildTrackBrowserOverlay(page);
        RefreshTrackBrowserList();

        if (_playheadUiJob != null) _playheadUiJob.Pause();
        _playheadUiJob = _root.schedule.Execute(UpdatePlayheadUI).Every(33);

        BootAudioSources();
    }

    private void NudgeNegativeTen() => NudgeSelectedMarkerSeconds(-.10f);
    private void NudgeNegativeOne() => NudgeSelectedMarkerSeconds(-.01f);
    private void NudgePositiveOne() => NudgeSelectedMarkerSeconds(.01f);
    private void NudgePositiveTen() => NudgeSelectedMarkerSeconds(.10f);

    private void UnwirePlayerButtons()
    {
        if (_btnPlay != null) _btnPlay.clicked -= OnPlayClicked;
        if (_btnStop != null) _btnStop.clicked -= OnStopClicked;
        if (_btnPrev != null) _btnPrev.clicked -= CyclePrevTrack;
        if (_btnNext != null) _btnNext.clicked -= CycleNextTrack;
        if (_btnLoad != null) _btnLoad.clicked -= OpenTrackBrowser;
        if (_btnChop != null) _btnChop.clicked -= ToggleChop;
        if (_btnApply != null) _btnApply.clicked -= ApplyChops;
        if (_btnSend != null) _btnSend.clicked -= SendChops;
        if (_btnDelMode != null) _btnDelMode.clicked -= OnDeleteMarkerButton;
        if (_btnSnap != null) _btnSnap.clicked -= ToggleSnap;
        if (_btnReset != null) _btnReset.clicked -= ResetAllMarkers;
        if (_btnMarkerPrev != null) _btnMarkerPrev.clicked -= SelectPreviousMarker;
        if (_btnMarkerNext != null) _btnMarkerNext.clicked -= SelectNextMarker;
        if (_btnNudgeNeg10 != null) _btnNudgeNeg10.clicked -= NudgeNegativeTen;
        if (_btnNudgeNeg01 != null) _btnNudgeNeg01.clicked -= NudgeNegativeOne;
        if (_btnNudgePos01 != null) _btnNudgePos01.clicked -= NudgePositiveOne;
        if (_btnNudgePos10 != null) _btnNudgePos10.clicked -= NudgePositiveTen;
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
        _closingHitMarker = false;

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
        if (KMusicChopState.TryLoadAppliedMarkerPositions(out float[] savedMarkers,
            out bool lastIsEndOnly))
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
            _closingHitMarker = lastIsEndOnly && _chops01.Count >= 2;
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
        UnwirePlayerButtons();
        if (_playheadUiJob != null) { _playheadUiJob.Pause(); _playheadUiJob = null; }
        ResetSampleKey();
        // Do not cancel the original-audio scan just because the sampler panel
        // is hidden. It can finish and SAVE while another tab is visible;
        // OnEnable reuses the finished result or the existing job.
        // Source replacement still calls ResetTransientAnalysis(true).
        if (_pitchBus != null && _onPitchBusChanged != null)
            _pitchBus.OnChanged -= _onPitchBusChanged;
        _pitchBus = null;
        if (_wave != null)
        {
            _wave.UserPointer01 -= OnWavePointer01;
            _wave.UserTap01 -= OnWaveTap01;
            _wave.MarkerDrag01 -= OnWaveMarkerDrag01;
            _wave.MarkerSelected -= OnWaveMarkerSelected;
            _wave.PlayheadMoved01 -= OnWavePlayheadMoved01;
            _wave.PlayheadGrabbed -= OnWavePlayheadGrabbed;
        }
        _playheadGrabbed = false;

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

    private void OnDestroy()
    {
        // Unity component teardown otherwise retains imported native clips until
        // process exit. Invalidated coroutines observe the generation change.
        ClearSampleSelection();
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
        bool resourceRestore = !string.IsNullOrEmpty(_pendingProjectId);
        if (_clip != null && _clip == _clips[index])
        {
            _keyLoadRoute = resourceRestore ? "PROJECT RESOURCE (REUSE)" : "BROWSE RESOURCE (REUSE)";
            _clipIndex = index;
            LogKeySource("REUSE", _clip, "same original resource clip");
            if (!_keyAnalysisRunning && !_sampleKey.found) BeginSampleKeyAnalysis();
            RefreshTrackBrowserList();
            return;
        }
        _keyLoadRoute = resourceRestore ? "PROJECT RESOURCE (NEW)" : "BROWSE RESOURCE (NEW)";
        _chops01.Clear();
        _closingHitMarker = false;
        SyncMarkersToWave();

        ReleaseCurrentAudioForReplacement();
        _clipIndex = index;
        _clip = _clips[_clipIndex];
        _ownsSourceClip = false;
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
        bool restoring = !string.IsNullOrEmpty(_pendingProjectId);
        if (_clip != null && string.Equals(_clipResourcesPath, "uri:" + t.uri, StringComparison.Ordinal))
        {
            _keyLoadRoute = restoring ? "ANDROID PROJECT (REUSE)" : "ANDROID BROWSE (REUSE)";
            SaveLastTrackId(_clipResourcesPath);
            LogKeySource("REUSE", _clip, "same MediaStore URI");
            if (!_keyAnalysisRunning && !_sampleKey.found) BeginSampleKeyAnalysis();
            RefreshTrackBrowserList();
            return;
        }
        _keyLoadRoute = restoring ? "ANDROID PROJECT (DECODE)" : "ANDROID BROWSE (DECODE)";
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
        if (requestSerial != _sampleRequestSerial)
        {
            if (loaded != null) Destroy(loaded);
            yield break;
        }
        if (loaded == null)
        {
            MissingProjectSample("uri:" + t.uri);
            yield break;
        }

        ReleaseCurrentAudioForReplacement();
        _clip = loaded;
        _ownsSourceClip = true;
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
    public bool DawBeatAlignFromHead => _beatAlignFromHead;
    public void DawAlignBeatsFromHead()
    {
        if (_clip == null || _clip.samples < 1024) return;
        // Even when switching from an existing warp, HEAD is a normalized
        // position in that clip. It maps approximately onto original time;
        // the source clip is always used for offline resynthesis.
        _beatAlignStart01 = Mathf.Clamp(_scrub01, 0f, .99999f);
        _beatAlignFromHead = true;
        if (!_autoWarpEnabled)
        {
            _autoWarpEnabled = true;
            ProjectPrefs.SetInt(WarpEnabledKey, 1);
            ProjectPrefs.Save();
        }
        _warpStatus = "BEAT 1 SET / ALIGNING";
        QueueWarp(true);
    }
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
        _sourceBpmManual = true;
        _sourceBpm = Mathf.Round(_sourceBpm * 10f) / 10f;
        QueueWarp(); // debounce repeated +/- taps; only render final tempo
    }
    public void DawScaleSourceBpm(float scale)
    {
        if (_clip == null) return;
        _sourceBpm = Mathf.Round(Mathf.Clamp(_sourceBpm * scale,
            40f, 240f) * 10f) / 10f;
        _sourceBpmManual = true;
        _tempoDetectionStatus = "SOURCE BPM ADJUSTED / CHECK BY EAR";
        QueueWarp();
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
        BeginSampleKeyAnalysis();
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
        _warpProcessing = false;
        _warpProgress = 0f;
        // Let the old coroutine notice its generation change and release its
        // in-progress AudioClip. StopCoroutine bypasses its cleanup and leaked
        // megabytes of native audio memory when tempo/source changed repeatedly.
        _warpJob = null;
        if (_warpedClip != null && _warpedClip != _clip)
        {
            AudioClip old = _warpedClip;
            KMusicChopState.SetCachedClip(_clipResourcesPath, _clip);
            var drum = FindObjectOfType<KMusicDrumSequencer>();
            if (drum != null) drum.ReleaseRuntimeSampleReferences();
            if (audioSource != null && audioSource.clip == old) audioSource.clip = _clip;
            Destroy(old);
        }
        _warpedClip = null;
        _activeWarpTimeMap = null;
        _tempoDetectionRevision++;
        _tempoDetectionRunning = false;
        _tempoDetectionStatus = "";
        _sourceBpm = ZPunkSampleWarp.EstimateLoopBpm(_clip, CurrentSongTempo());
        _sourceBpmRhythmic = false;
        _sourceBpmManual = false;
        _sourceBpmConfidence = 0f;
        _beatAlignFromHead = false; // a new sample needs a new first-beat anchor
        _beatAlignStart01 = 0f;
        UpdateBeatGrid();
        _lastSongBpm = -1f;
        BeginTransientAnalysis();
        QueueWarp(true);
    }
    private void QueueWarp(bool immediate = false)
    {
        _warpPendingAt = Time.unscaledTime + (immediate ? 0.05f : 0.35f);
        if (_autoWarpEnabled && _clip != null)
        {
            _warpStatus = "WAITING TO WARP";
            _warpProgress = 0f;
            _warpStage = "QUEUED";
            _warpProcessing = true;
        }
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
                UpdateBeatGrid();
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
    private string _appliedWarpSignature;

    private string WarpSignature()
    {
        return (_autoWarpEnabled ? "1" : "0") + ":" +
            _sourceBpm.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ":" +
            CurrentSongTempo().ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ":" +
            (_sourceBpmManual ? "M" : _sourceBpmRhythmic ? "R" : "?") + ":" +
            (_beatAlignFromHead ? _beatAlignStart01.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "-");
    }

    private void ApplyWarp()
    {
        if (_clip == null) return;
        string signature = WarpSignature();
        if (signature == _appliedWarpSignature)
        {
            _warpProcessing = false;
            return;
        }
        int generation = ++_warpGeneration;
        _warpProcessing = _autoWarpEnabled;
        _warpProgress = 0f;
        // A stale worker aborts on its next chunk and destroys its temporary clip.
        // Do NOT hard-stop it here: that skips StretchAsync's native memory cleanup.
        _warpJob = StartCoroutine(RunWarp(generation));
    }

    private IEnumerator RunWarp(int generation)
    {
        AudioClip next = null;
        List<ZPunkSampleWarp.TimeMapPoint> nextTimeMap = null;
        string result = "OFF / ORIGINAL AUDIO";
        if (_autoWarpEnabled && !_sourceBpmRhythmic && !_sourceBpmManual)
        {
            // The previous source BPM came from assumed loop length, not
            // rhythmic evidence. Preserve PCM while hit/BPM analysis runs.
            result = "ORIGINAL / BPM UNCONFIRMED";
        }
        else if (_autoWarpEnabled)
        {
            _warpStatus = "WARPING";
            yield return StartCoroutine(ZPunkSampleWarp.StretchAsync(_clip,
                _sourceBpm, CurrentSongTempo(), (clip, status) =>
                {
                    next = clip;
                    result = status;
                }, () => generation != _warpGeneration || this == null,
                (value, stage) =>
                {
                    if (generation != _warpGeneration) return;
                    _warpProgress = Mathf.Clamp01(value);
                    _warpStage = stage;
                }, _beatAlignFromHead ? _beatAlignStart01 : -1f,
                map => { nextTimeMap = map; }));
            if (generation != _warpGeneration)
            {
                if (next != null && next != _clip) Destroy(next);
                yield break;
            }
            if (next == null) result = "ORIGINAL / " + result;
        }
        if (generation != _warpGeneration)
        {
            if (next != null && next != _clip) Destroy(next);
            yield break;
        }
        AudioClip oldWarp = _warpedClip;
        _warpedClip = next != _clip ? next : null;
        _activeWarpTimeMap = _warpedClip != null ? nextTimeMap : null;
        _appliedWarpSignature = WarpSignature();
        _warpStatus = result;
        _warpProcessing = false;
        _warpProgress = _autoWarpEnabled && next != null ? 1f : 0f;
        AudioClip active = _warpedClip != null ? _warpedClip : _clip;
        if (audioSource != null && active != null)
        {
            bool wasPlaying = audioSource.isPlaying;
            // A stopped AudioSource often reports time 0 even when the user
            // has moved the playhead elsewhere. Preserve the scrub position.
            float ratio = wasPlaying && audioSource.clip != null && audioSource.clip.length > 0f
                ? audioSource.time / audioSource.clip.length : _scrub01;
            _scrub01 = Mathf.Clamp01(ratio);
            audioSource.clip = active;
            audioSource.time = _scrub01 * Mathf.Max(0f, active.length - .001f);
            if (wasPlaying) audioSource.Play();
            _wave?.SetPlayhead01(_scrub01);
        }
        if (!string.IsNullOrEmpty(_clipResourcesPath))
            KMusicChopState.SetCachedClip(_clipResourcesPath, active);
        if (_wave != null)
        {
            float start01 = _wave.ViewStart01, len01 = _wave.ViewLen01;
            _wave.SetClip(active);
            _wave.RestoreWindow(start01, len01);
            UpdateBeatGrid();
            SyncMarkersToWave();
            _wave.SetTransientGuides(_transientHints);
            HighlightSelectedSlice();
        }
        // NEVER rescan a rendered warp. The source's disk/memory-cached guides
        // are mapped through the same curve used to render this waveform.
        if (oldWarp != _warpedClip) BeginTransientAnalysis();
        if (oldWarp != null && oldWarp != _clip && oldWarp != active)
        {
            var drum = FindObjectOfType<KMusicDrumSequencer>();
            if (drum != null) drum.ReleaseRuntimeSampleReferences();
            Destroy(oldWarp);
        }
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
            UpdateBeatGrid();
        }
    }
    public void DawPrepareSliceLab()
    {
        DawEnableManualMarkers();
        if (!_autoWarpEnabled) return;
        // An intentionally non-destructive default: leave warp available as
        // an explicit secondary button, but not applied behind the user's back.
        _autoWarpEnabled = false;
        _beatAlignFromHead = false;
        ProjectPrefs.SetInt(WarpEnabledKey, 0);
        ProjectPrefs.Save();
        QueueWarp(true);
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
            string markerRole = _closingHitMarker && _selectedMarkerIndex == _chops01.Count - 1
                ? " / END" : "";
            return "SELECTED M" + (_selectedMarkerIndex + 1).ToString("00") + markerRole +
                "  /  " + (_chops01[_selectedMarkerIndex] * DawSampleDuration).ToString("0.000") +
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
        // Do not raise the software keyboard until the user taps Search.

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
        _trackBrowserOverlay.style.backgroundColor = new Color(.012f,.023f,.040f,.95f);

        var card = new VisualElement { name = "TrackBrowserCard" };
        card.AddToClassList("km-track-card");
        ZPunkOverlaySkin.Card(card, 600, 840);

        var section = new Label("SAMPLER  /  LIBRARY");
        section.style.fontSize = 11;
        section.style.color = ZPunkOverlaySkin.Accent;
        section.style.marginBottom = 5;
        section.style.flexShrink = 0;
        card.Add(section);

        var title = new Label("BROWSE SAMPLES");
        title.AddToClassList("km-track-title");
        ZPunkOverlaySkin.Heading(title);
        title.style.marginBottom = 5;
        card.Add(title);

        var subtitle = new Label("Choose a sample for CHOPS");
        subtitle.style.fontSize = 12;
        subtitle.style.color = ZPunkOverlaySkin.Muted;
        subtitle.style.marginBottom = 12;
        subtitle.style.flexShrink = 0;
        card.Add(subtitle);

        var header = new VisualElement { name = "ZPunkSampleBrowserActions" };
        header.AddToClassList("km-track-header");
        header.style.flexDirection = FlexDirection.Row;
        header.style.alignItems = Align.Center;
        header.style.flexShrink = 0;
        header.style.marginBottom = 11;

        // Import stays accessible from the sampler workspace. This dialog is
        // only for choosing existing samples, so it needs one dismissal action.
        var closeBtn = new Button(CloseTrackBrowser) { text = "CLOSE" };
        closeBtn.AddToClassList("km-track-close");
        ZPunkOverlaySkin.Button(closeBtn);
        closeBtn.style.flexGrow = 0;
        closeBtn.style.minWidth = 100;
        closeBtn.style.width = 110;
        header.style.justifyContent = Justify.FlexEnd;
        header.Add(closeBtn);
        card.Add(header);

        var searchTitle = new Label("SEARCH LIBRARY");
        searchTitle.style.fontSize = 11;
        searchTitle.style.color = ZPunkOverlaySkin.Muted;
        searchTitle.style.marginBottom = 5;
        searchTitle.style.flexShrink = 0;
        card.Add(searchTitle);

        _trackBrowserSearch = new TextField();
        _trackBrowserSearch.name = "TrackSearchField";
        _trackBrowserSearch.label = "";
        _trackBrowserSearch.value = "";
        _trackBrowserSearch.AddToClassList("km-project-rename-field");
        _trackBrowserSearch.AddToClassList("km-track-search");
        ZPunkOverlaySkin.TextInput(_trackBrowserSearch);
        _trackBrowserSearch.style.marginTop = 0;
        _trackBrowserSearch.style.marginBottom = 10;
        _trackBrowserSearch.RegisterValueChangedCallback(_ => RefreshTrackBrowserList());

        _trackBrowserCountLabel = new Label("0 items");
        _trackBrowserCountLabel.AddToClassList("km-track-count");
        _trackBrowserCountLabel.style.fontSize = 12;
        _trackBrowserCountLabel.style.color = ZPunkOverlaySkin.Muted;
        _trackBrowserCountLabel.style.marginBottom = 8;

        _trackBrowserList = new ScrollView();
        _trackBrowserList.name = "TrackScroll";
        _trackBrowserList.AddToClassList("km-project-scroll");
        _trackBrowserList.AddToClassList("km-track-scroll");
        _trackBrowserList.style.overflow = Overflow.Hidden;

        card.Add(_trackBrowserSearch);
        card.Add(_trackBrowserCountLabel);
        card.Add(ZPunkOverlaySkin.ScrollWithRail(_trackBrowserList));
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
            row.style.minHeight = 66;
            row.style.height = StyleKeyword.Auto;
            row.style.flexDirection = FlexDirection.Column;
            row.style.alignItems = Align.Stretch;
            row.style.justifyContent = Justify.Center;
            row.style.marginBottom = 7;
            row.style.paddingLeft = 11;
            row.style.paddingRight = 11;
            row.style.paddingTop = 10;
            row.style.paddingBottom = 10;
            row.style.borderLeftWidth = item.IsCurrent ? 3 : 1;
            row.style.borderLeftColor = item.IsCurrent ? ZPunkOverlaySkin.Accent : ZPunkOverlaySkin.Line;
            row.style.borderTopLeftRadius = 7;
            row.style.borderTopRightRadius = 7;
            row.style.borderBottomLeftRadius = 7;
            row.style.borderBottomRightRadius = 7;
            row.style.backgroundColor = item.IsCurrent ?
                new Color(.105f,.212f,.225f) : ZPunkOverlaySkin.Panel;
            if (item.IsCurrent)
                row.AddToClassList("km-track-row--current");

            var titleRow = new VisualElement();
            titleRow.style.flexDirection = FlexDirection.Row;
            titleRow.style.alignItems = Align.Center;
            titleRow.style.minWidth = 0;
            var itemTitle = new Label(item.Title);
            itemTitle.AddToClassList("km-track-row-title");
            itemTitle.style.flexGrow = 1;
            itemTitle.style.flexShrink = 1;
            itemTitle.style.minWidth = 0;
            itemTitle.style.fontSize = 14;
            itemTitle.style.color = ZPunkOverlaySkin.Ink;
            itemTitle.style.whiteSpace = WhiteSpace.NoWrap;
            itemTitle.style.overflow = Overflow.Hidden;
            itemTitle.style.textOverflow = TextOverflow.Ellipsis;
            titleRow.Add(itemTitle);
            if (item.IsCurrent)
            {
                var inUse = new Label("IN USE");
                inUse.style.fontSize = 10;
                inUse.style.color = ZPunkOverlaySkin.Accent;
                inUse.style.marginLeft = 7;
                inUse.style.flexShrink = 0;
                titleRow.Add(inUse);
            }
            row.Add(titleRow);

            if (!string.IsNullOrEmpty(item.Subtitle))
            {
                var detail = new Label(item.Subtitle);
                detail.AddToClassList("km-track-row-subtitle");
                detail.style.fontSize = 12;
                detail.style.color = ZPunkOverlaySkin.Muted;
                detail.style.whiteSpace = WhiteSpace.NoWrap;
                detail.style.overflow = Overflow.Hidden;
                detail.style.textOverflow = TextOverflow.Ellipsis;
                detail.style.marginTop = 4;
                row.Add(detail);
            }
            _trackBrowserList.Add(row);
        }

        if (_trackBrowserCountLabel != null)
            _trackBrowserCountLabel.text = shown + " / " + items.Count + " items";

        if (shown == 0)
        {
            var empty = new Label("NO SAMPLES FOUND. CLEAR THE SEARCH OR ADD SAMPLES FROM THE SAMPLER.");
            empty.AddToClassList("km-track-empty");
            empty.style.fontSize = 12;
            empty.style.color = ZPunkOverlaySkin.Muted;
            empty.style.paddingTop = 18;
            empty.style.paddingBottom = 18;
            empty.style.whiteSpace = WhiteSpace.Normal;
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
        StartImportedLoad(importedAbsolutePath, serial);
        UpdateTrackTitle("IMPORTED SAMPLE");
    }

    public void OnIOSSampleImportCancelled(string _)
    {
        UpdateTrackTitle("IMPORT CANCELLED");
    }

    private void CancelMarkerPreview()
    {
        if (_markerPreviewCoroutine == null) return;
        StopCoroutine(_markerPreviewCoroutine);
        _markerPreviewCoroutine = null;
    }

    private void OnPlayClicked()
    {
        if (_clip == null || audioSource == null) return;

        // Stop() rewinds an AudioSource. Restore the actual cursor position
        // AFTER stopping, or every Play will unintentionally start at zero.
        CancelMarkerPreview();
        AudioClip active = _warpedClip != null ? _warpedClip : _clip;
        float start01 = Mathf.Clamp01(_scrub01);
        audioSource.Stop();
        audioSource.clip = active;
        audioSource.loop = false;
        audioSource.pitch = Mathf.Max(0.01f, GetCurrentSamplePitchRate());
        audioSource.time = Mathf.Clamp(start01 * active.length, 0f,
            Mathf.Max(0f, active.length - .001f));
        _wave?.SetPlayhead01(start01);
        audioSource.Play();
    }

    private void OnStopClicked()
    {
        if (audioSource == null) return;
        AudioClip active = _warpedClip != null ? _warpedClip : _clip;
        if (audioSource.isPlaying && active != null && audioSource.clip == active)
            _scrub01 = Mathf.Clamp01(audioSource.time / Mathf.Max(.001f, active.length));
        CancelMarkerPreview();
        audioSource.Stop();
        audioSource.pitch = 1f;
        _wave?.SetPlayhead01(_scrub01);
        UpdateTimeLabels(active != null ? _scrub01 * active.length : 0f,
            active != null ? active.length : 0f);
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

    private void OnWavePlayheadGrabbed(bool grabbed)
    {
        _playheadGrabbed = grabbed;
        if (grabbed && _markerPreviewCoroutine != null)
        {
            // Stop a short marker audition before the user scrubs. Otherwise
            // that coroutine can stop a newly started preview or reset time.
            CancelMarkerPreview();
            if (audioSource != null) audioSource.Stop();
        }
    }

    private void OnWavePlayheadMoved01(float t01)
    {
        SeekTo01(t01);
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

        AudioClip active = _warpedClip != null ? _warpedClip : _clip;
        if (audioSource.clip != active) audioSource.clip = active;
        float t = t01 * active.length;
        audioSource.time = Mathf.Clamp(t, 0f, Mathf.Max(0f, active.length - 0.001f));

        _wave?.SetPlayhead01(t01);
        UpdateTimeLabels(audioSource.time, active.length);
    }

    private void OnWaveMarkerSelected(int index)
    {
        if (index < 0 || index >= _chops01.Count) return;
        _selectedMarkerIndex = index;
        _scrub01 = _chops01[index];
        _wave?.SetSelectedMarker(index);
        _wave?.SetPlayhead01(_scrub01);
        HighlightSelectedSlice();
        UpdateMarkerNavigatorUI();
    }

    private void OnWaveMarkerDrag01(int markerIndex, float t01, bool isDrag)
    {
        if (_clip == null || _clip.length <= 0f || audioSource == null) return;
        if (!_chopArmed) return;
        if (markerIndex < 0 || markerIndex >= _chops01.Count) return;

        float newT01 = GetClampedMarkerPosition01(markerIndex, t01);
        if (_snapEnabled) newT01 = GetClampedMarkerPosition01(markerIndex, SnapMarker01(newT01));

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

        float minGap01 = MinMarkerGapSeconds / Mathf.Max(.001f, DawSampleDuration);
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

        float activeLength = (_warpedClip != null ? _warpedClip : _clip).length;
        startTime = Mathf.Clamp(start01 * activeLength, 0f, Mathf.Max(0f, activeLength - .001f));
        // Preview the same bounded duration heard in the sequenced chop engine.
        if (_chopEngine == null) _chopEngine = FindObjectOfType<KMusicDrumSequencer>();
        float cap = _chopEngine != null ? _chopEngine.MaxChopDurationSeconds : 4f;
        float endTime = Mathf.Clamp(end01 * activeLength, startTime, activeLength);

        if (_markerPreviewCoroutine != null)
            StopCoroutine(_markerPreviewCoroutine);

        // Watch source position, not the original pitch-dependent stopwatch:
        // a live semitone edit must not spill into the following chop or cut
        // the current one short. Keep the existing duration safety cap.
        _markerPreviewCoroutine = StartCoroutine(CoPreviewSlice(startTime, endTime, cap));
    }

    private IEnumerator CoPreviewSlice(float startTime, float endTime, float maxSeconds)
    {
        if (_clip == null || audioSource == null)
            yield break;

        float pitchRate = Mathf.Max(0.01f, GetCurrentSamplePitchRate());

        audioSource.Stop();
        AudioClip sampleToPlay = _warpedClip != null ? _warpedClip : _clip;
        audioSource.clip = sampleToPlay;
        audioSource.loop = false;
        audioSource.pitch = pitchRate;
        audioSource.time = Mathf.Clamp(startTime, 0f, Mathf.Max(0f, sampleToPlay.length - 0.001f));
        audioSource.Play();

        float elapsed = 0f;
        while (audioSource != null && audioSource.isPlaying && elapsed < maxSeconds)
        {
            if (audioSource.time >= endTime - 0.002f) break;
            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }

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
            KMusicChopState.SaveApplied(_clipResourcesPath, _chops01, _closingHitMarker);
        else
            KMusicChopState.SaveAppliedFromClip(_clip, resourcesPathOrNull: null, markerPositions01: _chops01, lastMarkerIsEndOnly: _closingHitMarker);
    }

    private void OnAddMarkerButton()
    {
        SetMarkerMode(true);

        if (_clip == null || _clip.length <= 0f || audioSource == null) return;

        float t01;

        if (audioSource.isPlaying)
        {
            t01 = Mathf.Clamp01(audioSource.time / Mathf.Max(.001f, DawSampleDuration));
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
            t01 = Mathf.Clamp01(audioSource.time / Mathf.Max(.001f, DawSampleDuration));
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
        float seconds = Mathf.Max(.001f, DawSampleDuration);
        float time = Mathf.Clamp01(position) * seconds;
        float snapped = _beatPhaseSeconds +
            Mathf.Round((time - _beatPhaseSeconds) / secondsPerSixteenth) * secondsPerSixteenth;
        return Mathf.Clamp01(snapped / seconds);
    }

    private void DropChopMarkerAt01(float t01)
    {
        if (_clip == null || _clip.length <= 0f) return;
        if (_chops01.Count >= 17) return;

        t01 = Mathf.Clamp01(t01);
        if (_snapEnabled) t01 = SnapMarker01(t01);

        float minSlice01 = MinMarkerGapSeconds / Mathf.Max(.001f, DawSampleDuration);
        // All markers stay separated, even with snap disabled; never push 0.0
        // toward the next grid division or a percentage of the sample length.
        if (t01 > 1f - minSlice01) t01 = Mathf.Max(0f, 1f - minSlice01);
        for (int i = 0; i < _chops01.Count; i++)
            if (Mathf.Abs(_chops01[i] - t01) < minSlice01)
                return;

        if (_closingHitMarker && _chops01.Count > 0 &&
            t01 > _chops01[_chops01.Count - 1])
            _closingHitMarker = false;
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
            bool removedClosingMarker = _closingHitMarker && best == _chops01.Count - 1;
            _chops01.RemoveAt(best);
            if (removedClosingMarker || _chops01.Count < 2) _closingHitMarker = false;
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
            KMusicChopState.SaveApplied(_clipResourcesPath, _chops01, _closingHitMarker);
        else
            KMusicChopState.SaveAppliedFromClip(_clip, resourcesPathOrNull: null, markerPositions01: _chops01, lastMarkerIsEndOnly: _closingHitMarker);

        UpdateTrackTitle($"APPLIED {Mathf.Min(16, _chops01.Count)} SLICES");
    }

    private void SendChops()
    {
        if (_clip == null || _clip.length <= 0f) return;

        if (!string.IsNullOrEmpty(_clipResourcesPath))
            KMusicChopState.SaveApplied(_clipResourcesPath, _chops01, _closingHitMarker);
        else
            KMusicChopState.SaveAppliedFromClip(_clip, resourcesPathOrNull: null, markerPositions01: _chops01, lastMarkerIsEndOnly: _closingHitMarker);

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

    private void SelectMarkerFromChopPicker(int markerIndex, bool audition = true)
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
        HighlightSelectedSlice();
        if (audition) AuditionMarkerBoundary(_selectedMarkerIndex);
    }

    private void RefreshChopPickerUI()
    {
        for (int i = 0; i < _chopPickerButtons.Count; i++)
        {
            var btn = _chopPickerButtons[i];
            if (btn == null) continue;

            bool hasMarker = i < _chops01.Count - (_closingHitMarker ? 1 : 0) && i < 16;
            btn.SetEnabled(hasMarker);
            btn.EnableInClassList("km-chop-btn--wired", hasMarker);
            btn.EnableInClassList("km-chop-btn--active", hasMarker && i == _selectedMarkerIndex);
        }
    }

    private void UpdateMarkerNavigatorUI()
    {
        ClampSelectedMarkerIndex();
        _wave?.SetSelectedMarker(_selectedMarkerIndex);
        HighlightSelectedSlice();

        if (_markerNavLabel != null)
        {
            if (_chops01.Count == 0 || _selectedMarkerIndex < 0)
            {
                _markerNavLabel.text = "NO MARKER";
            }
            else
            {
                float t = _chops01[_selectedMarkerIndex] * DawSampleDuration;
                string role = (_closingHitMarker && _selectedMarkerIndex == _chops01.Count - 1) ||
                    _selectedMarkerIndex == 16 ? "END" : $"START {_selectedMarkerIndex + 1:00}";
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

        float delta01 = deltaSeconds / Mathf.Max(.001f, DawSampleDuration);
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
        HighlightSelectedSlice();
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
        // Do not let the 30Hz playback poll erase a placed cursor when
        // stopped, or fight a finger that is holding the playhead.
        if (_playheadGrabbed || !audioSource.isPlaying)
        {
            if (_playheadGrabbed) _wave?.AutoScrollDraggedPlayhead(1f / 30f);
            _wave?.SetPlayhead01(_scrub01);
            UpdateTimeLabels(_scrub01 * active.length, active.length);
            return;
        }
        float t = audioSource.time;
        float t01 = Mathf.Clamp01(t / active.length);
        _scrub01 = t01;
        _wave?.SetPlayhead01(t01);
        _wave?.FollowPlayhead(t01);
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
        _closingHitMarker = false;
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
        _closingHitMarker = false;

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
