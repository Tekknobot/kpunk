
#if UNITY_IOS
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;

public static class ZPunkIOSFrameworks
{
    [PostProcessBuild(100)]
    public static void AddRequiredFrameworks(
        BuildTarget target, string buildPath)
    {
        if (target != BuildTarget.iOS)
            return;

        string path = PBXProject.GetPBXProjectPath(buildPath);

        var project = new PBXProject();
        project.ReadFromFile(path);

        string frameworkTarget =
            project.GetUnityFrameworkTargetGuid();

        project.AddFrameworkToProject(
            frameworkTarget,
            "UniformTypeIdentifiers.framework",
            false
        );

        project.WriteToFile(path);
    }
}
#endif
