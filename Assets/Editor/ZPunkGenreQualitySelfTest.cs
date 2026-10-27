#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace ZPunk.DAW
{
    // Optional editor-only smoke test. Does not ship to devices or alter the UI.
    public static class ZPunkGenreQualitySelfTest
    {
        [MenuItem("ZPUNK/Diagnostics/Test Genre Generator (2.2.2)")]
        public static void Run()
        {
            int passes = 0;
            int styles = 0;
            if (ZPunkGenreGenerator.Genres.Length != 6)
                throw new InvalidOperationException("The six original genres were changed.");

            for (int genre = 0; genre < ZPunkGenreGenerator.Genres.Length; genre++)
            {
                styles += ZPunkGenreGenerator.Styles[genre].Length;
                for (int style = 0; style < ZPunkGenreGenerator.Styles[genre].Length; style++)
                {
                    foreach (TrackKind kind in new[] { TrackKind.Drums, TrackKind.Bass, TrackKind.Synth, TrackKind.Pads })
                    {
                        for (int variation = 1; variation <= 3; variation++)
                        {
                            var clip = new ClipContentData();
                            bool chords = kind == TrackKind.Synth && variation == 3;
                            ZPunkGenreGenerator.Fill(clip, kind, genre, style, 4, 9, true, variation,
                                                     synthChords: chords, progressionChoice: 2);
                            Check(clip, kind, genre, style);
                            passes++;
                        }
                    }

                    // Unlike drum clips, offbeat HOUSE chord stabs must not have a
                    // synthetic note inserted at tick zero to fill the silence.
                    if (genre == 1 && style == 0)
                    {
                        var chord = new ClipContentData();
                        ZPunkGenreGenerator.Fill(chord, TrackKind.Synth, genre, style, 4, 9, true, 1,
                                                 synthChords: true);
                        foreach (var note in chord.notes)
                            if (note.startTick == 0)
                                throw new InvalidOperationException("HOUSE stabs were shifted to tick zero.");
                    }
                }
            }
            if (styles != 19)
                throw new InvalidOperationException("The 19 original styles were changed.");
            Debug.Log("ZPUNK 2.2.2 generator audit: " + passes + " clips verified across " +
                      ZPunkGenreGenerator.Genres.Length + " genres and " + styles + " styles.");
        }

        private static void Check(ClipContentData clip, TrackKind kind, int genre, int style)
        {
            if (clip.lengthTicks != 4 * SongData.TicksPerBar4)
                throw new InvalidOperationException("Incorrect clip length.");
            if (kind == TrackKind.Drums)
            {
                if (clip.drums == null || clip.drums.Count == 0)
                    throw new InvalidOperationException("Empty drum groove.");
                foreach (var hit in clip.drums)
                {
                    if (hit.startTick < 0 || hit.startTick >= clip.lengthTicks ||
                        hit.lane < 0 || hit.lane > 7 || hit.velocity < 1 || hit.velocity > 127)
                        throw new InvalidOperationException("Invalid drum event.");
                }
                if (genre == 5)
                {
                    // French House retains all four anchors despite humanization.
                    for (int bar = 0; bar < 4; bar++)
                        for (int beat = 0; beat < 4; beat++)
                        {
                            long tick = (long)bar * SongData.TicksPerBar4 +
                                        (long)beat * SongData.TicksPerQuarter;
                            bool found = false;
                            foreach (var hit in clip.drums)
                                if (hit.lane == 0 && hit.startTick == tick) { found = true; break; }
                            if (!found) throw new InvalidOperationException("French House kick anchor missing: " +
                                                                          style + "/" + bar + "/" + beat);
                        }
                }
            }
            else
            {
                if (clip.notes == null || clip.notes.Count == 0)
                    throw new InvalidOperationException("Empty MIDI clip.");
                foreach (var note in clip.notes)
                    if (note.startTick < 0 || note.startTick >= clip.lengthTicks ||
                        note.durationTicks <= 0 || note.startTick + note.durationTicks > clip.lengthTicks ||
                        note.pitch < 0 || note.pitch > 127 || note.velocity < 1 || note.velocity > 127)
                        throw new InvalidOperationException("Invalid MIDI note in " + kind + ".");
                if (kind == TrackKind.Pads)
                    for (int bar = 0; bar < 4; bar++)
                    {
                        long from = (long)bar * SongData.TicksPerBar4;
                        long until = from + SongData.TicksPerBar4;
                        bool active = false;
                        foreach (var note in clip.notes)
                            if (note.startTick < until && note.startTick + note.durationTicks > from)
                            { active = true; break; }
                        if (!active) throw new InvalidOperationException("Silent pad bar for " +
                                                                      genre + "/" + style + "/" + bar);
                    }
            }
        }
    }
}
#endif
