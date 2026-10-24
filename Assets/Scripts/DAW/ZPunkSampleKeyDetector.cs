using System;
using System.Collections;
using UnityEngine;

namespace ZPunk.DAW
{
    /// <summary>
    /// Offline, bounded-time chroma/key estimate from the ORIGINAL sample PCM.
    /// AudioClip.GetData must run on Unity's main thread; analysis is spread
    /// across frames to keep mobile touch/audio responsive. No microphone,
    /// network, plugins, allocations proportional to song length or playback.
    /// </summary>
    public static class ZPunkSampleKeyDetector
    {
        public struct Result
        {
            public bool found;
            public int tonic;
            public bool minor;
            public float confidence;
            public string status;
            public string Name => found ? NameFor(tonic, minor) : "UNKNOWN";
        }

        public static string NameFor(int key, bool minor)
        {
            string[] names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
            return names[((key % 12) + 12) % 12] + (minor ? " MIN" : " MAJ");
        }

        // Standard tonal profiles, indexed chromatically from the root.
        private static readonly double[] Major = { 6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88 };
        private static readonly double[] Minor = { 6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17 };
        // Minor songs may have a major IV chord (e.g. Cm -> F) because of
        // Dorian/modal mixture. The raised sixth should not automatically
        // cause the entire song to be labeled as the IV major key.
        private static readonly double[] MinorDorian = { 6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 2.55, 4.15, 3.34, 3.17 };
        private static readonly int[] MajorNotes = { 0, 2, 4, 5, 7, 9, 11 };
        private static readonly int[] NaturalMinorNotes = { 0, 2, 3, 5, 7, 8, 10 };
        private static readonly int[] DorianMinorNotes = { 0, 2, 3, 5, 7, 9, 10 };
        private static readonly int[] HarmonicMinorNotes = { 0, 2, 3, 5, 7, 8, 11 };
        // ~0.74 s at 11 kHz provides better pitch resolution for bass notes.
        // Windows span the whole recording; only one reusable PCM buffer is kept.
        private const int FftSize = 8192;
        private const int MaxWindows = 144;
        // A root+third+fifth present together is more useful for separating
        // relative keys than their shared whole-song pitch-class histogram.
        // Section evidence reuses the existing FFT windows; a second pass
        // may spend up to 24 of the 144-window budget on disputed sections.
        private const int KeyCount = 24; // 12 major, then 12 minor
        private const int BaseWindowCap = 120;
        private const int ExtraWindowCap = 24;
        private const int SectionCount = 3;

        // Fixed-size per-section accumulators: memory does not scale with audio length.
        private sealed class SectionEvidence
        {
            public readonly double[] chroma = new double[12];
            public readonly double[] bass = new double[12];
            public readonly double[] openingBass = new double[12];
            public readonly double[] triads = new double[KeyCount];
            public readonly double[] rootedTriads = new double[KeyCount];
            public readonly double[] closingTriads = new double[KeyCount];
            public readonly double[] cadences = new double[KeyCount];
            public int accepted, bassAccepted, openingAccepted, attempted, chordFrames;
            public int previousChord = -1;
            public double previousChordStrength, previousTime = -1;

            // Require repeated coherent pitch information. Sparse, percussion-led
            // sections are deliberately excluded from the decision process.
            public bool Reliable => accepted >= 5 && chordFrames >= 2 &&
                (bassAccepted >= 3 || chordFrames >= 5) &&
                accepted >= Math.Max(3, attempted / 6);
        }

        private static int TopKey(double[] scores, out double margin)
        {
            int best = 0;
            double highest = double.NegativeInfinity, runnerUp = double.NegativeInfinity;
            for (int i = 0; i < KeyCount; i++)
            {
                if (scores[i] > highest)
                {
                    runnerUp = highest;
                    highest = scores[i];
                    best = i;
                }
                else if (scores[i] > runnerUp) runnerUp = scores[i];
            }
            margin = highest - runnerUp;
            return best;
        }

        // Exactly the established full-song scoring formula, reusable on thirds.
        // No fixed bias toward minor or major is introduced.
        private static double[] ScoreEvidence(double[] chroma, double[] bass,
            double[] openingBass, double[] triads, double[] rootedTriads,
            double[] closingTriads, double[] cadences,
            int accepted, int bassAccepted, int openingAccepted)
        {
            double[] result = new double[KeyCount];
            if (accepted < 1) return result;
            for (int key = 0; key < 12; key++)
            {
                for (int mode = 0; mode < 2; mode++)
                {
                    bool minorCandidate = mode == 1;
                    double score = Correlation(chroma, minorCandidate ? Minor : Major, key);
                    if (minorCandidate)
                        score = Math.Max(score, Correlation(chroma, MinorDorian, key) - .025);
                    double scaleFit = ScaleFit(chroma, key, minorCandidate);
                    score += .30 * (scaleFit - .65);
                    double tonicShare = chroma[key] / accepted;
                    score += .11 * (tonicShare - 1.0 / 12.0);
                    int expectedThird = (key + (minorCandidate ? 3 : 4)) % 12;
                    int otherThird = (key + (minorCandidate ? 4 : 3)) % 12;
                    score += .26 * (chroma[expectedThird] - chroma[otherThird]) / accepted;
                    if (bassAccepted >= 3)
                        score += .32 * (bass[key] / bassAccepted - 1.0 / 12.0);
                    if (openingAccepted >= 3)
                        score += .09 * (openingBass[key] / openingAccepted - 1.0 / 12.0);
                    int index = mode * 12 + key;
                    double scaleCompatibility = Math.Max(.30,
                        Math.Min(1.0, (scaleFit - .40) / .38));
                    double harmonicEvidence = 2.0 * triads[index] / accepted;
                    if (bassAccepted >= 3)
                        harmonicEvidence += 4.0 * rootedTriads[index] / bassAccepted;
                    harmonicEvidence += .6 * closingTriads[index] / accepted;
                    harmonicEvidence += 2.0 * cadences[index] / accepted;
                    result[index] = score + scaleCompatibility * harmonicEvidence;
                }
            }
            return result;
        }

        private static double[] ScoreSection(SectionEvidence section)
        {
            return ScoreEvidence(section.chroma, section.bass, section.openingBass,
                section.triads, section.rootedTriads, section.closingTriads,
                section.cadences, section.accepted, section.bassAccepted,
                section.openingAccepted);
        }

        private static void CollectSection(SectionEvidence section, double[] frameChroma,
            double[] frameBass, double sum, double bassSum, bool bassPresent,
            double strongestChord, double secondChord, int strongestIndex,
            double positionWithinSection, double time, bool extra)
        {
            section.attempted++;
            double maxShare = 0;
            for (int note = 0; note < 12; note++)
                maxShare = Math.Max(maxShare, frameChroma[note] / sum);

            // Broad, drum-like spectra and silent frames don't influence a
            // section's estimate. Allow clear pitched bass even without triads.
            if (maxShare < .16 ||
                (strongestChord < .020 && !(bassPresent && maxShare > .30)))
            {
                section.previousChord = -1;
                return;
            }
            section.accepted++;
            if (strongestChord >= .032) section.chordFrames++;
            for (int note = 0; note < 12; note++)
                section.chroma[note] += frameChroma[note] / sum;
            if (bassPresent)
            {
                for (int note = 0; note < 12; note++)
                {
                    double share = frameBass[note] / bassSum;
                    section.bass[note] += share;
                    // Extra windows are sampled out of chronological order;
                    // they must not overwrite the original opening evidence.
                    if (!extra && section.openingAccepted < 4)
                        section.openingBass[note] += share;
                }
                section.bassAccepted++;
                if (!extra && section.openingAccepted < 4) section.openingAccepted++;
            }
            for (int index = 0; index < KeyCount; index++)
            {
                int root = index % 12;
                double triad = TriadSupport(frameChroma, sum, root, index >= 12);
                section.triads[index] += triad;
                if (bassPresent) section.rootedTriads[index] +=
                    triad * frameBass[root] / bassSum;
                if (positionWithinSection >= .88)
                    section.closingTriads[index] += triad;
            }
            bool clear = strongestIndex >= 0 && strongestChord > .038 &&
                strongestChord > secondChord * 1.12;
            if (!extra && clear && section.previousChord >= 0 &&
                time - section.previousTime > 0 && time - section.previousTime < 3.5)
            {
                int tonic = strongestIndex % 12;
                int previousRoot = section.previousChord % 12;
                bool dominant = previousRoot == (tonic + 7) % 12;
                bool subtonic = strongestIndex >= 12 && previousRoot == (tonic + 10) % 12;
                if (dominant || subtonic)
                    section.cadences[strongestIndex] +=
                        Math.Min(section.previousChordStrength, strongestChord) *
                        (dominant ? 1.0 : .45);
            }
            section.previousChord = !extra && clear ? strongestIndex : -1;
            section.previousChordStrength = strongestChord;
            section.previousTime = time;
        }

        // Compare credible independent section estimates, not a majority vote.
        // Extra FFT work is only justified by an actual tonal disagreement.
        private static int FindDisputedSections(SectionEvidence[] sections,
            double[] globalScores, bool[] targets)
        {
            double globalMargin;
            int globalKey = TopKey(globalScores, out globalMargin);
            int[] winners = new int[SectionCount];
            double[] margins = new double[SectionCount];
            int credible = 0;
            for (int i = 0; i < SectionCount; i++)
            {
                winners[i] = -1;
                if (!sections[i].Reliable) continue;
                winners[i] = TopKey(ScoreSection(sections[i]), out margins[i]);
                if (margins[i] >= .045) credible++;
            }
            if (credible < 2) return 0;
            bool disagreement = false;
            for (int i = 0; i < SectionCount; i++)
            {
                if (winners[i] < 0 || margins[i] < .045) continue;
                for (int j = i + 1; j < SectionCount; j++)
                    if (winners[j] >= 0 && margins[j] >= .045 && winners[i] != winners[j])
                        disagreement = true;
            }
            if (!disagreement) return 0;
            int count = 0;
            for (int i = 0; i < SectionCount; i++)
            {
                // Gather more data in credible sections that disagree with
                // the overall estimate, and in the strong corroborating ones.
                targets[i] = winners[i] >= 0 && margins[i] >= .045 &&
                    (winners[i] != globalKey || globalMargin < .08);
                if (targets[i]) count++;
            }
            // If only one challenger was flagged, sample at least one
            // competing reliable section as a control.
            if (count < 2)
                for (int i = 0; i < SectionCount; i++)
                    if (!targets[i] && winners[i] >= 0 && margins[i] >= .045)
                    {
                        targets[i] = true;
                        count++;
                        break;
                    }
            return count;
        }

        public static IEnumerator Analyze(AudioClip sample, Action<Result> finished, Func<bool> cancelled)
        {
            Result unavailable = new Result { status = "NO TONAL KEY" };
            if (sample == null || sample.samples < 2048 || sample.frequency < 4000 || sample.channels < 1)
            {
                finished?.Invoke(unavailable);
                yield break;
            }

            int channels = sample.channels;
            int stride = Mathf.Max(1, Mathf.RoundToInt(sample.frequency / 11025f));
            int required = (FftSize - 1) * stride + 1;
            int readFrames = Mathf.Min(required, sample.samples);
            float[] interleaved = new float[readFrames * channels];
            double[] re = new double[FftSize], im = new double[FftSize];
            double[] chroma = new double[12], frameChroma = new double[12];
            // Strong, recurring bass notes provide additional tonic evidence
            // when relative major/minor keys share almost the same pitch set.
            double[] bassChroma = new double[12], frameBass = new double[12];
            // Many loops establish their tonal center in the opening phrase.
            // Record the first few *tonal* bass frames separately, so a later
            // relative-major chord cannot automatically win by note frequency.
            double[] openingBass = new double[12];
            // Local chord evidence, especially a tonic triad with its own bass.
            // Relative keys share pitches, but C-E-G over C and A-C-E over A
            // are different harmonic events. Keep those events separate.
            double[] tonicTriads = new double[KeyCount];
            double[] bassRootedTriads = new double[KeyCount];
            double[] closingTriads = new double[KeyCount];
            double[] resolvedCadences = new double[KeyCount];
            double[] chordVotes = new double[KeyCount];
            int previousChord = -1;
            int previousChordWindow = -10;
            double previousChordStrength = 0;
            double[] magnitudes = new double[FftSize / 2 + 1];
            int windows = sample.samples <= required ? 1 :
                Mathf.Min(BaseWindowCap, Mathf.Max(16, Mathf.CeilToInt(sample.length * 1.6f)));
            // Reserve a small portion of the existing 144-window limit for
            // disagreement-driven resampling; easy tracks use fewer FFTs.
            int extraBudget = Math.Min(ExtraWindowCap, MaxWindows - windows);
            int extraWindows = 0;
            bool[] extraTargets = new bool[SectionCount];
            int[] targetOrder = new int[SectionCount];
            int targetCount = 0;
            SectionEvidence[] sections = new SectionEvidence[SectionCount];
            for (int i = 0; i < SectionCount; i++) sections[i] = new SectionEvidence();
            int accepted = 0, bassAccepted = 0, openingAccepted = 0;
            double effectiveRate = (double)sample.frequency / stride;
            int loBin = Math.Max(2, (int)Math.Ceiling(48 * FftSize / effectiveRate));
            int hiBin = Math.Min(FftSize / 2 - 2, (int)(1450 * FftSize / effectiveRate));

            for (int win = 0; win < windows + extraWindows; win++)
            {
                if (cancelled != null && cancelled()) yield break;
                bool extra = win >= windows;
                // The first pass spreads evenly across beginning, middle, end.
                // The optional second pass adds different offsets only in
                // sections whose convincing estimates conflict.
                double position;
                if (!extra) position = (win + .5) / windows;
                else
                {
                    int index = win - windows;
                    int target = targetOrder[index % targetCount];
                    int slot = index / targetCount;
                    int slotsInSection = (extraWindows + targetCount - 1) / targetCount;
                    double within = (slot + .37 + .17 * (slot % 2)) / (slotsInSection + .5);
                    position = (target + Math.Min(.98, within)) / SectionCount;
                }
                int segment = Math.Min(SectionCount - 1, (int)(position * SectionCount));
                double withinSegment = position * SectionCount - segment;
                int start = sample.samples <= readFrames ? 0 :
                    (int)Math.Round((double)(sample.samples - readFrames) * position);
                bool read = false;
                try { read = sample.GetData(interleaved, start); }
                catch (Exception) { read = false; }
                if (!read)
                {
                    finished?.Invoke(new Result { status = "AUDIO NOT READABLE" });
                    yield break;
                }

                double energy = 0.0;
                for (int i = 0; i < FftSize; i++)
                {
                    int frame = i * stride;
                    float value = 0f;
                    if (frame < readFrames)
                    {
                        int index = frame * channels;
                        // For phase-opposed stereo, use the louder channel.
                        value = interleaved[index];
                        if (channels > 1)
                        {
                            float second = interleaved[index + 1];
                            if (Math.Sign(value) == Math.Sign(second)) value = (value + second) * .5f;
                            else if (Mathf.Abs(second) > Mathf.Abs(value)) value = second;
                        }
                    }
                    double window = .5 - .5 * Math.Cos(2.0 * Math.PI * i / (FftSize - 1));
                    re[i] = value * window;
                    im[i] = 0;
                    energy += re[i] * re[i];
                }
                if (energy < 0.00003)
                {
                    if (win % 4 == 3) yield return null;
                    if (win == windows - 1 && extraBudget > 0 && accepted >= 8)
                    {
                        double[] baseline = ScoreEvidence(chroma, bassChroma, openingBass,
                            tonicTriads, bassRootedTriads, closingTriads, resolvedCadences,
                            accepted, bassAccepted, openingAccepted);
                        FindDisputedSections(sections, baseline, extraTargets);
                        for (int i = 0; i < SectionCount; i++)
                            if (extraTargets[i]) targetOrder[targetCount++] = i;
                        if (targetCount >= 2) extraWindows = extraBudget;
                    }
                    continue;
                }

                Fft(re, im);
                double highest = 0;
                for (int b = loBin; b <= hiBin + 1; b++)
                {
                    magnitudes[b] = Math.Sqrt(re[b] * re[b] + im[b] * im[b]);
                    if (magnitudes[b] > highest) highest = magnitudes[b];
                }
                Array.Clear(frameChroma, 0, frameChroma.Length);
                Array.Clear(frameBass, 0, frameBass.Length);
                double sum = 0, bassSum = 0;
                int peaks = 0;
                double minMagnitude = highest * .115;
                for (int b = loBin + 1; b < hiBin; b++)
                {
                    double magnitude = magnitudes[b];
                    if (magnitude < minMagnitude || magnitude < magnitudes[b - 1] || magnitude < magnitudes[b + 1]) continue;
                    // Parabolic peak interpolation improves semitone alignment.
                    double a = Math.Log(magnitudes[b - 1] + 1e-10);
                    double c = Math.Log(magnitudes[b + 1] + 1e-10);
                    double m = Math.Log(magnitude + 1e-10);
                    double curvature = a - 2 * m + c;
                    double offset = Math.Abs(curvature) > 1e-9 ? .5 * (a - c) / curvature : 0;
                    offset = Math.Max(-.5, Math.Min(.5, offset));
                    double hz = (b + offset) * effectiveRate / FftSize;
                    if (hz <= 0) continue;
                    double midi = 69 + 12 * Math.Log(hz / 440.0, 2);
                    int rounded = (int)Math.Round(midi);
                    double cents = Math.Abs(midi - rounded);
                    if (cents >= .43) continue;
                    int chromatic = ((rounded % 12) + 12) % 12;
                    // The 3rd/5th/etc. partials of a bass or synth note can
                    // look like independent fifths and *major thirds* to a
                    // plain peak picker. Discount peaks that are plausibly
                    // harmonics of a stronger fundamental in this same frame.
                    // Keep genuine chord tones: this is a soft attenuation.
                    double fundamentalWeight = OvertoneWeight(magnitudes, b, loBin, magnitude);
                    double weight = Math.Sqrt(magnitude) * Math.Pow(130.0 / hz, .32) *
                        (1.0 - cents / .50) * fundamentalWeight;
                    frameChroma[chromatic] += weight;
                    sum += weight;
                    // 310 Hz counted much of the lower melody/chord voicing
                    // as 'bass' and could incorrectly turn an F-major chord
                    // into proof that F was the overall key.
                    if (hz <= 225.0)
                    {
                        // Normalize each bass frame so a few loud moments do
                        // not drown the harmonic context of the entire song.
                        double lowWeight = weight * Math.Pow(180.0 / hz, .24);
                        frameBass[chromatic] += lowWeight;
                        bassSum += lowWeight;
                    }
                    peaks++;
                }
                if (sum > 0 && peaks >= 2)
                {
                    for (int note = 0; note < 12; note++) chroma[note] += frameChroma[note] / sum;
                    accepted++;
                    bool bassPresent = bassSum > sum * .12;
                    if (bassPresent)
                    {
                        for (int note = 0; note < 12; note++)
                        {
                            double share = frameBass[note] / bassSum;
                            bassChroma[note] += share;
                            if (!extra && openingAccepted < 4) openingBass[note] += share;
                        }
                        bassAccepted++;
                        if (!extra && openingAccepted < 4) openingAccepted++;
                    }

                    // Analyze harmony in EACH window rather than only after
                    // collapsing the entire song into one histogram. Distinct
                    // root, third and fifth notes must occur simultaneously.
                    double strongestChord = 0, secondChord = 0;
                    int strongestIndex = -1;
                    for (int mode = 0; mode < 2; mode++)
                    {
                        for (int root = 0; root < 12; root++)
                        {
                            int index = mode * 12 + root;
                            double triad = TriadSupport(frameChroma, sum, root, mode == 1);
                            tonicTriads[index] += triad;
                            if (bassPresent)
                                bassRootedTriads[index] += triad * frameBass[root] / bassSum;
                            if (position >= .88)
                                closingTriads[index] += triad;
                            chordVotes[index] = triad * (bassPresent ?
                                1.0 + 1.5 * frameBass[root] / bassSum : 1.0);
                            if (chordVotes[index] > strongestChord)
                            {
                                secondChord = strongestChord;
                                strongestChord = chordVotes[index];
                                strongestIndex = index;
                            }
                            else if (chordVotes[index] > secondChord)
                                secondChord = chordVotes[index];
                        }
                    }
                    // Count convincing V-to-I (or V-to-i) arrivals. Minor
                    // V-to-i is common in natural-minor electronic music too.
                    // Do not call a transition a cadence if adjacent sampled
                    // frames are separated by an excessive time interval.
                    bool clearChord = strongestIndex >= 0 && strongestChord > .038 &&
                        strongestChord > secondChord * 1.12;
                    if (!extra && clearChord && previousChord >= 0 && win - previousChordWindow <= 2 &&
                        sample.length / windows < 3.5f)
                    {
                        int tonic = strongestIndex % 12;
                        int previousRoot = previousChord % 12;
                        bool dominant = previousRoot == (tonic + 7) % 12;
                        bool minorSubtonic = strongestIndex >= 12 &&
                            previousRoot == (tonic + 10) % 12;
                        if (dominant || minorSubtonic)
                        {
                            double strength = Math.Min(previousChordStrength, strongestChord);
                            // Subtonic VII -> i is a weaker clue than V -> i.
                            resolvedCadences[strongestIndex] += strength *
                                (dominant ? 1.0 : .45);
                        }
                    }
                    // Don't connect extra-pass windows to the original time
                    // series: resampling jumps backwards within the recording.
                    previousChord = !extra && clearChord ? strongestIndex : -1;
                    previousChordStrength = strongestChord;
                    previousChordWindow = win;
                    CollectSection(sections[segment], frameChroma, frameBass, sum,
                        bassSum, bassPresent, strongestChord, secondChord,
                        strongestIndex, withinSegment, position * sample.length, extra);
                }
                if (win % 2 == 1) yield return null;

                if (win == windows - 1 && extraBudget > 0 && accepted >= 8)
                {
                    double[] baseline = ScoreEvidence(chroma, bassChroma, openingBass,
                        tonicTriads, bassRootedTriads, closingTriads, resolvedCadences,
                        accepted, bassAccepted, openingAccepted);
                    FindDisputedSections(sections, baseline, extraTargets);
                    for (int i = 0; i < SectionCount; i++)
                        if (extraTargets[i]) targetOrder[targetCount++] = i;
                    if (targetCount >= 2) extraWindows = extraBudget;
                }
            }
            if (cancelled != null && cancelled()) yield break;
            if (accepted < Math.Max(3, windows / 6))
            {
                finished?.Invoke(unavailable);
                yield break;
            }

            // Global estimate preserves the previously validated scoring.
            // Section estimates are a separate cross-check, weighted by the
            // amount of actual pitched/chordal evidence in each third.
            double[] globalScores = ScoreEvidence(chroma, bassChroma, openingBass,
                tonicTriads, bassRootedTriads, closingTriads, resolvedCadences,
                accepted, bassAccepted, openingAccepted);
            double[] candidateScores = (double[])globalScores.Clone();
            double[] sectionWeights = new double[SectionCount];
            double[][] localScores = new double[SectionCount][];
            int reliableSections = 0;
            double weightSum = 0;
            for (int i = 0; i < SectionCount; i++)
            {
                SectionEvidence section = sections[i];
                if (!section.Reliable) continue;
                localScores[i] = ScoreSection(section);
                // Suppress weak/transient sections instead of forcing a vote.
                // Capping the weight avoids a long, loud chorus dominating.
                double weight = Math.Min(1.0, section.chordFrames / 6.0) *
                    Math.Min(1.0, section.accepted / 12.0);
                sectionWeights[i] = weight;
                weightSum += weight;
                reliableSections++;
            }
            if (weightSum > 0)
            {
                for (int key = 0; key < KeyCount; key++)
                {
                    double sectionScore = 0;
                    for (int i = 0; i < SectionCount; i++)
                        if (localScores[i] != null)
                            sectionScore += localScores[i][key] * sectionWeights[i];
                    sectionScore /= weightSum;
                    // The aggregate still owns the final result: this is
                    // evidence fusion, never a 2-out-of-3 majority vote.
                    candidateScores[key] = .78 * globalScores[key] + .22 * sectionScore;
                }
            }
            double bestMargin;
            int bestIndex = TopKey(candidateScores, out bestMargin);
            int bestKey = bestIndex % 12;
            bool bestMinor = bestIndex >= 12;
            double best = candidateScores[bestIndex];
            double next = best - bestMargin;

            int agreeing = 0, conflicts = 0;
            double agreeingStrength = 0, conflictingStrength = 0;
            for (int i = 0; i < SectionCount; i++)
            {
                if (localScores[i] == null) continue;
                double margin;
                int winner = TopKey(localScores[i], out margin);
                // Weak preferences carry no agreement or conflict evidence.
                if (margin < .045) continue;
                if (winner == bestIndex)
                {
                    agreeing++;
                    agreeingStrength += margin * sectionWeights[i];
                }
                else
                {
                    conflicts++;
                    conflictingStrength += margin * sectionWeights[i];
                }
            }
            // Key can be genuinely ambiguous, especially for drum-only loops,
            // single-note bass, and relative major/minor progressions.
            if (best < .32)
            {
                finished?.Invoke(unavailable);
                yield break;
            }
            // Relative major/minor pairs use the same pitch collection.
            // Compare the chosen key DIRECTLY with its relative counterpart:
            // if no stable tonic/harmony distinguishes them, prefer an honest
            // ambiguous result rather than confidently inventing a major key.
            int relativeKey = bestMinor ? (bestKey + 3) % 12 : (bestKey + 9) % 12;
            int relativeIndex = bestMinor ? relativeKey : 12 + relativeKey;
            double relativeGap = best - candidateScores[relativeIndex];
            if (relativeGap < .035 && candidateScores[relativeIndex] > .42)
            {
                finished?.Invoke(new Result { status = "RELATIVE KEY UNCLEAR" });
                yield break;
            }
            // In addition to relative-key confusion, strong subdominant and
            // borrowed chords can make two *non-relative* tonics plausible.
            // An uncertain result is safer than a confidently incorrect major.
            if (bestMargin < .028 && next > .40)
            {
                finished?.Invoke(new Result { status = "KEY UNCLEAR" });
                yield break;
            }
            // Agreement affects confidence, not the key's identity. When a
            // credible section contradicts another, don't report HIGH even
            // if the whole-song histogram is otherwise very strong.
            double corroboration = agreeing >= 2 && conflicts == 0 ? .04 : 0;
            double contradiction = conflictingStrength > agreeingStrength * .6 ?
                Math.Min(.14, conflictingStrength * .3) : 0;
            float confidence = Mathf.Clamp01((float)((best - .25) * 1.1 +
                Math.Min(bestMargin, relativeGap) * 3.0 +
                corroboration - contradiction));
            string grade = best >= .68 && bestMargin >= .10 && relativeGap >= .08 ? "HIGH" :
                           best >= .48 && bestMargin >= .045 && relativeGap >= .045 ? "MED" : "LOW";
            if (conflicts > 0 || reliableSections < 2 || agreeing < 2)
            {
                if (grade == "HIGH") grade = "MED";
            }
            if (conflicts >= 2 && conflictingStrength > agreeingStrength)
                grade = "LOW";
            if (accepted < 8 || bassAccepted < 3 || sample.length < 6f) grade = "LOW";
            finished?.Invoke(new Result { found = true, tonic = bestKey, minor = bestMinor,
                confidence = confidence, status = grade });
        }


        /// <summary>
        /// Salience of a major/minor triad occurring together in a single FFT
        /// frame. The wrong third subtracts evidence rather than making every
        /// root+fifth (power chord) automatically qualify as major or minor.
        /// </summary>
        // Downweight overtone leakage without erasing octave-doubled notes or
        // genuine simultaneous instruments. In particular, harmonic 5 of a
        // sustained note lands near its MAJOR THIRD and can otherwise change
        // a true minor key into an apparent major key.
        private static double OvertoneWeight(double[] magnitudes, int bin, int firstBin, double magnitude)
        {
            double penalty = 0;
            for (int partial = 3; partial <= 7; partial++)
            {
                int fundamental = (int)Math.Round((double)bin / partial);
                if (fundamental <= firstBin || fundamental + 1 >= magnitudes.Length) continue;
                double lower = Math.Max(magnitudes[fundamental - 1],
                    Math.Max(magnitudes[fundamental], magnitudes[fundamental + 1]));
                if (lower <= magnitude * .75) continue;
                double strength = partial == 5 ? .65 : partial == 3 || partial == 6 ? .43 : .32;
                penalty = Math.Max(penalty, strength *
                    Math.Min(1.0, (lower / Math.Max(magnitude, 1e-10) - .75) * .75));
            }
            return 1.0 - penalty;
        }

        private static double ScaleShare(double[] chroma, int tonic, int[] scale)
        {
            double total = 0, inside = 0;
            for (int n = 0; n < 12; n++) total += chroma[n];
            if (total <= 0) return 0;
            for (int n = 0; n < scale.Length; n++) inside += chroma[(tonic + scale[n]) % 12];
            return inside / total;
        }

        private static double ScaleFit(double[] chroma, int tonic, bool minor)
        {
            if (!minor) return ScaleShare(chroma, tonic, MajorNotes);
            return Math.Max(ScaleShare(chroma, tonic, NaturalMinorNotes),
                Math.Max(ScaleShare(chroma, tonic, DorianMinorNotes) - .012,
                    ScaleShare(chroma, tonic, HarmonicMinorNotes) - .02));
        }

        private static double TriadSupport(double[] frame, double frameTotal, int root, bool minor)
        {
            if (frameTotal <= 0) return 0;
            double tonic = frame[root] / frameTotal;
            double third = frame[(root + (minor ? 3 : 4)) % 12] / frameTotal;
            double fifth = frame[(root + 7) % 12] / frameTotal;
            double otherThird = frame[(root + (minor ? 4 : 3)) % 12] / frameTotal;
            return Math.Max(0.0, Math.Min(tonic, Math.Min(third, fifth)) - .30 * otherThird);
        }

        private static double Correlation(double[] values, double[] profile, int root)
        {
            double x = 0, y = 0;
            for (int n = 0; n < 12; n++) { x += values[n]; y += profile[n]; }
            x /= 12.0; y /= 12.0;
            double numerator = 0, dx = 0, dy = 0;
            for (int n = 0; n < 12; n++)
            {
                double a = values[(n + root) % 12] - x;
                double b = profile[n] - y;
                numerator += a * b; dx += a * a; dy += b * b;
            }
            return numerator / Math.Sqrt(dx * dy + 1e-15);
        }

        private static void Fft(double[] real, double[] imaginary)
        {
            const int n = FftSize;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i >= j) continue;
                double t = real[i]; real[i] = real[j]; real[j] = t;
                t = imaginary[i]; imaginary[i] = imaginary[j]; imaginary[j] = t;
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2 * Math.PI / len;
                double wr = Math.Cos(ang), wi = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1, ci = 0;
                    for (int j = 0; j < len / 2; j++)
                    {
                        int a = i + j, b = a + len / 2;
                        double tr = real[b] * cr - imaginary[b] * ci;
                        double ti = real[b] * ci + imaginary[b] * cr;
                        real[b] = real[a] - tr; imaginary[b] = imaginary[a] - ti;
                        real[a] += tr; imaginary[a] += ti;
                        double nextCr = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = nextCr;
                    }
                }
            }
        }
    }
}
