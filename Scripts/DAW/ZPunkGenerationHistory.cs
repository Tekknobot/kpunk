using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ZPunk.DAW
{
    /// <summary>Stores exact generated MIDI/drum snapshots per project, track, genre and mode.</summary>
    internal static class ZPunkGenerationHistory
    {
        private const int MaximumEntries = 12;
        [Serializable]
        internal sealed class Entry
        {
            public int style, key, variation, bars;
            public bool minor, chords;
            public string createdUtc;
            public ClipContentData snapshot;
        }
        [Serializable]
        private sealed class Store { public List<Entry> entries = new List<Entry>(); }

        private static string PathFor(int slot, TrackKind kind, int genre, bool chords)
        {
            string folder = Path.Combine(Application.persistentDataPath, "ZPunkGeneratorHistory");
            return Path.Combine(folder, "project_" + Mathf.Clamp(slot, 0, 999).ToString("000") +
                "_track_" + (int)kind + "_genre_" + Mathf.Clamp(genre, 0, ZPunkGenreGenerator.Genres.Length - 1) +
                (chords ? "_chords" : "_notes") + ".json");
        }
        internal static List<Entry> Read(int slot, TrackKind kind, int genre, bool chords)
        {
            try
            {
                string path = PathFor(slot, kind, genre, chords);
                if (!File.Exists(path)) return new List<Entry>();
                Store store = JsonUtility.FromJson<Store>(File.ReadAllText(path));
                return store != null && store.entries != null ? store.entries : new List<Entry>();
            }
            catch (Exception exception)
            {
                Debug.LogWarning("ZPUNK generator history could not be read: " + exception.Message);
                return new List<Entry>();
            }
        }
        internal static void Add(int slot, TrackKind kind, int genre, int style,
                                 int key, bool minor, int bars, int variation, bool chords,
                                 ClipContentData generated)
        {
            if (generated == null) return;
            string path = PathFor(slot, kind, genre, chords);
            try
            {
                var entries = Read(slot, kind, genre, chords);
                entries.Insert(0, new Entry {
                    style = style, key = key, minor = minor, bars = bars, variation = variation,
                    chords = chords, createdUtc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm"),
                    snapshot = JsonUtility.FromJson<ClipContentData>(JsonUtility.ToJson(generated))
                });
                if (entries.Count > MaximumEntries) entries.RemoveRange(MaximumEntries, entries.Count - MaximumEntries);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonUtility.ToJson(new Store { entries = entries }));
                // Write completed JSON before changing the visible history file.
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("ZPUNK generator history could not be saved: " + exception.Message);
            }
        }
        // Delete only the selected history row. Never edit the current clip.
        // Index is the order returned by Read (newest first).
        internal static bool Delete(int slot, TrackKind kind, int genre, bool chords, int index)
        {
            string path = PathFor(slot, kind, genre, chords);
            try
            {
                var entries = Read(slot, kind, genre, chords);
                if (index < 0 || index >= entries.Count) return false;
                entries.RemoveAt(index);
                string directory = Path.GetDirectoryName(path);
                Directory.CreateDirectory(directory);
                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonUtility.ToJson(new Store { entries = entries }));
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("ZPUNK generator history could not delete entry: " + exception.Message);
                return false;
            }
        }

        internal static ClipContentData Copy(Entry entry)
        {
            return entry == null || entry.snapshot == null ? null :
                JsonUtility.FromJson<ClipContentData>(JsonUtility.ToJson(entry.snapshot));
        }
    }
}
