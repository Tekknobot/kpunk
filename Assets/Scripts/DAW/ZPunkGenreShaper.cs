using System;
using System.Collections.Generic;

namespace ZPunk.DAW
{
    /// <summary>
    /// Non-destructive, deterministic shaping pass for the existing genre composers.
    /// The neutral (2) position preserves their original 19 style identities.
    /// This deliberately does not touch instruments, transport, or other clips.
    /// </summary>
    internal static class ZPunkGenreShaper
    {
        internal sealed class Settings
        {
            internal int density = 2, groove = 2, gate = 2, register = 2, energy = 2;
            internal int kick = 2, hats = 2, percussion = 2, fills = 2;
        }

        internal static readonly string[] Density = { "SPARSE", "LIGHT", "ORIGINAL", "BUSY", "FULL" };
        internal static readonly string[] Groove = { "TIGHT", "STRAIGHT", "ORIGINAL", "SWING", "SHUFFLE" };
        internal static readonly string[] Gate = { "SHORT", "CLIPPED", "ORIGINAL", "HELD", "LONG" };
        internal static readonly string[] Register = { "-2 OCT", "-1 OCT", "ORIGINAL", "+1 OCT", "+2 OCT" };
        internal static readonly string[] Spread = { "CLOSE", "TIGHT", "ORIGINAL", "OPEN", "WIDE" };
        internal static readonly string[] Energy = { "SOFT", "MELLOW", "ORIGINAL", "PUNCHY", "HARD" };
        internal static readonly string[] LaneLevel = { "MINIMUM", "LOW", "ORIGINAL", "HIGH", "MAX" };
        internal static readonly string[] Fills = { "NONE", "SUBTLE", "ORIGINAL", "MORE", "BUSY" };

        // Kept in the editable clip's existing generatedTag, so reopening the
        // project restores the actual parameter values without changing schema.
        private const string Marker = " / SHAPE=";
        internal static void Remember(ClipContentData clip, Settings s)
        {
            if (clip == null || s == null) return;
            string label = clip.generatedTag ?? "";
            int start = label.IndexOf(Marker, StringComparison.Ordinal);
            if (start >= 0) label = label.Substring(0, start);
            clip.generatedTag = label + Marker +
                string.Concat(C(s.density), C(s.groove), C(s.gate), C(s.register), C(s.energy),
                              C(s.kick), C(s.hats), C(s.percussion), C(s.fills));
        }
        private static string C(int v) => Math.Max(0, Math.Min(4, v)).ToString();
        internal static bool Restore(string tag, Settings s)
        {
            if (string.IsNullOrEmpty(tag) || s == null) return false;
            int i = tag.LastIndexOf(Marker, StringComparison.Ordinal);
            if (i < 0 || tag.Length < i + Marker.Length + 9) return false;
            string digits = tag.Substring(i + Marker.Length, 9);
            for (int n = 0; n < digits.Length; n++)
                if (digits[n] < '0' || digits[n] > '4') return false;
            s.density = digits[0] - '0'; s.groove = digits[1] - '0';
            s.gate = digits[2] - '0'; s.register = digits[3] - '0';
            s.energy = digits[4] - '0'; s.kick = digits[5] - '0';
            s.hats = digits[6] - '0'; s.percussion = digits[7] - '0';
            s.fills = digits[8] - '0';
            return true;
        }

        private static uint Mix(int a, long b, int c)
        {
            unchecked
            {
                uint h = (uint)a * 2654435761u ^ (uint)b * 2246822519u ^ (uint)(b >> 32) * 3266489917u ^ (uint)c * 668265263u;
                h ^= h >> 16; h *= 2246822519u; h ^= h >> 13; h *= 3266489917u; h ^= h >> 16;
                return h;
            }
        }
        private static bool Hit(int variation, long tick, int salt, int chance) =>
            chance > 0 && (chance >= 100 || Mix(variation, tick, salt) % 100u < (uint)chance);
        private static int Clamp(int n, int min, int max) => Math.Max(min, Math.Min(max, n));
        private static long LimitTick(long t, long length) => Math.Max(0, Math.Min(length - 1, t));

        internal static void Apply(ClipContentData content, TrackKind kind, Settings s, int genre, int variation, bool chords)
        {
            if (content == null || s == null || content.lengthTicks < 1) return;
            if (kind == TrackKind.Drums) ShapeDrums(content, s, genre, variation);
            else ShapeMidi(content, kind, s, variation, chords || kind == TrackKind.Pads);
            Remember(content, s);
        }

        private static void ShapeDrums(ClipContentData c, Settings s, int genre, int variation)
        {
            if (c.drums == null) return;
            // Parameter changes are applied to fresh genre output, never to the
            // previous shaped notes. Kicks and backbeats retain their style DNA.
            var source = c.drums;
            var shaped = new List<DrumEvent>(source.Count + 24);
            var positions = new HashSet<string>();
            long barTicks = SongData.TicksPerBar4;
            long sixteenth = SongData.TicksPerSixteenth;
            int[] densityKeep = { 42, 72, 100, 100, 100 };
            int[] laneKeep = { 28, 65, 100, 100, 100 };
            foreach (var hit in source)
            {
                if (hit == null || hit.startTick < 0 || hit.startTick >= c.lengthTicks) continue;
                int lane = hit.lane;
                int laneSetting = lane == 0 ? s.kick : lane == 3 || lane == 4 ? s.hats :
                                  lane >= 5 ? s.percussion : 2;
                bool protectedBeat = (lane == 0 && hit.startTick % barTicks == 0) ||
                                     (lane == 1 && ((hit.startTick / sixteenth) % 16 == 4 ||
                                                    (hit.startTick / sixteenth) % 16 == 12));
                int chance = Math.Min(densityKeep[Clamp(s.density, 0, 4)], laneKeep[Clamp(laneSetting, 0, 4)]);
                if (s.fills < 2 && lane >= 5 && hit.startTick % barTicks >= barTicks - 4 * sixteenth)
                    chance = Math.Min(chance, s.fills == 0 ? 0 : 40);
                if (!protectedBeat && !Hit(variation, hit.startTick, lane * 31 + 21, chance)) continue;
                long tick = Swing(hit.startTick, s.groove, true, lane, c.lengthTicks);
                AddDrum(shaped, positions, lane, tick, Clamp(hit.velocity + (s.energy - 2) * 12, 20, 127));
            }
            // Raise lane activity by adding musically placed pickups from each
            // genre's existing hit vocabulary. Avoid redundant exact duplicates.
            int bars = (int)(c.lengthTicks / barTicks);
            for (int b = 0; b < bars; b++)
            {
                long origin = b * barTicks;
                int hatBoost = Math.Max(0, Math.Max(s.hats - 2, s.density - 2));
                int kickBoost = Math.Max(0, Math.Max(s.kick - 2, s.density - 2));
                int percBoost = Math.Max(0, Math.Max(s.percussion - 2, s.density - 2));
                if (hatBoost > 0)
                {
                    int[] hats = hatBoost == 1 ? new[] { 2, 6, 10, 14 } :
                        new[] { 1, 2, 3, 5, 6, 7, 9, 10, 11, 13, 14, 15 };
                    foreach (int step in hats)
                        if (Hit(variation, origin + step * sixteenth, 37, hatBoost == 1 ? 72 : 93))
                            AddDrum(shaped, positions, 3,
                                Swing(origin + step * sixteenth, s.groove, true, 3, c.lengthTicks), 55 + s.energy * 7);
                }
                if (kickBoost > 0)
                {
                    int[] steps = genre == 1 || genre == 2 || genre == 5 ?
                        (kickBoost == 1 ? new[] { 0, 4, 8, 12 } : new[] { 0, 3, 4, 8, 11, 12, 15 }) :
                        (kickBoost == 1 ? new[] { 0, 8 } : new[] { 0, 6, 8, 10, 14 });
                    foreach (int step in steps)
                        if (Hit(variation, origin + step * sixteenth, 41, kickBoost == 1 ? 78 : 95))
                            AddDrum(shaped, positions, 0,
                                Swing(origin + step * sixteenth, s.groove, true, 0, c.lengthTicks), 83 + s.energy * 8);
                }
                if (percBoost > 0)
                {
                    int[] steps = percBoost == 1 ? new[] { 3, 11 } : new[] { 3, 7, 11, 15 };
                    foreach (int step in steps)
                        if (Hit(variation, origin + step * sixteenth, 43, percBoost == 1 ? 55 : 88))
                            AddDrum(shaped, positions, 6,
                                Swing(origin + step * sixteenth, s.groove, true, 6, c.lengthTicks), 41 + s.energy * 7);
                }
                if (s.fills > 2 && b == bars - 1)
                {
                    int[] steps = s.fills == 3 ? new[] { 14, 15 } : new[] { 11, 12, 13, 14, 15 };
                    foreach (int step in steps)
                        AddDrum(shaped, positions, step == 15 ? 1 : 6,
                            Swing(origin + step * sixteenth, s.groove, true, step == 15 ? 1 : 6, c.lengthTicks),
                            46 + s.energy * 6 + (step == 15 ? 16 : 0));
                }
            }
            shaped.Sort((a, b) => a.startTick == b.startTick ? a.lane.CompareTo(b.lane) : a.startTick.CompareTo(b.startTick));
            c.drums = shaped;
        }

        private static void AddDrum(List<DrumEvent> output, HashSet<string> positions, int lane, long tick, int velocity)
        {
            if (!positions.Add(lane + ":" + tick)) return;
            output.Add(new DrumEvent { lane = lane, startTick = tick, velocity = Clamp(velocity, 20, 127), legacyVelocityTier = 2 });
        }

        private static long Swing(long start, int groove, bool drums, int lane, long length)
        {
            if (groove == 2) return start;
            // Preserve barline kicks and the snare's strong backbeat. Offset
            // off-beat eighths, never changing the order of quarter-note anchors.
            int step = (int)((start % SongData.TicksPerBar4) / SongData.TicksPerSixteenth);
            if (drums && ((lane == 0 && step % 4 == 0) || (lane == 1 && step % 4 == 0))) return start;
            int offset = groove == 0 ? -8 : groove == 1 ? -3 : groove == 3 ? 19 : 39;
            if (step % 2 == 0) return start;
            return LimitTick(start + offset, length);
        }

        private static void ShapeMidi(ClipContentData c, TrackKind kind, Settings s, int variation, bool polyphonic)
        {
            if (c.notes == null || c.notes.Count == 0) return;
            var groups = new SortedDictionary<long, List<NoteEvent>>();
            foreach (var note in c.notes)
            {
                if (note == null) continue;
                List<NoteEvent> group;
                if (!groups.TryGetValue(note.startTick, out group))
                    groups[note.startTick] = group = new List<NoteEvent>();
                group.Add(note);
            }
            if (groups.Count == 0) return;
            int[] keep = { 33, 65, 100, 100, 100 };
            var result = new List<NoteEvent>(c.notes.Count + 32);
            long barTicks = SongData.TicksPerBar4;
            var firstByBar = new HashSet<long>();
            foreach (var pair in groups)
            {
                long bar = pair.Key / barTicks;
                bool first = firstByBar.Add(bar);
                if (!first && !Hit(variation, pair.Key, 83, keep[Clamp(s.density, 0, 4)])) continue;
                long newTick = Swing(pair.Key, s.groove, false, 0, c.lengthTicks);
                // Grouped chord voices MUST move together; their existing IDs
                // remain intact for whole-chord drag/resize after generation.
                List<NoteEvent> group = pair.Value;
                group.Sort((a, b) => a.pitch.CompareTo(b.pitch));
                int[] gatePercent = { 45, 72, 100, 145, 190 };
                for (int i = 0; i < group.Count; i++)
                {
                    var source = group[i];
                    int pitch = source.pitch;
                    if (polyphonic)
                    {
                        if (s.register < 2 && i > 0) pitch -= 12 * (i == group.Count - 1 ? 1 : 0);
                        if (s.register > 2 && i > 0) pitch += 12 * (i == group.Count - 1 || s.register == 4 && i >= group.Count - 2 ? 1 : 0);
                        if (s.register == 0 && i == 1 && group.Count > 3) pitch -= 12;
                    }
                    else pitch += (s.register - 2) * 12;
                    pitch = Clamp(pitch, 0, 127);
                    long duration = Math.Max(1, source.durationTicks * gatePercent[Clamp(s.gate, 0, 4)] / 100);
                    duration = Math.Min(duration, c.lengthTicks - newTick);
                    result.Add(new NoteEvent { pitch = pitch, startTick = newTick,
                        durationTicks = Math.Max(1, duration),
                        velocity = Clamp(source.velocity + (s.energy - 2) * 11, 25, 127),
                        chordGroupId = source.chordGroupId, accent = source.accent, slide = source.slide });
                }
                // Additional short musical responses reuse the composed chord
                // or melodic pitch; they do not select a new random harmony.
                if (s.density > 2 && kind != TrackKind.Pads)
                {
                    int percent = s.density == 3 ? 36 : 72;
                    long addTick = pair.Key + (s.density == 3 ? SongData.TicksPerQuarter : SongData.TicksPerQuarter / 2);
                    long next = c.lengthTicks;
                    foreach (var future in groups.Keys) if (future > pair.Key) { next = future; break; }
                    if (addTick + SongData.TicksPerSixteenth / 2 < next &&
                        Hit(variation, pair.Key, 91, percent))
                    {
                        long echoTick = Swing(addTick, s.groove, false, 0, c.lengthTicks);
                        long echoDuration = Math.Max(1, Math.Min(next - echoTick - 1,
                            SongData.TicksPerQuarter * (s.gate + 2) / 6));
                        if (echoDuration > 2)
                        {
                            string echoGroup = polyphonic ? "gen_echo_" + variation + "_" + pair.Key : null;
                            int generatedGroupStart = result.Count - group.Count;
                            for (int i = 0; i < group.Count; i++)
                            {
                                var note = group[i];
                                int pitch = result[generatedGroupStart + i].pitch;
                                result.Add(new NoteEvent { pitch = pitch, startTick = echoTick,
                                    durationTicks = echoDuration, velocity = Clamp(note.velocity - 9 + (s.energy - 2) * 11, 25, 127),
                                    chordGroupId = echoGroup });
                            }
                        }
                    }
                }
            }
            // Prevent same-pitch collisions after swing, hold and extra replies.
            // All notes in each chord retain matching tick and group identifiers.
            result.Sort((a, b) => a.startTick == b.startTick ? a.pitch.CompareTo(b.pitch) : a.startTick.CompareTo(b.startTick));
            var previousByPitch = new Dictionary<int, NoteEvent>();
            foreach (var n in result)
            {
                NoteEvent previous;
                if (previousByPitch.TryGetValue(n.pitch, out previous) && previous.startTick < n.startTick)
                    previous.durationTicks = Math.Max(1, Math.Min(previous.durationTicks, n.startTick - previous.startTick - 1));
                previousByPitch[n.pitch] = n;
            }
            c.notes = result;
        }
    }
}
