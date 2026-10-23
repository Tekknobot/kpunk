using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace ZPunk.DAW
{
    /// <summary>
    /// Tiny, bounded on-disk cache of detected amber hit guides. Stores NO PCM.
    /// It is independent of user-authored CHOPS markers and project save data.
    /// Cache keys include the sample's identity, file modification, audio layout,
    /// and any warp settings that change detected guide positions.
    /// </summary>
    public static class ZPunkSampleAnalysisCache
    {
        private const int FormatVersion = 2; // bump when the detector changes
        private const int MaxEntries = 48;
        private const long MaxTotalBytes = 3L * 1024 * 1024;
        private const int MaxHints = 256;
        private static string CacheDir => Path.Combine(Application.persistentDataPath, "ZPunkAnalysisCache");

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
            else if (sampleId.StartsWith("res:", StringComparison.Ordinal) || !sampleId.Contains(":"))
                key.Append('|').Append(Application.version); // bundled samples change on app update
            // MediaStore content URI may stay the same; audio size/format also
            // participate in the key when its timestamp cannot be queried.
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
                if (key == null) return false;
                string path = PathFor(key);
                if (!File.Exists(path)) return false;
                var entry = JsonUtility.FromJson<CachedGuides>(File.ReadAllText(path));
                if (entry == null || entry.version != FormatVersion || entry.fingerprint != key ||
                    entry.positions == null || entry.kicks == null || entry.strengths == null ||
                    entry.positions.Length != entry.kicks.Length ||
                    entry.positions.Length != entry.strengths.Length ||
                    entry.positions.Length > MaxHints) return false;
                var hints = new List<ZPunkSampleWarp.TransientHint>(entry.positions.Length);
                for (int i = 0; i < entry.positions.Length; i++)
                {
                    float pos = entry.positions[i];
                    if (float.IsNaN(pos) || float.IsInfinity(pos) || pos < 0f || pos > 1f)
                        return false;
                    hints.Add(new ZPunkSampleWarp.TransientHint(pos, entry.kicks[i],
                        Mathf.Clamp(entry.strengths[i], .1f, 2f)));
                }
                result = hints;
                return true; // includes a valid zero-hit result
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Sampler] Hit guide cache read skipped: " + ex.Message);
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
                Prune();
            }
            catch (Exception ex)
            {
                // A full/unwritable disk must never prevent using the sample.
                Debug.LogWarning("[Sampler] Hit guide cache write skipped: " + ex.Message);
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
