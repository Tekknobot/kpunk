using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using AudioHelm;
using KMusic;
using KMusic.UI;
using UnityEngine;
using UnityEngine.Networking;

namespace ZPunk.DAW
{
    /// <summary>
    /// Owns the third Audio Helm output (Bass channel 2). Main Synth and Pads are
    /// existing scene instruments. Each instrument has independent native audio.
    /// </summary>
    [DefaultExecutionOrder(-75)]
    public sealed class ZPunkInstrumentRack : MonoBehaviour
    {
        private HelmController bass;
        private HelmPatch bassPatch;
        private readonly List<string> bassPresets=new List<string>();
        // Factory presets remain on disk for projects saved before the curated ZOR bank.
        private string legacyBassPresetPath;
        private bool loadingLegacyBass;
        private static bool IsLegacyBassPath(string path)
        {
            return !string.IsNullOrEmpty(path) && !path.Contains("..") &&
                !path.Contains("\\") && !path.StartsWith("/") &&
                path.StartsWith("Bass/", StringComparison.OrdinalIgnoreCase) &&
                path.EndsWith(".helm", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith("Bass/ZOR ", StringComparison.OrdinalIgnoreCase);
        }
        private int bassPresetIndex=-1;
        private KMusicProjectManager projects;
        private int lastSlot=-1;
        private bool presetsReady;
        private SongData lastSong;
        private int loadRevision;
        private int loadingPresetIndex = -1;
        public HelmController BassController => bass;
        // UI label only: preserve full Bass/ZOR ... .helm path for loading and project saves.
        // Example: Bass/ZOR Techno Acid 303.helm -> ZOR Techno Acid 303
        public string BassPresetName => !string.IsNullOrEmpty(legacyBassPresetPath)
            ? BassPresetDisplayName(legacyBassPresetPath) + " (Legacy)"
            : bassPresetIndex>=0 && bassPresetIndex<bassPresets.Count
                ? BassPresetDisplayName(bassPresets[bassPresetIndex]) : "INITIALIZING";

        private static string BassPresetDisplayName(string relPath)
        {
            if (string.IsNullOrEmpty(relPath)) return "";
            string normalized = relPath.Replace('\\', '/');
            int slash = normalized.LastIndexOf('/');
            string filename = slash >= 0 ? normalized.Substring(slash + 1) : normalized;
            return filename.EndsWith(".helm", StringComparison.OrdinalIgnoreCase)
                ? filename.Substring(0, filename.Length - 5) : filename;
        }
        public int BassPresetCount => bassPresets.Count;
        public int BassPresetIndex => bassPresetIndex;
        public string CurrentBassPresetRelPath => !string.IsNullOrEmpty(legacyBassPresetPath)
            ? legacyBassPresetPath : (bassPresetIndex >= 0 && bassPresetIndex < bassPresets.Count
                ? bassPresets[bassPresetIndex] : null);
        public bool BassPresetsReady => presetsReady && bass != null && loadingPresetIndex < 0 && !loadingLegacyBass;

        // Project restore must be able to request a preset even when it is the
        // current preset, without changing the arrangement or saving a new choice.
        public bool RequestProjectBassPreset(string relPath)
        {
            if (!presetsReady || bassPresets.Count == 0 || string.IsNullOrEmpty(relPath))
                return false;
            int index = bassPresets.FindIndex(x =>
                string.Equals(x, relPath, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                if (!IsLegacyBassPath(relPath)) return false;
                if (string.Equals(legacyBassPresetPath, relPath, StringComparison.OrdinalIgnoreCase)
                    && !loadingLegacyBass) return true;
                StartCoroutine(LoadBassPresetPath(relPath, -1, false));
                return true;
            }
            if (string.IsNullOrEmpty(legacyBassPresetPath) &&
                (loadingPresetIndex == index || (bassPresetIndex == index && loadingPresetIndex < 0)))
                return true;
            StartCoroutine(LoadBassPreset(index, false));
            return true;
        }
        public event Action PresetChanged;

        private void Awake()
        {
            var existing=GameObject.Find("ZPunkBassSynth");
            if(existing==null)
            {
                var prefab=Resources.Load<GameObject>("ZPunkBassSynth");
                if(prefab==null){Debug.LogError("[ZPunk] Bass synth prefab missing from Resources.");return;}
                existing=Instantiate(prefab,Vector3.zero,Quaternion.identity);
                existing.name="ZPunkBassSynth";
            }
            bass=existing.GetComponent<HelmController>();
            if(bass!=null)bass.channel=2;
            var seq = existing.GetComponent<HelmSequencer>();
            if (seq != null)
            {
                seq.enabled = false;
            }
            bassPatch=existing.GetComponent<HelmPatch>();
            if(bassPatch==null)bassPatch=existing.AddComponent<HelmPatch>();
        }
        private IEnumerator Start()
        {
            projects=FindObjectOfType<KMusicProjectManager>();
            string index=Path.Combine(Application.streamingAssetsPath,"HelmPresets/presets_index.txt");
            string fileText=null;
#if UNITY_ANDROID && !UNITY_EDITOR
            using(var request=UnityWebRequest.Get(index))
            {
                yield return request.SendWebRequest();
                if(request.result==UnityWebRequest.Result.Success)fileText=request.downloadHandler.text;
            }
#else
            if(File.Exists(index))fileText=File.ReadAllText(index);
#endif
            if(!string.IsNullOrEmpty(fileText))
                foreach(var raw in fileText.Split(new[]{'\n','\r'},StringSplitOptions.RemoveEmptyEntries))
                {
                    string path=raw.Trim().Replace('\\','/');
                    if(path.StartsWith("Bass/ZOR ",StringComparison.OrdinalIgnoreCase) &&
                        path.EndsWith(".helm",StringComparison.OrdinalIgnoreCase))
                        bassPresets.Add(path);
                }
            if(bassPresets.Count==0)
                Debug.LogWarning("[ZPunk] Bass preset list empty; instrument can still play MIDI.");
            int choice=0;
            string preferredLegacy=null;
            var song=projects!=null?projects.GetSongForArrangementEditor():null;
            if(song!=null && song.tracks!=null && song.tracks.Count>1 && song.tracks[1]!=null)
            {
                string preferred=song.tracks[1].instrument?.presetRelPath;
                int found=bassPresets.FindIndex(x=>string.Equals(x,preferred,StringComparison.OrdinalIgnoreCase));
                if(found>=0)choice=found;
                else if(IsLegacyBassPath(preferred))preferredLegacy=preferred;
            }
            if(!string.IsNullOrEmpty(preferredLegacy))
                yield return LoadBassPresetPath(preferredLegacy,-1,false);
            else if(bassPresets.Count>0)yield return LoadBassPreset(choice,false);
            lastSlot=projects!=null?projects.CurrentSlot:-1;
            lastSong=projects!=null?projects.CurrentSong:null;
            presetsReady=true;
        }
        private void Update()
        {
            // When opening a different project, restore its independent Bass preset.
            if(!presetsReady || projects==null)return;
            var song=projects.CurrentSong;
            if(lastSlot==projects.CurrentSlot && ReferenceEquals(lastSong,song))return;
            lastSlot=projects.CurrentSlot;
            lastSong=song;
            if(song==null || song.tracks==null || song.tracks.Count<2 || song.tracks[1]==null)return;
            var instrument=song.tracks[1].instrument;
            if(instrument==null || string.IsNullOrEmpty(instrument.presetRelPath))return;
            RequestProjectBassPreset(instrument.presetRelPath);
        }
        public HelmController Controller(TrackKind kind)
        {
            if(kind==TrackKind.Bass)return bass;
            if(kind==TrackKind.Synth)
            {
                // The preset picker is the authoritative owner of the Synth
                // patch/channel. The piano-roll may reference another controller
                // during reinitialization; SOUND must edit the preset's engine.
                var picker=FindObjectOfType<KMusicHelmPresetPickerUI>();
                if(picker!=null && picker.isActiveAndEnabled && picker.ActiveHelmController!=null)
                    return picker.ActiveHelmController;
                var painter=FindObjectOfType<KMusicPianoRollStepPainter>();
                return painter!=null?painter.ActiveHelmController:null;
            }
            if(kind==TrackKind.Pads)
            {
                var pads=FindObjectOfType<KMusicPadSynthRuntime>();
                return pads!=null?pads.ActiveHelmController:null;
            }
            return null;
        }
        public void StepBassPreset(int delta)
        {
            if(bassPresets.Count==0)return;
            int current=loadingPresetIndex>=0?loadingPresetIndex:bassPresetIndex;
            int next=!string.IsNullOrEmpty(legacyBassPresetPath)
                ? (delta>=0 ? 0 : bassPresets.Count-1)
                : ((current+delta)%bassPresets.Count+bassPresets.Count)%bassPresets.Count;
            StartCoroutine(LoadBassPreset(next,true));
        }
        private IEnumerator LoadBassPreset(int index,bool persist)
        {
            if(bassPresets.Count==0)yield break;
            int choice=Mathf.Clamp(index,0,bassPresets.Count-1);
            yield return LoadBassPresetPath(bassPresets[choice],choice,persist);
        }
        private IEnumerator LoadBassPresetPath(string presetRelPath,int choice,bool persist)
        {
            if(bass==null || bassPatch==null || string.IsNullOrEmpty(presetRelPath))yield break;
            int revision=++loadRevision;
            loadingPresetIndex=choice;
            loadingLegacyBass=choice<0;
            var relative="HelmPresets/"+presetRelPath;
            var location=Path.Combine(Application.streamingAssetsPath,relative);
            string json=null;
#if UNITY_ANDROID && !UNITY_EDITOR
            using(var request=UnityWebRequest.Get(location))
            {
                yield return request.SendWebRequest();
                if(request.result==UnityWebRequest.Result.Success)json=request.downloadHandler.text;
            }
#else
            if(File.Exists(location))json=File.ReadAllText(location);
#endif
            if(revision!=loadRevision)yield break;
            if(string.IsNullOrEmpty(json)) { loadingPresetIndex=-1; loadingLegacyBass=false; yield break; }
            HelmPatchFormat patch;
            try { patch=JsonUtility.FromJson<HelmPatchFormat>(json); }
            catch(Exception e) { loadingPresetIndex=-1; loadingLegacyBass=false; Debug.LogWarning("[ZPunk] Bass preset parse failed: "+e.Message); yield break; }
            if(patch==null || patch.settings==null) { loadingPresetIndex=-1; loadingLegacyBass=false; yield break; }
            // Let native mixer/channel initialization complete on cold launch.
            yield return null;
            yield return null;
            if(revision!=loadRevision)yield break;
            bassPatch.patchData=patch;
            ZPunkSoundUI.ClearLiveEdits(bass);
            bass.LoadPatch(bassPatch);
            bassPresetIndex=choice;
            legacyBassPresetPath=choice<0 ? presetRelPath : null;
            loadingPresetIndex=-1;
            loadingLegacyBass=false;
            var song=persist && projects!=null?projects.GetSongForArrangementEditor():null;
            if(song!=null && song.tracks!=null && song.tracks.Count>1 && song.tracks[1]!=null)
            {
                if(!ReferenceEquals(song,projects.CurrentSong))song=ArrangementEditing.IndependentCopy(song);
                song.tracks[1].instrument.presetRelPath=presetRelPath;
                song.tracks[1].instrument.presetIndex=bassPresetIndex;
                projects.AcceptArrangementEdit(song);
            }
            PresetChanged?.Invoke();
        }
    }
}
