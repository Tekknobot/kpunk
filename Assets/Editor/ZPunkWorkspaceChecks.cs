#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>Audits the UI tree wired to Main.unity. Does not modify a scene.</summary>
public static class ZPunkWorkspaceChecks
{
    [MenuItem("ZPUNK/Diagnostics/2.0/Validate Workspace UI")]
    public static void ValidateWorkspace()
    {
        const string source = "Assets/Resources/KMusic/UI/Main.uxml";
        var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(source);
        if (uxml == null) throw new Exception("Missing UXML: " + source);
        var tree = new VisualElement();
        uxml.CloneTree(tree);
        string[] names = {
            "Root", "Pages", "PageArrangement", "PageSampler", "PageSeq",
            "PagePad", "PageSynth", "PageSynth2", "PageFx",
            "PlayButton", "StopButton", "NewFileButton", "LoadButton",
            "DrumStepGrid"
        };
        foreach (var name in names)
            if (tree.Q<VisualElement>(name) == null)
                throw new Exception("ZPunk workspace UXML is missing: " + name);
        var scene = AssetDatabase.LoadAssetAtPath<SceneAsset>("Assets/Scenes/Main.unity");
        if (scene == null) throw new Exception("Main.unity missing");
        Debug.Log("[ZPunk 2.0] Workspace UI PASS: correct UXML contains " +
            "ARRANGE page, original instrument pages, and transport buttons. " +
            "Navigation dock is constructed by KMusicTabsUI at runtime.");
    }
}
#endif
