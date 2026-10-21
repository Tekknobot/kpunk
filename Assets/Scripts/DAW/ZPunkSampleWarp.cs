using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ZPunk.DAW
{
    /// <summary>
    /// Offline, pitch-preserving sample warp. Detects strong percussive attacks and
    /// gently quantizes nearby attacks to the song's eighth-note grid. A monotonic
    /// time map guides WSOLA overlap/add synthesis between timing anchors.
    /// Original AudioClip data is never modified. Processing yields each frame.
    /// This is transient-guided groove tightening, not kick/snare stem separation.
    /// </summary>
    public static class ZPunkSampleWarp
    {
        private const int WindowSize = 1024;
        private const int SynthesisHop = 512;
        private const int AnalysisHop = 512;
        private static readonly float[] Hann = MakeWindow();

        private struct WarpPoint
        {
            public int source;
            public int destination;
            public WarpPoint(int src, int dst) { source = src; destination = dst; }
        }

        private static float[] MakeWindow()
        {
            var window = new float[WindowSize];
            for (int i = 0; i < window.Length; i++)
                window[i] = .5f - .5f * Mathf.Cos(2f * Mathf.PI * (i + .5f) / WindowSize);
            return window;
        }

        // These are *suggested* attack locations, not stem separation. Classification
        // is based on low-frequency versus broadband onset energy and is intentionally
        // displayed as an estimate, not a guaranteed kick/snare identification.
        public struct TransientHint
        {
            public float position01;
            public bool kick;
            public TransientHint(float position, bool isKick)
            { position01 = position; kick = isKick; }
        }
        private struct AttackCandidate
        {
            public int frame;
            public float strength;
            public bool kick;
            public AttackCandidate(int at, float value, bool isKick)
            { frame = at; strength = value; kick = isKick; }
        }

        // The analysis FFT/RMS windows describe a REGION, not the first sample of
        // an attack. Find its actual leading edge in 1 ms envelopes around a
        // candidate, preventing ghost markers from preceding the visible hit.
        // Reuse the PCM buffer: songs can contain thousands of onsets.
        private static int RefineAttackFrame(AudioClip clip, int estimate,
            ref float[] pcm, ref float[] envelope)
        {
            int rate = clip.frequency;
            int channels = clip.channels;
            int windowStart = Mathf.Max(0, estimate - Mathf.RoundToInt(rate * .020f));
            int windowEnd = Mathf.Min(clip.samples, estimate + Mathf.RoundToInt(rate * .020f));
            int frames = windowEnd - windowStart;
            if (frames < 64) return Mathf.Clamp(estimate, 0, clip.samples - 1);
            if (pcm == null || pcm.Length < frames * channels)
                pcm = new float[frames * channels];
            if (!clip.GetData(pcm, windowStart)) return Mathf.Clamp(estimate, 0, clip.samples - 1);

            int step = Mathf.Max(16, rate / 1000); // about one millisecond
            int bins = frames / step;
            if (bins < 8) return Mathf.Clamp(estimate, 0, clip.samples - 1);
            if (envelope == null || envelope.Length < bins) envelope = new float[bins];
            for (int b = 0; b < bins; b++)
            {
                float energy = 0f;
                for (int n = b * step; n < (b + 1) * step; n++)
                {
                    int p = n * channels;
                    float v = channels == 2 ? (pcm[p] + pcm[p + 1]) * .5f : pcm[p];
                    energy += v * v;
                }
                envelope[b] = Mathf.Sqrt(energy / step);
            }
            // The strongest positive envelope slope identifies an attack. Search
            // around it for the first substantial rise, rather than returning its
            // (usually later) energy peak or a fixed number of milliseconds.
            int minBin = Mathf.Max(2, (estimate - windowStart) / step - 15);
            int maxBin = Mathf.Min(bins - 2, (estimate - windowStart) / step + 14);
            int bestBin = -1;
            float bestRise = 0f;
            for (int b = minBin; b <= maxBin; b++)
            {
                float before = (envelope[b - 1] + envelope[b - 2]) * .5f;
                float rise = envelope[b] - before;
                if (rise > bestRise) { bestRise = rise; bestBin = b; }
            }
            if (bestBin < 0 || bestRise < .0004f) return Mathf.Clamp(estimate, 0, clip.samples - 1);
            int firstBin = bestBin;
            int firstAllowed = Mathf.Max(minBin, bestBin - 8);
            for (int b = bestBin - 1; b >= firstAllowed; b--)
            {
                float before = (envelope[b - 1] + envelope[b - 2]) * .5f;
                if (envelope[b] - before < bestRise * .24f) break;
                firstBin = b;
            }
            return Mathf.Clamp(windowStart + firstBin * step, 0, clip.samples - 1);
        }

        public static IEnumerator AnalyzeTransientHints(AudioClip clip,
            Action<List<TransientHint>> complete, Func<bool> cancelled = null)
        {
            var hints = new List<TransientHint>();
            if (clip == null || clip.samples < 1024 || clip.frequency <= 0 ||
                clip.channels < 1 || clip.channels > 2)
            { complete?.Invoke(hints); yield break; }

            int channels = clip.channels, rate = clip.frequency;
            const int hop = 512;
            const int chunkFrames = 16384;
            float[] buffer = new float[chunkFrames * channels];
            float bass = 0, presence = 0, slowLow = 0, slowHigh = 0;
            float lowEnergy = 0, highEnergy = 0, sumStrength = 0;
            int accumulator = 0;
            float lowAlpha = 1f - Mathf.Exp(-2f * Mathf.PI * 190f / rate);
            float presenceAlpha = 1f - Mathf.Exp(-2f * Mathf.PI * 1800f / rate);
            var candidates = new List<AttackCandidate>();
            for (int offset = 0; offset < clip.samples; offset += chunkFrames)
            {
                if (cancelled != null && cancelled()) yield break;
                int length = Mathf.Min(chunkFrames, clip.samples - offset);
                if (!clip.GetData(buffer, offset))
                { complete?.Invoke(hints); yield break; }
                for (int i = 0; i < length; ++i)
                {
                    int at = i * channels;
                    float x = channels == 2 ? (buffer[at] + buffer[at + 1]) * .5f : buffer[at];
                    bass += lowAlpha * (x - bass);
                    presence += presenceAlpha * (x - presence);
                    float high = x - presence;
                    lowEnergy += bass * bass;
                    highEnergy += high * high;
                    if (++accumulator != hop) continue;
                    float lo = Mathf.Sqrt(lowEnergy / hop);
                    float hi = Mathf.Sqrt(highEnergy / hop);
                    float onsetLow = Mathf.Max(0f, lo - slowLow * 1.15f);
                    float onsetHigh = Mathf.Max(0f, hi - slowHigh * 1.17f);
                    float strength = onsetLow * 1.45f + onsetHigh * .8f;
                    sumStrength += strength;
                    // Local contrast suppresses most steady tones and quiet noise.
                    if (strength > .001f && (onsetLow > slowLow * .13f ||
                        onsetHigh > slowHigh * .16f))
                        candidates.Add(new AttackCandidate(Mathf.Max(0, offset + i),
                            strength, onsetLow * 1.15f >= onsetHigh * .85f));
                    slowLow = .87f * slowLow + .13f * lo;
                    slowHigh = .87f * slowHigh + .13f * hi;
                    lowEnergy = highEnergy = 0f;
                    accumulator = 0;
                }
                yield return null; // bounded PCM work on the main thread
            }
            float threshold = Mathf.Max(.0015f, sumStrength /
                Mathf.Max(1f, clip.samples / (float)hop) * 1.7f);
            int minimumSpacing = Mathf.RoundToInt(rate * .075f);
            // Keep the strongest candidate in each ~75ms neighborhood.
            var picked = new List<AttackCandidate>();
            foreach (var hit in candidates)
            {
                if (hit.strength < threshold) continue;
                if (picked.Count > 0 && hit.frame - picked[picked.Count - 1].frame < minimumSpacing)
                {
                    if (hit.strength > picked[picked.Count - 1].strength)
                        picked[picked.Count - 1] = hit;
                }
                else picked.Add(hit);
            }
            // A very long song would otherwise display thousands of guides.
            // Keep a bounded set of the strongest; return in chronological order.
            const int maxHints = 256;
            if (picked.Count > maxHints)
            {
                picked.Sort((a, b) => b.strength.CompareTo(a.strength));
                picked.RemoveRange(maxHints, picked.Count - maxHints);
                picked.Sort((a, b) => a.frame.CompareTo(b.frame));
            }
            float[] detailPcm = null, detailEnvelope = null;
            int lastFrame = -minimumSpacing;
            for (int i = 0; i < picked.Count; i++)
            {
                if (cancelled != null && cancelled()) yield break;
                var hit = picked[i];
                int attackFrame = RefineAttackFrame(clip, hit.frame, ref detailPcm, ref detailEnvelope);
                // Refinement must not collapse distinct events into a duplicate.
                if (attackFrame - lastFrame < minimumSpacing / 2) continue;
                hints.Add(new TransientHint(Mathf.Clamp01(attackFrame / (float)clip.samples), hit.kick));
                lastFrame = attackFrame;
                if ((i & 31) == 31) yield return null;
            }
            complete?.Invoke(hints);
        }

        // Duration-derived estimate only. For an untrimmed song, users must set
        // source BPM manually; guessing BPM from duration is not beat detection.
        public static float EstimateLoopBpm(AudioClip clip, float songBpm)
        {
            if (clip == null || clip.length < .35f) return 0f;
            float preferred = Mathf.Clamp(songBpm, 40f, 240f);
            float best = 0f, error = float.MaxValue;
            for (int bars = 1; bars <= 16384; bars *= 2)
            {
                float bpm = bars * 240f / clip.length;
                if (bpm < 40f || bpm > 240f) continue;
                float difference = Mathf.Abs(Mathf.Log(bpm / preferred));
                if (difference < error) { error = difference; best = bpm; }
            }
            return best > 0f ? Mathf.Round(best * 10f) / 10f : 0f;
        }

        public static AudioClip Stretch(AudioClip source, float sourceBpm, float targetBpm,
            out string reason)
        {
            AudioClip result = null;
            string status = "WARP NOT STARTED";
            IEnumerator task = StretchAsync(source, sourceBpm, targetBpm,
                (clip, message) => { result = clip; status = message; });
            var pending = new Stack<IEnumerator>();
            pending.Push(task);
            while (pending.Count > 0)
            {
                IEnumerator current = pending.Peek();
                if (!current.MoveNext()) pending.Pop();
                else if (current.Current is IEnumerator nested) pending.Push(nested);
            }
            reason = status;
            return result;
        }

        /// <param name="progress">Normalized 0..1, with stage text.</param>
        public static IEnumerator StretchAsync(AudioClip original, float sourceBpm,
            float targetBpm, Action<AudioClip, string> complete,
            Func<bool> cancelled = null, Action<float, string> progress = null)
        {
            if (original == null) { complete?.Invoke(null, "NO SAMPLE"); yield break; }
            if (sourceBpm <= 0f || targetBpm <= 0f)
            { complete?.Invoke(null, "SET SOURCE BPM"); yield break; }
            int channels = original.channels;
            int sampleRate = original.frequency;
            if (channels < 1 || channels > 2 || original.samples < 1024 || sampleRate < 1)
            { complete?.Invoke(null, "UNSUPPORTED AUDIO FORMAT"); yield break; }
            float ratio = Mathf.Clamp(sourceBpm / targetBpm, .25f, 4f);
            long longCount = (long)Math.Round(original.samples * (double)ratio);
            if (longCount > int.MaxValue - 4096 || longCount < 1024)
            { complete?.Invoke(null, "EXCEEDS UNITY AUDIOCLIP LIMIT"); yield break; }
            int outputCount = (int)longCount;

            // Spectral-band energy onsets, analyzed in bounded buffers. No large
            // full-song PCM arrays or allocations on the audio callback thread.
            var strengths = new List<float>();
            var positions = new List<int>();
            int analysisFrames = Math.Max(AnalysisHop, sampleRate / 4);
            var pcm = new float[analysisFrames * channels];
            float low = 0f, presence = 0f, slowLow = 0f, slowHigh = 0f;
            float lowPower = 0f, highPower = 0f, totalScore = 0f;
            int accumulator = 0, scan = 0;
            float lowFactor = 1f - Mathf.Exp(-2f * Mathf.PI * 190f / sampleRate);
            float presenceFactor = 1f - Mathf.Exp(-2f * Mathf.PI * 1800f / sampleRate);
            progress?.Invoke(0f, "FINDING TRANSIENTS");
            while (scan < original.samples)
            {
                if (cancelled != null && cancelled()) yield break;
                int count = Math.Min(analysisFrames, original.samples - scan);
                if (!original.GetData(pcm, scan))
                { complete?.Invoke(null, "PCM READ FAILED"); yield break; }
                for (int i = 0; i < count; ++i)
                {
                    float sample = 0f;
                    int offset = i * channels;
                    for (int c = 0; c < channels; c++) sample += pcm[offset + c];
                    sample /= channels;
                    low += lowFactor * (sample - low);
                    presence += presenceFactor * (sample - presence);
                    float high = sample - presence;
                    lowPower += low * low;
                    highPower += high * high;
                    if (++accumulator == AnalysisHop)
                    {
                        float lowRms = Mathf.Sqrt(lowPower / AnalysisHop);
                        float highRms = Mathf.Sqrt(highPower / AnalysisHop);
                        float lowRise = Mathf.Max(0f, lowRms - slowLow * 1.12f);
                        float highRise = Mathf.Max(0f, highRms - slowHigh * 1.16f);
                        float score = lowRise * 1.35f + highRise * .75f;
                        positions.Add(scan + i);
                        strengths.Add(score);
                        totalScore += score;
                        slowLow = .9f * slowLow + .1f * lowRms;
                        slowHigh = .9f * slowHigh + .1f * highRms;
                        lowPower = highPower = 0f;
                        accumulator = 0;
                    }
                }
                scan += count;
                progress?.Invoke(.25f * scan / original.samples, "FINDING TRANSIENTS");
                yield return null;
            }

            var points = new List<WarpPoint> { new WarpPoint(0, 0) };
            float meanScore = totalScore / Mathf.Max(1, strengths.Count);
            float threshold = Mathf.Max(.0009f, meanScore * 1.8f);
            var attackFrames = new List<int>();
            int lastAttack = -sampleRate;
            int lastAccepted = -sampleRate;
            int minGap = Mathf.RoundToInt(sampleRate * .135f);
            float eighthFrames = sampleRate * 30f / targetBpm;
            float beatFrames = eighthFrames * 2f;
            // A song may have silence before the first kick. Find the phase
            // shared by rhythmic transients instead of forcing every attack
            // toward a grid with its first beat at sample frame zero.
            float phaseFrames = 0f, peakPhaseScore = 0f, phaseTotal = 0f;
            float phaseTolerance = Mathf.Min(sampleRate * .055f, beatFrames * .18f);
            const int phaseBins = 48;
            for (int phaseBin = 0; phaseBin < phaseBins; phaseBin++)
            {
                float phase = beatFrames * phaseBin / phaseBins;
                float score = 0f, total = 0f;
                for (int k = 2; k + 2 < strengths.Count; k++)
                {
                    float power = strengths[k];
                    if (power < threshold || power < strengths[k - 1] ||
                        power < strengths[k + 1]) continue;
                    float at = positions[k] * ratio;
                    float distance = Mathf.Abs(Mathf.Repeat(at - phase + beatFrames * .5f,
                        beatFrames) - beatFrames * .5f);
                    total += power;
                    if (distance < phaseTolerance)
                        score += power * (1f - distance / phaseTolerance);
                }
                if (score > peakPhaseScore)
                { peakPhaseScore = score; phaseFrames = phase; phaseTotal = total; }
                if ((phaseBin & 7) == 7)
                {
                    if (cancelled != null && cancelled()) yield break;
                    progress?.Invoke(.25f, "LOCKING BEAT GRID");
                    yield return null;
                }
            }
            // Low-confidence audio is left on the normal zero-based beat grid.
            if (phaseTotal <= .0001f || peakPhaseScore / phaseTotal < .16f)
                phaseFrames = 0f;
            float maxSnapFrames = sampleRate * Mathf.Min(.075f, 9.6f / targetBpm);
            int inputGuard = Mathf.RoundToInt(sampleRate * .1f);
            int lastDest = 0;
            float[] attackPcm = null, attackEnvelope = null;
            for (int i = 2; i + 2 < strengths.Count; i++)
            {
                if (cancelled != null && cancelled()) yield break;
                float strength = strengths[i];
                if (strength < threshold || strength < strengths[i - 1] ||
                    strength < strengths[i + 1] || strength < strengths[i - 2] ||
                    strength < strengths[i + 2]) continue;
                int sourceAt = RefineAttackFrame(original, positions[i], ref attackPcm, ref attackEnvelope);
                if (sourceAt - lastAttack >= minGap && sourceAt >= 0 &&
                    sourceAt < original.samples - inputGuard)
                {
                    attackFrames.Add(sourceAt);
                    lastAttack = sourceAt;
                }
                if (sourceAt - lastAccepted < minGap || sourceAt < inputGuard ||
                    sourceAt > original.samples - inputGuard) continue;
                int expected = Mathf.RoundToInt(sourceAt * ratio);
                int snapped = Mathf.RoundToInt(phaseFrames +
                    Mathf.Round((expected - phaseFrames) / eighthFrames) * eighthFrames);
                float deviation = Mathf.Abs(snapped - expected);
                if (deviation < sampleRate * .004f || deviation > maxSnapFrames ||
                    snapped <= lastDest + sampleRate * .13f ||
                    snapped >= outputCount - sampleRate * .10f) continue;
                WarpPoint previous = points[points.Count - 1];
                float sourceDelta = sourceAt - previous.source;
                float destinationDelta = snapped - previous.destination;
                float localRatio = destinationDelta / Mathf.Max(1f, sourceDelta);
                // Enforce monotonic, moderate stretch between neighboring hits.
                if (localRatio < ratio * .67f || localRatio > ratio * 1.5f) continue;
                float remainingRatio = (outputCount - snapped) /
                    Mathf.Max(1f, original.samples - sourceAt);
                if (remainingRatio < ratio * .67f || remainingRatio > ratio * 1.5f) continue;
                points.Add(new WarpPoint(sourceAt, snapped));
                lastAccepted = sourceAt;
                lastDest = snapped;
            }
            points.Add(new WarpPoint(original.samples, outputCount));
            progress?.Invoke(.25f, "ALIGNING GRID");
            yield return null;
            if (cancelled != null && cancelled()) yield break;
            int shifted = points.Count - 2;
            if (shifted == 0 && Mathf.Abs(ratio - 1f) < .001f)
            {
                progress?.Invoke(1f, "ALREADY ALIGNED");
                complete?.Invoke(original, "GRID / NO CORRECTION NEEDED");
                yield break;
            }
            AudioClip target = null;
            try
            {
                target = AudioClip.Create(original.name + " [Grid Warp]",
                    outputCount, channels, sampleRate, false);
            }
            catch (Exception ex)
            {
                complete?.Invoke(null, "NOT ENOUGH MEMORY: " + ex.GetType().Name);
                yield break;
            }
            if (target == null) { complete?.Invoke(null, "UNABLE TO ALLOCATE AUDIO"); yield break; }

            bool failed = false;
            int block = Math.Max(4096, sampleRate * 2);
            int overlap = Mathf.Min(2048, block / 8);
            int cursor = 0;
            float[] tail = null;
            float[] inputBuffer = null;
            float[] outputBuffer = new float[block * channels];
            float[] weightBuffer = new float[block];
            float[] writeBuffer = new float[(block - overlap) * channels];
            while (cursor < outputCount)
            {
                if (cancelled != null && cancelled())
                { UnityEngine.Object.Destroy(target); yield break; }
                int outFrames = Math.Min(block, outputCount - cursor);
                int margin = WindowSize + sampleRate / 20;
                int sourceStart = Mathf.Max(0,
                    Mathf.FloorToInt(MapOutputToSource(points, cursor)) - margin);
                int sourceEnd = Mathf.Min(original.samples,
                    Mathf.CeilToInt(MapOutputToSource(points, cursor + outFrames)) + margin);
                int sourceFrames = sourceEnd - sourceStart;
                if (sourceFrames < WindowSize)
                { failed = true; break; }
                if (inputBuffer == null || inputBuffer.Length < sourceFrames * channels)
                    inputBuffer = new float[sourceFrames * channels];
                // GetData can wrap at the end of a non-streaming AudioClip.
                // The synthesis routine only reads sourceFrames from this buffer.
                if (!original.GetData(inputBuffer, sourceStart)) { failed = true; break; }
                Array.Clear(outputBuffer, 0, outFrames * channels);
                Array.Clear(weightBuffer, 0, outFrames);
                yield return StretchMappedBlock(inputBuffer, sourceFrames, sourceStart,
                    cursor, outFrames, channels, points, outputBuffer, weightBuffer, cancelled);
                // WSOLA can double/blur sharp attacks. Replace just the narrow
                // transient core with untouched source PCM and crossfade its edges.
                // The body of each sound remains time-warped / pitch-preserved.
                PreserveTransientCores(inputBuffer, sourceFrames, sourceStart,
                    cursor, outFrames, channels, sampleRate, points, attackFrames, outputBuffer);
                if (cancelled != null && cancelled())
                { UnityEngine.Object.Destroy(target); yield break; }
                if (tail != null)
                {
                    int blend = Math.Min(overlap, outFrames);
                    for (int f = 0; f < blend; f++)
                    {
                        float t = (f + 1f) / (blend + 1f);
                        for (int c = 0; c < channels; c++)
                        {
                            int at = f * channels + c;
                            outputBuffer[at] = tail[at] * (1f - t) + outputBuffer[at] * t;
                        }
                    }
                }
                bool more = cursor + outFrames < outputCount;
                int writeFrames = more ? outFrames - overlap : outFrames;
                int copySamples = writeFrames * channels;
                float[] write = writeBuffer;
                if (write.Length != copySamples)
                {
                    // Last block may have a shorter final write.
                    write = new float[copySamples];
                }
                Array.Copy(outputBuffer, 0, write, 0, copySamples);
                if (!target.SetData(write, cursor)) { failed = true; break; }
                if (more)
                {
                    if (tail == null) tail = new float[overlap * channels];
                    Array.Copy(outputBuffer, writeFrames * channels, tail, 0, tail.Length);
                }
                cursor += writeFrames;
                progress?.Invoke(.25f + .75f * cursor / outputCount,
                    shifted > 0 ? "ALIGNING TRANSIENTS" : "MATCHING TEMPO");
                yield return null;
            }
            if (cancelled != null && cancelled())
            { UnityEngine.Object.Destroy(target); yield break; }
            if (failed)
            {
                UnityEngine.Object.Destroy(target);
                complete?.Invoke(null, "PCM READ/WRITE FAILED");
                yield break;
            }
            progress?.Invoke(1f, "GRID WARP COMPLETE");
            complete?.Invoke(target, shifted > 0 ?
                "GRID ALIGNED / " + shifted + " ATTACKS / " +
                sourceBpm.ToString("0.#") + " TO " + targetBpm.ToString("0.#") + " BPM" :
                "TEMPO MATCHED / NO GRID ATTACKS FOUND");
        }

        private static float MapSourceToOutput(List<WarpPoint> points, float source)
        {
            int left = 0, right = points.Count - 1;
            while (left + 1 < right)
            {
                int middle = (left + right) / 2;
                if (points[middle].source <= source) left = middle;
                else right = middle;
            }
            WarpPoint a = points[left], b = points[right];
            float t = Mathf.Clamp01((source - a.source) / Mathf.Max(1f, b.source - a.source));
            return Mathf.Lerp(a.destination, b.destination, t);
        }

        private static void PreserveTransientCores(float[] input, int inputFrames, int inputStart,
            int outputStart, int outputFrames, int channels, int rate,
            List<WarpPoint> points, List<int> attacks, float[] output)
        {
            int pre = Mathf.Max(32, Mathf.RoundToInt(.010f * rate));
            int post = Mathf.Max(64, Mathf.RoundToInt(.045f * rate));
            int fade = Mathf.Max(16, Mathf.RoundToInt(.008f * rate));
            foreach (int attack in attacks)
            {
                int center = Mathf.RoundToInt(MapSourceToOutput(points, attack));
                int start = Mathf.Max(0, center - pre);
                int end = Mathf.Min(outputStart + outputFrames, center + post);
                if (end <= outputStart || start >= outputStart + outputFrames) continue;
                for (int global = Mathf.Max(start, outputStart); global < end; global++)
                {
                    int sourceAt = attack + (global - center) - inputStart;
                    if (sourceAt < 0 || sourceAt >= inputFrames) continue;
                    float fadeIn = Mathf.Clamp01((global - (center - pre)) / (float)fade);
                    float fadeOut = Mathf.Clamp01((center + post - global) / (float)fade);
                    float blend = Mathf.Min(fadeIn, fadeOut);
                    // Equal across channels to preserve stereo imaging.
                    int outAt = (global - outputStart) * channels;
                    int inAt = sourceAt * channels;
                    for (int c = 0; c < channels; c++)
                        output[outAt + c] = Mathf.Lerp(output[outAt + c], input[inAt + c], blend);
                }
            }
        }

        private static float MapOutputToSource(List<WarpPoint> points, float output)
        {
            int left = 0, right = points.Count - 1;
            while (left + 1 < right)
            {
                int middle = (left + right) / 2;
                if (points[middle].destination <= output) left = middle;
                else right = middle;
            }
            WarpPoint a = points[left], b = points[right];
            float span = Mathf.Max(1f, b.destination - a.destination);
            float t = Mathf.Clamp01((output - a.destination) / span);
            return Mathf.Lerp(a.source, b.source, t);
        }

        private static IEnumerator StretchMappedBlock(float[] input, int inputFrames,
            int inputStart, int outputStart, int outFrames, int channels,
            List<WarpPoint> points, float[] result, float[] weights,
            Func<bool> cancelled)
        {
            const int radius = 128;
            int available = Math.Max(0, inputFrames - WindowSize);
            for (int outPos = 0; outPos < outFrames; outPos += SynthesisHop)
            {
                int expected = Mathf.Clamp(Mathf.RoundToInt(
                    MapOutputToSource(points, outputStart + outPos) - inputStart),
                    0, available);
                int position = expected;
                if (outPos > 0 && inputFrames >= WindowSize)
                {
                    float best = -2f;
                    for (int d = -radius; d <= radius; d += 16)
                    {
                        int offset = Mathf.Clamp(expected + d, 0, available);
                        float dot = 0f, energyA = 0f, energyB = 0f;
                        for (int j = 0; j < 256 && outPos + j < outFrames; j += 2)
                        {
                            int frame = outPos + j;
                            float previous = result[frame * channels] /
                                Mathf.Max(.00001f, weights[frame]);
                            float incoming = input[Math.Min(inputFrames - 1, offset + j) * channels];
                            dot += previous * incoming;
                            energyA += previous * previous;
                            energyB += incoming * incoming;
                        }
                        float score = dot / Mathf.Sqrt(Mathf.Max(.0000001f, energyA * energyB));
                        if (score > best) { best = score; position = offset; }
                    }
                }
                int count = Mathf.Min(WindowSize, outFrames - outPos);
                for (int j = 0; j < count; j++)
                {
                    float weight = Hann[j];
                    int src = Math.Min(inputFrames - 1, position + j) * channels;
                    int dst = (outPos + j) * channels;
                    for (int c = 0; c < channels; c++)
                        result[dst + c] += input[src + c] * weight;
                    weights[outPos + j] += weight;
                }
                if (outPos > 0 && outPos % 8192 == 0)
                {
                    if (cancelled != null && cancelled()) yield break;
                    yield return null;
                }
            }
            for (int f = 0; f < outFrames; f++)
            {
                float normalization = 1f / Mathf.Max(.0001f, weights[f]);
                for (int c = 0; c < channels; c++)
                    result[f * channels + c] *= normalization;
                if (f > 0 && f % 16384 == 0)
                {
                    if (cancelled != null && cancelled()) yield break;
                    yield return null;
                }
            }
        }
    }
}
