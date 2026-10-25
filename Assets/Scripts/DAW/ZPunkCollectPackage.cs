using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;
using KMusic;

namespace ZPunk.DAW
{
    /// <summary>
    /// Collects a point-in-time DAW handoff. Never moves or renames imported audio.
    /// The original native JSON is retained; Sample_Map.json records relocated assets.
    /// This package is not yet a directly importable native ZPUNK project.
    /// </summary>
    public sealed class ZPunkCollectPackage : IDisposable
    {
        [Serializable]
        private sealed class AssetEntry
        {
            public string role;
            public string sourceId;
            public string collectedPath;
        }
        [Serializable]
        private sealed class Manifest
        {
            public string format = "zpunk-collected-1";
            public string projectId;
            public string projectName;
            public string exportedUtc;
            public float bpm;
            public int numerator;
            public int denominator;
            public int sampleRate;
            public int slot;
            public List<AssetEntry> assets = new List<AssetEntry>();
        }

        private static readonly string[] Stems = { "DRUMS", "BASS", "SYNTH", "PADS", "CHOPS" };
        private static readonly string[] DrumNames = { "KICK", "SNARE", "CLAP", "HAT_CLOSED", "HAT_OPEN", "RIDE", "RIM", "CRASH" };
        private readonly string stage;
        private readonly string root;
        private readonly Manifest manifest;
        private readonly SongData song;
        private bool disposed;
        private readonly HashSet<string> captured = new HashSet<string>(StringComparer.Ordinal);
        public string DisplayName { get; private set; }
        public string StagingPath => root;
        public int StemCount => Stems.Length;

        // Clear only collector-owned temporary files. Never touch normal projects,
        // imported samples, existing ZIP exports, or unrelated files.
        public static void CleanupInterruptedCollections()
        {
            string exports = Path.Combine(Application.persistentDataPath, "Exports");
            if (!Directory.Exists(exports)) return;
            foreach (string directory in Directory.GetDirectories(exports, ".collect_*"))
            {
                try { Directory.Delete(directory, true); }
                catch (Exception ex) { Debug.LogWarning("[ZPUNK Collect] Cleanup: " + ex.Message); }
            }
            foreach (string partial in Directory.GetFiles(exports, "*.zip.partial"))
            {
                try { File.Delete(partial); }
                catch (Exception ex) { Debug.LogWarning("[ZPUNK Collect] Cleanup: " + ex.Message); }
            }
        }

        public static string SafeName(string name)
        {
            var b = new StringBuilder();
            foreach (char c in (name ?? "").Trim())
            {
                if (char.IsLetterOrDigit(c) && c <= 127) b.Append(c);
                else if (c == ' ' || c == '-' || c == '_') b.Append('_');
            }
            string clean = b.ToString().Trim('_');
            while (clean.Contains("__")) clean = clean.Replace("__", "_");
            if (clean.Length > 48) clean = clean.Substring(0, 48).TrimEnd('_');
            return string.IsNullOrEmpty(clean) ? "ZPUNK_PROJECT" : clean;
        }

        public ZPunkCollectPackage(string projectJson, SongData snapshot,
            string title, string projectId, int slot)
        {
            if (string.IsNullOrEmpty(projectJson) || snapshot == null || snapshot.tracks == null || snapshot.tracks.Count != 5)
                throw new InvalidDataException("Project snapshot is incomplete.");
            song = SongData.FromJson(snapshot.ToJson());
            DisplayName = SafeName(title);
            stage = Path.Combine(Application.persistentDataPath, "Exports", ".collect_" + Guid.NewGuid().ToString("N"));
            root = Path.Combine(stage, DisplayName);
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "Audio Stems"));
            Directory.CreateDirectory(Path.Combine(root, "MIDI"));
            Directory.CreateDirectory(Path.Combine(root, "Samples"));
            Directory.CreateDirectory(Path.Combine(root, "ZPUNK Project"));
            try
            {
                File.WriteAllText(Path.Combine(root, "ZPUNK Project", "Original_Project.json"), projectJson, Encoding.UTF8);
                File.WriteAllBytes(Path.Combine(root, "MIDI", DisplayName + ".mid"),
                    ZPunkArrangementMidiExport.Create(song));
                manifest = new Manifest {
                    projectId = projectId, projectName = title,
                    exportedUtc = DateTime.UtcNow.ToString("o"),
                    bpm = song.bpm, numerator = song.numerator, denominator = song.denominator,
                    sampleRate = AudioSettings.outputSampleRate, slot = slot
                };
            }
            catch { Dispose(); throw; }
        }

        public string MasterFile => Path.Combine(root, DisplayName + "_Master.wav");
        public string StemFile(int index)
        {
            if (index < 0 || index >= Stems.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return Path.Combine(root, "Audio Stems", Stems[index] + ".wav");
        }

        /// <summary>Include original user audio and drum-kit sources where they can be resolved.
        /// Missing material throws before any lengthy real-time stem bounce.</summary>
        public void CollectDependencies(KMusicPlayerUI player)
        {
            var sampleRefs = new HashSet<string>(StringComparer.Ordinal);
            if (song.tracks != null && song.tracks.Count > 4 && song.tracks[4] != null &&
                song.tracks[4].instrument != null)
            {
                string id = song.tracks[4].instrument.sampleResourcesPath;
                if (!string.IsNullOrWhiteSpace(id)) sampleRefs.Add(id);
            }
            // Use persisted project JSON's optional CHOPS source ID too, even when
            // the instrument has no authored clips yet (native project snapshot).
            string original = File.ReadAllText(Path.Combine(root, "ZPUNK Project", "Original_Project.json"));
            var src = JsonUtility.FromJson<CollectedProjectSources>(original);
            if (src != null)
            {
                if (src.chops != null && !string.IsNullOrWhiteSpace(src.chops.resourcesPath))
                    sampleRefs.Add(src.chops.resourcesPath);
                if (src.player != null && !string.IsNullOrWhiteSpace(src.player.lastTrackId))
                    sampleRefs.Add(src.player.lastTrackId);
            }
            foreach (string id in sampleRefs) CollectSample(KMusicPlayerUI.CanonicalSampleId(id), player);

            bool usesDrums = false;
            foreach (var e in SongEventPlanner.Build(song).samples)
                if (e.type == SongEventPlanner.EventType.Drum) { usesDrums = true; break; }
            if (usesDrums)
            {
                var kits = Resources.LoadAll<DrumKit>(
                    "KMusic/Drums/DrumKits"
                );
                int index = src != null && src.drums != null ? src.drums.kitIndex :
                    song.tracks[0].instrument != null ? song.tracks[0].instrument.drumKitIndex : 0;
                if (kits == null || kits.Length == 0)
                    throw new FileNotFoundException(
                        "No drum kits found in Resources/KMusic/Drums/DrumKits."
                    );

                if (index < 0 || index >= kits.Length)
                    throw new IndexOutOfRangeException(
                        $"Selected drum kit index {index} is invalid. " +
                        $"Available kits: {kits.Length}."
                    );
                var kit = kits[index];
                for (int lane = 0; lane < DrumNames.Length; lane++)
                {
                    var clip = kit.GetClipByDrumId(lane + 1);
                    if (clip == null) throw new FileNotFoundException("Drum kit missing " + DrumNames[lane]);
                    string rel = "Samples/Drums/" + DrumNames[lane] + ".wav";
                    WriteClipWav(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)), clip);
                    manifest.assets.Add(new AssetEntry { role = "drum", sourceId = DrumNames[lane], collectedPath = rel });
                }
            }
            File.WriteAllText(Path.Combine(root, "ZPUNK Project", "Sample_Map.json"),
                JsonUtility.ToJson(manifest, true), Encoding.UTF8);
        }

        [Serializable] private sealed class CollectedProjectSources
        {
            public SourceRef chops;
            public SourceRef player;
            public DrumRef drums;
        }
        [Serializable] private sealed class DrumRef { public int kitIndex; }
        [Serializable] private sealed class SourceRef
        {
            public string resourcesPath;
            public string lastTrackId;
        }

        private void CollectSample(string id, KMusicPlayerUI player)
        {
            if (string.IsNullOrWhiteSpace(id) || !captured.Add(id)) return;
            // Every distinct dependency receives its own stable package path.
            // Two projects or alternate source IDs must never overwrite one another.
            string rel = "Samples/CHOPS_Source_" + captured.Count.ToString("00");
            string sourceFile = null;
            if (id.StartsWith("local:", StringComparison.Ordinal))
            {
                string relative = id.Substring(6).Replace('/', Path.DirectorySeparatorChar);
                string localRoot = Path.GetFullPath(Application.persistentDataPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string candidate = Path.GetFullPath(Path.Combine(localRoot, relative));
                if (candidate.StartsWith(localRoot, StringComparison.Ordinal)) sourceFile = candidate;
            }
            else if (id.StartsWith("file:", StringComparison.Ordinal)) sourceFile = id.Substring(5);
            // Never treat resource IDs / content URIs as arbitrary filesystem paths.
            if (!string.IsNullOrEmpty(sourceFile) && File.Exists(sourceFile))
            {
                string extension = Path.GetExtension(sourceFile).ToLowerInvariant();
                if (extension != ".wav" && extension != ".mp3" && extension != ".ogg" &&
                    extension != ".flac" && extension != ".m4a" && extension != ".aac")
                    throw new InvalidDataException("Unrecognized sample file type: " + extension);
                rel += extension;
                File.Copy(sourceFile, Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)), false);
            }
            else
            {
                // URI media-store tracks are already loaded in the sampler; use
                // original decoded audio, not the cropped/warped CHOPS cache.
                AudioClip clip = player != null ? player.GetOriginalSampleForCollection(id) : null;
                if (clip == null && !id.StartsWith("uri:", StringComparison.Ordinal) &&
                    !id.StartsWith("local:", StringComparison.Ordinal) &&
                    !id.StartsWith("file:", StringComparison.Ordinal))
                {
                    string wanted = id.StartsWith("res:", StringComparison.Ordinal) ? id.Substring(4) : id;
                    clip = Resources.Load<AudioClip>(wanted);
                }
                if (clip == null)
                    throw new FileNotFoundException("Missing CHOPS source for Collect & Export: " + id);
                rel += ".wav";
                WriteClipWav(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)), clip);
            }
            manifest.assets.Add(new AssetEntry { role = "chops-original", sourceId = id, collectedPath = rel });
            // Preserve an already-rendered warp as a separate handoff sample.
            // Never substitute it for the original, whose chop coordinates are saved.
            AudioClip rendered = player != null ? player.GetRenderedWarpForCollection(id) : null;
            if (rendered != null)
            {
                string warpedRel = "Samples/CHOPS_Warped_" + captured.Count.ToString("00") + ".wav";
                WriteClipWav(Path.Combine(root, warpedRel.Replace('/', Path.DirectorySeparatorChar)), rendered);
                manifest.assets.Add(new AssetEntry { role = "chops-warped", sourceId = id, collectedPath = warpedRel });
            }
        }

        private static void WriteClipWav(string path, AudioClip clip)
        {
            if (clip == null || clip.samples < 1 || clip.channels < 1 || clip.channels > 2)
                throw new InvalidDataException("Unusable or unsupported source clip.");
            long count = (long)clip.samples * clip.channels;
            if (count > (int.MaxValue - 44L) / 2)
                throw new InvalidDataException("Collected WAV exceeds RIFF 2 GB limit.");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                int byteCount = checked((int)(count * 2));
                writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + byteCount);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
                writer.Write((short)1); writer.Write((short)clip.channels);
                writer.Write(clip.frequency); writer.Write(clip.frequency * clip.channels * 2);
                writer.Write((short)(clip.channels * 2)); writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(byteCount);
                int frame = 0;
                const int chunkFrames = 4096;
                float[] block = new float[chunkFrames * clip.channels];
                while (frame < clip.samples)
                {
                    int frames = Math.Min(chunkFrames, clip.samples - frame);
                    if (!clip.GetData(block, frame))
                        throw new IOException("Sample PCM cannot be decoded: " + clip.name);
                    for (int i = 0; i < frames * clip.channels; i++)
                    {
                        float v = Mathf.Clamp(block[i], -1f, 1f);
                        writer.Write((short)Mathf.RoundToInt(v * (v < 0 ? 32768f : 32767f)));
                    }
                    frame += frames;
                }
            }
        }

        public string FinalizeZip()
        {
            if (disposed) throw new ObjectDisposedException(nameof(ZPunkCollectPackage));
            // No implicit ZPUNK import promise: audio and MIDI open in all DAWs;
            // original project JSON retains its original app-local/URI references.
            File.WriteAllText(Path.Combine(root, "README.txt"),
                "ZPUNK COLLECT & EXPORT 2.2.0\n\n" +
                "Project: " + manifest.projectName + "\nBPM: " + song.bpm.ToString("0.##") +
                "\nTime signature: " + song.numerator + "/" + song.denominator +
                "\nProject ID: " + manifest.projectId + "\n\n" +
                "Import all WAV files in Audio Stems/ at bar 1; their starts are aligned.\n" +
                "The Master WAV is the full reference mix. MIDI contains notes/triggers only.\n" +
                "Samples/ contains the original CHOPS source, available rendered warps and selected drum-kit audio.\n" +
                "ZPUNK Project/Original_Project.json is an archival snapshot, not a desktop DAW session.\n" +
                "It may contain device-local sample references; consult Sample_Map.json for bundled replacements.\n" +
                "ZPUNK does not yet support importing collected packages automatically.\n" +
                "Helm presets/FX are rendered into the audio stems, not translated into DAW plugins.\n",
                Encoding.UTF8);
            string path = ZPunkMobileFileExport.CreatePrivateFilePath(DisplayName + "_ZPUNK", ".zip");
            string partial = path + ".partial";
            try
            {
                using (var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
                {
                    foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                    {
                        string local = file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar)
                            .Replace(Path.DirectorySeparatorChar, '/');
                        string entryPath = DisplayName + "/" + local;
                        var entry = zip.CreateEntry(entryPath, System.IO.Compression.CompressionLevel.Optimal);
                        using (var output = entry.Open())
                        using (var input = File.OpenRead(file)) input.CopyTo(output);
                    }
                }
                using (var validateStream = File.OpenRead(partial))
                using (var validate = new ZipArchive(validateStream, ZipArchiveMode.Read))
                {
                    if (validate.Entries.Count < 10)
                        throw new InvalidDataException("Collected ZIP is incomplete.");
                    foreach (var entry in validate.Entries)
                        if (entry.Length < 1 && !entry.FullName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("A collected entry was empty: " + entry.FullName);
                }
                File.Move(partial, path);
                return path;
            }
            catch
            {
                try { if (File.Exists(partial)) File.Delete(partial); } catch { }
                throw;
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
            catch (Exception e) { Debug.LogWarning("[ZPUNK Collect] Staging cleanup: " + e.Message); }
        }
    }
}
