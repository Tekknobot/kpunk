using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace ZPunk.DAW
{
    /// <summary>Public-file delivery. Android 10+ writes Downloads/ZPunk via MediaStore;
    /// iOS presents the system Files document exporter; editor keeps a local copy.</summary>
    public static class ZPunkMobileFileExport
    {
#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void KMusic_OpenExportDocumentPicker(string path);
#endif
        public static string CreatePrivateFilePath(string title, string extension)
        {
            string clean = string.IsNullOrWhiteSpace(title) ? "ZPUNK_SONG" : title.Trim();
            foreach (char c in Path.GetInvalidFileNameChars()) clean = clean.Replace(c, '_');
            clean = clean.Replace('/', '_').Replace('\\', '_').Replace(':', '_');
            if (clean.Length > 48) clean = clean.Substring(0, 48);
            string path = Path.Combine(Application.persistentDataPath, "Exports");
            Directory.CreateDirectory(path);
            return Path.Combine(path, clean + "_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmssfff") + extension);
        }
        public static string Publish(string filePath)
        {
            if (!File.Exists(filePath)) throw new FileNotFoundException("Export file was not created", filePath);
#if UNITY_ANDROID && !UNITY_EDITOR
            using (var unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = unity.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var bridge = new AndroidJavaClass("com.zillatronics.kmusic.MediaStoreBridge"))
            {
                string mime = filePath.EndsWith(".mid", StringComparison.OrdinalIgnoreCase) ? "audio/midi" : "audio/wav";
                string result = bridge.CallStatic<string>("exportToDownloads", activity, filePath,
                    Path.GetFileName(filePath), mime);
                if (string.IsNullOrEmpty(result) || result.StartsWith("ERROR:", StringComparison.Ordinal))
                    throw new IOException(result ?? "Android export failed.");
                return "Saved in Files > Downloads > ZPunk";
            }
#elif UNITY_IOS && !UNITY_EDITOR
            KMusic_OpenExportDocumentPicker(filePath);
            return "Choose a location in Files, then tap Save.";
#else
            return "Saved: " + filePath;
#endif
        }
    }
}
