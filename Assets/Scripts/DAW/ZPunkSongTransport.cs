using System;
using System.Collections.Generic;
using AudioHelm;
using KMusic;
using KMusic.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace ZPunk.DAW
{
    /// <summary>
    /// ZPunk 2.0 song transport. Arrangement mode is permanently enabled.
    /// Drums/chops are DSP scheduled. Audio Helm's public HelmController NoteOn API is
    /// immediate, so melodic events dispatch on the Unity frame closest to their DSP time.
    /// No claim of sample-accurate synth scheduling is made by this implementation.
    /// </summary>
    [DefaultExecutionOrder(250)]
    public sealed class ZPunkSongTransport : MonoBehaviour
    {
        [SerializeField, Range(0.04f, 0.20f)] private float audioLookahead = 0.10f;
        [SerializeField, Range(0.02f, 0.30f)] private float startDelay = 0.15f;
        [SerializeField] private bool verbose;

        private KMusicDrumSequencer drums;
        private KMusicProjectManager projects;
        private KMusicApp app;
        private HelmController synth;
        private HelmController pads;
        private readonly List<HelmSequencer> suspendedSequencers = new List<HelmSequencer>();
        private readonly Dictionary<int, HelmController> trackControllers = new Dictionary<int, HelmController>();
        private SongData song;
        private SongEventPlanner.Plan plan;
        private Label statusLabel;
        private AudioHelmClock clock;
        private bool songMode;
        private bool playing;
        private bool paused;
        private long pausedAtTick;
        private int prefetchedLoopSamples;

        public static bool IsSongModeActive { get; private set; }
        private double anchorDsp;
        private double secondsPerTick;
        private long startTick;
        private long loopStart;
        private long loopEnd;
        private int sampleCursor;
        private int midiCursor;
        private long cycle;
        private float bpm;

        public bool SongMode => songMode;
        public bool IsPlaying => playing;
        public long CurrentTick { get; private set; }

        private void Resolve()
        {
            if (drums == null) drums = GetComponent<KMusicDrumSequencer>();
            if (projects == null) projects = FindObjectOfType<KMusicProjectManager>();
            if (app == null) app = FindObjectOfType<KMusicApp>();
            if (clock == null) clock = FindObjectOfType<AudioHelmClock>();
        }

        // ZPunk 2.0 is permanently in arrangement mode. Retain the legacy
        // entry point so existing scenes/scripts can initialize safely.
        private void Start() { EnsureSongMode(); }

        public void InstallModeButton(VisualElement root, Button playButton)
        {
            EnsureSongMode();
            // Remove old DAW switch if retained by a customized visual tree.
            var old = root != null ? root.Q<Button>("ZPunkDawModeButton") : null;
            if (old != null) old.RemoveFromHierarchy();
        }

        private void RefreshButton() { }

        public void EnsureSongMode()
        {
            if (!songMode) ToggleSongMode();
        }

        public void ToggleSongMode()
        {
            Resolve();
            if (playing) StopSong();
            if (drums != null && drums.IsPlaying) drums.StopPlayback();
            songMode = true;
            IsSongModeActive = true;
            if (songMode)
            {
                // Do not let old 16-step Helm sequences run underneath the DAW clips.
                var sequencers = FindObjectsOfType<HelmSequencer>();
                foreach (HelmSequencer seq in sequencers)
                {
                    if (seq != null && seq.enabled)
                    {
                        suspendedSequencers.Add(seq);
                        seq.enabled = false;
                    }
                }
                if (clock != null) clock.pause = true;
                Debug.Log("[ZPunk DAW] Arrangement playback ready; PLAY uses song clips or a live pattern preview.");
            }
            else
            {
                foreach (HelmSequencer seq in suspendedSequencers)
                    if (seq != null) seq.enabled = true;
                suspendedSequencers.Clear();
                if (clock != null) clock.pause = true;
            }
            RefreshButton();
        }

        public bool PlaySong(long fromTick = 0)
        {
            Resolve();
            if (playing) StopSong();
            if (!songMode) return false;
            if (drums == null || projects == null)
            {
                Debug.LogWarning("[ZPunk DAW] Project or drum engine unavailable; cannot begin playback.");
                return false;
            }
            if (drums.IsPlaying) drums.StopPlayback();
            // Live groove edits become a temporary preview, without requiring SAVE.
            // Authored DAW arrangements are returned unchanged by the project manager.
            try { song = projects.GetSongForPlayback(); }
            catch (Exception e)
            {
                Debug.LogError("[ZPunk DAW] Cannot capture current song: " + e.Message);
                return false;
            }
            if (song == null)
            {
                Debug.LogWarning("[ZPunk DAW] No song data to play.");
                return false;
            }
            try { plan = SongEventPlanner.Build(song); }
            catch (Exception e)
            {
                Debug.LogError("[ZPunk DAW] Cannot build arrangement: " + e.Message);
                return false;
            }
            // Never run a silent transport and make the user think playback succeeded.
            // Snapshot selection already attempted a safe live-grid fallback.
            if (plan.samples.Count == 0 && plan.midi.Count == 0)
            {
                drums.SetDAWPlayheadStep(-1);
                int maskedHits = 0;
                foreach (byte mask in drums.CaptureDrumMask())
                    for (int lane = 0; lane < 8; ++lane)
                        if ((mask & (1 << lane)) != 0) maskedHits++;
                Debug.LogWarning("[ZPunk DAW] No playable events after capture; " +
                    $"runtimeDrumHits={maskedHits}, visibleDrumCells={drums.CountVisibleDrumSteps()}, " +
                    $"songTracks={song.tracks?.Count ?? 0}, clipContents={song.contents?.Count ?? 0}, " +
                    $"isLegacy={song.migratedFromLegacy}. " +
                    "DAW transport was not started. Copy this line AND the LIVE CAPTURE EMPTY line if present.");
                plan = null;
                return false;
            }
            ResolveControllers();
            bpm = Mathf.Clamp(song.bpm, 1f, 400f);
            secondsPerTick = 60.0 / bpm / SongData.TicksPerQuarter;
            long length = Math.Max(1L, plan.lengthTicks);
            loopStart = song.arrangementLoopEnabled ? Math.Max(0L, Math.Min(length - 1, song.loopStartTick)) : 0;
            loopEnd = song.arrangementLoopEnabled ? Math.Max(loopStart + 1, Math.Min(length, song.loopEndTick)) : length;
            startTick = Math.Max(0L, Math.Min(length - 1, fromTick));
            if (song.arrangementLoopEnabled && startTick >= loopEnd) startTick = loopStart;
            anchorDsp = AudioSettings.dspTime + Math.Max(startDelay, audioLookahead + 0.02f);
            sampleCursor = 0;
            midiCursor = 0;
            cycle = 0;
            prefetchedLoopSamples = 0;
            paused = false;
            CurrentTick = startTick;
            drums.SetDAWPlayheadStep(-1);
            SkipBefore(startTick);
            if (clock != null)
            {
                clock.bpm = bpm;
                clock.pause = false; // Keep native synth output alive; old HelmSequencers are disabled.
            }
            playing = true;
            if (verbose)
                Debug.Log($"[ZPunk DAW] PLAY events: {plan.samples.Count} samples, {plan.midi.Count} MIDI, from tick {startTick}");
            return true;
        }

        private void ResolveControllers()
        {
            trackControllers.Clear();
            var painter = FindObjectOfType<KMusic.UI.KMusicPianoRollStepPainter>();
            var padRuntime = FindObjectOfType<KMusicPadSynthRuntime>();
            synth = painter != null ? painter.ActiveHelmController : null;
            pads = padRuntime != null ? padRuntime.ActiveHelmController : null;
            if (song == null || song.tracks == null) return;
            for (int i = 0; i < song.tracks.Count; i++)
            {
                TrackData track = song.tracks[i];
                if (track == null) continue;
                if (track.kind == TrackKind.Synth && synth != null) trackControllers[i] = synth;
                if (track.kind == TrackKind.Pads && pads != null) trackControllers[i] = pads;
                // Bass remains silent until a third, independent Audio Helm instance is added.
            }
            if (synth == null) Debug.LogWarning("[ZPunk DAW] Main synth unavailable. Synth track will be silent.");
            if (pads == null) Debug.LogWarning("[ZPunk DAW] Pad synth unavailable. Pads track will be silent.");
        }

        private bool TrackAudible(int index)
        {
            if (song == null || song.tracks == null || index < 0 || index >= song.tracks.Count) return false;
            var track = song.tracks[index];
            if (track == null || track.mute || track.volume <= 0) return false;
            bool anySolo = false;
            foreach (TrackData t in song.tracks)
                if (t != null && t.solo) { anySolo = true; break; }
            return !anySolo || track.solo;
        }

        // Timeline positions are kept in integer ticks, even after a tempo change.
        private double DspAt(long tick) { return anchorDsp + (tick - startTick) * secondsPerTick; }

        private void Update()
        {
            if (!playing || plan == null) return;
            double now = AudioSettings.dspTime;
            if (now < anchorDsp - audioLookahead) return;
            double position = (now - anchorDsp) / secondsPerTick + startTick;
            CurrentTick = (long)Math.Max(0.0, Math.Floor(position));
            // The existing 16-step grids still need a moving playhead in DAW mode.
            // For a song arrangement this displays the current bar-relative step.
            if (now >= anchorDsp)
            {
                int step = (int)((CurrentTick / SongData.TicksPerSixteenth) % 16);
                if (drums != null) drums.SetDAWPlayheadStep(step);
            }
            long end = loopEnd;
            double endDsp = DspAt(end);

            // Sample events are pre-scheduled on Unity's DSP clock.
            while (sampleCursor < plan.samples.Count)
            {
                var e = plan.samples[sampleCursor];
                if (e.tick >= end) break;
                double target = DspAt(e.tick);
                if (target > now + audioLookahead) break;
                sampleCursor++;
                DispatchSample(e, target, now);
            }

            // Pre-schedule the start of the next loop while we still have DSP lead time.
            // This prevents late kick/chop events on a loop boundary.
            if (song.arrangementLoopEnabled && now + audioLookahead >= endDsp)
            {
                while (prefetchedLoopSamples < plan.samples.Count)
                {
                    var e = plan.samples[prefetchedLoopSamples];
                    if (e.tick < loopStart) { prefetchedLoopSamples++; continue; }
                    if (e.tick >= loopEnd) break;
                    double target = endDsp + (e.tick - loopStart) * secondsPerTick;
                    if (target > now + audioLookahead) break;
                    prefetchedLoopSamples++;
                    DispatchSample(e, target, now);
                }
            }

            // Audio Helm's NoteOn/NoteOff public API does not accept a future DSP timestamp.
            // Dispatch on the first suitable frame. This is not sample-accurate MIDI.
            while (midiCursor < plan.midi.Count)
            {
                var e = plan.midi[midiCursor];
                if (e.tick >= end || DspAt(e.tick) > now) break;
                midiCursor++;
                if (!trackControllers.TryGetValue(e.trackIndex, out HelmController controller) || controller == null) continue;
                if (e.type == SongEventPlanner.EventType.NoteOff) controller.NoteOff(e.value);
                else if (TrackAudible(e.trackIndex))
                    controller.NoteOn(e.value, Mathf.Clamp01((e.velocity / 127f) * song.tracks[e.trackIndex].volume));
            }

            if (now >= endDsp)
            {
                StopAllSynthNotes();
                if (song.arrangementLoopEnabled)
                {
                    // Preserve the original DSP phase rather than starting a new loop at Update time.
                    double nextAnchor = endDsp - (loopStart - 0) * secondsPerTick;
                    anchorDsp = nextAnchor;
                    startTick = 0;
                    cycle++;
                    sampleCursor = prefetchedLoopSamples;
                    midiCursor = 0;
                    prefetchedLoopSamples = 0;
                    SkipBefore(loopStart);
                    CurrentTick = loopStart;
                }
                else StopSong();
            }
        }

        private void DispatchSample(SongEventPlanner.Event e, double target, double now)
        {
            if (target < now - 0.02 || !TrackAudible(e.trackIndex)) return;
            TrackData track = song.tracks[e.trackIndex];
            float velocity = Mathf.Clamp01(e.velocity / 127f) * Mathf.Clamp01(track.volume);
            if (e.type == SongEventPlanner.EventType.Drum)
                drums.ScheduleDAWDrum(e.value, Math.Max(now + 0.005, target), velocity);
            else if (e.type == SongEventPlanner.EventType.Chop)
                drums.ScheduleDAWChop(e.value, Math.Max(now + 0.005, target), velocity);
        }

        /// <summary>Pause while retaining the playhead position. ResumeSong starts with DSP pre-roll.</summary>
        public void PauseSong()
        {
            if (!playing) return;
            long position = CurrentTick;
            StopSong();
            paused = true;
            pausedAtTick = position;
        }

        public void ResumeSong()
        {
            if (songMode && paused) PlaySong(pausedAtTick);
        }

        private void SkipBefore(long position)
        {
            while (sampleCursor < plan.samples.Count && plan.samples[sampleCursor].tick < position) sampleCursor++;
            while (midiCursor < plan.midi.Count && plan.midi[midiCursor].tick < position) midiCursor++;
        }

        public void StopSong()
        {
            if (!playing) return;
            playing = false;
            StopAllSynthNotes();
            if (drums != null)
            {
                drums.StopDAWVoices();
                drums.SetDAWPlayheadStep(-1);
            }
            if (clock != null) clock.pause = true;
            CurrentTick = 0;
            paused = false;
        }

        public void Seek(long tick)
        {
            if (!playing) { CurrentTick = Math.Max(0L, tick); return; }
            StopSong();
            PlaySong(tick);
        }

        private void StopAllSynthNotes()
        {
            var seen = new HashSet<int>();
            foreach (HelmController controller in trackControllers.Values)
                if (controller != null && seen.Add(controller.channel)) controller.AllNotesOff();
        }

        private void OnDisable()
        {
            StopSong();
            IsSongModeActive = false;
            foreach (HelmSequencer seq in suspendedSequencers)
                if (seq != null) seq.enabled = true;
            suspendedSequencers.Clear();
            songMode = false;
        }
        private void OnDestroy()
        {
            StopSong();
            IsSongModeActive = false;
            foreach (HelmSequencer seq in suspendedSequencers)
                if (seq != null) seq.enabled = true;
            suspendedSequencers.Clear();
        }
    }
}
