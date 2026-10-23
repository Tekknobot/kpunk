using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace ZPunk.DAW
{
    /// <summary>
    /// Tiny, bounded on-disk cache of detected amber hit guides. Stores NO PCM.
    /// It is independent of user-authored CHOPS markers and project save data.
    /// Cache keys describe the original source, not song BPM, preview pitch or
    /// warp settings. A cheap PCM signature detects changes to URI/resources
    /// audio, whose source file timestamps are not always available.
    /// </summary>
    public static class ZPunkSampleAnalysisCache
    {
        private const int FormatVersion = 3; // canonical original source guides; bump with detector
        private const int MaxEntries = 256;
        private const long MaxTotalBytes = 12L * 1024 * 1024;
        private const int MaxHints = 256;
        private static string CacheDir => Path.Combine(Application.persistentDataPath, "ZPunkAnalysisCache");

        // Visible in Unity Editor and DEVELOPMENT_BUILD player logs. No noisy
        // or potentially sensitive sample paths in production builds.
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void Trace(string message)
        {
            UnityEngine.Debug.Log("[ZPUNK/HitCache] " + message);
        }

        // Hash a handful of short PCM windows. This is deliberately bounded:
        // ~5k floats rather than decoding/analyzing a whole song every load.
        // File-backed imports use stronger file-size/mtime invalidation below.
        private static string SampledPcmSignature(AudioClip clip)
        {
            if (clip == null || clip.samples < 1) return null;
            int frames = Math.Min(256, clip.samples);
            int channels = clip.channels;
            if (channels < 1 || channels > 8) return null;
            float[] data = new float[frames * channels];
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offset;
            for (int i = 0; i < 9; i++)
            {
                int position = (int)(((long)(clip.samples - frames) * i) / 8);
                if (!clip.GetData(data, position)) return null;
                for (int j = 0; j < data.Length; j++)
                {
                    int quantized = Mathf.RoundToInt(Mathf.Clamp(data[j], -1f, 1f) * 32767f);
                    unchecked
                    {
                        hash ^= (uint)(quantized & 0xffff);
                        hash *= prime;
                    }
                }
            }
            return hash.ToString("x16");
        }

        [Serializable]
        private sealed class CachedGuides
        {
            public int version;
            public string fingerprint;
            public float[] positions;
            public float[] strengths;
            public bool[] kicks;
        }

        private static string Fingerprint(string sampleId, AudioClip original,
            AudioClip analyzed, string variant)
        {
            if (string.IsNullOrEmpty(sampleId) || original == null || analyzed == null)
                return null;
            var key = new StringBuilder(256);
            key.Append(FormatVersion).Append('|').Append(sampleId).Append('|');
            // Source and warped clips can differ in duration and sample count.
            key.Append(original.samples).Append(':').Append(original.channels).Append(':')
                .Append(original.frequency).Append('|').Append(analyzed.samples)
                .Append(':').Append(analyzed.channels).Append(':').Append(analyzed.frequency)
                .Append('|').Append(variant ?? "original");
            string path = null;
            if (sampleId.StartsWith("local:", StringComparison.Ordinal))
                path = Path.Combine(Application.persistentDataPath,
                    sampleId.Substring(6).Replace('/', Path.DirectorySeparatorChar));
            else if (sampleId.StartsWith("file:", StringComparison.Ordinal))
                path = sampleId.Substring(5);
            if (!string.IsNullOrEmpty(path))
            {
                var info = new FileInfo(path);
                if (!info.Exists) return null; // missing source cannot yield a valid cache hit
                key.Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks);
            }
            else
            {
                // Covers res: tracks and Android content URIs. An unchanged
                // bundled resource remains cached across version bumps.
                string signature = SampledPcmSignature(original);
                key.Append('|').Append(signature ?? ("fallback:" + Application.version));
            }
            return key.ToString();
        }

        private static string PathFor(string fingerprint)
        {
            byte[] data = Encoding.UTF8.GetBytes(fingerprint);
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(data);
                var name = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) name.Append(b.ToString("x2"));
                return Path.Combine(CacheDir, name.ToString() + ".json");
            }
        }

        public static bool TryLoad(string id, AudioClip original, AudioClip analyzed,
            string variant, out List<ZPunkSampleWarp.TransientHint> result)
        {
            result = null;
            try
            {
                string key = Fingerprint(id, original, analyzed, variant);
                if (key == null)
                {
                    Trace("MISS / sample ID, audio or source file unavailable");
                    return false;
                }
                string path = PathFor(key);
                string code = Path.GetFileNameWithoutExtension(path).Substring(0, 10);
                if (!File.Exists(path))
                {
                    Trace("MISS " + code + " / new sample, modified source or expired entry");
                    return false;
                }
                var entry = JsonUtility.FromJson<CachedGuides>(File.ReadAllText(path));
                if (entry == null || entry.version != FormatVersion || entry.fingerprint != key ||
                    entry.positions == null || entry.kicks == null || entry.strengths == null ||
                    entry.positions.Length != entry.kicks.Length ||
                    entry.positions.Length != entry.strengths.Length ||
                    entry.positions.Length > MaxHints)
                {
                    Trace("MISS " + code + " / stale or invalid cache entry");
                    return false;
                }
                var hints = new List<ZPunkSampleWarp.TransientHint>(entry.positions.Length);
                for (int i = 0; i < entry.positions.Length; i++)
                {
                    float pos = entry.positions[i];
                    if (float.IsNaN(pos) || float.IsInfinity(pos) || pos < 0f || pos > 1f)
                    {
                        Trace("MISS " + code + " / invalid marker position");
                        return false;
                    }
                    hints.Add(new ZPunkSampleWarp.TransientHint(pos, entry.kicks[i],
                        Mathf.Clamp(entry.strengths[i], .1f, 2f)));
                }
                result = hints;
                Trace("HIT " + code + " / guides=" + hints.Count + " / " + (variant ?? "original"));
                return true; // includes a valid zero-hit result
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[Sampler] Hit guide cache read skipped: " + ex.Message);
                return false;
            }
        }

        public static void Save(string id, AudioClip original, AudioClip analyzed,
            string variant, List<ZPunkSampleWarp.TransientHint> hints)
        {
            if (hints == null || hints.Count > MaxHints) return;
            try
            {
                string key = Fingerprint(id, original, analyzed, variant);
                if (key == null) return;
                Directory.CreateDirectory(CacheDir);
                var entry = new CachedGuides
                {
                    version = FormatVersion,
                    fingerprint = key,
                    positions = new float[hints.Count],
                    strengths = new float[hints.Count],
                    kicks = new bool[hints.Count]
                };
                for (int i = 0; i < hints.Count; i++)
                {
                    entry.positions[i] = hints[i].position01;
                    entry.strengths[i] = hints[i].strength;
                    entry.kicks[i] = hints[i].kick;
                }
                string destination = PathFor(key);
                string temp = destination + ".tmp";
                File.WriteAllText(temp, JsonUtility.ToJson(entry));
                if (File.Exists(destination)) File.Delete(destination);
                File.Move(temp, destination);
                Trace("SAVE " + Path.GetFileNameWithoutExtension(destination).Substring(0, 10) +
                    " / guides=" + hints.Count + " / " + (variant ?? "original"));
                Prune();
            }
            catch (Exception ex)
            {
                // A full/unwritable disk must never prevent using the sample.
                UnityEngine.Debug.LogWarning("[Sampler] Hit guide cache write skipped: " + ex.Message);
            }
        }

        private static void Prune()
        {
            FileInfo[] files = new DirectoryInfo(CacheDir).GetFiles("*.json");
            Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            long size = 0;
            for (int i = 0; i < files.Length; i++)
            {
                size += files[i].Length;
                if (i >= MaxEntries || size > MaxTotalBytes)
                    files[i].Delete();
            }
        }
    }
}
