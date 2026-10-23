#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using ZPunk.DAW;

/// <summary>Quick Editor regression check for the persistent amber-guide cache.</summary>
public static class ZPunkSamplerStabilityChecks
{
    [MenuItem("ZPUNK/Diagnostics/Test Sampler Hit Cache")]
    public static void TestGuideCache()
    {
        AudioClip sample = AudioClip.Create("ZPunk Cache Test", 44100, 1, 44100, false);
        try
        {
            // Resource-style ID is stable without creating sample files on disk.
            const string id = "res:ZPunkSamplerCacheTest";
            string variant = "test-" + Guid.NewGuid().ToString("N");
            var hints = new List<ZPunkSampleWarp.TransientHint>
            {
                new ZPunkSampleWarp.TransientHint(.125f, true, 1.3f),
                new ZPunkSampleWarp.TransientHint(.625f, false, .7f)
            };
            ZPunkSampleAnalysisCache.Save(id, sample, sample, variant, hints);
            Check(ZPunkSampleAnalysisCache.TryLoad(id, sample, sample, variant, out var loaded),
                "saved hints should reload");
            Check(loaded.Count == 2 && loaded[0].kick && !loaded[1].kick &&
                Mathf.Abs(loaded[1].position01 - .625f) < .00001f,
                "hint positions and classifications must round-trip");
            Check(!ZPunkSampleAnalysisCache.TryLoad(id, sample, sample,
                variant + "-changed", out _), "warp changes must invalidate guides");
            ZPunkSampleAnalysisCache.Save(id, sample, sample, variant,
                new List<ZPunkSampleWarp.TransientHint>());
            Check(ZPunkSampleAnalysisCache.TryLoad(id, sample, sample, variant, out loaded)
                && loaded.Count == 0, "zero-hit analysis must be cached too");
            Debug.Log("[ZPUNK] Sampler cache checks passed (round-trip, warp invalidation, empty results).");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(sample);
        }
    }

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException("ZPUNK sampler cache test failed: " + message);
    }
}
#endif
