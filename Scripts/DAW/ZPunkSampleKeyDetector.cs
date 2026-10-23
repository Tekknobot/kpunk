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
        private const int FftSize = 4096;
        private const int MaxWindows = 48;

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
            double[] magnitudes = new double[FftSize / 2 + 1];
            int windows = Mathf.Min(MaxWindows, Mathf.Max(8, Mathf.CeilToInt(sample.length * 1.5f)));
            int accepted = 0;
            double effectiveRate = (double)sample.frequency / stride;
            int loBin = Math.Max(2, (int)Math.Ceiling(65 * FftSize / effectiveRate));
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
                double sum = 0;
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
                    double weight = Math.Sqrt(magnitude) * Math.Pow(130.0 / hz, .32) * (1.0 - cents / .50);
                    frameChroma[chromatic] += weight;
                    sum += weight;
                    peaks++;
                }
                if (sum > 0 && peaks >= 2)
                {
                    for (int note = 0; note < 12; note++) chroma[note] += frameChroma[note] / sum;
                    accepted++;
                }
                if (win % 3 == 2) yield return null;
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
            for (int key = 0; key < 12; key++)
            {
                for (int mode = 0; mode < 2; mode++)
                {
                    double score = Correlation(chroma, mode == 0 ? Major : Minor, key);
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
            float confidence = Mathf.Clamp01((float)((best - .25) * 1.1 + (best - next) * 3.0));
            string grade = best >= .68 && best - next >= .10 ? "HIGH" :
                           best >= .48 && best - next >= .045 ? "MED" : "LOW";
            finished?.Invoke(new Result { found = true, tonic = bestKey, minor = bestMinor,
                confidence = confidence, status = grade });
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
