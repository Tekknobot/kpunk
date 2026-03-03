using System;
using UnityEngine;

namespace KMusic.Core
{
    /// <summary>
    /// Project-slot namespaced PlayerPrefs wrapper.
    /// Ensures each "project slot" has fully isolated prefs keys:
    ///   kmusic.p005.<rest_of_key>
    ///
    /// Global prefs (not namespaced) should only be used for true app-wide settings
    /// like remembering the last selected slot.
    /// </summary>
    public static class ProjectPrefs
    {
        // This key intentionally remains GLOBAL so we can discover the active slot
        // before any scene objects initialize.
        public const string GlobalSlotKey = "kmusic.project.slot.v1";

        private static bool _inited;
        private static int _activeSlot;

        private static void EnsureInit()
        {
            if (_inited) return;
            _activeSlot = PlayerPrefs.GetInt(GlobalSlotKey, 0);
            if (_activeSlot < 0) _activeSlot = 0;
            _inited = true;
        }

        public static int ActiveSlot
        {
            get { EnsureInit(); return _activeSlot; }
        }

        /// <summary>
        /// Sets the active slot for namespacing AND updates the global slot key.
        /// Call this from your ProjectManager when switching/loading slots.
        /// </summary>
        public static void SetActiveSlot(int slot)
        {
            EnsureInit();
            if (slot < 0) slot = 0;
            _activeSlot = slot;

            PlayerPrefs.SetInt(GlobalSlotKey, _activeSlot);
            PlayerPrefs.Save();
        }

        public static string Key(string baseKey)
        {
            EnsureInit();
            string prefix = $"kmusic.p{_activeSlot:000}.";

            if (string.IsNullOrEmpty(baseKey))
                return prefix;

            if (baseKey.StartsWith("kmusic.", StringComparison.OrdinalIgnoreCase))
                return prefix + baseKey.Substring("kmusic.".Length);

            return prefix + baseKey;
        }

        // ----------------------------
        // Namespaced wrappers
        // ----------------------------

        public static bool HasKey(string baseKey) => PlayerPrefs.HasKey(Key(baseKey));
        public static void DeleteKey(string baseKey) => PlayerPrefs.DeleteKey(Key(baseKey));

        public static void SetString(string baseKey, string value) => PlayerPrefs.SetString(Key(baseKey), value ?? "");
        public static string GetString(string baseKey, string defaultValue = "") => PlayerPrefs.GetString(Key(baseKey), defaultValue ?? "");

        public static void SetInt(string baseKey, int value) => PlayerPrefs.SetInt(Key(baseKey), value);
        public static int GetInt(string baseKey, int defaultValue = 0) => PlayerPrefs.GetInt(Key(baseKey), defaultValue);

        public static void SetFloat(string baseKey, float value) => PlayerPrefs.SetFloat(Key(baseKey), value);
        public static float GetFloat(string baseKey, float defaultValue = 0f) => PlayerPrefs.GetFloat(Key(baseKey), defaultValue);

        public static void Save() => PlayerPrefs.Save();

        // ----------------------------
        // Global (non-namespaced) wrappers
        // ----------------------------

        public static bool HasGlobalKey(string key) => PlayerPrefs.HasKey(key);
        public static void DeleteGlobalKey(string key) => PlayerPrefs.DeleteKey(key);

        public static void SetGlobalString(string key, string value) => PlayerPrefs.SetString(key, value ?? "");
        public static string GetGlobalString(string key, string defaultValue = "") => PlayerPrefs.GetString(key, defaultValue ?? "");

        public static void SetGlobalInt(string key, int value) => PlayerPrefs.SetInt(key, value);
        public static int GetGlobalInt(string key, int defaultValue = 0) => PlayerPrefs.GetInt(key, defaultValue);

        public static void SetGlobalFloat(string key, float value) => PlayerPrefs.SetFloat(key, value);
        public static float GetGlobalFloat(string key, float defaultValue = 0f) => PlayerPrefs.GetFloat(key, defaultValue);

        public static void SaveGlobal() => PlayerPrefs.Save();
    }
}
