#if UNITY_EDITOR

using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Builds a preset index file used at runtime to load Helm presets.
/// Editor-only tool — safe for Android/iOS builds.
/// </summary>
public static class HelmPresetIndexBuilder
{
    private const string RelRoot = "HelmPresets";
    private const string IndexName = "presets_index.txt";

    [MenuItem("KMusic/Helm/Build Preset Index")]
    public static void BuildIndex()
    {
        string root = Path.Combine(Application.streamingAssetsPath, RelRoot);

        if (!Directory.Exists(root))
        {
            Debug.LogError(
                $"[HelmPresetIndexBuilder] Missing folder:\n{root}\n\n" +
                $"Create this folder and place your .helm files inside:\n" +
                $"Assets/StreamingAssets/{RelRoot}/"
            );
            return;
        }

        var files = Directory
            .GetFiles(root, "*.helm", SearchOption.AllDirectories)
            .Select(p => p.Replace("\\", "/"))
            .Select(p => p.Substring(root.Replace("\\", "/").Length).TrimStart('/')) // relative paths
            .OrderBy(p => p)
            .ToArray();

        if (files.Length == 0)
        {
            Debug.LogWarning($"[HelmPresetIndexBuilder] No .helm files found under:\n{root}");
            return;
        }

        string indexPath = Path.Combine(root, IndexName);
        File.WriteAllLines(indexPath, files);

        Debug.Log($"[HelmPresetIndexBuilder] ✔ Wrote {files.Length} presets →\n{indexPath}");

        AssetDatabase.Refresh();
    }
}

#endif