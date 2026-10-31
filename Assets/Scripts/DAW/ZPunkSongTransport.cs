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

        // The three melodic tracks must remain audible while their piano rolls are
        // edited. Keep stable native sequencers and change their individual notes;
        // never rebuild the entire Helm instrument graph for an ordinary MIDI edit.
        private struct NativeNoteKey : IEquatable<NativeNoteKey>
        {
            public int pitch;
            public long start, end;
            public bool shortenedEnd;

            public bool Equals(NativeNoteKey other) => pitch == other.pitch &&
                start == other.start && end == other.end && shortenedEnd == other.shortenedEnd;
            public override bool Equals(object obj) => obj is NativeNoteKey other && Equals(other);
            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = pitch;
                    hash = (hash * 397) ^ start.GetHashCode();
                    hash = (hash * 397) ^ end.GetHashCode();
                    return (hash * 397) ^ (shortenedEnd ? 1 : 0);
                }
            }
        }

        private sealed class NativeNoteEntry
        {
            public NativeNoteKey key;
            public int velocity;
            public float from, to;
            public IntPtr handle;
        }

        private sealed class NativeMidiSegment
        {
            public IntPtr sequencer;
            public int track;
            public long rangeStart, rangeEnd;
            public double startBeat;
            public bool looping;
            public bool finished;
            public readonly List<NativeNoteEntry> notes = new List<NativeNoteEntry>();
        }

        private readonly List<NativeMidiSegment> nativeMidiSegments = new List<NativeMidiSegment>();

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
        // LOOP +/- is implemented as one quantized restart, not an overlapping
        // native sequencer handoff. Until the next beat the old length remains
        // displayed; the newly scheduled run owns all five tracks exclusively.
        private bool quantizedLoopRestartPending;
        private double quantizedLoopRestartDsp;
        private int previousLoopBarsDuringRestart;
        public int ActiveLoopBars => quantizedLoopRestartPending ?
            previousLoopBarsDuringRestart : (int)Math.Max(1L,
            (Math.Max(1L, loopEnd - loopStart) + SongData.TicksPerBar4 - 1) / SongData.TicksPerBar4);
        public bool LoopChangePending => playing && quantizedLoopRestartPending;
        public int PendingLoopBars => (int)Math.Max(1L,
            (Math.Max(1L, loopEnd - loopStart) + SongData.TicksPerBar4 - 1) / SongData.TicksPerBar4);
        // Kept for UI compatibility. The new path always schedules a restart.
        public bool LoopChangeNeedsRestart => false;

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
                if (c != 0) return c;
                c = a.start.CompareTo(b.start);
                return c != 0 ? c : a.end.CompareTo(b.end);
            });
            // Two overlapping arrangement clips may reference the SAME MIDI
            // content at the SAME tick. Do not play two identical NoteOns into
            // one monophonic Bass channel. This only deduplicates audio events;
            // it never edits clips, generated MIDI or the saved song document.
            int duplicateNotes = 0;
            for (int i = 1; i < spans.Count; )
            {
                MidiSpan a = spans[i - 1], b = spans[i];
                if (a.track == b.track && a.pitch == b.pitch &&
                    a.start == b.start && a.end == b.end)
                {
                    if (b.velocity > a.velocity)
                    {
                        a.velocity = b.velocity;
                        spans[i - 1] = a;
                    }
                    spans.RemoveAt(i);
                    duplicateNotes++;
                }
                else i++;
            }
            if (duplicateNotes > 0)
                Debug.LogWarning($"[ZPUNK MIDI] Suppressed {duplicateNotes} duplicate native " +
                    "note triggers from overlapping clips (song data unchanged).");
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

        private List<NativeNoteEntry> NativeNotesForSegment(
            List<MidiSpan> spans, int track, long rangeStart, long rangeEnd)
        {
            var result = new List<NativeNoteEntry>();
            for (int i = 0; i < spans.Count; i++)
            {
                MidiSpan span = spans[i];
                if (span.track != track || span.start >= rangeEnd || span.end <= rangeStart)
                    continue;
                long start = Math.Max(rangeStart, span.start);
                long end = Math.Min(rangeEnd, span.end);
                if (end <= start) continue;
                float from = (float)((double)(start - rangeStart) / SongData.TicksPerSixteenth);
                float to = (float)((double)(end - rangeStart) / SongData.TicksPerSixteenth);
                // As before, a note off at a clip join or at the end of a
                // native loop needs a small separation from its next note on.
                float epsilon = Mathf.Clamp(bpm / 15000f, 0.001f, 0.02f);
                bool touchingNext = i + 1 < spans.Count &&
                    spans[i + 1].track == track && spans[i + 1].pitch == span.pitch &&
                    spans[i + 1].start == end;
                bool shortened = end == rangeEnd || touchingNext;
                if (shortened) to = Mathf.Max(from + 0.0001f, to - epsilon);
                result.Add(new NativeNoteEntry
                {
                    key = new NativeNoteKey { pitch = span.pitch, start = start,
                        end = end, shortenedEnd = shortened },
                    velocity = span.velocity, from = from, to = to
                });
            }
            return result;
        }

        private void AddNativeSegment(List<MidiSpan> spans, long rangeStart,
            long rangeEnd, double startBeat, bool looping)
        {
            if (rangeEnd <= rangeStart) return;
            double durationSixteenths = (double)(rangeEnd - rangeStart) / SongData.TicksPerSixteenth;
            foreach (var controllerEntry in trackControllers)
            {
                if (controllerEntry.Value == null) continue;
                int track = controllerEntry.Key;
                // Intentionally allocate an empty pattern for every instrument.
                // Adding the *first* note to BASS/SYNTH/PADS should not need to
                // tear down the other instruments or restart the audio clock.
                IntPtr seq = AudioHelm.Native.CreateSequencer();
                if (seq == IntPtr.Zero) throw new InvalidOperationException("Helm native sequencer allocation failed.");
                nativeMidiSequencers.Add(seq);
                nativeMidiTracks[seq] = track;
                nativeMidiEnabled[seq] = false;
                var state = new NativeMidiSegment { sequencer = seq, track = track,
                    rangeStart = rangeStart, rangeEnd = rangeEnd,
                    startBeat = startBeat, looping = looping };
                nativeMidiSegments.Add(state);
                AudioHelm.Native.EnableSequencer(seq, false);
                AudioHelm.Native.ChangeSequencerChannel(seq, controllerEntry.Value.channel);
                AudioHelm.Native.ChangeSequencerLength(seq, (float)durationSixteenths);
                AudioHelm.Native.LoopSequencer(seq, looping);
                AudioHelm.Native.SetSequencerStart(seq, startBeat);
                foreach (var note in NativeNotesForSegment(spans, track, rangeStart, rangeEnd))
                {
                    note.handle = AudioHelm.Native.CreateNote(seq, note.key.pitch,
                        Mathf.Clamp01(note.velocity / 127f), note.from, note.to);
                    if (note.handle == IntPtr.Zero)
                        throw new InvalidOperationException("Helm native MIDI note allocation failed.");
                    state.notes.Add(note);
                }
            }
        }

        /// <summary>
        /// Modify the running native Helm notes in-place. Unchanged notes retain
        /// their pointers and (crucially) their *currently playing voices*.
        /// This fixes the full-instrument AllNotesOff/restart gap previously
        /// triggered by every piano-roll tap or drag.
        /// </summary>
        private bool UpdateNativeMidiInPlace(long playheadTick, bool retainChangedNoteEnds = false)
        {
            if (!nativeMidiActive || nativeMidiSegments.Count == 0) return false;
            try
            {
                var spans = BuildMidiSpans(plan);
                foreach (var segment in nativeMidiSegments)
                {
                    var desired = NativeNotesForSegment(spans, segment.track,
                        segment.rangeStart, segment.rangeEnd);
                    var existing = new Dictionary<NativeNoteKey, Queue<NativeNoteEntry>>();
                    foreach (var note in segment.notes)
                    {
                        if (!existing.TryGetValue(note.key, out var queue))
                            existing[note.key] = queue = new Queue<NativeNoteEntry>();
                        queue.Enqueue(note);
                    }
                    var keep = new List<NativeNoteEntry>(desired.Count);
                    var reused = new HashSet<NativeNoteEntry>();
                    foreach (var spec in desired)
                    {
                        NativeNoteEntry old = null;
                        if (existing.TryGetValue(spec.key, out var matches))
                            while (matches.Count > 0 && old == null)
                            {
                                var candidate = matches.Dequeue();
                                if (!reused.Contains(candidate)) old = candidate;
                            }
                        if (old == null && retainChangedNoteEnds)
                        {
                            // LOOP +/- can change a note's truncated END without
                            // changing the actual note-on. Edit that native note
                            // rather than deleting it and killing a held voice.
                            foreach (var candidate in segment.notes)
                                if (!reused.Contains(candidate) &&
                                    candidate.key.pitch == spec.key.pitch &&
                                    candidate.key.start == spec.key.start)
                                { old = candidate; break; }
                        }
                        if (old != null)
                        {
                            reused.Add(old);
                            if (old.from != spec.from)
                            {
                                IntPtr changed = AudioHelm.Native.ChangeNoteStart(
                                    segment.sequencer, old.handle, spec.from);
                                if (changed == IntPtr.Zero)
                                    throw new InvalidOperationException("Helm live note start update failed.");
                                old.handle = changed;
                            }
                            if (old.to != spec.to)
                            {
                                IntPtr changed = AudioHelm.Native.ChangeNoteEnd(
                                    segment.sequencer, old.handle, spec.to);
                                if (changed == IntPtr.Zero)
                                    throw new InvalidOperationException("Helm live note end update failed.");
                                old.handle = changed;
                            }
                            if (old.velocity != spec.velocity)
                            {
                                IntPtr changed = AudioHelm.Native.ChangeNoteVelocity(old.handle,
                                    Mathf.Clamp01(spec.velocity / 127f));
                                if (changed != IntPtr.Zero) old.handle = changed;
                            }
                            old.key = spec.key;
                            old.from = spec.from;
                            old.to = spec.to;
                            old.velocity = spec.velocity;
                            keep.Add(old);
                        }
                        else
                        {
                            spec.handle = AudioHelm.Native.CreateNote(segment.sequencer,
                                spec.key.pitch, Mathf.Clamp01(spec.velocity / 127f), spec.from, spec.to);
                            if (spec.handle == IntPtr.Zero)
                                throw new InvalidOperationException("Helm live note allocation failed.");
                            keep.Add(spec);
                        }
                    }
                    foreach (var old in segment.notes)
                    {
                        if (reused.Contains(old)) continue;
                        AudioHelm.Native.DeleteNote(segment.sequencer, old.handle);
                        // A deleted MIDI note may still be held: release only
                        // that pitch, not other notes or other instruments.
                        bool segmentStarted = AudioHelmClock.GetGlobalBeatTime() >= segment.startBeat;
                        if (segmentStarted && old.key.start <= playheadTick &&
                            playheadTick < old.key.end && TrackAudible(segment.track) &&
                            trackControllers.TryGetValue(segment.track, out HelmController ctl) && ctl != null)
                            ctl.NoteOff(old.key.pitch);
                    }
                    segment.notes.Clear();
                    segment.notes.AddRange(keep);
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ZPUNK MIDI] In-place live edit failed; native transport retained: " + ex);
                // Don't stop the other channels, even if an edit failed. The
                // next playback start will rebuild the full native snapshot.
                return true;
            }
        }

        private bool PrepareNativeMidi(double nativeOriginBeat = 0.0, long midiStartTick = -1)
        {
            DisposeNativeMidi();
            if (clock == null || plan == null) return false;
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

        private float nextLegacySequencerAudit;
        private void SuspendUnexpectedLegacySequencers()
        {
            if (!songMode) return;
            // The DAW owns the BASS/SYNTH/PADS channels exclusively.
            foreach (var legacy in FindObjectsOfType<HelmSequencer>())
            {
                if (legacy == null || !legacy.enabled) continue;
                legacy.enabled = false;
                if (!suspendedSequencers.Contains(legacy)) suspendedSequencers.Add(legacy);
                Debug.LogWarning("[ZPUNK MIDI] Disabled legacy HelmSequencer on " +
                    legacy.gameObject.name + " during DAW playback.");
            }
        }

        /// <summary>
        /// Apply LOOP +/- without an old/new Helm overlap. The outgoing native
        /// patterns and already scheduled drum/chop voices are stopped once;
        /// fresh patterns start from tick zero at the next safe beat on the same
        /// DSP timeline. Subsequent +/- taps before that beat replace the
        /// waiting arrangement, never creating simultaneous native patterns.
        /// </summary>
        private void QuantizedLoopRestart(SongData updated, SongEventPlanner.Plan next,
            long newLoopStart, long newLoopEnd, double now)
        {
            double restartDsp;
            int oldBars = ActiveLoopBars;
            // A beat late in its audio lookahead is unsafe to schedule; use
            // the following beat rather than playing notes late or twice.
            double minLead = Math.Max(0.12, minSampleScheduleLead * 2.0 + 0.04);
            if (quantizedLoopRestartPending && quantizedLoopRestartDsp >= now + minLead)
            {
                restartDsp = quantizedLoopRestartDsp;
                oldBars = previousLoopBarsDuringRestart;
            }
            else
            {
                double tickAtNow = now < anchorDsp ? startTick :
                    startTick + (now - anchorDsp) / secondsPerTick;
                double beatTicks = SongData.TicksPerQuarter;
                long nextBeatTick = ((long)Math.Floor(tickAtNow / beatTicks) + 1L) *
                    SongData.TicksPerQuarter;
                restartDsp = DspAt(nextBeatTick);
                double beatSeconds = SongData.TicksPerQuarter * secondsPerTick;
                // This can only defer by whole beats, so the restart is
                // musical even if +/- was pressed directly at the boundary.
                while (restartDsp < now + minLead) restartDsp += beatSeconds;
            }

            // Absolutely no old Helm sequencer remains enabled or queued once
            // the new clock/start is scheduled. This deliberately makes a very
            // short pause preferable to duplicate/16th-note retriggers.
            DisposeNativeMidi();
            StopAllSynthNotes();
            if (drums != null) drums.StopDAWVoices();

            song = updated;
            plan = next;
            bpm = Mathf.Clamp(updated.bpm, 1f, 400f);
            secondsPerTick = 60.0 / bpm / SongData.TicksPerQuarter;
            loopStart = newLoopStart;
            loopEnd = newLoopEnd;
            activeLoopEnabled = updated.arrangementLoopEnabled;
            startTick = newLoopStart;
            CurrentTick = newLoopStart;
            anchorDsp = restartDsp;
            cycle = 0;
            sampleCursor = 0;
            midiCursor = 0;
            prefetchedLoopSamples = 0;
            scheduledSamples.Clear();
            lateMidiEvents = 0;
            SkipBefore(startTick);

            // All five tracks are explicitly anchored to this same DSP time.
            ConfigureScheduleBuffer();
            if (drums != null)
            {
                drums.PrepareDAWPlayback(
                    plan.samples.Exists(e => e.type == SongEventPlanner.EventType.Chop),
                    plan.samples, bpm, activeLoopEnabled ? loopStart : 0,
                    activeLoopEnabled ? loopEnd : 0, audioLookahead);
                drums.SetDAWPlayheadStep(-1);
            }
            ResolveControllers();
            ApplyAllTrackGains();
            bool nativeReady = PrepareNativeMidi();
            if (clock != null)
            {
                clock.bpm = bpm;
                clock.pause = false;
                clock.StartScheduled(anchorDsp);
            }
            if (nativeReady) EnableNativeMidi();
            quantizedLoopRestartPending = true;
            quantizedLoopRestartDsp = restartDsp;
            previousLoopBarsDuringRestart = oldBars;
            liveRevision++;
            Debug.Log($"[ZPUNK LOOP] Beat restart queued: {oldBars} -> {PendingLoopBars} " +
                $"bars; old Helm sequences cleared; {nativeMidiSequencers.Count} replacement " +
                $"native sequences (one per melodic track); all five tracks restart " +
                $"at bar 1, DSP {restartDsp:F3}.");
        }

        // Seeking into an arrangement can create a one-shot opening segment.
        // Once it finishes, keep it disabled so later mix refreshes do not
        // accidentally reactivate the completed native sequence.
        private void ExpireFinishedNativeSegments()
        {
            if (!nativeMidiActive) return;
            double beatNow = AudioHelmClock.GetGlobalBeatTime();
            foreach (var segment in nativeMidiSegments)
            {
                if (segment.looping || segment.finished ||
                    segment.sequencer == IntPtr.Zero) continue;
                double endBeat = segment.startBeat +
                    (double)(segment.rangeEnd - segment.rangeStart) /
                    SongData.TicksPerQuarter;
                // Let the last native note-off cross the audio callback before
                // retiring this ONE-SHOT (not the repeating successor).
                if (beatNow < endBeat + 0.10) continue;
                AudioHelm.Native.EnableSequencer(segment.sequencer, false);
                nativeMidiEnabled[segment.sequencer] = false;
                segment.finished = true;
                if (verbose)
                    Debug.Log($"[ZPUNK LOOP] Finished non-looping {segment.track} " +
                        $"segment at native beat {endBeat:F2}.");
            }
        }

        private void SyncNativeMidiAudibility()
        {
            if (!nativeMidiActive) return;
            var stoppedChannels = new HashSet<int>();
            foreach (IntPtr seq in nativeMidiSequencers)
            {
                if (!nativeMidiTracks.TryGetValue(seq, out int track)) continue;
                NativeMidiSegment matchingSegment = nativeMidiSegments.Find(s => s.sequencer == seq);
                bool shouldEnable = TrackAudible(track) &&
                    (matchingSegment == null || !matchingSegment.finished);
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
            nativeMidiSegments.Clear();
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

        // Returns the Helm beat that coincides with the transport DSP anchor.
        // Used by in-place MIDI editing; LOOP +/- uses a fresh clock instead.
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
            // Preserve the running transport cycle on ordinary live edits.
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

        private void ReconcileFallbackHeldMidi(SongEventPlanner.Plan previous,
            SongEventPlanner.Plan replacement, long atTick, long updatedTick = -1)
        {
            // Helm native sequencing is normally active. If the plugin rejects
            // native sequencers, preserve unchanged Update-dispatched notes as well.
            var oldHeld = HeldMidiNotes(previous, atTick);
            var newHeld = HeldMidiNotes(replacement, updatedTick >= 0 ? updatedTick : atTick);
            foreach (var entry in oldHeld)
            {
                if (!newHeld.ContainsKey(entry.Key) &&
                    trackControllers.TryGetValue(entry.Value.trackIndex, out HelmController ctl) && ctl != null)
                    ctl.NoteOff(entry.Value.value);
            }
            foreach (var entry in newHeld)
            {
                if (!oldHeld.ContainsKey(entry.Key) && TrackAudible(entry.Value.trackIndex) &&
                    trackControllers.TryGetValue(entry.Value.trackIndex, out HelmController ctl) && ctl != null)
                    ctl.NoteOn(entry.Value.value, Mathf.Clamp01(entry.Value.velocity / 127f));
            }
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
            if (nativeReady)
            {
                // The newly built native sequencers already contain each held
                // note as a clipped span starting at position. Sending another
                // controller.NoteOn creates an unmatched voice whose native
                // NoteOff may NOT release it (often heard until loop wrap).
                EnableNativeMidi();
            }
            else
            {
                // The frame-scheduled fallback needs the held notes restored;
                // its own NOTE OFF events subsequently release them.
                RestoreHeldMidi(position);
            }
        }

        /// <summary>Changing the loop so it no longer contains the playhead
        /// requires a deliberate seek. Otherwise editing never restarts PCM.</summary>
        private void SeekLive(long newTick)
        {
            // Unlike StopSong/PlaySong, this preserves the edited in-memory
            // arrangement and never reloads an older project snapshot.
            newTick = Math.Max(0L, Math.Min(loopEnd - 1, newTick));
            quantizedLoopRestartPending = false;
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
            SuspendUnexpectedLegacySequencers();
            ApplyAllTrackGains();
            bpm = Mathf.Clamp(song.bpm, 1f, 400f);
            secondsPerTick = 60.0 / bpm / SongData.TicksPerQuarter;
            long length = Math.Max(1L, plan.lengthTicks);
            loopStart = song.arrangementLoopEnabled ? Math.Max(0L, Math.Min(length - 1, song.loopStartTick)) : 0;
            loopEnd = song.arrangementLoopEnabled ? Math.Max(loopStart + 1, Math.Min(length, song.loopEndTick)) : length;
            activeLoopEnabled = song.arrangementLoopEnabled;
            quantizedLoopRestartPending = false;
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
            SongEventPlanner.Plan previousPlan = plan;
            bool midiChanged = !MidiPlanEquals(plan, next);
            bool tempoChanged = Math.Abs(updated.bpm - bpm) > .01f;
            long length = Math.Max(1L, next.lengthTicks);
            long requestedLoopStart = updated.arrangementLoopEnabled ?
                Math.Max(0L, Math.Min(length - 1, updated.loopStartTick)) : 0L;
            long requestedLoopEnd = updated.arrangementLoopEnabled ?
                Math.Max(requestedLoopStart + 1, Math.Min(length, updated.loopEndTick)) : length;
            bool loopChanged = requestedLoopStart != loopStart ||
                requestedLoopEnd != loopEnd || activeLoopEnabled != updated.arrangementLoopEnabled;

            // LOOP +/- always restarts at bar one on a beat. Do not run two
            // native patterns across the old loop boundary in either direction.
            // Edits made before the scheduled downbeat replace the pending plan
            // at the SAME beat, rather than starting competing sequencers.
            if (!exportingOneShot && updated.arrangementLoopEnabled &&
                requestedLoopStart == 0 && loopStart == 0 &&
                (loopChanged || quantizedLoopRestartPending))
            {
                SuspendUnexpectedLegacySequencers();
                QuantizedLoopRestart(updated, next, requestedLoopStart,
                    requestedLoopEnd, now);
                return;
            }

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
            if (loopChanged)
            {
                quantizedLoopRestartPending = false;
                loopStart = requestedLoopStart;
                loopEnd = requestedLoopEnd;
                activeLoopEnabled = updated.arrangementLoopEnabled;
                if (position >= loopEnd)
                {
                    SeekLive(loopStart);
                    return;
                }
                // Nonstandard range changes retain the existing seek path.
                if (nativeMidiActive)
                    RefreshNativeMidiWithoutStoppingSamples(position);
            }
            if (drums != null)
                drums.PrepareDAWPlayback(false, plan.samples, bpm,
                    activeLoopEnabled ? loopStart : 0,
                    activeLoopEnabled ? loopEnd : 0, audioLookahead);
            ResolveControllers();
            ApplyAllTrackGains();
            if (nativeMidiActive) SyncNativeMidiAudibility();
            // Ordinary piano-roll edits keep their existing in-place Helm path.
            if (midiChanged && !loopChanged && !UpdateNativeMidiInPlace(position))
                ReconcileFallbackHeldMidi(previousPlan, next, position);
            ResetFutureSamples(position);
            midiCursor = 0;
            while (midiCursor < plan.midi.Count && plan.midi[midiCursor].tick < position)
                midiCursor++;
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
            if (Time.unscaledTime >= nextLegacySequencerAudit)
            {
                // A late prefab or UI callback can re-enable a legacy pattern.
                SuspendUnexpectedLegacySequencers();
                nextLegacySequencerAudit = Time.unscaledTime + 0.5f;
            }
            ExpireFinishedNativeSegments();
            double now = AudioSettings.dspTime;
            if (now < anchorDsp - audioLookahead) return;
            if (quantizedLoopRestartPending && now >= quantizedLoopRestartDsp)
            {
                quantizedLoopRestartPending = false;
                Debug.Log("[ZPUNK LOOP] Quantized beat restart active at bar 1.");
            }
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

            // One authoritative plan supplies the next-loop sample prefetch.
            if (activeLoopEnabled && now + audioLookahead >= endDsp)
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
                if (!nativeMidiActive) StopAllSynthNotes();
                if (activeLoopEnabled)
                {
                    anchorDsp = endDsp;
                    startTick = loopStart;
                    CurrentTick = loopStart;
                    cycle++;
                    scheduledSamples.RemoveWhere(e => e.cycleId < cycle);
                    sampleCursor = prefetchedLoopSamples;
                    midiCursor = 0;
                    prefetchedLoopSamples = 0;
                    SkipBefore(startTick);
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
            quantizedLoopRestartPending = false;
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
