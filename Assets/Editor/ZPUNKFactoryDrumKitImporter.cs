// ZPUNK Factory Drum Kit Importer
// Place this file under Assets/Editor. Open Tools > ZPUNK > Import Factory Drum Kits.
// Creates DrumKit ScriptableObject assets and wires the eight AudioClip slots automatically.
// Designed to be safe to re-run and never overwrite an unrelated pre-existing kit.
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class ZPUNKFactoryDrumKitImporter
{
    private const string ManifestPath = "Assets/ZPUNKKitFactory/KitManifest.json";
    private const string KitDirectory = "Assets/Resources/KMusic/Drums/DrumKits";

    [Serializable] private class KitFile { public KitSpec[] kits; }
    [Serializable] private class KitSpec
    {
        public string assetName, kitName, assetPath, kick, snare, clap, hatClosed, hatOpen, ride, rim, crash;
    }

    private static readonly string[] VoiceNames =
        { "kick", "snare", "clap", "hatClosed", "hatOpen", "ride", "rim", "crash" };

    [MenuItem("Tools/ZPUNK/Import Factory Drum Kits")]
    public static void ImportKits()
    {
        if (!File.Exists(ManifestPath))
        {
            Debug.LogError("ZPUNK kit manifest not found: " + ManifestPath);
            return;
        }
        KitFile manifest;
        try { manifest = JsonUtility.FromJson<KitFile>(File.ReadAllText(ManifestPath)); }
        catch (Exception ex) { Debug.LogError("Invalid drum-kit JSON: " + ex); return; }
        if (manifest == null || manifest.kits == null || manifest.kits.Length == 0)
        {
            Debug.LogError("ZPUNK drum-kit manifest is empty."); return;
        }

        Type drumKitType = LocateDrumKitType();
        if (drumKitType == null)
        {
            Debug.LogError("Cannot find a DrumKit ScriptableObject class. " +
                "Check that the existing DrumKit.cs compiles in the project.");
            return;
        }

        int created=0, updated=0, skipped=0;
        foreach (KitSpec spec in manifest.kits)
        {
            if (String.IsNullOrWhiteSpace(spec.assetName) || String.IsNullOrWhiteSpace(spec.kitName))
            { Debug.LogError("Missing assetName or kitName in manifest."); skipped++; continue; }
            string safeFile = spec.assetName + ".asset";
            if (spec.assetName.Contains("/") || spec.assetName.Contains("\\") || spec.assetName.Contains(".."))
            { Debug.LogError("Unsafe assetName: " + spec.assetName); skipped++; continue; }
            string assetPath = KitDirectory + "/" + safeFile;
            // Preserve the original Kit_01 ... Kit_11 and any user-created kit files.
            UnityEngine.Object occupied = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (occupied && !(occupied is ScriptableObject && occupied.GetType() == drumKitType))
            { Debug.LogError("Asset path occupied, not touching " + assetPath); skipped++; continue; }

            ScriptableObject target = occupied as ScriptableObject;
            bool isNew = target == null;
            if (!isNew && !HasMatchingName(target, spec.kitName))
            {
                Debug.LogError("Existing kit is not the expected factory kit, skipping: " + assetPath);
                skipped++; continue;
            }
            if (isNew)
            {
                target = ScriptableObject.CreateInstance(drumKitType);
                target.name = spec.assetName;
            }
            SerializedObject data = new SerializedObject(target);
            if (!TrySetKitName(data, spec.kitName))
            {
                Debug.LogError("No serialized Kit Name string field found on DrumKit.");
                if (isNew) UnityEngine.Object.DestroyImmediate(target);
                skipped++; continue;
            }

            bool ok=true;
            foreach (string slot in VoiceNames)
            {
                string wav = GetWavPath(spec, slot);
                AudioClip clip = String.IsNullOrEmpty(wav) ? null : AssetDatabase.LoadAssetAtPath<AudioClip>(wav);
                if (!clip) { Debug.LogError("Missing sample for " + spec.kitName + ": " + slot + " -> " + wav); ok=false; continue; }
                SerializedProperty prop = FindVoiceProperty(data, slot);
                if (prop == null)
                { Debug.LogError("Cannot find DrumKit AudioClip field " + slot + ". Inspect DrumKit.cs field names."); ok=false; continue; }
                prop.objectReferenceValue = clip;
            }
            if (!ok)
            {
                if (isNew) UnityEngine.Object.DestroyImmediate(target);
                skipped++; continue;
            }
            data.ApplyModifiedPropertiesWithoutUndo();
            if (isNew)
            {
                AssetDatabase.CreateAsset(target,assetPath);
                created++;
            }
            else { EditorUtility.SetDirty(target); updated++; }
        }
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"ZPUNK drum kits: {created} created, {updated} updated, {skipped} skipped. " +
                  "Original kits were preserved. Reopen the kit selector or restart Play Mode if needed.");
    }

    [MenuItem("Tools/ZPUNK/Validate Factory Drum Samples")]
    public static void ValidateSamples()
    {
        if (!File.Exists(ManifestPath)) { Debug.LogError("Missing manifest: " + ManifestPath); return; }
        KitFile manifest = JsonUtility.FromJson<KitFile>(File.ReadAllText(ManifestPath));
        int missing=0, checkedCount=0;
        foreach (KitSpec kit in manifest.kits)
        foreach (string key in VoiceNames)
        {
            string path = GetWavPath(kit,key);
            if (!AssetDatabase.LoadAssetAtPath<AudioClip>(path)) { Debug.LogError("Missing AudioClip: " + path); missing++; }
            checkedCount++;
        }
        Debug.Log($"ZPUNK samples: checked {checkedCount} assignments; {missing} missing.");
    }

    private static Type LocateDrumKitType()
    {
        string[] guids = AssetDatabase.FindAssets("t:DrumKit", new []{KitDirectory});
        foreach (string guid in guids)
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(guid)) as ScriptableObject;
            if (asset != null && asset.GetType().Name == "DrumKit") return asset.GetType();
        }
        return TypeCache.GetTypesDerivedFrom<ScriptableObject>()
                        .FirstOrDefault(t => t.Name == "DrumKit" && !t.IsAbstract);
    }
    private static string Norm(string s)
    {
        if (s == null) return "";
        return new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    }
    private static bool NameMatches(SerializedProperty p,string key)
    {
        string n=Norm(p.name), d=Norm(p.displayName), k=Norm(key);
        string[] probes = {n,d};
        foreach (string q in probes)
        {
            if (q == k || q == "m"+k || q == k+"clip" || q == k+"sample" || q == k+"audioclip") return true;
            if (key == "hatClosed" && new[]{"hihatclosed","closedhat","closedhihat","hihatc","hatc"}.Contains(q)) return true;
            if (key == "hatOpen" && new[]{"hihatopen","openhat","openhihat","hihato","hato"}.Contains(q)) return true;
        }
        return false;
    }
    private static SerializedProperty FindVoiceProperty(SerializedObject data,string name)
    {
        var it=data.GetIterator(); bool enter=true;
        while (it.NextVisible(enter))
        {
            enter=false;
            if (it.propertyType==SerializedPropertyType.ObjectReference && NameMatches(it,name)) return it.Copy();
        }
        return null;
    }
    private static SerializedProperty FindNameProperty(SerializedObject data)
    {
        var it=data.GetIterator(); bool enter=true;
        while (it.NextVisible(enter))
        {
            enter=false;
            if (it.propertyType!=SerializedPropertyType.String) continue;
            string n=Norm(it.name),d=Norm(it.displayName);
            if (new[]{"kitname","mkitname"}.Contains(n) || d=="kitname") return it.Copy();
        }
        return null;
    }
    private static bool TrySetKitName(SerializedObject obj,string name)
    {
        var prop=FindNameProperty(obj);
        if (prop==null) return false;
        prop.stringValue=name;
        return true;
    }
    private static bool HasMatchingName(ScriptableObject asset,string expected)
    {
        var p=FindNameProperty(new SerializedObject(asset));
        return p!=null && p.stringValue==expected;
    }
    private static string GetWavPath(KitSpec kit,string slot)
    {
        switch (slot)
        {
            case "kick":return kit.kick;
            case "snare":return kit.snare;
            case "clap":return kit.clap;
            case "hatClosed":return kit.hatClosed;
            case "hatOpen":return kit.hatOpen;
            case "ride":return kit.ride;
            case "rim":return kit.rim;
            case "crash":return kit.crash;
            default:return null;
        }
    }
}
#endif
