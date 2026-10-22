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
                                int[] harmonyDegrees = null, int[][] harmonyVoices = null)
        {
            if (clip == null) throw new ArgumentNullException(nameof(clip));
            if (kind == TrackKind.Chops) return;
            genre = Clamp(genre, 0, Genres.Length - 1);
            style = Clamp(style, 0, Styles[genre].Length - 1);
            bars = Clamp(bars, 1, 4);
            key = Mod(key, 12);
            clip.kind = kind == TrackKind.Drums ? ClipKind.Drums : ClipKind.Midi;
            clip.lengthTicks = bars * SongData.TicksPerBar4;
            clip.generatedTag = Label(genre, style, key, minor, synthChords && kind == TrackKind.Synth) + " / V" + variation;
            if (clip.drums == null) clip.drums = new List<DrumEvent>();
            if (clip.notes == null) clip.notes = new List<NoteEvent>();
            clip.drums.Clear();
            clip.notes.Clear();
            if (clip.chops != null) clip.chops.Clear();
            int seed = unchecked(variation * 104729 + genre * 3659 + style * 1619 + (int)kind * 307 + key * 47 + (minor ? 173 : 97));
            var rng = new Random(seed);
            int[][] chordVoices = new int[bars][];
            for (int bar = 0; bar < bars; bar++)
            {
                if (kind == TrackKind.Drums) { GenerateDrums(clip, genre, style, bar, bars, variation, rng); continue; }
                int degree = harmonyDegrees != null && bar < harmonyDegrees.Length && harmonyDegrees[bar] >= 0
                    ? harmonyDegrees[bar] : Progression(genre, minor)[bar % 4];
                if (kind == TrackKind.Bass) GenerateBass(clip, genre, style, bar, bars, degree, key, minor, variation, rng,
                    harmonyDegrees != null && (bar + 1) % bars < harmonyDegrees.Length ? harmonyDegrees[(bar + 1) % bars] : -1);
                else if (kind == TrackKind.Pads || (kind == TrackKind.Synth && synthChords))
                {
                    // Use actual semitones of the current chord, not the tonic triad transposed
                    // mechanically. Nearby inversion choices keep successive bars connected.
                    int[] previous = bar > 0 ? chordVoices[bar - 1] : null;
                    bool pads = kind == TrackKind.Pads;
                    chordVoices[bar] = VoiceChord(degree, key, minor, genre, style, pads, previous);
                    GenerateHarmony(clip, genre, style, bar, pads, chordVoices[bar], variation, rng);
                }
                else GenerateMelody(clip, genre, style, bar, degree, key, minor, variation, rng,
                    harmonyVoices != null && bar < harmonyVoices.Length ? harmonyVoices[bar] : null);
            }
            // Some styles begin with an intentional offbeat. Generated clip content,
            // however, must start at local tick zero: otherwise the piano roll and
            // the arrangement can show an unwanted empty lead-in. Shift the whole
            // phrase, preserving every note's relative timing and duration.
            AlignToStart(clip);
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

        // Scale degrees allow major and natural-minor progressions to share a tonic.
        private static int[] Progression(int g, bool minor)
        {
            if (g == 0) return minor ? new[] { 0, 5, 3, 4 } : new[] { 0, 5, 3, 4 };
            if (g == 1) return minor ? new[] { 0, 3, 5, 4 } : new[] { 0, 3, 5, 4 };
            if (g == 2) return new[] { 0, 0, 0, 4 };
            if (g == 3) return minor ? new[] { 0, 5, 3, 4 } : new[] { 0, 5, 1, 4 };
            // Funk stays close to the tonic; the last bar turns toward the dominant.
            if (g == 5) return minor ? new[] { 0, 5, 3, 4 } : new[] { 0, 3, 4, 3 };
            return minor ? new[] { 0, 0, 3, 4 } : new[] { 0, 0, 3, 4 };
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

        private static void Note(ClipContentData c, int bar, int step, int duration,
                                 int pitch, int velocity, Random r)
        {
            if (step < 0 || step > 15 || duration < 1) return;
            long tick = bar * SongData.TicksPerBar4 + step * SongData.TicksPerSixteenth;
            // Prevent doubled same-pitch MIDI notes when a turnaround and the
            // underlying style both schedule the same sixteenth.
            foreach (var existing in c.notes)
                if (existing.startTick == tick && existing.pitch == pitch) return;
            c.notes.Add(new NoteEvent {
                startTick = tick,
                durationTicks = Math.Min(16 - step, duration) * SongData.TicksPerSixteenth,
                pitch = Clamp(pitch, 0, 127), velocity = Clamp(velocity + r.Next(-5, 6), 25, 127)
            });
        }
        // Expressive, playable funk/disco-inspired bass. Style templates keep the
        // original genres recognisable; variation changes accents, fills and octave
        // choices rather than scattering unmusical random notes across the scale.
        private static int ApproachBass(int nextRoot, int currentRoot)
        {
            // Leading chromatic pickup, kept in the normal bass register.
            int target = nextRoot;
            while (target - currentRoot > 7) target -= 12;
            while (currentRoot - target > 7) target += 12;
            int approach = target - 1;
            return approach < 35 ? approach + 12 : approach;
        }

        private static int BassRoot(int key, int degree, bool minor)
        {
            int pitch = Pitch(36, key, degree, minor);
            while (pitch > 59) pitch -= 12;
            while (pitch < 36) pitch += 12;
            // Prefer nearby low-mid inversions of progression notes rather than
            // dropping back to the sub octave at each chord change.
            if (pitch >= 48) pitch -= 12;
            return pitch;
        }

        private static void GenerateBass(ClipContentData c, int g, int s, int bar, int bars,
                                         int degree, int key, bool minor, int variation, Random r, int nextHarmonyDegree = -1)
        {
            // MIDI note 36 is C2 (60 is C4). Keep the main bass around
            // C2-B2; A2 is MIDI 45 / 110 Hz. Lower octaves remain available
            // when the user edits a clip for dedicated sub-bass parts.
            int root = BassRoot(key, degree, minor);
            int fifth = Scale(degree + 4, minor) - Scale(degree, minor);
            int seventh = Scale(degree + 6, minor) - Scale(degree, minor);
            int family = Mod(variation - 1, 4);
            bool answer = (bar & 1) != 0;
            // Approach the chord which actually follows in THIS clip, including
            // the return to its beginning when 1/2/4-bar clips loop.
            int nextDegree = nextHarmonyDegree >= 0 ? nextHarmonyDegree : Progression(g, minor)[(bar + 1) % bars];
            int nextRoot = BassRoot(key, nextDegree, minor);
            if (g == 0) // BOOM BAP: warm, behind-the-beat syncopation and small pickups.
            {
                int[] steps = s == 1 ? new[] { 0, 7, 10 } :
                              s == 2 ? new[] { 0, 3, 6, 10, 14 } : new[] { 0, 6, 10, 14 };
                for (int i = 0; i < steps.Length; i++)
                {
                    int step = steps[i];
                    int pitch = step == 14 ? (family == 2 ? ApproachBass(nextRoot, root) : root + fifth) :
                                step == 10 && family == 1 ? root + 12 :
                                step == 6 && family == 3 ? root + fifth : root;
                    Note(c, bar, step, step == 0 ? 4 : s == 1 ? 3 : 2,
                         pitch, step == 0 ? 112 : i % 2 == 0 ? 94 : 82, r);
                }
                if (s == 2 && family == 3) Note(c, bar, 15, 1, ApproachBass(nextRoot, root), 66, r);
            }
            else if (g == 1) // HOUSE: tight disco-funk bounce between the kicks.
            {
                int[] steps = s == 1 ? new[] { 0, 3, 7, 10, 14 } :
                              s == 2 ? new[] { 0, 2, 5, 7, 10, 13, 15 } :
                                       new[] { 0, 3, 6, 10, 14 };
                for (int i = 0; i < steps.Length; i++)
                {
                    int step = steps[i];
                    int pitch = step == 15 ? ApproachBass(nextRoot, root) :
                                step == 7 && family != 0 ? root + 12 :
                                step == 10 && (family == 1 || s == 2) ? root + fifth :
                                step == 14 && family == 3 ? root + 12 : root;
                    int length = step == 0 && s == 1 ? 3 : step == 10 && s == 1 ? 3 : 1 + (step % 4 == 2 ? 1 : 0);
                    Note(c, bar, step, length, pitch,
                         step == 0 ? 111 : step % 4 == 2 ? 102 : 84, r);
                }
                if (family == 2 && s != 2) Note(c, bar, 15, 1, ApproachBass(nextRoot, root), 69, r);
            }
            else if (g == 2) // TECHNO: repetitive machine pulse with small funky skips.
            {
                int[] steps = s == 1 ? new[] { 0, 7, 10 } :
                              s == 2 ? new[] { 0, 3, 6, 8, 11, 14 } :
                                       new[] { 0, 2, 4, 6, 8, 10, 12, 14 };
                for (int i = 0; i < steps.Length; i++)
                {
                    int step = steps[i];
                    int pitch = step == 8 && family == 1 ? root + 12 :
                                step == 11 && s == 2 ? root + fifth :
                                step == 14 && family == 3 ? root + 12 : root;
                    Note(c, bar, step, s == 1 ? (step == 0 ? 5 : 3) : 1, pitch,
                         step % 4 == 0 ? 109 : 82, r);
                }
                if (family == 2 && s == 2) Note(c, bar, 15, 1, ApproachBass(nextRoot, root), 72, r);
            }
            else if (g == 3) // BREAKS: broken, syncopated low end with octave calls.
            {
                int[] steps = s == 2 ? new[] { 0, 7, 10, 14 } :
                              s == 1 ? new[] { 0, 3, 6, 9, 11, 14 } :
                                       new[] { 0, 3, 7, 10, 14 };
                for (int i = 0; i < steps.Length; i++)
                {
                    int step = steps[i];
                    int pitch = step == 14 ? (family == 2 ? ApproachBass(nextRoot, root) : root + fifth) :
                                step == 7 && (family == 1 || s == 1) ? root + 12 :
                                step == 10 && family == 3 ? root + fifth : root;
                    Note(c, bar, step, step == 0 ? 3 : s == 2 && step == 7 ? 3 : 2,
                         pitch, step == 0 ? 112 : i % 2 == 0 ? 97 : 84, r);
                }
            }
            else if (g == 5) // FRENCH HOUSE: repeating filtered-disco bass hooks.
            {
                int[] steps = s == 0 ? new[] { 0, 3, 6, 8, 11, 14 } :
                              s == 1 ? new[] { 0, 2, 6, 10, 12, 14 } :
                                       new[] { 0, 3, 5, 7, 10, 13, 15 };
                for (int i = 0; i < steps.Length; i++)
                {
                    int step = steps[i];
                    int pitch = step == 15 ? ApproachBass(nextRoot, root) :
                                (step == 6 || step == 7) ? root + 12 :
                                (step == 10 && family != 0) ? root + fifth :
                                (step == 14 && answer && family >= 2) ? root + fifth : root;
                    Note(c, bar, step, s == 1 && step == 0 ? 3 : 1 + (step == 0 ? 1 : 0),
                         pitch, step == 0 ? 113 : step % 4 == 2 ? 101 : 84, r);
                }
            }
            else // FUNK: repetitive pocket, percussive rests, octave pops and pickups.
            {
                int[] steps = s == 0 ? new[] { 0, 3, 6, 10, 12, 14 } :
                              s == 1 ? new[] { 0, 3, 7, 10, 14 } :
                                       new[] { 0, 2, 5, 7, 10, 13, 15 };
                for (int i = 0; i < steps.Length; i++)
                {
                    int step = steps[i];
                    // Musical 5th, octave and approach colors instead of random leaps.
                    int pitch = step == 15 ? ApproachBass(nextRoot, root) :
                                step == 7 && s != 0 ? root + 12 :
                                step == 6 && family == 1 ? root + fifth :
                                step == 10 ? (family == 2 ? root + seventh - 12 : root + fifth) :
                                step == 14 && family == 3 ? root + 12 : root;
                    int length = step == 0 ? (s == 1 ? 3 : 2) :
                                 step == 10 && s == 1 ? 3 : 1 + (step == 12 ? 1 : 0);
                    Note(c, bar, step, length, pitch,
                         step == 0 ? 115 : step == 10 || step == 7 ? 105 : 82, r);
                }
                // Only occasional turnaround notes. Let the space create groove.
                if (answer && family == 1 && s != 2)
                    Note(c, bar, 15, 1, ApproachBass(nextRoot, root), 68, r);
            }
        }

        private static void GenerateMelody(ClipContentData c, int g, int s, int bar, int degree,
                                           int key, bool minor, int variation, Random r, int[] sourceChord = null)
        {
            // 90s HOUSE: anthemic, memorable call-and-response hooks with intentional
            // gaps for the vocal or bass. Chord tones anchor the phrase; short scale
            // passing notes and an end-of-pair pickup provide forward momentum.
            if (g == 1 && s == 3)
            {
                Generate90sHouseHook(c, bar, degree, key, minor, variation, r, sourceChord);
                return;
            }
            // Motifs repeat on bar pairs; answer bars vary the end, not every note.
            int family = Mod(variation - 1, 4);
            bool answer = (bar & 1) != 0;
            int[] starts = g == 0 ? (s == 1 ? new[] { 0, 8, 13 } : new[] { 0, 5, 10, 14 }) :
                           g == 1 ? (s == 3 ? new[] { 0, 3, 6, 10, 14 } : s == 1 ? new[] { 2, 10 } : new[] { 2, 6, 10, 14 }) :
                           g == 2 ? (s == 1 ? new[] { 0, 8 } : s == 2 ? new[] { 0, 3, 7, 11, 14 } : new[] { 0, 4, 6, 10, 12, 14 }) :
                           g == 3 ? (s == 2 ? new[] { 0, 8, 13 } : s == 1 ? new[] { 0, 3, 6, 10, 13, 15 } : new[] { 0, 3, 7, 10, 14 }) :
                                    (s == 0 ? new[] { 0, 3, 6, 10, 14 } : s == 1 ? new[] { 0, 7, 10, 14 } : new[] { 0, 2, 6, 9, 12, 15 });
            int[] motif = g == 1 && s == 3 ? new[] { 0, 2, 4, 3, 2, 0 } : new[] { 0, 2, 4, 2, 1, 0 };
            for (int i = 0; i < starts.Length; i++)
            {
                int position = starts[i];
                // Answer bar stays recognisable but breathes slightly differently.
                if (answer && i == starts.Length - 1 && position > 0 && family == 1)
                    position--;
                int chordOffset = motif[(i + (family == 2 ? 2 : 0)) % motif.Length];
                int melodicDegree = degree + chordOffset;
                if (answer && i == starts.Length - 1) melodicDegree = degree + (family == 3 ? 1 : 0);
                else if (family == 1 && i == 1) melodicDegree = degree + 4;
                else if (family == 3 && i == 2) melodicDegree = degree - 1;
                if (family == 2 && i == starts.Length - 2 && !answer) melodicDegree += 1;
                int pitch = Pitch(g == 2 ? 60 : 60, key, melodicDegree, minor);
                // Fold into the same playable 1-2 octave register.
                while (pitch > 83) pitch -= 12;
                while (pitch < 58) pitch += 12;
                int length = g == 1 && s == 3 ? (i == 0 ? 3 : 2) : g == 1 && s == 1 ? 4 : g == 0 && s == 1 ? 3 : g == 2 ? 1 : 2;
                if (sourceChord != null && sourceChord.Length > 0 && (i == 0 || i == starts.Length - 1 || position % 4 == 0))
                {
                    // Land on a nearby real chord tone at phrase anchors.
                    int best = pitch, distance = int.MaxValue;
                    foreach (int tone in sourceChord)
                        for (int shift = -12; shift <= 24; shift += 12)
                        {
                            int candidate = tone + shift;
                            if (candidate < 58 || candidate > 84) continue;
                            int d = Math.Abs(candidate - pitch);
                            if (d < distance) { best = candidate; distance = d; }
                        }
                    pitch = best;
                }
                if (answer && i == starts.Length - 1) length = Math.Max(1, length - 1);
                Note(c, bar, position, length, pitch, i == 0 ? 98 : 79 + (i % 2) * 9, r);
            }
        }

        private static void Generate90sHouseHook(ClipContentData c, int bar, int degree,
                                                  int key, bool minor, int variation, Random r,
                                                  int[] sourceChord)
        {
            int variant = Mod(variation - 1, 4);
            bool response = (bar % 2) == 1;
            // Snappy eighth/sixteenth-note phrasing: strong hook, a breath, then an
            // answering pickup. Each two-bar pair repeats its rhythmic identity.
            int[][] callSteps = { new[] { 0, 3, 4, 7, 10, 12, 14 },
                                  new[] { 0, 2, 4, 7, 10, 12, 15 },
                                  new[] { 0, 3, 6, 8, 11, 12, 14 },
                                  new[] { 0, 4, 6, 7, 10, 13, 15 } };
            int[][] replySteps = { new[] { 0, 2, 4, 6, 10, 11, 14 },
                                   new[] { 0, 3, 4, 8, 10, 13, 15 },
                                   new[] { 0, 2, 6, 8, 10, 12, 15 },
                                   new[] { 0, 3, 5, 8, 11, 12, 14 } };
            int[] starts = response ? replySteps[variant] : callSteps[variant];
            int[] callShape = { 0, 2, 4, 2, 1, 0, 2 };
            int[] replyShape = { 4, 2, 1, 0, 2, 1, 0 };
            for (int i = 0; i < starts.Length; ++i)
            {
                int melodicDegree = degree + (response ? replyShape[i] : callShape[i]);
                int pitch = Pitch(60, key, melodicDegree, minor);
                while (pitch > 79) pitch -= 12;
                while (pitch < 60) pitch += 12;
                // Anchor downbeats and held hook notes to the actual existing chord.
                // Use any supplied arrangement harmony, not merely the inferred scale.
                if (sourceChord != null && sourceChord.Length > 0 && (i == 0 || i == 3 || i == 5))
                {
                    int best = pitch, bestDistance = int.MaxValue;
                    foreach (int chordPitch in sourceChord)
                        for (int octave = -2; octave <= 2; ++octave)
                        {
                            int candidate = chordPitch + octave * 12;
                            if (candidate < 60 || candidate > 81) continue;
                            int distance = Math.Abs(candidate - pitch);
                            if (distance < bestDistance) { bestDistance = distance; best = candidate; }
                        }
                    pitch = best;
                }
                int gate = i == 0 || i == 4 ? 3 : i == 3 ? 2 : 1;
                int accent = i == 0 || i == 3 || i == 4 ? 104 : (i == 6 ? 90 : 83);
                Note(c, bar, starts[i], gate, pitch, accent, r);
            }
        }

        private static int[] VoiceChord(int degree, int key, bool minor, int g, int s,
                                         bool pads, int[] previous)
        {
            int root = Pitch(pads ? 48 : 48, key, degree, minor);
            int third = Scale(degree + 2, minor) - Scale(degree, minor);
            int fifth = Scale(degree + 4, minor) - Scale(degree, minor);
            int seventh = Scale(degree + 6, minor) - Scale(degree, minor);
            int ninth = Scale(degree + 8, minor) - Scale(degree, minor);
            int[] intervals = g == 2 ? (s == 1 ? new[] { 0, fifth, ninth } : new[] { 0, third, fifth }) :
                              g == 0 ? (s == 2 ? new[] { third, seventh, ninth } : new[] { 0, third, seventh, ninth }) :
                              g == 1 ? (s == 3 ? new[] { third, fifth, seventh, ninth } : s == 1 ? new[] { 0, third, seventh, ninth } : new[] { 0, third, fifth, seventh }) :
                              g == 3 ? (s == 1 ? new[] { third, fifth, seventh, ninth } : new[] { 0, third, fifth, seventh }) :
                                       (s == 1 ? new[] { third, seventh, ninth } : new[] { 0, third, seventh, ninth });
            int[] voices = new int[intervals.Length];
            for (int i = 0; i < voices.Length; i++)
            {
                int initial = root + intervals[i];
                // Keep lower-mid voicings for warmth, but don't let repeated inversions
                // cause 12-semitone jumps between adjacent chords.
                int target = previous != null ? previous[Math.Min(i, previous.Length - 1)] :
                             (pads ? 57 + i * 5 : 55 + i * 5);
                int best = Clamp(initial, 45, 84);
                int score = int.MaxValue;
                for (int octave = -3; octave <= 2; octave++)
                {
                    int candidate = initial + 12 * octave;
                    if (candidate < (pads ? 48 : 50) || candidate > (pads ? 82 : 79)) continue;
                    int distance = Math.Abs(candidate - target) + Math.Abs(candidate - (pads ? 59 + 5 * i : 56 + 5 * i)) / 4;
                    if (distance < score) { score = distance; best = candidate; }
                }
                voices[i] = best;
            }
            // Keep pitches unique without pushing extreme inversions.
            for (int i = 1; i < voices.Length; i++)
                if (voices[i] == voices[i - 1] && voices[i] + 12 <= 82) voices[i] += 12;
            return voices;
        }
        private static void GenerateHarmony(ClipContentData c, int g, int s, int bar,
                                             bool pads, int[] voices, int variation, Random r)
        {
            int[] starts;
            int[] lengths;
            if (pads)
            {
                starts = g == 0 ? new[] { 2 } : g == 3 && s != 2 ? new[] { 0, 10 } :
                         g == 4 && s == 2 ? new[] { 0, 10 } : new[] { 0 };
                lengths = starts.Length == 2 ? new[] { 9, 6 } : new[] { g == 0 ? 12 : 16 };
                if (g == 2 && s == 2) { starts = new[] { 0, 8 }; lengths = new[] { 7, 8 }; }
            }
            else if (g == 0)
            {
                starts = s == 1 ? new[] { 2, 12 } : s == 2 ? new[] { 3, 7, 11, 14 } : new[] { 0, 9 };
                lengths = s == 1 ? new[] { 8, 3 } : s == 2 ? new[] { 2, 2, 2, 2 } : new[] { 6, 4 };
            }
            else if (g == 1)
            {
                // 90s chords: alternate sustained piano punctuation with compact
                // rave stabs. Short, rhythmically displaced answers build cadence.
                starts = s == 3 ? (bar % 2 == 0 ? new[] { 0, 6, 10, 14 } : new[] { 0, 4, 7, 11, 14 }) : s == 1 ? new[] { 0, 12 } : s == 2 ? new[] { 2, 6, 9, 14 } : new[] { 2, 6, 10, 14 };
                lengths = s == 3 ? (bar % 2 == 0 ? new[] { 4, 2, 3, 2 } : new[] { 3, 2, 2, 2, 2 }) : s == 1 ? new[] { 12, 4 } : s == 2 ? new[] { 2, 2, 2, 2 } : new[] { 2, 2, 2, 2 };
            }
            else if (g == 2)
            {
                starts = s == 1 ? new[] { 0, 12 } : s == 2 ? new[] { 0, 7, 11 } : new[] { 2, 6, 10, 14 };
                lengths = s == 1 ? new[] { 9, 3 } : s == 2 ? new[] { 2, 1, 2 } : new[] { 2, 2, 2, 2 };
            }
            else if (g == 3)
            {
                starts = s == 2 ? new[] { 0, 8 } : s == 1 ? new[] { 0, 3, 7, 11, 14 } : new[] { 0, 7, 11 };
                lengths = s == 2 ? new[] { 7, 8 } : s == 1 ? new[] { 2, 1, 2, 1, 2 } : new[] { 3, 2, 3 };
            }
            else if (g == 5)
            {
                starts = s == 0 ? new[] { 2, 6, 10, 14 } : s == 1 ? new[] { 0, 6, 10, 14 } : new[] { 2, 5, 10, 13 };
                lengths = s == 1 ? new[] { 3, 2, 2, 2 } : new[] { 2, 2, 2, 1 };
            }
            else // Funk chords: tight stabs that sit between the bass notes.
            {
                starts = s == 0 ? new[] { 0, 6, 10, 14 } :
                         s == 1 ? new[] { 0, 7, 11 } : new[] { 0, 3, 7, 10, 14 };
                lengths = s == 0 ? new[] { 2, 2, 2, 1 } :
                          s == 1 ? new[] { 4, 2, 3 } : new[] { 1, 2, 1, 2, 1 };
            }
            int family = Mod(variation - 1, 4);
            for (int i = 0; i < starts.Length; i++)
            {
                // Different variations keep the harmonic family but change timing,
                // gates and upper-voice accents rather than becoming random clusters.
                int step = starts[i];
                if (!pads && family == 1 && i == starts.Length - 1 && step > 0) step--;
                if (!pads && family == 3 && i == 1 && step < 15) step++;
                int gate = lengths[i];
                if (!pads && family == 2) gate = Math.Max(1, gate - 1);
                if (pads && family == 3 && i == 0) gate = Math.Max(6, gate - 3);
                int vel = pads ? 69 : g == 2 ? 79 : g == 1 && s == 3 ? (i == 0 || i == 2 ? 103 : 86) : g == 1 ? 84 : 77;
                for (int voice = 0; voice < voices.Length; voice++)
                    Note(c, bar, step, gate,
                         voices[voice] + (!pads && family == 2 && voice == voices.Length - 1 && i > 0 ? 12 : 0),
                         vel - voice * 3 - (i % 2) * 5, r);
            }
        }
        private static int Clamp(int value, int minimum, int maximum) => Math.Max(minimum, Math.Min(maximum, value));
        private static int Mod(int value, int count) => ((value % count) + count) % count;
    }
}
