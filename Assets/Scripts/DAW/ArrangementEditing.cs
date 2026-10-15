using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZPunk.DAW
{
    /// <summary>Phase 3A/3B: document-only clip operations. No playback or UI side effects.</summary>
    public static class ArrangementEditing
    {
        public const long MaxSongTicks = 1024L * SongData.TicksPerBar4;
        public static string Id(string prefix) { return prefix + "/" + Guid.NewGuid().ToString("N"); }

        public static long Snap(long tick, long quantum)
        {
            quantum = Math.Max(1L, quantum);
            return Math.Max(0L, (long)Math.Round((double)tick / quantum, MidpointRounding.AwayFromZero) * quantum);
        }

        // Signed delta: dragging left must work as well as dragging right.
        public static long SnapDelta(long delta, long quantum)
        {
            quantum = Math.Max(1L, quantum);
            return (long)Math.Round((double)delta / quantum, MidpointRounding.AwayFromZero) * quantum;
        }

        public static ClipContentData FindContent(SongData song, string id)
        {
            if (song == null || song.contents == null || string.IsNullOrEmpty(id)) return null;
            foreach (var c in song.contents)
                if (c != null && c.id == id) return c;
            return null;
        }

        public static ClipData FindClip(SongData song, string id, out TrackData owner)
        {
            owner = null;
            if (song == null || song.tracks == null) return null;
            foreach (var track in song.tracks)
            {
                if (track == null || track.clips == null) continue;
                foreach (var clip in track.clips)
                    if (clip != null && clip.id == id) { owner = track; return clip; }
            }
            return null;
        }

        public static ClipData CreateClip(SongData song, TrackData track, long tick)
        {
            if (song == null || track == null) return null;
            if (track.clips == null) track.clips = new List<ClipData>();
            if (song.contents == null) song.contents = new List<ClipContentData>();
            tick = Math.Max(0L, Math.Min(MaxSongTicks - SongData.TicksPerBar4, tick));
            ClipKind kind = track.kind == TrackKind.Drums ? ClipKind.Drums :
                track.kind == TrackKind.Chops ? ClipKind.Chops : ClipKind.Midi;
            var content = new ClipContentData { id = Id("content"), kind = kind, lengthTicks = SongData.TicksPerBar4 };
            var clip = new ClipData { id = Id("clip"), contentId = content.id,
                name = track.name + " CLIP", startTick = tick, lengthTicks = SongData.TicksPerBar4,
                sourceOffsetTick = 0, loopEnabled = false };
            song.contents.Add(content);
            track.clips.Add(clip);
            ExtendSong(song, tick + clip.lengthTicks);
            return clip;
        }

        /// <summary>Find the next clip-free span; prevents a duplicate from silently stacking over the following pattern.</summary>
        public static long NextFreeStart(TrackData track, long proposed, long duration)
        {
            proposed = Math.Max(0L, proposed);
            duration = Math.Max(1L, duration);
            if (track == null || track.clips == null) return proposed;
            // Move beyond conflicting clips; bounded by total clip count, not timeline length.
            for (int attempt = 0; attempt <= track.clips.Count; attempt++)
            {
                long next = proposed;
                foreach (var other in track.clips)
                {
                    if (other == null || other.lengthTicks <= 0) continue;
                    long end = other.startTick + other.lengthTicks;
                    if (other.startTick < proposed + duration && end > proposed)
                        next = Math.Max(next, end);
                }
                if (next == proposed) break;
                proposed = next;
            }
            return proposed;
        }

        public static ClipData Duplicate(SongData song, ClipData source, TrackData track)
        {
            if (song == null || source == null || track == null) return null;
            var content = FindContent(song, source.contentId);
            if (content == null) return null;
            long start = NextFreeStart(track, source.startTick + source.lengthTicks, source.lengthTicks);
            if (start < 0 || start > MaxSongTicks - source.lengthTicks) return null;
            // Independent content by default: editing one duplicate never changes another.
            var copy = JsonUtility.FromJson<ClipContentData>(JsonUtility.ToJson(content));
            copy.id = Id("content");
            song.contents.Add(copy);
            var clip = new ClipData { id = Id("clip"), name = source.name + " COPY",
                contentId = copy.id, startTick = start, lengthTicks = source.lengthTicks,
                sourceOffsetTick = source.sourceOffsetTick, loopEnabled = source.loopEnabled };
            track.clips.Add(clip);
            ExtendSong(song, start + clip.lengthTicks);
            return clip;
        }

        public static bool Move(SongData song, ClipData clip, long newStart)
        {
            if (song == null || clip == null) return false;
            newStart = Math.Max(0L, Math.Min(MaxSongTicks - clip.lengthTicks, newStart));
            if (clip.startTick == newStart) return false;
            clip.startTick = newStart;
            ExtendSong(song, clip.startTick + clip.lengthTicks);
            return true;
        }

        public static bool Resize(SongData song, ClipData clip, long newLength)
        {
            if (song == null || clip == null) return false;
            newLength = Math.Max(SongData.TicksPerSixteenth,
                Math.Min(MaxSongTicks - clip.startTick, newLength));
            if (newLength == clip.lengthTicks) return false;
            clip.lengthTicks = newLength;
            var content = FindContent(song, clip.contentId);
            if (content != null && clip.sourceOffsetTick + newLength > content.lengthTicks)
                clip.loopEnabled = true;
            ExtendSong(song, clip.startTick + newLength);
            return true;
        }

        public static bool Delete(SongData song, ClipData clip, TrackData owner)
        {
            if (song == null || clip == null || owner == null || owner.clips == null) return false;
            if (!owner.clips.Remove(clip)) return false;
            // Remove unreferenced content only, respecting links in imported v1 patterns.
            bool inUse = false;
            foreach (var track in song.tracks)
            {
                if (track == null || track.clips == null) continue;
                foreach (var other in track.clips)
                    if (other != null && other.contentId == clip.contentId) { inUse = true; break; }
                if (inUse) break;
            }
            if (!inUse)
                song.contents.RemoveAll(c => c != null && c.id == clip.contentId);
            return true;
        }

        public static void ExtendSong(SongData song, long atLeast)
        {
            if (song == null) return;
            long end = Math.Max(SongData.TicksPerBar4, Math.Min(MaxSongTicks, atLeast));
            if (end > song.lengthTicks)
            {
                bool loopWasAtEnd = song.loopEndTick >= song.lengthTicks;
                song.lengthTicks = end;
                if (loopWasAtEnd && !song.loopLengthCustom) song.loopEndTick = end;
            }
        }

        public static SongData IndependentCopy(SongData song)
        {
            return song == null ? null : SongData.FromJson(JsonUtility.ToJson(song));
        }
    }
}
