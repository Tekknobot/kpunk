using System;

namespace ZPunk.DAW
{
    /// <summary>
    /// Compact, offline chord vocabulary transcribed from the supplied MIDI Chords pack.
    /// The pack contains transpositions of the same shapes; only C major / A minor
    /// were needed. No MIDI parser or thousands of MIDI assets ship on mobile.
    /// Profiles control harmony/pitch only, never note onsets, lengths or drum lanes.
    /// </summary>
    public static class ZPunkChordLibrary
    {
        public sealed class Chord
        {
            public readonly int RootSemitones; // Relative to the selected tonic.
            public readonly int Degree; // Nearest scale degree for melodic passing notes.
            public readonly int[] Intervals; // Pitch classes above the actual root.
            public readonly string Suffix;
            public Chord(int root, int degree, int[] intervals, string suffix)
            {
                RootSemitones = root; Degree = degree; Intervals = intervals; Suffix = suffix;
            }
            public bool Contains(int midiNote, int key)
            {
                int pc = Mod(midiNote - key - RootSemitones, 12);
                foreach (int interval in Intervals) if (interval == pc) return true;
                return false;
            }
            public int ToneNearest(int wanted, int fallback)
            {
                int best = fallback, distance = 100;
                foreach (int pitch in Intervals)
                {
                    int d = Math.Abs(pitch - wanted);
                    if (d < distance) { distance = d; best = pitch; }
                }
                return best;
            }
        }

        private static readonly int[] MajorSteps = { 0, 2, 4, 5, 7, 9, 11 };
        private static readonly int[] MinorSteps = { 0, 2, 3, 5, 7, 8, 10 };
        // Extracted from the pack's C major triad and extended-chord MIDI files.
        // Per degree: triad, seventh, extended color (root-relative pitch classes).
        private static readonly int[][][] MajorShapes = {
            new[] { new[] { 0, 4, 7 }, new[] { 0, 4, 7, 11 }, new[] { 0, 2, 4, 7, 11 } }, // 1
            new[] { new[] { 0, 3, 7 }, new[] { 0, 3, 7, 10 }, new[] { 0, 2, 3, 7, 10 } }, // 2
            new[] { new[] { 0, 3, 7 }, new[] { 0, 3, 7, 10 }, new[] { 0, 3, 5, 10 } }, // 3
            new[] { new[] { 0, 4, 7 }, new[] { 0, 4, 7, 11 }, new[] { 0, 2, 4, 7, 11 } }, // 4
            new[] { new[] { 0, 4, 7 }, new[] { 0, 4, 7, 10 }, new[] { 0, 2, 4, 7, 10 } }, // 5
            new[] { new[] { 0, 3, 7 }, new[] { 0, 3, 7, 10 }, new[] { 0, 2, 3, 7, 10 } }, // 6
            new[] { new[] { 0, 3, 6 }, new[] { 0, 3, 6, 10 }, new[] { 0, 3, 5, 6, 10 } }, // 7
        };

        // Extracted from the pack's A minor triad and extended-chord MIDI files.
        // Per degree: triad, seventh, extended color (root-relative pitch classes).
        private static readonly int[][][] MinorShapes = {
            new[] { new[] { 0, 3, 7 }, new[] { 0, 3, 7, 10 }, new[] { 0, 2, 3, 7, 10 } }, // 1
            new[] { new[] { 0, 3, 6 }, new[] { 0, 3, 6, 10 }, new[] { 0, 3, 5, 6, 10 } }, // 2
            new[] { new[] { 0, 4, 7 }, new[] { 0, 4, 7, 11 }, new[] { 0, 2, 4, 7, 11 } }, // 3
            new[] { new[] { 0, 3, 7 }, new[] { 0, 3, 7, 10 }, new[] { 0, 2, 3, 7, 10 } }, // 4
            new[] { new[] { 0, 3, 7 }, new[] { 0, 3, 7, 10 }, new[] { 0, 3, 5, 10 } }, // 5
            new[] { new[] { 0, 4, 7 }, new[] { 0, 4, 7, 11 }, new[] { 0, 2, 4, 7, 11 } }, // 6
            new[] { new[] { 0, 4, 7 }, new[] { 0, 4, 7, 10 }, new[] { 0, 2, 4, 7, 10 } }, // 7
        };

        // First four chord changes of selected advanced major MIDI progressions,
        // adapted to ZPUNK's existing one-chord-per-bar harmonic plan.
        private static readonly Chord[][] MajorProgressions = {
            new[] { // Advanced Major Prog 01
                new Chord(0, 0, new[] { 0, 4, 7, 11 }, "maj7"),
                new Chord(5, 3, new[] { 0, 2, 4, 7, 11 }, "maj9"),
                new Chord(2, 1, new[] { 0, 2, 3, 7, 10 }, "m9"),
                new Chord(7, 4, new[] { 0, 2, 4, 7, 10 }, "9"),
            },
            new[] { // Advanced Major Prog 02
                new Chord(0, 0, new[] { 0, 2, 7, 11 }, "maj9"),
                new Chord(5, 3, new[] { 0, 2, 7, 11 }, "maj7sus"),
                new Chord(3, 1, new[] { 0, 4, 7, 11 }, "maj7"),
                new Chord(8, 4, new[] { 0, 4, 7, 11 }, "maj7"),
            },
            new[] { // Advanced Major Prog 04
                new Chord(0, 0, new[] { 0, 2, 4, 7 }, "add9"),
                new Chord(4, 2, new[] { 0, 4, 7, 10 }, "7"),
                new Chord(5, 3, new[] { 0, 4, 7, 11 }, "maj7"),
                new Chord(5, 3, new[] { 0, 2, 3, 7, 9 }, "m6(9)"),
            },
            new[] { // Advanced Major Prog 08
                new Chord(9, 5, new[] { 0, 2, 3, 5, 10 }, "m11"),
                new Chord(8, 4, new[] { 0, 2, 4, 7, 11 }, "maj9"),
                new Chord(10, 5, new[] { 0, 2, 4, 7, 11 }, "maj9"),
                new Chord(0, 0, new[] { 0, 2, 4, 7 }, "add9"),
            },
        };

        // First four chord changes of selected advanced minor MIDI progressions,
        // adapted to ZPUNK's existing one-chord-per-bar harmonic plan.
        private static readonly Chord[][] MinorProgressions = {
            new[] { // Advanced Minor Prog 01
                new Chord(2, 1, new[] { 0, 3, 6, 10 }, "m7b5"),
                new Chord(7, 4, new[] { 0, 1, 4, 7, 10 }, "7b9"),
                new Chord(0, 0, new[] { 0, 2, 3, 7, 10 }, "m9"),
                new Chord(8, 5, new[] { 0, 2, 4, 7, 11 }, "maj9"),
            },
            new[] { // Advanced Minor Prog 02
                new Chord(0, 0, new[] { 0, 2, 3, 7, 10 }, "m9"),
                new Chord(10, 6, new[] { 0, 2, 4, 7 }, "add9"),
                new Chord(8, 5, new[] { 0, 2, 4, 7, 11 }, "maj9"),
                new Chord(7, 4, new[] { 0, 1, 4, 8, 10 }, "7alt"),
            },
            new[] { // Advanced Minor Prog 04
                new Chord(0, 0, new[] { 0, 2, 3, 7, 10 }, "m9"),
                new Chord(5, 3, new[] { 0, 2, 3, 7, 10 }, "m9"),
                new Chord(8, 5, new[] { 0, 2, 4, 7, 11 }, "maj9"),
                new Chord(7, 4, new[] { 0, 4, 8, 10 }, "7alt"),
            },
            new[] { // Advanced Minor Prog 06
                new Chord(0, 0, new[] { 0, 2, 3, 5, 10 }, "m11"),
                new Chord(8, 5, new[] { 0, 2, 4, 7, 11 }, "maj9"),
                new Chord(3, 2, new[] { 0, 2, 4, 7, 11 }, "maj9"),
                new Chord(5, 3, new[] { 0, 2, 3, 7, 10 }, "m9"),
            },
        };

        public const int LibraryProgressionCount = 4;
        private static readonly string[] ProgressionNames = { "MIDI SOUL", "MIDI MODAL", "MIDI TURNAROUND", "MIDI COLOR" };

        public static bool IsLibraryChoice(int choice)
        {
            return choice >= 9 && choice < 9 + LibraryProgressionCount;
        }
        public static string ProgressionLabel(int choice)
        {
            return IsLibraryChoice(choice) ? ProgressionNames[choice - 9] : "GENRE DEFAULT";
        }
        public static Chord ForBar(int genre, int style, bool minor, int degree, int choice, int bar, int offset)
        {
            if (IsLibraryChoice(choice))
            {
                var series = (minor ? MinorProgressions : MajorProgressions)[choice - 9];
                return series[Mod(bar + offset, series.Length)];
            }
            degree = Mod(degree, 7);
            var shapes = (minor ? MinorShapes : MajorShapes)[degree];
            // Style decides richness, not track or variation: all MIDI tracks agree.
            // Techno keeps lean triads / restrained suspended colors.
            int family = genre == 2 ? 0 : genre == 1 || genre == 5 ? 2 :
                         genre == 0 ? 1 + (style == 2 ? 1 : 0) :
                         genre == 4 ? 2 : 1;
            if (genre == 2 && style == 1 && (bar + offset) % 2 == 1) family = 1;
            if ((genre == 0 || genre == 3) && style != 2 && (bar + offset) % 3 == 2) family = 2;
            family = Math.Min(2, family);
            int[] pitches = shapes[family];
            int root = (minor ? MinorSteps : MajorSteps)[degree];
            return new Chord(root, degree, pitches, "");
        }
        public static int[] LibraryDegrees(bool minor, int choice)
        {
            if (!IsLibraryChoice(choice)) return null;
            Chord[] chords = (minor ? MinorProgressions : MajorProgressions)[choice - 9];
            int[] result = new int[chords.Length];
            for (int i = 0; i < result.Length; i++) result[i] = chords[i].Degree;
            return result;
        }
        public static string LibraryChordNames(int key, bool minor, int choice, string[] keyNames)
        {
            if (!IsLibraryChoice(choice)) return "";
            Chord[] chords = (minor ? MinorProgressions : MajorProgressions)[choice - 9];
            string[] result = new string[chords.Length];
            for (int i = 0; i < result.Length; i++)
                result[i] = keyNames[Mod(key + chords[i].RootSemitones, 12)] + chords[i].Suffix;
            return string.Join(" - ", result);
        }
        private static int Mod(int a, int b) { return (a % b + b) % b; }
    }
}
