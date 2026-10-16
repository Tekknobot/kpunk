using System;
using System.Collections.Generic;
using AudioHelm;
using KMusic;
using KMusic.Core;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.Audio;

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
        private readonly Dictionary<int, AudioMixer> outputMixers = new Dictionary<int, AudioMixer>();
        private static readonly string[] HelmTrackGainParameters = { "", "ZPunkBassTrackGain", "ZPunkSynthTrackGain", "ZPunkPadsTrackGain", "" };
        private SongData song;
        private SongEventPlanner.Plan plan;
        private Label statusLabel;
        private AudioHelmClock clock;
        private bool songMode;
        private bool playing;
        private bool paused;
        private bool exportingOneShot;
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
        // An edited song can replace the active plan without resetting the transport.
        // DSP voices already queued within the lookahead interval cannot be recalled.
        private int liveRevision;
        public int LiveRevision => liveRevision;

        public bool SongMode => songMode;
        public bool IsPlaying => playing;
        public long CurrentTick { get; private set; }
        public double SongAnchorDsp => anchorDsp;

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

        public bool PlaySong(long fromTick = 0, bool oneShotExport = false)
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
            if (song != null && oneShotExport)
            {
                // Never modify the user's loop setting or editable live document.
                song = SongData.FromJson(song.ToJson());
                song.arrangementLoopEnabled = false;
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
            if (plan.samples.Count == 0 && plan.midi.Count == 0 && !song.arrangementAuthored)
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
            ApplyAllTrackGains();
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
            exportingOneShot = oneShotExport;
            if (verbose)
                Debug.Log($"[ZPunk DAW] PLAY events: {plan.samples.Count} samples, {plan.midi.Count} MIDI, from tick {startTick}");
            return true;
        }

        /// <summary>
        /// Apply edits while the DAW plays. Keep the playhead in musical ticks; recompute
        /// future events and retime the DSP anchor when BPM changes. Audio Helm note
        /// events are immediate/frame timed, and already queued samples can finish.
        /// </summary>
        /// <summary>Retimes the current DSP position without dropping held MIDI voices.</summary>
        public void SetLiveTempo(float nextBpm, SongData updated)
        {
            if (exportingOneShot) return;
            if (updated != null) song = updated;
            if (!playing) return;
            double now = AudioSettings.dspTime;
            long atTick = CurrentTick;
            bpm = Mathf.Clamp(nextBpm, 1f, 400f);
            secondsPerTick = 60.0 / bpm / SongData.TicksPerQuarter;
            anchorDsp = now - (atTick - startTick) * secondsPerTick;
            if (clock != null) clock.bpm = bpm;
            // Drums/chops already queued within the short lookahead may retain old timing.
            long safeTick = atTick + (long)Math.Ceiling((audioLookahead + .02f) / secondsPerTick);
            while (plan != null && sampleCursor < plan.samples.Count &&
                   plan.samples[sampleCursor].tick < safeTick) sampleCursor++;
            prefetchedLoopSamples = 0;
            liveRevision++;
        }

        public void ApplyLiveMix(SongData updated, bool stopSustains = false)
        {
            if (exportingOneShot) return;
            if (updated != null) song = updated;
            // Unity AudioMixer attenuation affects active/held Helm voices immediately.
            // Do not kill sustained notes just to update the fader.
            if (trackControllers.Count == 0) ResolveControllers();
            ApplyAllTrackGains();
        }

        public void ApplyLiveSong(SongData updated)
        {
            if (exportingOneShot) return;
            if (updated == null) return;
            Resolve();
            song = updated;
            if (trackControllers.Count == 0) ResolveControllers();
            ApplyAllTrackGains();
            if (!playing || plan == null) return;
            SongEventPlanner.Plan next;
            try { next = SongEventPlanner.Build(song); }
            catch (Exception ex)
            {
                Debug.LogWarning("[ZPunk DAW] Live edit rejected: " + ex.Message);
                return;
            }
            double now = AudioSettings.dspTime;
            long position = CurrentTick;
            bool tempoChanged = Math.Abs(song.bpm - bpm) > .01f;
            if (tempoChanged)
            {
                bpm = Mathf.Clamp(song.bpm, 1f, 400f);
                secondsPerTick = 60.0 / bpm / SongData.TicksPerQuarter;
                // Keep the current tick at the current DSP time rather than restarting.
                anchorDsp = now - (position - startTick) * secondsPerTick;
                if (clock != null) clock.bpm = bpm;
            }
            plan = next;
            long length = Math.Max(1L, plan.lengthTicks);
            loopStart = song.arrangementLoopEnabled ? Math.Max(0L, Math.Min(length - 1, song.loopStartTick)) : 0;
            loopEnd = song.arrangementLoopEnabled ? Math.Max(loopStart + 1, Math.Min(length, song.loopEndTick)) : length;
            sampleCursor = 0;
            midiCursor = 0;
            prefetchedLoopSamples = 0;
            // Don't schedule duplicate drum voices in the already-filled DSP window.
            long safeSampleTick = position + (long)Math.Ceiling((audioLookahead + .02f) / secondsPerTick);
            while (sampleCursor < plan.samples.Count && plan.samples[sampleCursor].tick < safeSampleTick)
                sampleCursor++;
            while (midiCursor < plan.midi.Count && plan.midi[midiCursor].tick < position)
                midiCursor++;
            ResolveControllers();
            // A removed/moved note must not hang. Resume any unchanged sustained
            // pitches that began before the current playhead but are still held.
            StopAllSynthNotes();
            var held = new Dictionary<string, SongEventPlanner.Event>();
            foreach (var midiEvent in plan.midi)
            {
                if (midiEvent.tick > position) break;
                string key = midiEvent.trackIndex + ":" + midiEvent.value;
                if (midiEvent.type == SongEventPlanner.EventType.NoteOn) held[key] = midiEvent;
                else held.Remove(key);
            }
            foreach (var entry in held.Values)
            {
                if (TrackAudible(entry.trackIndex) &&
                    trackControllers.TryGetValue(entry.trackIndex, out HelmController active) && active != null)
                    active.NoteOn(entry.value, Mathf.Clamp01(
                        entry.velocity / 127f));
            }
            liveRevision++;
            // If the loop is shortened past the playhead, jump to its beginning.
            if (position >= loopEnd) SeekLive(loopStart);
        }

        private void SeekLive(long newTick)
        {
            double now = AudioSettings.dspTime;
            startTick = newTick;
            anchorDsp = now + Math.Max(.035f, audioLookahead);
            CurrentTick = newTick;
            sampleCursor = 0;
            midiCursor = 0;
            prefetchedLoopSamples = 0;
            if (plan != null) SkipBefore(newTick);
            if (drums != null) drums.StopDAWVoices();
        }

        private void ResolveControllers()
        {
            trackControllers.Clear();
            outputMixers.Clear();
            var painter = FindObjectOfType<KMusic.UI.KMusicPianoRollStepPainter>();
            var padRuntime = FindObjectOfType<KMusicPadSynthRuntime>();
            var rack = FindObjectOfType<ZPunkInstrumentRack>();
            synth = painter != null ? painter.ActiveHelmController : null;
            pads = padRuntime != null ? padRuntime.ActiveHelmController : null;
            if (song == null || song.tracks == null) return;
            for (int i = 0; i < song.tracks.Count; i++)
            {
                TrackData track = song.tracks[i];
                if (track == null) continue;
                if (track.kind == TrackKind.Synth && synth != null) trackControllers[i] = synth;
                if (track.kind == TrackKind.Pads && pads != null) trackControllers[i] = pads;
                if (track.kind == TrackKind.Bass && rack != null && rack.BassController != null)
                    trackControllers[i] = rack.BassController;
            }
            foreach (var item in trackControllers)
            {
                var source = item.Value != null ? item.Value.GetComponent<AudioSource>() : null;
                if (source != null && source.outputAudioMixerGroup != null)
                    outputMixers[item.Key] = source.outputAudioMixerGroup.audioMixer;
            }
            if (synth == null) Debug.LogWarning("[ZPunk DAW] Main synth unavailable. Synth track will be silent.");
            if (pads == null) Debug.LogWarning("[ZPunk DAW] Pad synth unavailable. Pads track will be silent.");
        }

        /// <summary>True mixer attenuation, independent of the current Helm preset and NoteOn velocity.</summary>
        private void ApplyAllTrackGains()
        {
            if (song == null || song.tracks == null) return;
            foreach (var entry in outputMixers)
            {
                int index = entry.Key;
                if (index < 0 || index >= song.tracks.Count || entry.Value == null ||
                    index >= HelmTrackGainParameters.Length) continue;
                string exposed = HelmTrackGainParameters[index];
                if (string.IsNullOrEmpty(exposed)) continue;
                var track = song.tracks[index];
                float gain = track != null && TrackAudible(index) ? Mathf.Clamp01(track.volume) : 0f;
                // 100%=0dB, 50%=-6dB, 0%=-80dB. Changes affect held voices.
                float decibels = gain <= 0.0001f ? -80f : Mathf.Clamp(20f * Mathf.Log10(gain), -80f, 0f);
                if (!entry.Value.SetFloat(exposed, decibels) && verbose)
                    Debug.LogWarning("[ZPunk MIX] Missing AudioMixer exposed parameter: " + exposed);
            }
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
                    controller.NoteOn(e.value, Mathf.Clamp01(e.velocity / 127f));
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
            exportingOneShot = false;
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
