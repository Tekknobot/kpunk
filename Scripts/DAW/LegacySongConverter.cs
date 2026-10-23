using System;
using System.Collections.Generic;
using KMusic.Core;
using UnityEngine;

namespace ZPunk.DAW
{
    /// <summary>
    /// Converts immutable snapshots of the v1 bank/chain to a v2 arrangement.
    /// Does not access PlayerPrefs, mutate the bank, touch the synth, or change playback.
    /// A meaningful chain is mapped bar-for-bar (including silent gaps); if the chain
    /// has no assigned patterns, the currently active live pattern is used instead.
    /// </summary>
    public static class LegacySongConverter
    {
        [Serializable]
        public sealed class Settings
        {
            public string title = "UNTITLED";
            public float bpm = 120f;
            public string synthPresetPath;
            public int synthPresetIndex;
            public float synthVolume = -1f;
            public string padPresetPath;
            public int padPresetIndex;
            public float padVolume = -1f;
            public int kitIndex;
            public string sampleResourcesPath;
            public float[] sliceStart01;
            public float[] sliceEnd01;
        }

        private static readonly int[] TierVelocities = { 54, 90, 122 };
        private static readonly string[] TrackIds = { "drums", "bass", "synth", "pads", "chops" };

        public static SongData Convert(
            PatternBank.PatternBankSave bank,
            ChainStateSave chain,
            PatternData live,
            Settings settings)
        {
            if (settings == null) settings = new Settings();
            var song = new SongData
            {
                title = string.IsNullOrWhiteSpace(settings.title) ? "UNTITLED" : settings.title,
                bpm = Mathf.Clamp(settings.bpm, 1f, 400f),
                migratedFromLegacy = true,
                legacyChainEnabled = chain != null && chain.enabled
            };

            song.tracks.Add(NewTrack(TrackKind.Drums, "Drums", "#DEA44E"));
            song.tracks.Add(NewTrack(TrackKind.Bass, "Bass", "#E87F8A")); // new, empty lane
            song.tracks.Add(NewTrack(TrackKind.Synth, "Synth", "#6C9EDF"));
            song.tracks.Add(NewTrack(TrackKind.Pads, "Pads", "#A38BD9"));
            song.tracks.Add(NewTrack(TrackKind.Chops, "Chops", "#65BEA9"));

            song.tracks[0].instrument.drumKitIndex = settings.kitIndex;
            song.tracks[2].instrument.presetRelPath = settings.synthPresetPath;
            song.tracks[2].instrument.presetIndex = settings.synthPresetIndex;
            song.tracks[2].instrument.savedMasterVolume = settings.synthVolume;
            song.tracks[3].instrument.presetRelPath = settings.padPresetPath;
            song.tracks[3].instrument.presetIndex = settings.padPresetIndex;
            song.tracks[3].instrument.savedMasterVolume = settings.padVolume;
            song.tracks[4].instrument.sampleResourcesPath = settings.sampleResourcesPath;
            song.tracks[4].instrument.sliceStart01 = Copy(settings.sliceStart01);
            song.tracks[4].instrument.sliceEnd01 = Copy(settings.sliceEnd01);

            var byId = ReadPatterns(bank);
            var barPatterns = new List<PatternData>();
            var barIds = new List<int>();
            bool hasAssignedSlot = false;
            int length = chain == null ? 0 : Mathf.Clamp(chain.length, 0, 64);
            for (int bar = 0; bar < length; bar++)
            {
                int id = chain.slots != null && bar < chain.slots.Length ? chain.slots[bar] : -1;
                if (id >= 0 && byId.ContainsKey(id)) hasAssignedSlot = true;
                barIds.Add(id);
            }
            if (hasAssignedSlot)
            {
                for (int bar = 0; bar < length; bar++)
                {
                    PatternData pattern;
                    barPatterns.Add(byId.TryGetValue(barIds[bar], out pattern) ? pattern : null);
                }
            }
            else
            {
                // An unassigned chain is not an arrangement. Migrate live grid instead.
                barPatterns.Add(live ?? new PatternData());
                barIds.Clear();
                barIds.Add(-1);
            }

            song.lengthTicks = (long)barPatterns.Count * SongData.TicksPerBar4;
            song.loopEndTick = song.lengthTicks;
            // One clip placement per non-empty track/bar. Shared pattern content is
            // reused for repeated pattern IDs, allowing linked editing in the next phase.
            for (int bar = 0; bar < barPatterns.Count; bar++)
            {
                PatternData pattern = barPatterns[bar];
                if (pattern == null) continue;
                string patternKey = hasAssignedSlot ? "pattern-" + barIds[bar] : "live";
                for (int t = 0; t < song.tracks.Count; t++)
                {
                    if (t == 1) continue; // Bass did not exist in v1.
                    TrackData track = song.tracks[t];
                    string contentId = "legacy/" + patternKey + "/" + track.id;
                    ClipContentData content = FindContent(song, contentId);
                    if (content == null)
                    {
                        content = BuildContent(track.kind, pattern, contentId);
                        if (IsEmpty(content)) continue;
                        song.contents.Add(content);
                    }
                    track.clips.Add(new ClipData
                    {
                        id = "legacy/" + track.id + "/bar-" + bar,
                        name = hasAssignedSlot ? "P" + (barIds[bar] + 1).ToString("00") : "LIVE",
                        contentId = contentId,
                        startTick = (long)bar * SongData.TicksPerBar4,
                        lengthTicks = SongData.TicksPerBar4,
                        sourceOffsetTick = 0,
                        loopEnabled = false
                    });
                }
            }
            return song;
        }

        private static TrackData NewTrack(TrackKind kind, string name, string color)
        {
            return new TrackData { id = TrackIds[(int)kind], kind = kind, name = name, colorHex = color };
        }

        private static Dictionary<int, PatternData> ReadPatterns(PatternBank.PatternBankSave bank)
        {
            var result = new Dictionary<int, PatternData>();
            if (bank == null || bank.ids == null) return result;
            for (int i = 0; i < bank.ids.Count; i++)
            {
                var p = new PatternData
                {
                    drumMask = DecodeB64(bank.drumMaskB64, i, 16),
                    drumVelocityData = DecodeB64(bank.drumVelocityB64, i, 32),
                    sampleSteps = ReadInts(bank.sampleSteps, i),
                    seqSteps = ReadInts(bank.seqSteps, i),
                    seqRuns = ReadInts(bank.seqRuns, i),
                    padSteps = ReadInts(bank.padSteps, i),
                    padRuns = ReadInts(bank.padRuns, i),
                    padChordMode = bank.padChordModes != null && i < bank.padChordModes.Count
                        ? bank.padChordModes[i] : 0
                };
                result[bank.ids[i]] = p;
            }
            return result;
        }

        private static byte[] DecodeB64(List<string> source, int index, int size)
        {
            var bytes = new byte[size];
            if (source == null || index >= source.Count || string.IsNullOrEmpty(source[index])) return bytes;
            try
            {
                var decoded = System.Convert.FromBase64String(source[index]);
                Array.Copy(decoded, bytes, Math.Min(size, decoded.Length));
            }
            catch (FormatException) { /* broken legacy field: preserve other instruments */ }
            return bytes;
        }

        private static int[] ReadInts(List<PatternBank.PatternBankSave.IntArrayWrap> data, int index)
        {
            var result = new int[PatternData.Steps];
            if (data != null && index < data.Count && data[index] != null && data[index].v != null)
                Array.Copy(data[index].v, result, Math.Min(result.Length, data[index].v.Length));
            return result;
        }

        private static float[] Copy(float[] source)
        {
            if (source == null) return null;
            var copy = new float[source.Length];
            Array.Copy(source, copy, source.Length);
            return copy;
        }

        private static ClipContentData FindContent(SongData song, string id)
        {
            for (int i = 0; i < song.contents.Count; i++)
                if (song.contents[i].id == id) return song.contents[i];
            return null;
        }

        private static bool IsEmpty(ClipContentData c)
        {
            return c.notes.Count == 0 && c.drums.Count == 0 && c.chops.Count == 0;
        }

        private static ClipContentData BuildContent(TrackKind kind, PatternData p, string id)
        {
            var c = new ClipContentData { id = id, lengthTicks = SongData.TicksPerBar4 };
            switch (kind)
            {
                case TrackKind.Drums:
                    c.kind = ClipKind.Drums;
                    for (int step = 0; step < 16; step++)
                    {
                        byte mask = (byte)Get(p.drumMask, step);
                        int packIndex = step * 2;
                        int packed = Get(p.drumVelocityData, packIndex) |
                                     (Get(p.drumVelocityData, packIndex + 1) << 8);
                        for (int lane = 0; lane < 8; lane++)
                        {
                            if ((mask & (1 << lane)) == 0) continue;
                            int tier = Mathf.Clamp((packed >> (lane * 2)) & 3, 0, 2);
                            c.drums.Add(new DrumEvent
                            {
                                lane = lane,
                                startTick = step * SongData.TicksPerSixteenth,
                                velocity = TierVelocities[tier],
                                legacyVelocityTier = tier
                            });
                        }
                    }
                    break;
                case TrackKind.Synth:
                    c.kind = ClipKind.Midi;
                    AddNotes(c.notes, p.seqSteps, p.seqRuns, 0);
                    break;
                case TrackKind.Pads:
                    c.kind = ClipKind.Midi;
                    AddNotes(c.notes, p.padSteps, p.padRuns, Mathf.Clamp(p.padChordMode, 0, 3) + 1);
                    break;
                case TrackKind.Chops:
                    c.kind = ClipKind.Chops;
                    for (int step = 0; step < 16; step++)
                    {
                        int slice = Get(p.sampleSteps, step);
                        if (slice <= 0) continue;
                        c.chops.Add(new ChopEvent
                        {
                            sliceId = slice,
                            startTick = step * SongData.TicksPerSixteenth,
                            velocity = 100
                        });
                    }
                    break;
            }
            return c;
        }

        private static void AddNotes(List<NoteEvent> output, int[] steps, int[] runs, int chordModePlusOne)
        {
            for (int step = 0; step < 16; step++)
            {
                int valueId = Get(steps, step);
                if (valueId <= 0) continue;
                // Legacy held-note cells repeat the value; only a start cell emits a note.
                if (step > 0 && Get(steps, step - 1) == valueId && Get(runs, step) <= 0)
                    continue;

                int run = Get(runs, step);
                if (run <= 0)
                {
                    run = 1;
                    while (step + run < 16 && Get(steps, step + run) == valueId && Get(runs, step + run) <= 0)
                        run++;
                }
                run = Mathf.Clamp(run, 1, 16 - step);
                long duration = (long)run * SongData.TicksPerSixteenth;
                int root = Mathf.Clamp(21 + valueId - 1, 0, 127); // MATCHES live Audio Helm note mapping.
                if (chordModePlusOne == 0)
                {
                    output.Add(NewNote(root, step, duration, 127));
                    continue;
                }
                int mode = chordModePlusOne - 1;
                int[] intervals = mode == 1 ? new[] { 0, 3, 7 }
                    : mode == 2 ? new[] { 0, 4, 7, 10 }
                    : mode == 3 ? new[] { 0, 2, 7 }
                    : new[] { 0, 4, 7 };
                foreach (int interval in intervals)
                    output.Add(NewNote(Mathf.Clamp(root + interval, 0, 127), step, duration, 114));
            }
        }

        private static NoteEvent NewNote(int pitch, int step, long length, int velocity)
        {
            return new NoteEvent
            {
                pitch = pitch,
                startTick = (long)step * SongData.TicksPerSixteenth,
                durationTicks = length,
                velocity = velocity
            };
        }

        private static int Get(byte[] array, int index) { return array != null && index < array.Length ? array[index] : 0; }
        private static int Get(int[] array, int index) { return array != null && index < array.Length ? array[index] : 0; }
    }
}
