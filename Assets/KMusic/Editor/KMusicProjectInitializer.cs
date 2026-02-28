#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace KMusic.Editor
{
    [InitializeOnLoad]
    public static class KMusicProjectInitializer
    {
        static KMusicProjectInitializer()
        {
            EditorApplication.delayCall += EnsureScene;
        }

        private static void EnsureScene()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;
// Avoid re-running if scene exists and has KMusicApp.
            var scenePath = "Assets/Scenes/Main.unity";
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

            bool has = false;
            foreach (var go in Object.FindObjectsOfType<GameObject>())
            {
                if (go.GetComponent<KMusic.KMusicApp>() != null)
                {
                    has = true;
                    break;
                }
            }

            if (!has)
            {
                var go = new GameObject("KMusicApp");
                go.AddComponent<UIDocument>();
                go.AddComponent<KMusic.KMusicApp>();
            }

            EditorSceneManager.SaveScene(scene, scenePath);

            // Ensure in Build Settings
            var scenes = EditorBuildSettings.scenes;
            bool inList = false;
            foreach (var s in scenes) if (s.path == scenePath) inList = true;
            if (!inList)
            {
                var list = new System.Collections.Generic.List<EditorBuildSettingsScene>(scenes);
                list.Insert(0, new EditorBuildSettingsScene(scenePath, true));
                EditorBuildSettings.scenes = list.ToArray();
            }
        }
    }
}
#endif
