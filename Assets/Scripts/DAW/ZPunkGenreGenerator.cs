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

        // 2.3.x generator refinement: all 19 styles have an explicit musical identity.
        // These limits shape MIDI composition only. No audio, patches, kit levels or
        // transport/tick code is changed. Profiles are intentionally conservative:
        // a good motif repeated musically is preferable to a busier random phrase.
        private struct StyleDna
        {
            public readonly int BassNotes, SynthNotes, BassHold, SynthHold;
            public readonly int PadVoices, MotifWeight;
            public StyleDna(int bassNotes, int synthNotes, int bassHold, int synthHold,
                            int padVoices, int motifWeight)
            {
                BassNotes = bassNotes; SynthNotes = synthNotes;
                BassHold = bassHold; SynthHold = synthHold;
                PadVoices = padVoices; MotifWeight = motifWeight;
            }
        }

        private static readonly StyleDna[][] StyleDnaProfiles = {
            // BOOM BAP: classic pocket / dusty restraint / rhythmic displacement.
            new[] { new StyleDna(4, 4, 0, 0, 3, 2), new StyleDna(3, 3, 2, 1, 3, 3),
                    new StyleDna(5, 5, 0, 0, 3, 2) },
            // HOUSE: bounce / long-form warmth / chopped jackin / bold 90s hooks.
            new[] { new StyleDna(5, 4, 0, 0, 4, 2), new StyleDna(3, 3, 2, 2, 4, 3),
                    new StyleDna(7, 4, -1, 0, 3, 2), new StyleDna(6, 6, 0, 0, 4, 2) },
            // TECHNO: existing independent composers retain their sixteenth grids.
            new[] { new StyleDna(12, 10, 0, 0, 2, 3), new StyleDna(4, 3, 0, 0, 2, 3),
                    new StyleDna(9, 7, 0, 0, 2, 3) },
            // BREAKS: room beneath the break, especially in Jungle / Half-Time.
            new[] { new StyleDna(4, 4, 2, 0, 3, 2), new StyleDna(3, 4, 3, 0, 3, 3),
                    new StyleDna(3, 2, 3, 1, 2, 3) },
            // FUNK: disco octave motion / pocket answers / electro syncopation.
            new[] { new StyleDna(5, 4, 0, 0, 4, 2), new StyleDna(4, 3, 1, 0, 3, 3),
                    new StyleDna(6, 6, 0, 0, 3, 2) },
            // FRENCH HOUSE: looping filtered hooks / disco / two-bar touch phrases.
            new[] { new StyleDna(5, 3, 0, 0, 3, 3), new StyleDna(6, 5, 0, 0, 4, 2),
                    new StyleDna(6, 5, 0, 0, 4, 3) }
        };

        private static StyleDna Dna(int g, int s) { return StyleDnaProfiles[g][s]; }

        // Pick a balanced subset of the original genre's phrase, not the first N
        // hits: that preserves rhythmic gaps and ensures long notes have room.
        private static int[] LimitPhraseNotes(int[] steps, int limit)
        {
            if (steps.Length <= limit || limit <= 0) return steps;
            if (limit == 1) return new[] { steps[0] };
            var result = new int[limit];
            for (int i = 0; i < limit; i++)
                result[i] = steps[(i * (steps.Length - 1) + (limit - 1) / 2) / (limit - 1)];
            return result;
        }

        // A / A' / B / A'': stable first-bar fingerprint, small reply, variation
        // in the third bar, and reprise.  A separate chord plan transposes motifs
        // as harmonic roots change.  The same principle applies at 1-4 bar lengths.
        private static int[] MotifRhythm(int[][] grids, int g, int s, int bar, int bars,
                                         int rhythm, int character, int tempoBand, bool bass)
        {
            int variant = Mod(rhythm + (bar == 2 ? 1 : 0), grids.Length);
            int[] steps = ShapeRhythm(grids[variant], g, s, bar, bars,
                                      tempoBand, bass, character);
            var notes = new List<int>(steps);
            if (bar == 1 && notes.Count >= 4 && (character & 1) == 1)
            {
                // A tiny answering pickup, not another completely unrelated bar.
                int idx = notes.Count - 2;
                int reply = notes[idx] + ((character % 3 == 0) ? -1 : 1);
                if (reply > 0 && reply < 16 && !notes.Contains(reply) &&
                    !notes.Contains(reply - 1) && !notes.Contains(reply + 1))
                    notes[idx] = reply;
            }
            notes.Sort();
            StyleDna dna = Dna(g, s);
            return LimitPhraseNotes(notes.ToArray(), bass ? dna.BassNotes : dna.SynthNotes);
        }

        // Pads should be a harmonic bed rather than a second full chord-stab track.
        // Use the existing chord tones and voicing search, trimming only density.
        private static int[] ShapePadVoicing(int[] voicing, int g, int s)
        {
            int limit = Dna(g, s).PadVoices;
            if (voicing.Length <= limit) return voicing;
            var shaped = new int[limit];
            for (int i = 0; i < limit; i++)
                shaped[i] = voicing[i * (voicing.Length - 1) / (limit - 1)];
            return shaped;
        }

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
                new[] { "Relentless sixteenth-note sequencers, rolling hats and measured turnarounds", "Stripped four-to-floor groove, repeating offbeat cells and negative space", "Pounding kicks, metallic cross-rhythms and abrasive clipped percussion" },
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
                new[] { "Driving 16th-note low-end with octave accents and a repeating hook", "Lean, hypnotic low notes with deliberate offbeat silence", "Aggressive short-note ostinatos, root/fifth impacts and rolling pickups" },
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
                new[] { "Short power-stabs beneath a fast sixteenth-note sequence", "Sparse two-note stabs and long, unobtrusive drones", "Clipped minor-power stabs and dark open-fifth textures" },
                new[] { "Tight soulful stabs between snare accents", "Chopped voicings with quick delayed replies", "Sustained seventh colors over a half-time pulse" },
                new[] { "Short disco sevenths and ninths with rhythmic replies", "Sparse, warm ninth voicings and tightly muted stabs", "Bright clipped sevenths with synth-funk syncopation" },
                new[] { "Filtered disco sevenths and clipped offbeat stabs", "Classic French-touch piano chords with rhythmic responses", "Short, bright 90s-influenced house chord punches" }
            };
            return d[Clamp(g, 0, Genres.Length - 1)][Clamp(s, 0, d[Clamp(g, 0, Genres.Length - 1)].Length - 1)];
        }

        public static void Fill(ClipContentData clip, TrackKind kind, int genre, int style,
                                int bars, int key, bool minor, int variation, bool synthChords = false,
                                int[] harmonyDegrees = null, int[][] harmonyVoices = null,
                                int progressionChoice = 0, int progressionBarOffset = 0, float currentBpm = 120f)
        {
            if (clip == null) throw new ArgumentNullException(nameof(clip));
            if (kind == TrackKind.Chops) return;
            genre = Clamp(genre, 0, Genres.Length - 1);
            style = Clamp(style, 0, Styles[genre].Length - 1);
            bars = Clamp(bars, 1, 4);
            key = Mod(key, 12);
            progressionChoice = Clamp(progressionChoice, 0, ProgressionChoiceCount - 1);
            // BPM is a compositional parameter only. Do not modify MIDI ticks or transport.
            if (float.IsNaN(currentBpm) || float.IsInfinity(currentBpm)) currentBpm = 120f;
            currentBpm = Math.Max(40f, Math.Min(240f, currentBpm));
            int tempoBand = TempoBand(genre, style, currentBpm);
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
            // Keep BPM in the deterministic identity, including tenths for decimal tempos.
            // At 120 BPM preserve the old seed as a useful 2.2.6 reference point.
            int seed = unchecked(variation * 104729 + genre * 3659 + style * 1619 +
                                 (int)kind * 307 + key * 47 + (minor ? 173 : 97) +
                                 ((int)Math.Round(currentBpm * 10.0) - 1200) * 1009);
            if (kind == TrackKind.Drums)
            {
                // Keep the tick-based drum contract; develop each genre's own rhythm.
                var drumRng = new Random(seed);
                for (int bar = 0; bar < bars; bar++)
                    GenerateDrums(clip, genre, style, bar, bars, variation, tempoBand, drumRng);
                AlignToStart(clip);
            }
            else
            {
                int[] harmony = MakeHarmonyPlan(bars, genre, minor, progressionChoice,
                                                 progressionBarOffset, harmonyDegrees);
                // INDUSTRIAL default is a tonic-locked EBM vamp, not a forced
                // V-chord turnaround. Respect explicitly selected progressions
                // and externally supplied harmonic degrees.
                if (genre == 2 && style == 2 && progressionChoice == 0 && harmonyDegrees == null)
                    for (int bar = 0; bar < harmony.Length; bar++) harmony[bar] = 0;
                // Compute once per clip. All three instrument tracks see the same
                // chord identities even when generated in separate passes.
                var chordPlan = new ZPunkChordLibrary.Chord[bars];
                for (int bar = 0; bar < bars; bar++)
                    chordPlan[bar] = ZPunkChordLibrary.ForBar(genre, style, minor,
                        harmony[bar], progressionChoice, bar, progressionBarOffset);
                List<NoteEvent> bestNotes = null;
                double bestScore = double.MinValue;
                // Shared rhythmic fingerprint is independent of instrument kind;
                // tracks generated in any order can complement each other when
                // they use the same variation, key, genre and style.
                int groove = Mod(variation * 13 + genre * 7 + style * 5, 4);
                // All candidates are deterministic and editable. Score BEFORE
                // humanization; expressive jitter should not dictate composition.
                const int CandidateCount = 12;
                for (int candidate = 0; candidate < CandidateCount; candidate++)
                {
                    clip.notes.Clear();
                    var candidateRng = new Random(CandidateSeed(seed, candidate, harmony, bars));
                    GenerateMidiCandidate(clip, kind, genre, style, bars, key, minor,
                                          harmony, chordPlan, synthChords, harmonyVoices,
                                          tempoBand, groove, candidateRng);
                    double score = ScoreCandidate(clip, kind, genre, style, minor, key,
                                                  harmony, chordPlan,
                                                  synthChords && kind == TrackKind.Synth,
                                                  harmonyVoices, tempoBand, groove);
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

        // Style-specific tempo centers: the same BPM need not produce the same
        // density response in DUSTY boom bap, DRIVING techno or HALF-TIME breaks.
        private static int TempoBand(int g, int s, float bpm)
        {
            float center = g == 0 ? (s == 1 ? 82f : 93f) :
                g == 1 ? (s == 1 ? 120f : s == 3 ? 132f : 126f) :
                g == 2 ? (s == 1 ? 130f : s == 2 ? 140f : 136f) :
                g == 3 ? (s == 2 ? 92f : s == 1 ? 165f : 142f) :
                g == 4 ? (s == 1 ? 105f : s == 2 ? 118f : 114f) :
                         (s == 0 ? 122f : 126f);
            float difference = bpm - center;
            return difference <= -35f ? -2 : difference <= -13f ? -1 :
                   difference >= 35f ? 2 : difference >= 12f ? 1 : 0;
        }

        // Small, style-aware rhythmic shaping applied to the original vocabulary.
        // Changes use integer sixteenth positions so no quantization drift is possible.
        private static int[] ShapeRhythm(int[] source, int g, int s, int bar, int bars,
                                         int tempoBand, bool bass, int character)
        {
            var steps = new List<int>(source);
            bool spare = (g == 0 && s == 1) || (g == 1 && s == 1) ||
                         (g == 2 && s == 1) || (g == 3 && s == 2) ||
                         (g == 4 && s == 1);
            // At high absolute tempos retain the pocket rather than mechanically
            // filling every subdivision. Techno instead sharpens its sequencer pulse.
            if (tempoBand >= 1)
            {
                if (g == 2 && s != 1)
                    InsertStep(steps, bass ? (character % 2 == 0 ? 11 : 7) :
                                          (character % 2 == 0 ? 3 : 13), 1);
                else if (spare && steps.Count > 3 && (bar + character) % 2 == 0)
                    steps.RemoveAt(steps.Count - 2);
                else if ((g == 3 && s == 1) || (g == 4 && s == 2))
                    InsertStep(steps, 15, 1);
            }
            else if (tempoBand <= -1)
            {
                // At slower tempos make an occasional response audible, except in
                // deliberately sparse styles (including MINIMAL techno).
                if (!spare && (g == 0 || g == 1 || g == 4 || g == 5) &&
                    (bar + character) % 2 == 0)
                    InsertStep(steps, bass ? 11 : 13, 2);
            }
            // Development is constrained to multi-bar phrases. A one-bar loop
            // keeps the stable original motif and its immediately usable ending.
            if (bars == 4 && bar == 2 && !spare && (g == 1 || g == 4 || g == 5))
                InsertStep(steps, bass ? 9 : 11, 2);
            if (bars > 1 && bar == bars - 1 && (g == 0 || g == 3) && s != 1)
                InsertStep(steps, 15, 1);
            steps.Sort();
            return steps.ToArray();
        }

        private static void InsertStep(List<int> steps, int step, int minDistance)
        {
            if (step < 0 || step >= 16) return;
            foreach (int existing in steps)
                if (Math.Abs(existing - step) < minDistance + 1) return;
            steps.Add(step);
        }

        private static int ShapeLength(int length, int g, int s, int tempoBand)
        {
            if (g == 2) // Sequenced techno: clipped sixteenths, not half-note pulses.
                return Math.Min(length, s == 1 && tempoBand < 1 ? 2 : 1);
            if (tempoBand >= 1 && (g == 1 || g == 3 || g == 4 || g == 5))
                return Math.Max(1, length - 1);
            if (tempoBand <= -1 && ((g == 1 && s == 1) || (g == 0 && s == 1)))
                return Math.Min(6, length + 1);
            return length;
        }

        // 0 = original genre-specific harmony for backwards compatibility.
        // 1..8 = original diatonic progressions, shared across BASS/SYNTH/PADS.
        // 9..12 = MIDI-derived extended/borrowed progressions. The legacy arrays
        // remain ZERO-BASED scale degrees; new chromatic chord roots are in the library.
        // Eight legacy choices remain in their original positions. Four advanced
        // MIDI progressions are appended, so saved project choices keep their meaning.
        public const int ProgressionChoiceCount = 9 + ZPunkChordLibrary.LibraryProgressionCount;
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
            if (ZPunkChordLibrary.IsLibraryChoice(choice))
                return ZPunkChordLibrary.LibraryDegrees(minor, choice);
            if (choice < 1 || choice >= ProgressionChoiceCount) return Progression(genre, minor);
            return minor ? MinorProgressions[choice - 1] : MajorProgressions[choice - 1];
        }

        public static string ProgressionLabel(int genre, bool minor, int choice)
        {
            if (ZPunkChordLibrary.IsLibraryChoice(choice))
                return choice.ToString("00") + "  " + ZPunkChordLibrary.ProgressionLabel(choice);
            if (choice < 1 || choice >= ProgressionChoiceCount) return "GENRE DEFAULT";
            return choice.ToString("00") + "  " +
                (minor ? MinorProgressionLabels[choice - 1] : MajorProgressionLabels[choice - 1]);
        }

        // Musical names for the CURRENT key, e.g., A minor: Am - F - C - G.
        public static string ProgressionChordNames(int genre, int key, bool minor, int choice)
        {
            if (ZPunkChordLibrary.IsLibraryChoice(choice))
                return ZPunkChordLibrary.LibraryChordNames(key, minor, choice, Keys);
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
        private static void GenerateDrums(ClipContentData c, int g, int s, int bar, int bars, int variation, int tempoBand, Random r)
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
                GenerateTechnoDrums(c, s, bar, bars, family, tempoBand, r);
                // Techno has its own restrained, style-dependent phrase ending.
                // The generic fill would erase MINIMAL's intentional space.
                return;
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
            // BPM-aware punctuation sits on top of established kick/snare anchors.
            // All extra events use existing lanes and the exact 16th grid.
            if (g == 0 && tempoBand < 0 && s != 1)
                Drum(c, bar, 15, 6, 42, r, .42);
            else if (g == 1 && tempoBand > 0 && s != 1)
                Drum(c, bar, bar % 2 == 0 ? 7 : 15, 3, 48, r, .67);
            else if (g == 2 && tempoBand > 0)
                Drum(c, bar, s == 1 ? 11 : 15, 3, 55, r, s == 1 ? .45 : .85);
            else if (g == 3 && tempoBand > 0 && s == 1)
                Drum(c, bar, bar % 2 == 0 ? 7 : 15, 6, 44, r, .7);
            else if (g == 4 && tempoBand < 0 && s != 1)
                Drum(c, bar, 15, 6, 42, r, .56);
            else if (g == 5 && tempoBand > 0 && s == 2)
                Drum(c, bar, 15, 3, 53, r, .65);
            if (fill)
            {
                Drum(c, bar, 14, 6, 58, r, .7);
                Drum(c, bar, 15, 1, 59, r, g == 3 ? .6 : .28);
                Drum(c, bar, 15, 7, 65, r, .3);
            }
        }

        // TECHNO GROOVES: each style has its own rhythmic relationship between
        // kick, bass and pitched motif. Drum lanes are the existing eight ZPUNK lanes.
        // No new drum choke behaviour, transport changes or runtime dependencies.
        private static void GenerateTechnoDrums(ClipContentData c, int style, int bar,
                                               int bars, int family, int tempoBand, Random r)
        {
            bool turn = (bar & 1) != 0;
            bool last = bars > 1 && bar == bars - 1;
            Hits(c, bar, 0, new[] { 0, 4, 8, 12 }, style == 1 ? 113 : 120, r);
            if (style == 1) // MINIMAL: repetition, gaps, micro-percussion and restraint.
            {
                // The shuffled little tick is not a second full snare layer.
                Drum(c, bar, turn ? 12 : 4, 1, 74, r, .68);
                Hits(c, bar, 4, new[] { 2, 6, 10, 14 }, 68, r, .72);
                Hits(c, bar, 3, family % 2 == 0 ? new[] { 3, 11 } : new[] { 6, 14 }, 54, r, .75);
                Drum(c, bar, turn ? 11 : 7, 6, 54, r, .78);
                if (family == 2 && turn) Drum(c, bar, 15, 3, 46, r, .65);
                if (last && family == 3) Drum(c, bar, 15, 6, 49, r, .8);
            }
            else if (style == 2) // INDUSTRIAL: weight + 3/3/2 metallic cross-rhythm.
            {
                Hits(c, bar, 1, new[] { 4, 12 }, 109, r);
                Drum(c, bar, 12, 2, 78, r, .75);
                Hits(c, bar, 4, new[] { 2, 6, 10, 14 }, 89, r, .88);
                int[] metal = family % 2 == 0 ? new[] { 3, 6, 11, 14 } : new[] { 2, 5, 10, 13 };
                Hits(c, bar, 6, metal, 67, r, .82);
                Hits(c, bar, 3, new[] { 0, 2, 3, 6, 8, 10, 11, 14 }, 63, r, .86);
                Drum(c, bar, turn ? 15 : 7, 0, 78, r, .75);
                if (last) { Drum(c, bar, 15, 6, 83, r, .9); Drum(c, bar, 14, 3, 75, r, .78); }
                if (family == 3 && turn) Drum(c, bar, 15, 7, 65, r, .5);
            }
            else // DRIVING: retain a constant rolling pulse and mostly 16th-note motion.
            {
                Hits(c, bar, 1, new[] { 4, 12 }, 101, r, .93);
                Hits(c, bar, 4, new[] { 2, 6, 10, 14 }, 91, r, .96);
                Hits(c, bar, 3, new[] { 0, 2, 4, 6, 8, 10, 12, 14 }, 70, r, .94);
                Hits(c, bar, 3, turn ? new[] { 3, 7, 11, 15 } : new[] { 3, 11, 15 },
                     52, r, tempoBand >= 1 ? .84 : .69);
                Drum(c, bar, turn ? 11 : 7, 6, 55, r, .67);
                if (last && family % 2 == 0) Drum(c, bar, 15, 6, 67, r, .84);
            }
        }

        // Each candidate has an A / A' / B / A'' relationship, and one harmonic
        // plan shared by BASS, SYNTH and PADS (including user-selected progressions).
        // All times are integer sixteenth subdivisions, not half-note pulses.
        private static void ComposeTechnoCandidate(ClipContentData c, TrackKind kind,
                                                  int style, int bars, int key,
                                                  ZPunkChordLibrary.Chord[] chords,
                                                  bool synthChords, int tempoBand,
                                                  int motif, int rhythm, int character,
                                                  Random r)
        {
            for (int bar = 0; bar < bars; bar++)
            {
                var chord = chords[bar];
                var next = chords[(bar + 1) % bars];
                if (kind == TrackKind.Bass)
                    ComposeTechnoBass(c, style, bar, bars, key, chord, next,
                                     motif, rhythm, character, tempoBand, r);
                else if (kind == TrackKind.Pads)
                    ComposeTechnoTexture(c, style, bar, bars, key, chords,
                                         character, r);
                else if (synthChords)
                    ComposeTechnoStabs(c, style, bar, bars, key, chord,
                                      rhythm, character, r);
                else
                    ComposeTechnoLead(c, style, bar, bars, key, chord,
                                      motif, rhythm, character, tempoBand, r);
            }
        }

        // Four adjacent rhythmic cells with a stable 1st-bar identity. A variation
        // changes articulation, an occasional pickup, or one answer -- not the
        // whole phrase every bar. In MINIMAL this is deliberately *very* stable.
        private static int[] TechnoSteps(int[][] patterns, int style, int bar,
                                         int bars, int rhythm, int character,
                                         int tempoBand, bool bass)
        {
            // Industrial chooses from eight motifs deterministically; the
            // motif's identity remains stable across all bars, except one small
            // A/B turn in bar three. DRIVING and MINIMAL remain unchanged.
            int choice = style == 2
                ? Mod(rhythm * 2 + character + (bar == 2 ? 1 : 0), patterns.Length)
                : Mod(rhythm + (style == 1 ? 0 : (bar == 2 ? 1 : 0)), patterns.Length);
            var steps = new List<int>(patterns[choice]);
            if (style == 1)
            {
                // One deliberate edit in the final bar; preserve the hypnotic loop.
                if (bars > 1 && bar == bars - 1 && character % 2 == 0)
                    InsertStep(steps, bass ? 15 : 14, 1);
            }
            else if (style == 2)
            {
                if (bar % 2 == 1 && character % 2 == 0)
                    InsertStep(steps, bass ? 14 : 15, 0);
                if (bars > 1 && bar == bars - 1 && tempoBand > 0)
                    InsertStep(steps, bass ? 15 : 13, 0);
            }
            else // Driving: maintain an active 16th-note sequencer at *all* tempos.
            {
                if (tempoBand >= 0) InsertStep(steps, (bar & 1) == 0 ? 11 : 13, 0);
                if (bars > 1 && bar == bars - 1)
                    InsertStep(steps, bass ? 15 : 14, 0);
            }
            steps.Sort();
            return steps.ToArray();
        }

        private static readonly int[][] TechnoMinimalBass = {
            new[] { 3, 7, 11, 15 }, new[] { 2, 6, 11, 14 },
            new[] { 0, 7, 10, 14 }, new[] { 3, 6, 10, 15 }
        };
        private static readonly int[][] TechnoIndustrialBass = {
            new[] { 0, 3, 6, 8, 11, 14 }, new[] { 0, 2, 5, 8, 11, 13, 15 },
            new[] { 0, 1, 4, 7, 8, 11, 14 },
            // More 16-step motifs: alternating machine pulse, rolling offbeat,
            // stuttering two-note cell and tension/release gaps.
            new[] { 0, 2, 3, 6, 8, 10, 11, 14 },
            new[] { 0, 3, 4, 7, 8, 11, 12, 15 },
            new[] { 0, 2, 5, 6, 9, 10, 13, 14 },
            new[] { 0, 1, 3, 7, 8, 9, 11, 15 }
        };
        private static readonly int[][] TechnoDrivingBass = {
            new[] { 0, 2, 3, 5, 6, 8, 10, 11, 13, 15 },
            new[] { 0, 1, 3, 4, 6, 7, 9, 11, 14, 15 },
            new[] { 0, 2, 4, 5, 8, 9, 11, 12, 14, 15 },
            new[] { 0, 2, 3, 5, 7, 8, 10, 11, 13, 15 }
        };
        private static readonly int[][] TechnoMinimalLead = {
            new[] { 3, 11 }, new[] { 2, 7, 14 },
            new[] { 5, 13 }, new[] { 3, 10, 14 }
        };
        private static readonly int[][] TechnoIndustrialLead = {
            new[] { 0, 3, 6, 10, 13 }, new[] { 2, 5, 8, 11, 14 },
            new[] { 1, 4, 7, 11, 14 },
            new[] { 0, 4, 6, 8, 12, 14 },
            new[] { 2, 3, 7, 10, 11, 15 },
            new[] { 0, 3, 6, 9, 13 },
            new[] { 1, 5, 8, 12, 15 }
        };
        private static readonly int[][] TechnoDrivingLead = {
            new[] { 0, 2, 3, 5, 7, 8, 10, 13, 15 },
            new[] { 0, 1, 3, 4, 7, 9, 11, 14 },
            new[] { 0, 3, 4, 6, 7, 10, 12, 14, 15 },
            new[] { 0, 2, 4, 5, 8, 9, 11, 13, 15 }
        };

        private static void ComposeTechnoBass(ClipContentData c, int style, int bar,
                                              int bars, int key,
                                              ZPunkChordLibrary.Chord chord,
                                              ZPunkChordLibrary.Chord next, int motif,
                                              int rhythm, int character, int tempoBand,
                                              Random r)
        {
            int[][] vocabulary = style == 1 ? TechnoMinimalBass :
                                 style == 2 ? TechnoIndustrialBass : TechnoDrivingBass;
            int[] steps = TechnoSteps(vocabulary, style, bar, bars, rhythm,
                                      character, tempoBand, true);
            int root = BassRootFromSemitone(key + chord.RootSemitones);
            int nextRoot = BassRootFromSemitone(key + next.RootSemitones);
            int fifth = chord.ToneNearest(7, 7);
            int pitchChange = motif % 4;
            for (int i = 0; i < steps.Length; i++)
            {
                int step = steps[i];
                int pitch = root;
                if (style == 1)
                {
                    // Minimal is a near single-pitch ostinato. One octave note
                    // punctuates, rather than rewriting, the repeating cell.
                    if (i == steps.Length - 1 && (bar == bars - 1 || pitchChange == 2))
                        pitch = root + (pitchChange == 1 ? fifth : 12);
                }
                else if (style == 2)
                {
                    // EBM: a repeating root/upper-root cell. One *chosen*
                    // tension tone (minor second or tritone) lands in a fixed
                    // late-step slot, rather than random off-key notes.
                    if ((i + pitchChange) % 5 == 3) pitch = root + fifth;
                    else if ((i + motif) % 4 == 1) pitch = root + 12;
                    int tensionStep = (character % 2 == 0) ? 11 : 13;
                    if (step == tensionStep && (bar & 1) == 1)
                        pitch = root + (character % 3 == 0 ? 1 : 6);
                    // A brief semitone pickup in the fourth bar resolves to
                    // the next tonic; preserve manually chosen chord changes.
                    if (bar == bars - 1 && step == 15 && character == 4)
                        pitch = root - 1;
                }
                else // DRIVING: short low 16ths with purposeful octave accents.
                {
                    if ((i + pitchChange) % 5 == 3) pitch = root + 12;
                    else if ((i + motif) % 8 == 6) pitch = root + fifth;
                }
                if (i == steps.Length - 1 && step >= 13 && bar == bars - 1 &&
                    next.RootSemitones != chord.RootSemitones && style != 1)
                    pitch = BassApproach(root, nextRoot);
                int length = style == 1 && (i + motif) % 3 == 0 ? 2 : 1;
                int nextStep = i + 1 < steps.Length ? steps[i + 1] : 16;
                length = Math.Min(length, Math.Max(1, nextStep - step));
                int velocity = style == 1 ? (i == 0 ? 103 : step % 4 == 0 ? 95 : 84) :
                               style == 2 ? (step % 4 == 0 ? 118 : step % 2 == 1 ? 103 : 91) :
                                            (step % 4 == 0 ? 112 : step % 2 == 1 ? 91 : 99);
                AddMidi(c, bar, step, length, Clamp(pitch, 35, 60), velocity, r);
            }
        }

        private static void ComposeTechnoLead(ClipContentData c, int style, int bar,
                                              int bars, int key,
                                              ZPunkChordLibrary.Chord chord,
                                              int motif, int rhythm, int character,
                                              int tempoBand, Random r)
        {
            int[][] vocabulary = style == 1 ? TechnoMinimalLead :
                                 style == 2 ? TechnoIndustrialLead : TechnoDrivingLead;
            int[] steps = TechnoSteps(vocabulary, style, bar, bars, rhythm,
                                      character, tempoBand, false);
            int tonic = FoldPitch(60 + key + chord.RootSemitones, 66, 57, 75);
            int fifth = chord.ToneNearest(7, 7);
            int third = chord.ToneNearest(3, 4);
            for (int i = 0; i < steps.Length; i++)
            {
                int step = steps[i];
                int pitch = tonic;
                if (style == 1)
                {
                    // Repeat a small one- or two-note cell; answer at phrase end.
                    if (i == steps.Length - 1 && (character + rhythm) % 3 == 0)
                        pitch += fifth;
                }
                else if (style == 2)
                {
                    if ((i + motif) % 5 == 2) pitch += fifth;
                    else if ((i + motif) % 5 == 4) pitch += 12;
                    // Two recurring, intentional tension colors. The pitch
                    // returns to the root on subsequent steps.
                    if (step == (character % 2 == 0 ? 11 : 13) && bar % 2 == 1)
                        pitch = tonic + (character % 3 == 0 ? 1 : 6);
                }
                else // DRIVING: keep the 16th grid and a recognisable octave cell.
                {
                    if ((i + motif) % 5 == 2) pitch += 12;
                    else if ((i + motif) % 5 == 4) pitch += fifth;
                }
                pitch = FoldPitch(pitch, tonic + (style == 0 ? 3 : 0), 57, 82);
                int length = style == 1 && i == 0 && character == 4 ? 2 : 1;
                int nextStep = i + 1 < steps.Length ? steps[i + 1] : 16;
                length = Math.Min(length, Math.Max(1, nextStep - step));
                int velocity = style == 1 ? 85 :
                               style == 2 ? (step % 4 == 0 ? 111 : 94) :
                                            (step % 4 == 0 ? 106 : 88);
                if (style == 1 && i == steps.Length - 1) velocity += 7;
                AddMidi(c, bar, step, length, pitch, velocity, r);
            }
        }

        private static void ComposeTechnoStabs(ClipContentData c, int style, int bar,
                                               int bars, int key,
                                               ZPunkChordLibrary.Chord chord,
                                               int rhythm, int character, Random r)
        {
            int root = FoldPitch(60 + key + chord.RootSemitones, 64, 54, 72);
            // Industrial stabs are open-power intervals with occasional
            // tritone grit; other styles still use their selected chords.
            int color = style == 2 ? (character % 4 == 0 ? 6 : 7) : chord.ToneNearest(7, 7);
            int[] steps = style == 1 ? (rhythm % 2 == 0 ? new[] { 3 } : new[] { 11 }) :
                          style == 2 ? (rhythm % 2 == 0 ? new[] { 3, 10, 15 } : new[] { 2, 7, 13 }) :
                                       (rhythm % 2 == 0 ? new[] { 2, 6, 14 } : new[] { 3, 10, 14 });
            for (int i = 0; i < steps.Length; i++)
            {
                int step = steps[i];
                int untilNext = (i + 1 < steps.Length ? steps[i + 1] : 16) - step;
                // Even hard techno needs a few eighth/quarter-note holds among
                // its punches. MINIMAL leaves especially long spaces between hits.
                int desired = style == 1 ? ((bar + rhythm + character) % 3 == 0 ? 10 : 6) :
                              style == 2 ? ((i + character) % 3 == 0 ? 4 : 2) :
                                           ((bar + i + character) % 3 == 0 ? 6 : 3);
                int length = Math.Max(1, Math.Min(untilNext, desired));
                int velocity = style == 1 ? 74 : style == 2 ? 106 : 91;
                string group = GeneratedChordGroup(bar, step);
                AddMidi(c, bar, step, length, root, velocity, r, group);
                if (style != 1 || character % 2 == 0)
                    AddMidi(c, bar, step, length,
                            FoldPitch(root + color, root + 7, 55, 82), velocity - 7, r, group);
            }
        }

        private static void ComposeTechnoTexture(ClipContentData c, int style, int bar,
                                                 int bars, int key,
                                                 ZPunkChordLibrary.Chord[] chords,
                                                 int character, Random r)
        {
            var chord = chords[bar];
            bool previousHeld = style == 1 && bar % 2 == 1 &&
                                SameChord(chords[bar - 1], chord);
            if (previousHeld) return;
            bool hold = style == 1 && bar + 1 < bars && bar % 2 == 0 &&
                        SameChord(chord, chords[bar + 1]);
            int root = FoldPitch(48 + key + chord.RootSemitones, 55, 49, 65);
            int fifth = chord.ToneNearest(7, 7);
            int length = hold ? 32 : style == 2 ? 12 : 16;
            int velocity = style == 1 ? 54 : style == 2 ? 65 : 61;
            string group = GeneratedChordGroup(bar, 0);
            AddMidi(c, bar, 0, length, root, velocity, r, group);
            // Open fifths and octaves support hard sequencers without competing
            // with them; the PADS track must not turn into a bright chord solo.
            AddMidi(c, bar, 0, length, FoldPitch(root + fifth, 64, 56, 78),
                    velocity - 8, r, group);
            if (style == 2 && character % 3 == 0)
            {
                int third = chord.ToneNearest(3, 4);
                AddMidi(c, bar, 10, 5, FoldPitch(root + third + 12, 68, 60, 80),
                        43, r);
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
                    pitch = n.pitch, velocity = n.velocity, chordGroupId = n.chordGroupId });
            return copy;
        }

        // Older generated chord clips predate chordGroupId. Only simultaneous
        // voices from a known generated harmony clip are eligible for backfill.
        // The editor calls this on entering CHORD mode, after obtaining a writable
        // clip, so existing projects gain group editing without regeneration.
        public static bool HasUngroupedChordAttacks(ClipContentData clip)
        {
            if (clip == null || clip.kind != ClipKind.Midi || clip.notes == null ||
                clip.notes.Count < 2 || string.IsNullOrEmpty(clip.generatedTag)) return false;
            var first = new HashSet<long>();
            foreach (var note in clip.notes)
                if (note != null && string.IsNullOrEmpty(note.chordGroupId) &&
                    !first.Add(note.startTick)) return true;
            return false;
        }

        public static void GroupUngroupedChordAttacks(ClipContentData clip)
        {
            if (clip == null || clip.notes == null) return;
            var byStart = new Dictionary<long, List<NoteEvent>>();
            foreach (var note in clip.notes)
            {
                if (note == null || !string.IsNullOrEmpty(note.chordGroupId)) continue;
                List<NoteEvent> members;
                if (!byStart.TryGetValue(note.startTick, out members))
                {
                    members = new List<NoteEvent>();
                    byStart.Add(note.startTick, members);
                }
                members.Add(note);
            }
            foreach (var pair in byStart)
            {
                if (pair.Value.Count < 2) continue;
                // Tick-based IDs cannot collide with the bar/step-based IDs from
                // new generation, and remain stable across save and restore.
                string group = "legacy-generated-chord-" + pair.Key;
                foreach (var note in pair.Value) note.chordGroupId = group;
            }
        }

        private static bool SameChord(ZPunkChordLibrary.Chord left, ZPunkChordLibrary.Chord right)
        {
            if (left.RootSemitones != right.RootSemitones ||
                left.Intervals.Length != right.Intervals.Length) return false;
            for (int i = 0; i < left.Intervals.Length; i++)
                if (left.Intervals[i] != right.Intervals[i]) return false;
            return true;
        }

        // Strong melodic beats target exact chord classes (including chromatic borrowed
        // tones), while the original genre-specific rhythmic contour stays unchanged.
        private static int[] ChordReferencePitches(ZPunkChordLibrary.Chord chord, int key)
        {
            var notes = new int[chord.Intervals.Length];
            for (int i = 0; i < notes.Length; i++)
                notes[i] = 60 + key + chord.RootSemitones + chord.Intervals[i];
            return notes;
        }

        private static void GenerateMidiCandidate(ClipContentData clip, TrackKind kind, int g, int s,
                                                  int bars, int key, bool minor,
                                                  int[] harmony, ZPunkChordLibrary.Chord[] chordPlan,
                                                  bool synthChords, int[][] harmonyVoices,
                                                  int tempoBand, int groove, Random r)
        {
            int motif = r.Next(0, 16);
            int rhythm = r.Next(0, 4);
            int character = r.Next(0, 5);
            // Preserve Techno's proven independent dedicated sequencers.
            if (g != 2)
                rhythm = kind == TrackKind.Bass ? groove :
                         kind == TrackKind.Synth ? Mod(groove + 2, 4) : groove;
            int[] previousVoicing = null;
            int previousMelodyPitch = 68 + Mod(key, 5);
            // Techno gets independent per-style composers. In particular, don't
            // turn DRIVING into a sparse half-note pattern while adjusting MINIMAL.
            if (g == 2)
            {
                ComposeTechnoCandidate(clip, kind, s, bars, key, chordPlan,
                                      synthChords, tempoBand, motif, rhythm,
                                      character, r);
                return;
            }
            for (int bar = 0; bar < bars; bar++)
            {
                int degree = harmony[bar];
                int nextDegree = harmony[(bar + 1) % bars]; // Actual loop destination.
                var chord = chordPlan[bar];
                var nextChord = chordPlan[(bar + 1) % bars];
                if (kind == TrackKind.Bass)
                    ComposeBass(clip, g, s, bar, bars, key, minor, degree, nextDegree,
                                chord, nextChord, motif, rhythm, character, tempoBand, r);
                else if (kind == TrackKind.Pads || (kind == TrackKind.Synth && synthChords))
                {
                    bool pads = kind == TrackKind.Pads;
                    // Skip an odd bar only when the previous bar REALLY contains a
                    // two-bar held pad; otherwise the skipped bar becomes silent.
                    if (pads && bar > 0 && bar % 2 == 1 && harmony[bar - 1] == degree &&
                        SameChord(chordPlan[bar - 1], chord) && ShouldHoldPad(g, s, character))
                        continue;
                    int[] fullVoicing = ComposeVoicing(chord, key, pads, character,
                                                      previousVoicing);
                    int[] voicing = pads ? ShapePadVoicing(fullVoicing, g, s) : fullVoicing;
                    bool sustainedAcrossNext = bar + 1 < bars &&
                        SameChord(chord, chordPlan[bar + 1]);
                    ComposeChordRhythm(clip, g, s, bar, bars, degree, harmony, pads,
                                       voicing, rhythm, character, tempoBand, r,
                                       sustainedAcrossNext);
                    previousVoicing = fullVoicing;
                }
                else
                    ComposeMelody(clip, g, s, bar, bars, key, minor, degree, motif, rhythm,
                                  character, harmonyVoices != null && bar < harmonyVoices.Length &&
                                  harmonyVoices[bar] != null && harmonyVoices[bar].Length > 0
                                  ? harmonyVoices[bar] : ChordReferencePitches(chord, key),
                                  ref previousMelodyPitch, tempoBand, r);
            }
            // Never force a tick-zero note into intentionally offbeat chords/stabs.
            // Clip placement is already aligned by the arrangement's tick clock.
        }

        // These IDs are unique per attack within a generated clip and survive
        // history, save/load, and clip duplication. Chord Draw already uses the
        // same NoteEvent.chordGroupId contract for selection/move/resize.
        private static string GeneratedChordGroup(int bar, int step)
        {
            return "generated-chord-" + bar + "-" + step;
        }

        private static void AddMidi(ClipContentData clip, int bar, int step, int stepsLong,
                                    int pitch, int velocity, Random r, string chordGroupId = null)
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
                pitch = pitch, velocity = Clamp(velocity + r.Next(-4, 5), 25, 127),
                chordGroupId = chordGroupId });
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

        private static int BassRootFromSemitone(int semitone)
        {
            int pitch = 36 + semitone;
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
                if (s == 1) return new[] { new[] { 2, 10, 14 }, new[] { 2, 8, 14 },
                    new[] { 2, 10 }, new[] { 3, 10, 14 } };
                if (s == 2) return new[] { new[] { 3, 6, 11, 14 }, new[] { 2, 8, 12, 15 },
                    new[] { 3, 7, 11, 15 }, new[] { 2, 6, 9, 14 } };
                if (s == 3) return new[] { new[] { 0, 3, 4, 7, 10, 14 }, new[] { 0, 2, 4, 8, 11, 14 }, new[] { 0, 3, 6, 8, 12, 15 }, new[] { 0, 4, 6, 10, 12, 14 } };
                return new[] { new[] { 0, 2, 6, 10, 14 }, new[] { 0, 3, 6, 11, 14 }, new[] { 0, 2, 7, 10, 15 }, new[] { 0, 4, 6, 10, 14 } };
            }
            if (g == 2) return s == 1
                ? new[] { new[] { 0, 3, 7, 11 }, new[] { 0, 5, 9, 13 }, new[] { 0, 2, 7, 11, 15 }, new[] { 0, 3, 8, 13 } }
                : s == 2
                ? new[] { new[] { 0, 1, 3, 6, 7, 9, 11, 14 }, new[] { 0, 2, 3, 5, 8, 10, 13, 15 }, new[] { 0, 3, 4, 7, 9, 11, 14, 15 }, new[] { 0, 1, 4, 6, 9, 10, 13, 15 } }
                : new[] { new[] { 0, 2, 3, 5, 7, 8, 10, 13, 15 }, new[] { 0, 1, 3, 4, 7, 9, 11, 14 }, new[] { 0, 3, 4, 6, 7, 10, 12, 14, 15 }, new[] { 0, 2, 4, 5, 8, 9, 11, 13, 15 } };
            if (g == 3) return s == 2
                ? new[] { new[] { 0, 8, 13 }, new[] { 0, 7, 14 }, new[] { 0, 10, 14 }, new[] { 0, 6, 12 } }
                : s == 1
                ? new[] { new[] { 0, 3, 6, 9, 12, 15 }, new[] { 0, 2, 5, 8, 11, 14 }, new[] { 0, 3, 7, 10, 13 }, new[] { 0, 2, 6, 9, 13, 15 } }
                : new[] { new[] { 0, 3, 7, 10, 14 }, new[] { 0, 2, 7, 11, 14 }, new[] { 0, 5, 8, 11, 15 }, new[] { 0, 3, 6, 10, 13 } };
            if (g == 4) return s == 1
                // Short clav-style answers between the main bass attacks.
                ? new[] { new[] { 5, 12 }, new[] { 4, 11 },
                          new[] { 5, 9, 13 }, new[] { 4, 12, 15 } }
                : s == 2
                ? new[] { new[] { 0, 2, 5, 7, 10, 13 }, new[] { 0, 3, 6, 9, 12, 15 }, new[] { 0, 2, 6, 10, 14 }, new[] { 0, 3, 7, 11, 15 } }
                : new[] { new[] { 0, 3, 6, 10, 14 }, new[] { 0, 2, 7, 10, 13 }, new[] { 0, 3, 8, 11, 14 }, new[] { 0, 5, 7, 10, 15 } };
            return s == 0
                // Filtered chops, different accents from the root/octave bass.
                ? new[] { new[] { 2, 7, 10, 14 }, new[] { 2, 6, 10 },
                          new[] { 3, 7, 11, 14 }, new[] { 2, 10, 14 } }
                : s == 1
                ? new[] { new[] { 0, 2, 6, 10, 14 }, new[] { 0, 4, 7, 10, 13 }, new[] { 0, 3, 6, 9, 14 }, new[] { 0, 2, 7, 11, 15 } }
                : new[] { new[] { 0, 3, 5, 10, 13, 15 }, new[] { 0, 2, 6, 9, 12, 14 }, new[] { 0, 3, 7, 10, 14 }, new[] { 0, 5, 8, 11, 15 } };
        }

        private static int[][] MelodicContours(int g, int s)
        {
            // Short motifs are purposeful. Different rhythmic identities below
            // stop 'Deep' and 'Jackin', or 'Filter' and 'Touch', sharing a lead.
            if (g == 0 && s == 1) return new[] { new[] { 0, 1, 0, -1, 0 },
                new[] { 0, -1, 1, 0 }, new[] { 0, 2, 1, 0 } };
            if (g == 0 && s == 2) return new[] { new[] { 0, 3, 0, -1, 2 },
                new[] { 0, 2, -1, 3, 0 }, new[] { 0, -1, 2, 0 } };
            if (g == 1 && s == 1) return new[] { new[] { 0, 1, 2, 1, 0 },
                new[] { 0, 2, 1, 0 }, new[] { 0, 1, 0, -1 } };
            if (g == 1 && s == 2) return new[] { new[] { 0, 2, 0, -1, 0 },
                new[] { 0, 3, 1, 0 }, new[] { 0, -1, 2, 0 } };
            if (g == 1 && s == 3) return new[] { new[] { 0, 2, 4, 2, 0, 3 },
                new[] { 0, 4, 3, 2, 0 }, new[] { 0, 2, 5, 4, 2, 0 } };
            if (g == 3 && s == 0) return new[] { new[] { 0, 1, 0, -1 },
                new[] { 0, 2, 0, 1 }, new[] { 0, -1, 1, 0 } };
            if (g == 3 && s == 1) return new[] { new[] { 0, 2, 1, 0 },
                new[] { 0, 3, 0, -1 }, new[] { 0, 1, 3, 0 } };
            if (g == 3 && s == 2) return new[] { new[] { 0, 0, -1, 0 },
                new[] { 0, 2, 0 }, new[] { 0, -1, 0 } };
            if (g == 4 && s == 1) return new[] { new[] { 0, 0, 1, 0 },
                new[] { 0, -1, 0, 1 }, new[] { 0, 1, 0 } };
            if (g == 4 && s == 2) return new[] { new[] { 0, 4, 2, 0, 3 },
                new[] { 0, 2, 4, 0 }, new[] { 0, 4, 0, 2 } };
            if (g == 5 && s == 0) return new[] { new[] { 0, 0, 2, 0 },
                new[] { 0, 2, 0, 0 }, new[] { 0, 0, 1, 0 } };
            if (g == 5 && s == 1) return new[] { new[] { 0, 2, 4, 2, 0 },
                new[] { 0, 4, 2, 3, 0 }, new[] { 0, 2, 3, 4, 0 } };
            if (g == 5 && s == 2) return new[] { new[] { 0, 4, 2, 1, 0 },
                new[] { 0, 2, 4, 2, 0 }, new[] { 0, 4, 3, 2, 0 } };
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
                                          int character, int[] externalChord, ref int previous, int tempoBand, Random r)
        {
            int[][] grids = MelodyRhythms(g, s);
            // A-A'-B-A'' form in four bars; call/reply on short phrases.
            int[] steps = MotifRhythm(grids, g, s, bar, bars, rhythm, character,
                                      tempoBand, false);
            int[][] contours = MelodicContours(g, s);
            int[] shape = contours[Mod(motif / 4 + s, contours.Length)];
            // Previous note carries across bars, avoiding arbitrary octave resets.
            for (int i = 0; i < steps.Length; i++)
            {
                int step = steps[i];
                if (bar == 3 && i == steps.Length - 1 && step == 15 && character % 2 == 0) step = 14;
                // A phrase answers rather than copying its call note-for-note.
                int idx = Mod(i + (bar == 2 ? 1 : 0) + motif % 3, shape.Length);
                int offset = shape[idx];
                if (i == 0) offset = (motif % 2 == 0 ? 0 : 2);
                // Third bar develops the established motif rather than replaying it verbatim.
                if (bar == 2 && i > 0 && i < steps.Length - 1 && (i + character) % 3 == 0)
                    offset += (motif % 2 == 0 ? 1 : -1);
                if (i == steps.Length - 1) offset = (bar == 2 ? 2 : 0);
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
                if (i == steps.Length - 1 && bar == bars - 1 && g != 2) length = Math.Max(length, 2);
                length = ShapeLength(length + Dna(g, s).SynthHold, g, s, tempoBand);
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
                ? new[] { new[] { 0, 3, 7, 11, 14 }, new[] { 0, 5, 9, 13 }, new[] { 0, 2, 7, 10, 15 }, new[] { 0, 3, 8, 11 } }
                : s == 2
                ? new[] { new[] { 0, 1, 3, 6, 7, 9, 11, 14, 15 }, new[] { 0, 2, 3, 5, 8, 10, 13, 15 }, new[] { 0, 3, 4, 7, 9, 11, 14 }, new[] { 0, 1, 4, 6, 9, 10, 13, 15 } }
                : new[] { new[] { 0, 2, 3, 5, 6, 8, 10, 11, 13, 15 }, new[] { 0, 1, 3, 4, 7, 8, 10, 11, 14 }, new[] { 0, 3, 4, 6, 7, 9, 12, 14, 15 }, new[] { 0, 2, 4, 5, 8, 9, 11, 13, 15 } };
            if (g == 3) return s == 2
                ? new[] { new[] { 0, 8, 13 }, new[] { 0, 7, 12 },
                          new[] { 0, 10 }, new[] { 0, 8, 14 } }
                : s == 1
                ? new[] { new[] { 0, 6, 12 }, new[] { 0, 7, 13 },
                          new[] { 0, 8, 14 }, new[] { 0, 5, 11 } }
                : new[] { new[] { 0, 7, 12 }, new[] { 0, 6, 11, 14 },
                          new[] { 0, 8, 13 }, new[] { 0, 5, 10 } };
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
                                        ZPunkChordLibrary.Chord chord,
                                        ZPunkChordLibrary.Chord nextChord,
                                        int motif, int rhythm, int character, int tempoBand, Random r)
        {
            int root = BassRootFromSemitone(key + chord.RootSemitones);
            int nextRoot = BassRootFromSemitone(key + nextChord.RootSemitones);
            // Keep octave jumps, kick-pocket syncopation and note lengths exactly as
            // before; only the harmonic targets follow the richer progression.
            int fifth = chord.ToneNearest(7, 7);
            int third = chord.ToneNearest(4, 3);
            int seventh = chord.ToneNearest(10, 10);
            int[][] grids = BassRhythms(g, s);
            // A / answer / development / reprise, retaining the original motifs.
            int[] steps = MotifRhythm(grids, g, s, bar, bars, rhythm, character,
                                      tempoBand, true);
            for (int i = 0; i < steps.Length; i++)
            {
                int step = steps[i];
                int role = Mod(i + motif + (bar == 2 ? 1 : 0), 7);
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
                        // A fast break needs space for sub-bass; not a rival
                        // sixteenth-note melody underneath every snare roll.
                        pitch = s == 2 ? (i == steps.Length - 1 && motif % 3 == 0 ?
                                root + fifth : root) :
                                s == 1 ? (i == steps.Length - 1 && motif % 3 == 1 ?
                                root + 12 : root) :
                                role == 1 || role == 5 ? root + 12 : root;
                    else if (g == 5) // Disco octaves and concise French-touch hooks.
                        pitch = role == 1 || role == 4 ? root + 12 :
                                (role == 3 && s != 0) ? root + fifth : root;
                    else // HOUSE, with distinct Deep / Jackin / 90s vocabularies.
                        pitch = s == 1 ? (role == 4 && i == steps.Length - 1 ?
                                root + fifth : root) :
                                s == 2 ? (role == 1 || role == 4 ? root + 12 :
                                role == 6 ? root + third : root) :
                                role == 1 || role == 4 ? root + 12 :
                                role == 2 || role == 5 ? root + fifth : root;
                    if (g == 4 && role == 6 && (bar % 2 == 1)) pitch = root + seventh - 12;
                }
                // One deliberately placed pickup, not a random chromatic run.
                if (i == steps.Length - 1 && step >= 12 && (bar == bars - 1 || bar % 2 == 1) &&
                    (motif + character + bar) % 3 != 0 &&
                    nextChord.RootSemitones != chord.RootSemitones)
                    pitch = BassApproach(root, nextRoot);
                pitch = FoldPitch(pitch, pitch >= 48 ? 51 : 42, 35, 60);
                int gap = i + 1 < steps.Length ? steps[i + 1] - step : 16 - step;
                int length = g == 2 ? (s == 1 ? 2 : 1 + (i % 4 == 0 ? 1 : 0)) :
                             g == 0 && s == 1 ? 4 :
                             g == 1 && s == 1 ? 3 :
                             g == 3 && s == 2 ? 4 :
                             g == 4 ? 1 + ((i + character) % 3) :
                             g == 5 ? (s == 0 ? 2 : 1 + ((i + character) % 2)) : 1 + (step % 3);
                // Sustain style distinguishes sub-oriented Breaks and warm Deep
                // House from short, percussive Jackin or Disco bass notes.
                length += Dna(g, s).BassHold;
                if (g == 3 && s == 1) length = Math.Max(length, 5);
                if (g == 3 && s == 2) length = Math.Max(length, 6);
                length = ShapeLength(length, g, s, tempoBand);
                // Leave a release gap on the longer subs where possible.
                int maximum = (g == 3 && gap > 2) ? gap - 1 : gap;
                length = Math.Max(1, Math.Min(length, Math.Max(1, maximum)));
                int vel = i == 0 ? 112 : step % 4 == 0 ? 101 :
                          step % 2 == 1 ? 87 : 95;
                AddMidi(c, bar, step, length, pitch, vel, r);
            }
        }

        // Whole-voicing search: avoid crossing, congestion and large jumps between
        // chord changes. Select from valid inversions, not independent voice indices.
        private static int[] ComposeVoicing(ZPunkChordLibrary.Chord chord, int key,
                                            bool pads, int character, int[] previous)
        {
            int[] offsets = chord.Intervals;
            int root = 48 + key + chord.RootSemitones;
            int[][] choices = new int[offsets.Length][];
            int low = pads ? 51 : 54, high = pads ? 81 : 83;
            for (int i = 0; i < offsets.Length; i++)
            {
                var values = new List<int>(4);
                int fundamental = root + offsets[i];
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
            // Preserve all chord identities when a narrow mobile register is requested.
            int[] fallback = new int[offsets.Length];
            for (int i = 0; i < offsets.Length; i++)
                fallback[i] = FoldPitch(root + offsets[i], 60 + 4 * i, low, high);
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
                {
                    // Nearest-voice matching handles a chord gaining/losing an
                    // extension without forcing an octave leap in another voice.
                    int nearest = int.MaxValue;
                    foreach (int oldNote in previous)
                        nearest = Math.Min(nearest, Math.Abs(sorted[i] - oldNote));
                    cost += nearest * 2.4;
                    // Also keep the highest chord voice moving like a melody.
                    if (i == sorted.Length - 1)
                        cost += Math.Abs(sorted[i] - previous[previous.Length - 1]) * 1.3;
                }
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
                if (s == 1) return choice % 2 == 0 ? new[] { 2, 10 } : new[] { 2, 12 };
                if (s == 2) return choice % 2 == 0 ? new[] { 2, 7, 10, 14 } : new[] { 3, 6, 11, 14 };
                if (s == 3) return bar % 2 == 0 ? new[] { 0, 6, 10, 14 } : new[] { 2, 6, 11, 14 };
                return choice % 2 == 0 ? new[] { 2, 6, 10, 14 } : new[] { 2, 10, 14 };
            }
            if (g == 2) return s == 1 ? new[] { 0, 12 } :
                choice % 2 == 0 ? new[] { 2, 6, 10, 14 } : new[] { 0, 7, 11, 14 };
            if (g == 3) return s == 2 ? new[] { 0, 8 } :
                s == 1 ? (choice % 2 == 0 ? new[] { 2, 10 } : new[] { 3, 11 }) :
                choice % 2 == 0 ? new[] { 0, 7, 11 } : new[] { 0, 3, 9, 14 };
            if (g == 4) return s == 1 ? new[] { 3, 11 } :
                choice % 2 == 0 ? new[] { 0, 6, 10, 14 } : new[] { 0, 3, 7, 11, 14 };
            if (s == 0) return choice % 2 == 0 ? new[] { 2, 6, 10, 14 } : new[] { 2, 10, 14 };
            if (s == 1) return choice % 2 == 0 ? new[] { 2, 6, 10, 14 } : new[] { 0, 6, 11, 14 };
            return choice % 2 == 0 ? new[] { 0, 3, 7, 10, 14 } : new[] { 2, 6, 10, 13 };
        }

        private static void ComposeChordRhythm(ClipContentData c, int g, int s, int bar,
                                                int bars, int degree, int[] harmony, bool pads,
                                                int[] notes, int rhythm, int character, int tempoBand,
                                                Random r, bool sameNextChord)
        {
            int[] hits = ChordRhythms(g, s, pads, rhythm + character, bar);
            if (!pads)
            {
                if (g == 1 && tempoBand < 0 && s != 1)
                    hits = ShapeChordStabs(hits, s == 3 ? 15 : 11);
                hits = ShapeChordPhrase(hits, bar, character);
            }
            // Keep PADS separate from the busy rhythmic SYNTH-stab vocabulary.
            bool longHold = pads && ShouldHoldPad(g, s, character) &&
                            bar + 1 < bars && harmony[bar + 1] == degree &&
                            sameNextChord && bar % 2 == 0;
            for (int hit = 0; hit < hits.Length; hit++)
            {
                int step = hits[hit];
                if (longHold && hit > 0) break;
                int duration;
                if (pads) duration = longHold ? 32 :
                    hits.Length == 1 ? 16 - step :
                    (hit + 1 < hits.Length ? hits[hit + 1] - step - 1 : 16 - step);
                else
                {
                    int untilNext = hit + 1 < hits.Length ? hits[hit + 1] - step : 16 - step;
                    duration = NaturalChordLength(g, s, bar, hit, rhythm, character,
                                                  tempoBand, untilNext, r);
                }
                duration = Math.Max(1, duration);
                int vel = pads ? 66 : g == 1 && s == 3 ? 103 : g == 4 ? 94 : 84;
                string group = GeneratedChordGroup(bar, step);
                for (int i = 0; i < notes.Length; i++)
                    AddMidi(c, bar, step, duration, notes[i], vel - i * 3 - hit % 2 * 4, r, group);
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

        // Give a multi-bar phrase a small call/response shape. Preserve the
        // first-bar motif, thin the second-bar answer, open up bar three, then
        // return to the original figure in bar four. Never change sparse pads.
        private static int[] ShapeChordPhrase(int[] original, int bar, int character)
        {
            int phraseBar = bar % 4;
            if (original.Length < 3 || phraseBar == 0 || phraseBar == 3) return original;
            var shaped = new List<int>(original);
            if (phraseBar == 1)
                shaped.RemoveAt((character & 1) == 0 ? shaped.Count - 1 : 1);
            else if (shaped.Count >= 4)
                shaped.RemoveAt(1);
            return shaped.ToArray();
        }

        // Chord articulations use musical divisions rather than the generic
        // synth-lead's 1/16-note ShapeLength cap. Sparse styles get genuine
        // 1/4- and 1/2-bar sustains; busy styles retain occasional tight stabs.
        // Every voice in an attack receives the same duration, with no overlap
        // into the next attack or beyond its bar. Offbeats remain on the grid.
        private static int NaturalChordLength(int g, int s, int bar, int attack,
                                               int rhythm, int character, int tempoBand,
                                               int untilNext, Random r)
        {
            int feel = Mod(rhythm + character * 3 + bar * 2 + attack, 5);
            int desired;
            if (g == 1 && s == 1) // Deep house: rounded quarter/half-beat harmonies.
                desired = new[] { 8, 6, 4, 8, 6 }[feel];
            else if (g == 1 && s == 3) // Rave piano: a few punches, some held answers.
                desired = new[] { 2, 4, 3, 6, 4 }[feel];
            else if (g == 1 && s == 2) // Jackin: short rhythmic responses.
                desired = new[] { 2, 3, 4, 2, 6 }[feel];
            else if ((g == 0 && s == 1) || (g == 3 && s == 2) || (g == 4 && s == 1))
                desired = new[] { 8, 6, 4, 10, 6 }[feel];
            else if (g == 3 && s == 1) // Jungle: leave room underneath the break.
                desired = new[] { 4, 6, 3, 8, 4 }[feel];
            else
                desired = new[] { 4, 2, 6, 3, 4 }[feel];

            // Adjacent attacks retain a little separation; held notes can run
            // into the next beat when the phrase calls for a legato response.
            if (tempoBand >= 1 && desired < 8) desired++;
            if (tempoBand <= -1 && (g == 1 || g == 4 || g == 5) && desired > 2)
                desired--;
            int maximum = Math.Max(1, untilNext);
            int length = Math.Min(desired, maximum);
            if (length == maximum && maximum > 3 && (feel + r.Next(3)) % 3 != 0)
                length--; // rhythmic breath before the next chord
            return Math.Max(1, length);
        }

        private static int[] ShapeChordStabs(int[] original, int extra)
        {
            var hits = new List<int>(original);
            InsertStep(hits, extra, 1);
            hits.Sort();
            return hits.ToArray();
        }

        // Cheap deterministic ranking, with density, harmonic, rhythmic and register
        // checks. Genre-specific preferences prevent over-penalizing minimal techno.
        private static double ScoreCandidate(ClipContentData c, TrackKind kind, int g, int s,
                                              bool minor, int key, int[] harmony,
                                              ZPunkChordLibrary.Chord[] chordPlan, bool chords,
                                              int[][] externalVoices, int tempoBand,
                                              int groove)
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
                bool chordTone = chord != null
                    ? IsChordTone(n.pitch, key, harmony[bar], minor, chord)
                    : chordPlan[bar].Contains(n.pitch, key);
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
            foreach (int count in barNotes)
                if (count == 0)
                    score -= kind == TrackKind.Pads &&
                             ((g == 2 && (s == 1 || s == 2)) ||
                              (g == 3 && s == 2) || (g == 4 && s == 1)) ? 2 : 18;
            int target = TargetAttacksPerBar(kind, g, s, chords, tempoBand);
            double eventsPerBar = starts.Count / (double)harmony.Length;
            score -= Math.Abs(eventsPerBar - target) * (kind == TrackKind.Pads ? 1.5 : 3.5);
            if (!harmonic)
            {
                double allowed = g == 2 ? (s == 1 ? .94 : s == 2 ? .80 : .76) : .43;
                if (kind == TrackKind.Bass &&
                    ((g == 3 && s >= 1) || (g == 1 && s == 1) ||
                     (g == 5 && s == 0) || (g == 4 && s == 1)))
                    allowed = .90; // sub-bass and filtered loops need tonic repetition.
                if (kind == TrackKind.Synth &&
                    ((g == 5 && s == 0) || (g == 4 && s == 1)))
                    allowed = .66; // hook repetition is intentional in these styles.
                score -= Math.Max(0, repeatCount - sorted.Count * allowed) * 4;
                score -= jumps * (g == 3 ? 1.5 : 3);
                // Repeated low notes are a feature of dubby sub-bass, filtered
                // loops and tight funk pockets, not an automatic defect.
                bool loopVamp = (g == 3 && kind == TrackKind.Bass) ||
                                (g == 5 && s == 0) ||
                                (g == 4 && s == 1) || (g == 1 && s == 1);
                if (pitches.Count <= 2 && g != 2 && sorted.Count > 5 && !loopVamp) score -= 9;
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
            if (g == 2 && (kind == TrackKind.Bass || (kind == TrackKind.Synth && !chords)))
            {
                // Techno can repeat pitches, but attacks should sound like a
                // sixteenth sequencer rather than sustained half-note pads.
                foreach (var note in sorted)
                    score += note.durationTicks <= sixteenth * 2 ? .25 : -1.2;
            }
            if (harmony.Length == 4 && !harmonic)
                score += ScoreFourBarPhrase(starts, sixteenth);
            score += ScoreStyleDna(sorted, kind, g, s, harmony, chordPlan, chords,
                                   sixteenth, groove);
            if (kind == TrackKind.Bass && sorted[0].startTick == 0) score += 1;
            return score;
        }

        private static double ScoreStyleDna(List<NoteEvent> sorted, TrackKind kind,
                                            int g, int s, int[] harmony,
                                            ZPunkChordLibrary.Chord[] chords,
                                            bool synthChords, long sixteenth,
                                            int groove)
        {
            StyleDna dna = Dna(g, s);
            if (kind == TrackKind.Pads) return 0; // harmony has its own voicing test
            // Group each bar's distinct pitches by their actual grid attack, not
            // velocity/humanization. Multiple chord voices count as one onset.
            var signatures = new List<int>[harmony.Length];
            var seen = new HashSet<long>();
            double articulation = 0;
            int count = 0;
            foreach (var n in sorted)
            {
                int bar = Clamp((int)(n.startTick / SongData.TicksPerBar4), 0,
                                harmony.Length - 1);
                int step = (int)((n.startTick / sixteenth) % 16);
                if (signatures[bar] == null) signatures[bar] = new List<int>();
                int root = chords[bar].RootSemitones;
                // Relative pitch class makes the comparison independent of key
                // and independent of the selected progression's transpositions.
                int signature = step * 16 + Mod(n.pitch - root, 12);
                if (!synthChords && kind != TrackKind.Pads)
                {
                    signatures[bar].Add(signature);
                    if (kind == TrackKind.Bass && (g == 3 || (g == 1 && s == 1)))
                        articulation += n.durationTicks >= sixteenth * 3 ? .4 : -.4;
                    if (kind == TrackKind.Bass && ((g == 1 && s == 2) ||
                        (g == 4 && s == 2)))
                        articulation += n.durationTicks <= sixteenth * 2 ? .2 : -.3;
                    count++;
                }
                else if (seen.Add(n.startTick)) signatures[bar].Add(step * 16);
            }
            double score = count > 0 ? Math.Max(-4.0, Math.Min(4.0, articulation)) : 0;
            // Softly prefer rhythmic answers to the BASS fingerprint, without
            // banning intentional unisons or changing any existing drum events.
            if (kind == TrackKind.Synth && g != 2 &&
                ((g == 1 && s == 2) || (g == 4 && s == 1) ||
                 (g == 5 && s == 0)))
            {
                int[][] bassGrids = BassRhythms(g, s);
                int[] bassAnchor = bassGrids[Mod(groove, bassGrids.Length)];
                int matches = 0, attacks = 0;
                var uniqueSteps = new HashSet<int>();
                foreach (var note in sorted)
                {
                    int step = (int)((note.startTick / sixteenth) % 16);
                    int bar = (int)(note.startTick / SongData.TicksPerBar4);
                    if (!uniqueSteps.Add(bar * 16 + step)) continue;
                    attacks++;
                    foreach (int anchor in bassAnchor)
                        if (step == anchor) { matches++; break; }
                }
                if (attacks > 0) score -= 2.5 * matches / attacks;
            }
            if (signatures.Length > 1 && signatures[0] != null)
            {
                // The reprise is more important for hypnotic and loop-driven
                // styles, but a fully static four-bar copy gets no extra reward.
                int bar = signatures.Length - 1;
                if (signatures[bar] != null)
                {
                    int shared = 0;
                    foreach (int identity in signatures[bar])
                        if (signatures[0].Contains(identity)) shared++;
                    double overlap = shared / (double)Math.Max(1, Math.Max(
                        signatures[0].Count, signatures[bar].Count));
                    score += overlap * (2.0 + dna.MotifWeight);
                }
                if (signatures.Length >= 3 && signatures[2] != null)
                {
                    int newNotes = 0;
                    foreach (int identity in signatures[2])
                        if (!signatures[0].Contains(identity)) newNotes++;
                    // One or two fresh third-bar events creates development.
                    score += Math.Min(2, newNotes) * .35;
                }
            }
            return score;
        }

        private static int TargetAttacksPerBar(TrackKind kind, int g, int s,
                                                bool chords, int tempoBand)
        {
            if (kind == TrackKind.Pads) return 1;
            if (chords)
                return g == 1 ? (s == 1 ? 2 : s == 3 ? 4 : 4) :
                       g == 2 ? (s == 1 ? 2 : 4) : g == 0 ? (s == 1 ? 2 : 4) :
                       g == 3 ? (s == 2 ? 2 : 3) : g == 4 ? 4 : 4;
            int count = g == 0 ? (s == 1 ? 3 : s == 2 ? 5 : 4) :
                        g == 1 ? (s == 1 ? 4 : s == 2 ? 7 : s == 3 ? 6 : 5) :
                        g == 2 ? (s == 1 ? 4 : s == 2 ? 8 : 9) :
                        g == 3 ? (s == 1 ? 3 : s == 2 ? 2 : 3) :
                        g == 4 ? (s == 1 ? 4 : s == 2 ? 6 : 5) :
                                 (s == 0 ? 5 : s == 1 ? 6 : 6);
            if (kind == TrackKind.Synth && g != 2) count = Math.Max(2, count - 1);
            count = Math.Min(count, kind == TrackKind.Synth ? Dna(g, s).SynthNotes :
                             Dna(g, s).BassNotes);
            bool spare = (g == 0 && s == 1) || (g == 1 && s == 1) ||
                         (g == 2 && s == 1) || (g == 3 && s == 2) ||
                         (g == 4 && s == 1);
            if (tempoBand >= 1 && spare) count = Math.Max(2, count - 1);
            if (tempoBand >= 1 && g == 2 && s != 1) count++;
            if (tempoBand <= -1 && !spare && (g == 0 || g == 1 || g == 4 || g == 5)) count++;
            return count;
        }

        private static double ScoreFourBarPhrase(HashSet<long> starts, long sixteenth)
        {
            // Encourage the fourth bar to recall the original motif, while bar
            // three offers a contrasting response. Small bonus, not a mandate.
            var first = new HashSet<int>();
            var third = new HashSet<int>();
            var fourth = new HashSet<int>();
            foreach (long tick in starts)
            {
                int bar = (int)(tick / SongData.TicksPerBar4);
                int step = (int)((tick / sixteenth) % 16);
                if (bar == 0) first.Add(step);
                else if (bar == 2) third.Add(step);
                else if (bar == 3) fourth.Add(step);
            }
            int echo = 0, contrast = 0;
            foreach (int step in fourth) if (first.Contains(step)) echo++;
            foreach (int step in third) if (!first.Contains(step)) contrast++;
            return Math.Min(2.5, echo * .35 + contrast * .25);
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
                    else if (g == 2) sum += sync ? (s == 1 ? 2.3 : 2.6) :
                                               offbeat ? 1.7 : 1.1;
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
                    if (g == 2) sum += sync ? (s == 1 ? 2.0 : 2.5) :
                                          offbeat ? 1.6 : 1.0;
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
