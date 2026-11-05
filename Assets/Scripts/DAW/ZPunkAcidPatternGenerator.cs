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
        internal static readonly string[] Motifs =
            { "1 BAR", "2 BARS", "EVOLVE", "90S ACID", "GROOVE", "DIRTY" };
        internal static readonly string[] Syncopations = { "STRAIGHT", "MIXED", "OFFBEAT" };

        private static readonly int[][] ScaleOffsets =
        {
            new[] { 0, 2, 3, 5, 7, 8, 10 },
            new[] { 0, 2, 4, 5, 7, 9, 11 },
            new[] { 0, 2, 3, 5, 7, 9, 10 },
            new[] { 0, 1, 3, 5, 7, 8, 10 },
            new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 }
        };

        // On/off rhythms are 16-step abstractions of the seven user-supplied
        // Roland Cloud MIDI exports. Pitches are NEVER copied: transposable
        // notes are composed afresh within the selected key/scale and octave.
        // The exported MIDI provides no separate slide flags; gate overlap is
        // used only as a probability hint, not as an exact slide transcription.
        private static readonly string[] RolandGrooves = {
            "X.X.XX.X..X.XX..", // 1: sparse, held step overlaps
            "XXXXXXXXXXX.XXX.", // 2: busy, accented sixteenths
            "XXX.XXXXXXXXX.XX", // 3: repeated root and octave movement
            "XXXXXX.XX..XX.X.", // 4: syncopated staccato accents
            "XXXXXXXXXXXXXXXX", // 5: driving repeated sixteenths
            "XXX.XXXXXXXXXXX.", // 6: dense repeated movement
            "X.XXXXXXXXXXXXXX"  // 7: active, sharp staccato
        };
        // The file named "da funk.mid" has a one-bar broken/syncopated
        // twelve-hit contour, unlike the numbered loops. This is RHYTHM ONLY;
        // original pitches/intervals are never embedded or recreated.
        private const string DirtyGroove = "XXX.X.X.X.XXXXXX";
        private static readonly string[] RolandAccents = {
            "....X...........", "XXXXX.XXX.X.X...", "X.......X...X...",
            "XXXX.X.....X..X.", "........X.......", "X.X.XXXXXXXX.XX.",
            "X...X.X..X..X..."
        };

        internal sealed class Settings
        {
            internal int bars = 2, key = 9, scale = 0, octave = 2, range = 1;
            internal int division = 1, density = 2, accent = 2, slide = 2;
            internal int swing = 0, gate = 1, motif = 4, leap = 1;
            internal int syncopation = 1, variation = 1;
            // Scale-degree bitmask (up to 12 chromatic notes). -1 means all
            // notes in the currently selected scale. It is relative to the
            // tonic, so changing key transposes the chosen note pool.
            internal int noteMask = -1;
        }

        // One note switch per degree in the displayed scale. The UI and writer
        // both use these helpers, ensuring excluded pitches cannot be generated.
        internal static int DegreeCount(int scale)
        {
            return ScaleOffsets[Mathf.Clamp(scale, 0, ScaleOffsets.Length - 1)].Length;
        }

        internal static string DegreeName(int key, int scale, int degree)
        {
            int[] offsets = ScaleOffsets[Mathf.Clamp(scale, 0, ScaleOffsets.Length - 1)];
            return Keys[(Mathf.Clamp(key, 0, 11) + offsets[Mathf.Clamp(degree, 0, offsets.Length - 1)]) % 12];
        }

        internal static bool DegreeEnabled(Settings settings, int degree)
        {
            return settings != null && degree >= 0 && degree < DegreeCount(settings.scale) &&
                (settings.noteMask & (1 << degree)) != 0;
        }

        internal static int EnabledDegreeCount(Settings settings)
        {
            if (settings == null) return 0;
            int count = 0;
            for (int i = 0; i < DegreeCount(settings.scale); i++)
                if (DegreeEnabled(settings, i)) count++;
            return count;
        }

        internal static bool ToggleDegree(Settings settings, int degree)
        {
            if (settings == null || degree < 0 || degree >= DegreeCount(settings.scale)) return false;
            int validMask = (1 << DegreeCount(settings.scale)) - 1;
            int nextMask = (settings.noteMask ^ (1 << degree)) & validMask;
            if (nextMask == 0) return false; // Never generate an empty pool.
            settings.noteMask = nextMask;
            return true;
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
            int[] scaleDegrees = ScaleOffsets[scaleIndex];
            // Filter before ANY melodic decisions. Phrase anchors, generated
            // neighbours, mutations and octave leaps all draw from this pool.
            var allowedOffsets = new List<int>(scaleDegrees.Length);
            for (int degree = 0; degree < scaleDegrees.Length; degree++)
                if ((settings.noteMask & (1 << degree)) != 0)
                    allowedOffsets.Add(scaleDegrees[degree]);
            if (allowedOffsets.Count == 0) allowedOffsets.Add(scaleDegrees[0]);
            int[] degrees = allowedOffsets.ToArray();
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
            int motifMode = Mathf.Clamp(settings.motif, 0, Motifs.Length - 1);
            bool nineties = motifMode >= 3;
            bool rolandGroove = motifMode == 4;
            bool dirtyGroove = motifMode == 5;
            bool basedOnMidi = rolandGroove || dirtyGroove;
            int motifBars = (motifMode == 0 || dirtyGroove) ? 1 : 2;
            int sync = Mathf.Clamp(settings.syncopation, 0, 2);
            // Keep the underlying random sequence stable while the user sculpts
            // key, scale or density with the LIVE controls. New variation, motif,
            // grid and groove are the intentional ways to choose new phrasing.
            int seed = unchecked(0x303AC1D ^ (settings.variation * 314159) ^
                (bars * 99991) ^ (settings.division * 179) ^
                (motifMode * 379) ^ (settings.syncopation * 139));
            var rng = new System.Random(seed);
            int grooveIndex = rng.Next(RolandGrooves.Length);
            var source = new Step[motifBars, stepsPerBar];
            int beat = stepsPerBar / 4;
            for (int b = 0; b < motifBars; b++)
            {
                int phraseHome = b == 0 ? 0 : (rng.NextDouble() < .6 ? 0 :
                    (rng.NextDouble() < .5 ? 2 : 4) % degrees.Length);
                int previousDegree = phraseHome;
                for (int i = 0; i < stepsPerBar; i++)
                {
                    bool onBeat = i % beat == 0;
                    double chance = density / 100.0;
                    chance += onBeat ? (sync == 0 ? .16 : sync == 1 ? .08 : -.12) :
                        (sync == 2 ? .14 : sync == 0 ? -.12 : 0);
                    if (nineties)
                    {
                        // Asymmetrical 16-step groove, with different 4-step
                        // answers in bar two. Every grid size inherits it.
                        int sixteenth = i * 16 / stepsPerBar;
                        int pulse = sixteenth & 3;
                        double shape = pulse == 0 ? -.15 : pulse == 1 ? .15 :
                            pulse == 2 ? -.10 : .12;
                        chance += shape + (b == 1 && (sixteenth & 7) == 5 ? .14 : 0);
                        // Small negative space at phrase openings is a classic
                        // acid-house device, not a transcription of any song.
                        if (sixteenth == 0) chance *= .40;
                        if (stepsPerBar == 32) chance *= .78;
                    }
                    bool hit;
                    int sixteenthPos = i * 16 / stepsPerBar;
                    if (basedOnMidi)
                    {
                        string groove = dirtyGroove ? DirtyGroove : RolandGrooves[grooveIndex];
                        // 1/32 adds occasional optional passing hits rather than
                        // duplicating every original sixteenth note.
                        bool halfStep = stepsPerBar == 32 && (i & 1) == 1;
                        bool templateHit = groove[sixteenthPos] == 'X' && !halfStep;
                        double densityShape = (density - 30) / 60.0;
                        double probability = templateHit ? .59 + densityShape * .41 :
                            (halfStep ? .015 : .035) + densityShape * (halfStep ? .06 : .11);
                        if (sync == 2 && sixteenthPos % 4 == 0) probability *= .74;
                        if (sync == 2 && sixteenthPos % 4 == 3) probability *= 1.15;
                        if (b == 1 && (sixteenthPos == 7 || sixteenthPos == 15))
                            probability = Math.Min(1.0, probability + .12);
                        hit = rng.NextDouble() < probability;
                    }
                    else
                        hit = (!nineties && b == 0 && i == 0) ||
                            rng.NextDouble() < Math.Max(.04, Math.Min(.96, chance));
                    int degree;
                    if (nineties)
                    {
                        // Older 303 exports are dominated by repeating anchor
                        // tones and short responses, interrupted by octave jumps.
                        // DIRTY 303 uses more chromatic *motion* when CHROMATIC
                        // is chosen, but never leaves the explicitly chosen scale.
                        double choice = rng.NextDouble();
                        if (i % beat == 0 && choice < (basedOnMidi ? .68 : .55))
                            degree = phraseHome;
                        else if (choice < (dirtyGroove ? .34 : .48))
                            degree = previousDegree;
                        else if (choice < (dirtyGroove ? .85 : .77))
                        {
                            int movement = rng.NextDouble() < .78 ? 1 : 2;
                            degree = (previousDegree + (rng.Next(2) == 0 ? movement :
                                degrees.Length - movement)) % degrees.Length;
                        }
                        else degree = rng.Next(degrees.Length);
                    }
                    else
                    {
                        degree = rng.NextDouble() < .48 ? 0 :
                            rng.NextDouble() < .34 ? Math.Min(4, degrees.Length - 1) :
                            rng.Next(degrees.Length);
                    }
                    int leapChance = leap == 0 ? 0 :
                        leap + (basedOnMidi ? (dirtyGroove ? 14 : 11) : 0);
                    int upper = octaveRange > 0 && rng.Next(100) < leapChance ?
                        rng.Next(1, octaveRange + 1) : 0;
                    // For dense Roland-style patterns, most notes remain close
                    // to the bass anchor; rare leaps make them feel more dramatic.
                    if (basedOnMidi && leap > 0 && upper == 0 && hit &&
                        i % beat == 0 && octaveRange > 0 && rng.NextDouble() < .08)
                        upper = rng.Next(1, octaveRange + 1);
                    bool templateAccent = rolandGroove &&
                        RolandAccents[grooveIndex][sixteenthPos] == 'X';
                    int accentChance = accents + (onBeat ? (nineties ? 4 : 13) :
                        nineties && i % beat == beat - 1 ? 12 : 0);
                    if (accents == 0) accentChance = 0; // OFF really is OFF
                    else
                    {
                        if (templateAccent) accentChance += 14;
                        if (dirtyGroove && (sixteenthPos == 2 || sixteenthPos == 10 ||
                            sixteenthPos == 14)) accentChance += 12;
                    }
                    // The MIDI exports have note overlaps but do not include
                    // distinct slide flags. Never add a slide with OFF selected.
                    int slideChance = slides == 0 ? 0 : slides +
                        (basedOnMidi && (i + 1) % beat == 0 ? 8 : 0);
                    source[b, i] = new Step
                    {
                        hit = hit, degree = degree, upper = upper,
                        accent = rng.Next(100) < Math.Min(95, accentChance),
                        slide = rng.Next(100) < Math.Min(95, slideChance)
                    };
                    if (hit) previousDegree = degree;
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
                    if (bar >= motifBars && rng.NextDouble() <
                        (motifMode == 2 ? .30 : nineties ? .075 : .08))
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
                    bool slide = cell.slide && previous != null &&
                        absoluteStep - previousStep <= 2 &&
                        (nineties || previous.pitch != pitch);
                    if (slide)
                        previous.durationTicks = Math.Max(previous.durationTicks,
                            start - previous.startTick + Math.Max(1L, stepTicks / 3));
                    // Roland exports mix clipped staccato gates with longer
                    // tails. Humanize articulation without changing the grid,
                    // honouring the selected SHORT / TIGHT / LONG setting.
                    int noteGate = gate;
                    if (basedOnMidi && settings.gate != 2)
                    {
                        int variation = ((bar + i * 3 + grooveIndex) % 5 - 2) * 9;
                        noteGate = Math.Max(36, Math.Min(96,
                            gate + variation + (cell.accent ? -7 : 0)));
                    }
                    var note = new NoteEvent
                    {
                        pitch = pitch,
                        startTick = start,
                        durationTicks = Math.Max(1L, stepTicks * noteGate / 100),
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
                " / " + bars + " BARS / " +
                Motifs[motifMode] + " / V" + settings.variation;
        }
    }
}
