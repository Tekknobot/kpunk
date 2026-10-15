using System;
using System.Collections;
using UnityEngine;

namespace ZPunk.DAW
{
    /// <summary>
    /// Tempo matching for arbitrary-length clips. Works in bounded input chunks,
    /// yields to Unity between chunks, and does not impose an arbitrary 40s limit.
    /// Total clip size is ultimately constrained by available device memory and
    /// Unity AudioClip allocation limits. Source audio is never modified.
    /// </summary>
    public static class ZPunkSampleWarp
    {
        // One Hann table for all chunks instead of thousands of Mathf.Cos calls
        // while the main thread is also scheduling audio and rendering the DAW.
        private const int WindowSize = 1024;
        private static readonly float[] Hann = MakeWindow();
        private static float[] MakeWindow()
        {
            var result = new float[WindowSize];
            for (int i = 0; i < result.Length; ++i)
                result[i] = .5f - .5f * Mathf.Cos(2f * Mathf.PI * (i + .5f) / WindowSize);
            return result;
        }

        public static float EstimateLoopBpm(AudioClip clip, float songBpm)
        {
            if (clip == null || clip.length < .35f) return 0f;
            float preferred = Mathf.Clamp(songBpm, 40f, 240f);
            float best = 0f, error = float.MaxValue;
            // Long recordings may have hundreds of measures.
            for (int bars = 1; bars <= 16384; bars *= 2)
            {
                float bpm = bars * 240f / clip.length;
                if (bpm < 40f || bpm > 240f) continue;
                float difference = Mathf.Abs(Mathf.Log(bpm / preferred));
                if (difference < error) { error = difference; best = bpm; }
            }
            return best > 0f ? Mathf.Round(best * 10f) / 10f : 0f;
        }

        // Existing callers may still use the synchronous entry point for short clips.
        public static AudioClip Stretch(AudioClip source, float sourceBpm, float targetBpm,
            out string reason)
        {
            // Synchronous compatibility wrapper. New UI calls StretchAsync so it
            // can yield between chunks rather than blocking the main thread.
            AudioClip output = null;
            string message = "WARP NOT STARTED";
            IEnumerator task = StretchAsync(source, sourceBpm, targetBpm,
                (clip, status) => { output = clip; message = status; });
            // Unity's async caller understands nested IEnumerator yields. The
            // legacy sync entry point must explicitly traverse those children.
            var pending = new System.Collections.Generic.Stack<IEnumerator>();
            pending.Push(task);
            while (pending.Count > 0)
            {
                IEnumerator top = pending.Peek();
                if (!top.MoveNext()) pending.Pop();
                else if (top.Current is IEnumerator child) pending.Push(child);
            }
            reason = message;
            return output;
        }

        /// <summary>
        /// Chunk-wise, overlap-add pitch-preserving stretch. Callback runs on main
        /// thread when complete. Avoids full-length input/output float allocations.
        /// </summary>
        public static IEnumerator StretchAsync(AudioClip original, float sourceBpm,
            float targetBpm, Action<AudioClip, string> complete,
            Func<bool> cancelled = null)
        {
            if (original == null)
            {
                complete?.Invoke(null, "NO SAMPLE");
                yield break;
            }
            if (sourceBpm <= 0f || targetBpm <= 0f)
            {
                complete?.Invoke(null, "SET SOURCE BPM");
                yield break;
            }
            float ratio = Mathf.Clamp(sourceBpm / targetBpm, .25f, 4f);
            if (Mathf.Abs(ratio - 1f) < .001f)
            {
                complete?.Invoke(original, "TEMPO MATCHED");
                yield break;
            }

            int channels = original.channels, sampleRate = original.frequency;
            if (channels < 1 || channels > 2 || original.samples < 1024 || sampleRate < 1)
            {
                complete?.Invoke(null, "UNSUPPORTED AUDIO FORMAT");
                yield break;
            }
            long outputCount = (long)Math.Round(original.samples * (double)ratio);
            if (outputCount > int.MaxValue - 4096 || outputCount < 1024)
            {
                complete?.Invoke(null, "EXCEEDS UNITY AUDIOCLIP LIMIT");
                yield break;
            }

            AudioClip target = null;
            try
            {
                target = AudioClip.Create(original.name + " [ZPunk Warp]",
                    (int)outputCount, channels, sampleRate, false);
            }
            catch (Exception ex)
            {
                complete?.Invoke(null, "NOT ENOUGH MEMORY: " + ex.GetType().Name);
                yield break;
            }
            if (target == null)
            {
                complete?.Invoke(null, "UNABLE TO ALLOCATE AUDIO");
                yield break;
            }

            // Preserve the original two-second overlap-add boundaries (shorter
            // chunks introduced repeated audible edge fades). Instead distribute
            // the expensive inner stretch pass across Unity frames in ~8k-frame
            // slices. Reuse input/output buffers to avoid managed-GC spikes.
            int chunkLimit = Mathf.Max(4096, sampleRate * 2);
            var input = new float[chunkLimit * channels];
            var outputs = new System.Collections.Generic.Dictionary<int, float[]>();
            var weights = new System.Collections.Generic.Dictionary<int, float[]>();
            int cursor = 0;
            bool failed = false;
            while (cursor < original.samples)
            {
                if (cancelled != null && cancelled())
                {
                    UnityEngine.Object.Destroy(target);
                    yield break;
                }
                int frameCount = Math.Min(chunkLimit, original.samples - cursor);
                // GetData wraps the final buffer if there aren't enough source
                // frames remaining. StretchChunk only reads frameCount samples.
                if (!original.GetData(input, cursor))
                {
                    failed = true;
                    break;
                }
                int outStart = Mathf.RoundToInt(cursor * ratio);
                int outEnd = Mathf.Min((int)outputCount,
                    Mathf.RoundToInt((cursor + frameCount) * ratio));
                int outFrames = outEnd - outStart;
                if (outFrames <= 0) { cursor += frameCount; yield return null; continue; }
                if (!outputs.TryGetValue(outFrames, out float[] output))
                {
                    output = new float[outFrames * channels];
                    outputs[outFrames] = output;
                    weights[outFrames] = new float[outFrames];
                }
                yield return StretchChunkAsync(input, frameCount, outFrames,
                    channels, ratio, output, weights[outFrames], cancelled);
                if (cancelled != null && cancelled())
                {
                    UnityEngine.Object.Destroy(target);
                    yield break;
                }
                if (!target.SetData(output, outStart))
                {
                    failed = true;
                    break;
                }
                cursor += frameCount;
                yield return null;
            }
            if (cancelled != null && cancelled())
            {
                UnityEngine.Object.Destroy(target);
                yield break;
            }

            if (failed)
            {
                UnityEngine.Object.Destroy(target);
                complete?.Invoke(null, "READ/WRITE FAILED; USE PCM AUDIO");
                yield break;
            }
            complete?.Invoke(target, "PITCH PRESERVED  " +
                sourceBpm.ToString("0.#") + " TO " + targetBpm.ToString("0.#") + " BPM");
        }

        private static IEnumerator StretchChunkAsync(float[] input, int inputFrames, int outFrames,
            int channels, float ratio, float[] result, float[] weights,
            Func<bool> cancelled)
        {
            Array.Clear(result, 0, outFrames * channels);
            Array.Clear(weights, 0, outFrames);
            const int window = WindowSize, hop = 512, radius = 128;
            int available = Math.Max(0, inputFrames - window);
            for (int outPos = 0; outPos < outFrames; outPos += hop)
            {
                int expected = Mathf.Clamp(Mathf.RoundToInt(outPos / ratio), 0, available);
                int position = expected;
                if (outPos > 0 && inputFrames >= window)
                {
                    float best = -2f;
                    for (int d = -radius; d <= radius; d += 16)
                    {
                        int offset = Mathf.Clamp(expected + d, 0, available);
                        float dot = 0f, a = 0f, b = 0f;
                        for (int j = 0; j < 256 && outPos + j < outFrames; j += 2)
                        {
                            int f = outPos + j;
                            float previous = result[f * channels] / Mathf.Max(.00001f, weights[f]);
                            float incoming = input[Mathf.Min(inputFrames - 1, offset + j) * channels];
                            dot += previous * incoming;
                            a += previous * previous;
                            b += incoming * incoming;
                        }
                        float score = dot / Mathf.Sqrt(Mathf.Max(.0000001f, a * b));
                        if (score > best) { best = score; position = offset; }
                    }
                }
                int count = Mathf.Min(window, outFrames - outPos);
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
                float amplitude = 1f / Mathf.Max(.0001f, weights[f]);
                // Tiny edge fade prevents seams between incremental blocks.
                float fade = Mathf.Min(1f, Mathf.Min(f + 1, outFrames - f) / 64f);
                for (int c = 0; c < channels; c++)
                    result[f * channels + c] *= amplitude * fade;
                if (f > 0 && f % 8192 == 0)
                {
                    if (cancelled != null && cancelled()) yield break;
                    yield return null;
                }
            }
        }
    }
}
