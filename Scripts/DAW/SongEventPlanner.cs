using System;
using System.Collections.Generic;

namespace ZPunk.DAW
{
    /// <summary>
    /// Pure arrangement-to-event compiler. No Unity objects or audio side effects.
    /// Events are sorted in song ticks and may be used by the DSP audio scheduler.
    /// Phase 2 deliberately generates one song traversal; the transport handles looping.
    /// </summary>
    public static class SongEventPlanner
    {
        public enum EventType { Drum, Chop, NoteOn, NoteOff }

        public sealed class Event
        {
            public long tick;
            public int trackIndex;
            public EventType type;
            public int value; // MIDI pitch, 0-based drum lane, or 1-based slice ID
            public int velocity;
        }

        public sealed class Plan
        {
            public readonly List<Event> samples = new List<Event>();
            public readonly List<Event> midi = new List<Event>();
            public long lengthTicks;
            public int skippedClips;
        }

        /// <summary>True when a compiled arrangement contains at least one actual playback event.</summary>
        public static bool HasEvents(SongData song)
        {
            if (song == null) return false;
            Plan plan = Build(song);
            return plan.samples.Count > 0 || plan.midi.Count > 0;
        }

        public static Plan Build(SongData song)
        {
            if (song == null || song.tracks == null || song.contents == null)
                throw new ArgumentException("Song must have tracks and clip contents.");

            var plan = new Plan { lengthTicks = Math.Max(1L, song.lengthTicks) };
            var contents = new Dictionary<string, ClipContentData>();
            foreach (ClipContentData content in song.contents)
                if (content != null && !string.IsNullOrEmpty(content.id) && !contents.ContainsKey(content.id))
                    contents.Add(content.id, content);

            for (int trackIndex = 0; trackIndex < song.tracks.Count; ++trackIndex)
            {
                TrackData track = song.tracks[trackIndex];
                if (track == null || track.clips == null) continue;
                foreach (ClipData clip in track.clips)
                {
                    if (clip == null || clip.lengthTicks <= 0 || clip.startTick < 0 ||
                        !contents.TryGetValue(clip.contentId ?? "", out ClipContentData content) || content == null)
                    {
                        plan.skippedClips++;
                        continue;
                    }
                    long contentLength = Math.Max(1, content.lengthTicks);
                    long clipEnd = Math.Min(plan.lengthTicks, SaturatingAdd(clip.startTick, clip.lengthTicks));
                    if (clipEnd <= clip.startTick) continue;

                    // One segment per traversal of source content. Non-loop clips never wrap.
                    long cursor = clip.startTick;
                    long sourceOffset = Mod(clip.sourceOffsetTick, contentLength);
                    int segmentGuard = 0;
                    while (cursor < clipEnd && segmentGuard++ < 16384)
                    {
                        long available = contentLength - sourceOffset;
                        long segmentLength = Math.Min(available, clipEnd - cursor);
                        if (segmentLength <= 0) break;
                        AddSegment(plan, content, track.kind, trackIndex, cursor, sourceOffset, segmentLength);
                        cursor += segmentLength;
                        if (!clip.loopEnabled) break;
                        sourceOffset = 0;
                    }
                    if (cursor < clipEnd && clip.loopEnabled) plan.skippedClips++;
                }
            }

            Comparison<Event> order = (a, b) =>
            {
                int comparison = a.tick.CompareTo(b.tick);
                if (comparison != 0) return comparison;
                // Release old voices before starting new voices of the same pitch.
                comparison = ((a.type == EventType.NoteOff) ? -1 : (int)a.type)
                    .CompareTo((b.type == EventType.NoteOff) ? -1 : (int)b.type);
                return comparison != 0 ? comparison : a.trackIndex.CompareTo(b.trackIndex);
            };
            plan.samples.Sort(order);
            plan.midi.Sort(order);
            return plan;
        }

        private static void AddSegment(Plan plan, ClipContentData content, TrackKind trackKind,
            int trackIndex, long songStart, long sourceStart, long segmentLength)
        {
            long sourceEnd = sourceStart + segmentLength;
            if (trackKind == TrackKind.Drums && content.kind == ClipKind.Drums && content.drums != null)
            {
                foreach (DrumEvent drum in content.drums)
                {
                    if (drum == null || drum.lane < 0 || drum.lane >= 8 ||
                        drum.startTick < sourceStart || drum.startTick >= sourceEnd) continue;
                    plan.samples.Add(New(songStart + drum.startTick - sourceStart, trackIndex,
                        EventType.Drum, drum.lane, drum.velocity));
                }
            }
            else if (trackKind == TrackKind.Chops && content.kind == ClipKind.Chops && content.chops != null)
            {
                foreach (ChopEvent chop in content.chops)
                {
                    if (chop == null || chop.sliceId < 1 || chop.sliceId > 16 ||
                        chop.startTick < sourceStart || chop.startTick >= sourceEnd) continue;
                    plan.samples.Add(New(songStart + chop.startTick - sourceStart, trackIndex,
                        EventType.Chop, chop.sliceId, chop.velocity));
                }
            }
            else if ((trackKind == TrackKind.Bass || trackKind == TrackKind.Synth || trackKind == TrackKind.Pads) &&
                     content.kind == ClipKind.Midi && content.notes != null)
            {
                foreach (NoteEvent note in content.notes)
                {
                    if (note == null || note.pitch < 0 || note.pitch > 127 || note.durationTicks <= 0 ||
                        note.startTick < sourceStart || note.startTick >= sourceEnd) continue;
                    long tick = songStart + note.startTick - sourceStart;
                    long end = tick + Math.Min(note.durationTicks, sourceEnd - note.startTick);
                    if (end <= tick) continue;
                    plan.midi.Add(New(tick, trackIndex, EventType.NoteOn, note.pitch, note.velocity));
                    plan.midi.Add(New(end, trackIndex, EventType.NoteOff, note.pitch, 0));
                }
            }
        }

        private static Event New(long tick, int track, EventType kind, int value, int velocity)
        {
            return new Event { tick = tick, trackIndex = track, type = kind,
                value = value, velocity = Math.Max(0, Math.Min(127, velocity)) };
        }

        private static long Mod(long n, long d) { return (n % d + d) % d; }
        private static long SaturatingAdd(long a, long b)
        {
            return b > long.MaxValue - a ? long.MaxValue : a + b;
        }
    }
}
