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
        // Helm's native mixer is not exposed as a reliable Unity AudioSource meter.
        // Drive the channel indicators from the MIDI events we actually dispatch,
        // rather than treating the synth plugin's constant callback data as signal.
        private readonly Dictionary<int, Dictionary<int, float>> meterNoteVelocities =
            new Dictionary<int, Dictionary<int, float>>();
        private readonly Dictionary<int, float> meterEnvelope = new Dictionary<int, float>();
        private readonly Dictionary<int, float> meterLastTime = new Dictionary<int, float>();
        private readonly Dictionary<int, AudioMixer> outputMixers = new Dictionary<int, AudioMixer>();
        private readonly Dictionary<int, ZPunkHelmPcmMeter> pcmMeters = new Dictionary<int, ZPunkHelmPcmMeter>();
        private static readonly string[] HelmTrackGainParameters = { "", "ZPunkBassTrackGain", "ZPunkSynthTrackGain", "ZPunkPadsTrackGain", "" };
        private SongData song;
        private SongEventPlanner.Plan plan;
        private Label statusLabel;
        private AudioHelmClock clock;
        private bool songMode;
        private bool playing;
        private bool paused;
        private bool exportingOneShot;
        private SongData pendingExportSnapshot;
        private int isolatedExportTrack = -1;
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
        // An arrangement edit rebuilds the event plan while earlier audio starts may
        // already be waiting inside Unity's DSP scheduler. Remember committed starts
        // so rebuilding a plan cannot double-trigger the same boundary hit.
        private struct ScheduledSampleKey : IEquatable<ScheduledSampleKey>
        {
            public long cycleId;
            public long tick;
            public int track;
            public SongEventPlanner.EventType type;
            public int value;

            public ScheduledSampleKey(long cycleId, SongEventPlanner.Event e)
            {
                this.cycleId = cycleId;
                tick = e.tick;
                track = e.trackIndex;
                type = e.type;
                value = e.value;
            }

            public bool Equals(ScheduledSampleKey other)
            {
                return cycleId == other.cycleId && tick == other.tick &&
                    track == other.track && type == other.type && value == other.value;
            }

            public override bool Equals(object obj)
            {
                return obj is ScheduledSampleKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = cycleId.GetHashCode();
                    hash = (hash * 397) ^ tick.GetHashCode();
                    hash = (hash * 397) ^ track;
                    hash = (hash * 397) ^ (int)type;
                    return (hash * 397) ^ value;
                }
            }
        }
        private readonly HashSet<ScheduledSampleKey> scheduledSamples =
            new HashSet<ScheduledSampleKey>();
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

        /// <summary>Play an immutable snapshot with one track isolated, or -1 for the master.
        /// Overrides solo/mute only for isolated stems; the master keeps the real mix.</summary>
        public bool PlayExportSnapshot(SongData snapshot, int trackIndex)
        {
            if (snapshot == null || trackIndex < -1 || trackIndex >= 5)
                throw new ArgumentException("Invalid song snapshot or stem index.");
            pendingExportSnapshot = SongData.FromJson(snapshot.ToJson());
            isolatedExportTrack = trackIndex;
            try
            {
                bool success = PlaySong(0, true);
                if (!success) StopSong();
                return success;
            }
            catch
            {
                StopSong(); // Restore original gains even after a partial export start.
                throw;
            }
        }

        public bool PlaySong(long fromTick = -1, bool oneShotExport = false)
        {
            Resolve();
            // Normal PLAY starts from a manually scrubbed arrangement head.
            // Explicit callers (exports, resume, restart) retain their ticks.
            if (fromTick < 0L) fromTick = playing ? 0L : CurrentTick;
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
            try { song = pendingExportSnapshot != null && oneShotExport
                ? pendingExportSnapshot : projects.GetSongForPlayback();
                pendingExportSnapshot = null; }
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
            exportingOneShot = oneShotExport;
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
            scheduledSamples.Clear();
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
            // Preserve the fractional musical phase, not the last displayed whole
            // tick (which can lag by one 30-fps mobile frame).
            bool beforeStart = now < anchorDsp;
            double exactTick = beforeStart ? startTick :
                startTick + (now - anchorDsp) / secondsPerTick;
            long atTick = (long)Math.Max(0.0, Math.Floor(exactTick));
            bpm = Mathf.Clamp(nextBpm, 1f, 400f);
            secondsPerTick = 60.0 / bpm / SongData.TicksPerQuarter;
            if (!beforeStart)
                anchorDsp = now - (exactTick - startTick) * secondsPerTick;
            if (clock != null) clock.bpm = bpm;
            // Replay the scheduler's lookahead against the new tempo. Already queued
            // events retain their old DSP time and are deduplicated by identity;
            // previously *unscheduled* events must not be discarded wholesale.
            sampleCursor = 0;
            while (plan != null && sampleCursor < plan.samples.Count &&
                   plan.samples[sampleCursor].tick < atTick) sampleCursor++;
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
            bool beforeStart = now < anchorDsp;
            double exactTick = beforeStart ? startTick :
                startTick + (now - anchorDsp) / secondsPerTick;
            long position = (long)Math.Max(0.0, Math.Floor(exactTick));
            if (Math.Abs(song.bpm - bpm) > .01f)
            {
                bpm = Mathf.Clamp(song.bpm, 1f, 400f);
                secondsPerTick = 60.0 / bpm / SongData.TicksPerQuarter;
                // Fractional-tick phase continuity avoids a frame-sized tempo jump.
                if (!beforeStart)
                    anchorDsp = now - (exactTick - startTick) * secondsPerTick;
                if (clock != null) clock.bpm = bpm;
            }
            // A future-only edit (especially DUP) must not retrigger notes already
            // sounding. Compare the actual held pitches at the current tick.
            bool heldNotesUnchanged = beforeStart || SameHeldNotes(plan, next, position);
            if (heldNotesUnchanged && !beforeStart)
            {
                // The plan can be identical while a mute/solo edit changes which
                // held voices should actually sound. The meter tracks real NoteOn
                // dispatches, so detect notes that need starting or stopping.
                foreach (var held in HeldMidiNotes(next, position).Values)
                {
                    bool shouldSound = TrackAudible(held.trackIndex);
                    bool isSounding = meterNoteVelocities.TryGetValue(held.trackIndex, out var notes) &&
                        notes.ContainsKey(held.value);
                    if (shouldSound != isSounding) { heldNotesUnchanged = false; break; }
                }
            }
            plan = next;
            long length = Math.Max(1L, plan.lengthTicks);
            loopStart = song.arrangementLoopEnabled ? Math.Max(0L, Math.Min(length - 1, song.loopStartTick)) : 0;
            loopEnd = song.arrangementLoopEnabled ? Math.Max(loopStart + 1, Math.Min(length, song.loopEndTick)) : length;
            sampleCursor = 0;
            midiCursor = 0;
            prefetchedLoopSamples = 0;
            // Skip events genuinely in the past, not every event in the lookahead.
            // The scheduledSamples set prevents duplicate DSP starts after rebuilding.
            while (sampleCursor < plan.samples.Count && plan.samples[sampleCursor].tick < position)
                sampleCursor++;
            while (midiCursor < plan.midi.Count && plan.midi[midiCursor].tick < position)
                midiCursor++;
            ResolveControllers();
            if (!heldNotesUnchanged)
            {
                // Only an edit affecting sounding MIDI voices warrants a release.
                StopAllSynthNotes();
                var held = HeldMidiNotes(plan, position);
                foreach (var entry in held.Values)
                {
                    if (TrackAudible(entry.trackIndex) &&
                        trackControllers.TryGetValue(entry.trackIndex, out HelmController active) && active != null)
                    {
                        active.NoteOn(entry.value, Mathf.Clamp01(entry.velocity / 127f));
                        MeterNoteOn(entry.trackIndex, entry.value, entry.velocity / 127f);
                    }
                }
            }
            liveRevision++;
            // If the loop is shortened past the playhead, jump to its beginning.
            if (position >= loopEnd) SeekLive(loopStart);
        }

        private static Dictionary<int, SongEventPlanner.Event> HeldMidiNotes(
            SongEventPlanner.Plan source, long atTick)
        {
            var held = new Dictionary<int, SongEventPlanner.Event>();
            if (source == null) return held;
            foreach (var e in source.midi)
            {
                if (e.tick > atTick) break;
                int id = e.trackIndex * 128 + e.value;
                if (e.type == SongEventPlanner.EventType.NoteOn) held[id] = e;
                else held.Remove(id);
            }
            return held;
        }

        private static bool SameHeldNotes(SongEventPlanner.Plan original,
            SongEventPlanner.Plan updated, long atTick)
        {
            var before = HeldMidiNotes(original, atTick);
            var after = HeldMidiNotes(updated, atTick);
            if (before.Count != after.Count) return false;
            foreach (var item in before)
                if (!after.TryGetValue(item.Key, out var newer) ||
                    newer.velocity != item.Value.velocity || newer.tick != item.Value.tick)
                    return false;
            return true;
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
            InstallPcmMeters();
            foreach (var item in trackControllers)
            {
                var source = item.Value != null ? item.Value.GetComponent<AudioSource>() : null;
                if (source != null && source.outputAudioMixerGroup != null)
                    outputMixers[item.Key] = source.outputAudioMixerGroup.audioMixer;
                // Remove obsolete callbacks installed by earlier meter patches.
                // They do not reflect the native Helm synth output reliably.
                if (source != null)
                {
                    var oldTap = source.GetComponent<ZPunkHelmMeterTap>();
                    if (oldTap != null) Destroy(oldTap);
                }
            }
            if (synth == null) Debug.LogWarning("[ZPunk DAW] Main synth unavailable. Synth track will be silent.");
            if (pads == null) Debug.LogWarning("[ZPunk DAW] Pad synth unavailable. Pads track will be silent.");
        }

        private void InstallPcmMeters()
        {
            foreach (var item in trackControllers)
            {
                if (item.Value == null) continue;
                int index = item.Key;
                if (pcmMeters.TryGetValue(index, out var existing) && existing != null)
                {
                    existing.channel = item.Value.channel;
                    continue;
                }
                var monitor = new GameObject("ZPunk_Helm_PCM_Meter_" + index);
                monitor.transform.SetParent(transform, false);
                var source = monitor.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.spatialBlend = 0f;
                source.volume = 1f;
                // AudioClip drives OnAudioFilterRead. The meter zeros every buffer,
                // so this source adds no signal to the listener.
                var clip = AudioClip.Create("ZPunk meter clock", 256, 1,
                    Mathf.Max(8000, AudioSettings.outputSampleRate), false);
                source.clip = clip;
                source.loop = true;
                var meter = monitor.AddComponent<ZPunkHelmPcmMeter>();
                meter.channel = item.Value.channel;
                pcmMeters[index] = meter;
                source.Play();
            }
        }

        /// <summary>True mixer attenuation, independent of the current Helm preset and NoteOn velocity.</summary>
        private void ApplyAllTrackGains()
        {
            if (song == null || song.tracks == null) return;
            // Apply main DRUMS and CHOPS track gains at their actual sample PCM
            // stages too. The individual drum faders remain independent.
            if (drums != null && song.tracks.Count > 4)
                drums.ApplyDAWTrackMix(TrackAudible(0) ? Mathf.Clamp(song.tracks[0].volume, 0f, 2f) : 0f,
                                       TrackAudible(4) ? Mathf.Clamp(song.tracks[4].volume, 0f, 2f) : 0f);
            foreach (var entry in outputMixers)
            {
                int index = entry.Key;
                if (index < 0 || index >= song.tracks.Count || entry.Value == null ||
                    index >= HelmTrackGainParameters.Length) continue;
                string exposed = HelmTrackGainParameters[index];
                if (string.IsNullOrEmpty(exposed)) continue;
                var track = song.tracks[index];
                float gain = track != null && TrackAudible(index) ? Mathf.Clamp(track.volume, 0f, 2f) : 0f;
                // 100%=0dB, 50%=-6dB, 0%=-80dB. Changes affect held voices.
                float decibels = gain <= 0.0001f ? -80f : Mathf.Clamp(20f * Mathf.Log10(gain), -80f, 6f);
                if (!entry.Value.SetFloat(exposed, decibels) && verbose)
                    Debug.LogWarning("[ZPunk MIX] Missing AudioMixer exposed parameter: " + exposed);
            }
        }

        private void MeterNoteOn(int index, int note, float velocity)
        {
            if (!meterNoteVelocities.TryGetValue(index, out var notes))
                meterNoteVelocities[index] = notes = new Dictionary<int, float>();
            notes[note] = Mathf.Clamp01(velocity);
            // Each real NoteOn creates a fresh level transient. Held notes retain
            // their own velocity, so repeated notes produce visible meter movement.
            float level = Mathf.Clamp01(0.12f + 0.72f * Mathf.Clamp01(velocity));
            float current = meterEnvelope.TryGetValue(index, out float existing) ? existing : 0f;
            meterEnvelope[index] = Mathf.Max(current, level);
            meterLastTime[index] = Time.unscaledTime;
        }

        private void MeterNoteOff(int index, int note)
        {
            if (meterNoteVelocities.TryGetValue(index, out var notes)) notes.Remove(note);
        }

        public float GetTrackPeak(int index)
        {
            if (!playing || !TrackAudible(index)) return 0f;
            if (!pcmMeters.TryGetValue(index, out var meter) || meter == null || !meter.Available)
                return 0f; // No simulated readings: PCM measurement unavailable.
            float gain = song != null && song.tracks != null && index < song.tracks.Count
                && song.tracks[index] != null ? Mathf.Clamp(song.tracks[index].volume, 0f, 2f) : 0f;
            // Measured Helm PCM multiplied by the exposed AudioMixer attenuation.
            return Mathf.Max(0f, meter.Peak * gain);
        }

        private bool TrackAudible(int index)
        {
            if (song == null || song.tracks == null || index < 0 || index >= song.tracks.Count) return false;
            var track = song.tracks[index];
            if (exportingOneShot && isolatedExportTrack >= 0)
                return index == isolatedExportTrack && track != null && track.volume > 0;
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
                DispatchSample(e, target, now, cycle);
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
                    DispatchSample(e, target, now, cycle + 1);
                }
            }

            // Audio Helm dispatch is limited to Unity frame accuracy.
            DispatchDueMidi(now);

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
                    // Keep the just-prefetched cycle, discard identities from prior
                    // traversals so long-running arrangements do not accumulate them.
                    scheduledSamples.RemoveWhere(e => e.cycleId < cycle);
                    sampleCursor = prefetchedLoopSamples;
                    midiCursor = 0;
                    prefetchedLoopSamples = 0;
                    SkipBefore(loopStart);
                    CurrentTick = loopStart;
                    // Don't wait for another Update frame to fire boundary MIDI.
                    DispatchDueMidi(now);
                }
                else StopSong();
            }
        }

        private void DispatchDueMidi(double now)
        {
            while (midiCursor < plan.midi.Count)
            {
                var e = plan.midi[midiCursor];
                if (e.tick >= loopEnd || DspAt(e.tick) > now) break;
                midiCursor++;
                if (!trackControllers.TryGetValue(e.trackIndex, out HelmController controller) || controller == null)
                    continue;
                if (e.type == SongEventPlanner.EventType.NoteOff)
                {
                    controller.NoteOff(e.value);
                    MeterNoteOff(e.trackIndex, e.value);
                }
                else if (TrackAudible(e.trackIndex))
                {
                    controller.NoteOn(e.value, Mathf.Clamp01(e.velocity / 127f));
                    MeterNoteOn(e.trackIndex, e.value, e.velocity / 127f);
                }
            }
        }

        private void DispatchSample(SongEventPlanner.Event e, double target, double now, long eventCycle)
        {
            if (target < now - 0.02 || !TrackAudible(e.trackIndex)) return;
            var key = new ScheduledSampleKey(eventCycle, e);
            if (!scheduledSamples.Add(key)) return;
            TrackData track = song.tracks[e.trackIndex];
            float velocity = Mathf.Clamp01(e.velocity / 127f) * Mathf.Clamp01(track.volume);
            double scheduledAt = Math.Max(now + 0.005, target);
            if (e.type == SongEventPlanner.EventType.Drum)
                drums.ScheduleDAWDrum(e.value, scheduledAt, velocity);
            else if (e.type == SongEventPlanner.EventType.Chop)
                drums.ScheduleDAWChop(e.value, scheduledAt, velocity);
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
            // STOP can come from pointer-down and the following ClickEvent.
            // It must always silence native notes and cancel pre-scheduled sample
            // voices, even when a second stop arrives after playing is cleared.
            Resolve();
            bool wasExporting = exportingOneShot;
            playing = false;
            scheduledSamples.Clear();
            meterNoteVelocities.Clear();
            meterEnvelope.Clear();
            meterLastTime.Clear();
            exportingOneShot = false;
            isolatedExportTrack = -1;
            pendingExportSnapshot = null;
            paused = false;
            StopAllSynthNotes();
            if (drums != null)
            {
                drums.StopDAWVoices();
                drums.SetDAWPlayheadStep(-1);
            }
            if (clock != null) clock.pause = true;
            CurrentTick = 0;
            // Undo isolated mixer attenuation at the end of a bounce.
            if (wasExporting && projects != null)
            {
                try { song = projects.GetSongForPlayback(); ApplyAllTrackGains(); }
                catch (Exception e) { Debug.LogWarning("[ZPUNK Export] Mix restore: " + e.Message); }
            }
        }

        public void Seek(long tick)
        {
            if (!playing) { CurrentTick = Math.Max(0L, tick); return; }
            StopSong();
            PlaySong(tick);
        }

        private void StopAllSynthNotes()
        {
            meterNoteVelocities.Clear();
            meterEnvelope.Clear();
            meterLastTime.Clear();
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
            foreach (var meter in pcmMeters.Values)
                if (meter != null) Destroy(meter.gameObject);
            pcmMeters.Clear();
            StopSong();
            IsSongModeActive = false;
            foreach (HelmSequencer seq in suspendedSequencers)
                if (seq != null) seq.enabled = true;
            suspendedSequencers.Clear();
        }
    }
}
