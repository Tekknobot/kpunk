#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using ZPunk.DAW;

/// <summary>
/// Standalone Unity Editor smoke test (no NUnit assembly required).
/// Run via ZPUNK > Diagnostics > Test Amber Marker Cache.
/// </summary>
public static class ZPunkHitCacheSelfTest
{
    [MenuItem("ZPUNK/Diagnostics/Test Amber Marker Cache")]
    public static void Run()
    {
        AudioClip clip = null;
        string testFile = Path.Combine(Application.persistentDataPath,
            "zp_hitcache_selftest_" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            // Cache's local file identity should invalidate on source edits.
            File.WriteAllBytes(testFile, new byte[] { 1, 2, 3, 4 });
            clip = AudioClip.Create("HitCacheSelfTest", 4096, 1, 44100, false);
            var samples = new float[4096];
            for (int i = 0; i < samples.Length; i++)
                samples[i] = Mathf.Sin(i * .025f) * .4f;
            if (!clip.SetData(samples, 0)) throw new Exception("Failed to prepare test clip");
            string id = "file:" + testFile;
            var expected = new List<ZPunkSampleWarp.TransientHint>
            {
                new ZPunkSampleWarp.TransientHint(.25f, true, 1.1f),
                new ZPunkSampleWarp.TransientHint(.75f, false, .7f)
            };
            ZPunkSampleAnalysisCache.Save(id, clip, clip, "original", expected);
            if (!ZPunkSampleAnalysisCache.TryLoad(id, clip, clip, "original", out var read) ||
                read.Count != 2 || Mathf.Abs(read[0].position01 - .25f) > .00001f)
                throw new Exception("Cache HIT after SAVE failed");
            var map = new List<ZPunkSampleWarp.TimeMapPoint>
            {
                new ZPunkSampleWarp.TimeMapPoint(0f, 0f),
                new ZPunkSampleWarp.TimeMapPoint(.5f, .6f),
                new ZPunkSampleWarp.TimeMapPoint(1f, 1f)
            };
            var moved = ZPunkSampleWarp.RemapTransientHints(read, map);
            if (Mathf.Abs(moved[0].position01 - .3f) > .00001f ||
                Mathf.Abs(moved[1].position01 - .8f) > .00001f ||
                Mathf.Abs(read[0].position01 - .25f) > .00001f)
                throw new Exception("Warp remapping altered source guides or produced wrong positions");
            // Changing underlying file size must force a MISS, even if the
            // AudioClip metadata (samples/channels/frequency) is unchanged.
            File.AppendAllText(testFile, "modified");
            if (ZPunkSampleAnalysisCache.TryLoad(id, clip, clip, "original", out _))
                throw new Exception("File modification should invalidate the cached guide key");
            Debug.Log("[ZPUNK/HitCache] SELF TEST PASS / SAVE -> HIT -> WARP REMAP -> SOURCE CHANGE MISS");
        }
        catch (Exception exception)
        {
            Debug.LogError("[ZPUNK/HitCache] SELF TEST FAILED: " + exception);
        }
        finally
        {
            if (clip != null) UnityEngine.Object.DestroyImmediate(clip);
            if (File.Exists(testFile)) File.Delete(testFile);
        }
    }
}
#endif
