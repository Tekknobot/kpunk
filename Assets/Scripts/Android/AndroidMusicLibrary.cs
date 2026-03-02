// Assets/Scripts/Android/AndroidMusicLibrary.cs
// Runtime MediaStore music library access for Android.
//
// Usage:
//   - Call AndroidMusicLibrary.EnsurePermissionThenRefresh(MonoBehaviour host, Action<bool> onDone)
//     onDone(true) == permission granted + query attempted (even if 0 tracks)
//     onDone(false) == permission denied or Android JNI/bridge failed
//   - Then use AndroidMusicLibrary.Tracks + LoadTrackToClip coroutine.

using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace KMusic.Android
{
    [Serializable]
    public class TrackInfo
    {
        public string title;
        public string artist;
        public string album;
        public long durationMs;
        public string uri;
        public string mime;
    }

    [Serializable]
    public class TrackList
    {
        public TrackInfo[] tracks;
        public string error;
        // (optional if Java adds it later)
        public int count;
        public string note;
    }

    public static class AndroidMusicLibrary
    {
        public static TrackInfo[] Tracks { get; private set; } = Array.Empty<TrackInfo>();

        // ✅ Visible debug strings (use these in UI to see what's happening on-device)
        public static string DebugLastStatus { get; private set; } = "";
        public static string DebugLastJsonHead { get; private set; } = "";

#if UNITY_ANDROID && !UNITY_EDITOR
        private const string JavaClassName = "com.zillatronics.kmusic.MediaStoreBridge";
#endif

        /// <summary>
        /// Requests runtime permission then queries MediaStore.
        /// onDone(true) means permission granted and query ran (even if 0 results).
        /// onDone(false) means permission denied or query failed.
        /// </summary>
        public static void EnsurePermissionThenRefresh(MonoBehaviour host, Action<bool> onDone, int limit = 250)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (host == null)
            {
                DebugLastStatus = "host is null";
                onDone?.Invoke(false);
                return;
            }
            host.StartCoroutine(CoEnsurePermissionThenRefresh(onDone, limit));
#else
            Tracks = Array.Empty<TrackInfo>();
            DebugLastStatus = "Not Android build";
            DebugLastJsonHead = "";
            onDone?.Invoke(false);
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private static IEnumerator CoEnsurePermissionThenRefresh(Action<bool> onDone, int limit)
        {
            DebugLastStatus = "Starting permission check…";
            DebugLastJsonHead = "";

            // Determine permission string at runtime
            string perm = GetReadAudioPermission();
            DebugLastStatus = "Need perm: " + perm;

            if (!Permission.HasUserAuthorizedPermission(perm))
            {
                Permission.RequestUserPermission(perm);

                // Poll a bit for permission to update.
                float t = 0f;
                while (t < 6.0f && !Permission.HasUserAuthorizedPermission(perm))
                {
                    t += Time.unscaledDeltaTime;
                    yield return null;
                }
            }

            if (!Permission.HasUserAuthorizedPermission(perm))
            {
                DebugLastStatus = "Permission denied: " + perm;
                Tracks = Array.Empty<TrackInfo>();
                onDone?.Invoke(false);
                yield break;
            }

            DebugLastStatus = "Permission OK. Querying MediaStore…";

            TrackList list = null;
            try
            {
                list = QueryTracks(limit);
            }
            catch (Exception e)
            {
                DebugLastStatus = "QueryTracks exception: " + e.GetType().Name;
                Tracks = Array.Empty<TrackInfo>();
                onDone?.Invoke(false);
                yield break;
            }

            Tracks = list?.tracks ?? Array.Empty<TrackInfo>();

            // Put something human-readable into status.
            if (!string.IsNullOrEmpty(list?.error))
            {
                DebugLastStatus = "MediaStore error: " + list.error;
            }
            else
            {
                DebugLastStatus = $"MediaStore OK. tracks={Tracks.Length}";
                if (!string.IsNullOrEmpty(list?.note))
                    DebugLastStatus += " (" + list.note + ")";
            }

            // IMPORTANT: return true because permission is granted and query succeeded,
            // even if 0 tracks (so PlayerUI can show the real reason rather than silently falling back).
            onDone?.Invoke(true);
        }

        public static int SdkInt()
        {
            try
            {
                using var version = new AndroidJavaClass("android.os.Build$VERSION");
                return version.GetStatic<int>("SDK_INT");
            }
            catch { return 0; }
        }

        public static string GetReadAudioPermission()
        {
            int sdk = SdkInt();
            // Android 13+ (API 33) uses granular media perms.
            return (sdk >= 33) ? "android.permission.READ_MEDIA_AUDIO" : "android.permission.READ_EXTERNAL_STORAGE";
        }

        private static TrackList QueryTracks(int limit)
        {
            try
            {
                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");

                using var bridge = new AndroidJavaClass(JavaClassName);
                string json = bridge.CallStatic<string>("queryMusicJson", activity, limit);

                if (string.IsNullOrEmpty(json))
                {
                    DebugLastJsonHead = "(empty json)";
                    return new TrackList { tracks = Array.Empty<TrackInfo>(), error = "Empty response from queryMusicJson" };
                }

                DebugLastJsonHead = json.Substring(0, Mathf.Min(240, json.Length));

                var parsed = JsonUtility.FromJson<TrackList>(json);
                if (parsed == null)
                    return new TrackList { tracks = Array.Empty<TrackInfo>(), error = "JsonUtility.FromJson returned null" };

                return parsed;
            }
            catch (Exception e)
            {
                DebugLastJsonHead = "(exception)";
                return new TrackList { tracks = Array.Empty<TrackInfo>(), error = e.ToString() };
            }
        }

        /// <summary>
        /// Copies a MediaStore content:// uri into app cache and returns absolute file path.
        /// </summary>
        public static string CopyUriToCache(string uri)
        {
            try
            {
                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                using var bridge = new AndroidJavaClass(JavaClassName);
                return bridge.CallStatic<string>("copyUriToCache", activity, uri) ?? "";
            }
            catch (Exception e)
            {
                DebugLastStatus = "CopyUriToCache failed: " + e.GetType().Name;
                Debug.LogWarning("[AndroidMusicLibrary] CopyUriToCache failed: " + e);
                return "";
            }
        }

        /// <summary>
        /// Coroutine: copies selected track into cache, then loads it as an AudioClip via UnityWebRequest.
        /// </summary>
        public static IEnumerator LoadTrackToClip(TrackInfo t, Action<AudioClip> onLoaded)
        {
            if (t == null || string.IsNullOrEmpty(t.uri))
            {
                onLoaded?.Invoke(null);
                yield break;
            }

            string path = CopyUriToCache(t.uri);
            if (string.IsNullOrEmpty(path))
            {
                Debug.LogWarning("[AndroidMusicLibrary] Cache copy failed for uri=" + t.uri);
                onLoaded?.Invoke(null);
                yield break;
            }

            string url = "file://" + path.Replace("\\", "/");
            AudioType type = GuessAudioTypeFromPath(path);

            using UnityWebRequest req = UnityWebRequestMultimedia.GetAudioClip(url, type);

            // ✅ Critical for chopping/slicing:
            // We need a fully-decompressed PCM clip so timeSamples seeking and slicing behaves.
            // UnityWebRequest audio clips can default to streaming/compressed on some platforms.
            if (req.downloadHandler is DownloadHandlerAudioClip dh)
            {
                dh.streamAudio = false;
                dh.compressed = false;
            }

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning("[AndroidMusicLibrary] LoadAudioClip failed: " + req.error + " url=" + url);
                onLoaded?.Invoke(null);
                yield break;
            }

            var clip = DownloadHandlerAudioClip.GetContent(req);
            onLoaded?.Invoke(clip);
        }

        private static AudioType GuessAudioTypeFromPath(string path)
        {
            string ext = Path.GetExtension(path)?.ToLowerInvariant();
            switch (ext)
            {
                case ".mp3": return AudioType.MPEG;
                case ".wav": return AudioType.WAV;
                case ".ogg": return AudioType.OGGVORBIS;
                case ".m4a":
                case ".aac":
                case ".mp4": return AudioType.MPEG;
                default: return AudioType.UNKNOWN;
            }
        }
#endif
    }
}