using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

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
            Debug.LogError($"[HelmPresetIndexBuilder] Missing folder: {root}\n" +
                           $"Create: Assets/StreamingAssets/{RelRoot}/ and put .helm files inside.");
            return;
        }

        var files = Directory.GetFiles(root, "*.helm", SearchOption.AllDirectories)
            .Select(p => p.Replace("\\", "/"))
            .Select(p => p.Substring(root.Replace("\\", "/").Length).TrimStart('/')) // relative to HelmPresets/
            .OrderBy(p => p)
            .ToArray();

        if (files.Length == 0)
        {
            Debug.LogWarning($"[HelmPresetIndexBuilder] No .helm files found under: {root}");
            return;
        }

        string indexPath = Path.Combine(root, IndexName);
        File.WriteAllLines(indexPath, files);

        Debug.Log($"[HelmPresetIndexBuilder] Wrote {files.Length} presets -> {indexPath}");
        AssetDatabase.Refresh();
    }
}