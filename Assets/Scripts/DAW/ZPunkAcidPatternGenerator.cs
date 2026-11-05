using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZPunk.DAW
{
    /// <summary>
    /// ACID-only, deterministic, monophonic 303 pattern writer. Does not touch
    /// SongData tracks, instrument patches, other clips, or shared song harmony.
    /// The CREATE editor owns the commit and undo/refresh path.
    /// </summary>
    internal static class ZPunkAcidPatternGenerator
    {
        internal static readonly string[] Scales =
            { "MINOR", "MAJOR", "DORIAN", "PHRYGIAN", "CHROMATIC" };
        internal static readonly string[] Keys =
            { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
        internal static readonly int[] Bars = { 1, 2, 4, 8, 16 };
        internal static readonly string[] Divisions = { "1/8", "1/16", "1/32" };
        internal static readonly int[] Densities = { 30, 45, 60, 75, 90 };
        internal static readonly int[] AccentChances = { 0, 20, 35, 50, 70 };
        internal static readonly int[] SlideChances = { 0, 15, 30, 45, 60 };
        internal static readonly int[] SwingAmounts = { 0, 15, 30, 45 };
        internal static readonly int[] LeapChances = { 0, 15, 30, 50 };
        internal static readonly string[] Gates = { "SHORT", "TIGHT", "LONG" };
        internal static readonly string[] Motifs = { "1 BAR", "2 BARS", "EVOLVE" };
        internal static readonly string[] Syncopations = { "STRAIGHT", "MIXED", "OFFBEAT" };

        private static readonly int[][] ScaleOffsets =
        {
            new[] { 0, 2, 3, 5, 7, 8, 10 },
            new[] { 0, 2, 4, 5, 7, 9, 11 },
            new[] { 0, 2, 3, 5, 7, 9, 10 },
            new[] { 0, 1, 3, 5, 7, 8, 10 },
            new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 }
        };

        internal sealed class Settings
        {
            internal int bars = 2, key = 9, scale = 0, octave = 2, range = 1;
            internal int division = 1, density = 2, accent = 2, slide = 2;
            internal int swing = 0, gate = 1, motif = 1, leap = 1;
            internal int syncopation = 1, variation = 1;
        }

        private struct Step
        {
            internal bool hit, accent, slide;
            internal int degree, upper;
        }

        internal static void Fill(ClipContentData content, Settings settings)
        {
            if (content == null || settings == null) return;
            int bars = Bars[Mathf.Clamp(settings.bars, 0, Bars.Length - 1)];
            int scaleIndex = Mathf.Clamp(settings.scale, 0, ScaleOffsets.Length - 1);
            int[] degrees = ScaleOffsets[scaleIndex];
            int tonic = Mathf.Clamp(settings.key, 0, 11);
            int root = 12 * (Mathf.Clamp(settings.octave, 1, 4) + 1) + tonic;
            int stepsPerBar = settings.division == 0 ? 8 : settings.division == 2 ? 32 : 16;
            long stepTicks = SongData.TicksPerBar4 / stepsPerBar;
            int density = Densities[Mathf.Clamp(settings.density, 0, Densities.Length - 1)];
            int accents = AccentChances[Mathf.Clamp(settings.accent, 0, AccentChances.Length - 1)];
            int slides = SlideChances[Mathf.Clamp(settings.slide, 0, SlideChances.Length - 1)];
            int swing = SwingAmounts[Mathf.Clamp(settings.swing, 0, SwingAmounts.Length - 1)];
            int leap = LeapChances[Mathf.Clamp(settings.leap, 0, LeapChances.Length - 1)];
            int octaveRange = Mathf.Clamp(settings.range, 0, 2);
            int gate = settings.gate == 0 ? 50 : settings.gate == 2 ? 98 : 76;
            int motifMode = Mathf.Clamp(settings.motif, 0, 2);
            int motifBars = motifMode == 0 ? 1 : 2;
            int sync = Mathf.Clamp(settings.syncopation, 0, 2);
            int seed = unchecked(0x303AC1D ^ (settings.variation * 314159) ^
                (tonic * 701) ^ (scaleIndex * 1361) ^ (bars * 99991) ^
                (settings.division * 179) ^ (settings.density * 71));
            var rng = new System.Random(seed);
            var source = new Step[motifBars, stepsPerBar];
            int beat = stepsPerBar / 4;
            for (int b = 0; b < motifBars; b++)
            {
                for (int i = 0; i < stepsPerBar; i++)
                {
                    bool onBeat = i % beat == 0;
                    double chance = density / 100.0;
                    chance += onBeat ? (sync == 0 ? .16 : sync == 1 ? .08 : -.12) :
                        (sync == 2 ? .14 : sync == 0 ? -.12 : 0);
                    bool hit = b == 0 && i == 0 || rng.NextDouble() < Math.Max(.05, Math.Min(.98, chance));
                    int degree = rng.NextDouble() < .48 ? 0 :
                        rng.NextDouble() < .34 ? Math.Min(4, degrees.Length - 1) : rng.Next(degrees.Length);
                    source[b, i] = new Step
                    {
                        hit = hit, degree = degree,
                        upper = octaveRange > 0 && rng.Next(100) < leap ? rng.Next(1, octaveRange + 1) : 0,
                        accent = rng.Next(100) < Math.Min(95, accents + (onBeat ? 13 : 0)),
                        slide = rng.Next(100) < slides
                    };
                }
            }

            content.kind = ClipKind.Midi;
            content.lengthTicks = bars * (long)SongData.TicksPerBar4;
            content.notes = new List<NoteEvent>(bars * stepsPerBar);
            content.drums = new List<DrumEvent>();
            content.chops = new List<ChopEvent>();
            NoteEvent previous = null;
            long previousStep = -999;
            for (int bar = 0; bar < bars; bar++)
            {
                int sourceBar = bar % motifBars;
                for (int i = 0; i < stepsPerBar; i++)
                {
                    Step cell = source[sourceBar, i];
                    // Keep the pattern recognisable across repeated bars, but allow
                    // independent small changes and larger evolving variations.
                    if (bar >= motifBars && rng.NextDouble() < (motifMode == 2 ? .30 : .08))
                    {
                        if (rng.NextDouble() < .28) cell.hit = !cell.hit;
                        else cell.degree = rng.Next(degrees.Length);
                        if (rng.NextDouble() < .25) cell.accent = !cell.accent;
                    }
                    if (!cell.hit) continue;
                    long absoluteStep = (long)bar * stepsPerBar + i;
                    long offset = i % 2 == 1 ? stepTicks * swing / 100 : 0;
                    long start = absoluteStep * stepTicks + offset;
                    int pitch = Mathf.Clamp(root + degrees[cell.degree] + cell.upper * 12, 0, 127);
                    // Glide is only meaningful when a preceding gate is close by.
                    // Overlap its previous note with the new one so a monophonic
                    // synth can slide without retriggering the filter envelope.
                    bool slide = cell.slide && previous != null && absoluteStep - previousStep <= 2;
                    if (slide)
                        previous.durationTicks = Math.Max(previous.durationTicks,
                            start - previous.startTick + Math.Max(1L, stepTicks / 3));
                    var note = new NoteEvent
                    {
                        pitch = pitch,
                        startTick = start,
                        durationTicks = Math.Max(1L, stepTicks * gate / 100),
                        velocity = cell.accent ? 118 : 93,
                        accent = cell.accent,
                        slide = slide
                    };
                    content.notes.Add(note);
                    previous = note;
                    previousStep = absoluteStep;
                }
            }
            // Never write a dangling note past the end of the clip.
            foreach (var note in content.notes)
                note.durationTicks = Math.Max(1L,
                    Math.Min(note.durationTicks, content.lengthTicks - note.startTick));
            content.generatedTag = "ACID / " + Keys[tonic] + " " + Scales[scaleIndex] +
                " / " + bars + " BARS / V" + settings.variation;
        }
    }
}
