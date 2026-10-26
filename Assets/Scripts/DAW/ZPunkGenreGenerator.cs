using System;
using System.Collections.Generic;

namespace ZPunk.DAW
{
    /// <summary>
    /// Deterministic, offline groove and harmony generator. All results are ordinary editable
    /// SongData events, with no external models, samples or network dependencies.
    /// Reusing a key/genre/style/variation yields the same phrase on every platform.
    /// </summary>
    public static class ZPunkGenreGenerator
    {
        public static readonly string[] Genres = { "BOOM BAP", "HOUSE", "TECHNO", "BREAKS", "FUNK", "FRENCH HOUSE" };
        public static readonly string[][] Styles = {
            new[] { "CLASSIC", "DUSTY", "SYNCOPATED" },
            new[] { "CLASSIC", "DEEP", "JACKIN", "90s" },
            new[] { "DRIVING", "MINIMAL", "INDUSTRIAL" },
            new[] { "AMEN", "JUNGLE", "HALF-TIME" },
            new[] { "DISCO", "POCKET", "ELECTRO" },
            new[] { "FILTER", "DISCO", "TOUCH" }
        };
        public static readonly string[] Keys = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
        private static readonly int[] Major = { 0, 2, 4, 5, 7, 9, 11 };
        private static readonly int[] Minor = { 0, 2, 3, 5, 7, 8, 10 };

        public static string Label(int genre, int style, int key, bool minor, bool synthChords = false)
        {
            genre = Clamp(genre, 0, Genres.Length - 1);
            return Genres[genre] + " / " + Styles[genre][Clamp(style, 0, Styles[genre].Length - 1)] +
                   (synthChords ? " / CHORDS" : "") + " / " +
                   Keys[Mod(key, 12)] + (minor ? " MIN" : " MAJ");
        }
        public static string Description(int g, int s)
        {
            string[][] d = {
                new[] { "Weighted backbeat, broken kick and laid-back hats", "Spacious drums with ghosts and understated swing", "Off-grid-feeling syncopation with answering hits" },
                new[] { "Four-to-floor, offbeat hats and rolling bass", "Warm, open spaces and restrained chord movement", "Jackin accents, swung-feel hats and chord stabs", "90s Eurodance/rave hooks with call-and-response, offbeat pickups and bold chord answers" },
                new[] { "Steady machine pulse with evolving punctuation", "Sparse, hypnotic drums and focused low notes", "Hard syncopated percussion and clipped motifs" },
                new[] { "Break-style ghost snares and rolling kick variations", "Denser jungle accents with restless hat patterns", "Half-time backbeat with spacious, heavy hits" },
                new[] { "Disco pulse, tight claps and bright offbeat percussion", "Dry kick/snare pocket, ghost hits and loose syncopation", "Electro-funk accents, crisp hats and syncopated kicks" },
                new[] { "Four-on-the-floor with clipped filtered disco accents", "Tight disco drums with playful offbeat hats", "Bouncy French-touch percussion and small fills" }
            };
            return d[Clamp(g, 0, Genres.Length - 1)][Clamp(s, 0, d[Clamp(g, 0, Genres.Length - 1)].Length - 1)];
        }
        public static string BassDescription(int g, int s)
        {
            string[][] d = {
                new[] { "Lazy low-end pocket with octave replies", "Sparse soulful bass and muted pickups", "Broken sixteenth-note bass with syncopated fifths" },
                new[] { "Disco-style offbeat bounce and octave movement", "Warm sustained roots with restrained funk accents", "Tight jackin sixteenths and chromatic turnarounds", "Classic 90s house bass bounce with octave accents" },
                new[] { "Hypnotic root pulse with occasional octave jumps", "Minimal long notes with deliberate gaps", "Clipped industrial syncopation and fifths" },
                new[] { "Broken low-end call and response", "Restless bass movement with octave accents", "Heavy spaced roots with sharp pickups" },
                new[] { "Disco-funk octave bounce and short plucked notes", "Deep pocket with sparse fifths and rhythmic silence", "Electro-funk sixteenths, octave pops and passing tones" },
                new[] { "Filtered repetitive octave bass and short pickups", "Disco-root bounce with fifths and sixteenth-note silence", "Rubbery syncopated French-touch bass with turnarounds" }
            };
            return d[Clamp(g, 0, Genres.Length - 1)][Clamp(s, 0, d[Clamp(g, 0, Genres.Length - 1)].Length - 1)];
        }
        public static string ChordsDescription(int g, int s)
        {
            string[][] d = {
                new[] { "Soulful seventh/ninth voicings and late chord replies", "Slow-moving warm jazz colors and open space", "Short, syncopated extended chord punches" },
                new[] { "Seventh chord offbeats with smooth voice leading", "Long deep-house voicings with warm extensions", "Clipped piano-style sevenths and quick answers", "90s rave piano/organ stabs, syncopated eighth-note punches and anthemic chord changes" },
                new[] { "Restrained dark triads with machine-like repeats", "Open fifths, suspended intervals and plenty of air", "Short, tense minor-flavored harmonic hits" },
                new[] { "Tight soulful stabs between snare accents", "Chopped voicings with quick delayed replies", "Sustained seventh colors over a half-time pulse" },
                new[] { "Short disco sevenths and ninths with rhythmic replies", "Sparse, warm ninth voicings and tightly muted stabs", "Bright clipped sevenths with synth-funk syncopation" },
                new[] { "Filtered disco sevenths and clipped offbeat stabs", "Classic French-touch piano chords with rhythmic responses", "Short, bright 90s-influenced house chord punches" }
            };
            return d[Clamp(g, 0, Genres.Length - 1)][Clamp(s, 0, d[Clamp(g, 0, Genres.Length - 1)].Length - 1)];
        }

        public static void Fill(ClipContentData clip, TrackKind kind, int genre, int style,
                                int bars, int key, bool minor, int variation, bool synthChords = false,
                                int[] harmonyDegrees = null, int[][] harmonyVoices = null,
                                int progressionChoice = 0, int progressionBarOffset = 0)
        {
            if (clip == null) throw new ArgumentNullException(nameof(clip));
            if (kind == TrackKind.Chops) return;
            genre = Clamp(genre, 0, Genres.Length - 1);
            style = Clamp(style, 0, Styles[genre].Length - 1);
            bars = Clamp(bars, 1, 4);
            key = Mod(key, 12);
            progressionChoice = Clamp(progressionChoice, 0, ProgressionChoiceCount - 1);
            clip.kind = kind == TrackKind.Drums ? ClipKind.Drums : ClipKind.Midi;
            clip.lengthTicks = bars * SongData.TicksPerBar4;
            clip.generatedTag = Label(genre, style, key, minor, synthChords && kind == TrackKind.Synth) +
                (kind != TrackKind.Drums && progressionChoice > 0 ? " / PROG#" + progressionChoice : "") +
                " / V" + variation;
            if (clip.drums == null) clip.drums = new List<DrumEvent>();
            if (clip.notes == null) clip.notes = new List<NoteEvent>();
            clip.drums.Clear();
            clip.notes.Clear();
            if (clip.chops != null) clip.chops.Clear();
            int seed = unchecked(variation * 104729 + genre * 3659 + style * 1619 + (int)kind * 307 + key * 47 + (minor ? 173 : 97));
            if (kind == TrackKind.Drums)
            {
                // Keep the tick-based drum contract; develop each genre's own rhythm.
                var drumRng = new Random(seed);
                for (int bar = 0; bar < bars; bar++)
                    GenerateDrums(clip, genre, style, bar, bars, variation, drumRng);
                AlignToStart(clip);
            }
            else
            {
                int[] harmony = MakeHarmonyPlan(bars, genre, minor, progressionChoice,
                                                 progressionBarOffset, harmonyDegrees);
                List<NoteEvent> bestNotes = null;
                double bestScore = double.MinValue;
                // All candidates are deterministic and editable. Score BEFORE
                // humanization; expressive jitter should not dictate composition.
                const int CandidateCount = 12;
                for (int candidate = 0; candidate < CandidateCount; candidate++)
                {
                    clip.notes.Clear();
                    var candidateRng = new Random(CandidateSeed(seed, candidate, harmony, bars));
                    GenerateMidiCandidate(clip, kind, genre, style, bars, key, minor,
                                          harmony, synthChords, harmonyVoices, candidateRng);
                    double score = ScoreCandidate(clip, kind, genre, style, minor, key,
                                                  harmony, synthChords && kind == TrackKind.Synth,
                                                  harmonyVoices);
                    if (bestNotes == null || score > bestScore)
                    {
                        bestScore = score;
                        bestNotes = CopyNotes(clip.notes);
                    }
                }
                clip.notes.Clear();
                if (bestNotes != null) clip.notes.AddRange(bestNotes);
                // IMPORTANT: Never call AlignToStart for new MIDI. Moving a whole
                // phrase makes HOUSE offbeat stabs land on kick downbeats.
            }
            // Apply expressive timing after the quantized musical patterns are built.
            // The seed keeps rerendering, Undo/history and exported MIDI repeatable.
            HumanizePhrase(clip, kind, genre, style, variation, seed);
            if (clip.notes.Count > 0)
                clip.notes.Sort((a, b) => a.startTick == b.startTick ? a.pitch.CompareTo(b.pitch) : a.startTick.CompareTo(b.startTick));
            if (clip.drums.Count > 0)
                clip.drums.Sort((a, b) => a.startTick == b.startTick ? a.lane.CompareTo(b.lane) : a.startTick.CompareTo(b.startTick));
        }

        /// <summary>
        /// Moves the earliest generated drum hit or MIDI note to clip-local tick 0,
        /// preserving the exact timing differences between all events. This only
        /// edits event offsets inside content: arrangement clip placements remain
        /// where the user put them. Also repairs old generation-history snapshots
        /// when they are restored.
        /// </summary>
        public static void AlignToStart(ClipContentData clip)
        {
            if (clip == null) return;
            long earliest = long.MaxValue;
            if (clip.kind == ClipKind.Drums && clip.drums != null)
            {
                foreach (var hit in clip.drums)
                    if (hit != null && hit.startTick < earliest) earliest = hit.startTick;
                if (earliest == long.MaxValue || earliest <= 0) return;
                foreach (var hit in clip.drums)
                    if (hit != null) hit.startTick -= earliest;
            }
            else if (clip.kind == ClipKind.Midi && clip.notes != null)
            {
                foreach (var note in clip.notes)
                    if (note != null && note.startTick < earliest) earliest = note.startTick;
                if (earliest == long.MaxValue || earliest <= 0) return;
                foreach (var note in clip.notes)
                    if (note != null) note.startTick -= earliest;
            }
        }

        // 0 = original genre-specific harmony for backwards compatibility.
        // 1..8 = shared, explicitly selected progressions across BASS/SYNTH/PADS.
        // All are diatonic to the current major or natural-minor key; array values
        // are ZERO-BASED scale degrees, not MIDI semitones.
        public const int ProgressionChoiceCount = 9;
        private static readonly int[][] MinorProgressions = {
            new[] { 0, 5, 2, 6 }, // i - VI - III - VII, classic 90s eurodance
            new[] { 0, 6, 5, 6 }, // i - VII - VI - VII, anthemic turnaround
            new[] { 0, 3, 5, 4 }, // i - iv - VI - v
            new[] { 0, 2, 6, 5 }, // i - III - VII - VI
            new[] { 0, 5, 3, 6 }, // i - VI - iv - VII
            new[] { 0, 4, 5, 3 }, // i - v - VI - iv
            new[] { 0, 3, 6, 2 }, // i - iv - VII - III
            new[] { 5, 6, 0, 0 }  // VI - VII - i - i, tension / release
        };
        private static readonly int[][] MajorProgressions = {
            new[] { 0, 4, 5, 3 }, // I - V - vi - IV
            new[] { 0, 5, 3, 4 }, // I - vi - IV - V
            new[] { 0, 3, 4, 3 }, // I - IV - V - IV
            new[] { 5, 3, 0, 4 }, // vi - IV - I - V
            new[] { 0, 2, 3, 4 }, // I - iii - IV - V
            new[] { 0, 1, 3, 4 }, // I - ii - IV - V
            new[] { 0, 3, 1, 4 }, // I - IV - ii - V
            new[] { 3, 4, 0, 5 }  // IV - V - I - vi
        };
        private static readonly string[] MinorProgressionLabels = {
            "i - VI - III - VII", "i - VII - VI - VII", "i - iv - VI - v", "i - III - VII - VI",
            "i - VI - iv - VII", "i - v - VI - iv", "i - iv - VII - III", "VI - VII - i - i"
        };
        private static readonly string[] MajorProgressionLabels = {
            "I - V - vi - IV", "I - vi - IV - V", "I - IV - V - IV", "vi - IV - I - V",
            "I - iii - IV - V", "I - ii - IV - V", "I - IV - ii - V", "IV - V - I - vi"
        };

        public static int[] ProgressionFor(int genre, bool minor, int choice)
        {
            if (choice < 1 || choice >= ProgressionChoiceCount) return Progression(genre, minor);
            return minor ? MinorProgressions[choice - 1] : MajorProgressions[choice - 1];
        }

        public static string ProgressionLabel(int genre, bool minor, int choice)
        {
            if (choice < 1 || choice >= ProgressionChoiceCount) return "GENRE DEFAULT";
            return choice.ToString("00") + "  " +
                (minor ? MinorProgressionLabels[choice - 1] : MajorProgressionLabels[choice - 1]);
        }

        // Musical names for the CURRENT key, e.g., A minor: Am - F - C - G.
        public static string ProgressionChordNames(int genre, int key, bool minor, int choice)
        {
            int[] degrees = ProgressionFor(genre, minor, choice);
            string[] names = new string[degrees.Length];
            for (int i = 0; i < degrees.Length; i++)
            {
                int degree = degrees[i];
                int tonic = Mod(key + Scale(degree, minor), 12);
                int third = Scale(degree + 2, minor) - Scale(degree, minor);
                int fifth = Scale(degree + 4, minor) - Scale(degree, minor);
                string quality = third == 3 && fifth == 6 ? "dim" : third == 3 ? "m" : "";
                names[i] = Keys[tonic] + quality;
            }
            return string.Join(" - ", names);
        }

        public static int ProgressionFromTag(string generatedTag)
        {
            if (string.IsNullOrEmpty(generatedTag)) return 0;
            const string marker = " / PROG#";
            int start = generatedTag.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return 0;
            start += marker.Length;
            int end = start;
            while (end < generatedTag.Length && char.IsDigit(generatedTag[end])) end++;
            int parsed;
            return int.TryParse(generatedTag.Substring(start, end - start), out parsed)
                ? Clamp(parsed, 0, ProgressionChoiceCount - 1) : 0;
        }

        // Scale degrees allow major and natural-minor progressions to share a tonic.
        private static int[] Progression(int g, bool minor)
        {
            if (g == 0) return minor ? new[] { 0, 5, 3, 4 } : new[] { 0, 5, 3, 4 };
            if (g == 1) return minor ? new[] { 0, 3, 5, 4 } : new[] { 0, 3, 5, 4 };
            if (g == 2) return new[] { 0, 0, 0, 4 };
            if (g == 3) return minor ? new[] { 0, 5, 3, 4 } : new[] { 0, 5, 1, 4 };
            // FUNK holds a tonic-centered pocket, FRENCH HOUSE moves like disco.
            if (g == 4) return minor ? new[] { 0, 0, 3, 4 } : new[] { 0, 0, 3, 4 };
            return minor ? new[] { 0, 5, 3, 4 } : new[] { 0, 3, 4, 3 };
        }
        private static int Scale(int degree, bool minor)
        {
            int[] notes = minor ? Minor : Major;
            return 12 * (int)Math.Floor(degree / 7.0) + notes[Mod(degree, 7)];
        }
        private static int Pitch(int octaveBase, int key, int degree, bool minor)
        {
            return octaveBase + key + Scale(degree, minor);
        }
        private static void Drum(ClipContentData clip, int bar, int step, int lane, int volume,
                                 Random r, double chance = 1)
        {
            if (step < 0 || step >= 16 || r.NextDouble() >= chance) return;
            long tick = bar * SongData.TicksPerBar4 + step * SongData.TicksPerSixteenth;
            foreach (var existing in clip.drums)
                if (existing.startTick == tick && existing.lane == lane) return;
            clip.drums.Add(new DrumEvent {
                startTick = tick, lane = lane,
                velocity = Clamp(volume + r.Next(-5, 6), 20, 127), legacyVelocityTier = 2
            });
        }
        private static void Hits(ClipContentData c, int bar, int lane, int[] positions, int vel, Random r, double chance = 1)
        {
            foreach (int step in positions) Drum(c, bar, step, lane, vel, r, chance);
        }
        private static void GenerateDrums(ClipContentData c, int g, int s, int bar, int bars, int variation, Random r)
        {
            int family = Mod(variation - 1, 4);
            bool turn = (bar & 1) == 1;
            bool fill = bars > 1 && bar == bars - 1;
            if (g == 0)
            {
                Hits(c, bar, 1, new[] { 4, 12 }, 111, r);
                if (s == 0) Hits(c, bar, 0, turn ? new[] { 0, 7, 10 } : new[] { 0, 6, 10 }, 111, r);
                else if (s == 1) Hits(c, bar, 0, turn ? new[] { 0, 9 } : new[] { 0, 10 }, 110, r);
                else Hits(c, bar, 0, turn ? new[] { 0, 3, 10, 15 } : new[] { 0, 7, 10, 14 }, 109, r);
                Drum(c, bar, 11, 6, 39, r, s == 1 ? .37 : .6);
                Drum(c, bar, 15, 1, 40, r, s == 2 ? .55 : .23);
                if (s == 0) Hits(c, bar, 3, new[] { 0, 4, 8, 12 }, 70, r);
                else if (s == 1) Hits(c, bar, 3, new[] { 0, 4, 8, 12 }, 60, r, .85);
                else Hits(c, bar, 3, new[] { 0, 2, 4, 6, 8, 10, 12, 14 }, 67, r, .93);
                if (family == 1) Hits(c, bar, 3, new[] { 3, 11 }, 48, r);
                if (family == 2) Hits(c, bar, 3, new[] { 7, 15 }, 51, r);
                if (family == 3) Drum(c, bar, 14, 0, 69, r, .7);
                if (family == 2) Drum(c, bar, 7, 6, 47, r, .74);
                Drum(c, bar, 14, 4, 61, r, .3);
            }
            else if (g == 1)
            {
                Hits(c, bar, 0, new[] { 0, 4, 8, 12 }, 116, r);
                Hits(c, bar, 1, new[] { 4, 12 }, 98, r, s == 1 ? .75 : 1);
                Hits(c, bar, 2, new[] { 4, 12 }, 90, r, s == 2 ? .85 : .42);
                Hits(c, bar, 4, new[] { 2, 6, 10, 14 }, 85, r, s == 1 ? .65 : .98);
                int[] hats = s == 1 ? new[] { 0, 4, 8, 12 } :
                             s == 2 ? new[] { 0, 2, 3, 4, 6, 8, 10, 11, 12, 14 } :
                                      new[] { 0, 2, 4, 6, 8, 10, 12, 14 };
                for (int i = 0; i < hats.Length; i++)
                    Drum(c, bar, hats[i], 3, hats[i] % 4 == 0 ? 81 : 59, r, s == 1 ? .79 : .94);
                if (s == 2) Hits(c, bar, 6, turn ? new[] { 3, 11, 15 } : new[] { 3, 11 }, 57, r, .8);
                if (s == 1 && family == 1) Drum(c, bar, 14, 0, 67, r, .6);
                if (s == 2 && family == 2) Drum(c, bar, 15, 6, 55, r, .6);
                if (family == 1 && s != 1) Drum(c, bar, 15, 0, 65, r, .55);
                if (family == 3) Drum(c, bar, 7, 6, 51, r, .6);
                if (s == 3) // 90s: piano/rave house needs an energetic, unambiguous pulse.
                {
                    Hits(c, bar, 2, new[] { 4, 12 }, 96, r, .95);
                    Hits(c, bar, 4, new[] { 2, 6, 10, 14 }, 87, r);
                    if (turn) Hits(c, bar, 6, new[] { 3, 11 }, 57, r, .64);
                    if (fill && family % 2 == 0) Drum(c, bar, 15, 1, 68, r, .8);
                }
            }
            else if (g == 2)
            {
                Hits(c, bar, 0, new[] { 0, 4, 8, 12 }, 121, r, s == 1 ? .95 : 1);
                if (s != 1) Hits(c, bar, 1, new[] { 4, 12 }, 98, r, .9);
                Hits(c, bar, 4, new[] { 2, 6, 10, 14 }, 92, r, s == 1 ? .55 : .95);
                int[] hats = s == 1 ? new[] { 0, 4, 8, 12 } :
                             s == 2 ? new[] { 0, 1, 3, 4, 6, 7, 8, 10, 11, 12, 14, 15 } :
                                      new[] { 0, 2, 4, 6, 8, 10, 12, 14 };
                foreach (int p in hats) Drum(c, bar, p, 3, p % 4 == 0 ? 86 : 57, r, s == 1 ? .87 : .95);
                if (s == 2) Hits(c, bar, 6, turn ? new[] { 3, 7, 11, 15 } : new[] { 3, 11 }, 64, r, .82);
                if (family == 2 && s != 1) Drum(c, bar, 15, 7, 68, r, .6);
                if (family == 1 && s == 2) Drum(c, bar, 7, 0, 80, r, .6);
                if (family == 3) Drum(c, bar, 15, 6, 62, r, .65);
            }
            else if (g == 3)
            {
                // Breaks: prominent backbeat and ghost responses; not a sampled break.
                if (s == 2)
                {
                    Hits(c, bar, 0, turn ? new[] { 0, 7, 11 } : new[] { 0, 3, 10 }, 113, r);
                    Drum(c, bar, 8, 1, 118, r);
                    Drum(c, bar, 15, 1, 45, r, .7);
                }
                else
                {
                    Hits(c, bar, 0, turn ? new[] { 0, 7, 10, 14 } : new[] { 0, 3, 10, 14 }, 108, r);
                    Hits(c, bar, 1, s == 1 ? new[] { 4, 10, 12 } : new[] { 4, 12 }, 109, r);
                    Hits(c, bar, 6, s == 1 ? new[] { 2, 7, 15 } : new[] { 7, 11 }, 42, r, .82);
                }
                int[] hats = s == 1 ? new[] { 0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 15 } :
                                      new[] { 0, 2, 4, 6, 8, 10, 12, 14 };
                foreach (int p in hats) Drum(c, bar, p, 3, p % 4 == 0 ? 85 : 61, r, .95);
                Hits(c, bar, 4, new[] { 6, 14 }, 71, r, s == 2 ? .38 : .65);
                if (family == 1) Drum(c, bar, 15, 0, 64, r, .6);
                if (family == 3 && s != 2) Drum(c, bar, 9, 1, 58, r, .55);
                if (family == 2) Drum(c, bar, 15, 6, 58, r, .7);
                // Make AMEN and JUNGLE have different ghost-snare and roll behavior.
                if (s == 0)
                {
                    Drum(c, bar, turn ? 11 : 3, 1, 48, r, .8);
                    if (turn) Drum(c, bar, 15, 6, 53, r, .65);
                }
                else if (s == 1)
                {
                    Drum(c, bar, 3, 1, 46, r, .65);
                    Drum(c, bar, 11, 1, 51, r, .72);
                    Drum(c, bar, 13, 6, 41, r, .66);
                }
                else if (fill) Drum(c, bar, 14, 6, 53, r, .62);
            }
            else if (g == 5) // FRENCH HOUSE: never route into the broken FUNK kick grid.
            {
                Hits(c, bar, 0, new[] { 0, 4, 8, 12 }, 117, r);
                Hits(c, bar, 1, new[] { 4, 12 }, s == 1 ? 103 : 93, r);
                Hits(c, bar, 2, new[] { 4, 12 }, 86, r, s == 1 ? .93 : .68);
                Hits(c, bar, 4, new[] { 2, 6, 10, 14 }, 87, r, s == 0 ? .78 : .98);
                int[] hats = s == 0 ? new[] { 0, 2, 4, 6, 8, 10, 12, 14 } :
                             s == 1 ? new[] { 0, 2, 4, 6, 8, 10, 12, 14, 15 } :
                                      new[] { 0, 2, 3, 4, 6, 8, 10, 11, 12, 14 };
                foreach (int step in hats)
                    Drum(c, bar, step, 3, step % 4 == 0 ? 80 : 60, r, s == 0 ? .83 : .94);
                if (s == 0) // FILTER: hypnotic and sparse, let the bass carry the hook.
                {
                    Drum(c, bar, 11, 6, 43, r, .42);
                    if (turn) Drum(c, bar, 15, 3, 55, r, .7);
                }
                else if (s == 1) // DISCO: bright accents and a light turnaround.
                {
                    Hits(c, bar, 6, turn ? new[] { 3, 11, 15 } : new[] { 3, 11 }, 54, r, .75);
                    if (fill) Drum(c, bar, 15, 7, 58, r, .65);
                }
                else // TOUCH: syncopated embellishments around a stable kick.
                {
                    Hits(c, bar, 6, turn ? new[] { 7, 11, 15 } : new[] { 3, 11 }, 58, r, .77);
                    if (family == 2) Drum(c, bar, 15, 0, 72, r, .55);
                }
            }
            else // FUNK: tight live-feeling backbeat with fills that leave room for bass.
            {
                Hits(c, bar, 1, new[] { 4, 12 }, 112, r);
                Hits(c, bar, 2, new[] { 4, 12 }, 82, r, s == 1 ? .55 : .95);
                int[] kicks = s == 0 ? (turn ? new[] { 0, 7, 10, 14 } : new[] { 0, 6, 10 }) :
                              s == 1 ? (turn ? new[] { 0, 7, 11 } : new[] { 0, 10, 14 }) :
                                       (turn ? new[] { 0, 3, 8, 11, 14 } : new[] { 0, 6, 8, 14 });
                Hits(c, bar, 0, kicks, 108, r);
                int[] hats = s == 1 ? new[] { 0, 2, 4, 6, 8, 10, 12, 14 } :
                             s == 2 ? new[] { 0, 2, 3, 4, 6, 8, 10, 11, 12, 14, 15 } :
                                      new[] { 0, 2, 4, 6, 8, 10, 12, 14 };
                foreach (int step in hats)
                    Drum(c, bar, step, 3, step % 4 == 0 ? 77 : 55, r, s == 1 ? .82 : .95);
                Hits(c, bar, 4, s == 0 ? new[] { 2, 6, 10, 14 } : new[] { 6, 14 }, 75, r, s == 1 ? .5 : .83);
                Drum(c, bar, 11, 6, 44, r, .58);
                if (family == 1) Drum(c, bar, 15, 6, 55, r, .7);
                if (family == 2) Drum(c, bar, 7, 1, 44, r, .52);
                if (family == 3) Drum(c, bar, 15, 0, 72, r, .55);
            }
            if (fill)
            {
                Drum(c, bar, 14, 6, 58, r, .7);
                Drum(c, bar, 15, 1, 59, r, g == 3 ? .6 : .28);
                Drum(c, bar, 15, 7, 65, r, .3);
            }
        }

        // 2.2.0: A single harmonic plan feeds three different MIDI composers. The plan is
        // independent of track kind and variation, so separately generated tracks agree.
        private static int[] MakeHarmonyPlan(int bars, int g, bool minor, int choice,
                                             int offset, int[] supplied)
        {
            int[] selected = ProgressionFor(g, minor, choice);
            int[] plan = new int[bars];
            for (int bar = 0; bar < bars; bar++)
            {
                if (choice == 0 && supplied != null && bar < supplied.Length && supplied[bar] >= 0)
                    plan[bar] = Mod(supplied[bar], 7);
                else
                    plan[bar] = selected[Mod(bar + offset, selected.Length)];
            }
            return plan;
        }

        private static int CandidateSeed(int originalSeed, int candidate, int[] harmony, int bars)
        {
            unchecked
            {
                int h = originalSeed ^ (candidate + 1) * 0x45d9f3b ^ bars * 1013;
                for (int i = 0; i < harmony.Length; i++) h = h * 16777619 ^ harmony[i];
                return h;
            }
        }

        private static List<NoteEvent> CopyNotes(List<NoteEvent> notes)
        {
            var copy = new List<NoteEvent>(notes.Count);
            foreach (var n in notes)
                copy.Add(new NoteEvent { startTick = n.startTick, durationTicks = n.durationTicks,
                    pitch = n.pitch, velocity = n.velocity });
            return copy;
        }

        private static void GenerateMidiCandidate(ClipContentData clip, TrackKind kind, int g, int s,
                                                  int bars, int key, bool minor,
                                                  int[] harmony, bool synthChords,
                                                  int[][] harmonyVoices, Random r)
        {
            int motif = r.Next(0, 16);
            int rhythm = r.Next(0, 4);
            int character = r.Next(0, 5);
            int[] previousVoicing = null;
            int previousMelodyPitch = 68 + Mod(key, 5);
            for (int bar = 0; bar < bars; bar++)
            {
                int degree = harmony[bar];
                int nextDegree = harmony[(bar + 1) % bars]; // Actual loop destination.
                if (kind == TrackKind.Bass)
                    ComposeBass(clip, g, s, bar, bars, key, minor, degree, nextDegree,
                                motif, rhythm, character, r);
                else if (kind == TrackKind.Pads || (kind == TrackKind.Synth && synthChords))
                {
                    bool pads = kind == TrackKind.Pads;
                    // Skip an odd bar only when the previous bar REALLY contains a
                    // two-bar held pad; otherwise the skipped bar becomes silent.
                    if (pads && bar > 0 && bar % 2 == 1 && harmony[bar - 1] == degree &&
                        ShouldHoldPad(g, s, character))
                        continue;
                    int[] chord = ComposeVoicing(degree, key, minor, g, s, pads, character,
                                                 previousVoicing, r);
                    ComposeChordRhythm(clip, g, s, bar, bars, degree, harmony, pads,
                                       chord, rhythm, character, r);
                    previousVoicing = chord;
                }
                else
                    ComposeMelody(clip, g, s, bar, bars, key, minor, degree, motif, rhythm,
                                  character, harmonyVoices != null && bar < harmonyVoices.Length
                                  ? harmonyVoices[bar] : null, ref previousMelodyPitch, r);
            }
            // Never force a tick-zero note into intentionally offbeat chords/stabs.
            // Clip placement is already aligned by the arrangement's tick clock.
        }

        private static void AddMidi(ClipContentData clip, int bar, int step, int stepsLong,
                                    int pitch, int velocity, Random r)
        {
            if (bar < 0 || step < 0 || step >= 16 || stepsLong < 1) return;
            long start = (long)bar * SongData.TicksPerBar4 + (long)step * SongData.TicksPerSixteenth;
            if (start >= clip.lengthTicks) return;
            pitch = Clamp(pitch, 0, 127);
            foreach (var n in clip.notes)
                if (n.pitch == pitch && n.startTick == start) return;
            long duration = Math.Min((long)stepsLong * SongData.TicksPerSixteenth,
                                     clip.lengthTicks - start);
            if (duration <= 0) return;
            clip.notes.Add(new NoteEvent { startTick = start, durationTicks = duration,
                pitch = pitch, velocity = Clamp(velocity + r.Next(-4, 5), 25, 127) });
        }

        private static int FoldPitch(int note, int target, int low, int high)
        {
            int best = Clamp(note, low, high), bestDist = int.MaxValue;
            for (int shift = -48; shift <= 48; shift += 12)
            {
                int candidate = note + shift;
                if (candidate < low || candidate > high) continue;
                int d = Math.Abs(candidate - target);
                if (d < bestDist) { best = candidate; bestDist = d; }
            }
            return best;
        }

        private static int BassRoot(int key, int degree, bool minor)
        {
            int pitch = Pitch(36, key, degree, minor);
            while (pitch >= 48) pitch -= 12;
            while (pitch < 36) pitch += 12;
            return pitch;
        }

        private static int BassApproach(int current, int target)
        {
            while (target - current > 7) target -= 12;
            while (current - target > 7) target += 12;
            return Clamp(target + (target > current ? -1 : 1), 35, 59);
        }

        // Rhythm vocabularies define genre, while seed, register, contour and phrase
        // development make *different compositions*, not four recycled templates.
        private static int[][] MelodyRhythms(int g, int s)
        {
            if (g == 0) return s == 1
                ? new[] { new[] { 0, 8, 13 }, new[] { 0, 6, 12 }, new[] { 0, 7, 11, 14 }, new[] { 0, 5, 10 } }
                : new[] { new[] { 0, 5, 10, 14 }, new[] { 0, 3, 8, 13 }, new[] { 0, 6, 11, 14 }, new[] { 0, 7, 10, 15 } };
            if (g == 1)
            {
                if (s == 1) return new[] { new[] { 0, 6, 12 }, new[] { 0, 7, 14 }, new[] { 0, 5, 11 }, new[] { 0, 8, 14 } };
                if (s == 2) return new[] { new[] { 0, 2, 6, 9, 14 }, new[] { 0, 3, 7, 10, 15 }, new[] { 0, 2, 5, 10, 13 }, new[] { 0, 3, 6, 11, 14 } };
                if (s == 3) return new[] { new[] { 0, 3, 4, 7, 10, 14 }, new[] { 0, 2, 4, 8, 11, 14 }, new[] { 0, 3, 6, 8, 12, 15 }, new[] { 0, 4, 6, 10, 12, 14 } };
                return new[] { new[] { 0, 2, 6, 10, 14 }, new[] { 0, 3, 6, 11, 14 }, new[] { 0, 2, 7, 10, 15 }, new[] { 0, 4, 6, 10, 14 } };
            }
            if (g == 2) return s == 1
                ? new[] { new[] { 0, 8 }, new[] { 0, 10 }, new[] { 0, 6, 12 }, new[] { 0, 7 } }
                : s == 2
                ? new[] { new[] { 0, 3, 7, 11, 14 }, new[] { 0, 2, 6, 9, 13 }, new[] { 0, 4, 7, 11, 15 }, new[] { 0, 3, 8, 12, 14 } }
                : new[] { new[] { 0, 4, 6, 10, 12, 14 }, new[] { 0, 3, 6, 8, 12 }, new[] { 0, 4, 8, 10, 14 }, new[] { 0, 2, 6, 10, 12, 15 } };
            if (g == 3) return s == 2
                ? new[] { new[] { 0, 8, 13 }, new[] { 0, 7, 14 }, new[] { 0, 10, 14 }, new[] { 0, 6, 12 } }
                : s == 1
                ? new[] { new[] { 0, 3, 6, 9, 12, 15 }, new[] { 0, 2, 5, 8, 11, 14 }, new[] { 0, 3, 7, 10, 13 }, new[] { 0, 2, 6, 9, 13, 15 } }
                : new[] { new[] { 0, 3, 7, 10, 14 }, new[] { 0, 2, 7, 11, 14 }, new[] { 0, 5, 8, 11, 15 }, new[] { 0, 3, 6, 10, 13 } };
            if (g == 4) return s == 1
                ? new[] { new[] { 0, 7, 10, 14 }, new[] { 0, 5, 11 }, new[] { 0, 3, 10, 15 }, new[] { 0, 7, 12 } }
                : s == 2
                ? new[] { new[] { 0, 2, 5, 7, 10, 13 }, new[] { 0, 3, 6, 9, 12, 15 }, new[] { 0, 2, 6, 10, 14 }, new[] { 0, 3, 7, 11, 15 } }
                : new[] { new[] { 0, 3, 6, 10, 14 }, new[] { 0, 2, 7, 10, 13 }, new[] { 0, 3, 8, 11, 14 }, new[] { 0, 5, 7, 10, 15 } };
            return s == 0
                ? new[] { new[] { 0, 3, 6, 10, 14 }, new[] { 0, 2, 6, 11, 14 }, new[] { 0, 3, 8, 10, 15 }, new[] { 0, 5, 7, 11, 14 } }
                : s == 1
                ? new[] { new[] { 0, 2, 6, 10, 14 }, new[] { 0, 4, 7, 10, 13 }, new[] { 0, 3, 6, 9, 14 }, new[] { 0, 2, 7, 11, 15 } }
                : new[] { new[] { 0, 3, 5, 10, 13, 15 }, new[] { 0, 2, 6, 9, 12, 14 }, new[] { 0, 3, 7, 10, 14 }, new[] { 0, 5, 8, 11, 15 } };
        }

        private static int[][] MelodicContours(int g)
        {
            if (g == 0) return new[] { new[] { 0, 2, 3, 2, 0, -1 }, new[] { 2, 1, 0, -2, 0, 1 },
                new[] { 0, -1, 2, 1, -2, 0 }, new[] { 0, 2, 4, 3, 2, 0 } };
            if (g == 1) return new[] { new[] { 0, 2, 4, 2, 1, 0 }, new[] { 4, 3, 2, 0, 2, 1 },
                new[] { 0, 1, 2, 4, 3, 2 }, new[] { 2, 4, 5, 4, 2, 0 } };
            if (g == 2) return new[] { new[] { 0, 0, 1, 0, -1, 0 }, new[] { 0, 4, 0, 1, 0, -1 },
                new[] { 0, -1, 0, 2, 1, 0 }, new[] { 0, 0, 4, 0, 2, 0 } };
            if (g == 3) return new[] { new[] { 0, -1, 2, 0, -2, 1 }, new[] { 0, 2, 1, -1, 0, -2 },
                new[] { 4, 2, 0, 1, -1, 0 }, new[] { 0, 0, 3, 2, -1, 0 } };
            if (g == 4) return new[] { new[] { 0, 2, 3, 2, -1, 0 }, new[] { 0, 0, 4, 2, 1, -1 },
                new[] { 2, 4, 2, 0, -1, 0 }, new[] { 0, 3, 2, 4, 2, 0 } };
            return new[] { new[] { 0, 2, 4, 2, 0, 2 }, new[] { 2, 1, 0, 2, 4, 3 },
                new[] { 0, 4, 2, 1, 2, 0 }, new[] { 0, 2, 3, 4, 2, 0 } };
        }

        private static int NearestHarmonyNote(int pitch, int key, int degree, bool minor,
                                              int[] externalChord, int reference, int low, int high)
        {
            int best = pitch, bestCost = int.MaxValue;
            int[] pitches = externalChord != null && externalChord.Length > 0
                ? externalChord
                : new[] { Pitch(60, key, degree, minor), Pitch(60, key, degree + 2, minor),
                          Pitch(60, key, degree + 4, minor) };
            foreach (int chordPitch in pitches)
                for (int shift = -48; shift <= 48; shift += 12)
                {
                    int candidate = chordPitch + shift;
                    if (candidate < low || candidate > high) continue;
                    int cost = Math.Abs(candidate - pitch) * 2 + Math.Abs(candidate - reference);
                    if (cost < bestCost) { bestCost = cost; best = candidate; }
                }
            return best;
        }

        private static void ComposeMelody(ClipContentData c, int g, int s, int bar, int bars,
                                          int key, bool minor, int degree, int motif, int rhythm,
                                          int character, int[] externalChord, ref int previous, Random r)
        {
            int[][] grids = MelodyRhythms(g, s);
            // A-A'-B-A'' form in four bars; call/reply on short phrases.
            int variant = Mod(rhythm + (bar % 2 == 1 ? 1 : 0) + (bar == 2 ? 2 : 0), grids.Length);
            int[] steps = grids[variant];
            int[][] contours = MelodicContours(g);
            int[] shape = contours[Mod(motif / 4 + s, contours.Length)];
            // Previous note carries across bars, avoiding arbitrary octave resets.
            for (int i = 0; i < steps.Length; i++)
            {
                int step = steps[i];
                if (bar == 3 && i == steps.Length - 1 && step == 15 && character % 2 == 0) step = 14;
                // A phrase answers rather than copying its call note-for-note.
                int idx = Mod(i + (bar % 2 == 1 ? 2 : 0) + (bar == 2 ? 1 : 0) + motif % 3, shape.Length);
                int offset = shape[idx];
                if (i == 0) offset = (motif % 2 == 0 ? 0 : 2);
                // Third bar develops the established motif rather than replaying it verbatim.
                if (bar == 2 && i > 0 && i < steps.Length - 1 && (i + character) % 3 == 0)
                    offset += (motif % 2 == 0 ? 1 : -1);
                if (i == steps.Length - 1) offset = bar == bars - 1 ? 0 :
                    (bar % 2 == 1 ? 2 : 4);
                int pitch = Pitch(60, key, degree + offset, minor);
                pitch = FoldPitch(pitch, previous, g == 2 ? 55 : 60, g == 2 ? 79 : 83);
                // Chord-tone targets are deliberate; passing notes remain free between them.
                if (i == 0 || i == steps.Length - 1 || (step % 4 == 0 && i % 2 == 0))
                    pitch = NearestHarmonyNote(pitch, key, degree, minor, externalChord,
                                               previous, 60, 83);
                else if (g == 4 && i > 0 && i < steps.Length - 1 && r.Next(6) == 0)
                    pitch -= 1; // Occasional blues/chromatic funk color on weak notes.
                else if (g == 2 && s == 2 && i > 0 && r.Next(7) == 0)
                    pitch += r.Next(2) == 0 ? 1 : -1; // Industrial tension.
                int gap = i + 1 < steps.Length ? steps[i + 1] - step : 16 - step;
                int length = g == 0 ? 2 + r.Next(3) :
                             g == 1 && s == 1 ? 3 + r.Next(4) :
                             g == 1 && s == 3 ? 1 + r.Next(3) :
                             g == 2 ? (s == 1 ? 3 : 1 + r.Next(2)) :
                             g == 3 ? (s == 1 ? 1 + r.Next(2) : 1 + r.Next(3)) :
                             g == 5 ? (s == 0 ? 2 + r.Next(2) : 1 + r.Next(3)) : 1 + r.Next(2);
                if (i == steps.Length - 1 && bar == bars - 1) length = Math.Max(length, 2);
                length = Math.Max(1, Math.Min(length, Math.Max(1, gap)));
                int vel = i == 0 ? 107 : i == steps.Length - 1 ? 97 :
                    step % 4 == 0 ? 98 : 78 + character * 3;
                AddMidi(c, bar, step, length, pitch, vel, r);
                previous = pitch;
            }
        }

        private static int[][] BassRhythms(int g, int s)
        {
            if (g == 0) return s == 1
                ? new[] { new[] { 0, 7, 10 }, new[] { 0, 6, 12 }, new[] { 0, 8, 14 }, new[] { 0, 7, 11, 15 } }
                : new[] { new[] { 0, 6, 10, 14 }, new[] { 0, 3, 7, 11, 14 }, new[] { 0, 5, 10, 13 }, new[] { 0, 6, 9, 14 } };
            if (g == 1) return s == 1
                ? new[] { new[] { 0, 6, 10, 14 }, new[] { 0, 7, 12 }, new[] { 0, 5, 10, 14 }, new[] { 0, 3, 10, 14 } }
                : s == 2
                ? new[] { new[] { 0, 2, 5, 7, 10, 13, 15 }, new[] { 0, 3, 6, 9, 11, 14 }, new[] { 0, 2, 6, 10, 12, 15 }, new[] { 0, 3, 7, 10, 13, 15 } }
                : s == 3
                ? new[] { new[] { 0, 3, 6, 10, 12, 15 }, new[] { 0, 3, 7, 10, 14 }, new[] { 0, 2, 6, 9, 12, 15 }, new[] { 0, 3, 6, 11, 14 } }
                : new[] { new[] { 0, 3, 6, 10, 14 }, new[] { 0, 2, 6, 10, 14 }, new[] { 0, 3, 7, 11, 14 }, new[] { 0, 2, 7, 10, 15 } };
            if (g == 2) return s == 1
                ? new[] { new[] { 0, 8 }, new[] { 0, 7, 12 }, new[] { 0, 10 }, new[] { 0, 6, 13 } }
                : s == 2
                ? new[] { new[] { 0, 3, 6, 8, 11, 14 }, new[] { 0, 2, 5, 8, 12, 15 }, new[] { 0, 3, 7, 10, 14 }, new[] { 0, 4, 6, 9, 13 } }
                : new[] { new[] { 0, 2, 4, 6, 8, 10, 12, 14 }, new[] { 0, 2, 4, 7, 8, 10, 12, 15 }, new[] { 0, 3, 4, 6, 8, 11, 12, 14 }, new[] { 0, 2, 4, 6, 9, 10, 12, 14 } };
            if (g == 3) return s == 2
                ? new[] { new[] { 0, 7, 10, 14 }, new[] { 0, 6, 11 }, new[] { 0, 8, 13 }, new[] { 0, 5, 10, 15 } }
                : s == 1
                ? new[] { new[] { 0, 3, 6, 9, 11, 14 }, new[] { 0, 2, 5, 8, 12, 15 }, new[] { 0, 3, 7, 9, 12, 14 }, new[] { 0, 2, 6, 9, 13, 15 } }
                : new[] { new[] { 0, 3, 7, 10, 14 }, new[] { 0, 2, 6, 10, 13 }, new[] { 0, 5, 7, 11, 14 }, new[] { 0, 3, 8, 10, 15 } };
            if (g == 4) return s == 1
                ? new[] { new[] { 0, 3, 7, 10, 14 }, new[] { 0, 6, 10, 15 }, new[] { 0, 3, 8, 11 }, new[] { 0, 5, 10, 13 } }
                : s == 2
                ? new[] { new[] { 0, 2, 5, 7, 10, 13, 15 }, new[] { 0, 3, 6, 9, 12, 15 }, new[] { 0, 2, 6, 10, 13 }, new[] { 0, 3, 7, 10, 12, 14 } }
                : new[] { new[] { 0, 3, 6, 10, 12, 14 }, new[] { 0, 2, 6, 9, 12, 15 }, new[] { 0, 3, 7, 10, 14 }, new[] { 0, 5, 7, 10, 13, 15 } };
            return s == 1
                ? new[] { new[] { 0, 2, 6, 10, 12, 14 }, new[] { 0, 3, 6, 9, 12, 15 }, new[] { 0, 2, 7, 10, 13 }, new[] { 0, 3, 7, 10, 14 } }
                : s == 2
                ? new[] { new[] { 0, 3, 5, 7, 10, 13, 15 }, new[] { 0, 2, 6, 9, 12, 15 }, new[] { 0, 3, 7, 11, 14 }, new[] { 0, 2, 5, 10, 12, 15 } }
                : new[] { new[] { 0, 3, 6, 8, 11, 14 }, new[] { 0, 2, 6, 10, 13 }, new[] { 0, 3, 7, 10, 14 }, new[] { 0, 5, 8, 11, 15 } };
        }

        private static void ComposeBass(ClipContentData c, int g, int s, int bar, int bars,
                                        int key, bool minor, int degree, int nextDegree,
                                        int motif, int rhythm, int character, Random r)
        {
            int root = BassRoot(key, degree, minor);
            int nextRoot = BassRoot(key, nextDegree, minor);
            int fifth = Scale(degree + 4, minor) - Scale(degree, minor);
            int third = Scale(degree + 2, minor) - Scale(degree, minor);
            int seventh = Scale(degree + 6, minor) - Scale(degree, minor);
            int[][] grids = BassRhythms(g, s);
            int[] steps = grids[Mod(rhythm + (bar == 2 ? 1 : 0) + (bar == 3 ? 2 : 0), grids.Length)];
            for (int i = 0; i < steps.Length; i++)
            {
                int step = steps[i];
                int role = Mod(i + motif + (bar % 2 == 1 ? 2 : 0), 7);
                int pitch = root;
                if (i > 0)
                {
                    if (g == 2)
                        pitch = (role == 0 || role == 4) ? root + 12 :
                                (s == 2 && role == 3) ? root + fifth : root;
                    else if (g == 4)
                        pitch = role == 1 || role == 5 ? root + 12 :
                                role == 3 ? root + fifth :
                                role == 6 ? root + third : root;
                    else if (g == 0)
                        pitch = role == 2 ? root + 12 : role == 4 ? root + fifth :
                                role == 6 && s == 2 ? root + third : root;
                    else if (g == 3)
                        pitch = role == 1 || role == 5 ? root + 12 :
                                role == 3 ? root + fifth : root;
                    else if (g == 5) // Disco octaves and concise French-touch hooks.
                        pitch = role == 1 || role == 4 ? root + 12 :
                                (role == 3 && s != 0) ? root + fifth : root;
                    else
                        pitch = role == 1 || role == 4 ? root + 12 :
                                role == 2 || role == 5 ? root + fifth :
                                (role == 6 && s == 2) ? root + third : root;
                    if (g == 4 && role == 6 && (bar % 2 == 1)) pitch = root + seventh - 12;
                }
                // One deliberately placed pickup, not a random chromatic run.
                if (i == steps.Length - 1 && step >= 12 && (bar == bars - 1 || bar % 2 == 1) &&
                    (motif + character + bar) % 3 != 0 && nextDegree != degree)
                    pitch = BassApproach(root, nextRoot);
                pitch = FoldPitch(pitch, pitch >= 48 ? 51 : 42, 35, 60);
                int gap = i + 1 < steps.Length ? steps[i + 1] - step : 16 - step;
                int length = g == 2 && s == 1 ? 5 :
                             g == 0 && s == 1 ? 4 :
                             g == 1 && s == 1 ? 3 :
                             g == 3 && s == 2 ? 4 :
                             g == 4 ? 1 + ((i + character) % 3) :
                             g == 5 ? (s == 0 ? 2 : 1 + ((i + character) % 2)) : 1 + (step % 3);
                length = Math.Max(1, Math.Min(length, Math.Max(1, gap)));
                int vel = i == 0 ? 112 : step % 4 == 0 ? 101 :
                          step % 2 == 1 ? 87 : 95;
                AddMidi(c, bar, step, length, pitch, vel, r);
            }
        }

        // Whole-voicing search: avoid crossing, congestion and large jumps between
        // chord changes. Select from valid inversions, not independent voice indices.
        private static int[] ComposeVoicing(int degree, int key, bool minor, int g, int s,
                                            bool pads, int character, int[] previous, Random r)
        {
            int[] offsets;
            if (g == 2)
                offsets = s == 1 ? new[] { 0, 4, 8 } :
                    character % 2 == 0 ? new[] { 0, 2, 4 } : new[] { 0, 4, 6 };
            else if (g == 0)
                offsets = character % 3 == 0 ? new[] { 2, 6, 8 } : new[] { 0, 2, 6, 8 };
            else if (g == 4)
                offsets = character % 2 == 0 ? new[] { 2, 6, 8 } : new[] { 0, 2, 6, 8 };
            else if (g == 1 && s == 3)
                offsets = character % 2 == 0 ? new[] { 0, 2, 4, 6 } : new[] { 2, 4, 6, 8 };
            else if (g == 3)
                offsets = character % 2 == 0 ? new[] { 0, 2, 4, 6 } : new[] { 2, 4, 6, 8 };
            else
                offsets = character % 3 == 0 ? new[] { 2, 4, 6, 8 } : new[] { 0, 2, 6, 8 };
            int root = Pitch(48, key, degree, minor);
            int[][] choices = new int[offsets.Length][];
            int low = pads ? 51 : 54, high = pads ? 81 : 83;
            for (int i = 0; i < offsets.Length; i++)
            {
                var values = new List<int>(4);
                int fundamental = root + Scale(degree + offsets[i], minor) - Scale(degree, minor);
                for (int octave = -4; octave <= 3; octave++)
                {
                    int v = fundamental + octave * 12;
                    if (v >= low && v <= high) values.Add(v);
                }
                choices[i] = values.ToArray();
            }
            int[] current = new int[offsets.Length];
            int[] winner = null;
            double bestCost = double.MaxValue;
            SearchVoicings(choices, 0, current, previous, pads, character, ref winner, ref bestCost);
            if (winner != null) return winner;
            // Defensive fallback if the available voicing range ever changes.
            int[] fallback = new int[offsets.Length];
            for (int i = 0; i < offsets.Length; i++)
                fallback[i] = FoldPitch(root + Scale(degree + offsets[i], minor) - Scale(degree, minor),
                                        60 + 4 * i, low, high);
            Array.Sort(fallback);
            return fallback;
        }

        private static void SearchVoicings(int[][] choices, int depth, int[] current,
                                           int[] previous, bool pads, int character,
                                           ref int[] winner, ref double bestCost)
        {
            if (depth < choices.Length)
            {
                foreach (int pitch in choices[depth])
                {
                    current[depth] = pitch;
                    SearchVoicings(choices, depth + 1, current, previous, pads, character,
                                   ref winner, ref bestCost);
                }
                return;
            }
            int[] sorted = (int[])current.Clone();
            Array.Sort(sorted);
            int spread = sorted[sorted.Length - 1] - sorted[0];
            double cost = Math.Abs(spread - (pads ? 20 : 17)) * 1.5;
            for (int i = 0; i < sorted.Length; i++)
            {
                if (i > 0 && sorted[i] - sorted[i - 1] < 3) return;
                int aim = pads ? 55 + i * 5 : 60 + i * 5;
                cost += Math.Abs(sorted[i] - aim) * .42;
                if (previous != null)
                    cost += Math.Abs(sorted[i] - previous[Math.Min(i, previous.Length - 1)]) * 2.5;
                if (pads && sorted[i] < 55) cost += 9;
            }
            // An initial voicing may be different for each generation without
            // deliberately sacrificing smoothness on later chord changes.
            if (previous == null) cost += Math.Abs(sorted[0] - (pads ? 54 + character : 58 + character));
            if (cost < bestCost) { bestCost = cost; winner = sorted; }
        }

        // Only deliberately sustained pad styles may skip the next bar.
        private static bool ShouldHoldPad(int g, int s, int character)
        {
            return character % 2 == 0 &&
                   ((g == 0 && s == 1) || (g == 1 && s == 1) ||
                    (g == 2 && s == 1) || (g == 3 && s == 2) ||
                    (g == 5 && s == 0));
        }

        private static int[] ChordRhythms(int g, int s, bool pads, int choice, int bar)
        {
            if (pads)
            {
                // Pads must behave as beds, textures or occasional replies, not
                // as indiscriminate copies of SYNTH chord-stab patterns.
                if (g == 0) return s == 1 ? new[] { 0 } :
                    s == 2 && choice % 2 == 0 ? new[] { 0, 10 } : new[] { 0 };
                if (g == 1) return s == 1 ? new[] { 0 } :
                    s == 3 ? (bar % 2 == 0 ? new[] { 0, 8 } : new[] { 0 }) :
                    choice % 3 == 0 ? new[] { 0, 10 } : new[] { 0 };
                if (g == 2) return s == 2 && choice % 2 == 0 ? new[] { 0, 8 } : new[] { 0 };
                if (g == 3) return s == 1 && choice % 2 == 0 ? new[] { 0, 10 } : new[] { 0 };
                if (g == 4) return s == 2 && choice % 2 == 0 ? new[] { 0, 8 } :
                    s == 0 && choice % 3 == 0 ? new[] { 0, 10 } : new[] { 0 };
                return s == 0 ? new[] { 0 } :
                    s == 1 && choice % 2 == 0 ? new[] { 0, 8 } :
                    s == 2 && choice % 2 == 1 ? new[] { 0, 10 } : new[] { 0 };
            }
            if (g == 0) return s == 2 ?
                (choice % 2 == 0 ? new[] { 0, 3, 7, 11, 14 } : new[] { 0, 5, 9, 13 }) :
                (choice % 2 == 0 ? new[] { 0, 9 } : new[] { 0, 6, 12 });
            if (g == 1)
            {
                if (s == 1) return choice % 2 == 0 ? new[] { 0, 12 } : new[] { 0, 10 };
                if (s == 3) return bar % 2 == 0 ? new[] { 0, 6, 10, 14 } : new[] { 0, 4, 7, 11, 14 };
                return choice % 2 == 0 ? new[] { 2, 6, 10, 14 } : new[] { 2, 7, 10, 14 };
            }
            if (g == 2) return s == 1 ? new[] { 0, 12 } :
                choice % 2 == 0 ? new[] { 2, 6, 10, 14 } : new[] { 0, 7, 11, 14 };
            if (g == 3) return s == 2 ? new[] { 0, 8 } :
                choice % 2 == 0 ? new[] { 0, 7, 11 } : new[] { 0, 3, 9, 14 };
            if (g == 4) return s == 1 ? new[] { 0, 7, 11 } :
                choice % 2 == 0 ? new[] { 0, 6, 10, 14 } : new[] { 0, 3, 7, 11, 14 };
            if (s == 0) return choice % 2 == 0 ? new[] { 2, 6, 10, 14 } : new[] { 2, 10, 14 };
            if (s == 1) return choice % 2 == 0 ? new[] { 2, 6, 10, 14 } : new[] { 0, 6, 11, 14 };
            return choice % 2 == 0 ? new[] { 0, 3, 7, 10, 14 } : new[] { 2, 6, 10, 13 };
        }

        private static void ComposeChordRhythm(ClipContentData c, int g, int s, int bar,
                                                int bars, int degree, int[] harmony, bool pads,
                                                int[] notes, int rhythm, int character, Random r)
        {
            int[] hits = ChordRhythms(g, s, pads, rhythm + character, bar);
            bool longHold = pads && ShouldHoldPad(g, s, character) &&
                            bar + 1 < bars && harmony[bar + 1] == degree && bar % 2 == 0;
            for (int hit = 0; hit < hits.Length; hit++)
            {
                int step = hits[hit];
                if (longHold && hit > 0) break;
                int duration;
                if (pads) duration = longHold ? 32 :
                    hits.Length == 1 ? 16 - step :
                    (hit + 1 < hits.Length ? hits[hit + 1] - step - 1 : 16 - step);
                else duration = g == 1 && s == 1 ? 6 :
                    g == 0 && s == 1 ? 5 : g == 3 && s == 2 ? 6 :
                    g == 2 && s == 1 ? 7 : 1 + r.Next(2);
                duration = Math.Max(1, duration);
                int vel = pads ? 66 : g == 1 && s == 3 ? 103 : g == 4 ? 94 : 84;
                for (int i = 0; i < notes.Length; i++)
                    AddMidi(c, bar, step, duration, notes[i], vel - i * 3 - hit % 2 * 4, r);
            }
            // Movement in the top voice makes sustained pads less static; always
            // remain within the current chord and avoid re-articulating the pad root.
            if (pads && !longHold && hits.Length == 1 && character % 3 == 0 && g != 2 &&
                g != 5 && (s == 2 || g == 4))
            {
                int top = notes[notes.Length - 1];
                AddMidi(c, bar, 11, 4, top, 52, r);
            }
        }

        // Cheap deterministic ranking, with density, harmonic, rhythmic and register
        // checks. Genre-specific preferences prevent over-penalizing minimal techno.
        private static double ScoreCandidate(ClipContentData c, TrackKind kind, int g, int s,
                                              bool minor, int key, int[] harmony, bool chords,
                                              int[][] externalVoices)
        {
            if (c.notes.Count == 0) return double.MinValue;
            bool harmonic = kind == TrackKind.Pads || chords;
            long sixteenth = SongData.TicksPerSixteenth;
            var sorted = new List<NoteEvent>(c.notes);
            sorted.Sort((a, b) => a.startTick == b.startTick ? a.pitch.CompareTo(b.pitch) : a.startTick.CompareTo(b.startTick));
            var starts = new HashSet<long>();
            var pitches = new HashSet<int>();
            int[] barNotes = new int[harmony.Length];
            double score = 100;
            int repeatCount = 0, jumps = 0;
            int last = -1;
            foreach (var n in sorted)
            {
                if (n.startTick < 0 || n.startTick >= c.lengthTicks || n.durationTicks < 1 ||
                    n.durationTicks > c.lengthTicks - n.startTick) return double.MinValue;
                int bar = Clamp((int)(n.startTick / SongData.TicksPerBar4), 0, harmony.Length - 1);
                int step = (int)((n.startTick / sixteenth) % 16);
                barNotes[bar]++;
                // A held pad is active in every bar it spans; it is not a silent bar.
                if (kind == TrackKind.Pads)
                {
                    int lastActive = Clamp((int)((n.startTick + n.durationTicks - 1) /
                        SongData.TicksPerBar4), bar, harmony.Length - 1);
                    for (int active = bar + 1; active <= lastActive; active++) barNotes[active]++;
                }
                starts.Add(n.startTick);
                pitches.Add(n.pitch);
                int[] chord = externalVoices != null && bar < externalVoices.Length &&
                              externalVoices[bar] != null && externalVoices[bar].Length > 0 && !harmonic
                    ? externalVoices[bar] : null;
                bool chordTone = IsChordTone(n.pitch, key, harmony[bar], minor, chord);
                if ((step % 4 == 0 || harmonic || kind == TrackKind.Bass) && chordTone) score += .72;
                else if (step % 4 == 0 && !chordTone) score -= 2.5;
                if (kind == TrackKind.Bass && (n.pitch < 35 || n.pitch > 60)) score -= 10;
                if (kind == TrackKind.Synth && !chords && (n.pitch < 55 || n.pitch > 83)) score -= 8;
                if (!harmonic)
                {
                    if (last == n.pitch) repeatCount++;
                    if (last >= 0 && Math.Abs(n.pitch - last) > (kind == TrackKind.Bass ? 14 : 12)) jumps++;
                    last = n.pitch;
                }
            }
            foreach (int count in barNotes) if (count == 0) score -= 18;
            int target = harmonic ? (kind == TrackKind.Pads ? 1 : g == 2 ? 4 : 5) :
                         kind == TrackKind.Bass ? (g == 2 && s == 1 ? 2 : g == 1 && s == 2 ? 7 : 5) :
                         (g == 2 && s == 1 ? 2 : g == 1 && s == 3 ? 6 : 4);
            double eventsPerBar = starts.Count / (double)harmony.Length;
            score -= Math.Abs(eventsPerBar - target) * (kind == TrackKind.Pads ? 1.5 : 3.5);
            if (!harmonic)
            {
                double allowed = g == 2 ? .7 : .43;
                score -= Math.Max(0, repeatCount - sorted.Count * allowed) * 4;
                score -= jumps * (g == 3 ? 1.5 : 3);
                if (pitches.Count <= 2 && g != 2 && sorted.Count > 5) score -= 9;
            }
            // Real pitch movement between the chord tones and steady rhythmic
            // spacing both matter, but a groove may still be intentionally repetitive.
            if (starts.Count >= 3)
            {
                var gaps = new HashSet<long>();
                long prior = -1;
                foreach (var note in sorted)
                {
                    if (prior != note.startTick)
                    {
                        if (prior >= 0) gaps.Add(note.startTick - prior);
                        prior = note.startTick;
                    }
                }
                if (g != 2 && gaps.Count >= 2) score += 3;
            }
            // Style-specific syncopation matters more than insisting every
            // harmonic phrase starts on the downbeat.
            score += ScoreRhythmicFit(starts, kind, g, s, chords, sixteenth);
            if (kind == TrackKind.Bass && sorted[0].startTick == 0) score += 1;
            return score;
        }

        // Evaluate distinct onsets rather than counting every voice of a chord.
        // All numbers are relative preferences; none makes an offbeat illegal.
        private static double ScoreRhythmicFit(HashSet<long> starts, TrackKind kind,
                                               int g, int s, bool chords, long sixteenth)
        {
            if (starts.Count == 0) return 0;
            double sum = 0;
            foreach (long onset in starts)
            {
                int step = (int)((onset / sixteenth) % 16);
                bool onBeat = step % 4 == 0;
                bool offbeat = step % 4 == 2;
                bool sync = step % 2 == 1;
                if (kind == TrackKind.Pads)
                {
                    // Sustained atmospheres should usually enter with the chord change.
                    sum += onBeat ? 1.8 : offbeat ? .4 : -.5;
                    continue;
                }
                if (kind == TrackKind.Bass)
                {
                    if (g == 0) sum += sync ? 1.7 : onBeat ? 1.2 : .5; // boom bap pocket
                    else if (g == 1) sum += s == 1 ? (onBeat ? 1.9 : .7) :
                                               (offbeat ? 2.0 : onBeat ? 1.2 : .8);
                    else if (g == 2) sum += onBeat ? 2.0 : s == 2 && sync ? 1.2 : .3;
                    else if (g == 3) sum += sync ? 1.7 : onBeat ? 1.0 : .4;
                    else if (g == 4) sum += sync ? 2.0 : offbeat ? 1.2 : .7;
                    else sum += offbeat ? 2.1 : sync ? 1.4 : onBeat ? 1.0 : .4;
                }
                else if (chords)
                {
                    if (g == 1 && s == 3) sum += offbeat ? 2.2 : onBeat ? 1.5 : 1.0;
                    else if (g == 1 || g == 5) sum += offbeat ? 2.0 : onBeat ? .8 : 1.0;
                    else if (g == 0 || g == 4) sum += sync ? 1.9 : onBeat ? 1.1 : .6;
                    else sum += onBeat ? 1.7 : offbeat ? 1.0 : .5;
                }
                else // melodic SYNTH
                {
                    if (g == 2) sum += onBeat ? 1.7 : s == 2 && sync ? 1.1 : .4;
                    else if (g == 0 || g == 3) sum += sync ? 1.5 : onBeat ? 1.1 : .5;
                    else if (g == 4) sum += sync ? 1.7 : offbeat ? 1.2 : .5;
                    else sum += offbeat ? 1.5 : onBeat ? 1.1 : .8;
                }
            }
            // Bound the benefit so dense phrases don't win only by having more hits.
            return Math.Min(8.0, sum / Math.Max(1, starts.Count) * 4.0);
        }

        private static bool IsChordTone(int pitch, int key, int degree, bool minor, int[] external)
        {
            if (external != null)
            {
                foreach (int n in external) if (Mod(n, 12) == Mod(pitch, 12)) return true;
                return false;
            }
            int root = key + Scale(degree, minor);
            return Mod(pitch - root, 12) == 0 ||
                   Mod(pitch - root, 12) == Mod(Scale(degree + 2, minor) - Scale(degree, minor), 12) ||
                   Mod(pitch - root, 12) == Mod(Scale(degree + 4, minor) - Scale(degree, minor), 12) ||
                   Mod(pitch - root, 12) == Mod(Scale(degree + 6, minor) - Scale(degree, minor), 12) ||
                   Mod(pitch - root, 12) == Mod(Scale(degree + 8, minor) - Scale(degree, minor), 12);
        }

        /// <summary>
        /// Musical humanization, not arbitrary quantization noise. Dance kicks hold the
        /// grid; hats and ghost snares establish the pocket. MIDI chords remain aligned
        /// as a block, with shorter offbeat stabs and breathing room in bass phrases.
        /// Notes stay inside their clips and velocity/timing remain deterministic.
        /// </summary>
        private static void HumanizePhrase(ClipContentData clip, TrackKind kind, int genre,
                                           int style, int variation, int seed)
        {
            var r = new Random(unchecked(seed ^ 0x5A2C31));
            long sixteenth = SongData.TicksPerSixteenth;
            long clipEnd = clip.lengthTicks;
            bool relaxed = genre == 0 || genre == 3 || genre == 4;
            int strength = Mod(variation - 1, 4);
            if (kind == TrackKind.Drums)
            {
                foreach (var hit in clip.drums)
                {
                    long original = hit.startTick;
                    int step = (int)((original / sixteenth) % 16);
                    bool anchored = hit.lane == 0 && (genre == 1 || genre == 2 || genre == 5) && step % 4 == 0;
                    int late = 0;
                    if (!anchored)
                    {
                        if (hit.lane == 3 || hit.lane == 4) // hats: relaxed offbeats / swing
                            late = (step % 2 == 1 ? (relaxed ? 15 : 10) : 0) + r.Next(-3, 4);
                        else if (hit.lane == 1 || hit.lane == 2) // backbeat weight
                            late = relaxed ? 9 + r.Next(-3, 5) : 2 + r.Next(-2, 4);
                        else if (hit.lane == 6 || hit.lane == 7) // ghost percussion
                            late = 7 + r.Next(-6, 7);
                        else late = r.Next(-3, 5);
                        if (strength == 2 && step % 2 == 1) late += 5;
                    }
                    hit.startTick = ClampTick(original + late, clipEnd);
                    // Preserve weight on backbeats and strong kicks, soften busy hats.
                    int accent = step % 4 == 0 ? 3 : step % 2 == 1 ? -4 : 0;
                    if (hit.lane == 3 || hit.lane == 4) accent += (step / 2) % 2 == 0 ? 2 : -5;
                    if (hit.lane == 6) accent -= 4;
                    hit.velocity = Clamp(hit.velocity + accent + r.Next(-3, 4), 20, 127);
                }
                return;
            }
            if (clip.notes == null || clip.notes.Count == 0) return;
            // A chord event is a group of pitches with exactly the same start time.
            // Make the group breathe together, never arpeggiate accidentally.
            var offsets = new Dictionary<long, int>();
            var chordReleases = new Dictionary<long, int>();
            foreach (var note in clip.notes)
            {
                long original = note.startTick;
                int step = (int)((original / sixteenth) % 16);
                int offset;
                if (!offsets.TryGetValue(original, out offset))
                {
                    bool pickup = step % 2 == 1;
                    if (kind == TrackKind.Pads) offset = r.Next(-2, 4);
                    else if (kind == TrackKind.Bass)
                        offset = (relaxed ? 6 : 1) + (pickup ? (relaxed ? 7 : 4) : 0) + r.Next(-3, 4);
                    else
                        offset = (relaxed ? 5 : 2) + (pickup ? 7 : 0) + r.Next(-3, 4);
                    if (strength == 2 && pickup) offset += 4;
                    // Do not add an empty lead-in to a clip that begins on step zero.
                    if (original == 0) offset = 0;
                    offsets.Add(original, offset);
                }
                long start = ClampTick(original + offset, clipEnd);
                long maxDuration = Math.Max(1L, clipEnd - start);
                long duration = note.durationTicks;
                if (kind == TrackKind.Pads)
                {
                    int release;
                    if (!chordReleases.TryGetValue(original, out release))
                    {
                        release = r.Next(-3, 4);
                        chordReleases.Add(original, release);
                    }
                    // Keep intentionally held two-bar pads at their exact boundary;
                    // random release jitter would otherwise reintroduce a small gap.
                    if (duration < SongData.TicksPerBar4 * 2L) duration += release;
                }
                else
                {
                    // A small articulation difference matters more than a random
                    // pitch change, especially for piano stabs and synth hooks.
                    bool shortNote = duration <= sixteenth * 2;
                    int release;
                    if (kind == TrackKind.Synth)
                    {
                        // Identical chord onsets keep their release aligned.
                        if (!chordReleases.TryGetValue(original, out release))
                        {
                            release = shortNote ? r.Next(-9, 4) : r.Next(-12, 10);
                            chordReleases.Add(original, release);
                        }
                    }
                    else release = shortNote ? r.Next(-9, 4) : r.Next(-12, 10);
                    duration += release;
                    if (kind == TrackKind.Bass && step % 4 != 0) duration -= 5;
                }
                note.startTick = start;
                note.durationTicks = Math.Max(1L, Math.Min(maxDuration, duration));
                int accent = step % 4 == 0 ? 4 : step % 2 == 1 ? -2 : 0;
                if (kind == TrackKind.Pads) accent /= 2;
                note.velocity = Clamp(note.velocity + accent + r.Next(-3, 4), 25, 127);
            }
            // Never create same-pitch overlaps while re-articulating repeated notes.
            var ordered = new List<NoteEvent>(clip.notes);
            ordered.Sort((a, b) => a.startTick.CompareTo(b.startTick));
            var lastByPitch = new Dictionary<int, NoteEvent>();
            foreach (var note in ordered)
            {
                NoteEvent previous;
                if (lastByPitch.TryGetValue(note.pitch, out previous))
                    previous.durationTicks = Math.Min(previous.durationTicks,
                        Math.Max(1L, note.startTick - previous.startTick - 1));
                lastByPitch[note.pitch] = note;
            }
        }

        private static long ClampTick(long tick, long clipEnd)
        {
            return Math.Max(0L, Math.Min(clipEnd - 1L, tick));
        }

        private static int Clamp(int value, int minimum, int maximum) => Math.Max(minimum, Math.Min(maximum, value));
        private static int Mod(int value, int count) => ((value % count) + count) % count;
    }
}
