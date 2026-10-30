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
    /// DSP-anchored arrangement transport. Sample events use Unity PlayScheduled;
    /// melodic tracks use Helm's native audio-thread sequencers (when available).
    /// Update() handles UI/cursor and feeds the sample lookahead, not note timing.
    /// </summary>
    [DefaultExecutionOrder(250)]
    public sealed class ZPunkSongTransport : MonoBehaviour
    {
        [SerializeField, Range(0.12f, 0.50f)] private float audioLookahead = 0.32f;
        [SerializeField, Range(0.12f, 0.65f)] private float startDelay = 0.38f;
        [SerializeField] private bool verbose;
        // Only used when native Helm sequencing is unavailable. Native notes
        // are placed on Helm's audio clock instead of being sent from Update.
        private const double SamePitchRetriggerReleaseLead = 0.025;
        // Android devices running at 30 FPS may dispatch a DSP lookahead frame
        // late. Recover a modestly late sample event rather than dropping it.
        // Larger hitches are still discarded to avoid a machine-gun catch-up.
        private const double MaxRecoverableSampleLateness = 0.065;
        private double minSampleScheduleLead = 0.012;
        private readonly List<IntPtr> nativeMidiSequencers = new List<IntPtr>();
        private readonly Dictionary<IntPtr, int> nativeMidiTracks = new Dictionary<IntPtr, int>();
        private readonly Dictionary<IntPtr, bool> nativeMidiEnabled = new Dictionary<IntPtr, bool>();
        private bool nativeMidiActive;
        private bool activeLoopEnabled;

        private struct MidiSpan
        {
            public int track, pitch, velocity;
            public long start, end;
        }
        private struct PendingMidi
        {
            public long start;
            public int velocity;
        }

        private int lateMidiEvents;
        private int recoveredSampleEvents;
        private int droppedSampleEvents;
        private float nextTimingReport;

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
        // A real-time export is transport playback, but must not drive the editor viewport.
        public bool IsExportingOneShot => exportingOneShot;
        public long CurrentTick { get; private set; }
        public double SongAnchorDsp => anchorDsp;

        private void Resolve()
        {
            if (drums == null) drums = GetComponent<KMusicDrumSequencer>();
            if (projects == null) projects = FindObjectOfType<KMusicProjectManager>();
            if (app == null) app = FindObjectOfType<KMusicApp>();
            if (clock == null) clock = FindObjectOfType<AudioHelmClock>();
        }

        // Build actual note spans once, including durations and overlapping-pitch
        // pairing. NoteOff-before-NoteOn ordering is preserved at clip joins.
        private static List<MidiSpan> BuildMidiSpans(SongEventPlanner.Plan source)
        {
            var spans = new List<MidiSpan>();
            var pending = new Dictionary<int, Queue<PendingMidi>>();
            foreach (var e in source.midi)
            {
                int key = e.trackIndex * 128 + e.value;
                if (!pending.TryGetValue(key, out var notes))
                    pending[key] = notes = new Queue<PendingMidi>();
                if (e.type == SongEventPlanner.EventType.NoteOn)
                    notes.Enqueue(new PendingMidi { start = e.tick, velocity = e.velocity });
                else if (notes.Count > 0)
                {
                    var on = notes.Dequeue();
                    if (e.tick > on.start)
                        spans.Add(new MidiSpan { track = e.trackIndex, pitch = e.value,
                            velocity = on.velocity, start = on.start, end = e.tick });
                }
            }
            // Malformed/unclosed notes cannot hang the native synth indefinitely.
            foreach (var item in pending)
                foreach (var on in item.Value)
                    if (source.lengthTicks > on.start)
                        spans.Add(new MidiSpan { track = item.Key / 128, pitch = item.Key % 128,
                            velocity = on.velocity, start = on.start, end = source.lengthTicks });
            spans.Sort((a, b) => {
                int c = a.track.CompareTo(b.track);
                if (c != 0) return c;
                c = a.pitch.CompareTo(b.pitch);
                return c != 0 ? c : a.start.CompareTo(b.start);
            });
            // Native Helm pitch gates cannot represent multiple independently held
            // voices of the exact same pitch/channel. End the preceding voice on
            // the next retrigger, without affecting other simultaneous pitches.
            for (int i = 0; i + 1 < spans.Count; i++)
            {
                MidiSpan prior = spans[i], following = spans[i + 1];
                if (prior.track == following.track && prior.pitch == following.pitch &&
                    prior.end > following.start)
                {
                    prior.end = following.start;
                    spans[i] = prior;
                }
            }
            return spans;
        }

        private void AddNativeSegment(List<MidiSpan> spans, long rangeStart,
            long rangeEnd, double startBeat, bool looping)
        {
            if (rangeEnd <= rangeStart) return;
            double durationSixteenths = (double)(rangeEnd - rangeStart) / SongData.TicksPerSixteenth;
            // A native Helm sequencer is an audio-thread event engine, separate
            // from the UI/inspector sequencers disabled in arrangement mode.
            foreach (var controllerEntry in trackControllers)
            {
                if (controllerEntry.Value == null) continue;
                int track = controllerEntry.Key;
                bool hasNotes = false;
                foreach (var span in spans)
                    if (span.track == track && span.start < rangeEnd && span.end > rangeStart)
                    { hasNotes = true; break; }
                if (!hasNotes) continue;
                IntPtr seq = AudioHelm.Native.CreateSequencer();
                if (seq == IntPtr.Zero) throw new InvalidOperationException("Helm native sequencer allocation failed.");
                nativeMidiSequencers.Add(seq); // own immediately, including exception paths
                nativeMidiTracks[seq] = track;
                nativeMidiEnabled[seq] = false;
                AudioHelm.Native.EnableSequencer(seq, false); // never sound during construction
                // Helm returns a bool, but the stock wrapper does not interpret
                // it as a success code (unchanged/default channels may return false).
                AudioHelm.Native.ChangeSequencerChannel(seq, controllerEntry.Value.channel);
                AudioHelm.Native.ChangeSequencerLength(seq, (float)durationSixteenths);
                AudioHelm.Native.LoopSequencer(seq, looping);
                AudioHelm.Native.SetSequencerStart(seq, startBeat);
                for (int spanIndex = 0; spanIndex < spans.Count; spanIndex++)
                {
                    var span = spans[spanIndex];
                    if (span.track != track || span.start >= rangeEnd || span.end <= rangeStart)
                        continue;
                    long localStart = Math.Max(rangeStart, span.start);
                    long localEnd = Math.Min(rangeEnd, span.end);
                    if (localEnd <= localStart) continue;
                    float from = (float)((double)(localStart - rangeStart) / SongData.TicksPerSixteenth);
                    float to = (float)((double)(localEnd - rangeStart) / SongData.TicksPerSixteenth);
                    // Ensure native NoteOff is inside the pattern, and separate a
                    // same-pitch release/retrigger by ~1 ms rather than 25 ms.
                    float epsilon = Mathf.Clamp(bpm / 15000f, 0.001f, 0.02f);
                    bool touchingNext = spanIndex + 1 < spans.Count &&
                        spans[spanIndex + 1].track == span.track &&
                        spans[spanIndex + 1].pitch == span.pitch &&
                        spans[spanIndex + 1].start == localEnd;
                    if (localEnd == rangeEnd || touchingNext)
                        to = Mathf.Max(from + 0.0001f, to - epsilon);
                    if (AudioHelm.Native.CreateNote(seq, span.pitch,
                        Mathf.Clamp01(span.velocity / 127f), from, to) == IntPtr.Zero)
                        throw new InvalidOperationException("Helm native MIDI note allocation failed.");
                }
            }
        }

        private bool PrepareNativeMidi(double nativeOriginBeat = 0.0, long midiStartTick = -1)
        {
            DisposeNativeMidi();
            if (clock == null || plan == null || plan.midi.Count == 0) return false;
            try
            {
                if (midiStartTick < 0) midiStartTick = startTick;
                var spans = BuildMidiSpans(plan);
                if (song.arrangementLoopEnabled && midiStartTick != loopStart)
                {
                    // The first (partial) traversal is one-shot; all subsequent
                    // traversals use a separately prepared, perfectly periodic loop.
                    AddNativeSegment(spans, midiStartTick, loopEnd, nativeOriginBeat, false);
                    double firstBeats = (double)(loopEnd - midiStartTick) / SongData.TicksPerQuarter;
                    AddNativeSegment(spans, loopStart, loopEnd, nativeOriginBeat + firstBeats, true);
                }
                else
                    AddNativeSegment(spans, midiStartTick, loopEnd, nativeOriginBeat, song.arrangementLoopEnabled);
                return nativeMidiSequencers.Count > 0;
            }
            catch (Exception ex)
            {
                Debug.LogError("[ZPUNK TIMING] Native MIDI setup failed; using frame-timed fallback: " + ex);
                DisposeNativeMidi();
                return false;
            }
        }

        private void SyncNativeMidiAudibility()
        {
            if (!nativeMidiActive) return;
            var stoppedChannels = new HashSet<int>();
            foreach (IntPtr seq in nativeMidiSequencers)
            {
                if (!nativeMidiTracks.TryGetValue(seq, out int track)) continue;
                bool shouldEnable = TrackAudible(track);
                bool wasEnabled = nativeMidiEnabled.TryGetValue(seq, out bool previous) && previous;
                if (shouldEnable == wasEnabled) continue;
                AudioHelm.Native.EnableSequencer(seq, shouldEnable);
                nativeMidiEnabled[seq] = shouldEnable;
                if (!shouldEnable && trackControllers.TryGetValue(track, out HelmController ctl) &&
                    ctl != null && stoppedChannels.Add(ctl.channel))
                    ctl.AllNotesOff(); // never leak muted/isolated tracks into stem exports
            }
        }

        private void EnableNativeMidi()
        {
            try
            {
                nativeMidiActive = nativeMidiSequencers.Count > 0;
                SyncNativeMidiAudibility();
            }
            catch (Exception ex)
            {
                Debug.LogError("[ZPUNK TIMING] Native MIDI could not start; using fallback: " + ex);
                DisposeNativeMidi();
            }
        }

        private void DisposeNativeMidi()
        {
            nativeMidiActive = false;
            foreach (IntPtr seq in nativeMidiSequencers)
            {
                if (seq == IntPtr.Zero) continue;
                try { AudioHelm.Native.EnableSequencer(seq, false); }
                catch (Exception ex) { if (verbose) Debug.LogWarning("Helm disable: " + ex.Message); }
                try { AudioHelm.Native.DeleteSequencer(seq); }
                catch (Exception ex) { Debug.LogWarning("[ZPUNK TIMING] Helm cleanup: " + ex.Message); }
            }
            nativeMidiSequencers.Clear();
            nativeMidiTracks.Clear();
            nativeMidiEnabled.Clear();
        }

        private void ConfigureScheduleBuffer()
        {
            AudioSettings.GetDSPBufferSize(out int size, out int buffers);
            int rate = Math.Max(8000, AudioSettings.outputSampleRate);
            minSampleScheduleLead = Math.Max(0.012, 2.0 * size / rate);
            // Older scenes serialize the previous 0.10/0.18 s lookahead.
            // Enforce a safer floor for 16th notes on busy mobile frames.
            // The extra lead is also accounted for when sizing drum pools.
            audioLookahead = Mathf.Max(audioLookahead, 0.32f, (float)Math.Min(0.5,
                minSampleScheduleLead * 2.0 + 0.12));
        }

        private long ExactPlayheadTick(double now)
        {
            return (long)Math.Max(0.0, Math.Floor(now < anchorDsp
                ? startTick : startTick + (now - anchorDsp) / secondsPerTick));
        }

        // Returns the Helm beat that coincides with our current DSP anchor;
        // unlike restarting the native clock, this preserves all other tracks.
        private double NativeBeatAtAnchor()
        {
            return AudioHelmClock.GetGlobalBeatTime() +
                AudioHelmClock.GetGlobalBpm() *
                (anchorDsp - AudioHelmClock.GetLastSampledTime()) / 60.0;
        }

        private void ResetFutureSamples(long atTick)
        {
            if (drums != null) drums.CancelFutureDAWVoices();
            scheduledSamples.Clear();
            sampleCursor = 0;
            prefetchedLoopSamples = 0;
            cycle = 0;
            while (sampleCursor < plan.samples.Count && plan.samples[sampleCursor].tick < atTick)
                sampleCursor++;
        }

        private static Dictionary<int, SongEventPlanner.Event> HeldMidiNotes(
            SongEventPlanner.Plan source, long atTick)
        {
            var held = new Dictionary<int, SongEventPlanner.Event>();
            if (source == null) return held;
            foreach (var e in source.midi)
            {
                if (e.tick >= atTick) break;
                int key = e.trackIndex * 128 + e.value;
                if (e.type == SongEventPlanner.EventType.NoteOn) held[key] = e;
                else held.Remove(key);
            }
            return held;
        }

        private void RestoreHeldMidi(long atTick)
        {
            foreach (var entry in HeldMidiNotes(plan, atTick).Values)
                if (TrackAudible(entry.trackIndex) &&
                    trackControllers.TryGetValue(entry.trackIndex, out HelmController controller) &&
                    controller != null)
                    controller.NoteOn(entry.value, Mathf.Clamp01(entry.velocity / 127f));
        }

        private static bool MidiPlanEquals(SongEventPlanner.Plan a, SongEventPlanner.Plan b)
        {
            if (a == null || b == null || a.midi.Count != b.midi.Count) return false;
            for (int i = 0; i < a.midi.Count; i++)
            {
                var x = a.midi[i]; var y = b.midi[i];
                if (x.tick != y.tick || x.trackIndex != y.trackIndex || x.type != y.type ||
                    x.value != y.value || x.velocity != y.velocity) return false;
            }
            return true;
        }

        private void RefreshNativeMidiWithoutStoppingSamples(long position)
        {
            // Native sequencers share the clock but not the PCM scheduler. Rebuild
            // MIDI at the *current absolute beat*; don't pause/restart the song.
            double newStartBeat = NativeBeatAtAnchor() +
                (double)(position - startTick) / SongData.TicksPerQuarter;
            DisposeNativeMidi();
            StopAllSynthNotes();
            midiCursor = 0;
            while (midiCursor < plan.midi.Count && plan.midi[midiCursor].tick < position)
                midiCursor++;
            bool nativeReady = PrepareNativeMidi(newStartBeat, position);
            if (nativeReady) EnableNativeMidi();
            // Only currently sounding MIDI voices are re-articulated. Future
            // notes resume on Helm's native beat grid without a frame-rate gap.
            RestoreHeldMidi(position);
        }

        /// <summary>Changing the loop so it no longer contains the playhead
        /// requires a deliberate seek. Otherwise editing never restarts PCM.</summary>
        private void SeekLive(long newTick)
        {
            // Unlike StopSong/PlaySong, this preserves the edited in-memory
            // arrangement and never reloads an older project snapshot.
            newTick = Math.Max(0L, Math.Min(loopEnd - 1, newTick));
            DisposeNativeMidi();
            StopAllSynthNotes();
            if (drums != null) drums.StopDAWVoices();
            startTick = newTick;
            CurrentTick = newTick;
            sampleCursor = midiCursor = 0;
            prefetchedLoopSamples = 0;
            scheduledSamples.Clear();
            cycle = 0;
            SkipBefore(newTick);
            ConfigureScheduleBuffer();
            if (drums != null)
                drums.PrepareDAWPlayback(plan.samples.Exists(e => e.type == SongEventPlanner.EventType.Chop),
                    plan.samples, bpm, song.arrangementLoopEnabled ? loopStart : 0,
                    song.arrangementLoopEnabled ? loopEnd : 0, audioLookahead);
            bool nativeReady = PrepareNativeMidi();
            anchorDsp = AudioSettings.dspTime + Math.Max(startDelay, audioLookahead + 0.04f);
            if (clock != null)
            {
                clock.bpm = bpm;
                clock.pause = false;
                clock.StartScheduled(anchorDsp);
            }
            if (nativeReady) EnableNativeMidi();
            liveRevision++;
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
            activeLoopEnabled = song.arrangementLoopEnabled;
            startTick = Math.Max(0L, Math.Min(length - 1, fromTick));
            if (song.arrangementLoopEnabled && startTick >= loopEnd) startTick = loopStart;
            // Initialize pools and load the applied chop before starting the clock;
            // no GameObject creation or sample IO is allowed at the first downbeat.
            ConfigureScheduleBuffer();
            drums.PrepareDAWPlayback(plan.samples.Exists(e => e.type == SongEventPlanner.EventType.Chop),
                    plan.samples, bpm, song.arrangementLoopEnabled ? loopStart : 0,
                    song.arrangementLoopEnabled ? loopEnd : 0, audioLookahead);
            bool nativeReady = PrepareNativeMidi();
            anchorDsp = AudioSettings.dspTime + Math.Max(startDelay, audioLookahead + 0.04f);
            sampleCursor = 0;
            midiCursor = 0;
            cycle = 0;
            scheduledSamples.Clear();
            prefetchedLoopSamples = 0;
            lateMidiEvents = 0;
            recoveredSampleEvents = 0;
            droppedSampleEvents = 0;
            nextTimingReport = Time.unscaledTime + 8f;
            paused = false;
            CurrentTick = startTick;
            drums.SetDAWPlayheadStep(-1);
            SkipBefore(startTick);
            if (clock != null)
            {
                clock.bpm = bpm;
                clock.pause = false; // Keep native synth output alive; editor sequencers stay disabled.
                clock.StartScheduled(anchorDsp); // Native MIDI zero-beat matches PlayScheduled PCM time.
            }
            if (nativeReady) EnableNativeMidi();
            playing = true;
            if (verbose)
                Debug.Log($"[ZPunk DAW] PLAY events: {plan.samples.Count} samples, {plan.midi.Count} MIDI, from tick {startTick}");
            return true;
        }

        /// <summary>Change tempo with a continuous fractional-tick phase. Native
        /// Helm notes follow BPM changes; queued PCM starts are rescheduled.</summary>
        public void SetLiveTempo(float nextBpm, SongData updated)
        {
            if (exportingOneShot) return;
            if (updated != null) song = updated;
            if (!playing || plan == null) return;
            double now = AudioSettings.dspTime;
            bool beforeStart = now < anchorDsp;
            double exactTick = beforeStart ? startTick :
                startTick + (now - anchorDsp) / secondsPerTick;
            bpm = Mathf.Clamp(nextBpm, 1f, 400f);
            secondsPerTick = 60.0 / bpm / SongData.TicksPerQuarter;
            if (!beforeStart)
                anchorDsp = now - (exactTick - startTick) * secondsPerTick;
            if (clock != null) clock.bpm = bpm;
            ResetFutureSamples((long)Math.Max(0.0, Math.Floor(exactTick)));
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
            if (nativeMidiActive) SyncNativeMidiAudibility();
        }

        public void ApplyLiveSong(SongData updated)
        {
            if (exportingOneShot || updated == null) return;
            Resolve();
            if (!playing || plan == null)
            {
                song = updated;
                if (trackControllers.Count == 0) ResolveControllers();
                ApplyAllTrackGains();
                return;
            }
            SongEventPlanner.Plan next;
            try { next = SongEventPlanner.Build(updated); }
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
            bool midiChanged = !MidiPlanEquals(plan, next);
            bool tempoChanged = Math.Abs(updated.bpm - bpm) > .01f;
            long oldLoopStart = loopStart, oldLoopEnd = loopEnd;
            bool oldLoopEnabled = activeLoopEnabled;
            song = updated;
            plan = next;
            bpm = Mathf.Clamp(song.bpm, 1f, 400f);
            if (tempoChanged)
            {
                secondsPerTick = 60.0 / bpm / SongData.TicksPerQuarter;
                if (!beforeStart)
                    anchorDsp = now - (exactTick - startTick) * secondsPerTick;
                if (clock != null) clock.bpm = bpm;
            }
            long length = Math.Max(1L, plan.lengthTicks);
            loopStart = song.arrangementLoopEnabled ? Math.Max(0L, Math.Min(length - 1, song.loopStartTick)) : 0;
            loopEnd = song.arrangementLoopEnabled ? Math.Max(loopStart + 1, Math.Min(length, song.loopEndTick)) : length;
            bool loopChanged = oldLoopStart != loopStart || oldLoopEnd != loopEnd ||
                oldLoopEnabled != song.arrangementLoopEnabled;
            activeLoopEnabled = song.arrangementLoopEnabled;
            // A live edit can add many hats or extend a clip. Size pools before
            // the updated pattern enters the DSP scheduling window.
            if (drums != null)
                drums.PrepareDAWPlayback(false, plan.samples, bpm,
                    activeLoopEnabled ? loopStart : 0,
                    activeLoopEnabled ? loopEnd : 0, audioLookahead);
            ResolveControllers();
            ApplyAllTrackGains();
            if (nativeMidiActive) SyncNativeMidiAudibility();
            if (position >= loopEnd)
            {
                SeekLive(loopStart);
                return;
            }
            ResetFutureSamples(position);
            // Merely editing drums, chops or mixer state never kills synths.
            // Rebuild Helm only when MIDI notes or the loop structure changed.
            if (midiChanged || loopChanged)
                RefreshNativeMidiWithoutStoppingSamples(position);
            else
            {
                midiCursor = 0;
                while (midiCursor < plan.midi.Count && plan.midi[midiCursor].tick < position)
                    midiCursor++;
            }
            liveRevision++;
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
            if (drums != null && song.tracks.Count > 0 && song.tracks[0] != null && song.tracks[0].instrument != null)
                drums.ApplyDAWDrumDesign(song.tracks[0].instrument.drumDesign);
            ZPunkMasterAudioTap.ApplySongSettings(song);
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

            // The native Helm audio thread advances all melodic notes independently
            // of Unity FPS. Fallback is used only when native setup failed.
            if (!nativeMidiActive) DispatchDueMidi(now);
            if (Time.unscaledTime >= nextTimingReport)
            {
                // An audible lost hit is always worth reporting, even in a
                // release build with verbose timing diagnostics disabled.
                if (droppedSampleEvents > 0 ||
                    (verbose && (lateMidiEvents > 0 || recoveredSampleEvents > 0)))
                    Debug.LogWarning($"[ZPUNK TIMING] Last 8s: MIDI >20ms late={lateMidiEvents}, " +
                        $"sample hits recovered={recoveredSampleEvents}, sample hits dropped={droppedSampleEvents}. " +
                        "Check main-thread scheduling, missing kit clips or busy sample voices.");
                lateMidiEvents = 0;
                recoveredSampleEvents = 0;
                droppedSampleEvents = 0;
                nextTimingReport = Time.unscaledTime + 8f;
            }


            if (now >= endDsp)
            {
                // Native loop voices are already scheduled through this boundary.
                // Do not issue AllNotesOff at the frame boundary (causes a gap).
                if (!nativeMidiActive) StopAllSynthNotes();
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
                    // Legacy fallback only; native MIDI already crossed the boundary.
                    if (!nativeMidiActive) DispatchDueMidi(now);
                }
                else StopSong();
            }
        }

        // Fallback for platforms without native Helm scheduling.
        // A touching same-pitch repeat has NoteOff and NoteOn at the same tick.
        // Sending both native commands in a single Unity frame can collapse the
        // audible retrigger, especially on a 30 FPS phone. Give the release a
        // short head start without changing stored song/MIDI or adjacent pitches.
        private bool HasSamePitchRetriggerAtTick(int eventIndex)
        {
            var off = plan.midi[eventIndex];
            if (off.type != SongEventPlanner.EventType.NoteOff) return false;
            for (int i = eventIndex + 1; i < plan.midi.Count; i++)
            {
                var next = plan.midi[i];
                if (next.tick != off.tick) break;
                if (next.type == SongEventPlanner.EventType.NoteOn &&
                    next.trackIndex == off.trackIndex && next.value == off.value)
                    return true;
            }
            return false;
        }

        private void DispatchDueMidi(double now)
        {
            while (midiCursor < plan.midi.Count)
            {
                var e = plan.midi[midiCursor];
                if (e.tick >= loopEnd) break;
                double due = DspAt(e.tick);
                // Only anticipate NOTE OFF where a new note of the same pitch
                // starts at precisely that tick. Other note durations are intact.
                bool earlyRelease = due > now && due <= now + SamePitchRetriggerReleaseLead &&
                    HasSamePitchRetriggerAtTick(midiCursor);
                if (due > now && !earlyRelease) break;
                midiCursor++;
                if (due < now - 0.020) lateMidiEvents++;
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
            if (!TrackAudible(e.trackIndex)) return;
            double lateness = now - target;
            if (lateness > MaxRecoverableSampleLateness)
            {
                droppedSampleEvents++;
                return; // Too old: playing it now would cause an obvious flam.
            }
            var key = new ScheduledSampleKey(eventCycle, e);
            if (!scheduledSamples.Add(key)) return;
            // Track volume already runs through ApplyDAWTrackMix's PCM gain stage.
            // The AudioSource must receive only note velocity, never track gain twice.
            float velocity = Mathf.Clamp01(e.velocity / 127f);
            // Give late events at least one scheduling buffer of lead time.
            // On-time events retain their exact DSP timestamps.
            double scheduledAt = lateness > 0.0
                ? now + minSampleScheduleLead : Math.Max(now + minSampleScheduleLead, target);
            if (lateness > 0.020) recoveredSampleEvents++;
            if (e.type == SongEventPlanner.EventType.Drum)
            {
                if (drums == null || !drums.ScheduleDAWDrum(e.value, scheduledAt, velocity))
                {
                    droppedSampleEvents++;
                    if (verbose) Debug.LogWarning($"[ZPUNK TIMING] Drum lane {e.value + 1} " +
                        "could not schedule: missing kit sample or all sample voices busy.");
                }
            }
            else if (e.type == SongEventPlanner.EventType.Chop && drums != null)
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
            activeLoopEnabled = false;
            meterNoteVelocities.Clear();
            meterEnvelope.Clear();
            meterLastTime.Clear();
            exportingOneShot = false;
            isolatedExportTrack = -1;
            pendingExportSnapshot = null;
            paused = false;
            DisposeNativeMidi();
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
