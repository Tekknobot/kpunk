#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Checks the source tree of the replacement mobile workspace. Runtime notch
/// and iOS text rendering must still be visually verified on device/simulator.
/// </summary>
public static class ZPunkSafeAreaWorkspaceChecks
{
    [MenuItem("ZPunk/2.0/Validate Safe Workspace")]
    public static void Validate()
    {
        var tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(
            "Assets/Resources/KMusic/UI/Main.uxml");
        if (tree == null) throw new Exception("Cannot load the ZPunk main UXML");
        var root = new VisualElement();
        tree.CloneTree(root);
        foreach (var name in new[] { "Root", "Pages", "PageArrangement", "PageSampler",
            "PageSeq", "PagePad", "PageSynth", "PageSynth2", "PlayButton",
            "StopButton", "NewFileButton", "LoadButton" })
        {
            if (root.Q<VisualElement>(name) == null)
                throw new Exception("Missing UI control: " + name);
        }
        var theme = AssetDatabase.LoadAssetAtPath<StyleSheet>("Assets/Resources/KMusic/UI/kmusic_dark.uss");
        if (theme == null) throw new Exception("Missing runtime UI theme stylesheet");
        if (AssetDatabase.LoadAssetAtPath<SceneAsset>("Assets/Scenes/Main.unity") == null)
            throw new Exception("Main scene missing");
        Debug.Log("[ZPunk 2.0] Safe workspace source check PASS. " +
            "Runtime navigation: Arrange / Create / Sound. " +
            "Verify device safe area, text, and playback in Play Mode.");
    }
}
#endif
