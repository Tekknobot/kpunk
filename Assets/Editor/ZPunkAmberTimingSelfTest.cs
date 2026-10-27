#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using ZPunk.DAW;

/// <summary>
/// Deterministic synthetic percussive timing check for ZPUNK 2.1.0.
/// Checks onset timing in SOURCE PCM and stereo anti-phase handling.
/// Does not require NUnit, external samples or a scene.
/// </summary>
public static class ZPunkAmberTimingSelfTest
{
    [MenuItem("ZPUNK/Diagnostics/Test Amber Hit Timing (2.1.0)")]
    public static void Run()
    {
        AudioClip normal = null, opposite = null;
        try
        {
            TestMode(false, out normal);
            TestMode(true, out opposite);
            Debug.Log("[ZPUNK/Amber] PASS / original PCM leading edges, rendered guide correction + anti-phase stereo");
        }
        catch (Exception error)
        {
            Debug.LogError("[ZPUNK/Amber] FAIL / " + error);
        }
        finally
        {
            if (normal != null) UnityEngine.Object.DestroyImmediate(normal);
            if (opposite != null) UnityEngine.Object.DestroyImmediate(opposite);
        }
    }

    private static void TestMode(bool invertRight, out AudioClip clip)
    {
        const int rate = 44100;
        const int seconds = 3;
        var starts = new float[] { .20f, .53f, .86f, 1.19f, 1.52f, 1.85f, 2.18f, 2.51f };
        int frames = rate * seconds;
        float[] pcm = new float[frames * 2];
        foreach (float when in starts)
        {
            int start = Mathf.RoundToInt(when * rate);
            for (int at = 0; at < Mathf.RoundToInt(rate * .085f) && start + at < frames; at++)
            {
                float elapsed = at / (float)rate;
                float rise = Mathf.Clamp01(elapsed / .002f);
                float decay = Mathf.Exp(-elapsed * 28f);
                float wave = Mathf.Sin(2f * Mathf.PI * 83f * elapsed + .65f) * .65f +
                    Mathf.Sin(2f * Mathf.PI * 1470f * elapsed) * .18f;
                float value = wave * rise * decay;
                pcm[(start + at) * 2] += value;
                pcm[(start + at) * 2 + 1] += invertRight ? -value : value;
            }
        }
        clip = AudioClip.Create(invertRight ? "AntiphaseHits" : "StereoHits",
            frames, 2, rate, false);
        if (!clip.SetData(pcm, 0)) throw new Exception("Synthetic AudioClip.SetData failed");

        List<ZPunkSampleWarp.TransientHint> hits = null;
        IEnumerator scan = ZPunkSampleWarp.AnalyzeTransientHints(clip, found => hits = found);
        // The scanner yields to Unity after each bounded PCM chunk; in an
        // Editor diagnostic, manually walking the enumerator is deterministic.
        while (scan.MoveNext()) { }
        if (hits == null) throw new Exception("Transient scanner did not complete");
        int matched = 0;
        float worstMs = 0f;
        foreach (float expected in starts)
        {
            float best = float.MaxValue;
            foreach (var hit in hits)
                best = Mathf.Min(best, Mathf.Abs(hit.position01 * clip.length - expected));
            if (best <= .008f)
            {
                matched++;
                worstMs = Mathf.Max(worstMs, best * 1000f);
            }
        }
        if (matched < 6)
            throw new Exception((invertRight ? "Antiphase" : "Normal") + " matched " +
                matched + "/" + starts.Length + " attacks within 8ms; found " + hits.Count + " hints");
        // Simulate the visual guide mismatch shown after stretching: the
        // theoretical map says each attack is 10-30ms later than its actual
        // rendered PCM. The finished display hint should follow actual audio.
        var mapped = new List<ZPunkSampleWarp.TransientHint>();
        for (int i = 0; i < starts.Length; ++i)
            mapped.Add(new ZPunkSampleWarp.TransientHint(
                (starts[i] + .010f + (i % 3) * .010f) / seconds, i % 2 == 0));
        var corrected = ZPunkSampleWarp.RefineRenderedTransientHints(clip, mapped);
        if (corrected.Count != starts.Length)
            throw new Exception("Rendered alignment changed guide count");
        float worstCorrectionMs = 0f;
        for (int i = 0; i < starts.Length; ++i)
        {
            float offset = Mathf.Abs(corrected[i].position01 * seconds - starts[i]);
            worstCorrectionMs = Mathf.Max(worstCorrectionMs, offset * 1000f);
            if (offset > .005f)
                throw new Exception("Rendered guide " + i + " is " +
                    (offset * 1000f).ToString("0.0") + "ms off the synthetic onset");
        }
        Debug.Log("[ZPUNK/Amber] " + (invertRight ? "ANTIPHASE" : "STEREO") +
            " / " + matched + "/" + starts.Length + " source hits <=8ms / " +
            "rendered mapped corrections <=5ms / worst=" +
            worstCorrectionMs.ToString("0.0") + "ms");
    }
}
#endif
