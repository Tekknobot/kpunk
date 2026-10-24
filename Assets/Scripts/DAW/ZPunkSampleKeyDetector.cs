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
        // The additional evidence uses the EXISTING FFT windows (no more reads).
        private const int KeyCount = 24; // 12 major, then 12 minor

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
                Mathf.Min(MaxWindows, Mathf.Max(16, Mathf.CeilToInt(sample.length * 1.6f)));
            int accepted = 0, bassAccepted = 0, openingAccepted = 0;
            double effectiveRate = (double)sample.frequency / stride;
            int loBin = Math.Max(2, (int)Math.Ceiling(48 * FftSize / effectiveRate));
            int hiBin = Math.Min(FftSize / 2 - 2, (int)(1450 * FftSize / effectiveRate));

            for (int win = 0; win < windows; win++)
            {
                if (cancelled != null && cancelled()) yield break;
                // Evenly scan the body of the clip, including longer recordings.
                int start = sample.samples <= readFrames ? 0 :
                    (int)Math.Round((double)(sample.samples - readFrames) * (win + 0.5) / windows);
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
                if (energy < 0.00003) { if (win % 4 == 3) yield return null; continue; }

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
                            if (openingAccepted < 4) openingBass[note] += share;
                        }
                        bassAccepted++;
                        if (openingAccepted < 4) openingAccepted++;
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
                            if (win >= windows * .88)
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
                    if (clearChord && previousChord >= 0 && win - previousChordWindow <= 2 &&
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
                    // Don't connect two chords separated by an unknown phrase.
                    previousChord = clearChord ? strongestIndex : -1;
                    previousChordStrength = strongestChord;
                    previousChordWindow = win;
                }
                if (win % 2 == 1) yield return null;
            }
            if (cancelled != null && cancelled()) yield break;
            if (accepted < Math.Max(3, windows / 6))
            {
                finished?.Invoke(unavailable);
                yield break;
            }

            double best = -2, next = -2;
            int bestKey = 0;
            bool bestMinor = false;
            double[] candidateScores = new double[KeyCount];
            for (int key = 0; key < 12; key++)
            {
                for (int mode = 0; mode < 2; mode++)
                {
                    bool minorCandidate = mode == 1;
                    double score = Correlation(chroma, minorCandidate ? Minor : Major, key);
                    if (minorCandidate)
                        score = Math.Max(score, Correlation(chroma, MinorDorian, key) - .025);
                    // Check the *whole scale*, not just the most audible chord.
                    // An F-major chord inside a C-minor/Dorian song isn't by
                    // itself evidence of the F-major scale. Treat harmonic and
                    // Dorian minor as valid variants with small complexity costs.
                    double scaleFit = ScaleFit(chroma, key, minorCandidate);
                    score += .30 * (scaleFit - .65);
                    // Relative major and minor use the same seven notes.
                    // Favor the root actually recurring in the sample, with
                    // a moderate bass emphasis rather than a hard minor bias.
                    double tonicShare = chroma[key] / Math.Max(1, accepted);
                    score += .11 * (tonicShare - 1.0 / 12.0);
                    // The relative-major pitch set is almost identical to a
                    // natural minor scale. An audible minor third over the
                    // candidate root is positive minor evidence; an audible
                    // major third is positive major evidence. Keep this a
                    // *soft* weight so power chords / sus chords remain valid.
                    int expectedThird = (key + (mode == 1 ? 3 : 4)) % 12;
                    int otherThird = (key + (mode == 1 ? 4 : 3)) % 12;
                    score += .26 * (chroma[expectedThird] - chroma[otherThird]) /
                        Math.Max(1, accepted);
                    if (bassAccepted >= 3)
                    {
                        double bassShare = bassChroma[key] / bassAccepted;
                        score += .32 * (bassShare - 1.0 / 12.0);
                    }
                    if (openingAccepted >= 3)
                        score += .09 * (openingBass[key] / openingAccepted - 1.0 / 12.0);
                    // Complement the chroma-profile estimate with evidence
                    // that the candidate tonic actually appears as a chord,
                    // ideally with its own root in the bass, or as a harmonic
                    // destination. All measures are normalized by the number
                    // of existing tonal windows and stay bounded on phones.
                    int index = mode * 12 + key;
                    // A frequently repeated subdominant chord isn't proof of
                    // tonic. Require some overall scale compatibility before
                    // its triad / root-in-bass evidence can dominate.
                    double scaleCompatibility = Math.Max(.30,
                        Math.Min(1.0, (scaleFit - .40) / .38));
                    double harmonicEvidence = 2.0 * tonicTriads[index] / accepted;
                    if (bassAccepted >= 3)
                        harmonicEvidence += 4.0 * bassRootedTriads[index] / bassAccepted;
                    harmonicEvidence += .6 * closingTriads[index] / Math.Max(1.0, accepted);
                    harmonicEvidence += 2.0 * resolvedCadences[index] / Math.Max(1.0, accepted);
                    score += scaleCompatibility * harmonicEvidence;
                    candidateScores[index] = score;
                    if (score > best)
                    {
                        next = best;
                        best = score;
                        bestKey = key;
                        bestMinor = mode == 1;
                    }
                    else if (score > next) next = score;
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
            if (best - next < .028 && next > .40)
            {
                finished?.Invoke(new Result { status = "KEY UNCLEAR" });
                yield break;
            }
            float confidence = Mathf.Clamp01((float)((best - .25) * 1.1 +
                Math.Min(best - next, relativeGap) * 3.0));
            string grade = best >= .68 && best - next >= .10 && relativeGap >= .08 ? "HIGH" :
                           best >= .48 && best - next >= .045 && relativeGap >= .045 ? "MED" : "LOW";
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
