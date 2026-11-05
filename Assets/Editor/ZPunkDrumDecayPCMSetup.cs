#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Unity cannot inspect silence in compressed/streamed AudioClips via GetData.
/// Prepare just the clips used by ZPUNK's drum kits so runtime Decay can
/// measure the actual audible area, including on iOS and Android.
/// This is explicitly run from the Tools menu; it never changes importers
/// unexpectedly when scripts are imported or while the app is playing.
/// </summary>
public static class ZPunkDrumDecayPCMSetup
{
    [MenuItem("Tools/ZPUNK/Drum Designer/Prepare Sample PCM for Decay")]
    public static void PrepareSamples()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var guid in AssetDatabase.FindAssets("t:DrumKit"))
        {
            var kit = AssetDatabase.LoadAssetAtPath<DrumKit>(AssetDatabase.GUIDToAssetPath(guid));
            if (kit == null) continue;
            for (int lane = 1; lane <= 8; ++lane)
            {
                var clip = kit.GetClipByDrumId(lane);
                if (clip == null) continue;
                string path = AssetDatabase.GetAssetPath(clip);
                if (!string.IsNullOrEmpty(path)) paths.Add(path);
            }
        }

        // Support legacy Resources-loaded drums as well as DrumKit references.
        string[] folders = { "Assets/Resources/KMusic/Drums", "Assets/Resources/DrumKits" };
        foreach (string folder in folders)
        {
            if (!AssetDatabase.IsValidFolder(folder)) continue;
            foreach (var guid in AssetDatabase.FindAssets("t:AudioClip", new[] { folder }))
                paths.Add(AssetDatabase.GUIDToAssetPath(guid));
        }

        int changed = 0, checkedClips = 0;
        try
        {
            foreach (var path in paths)
            {
                var importer = AssetImporter.GetAtPath(path) as AudioImporter;
                if (importer == null) continue;
                ++checkedClips;
                bool dirty = false;
                var defaults = importer.defaultSampleSettings;
                if (defaults.loadType != AudioClipLoadType.DecompressOnLoad || !defaults.preloadAudioData)
                {
                    defaults.loadType = AudioClipLoadType.DecompressOnLoad;
                    defaults.preloadAudioData = true;
                    importer.defaultSampleSettings = defaults;
                    dirty = true;
                }
                // If the user already has per-platform overrides, preserve the
                // compression quality and other options, but fix their load type.
                BuildTargetGroup[] platforms = { BuildTargetGroup.Android, BuildTargetGroup.iOS };
                foreach (var platform in platforms)
                {
                    if (!importer.ContainsSampleSettingsOverride(platform)) continue;
                    var settings = importer.GetOverrideSampleSettings(platform);
                    if (settings.loadType == AudioClipLoadType.DecompressOnLoad && settings.preloadAudioData)
                        continue;
                    settings.loadType = AudioClipLoadType.DecompressOnLoad;
                    settings.preloadAudioData = true;
                    if (importer.SetOverrideSampleSettings(platform, settings)) dirty = true;
                }
                if (!dirty) continue;
                ++changed;
                importer.SaveAndReimport();
            }
        }
        finally
        {
            Debug.Log($"[ZPUNK DECAY] Checked {checkedClips} kit drum sample(s); " +
                $"updated {changed} to Decompress On Load for accurate silence detection. " +
                "Enter Play mode again to let the runtime cache new audible lengths.");
        }
    }
}
#endif
