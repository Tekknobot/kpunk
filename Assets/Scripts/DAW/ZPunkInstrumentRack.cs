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
        private int bassPresetIndex=-1;
        private KMusicProjectManager projects;
        private int lastSlot=-1;
        private bool presetsReady;
        private SongData lastSong;
        private int loadRevision;
        private int loadingPresetIndex = -1;
        public HelmController BassController => bass;
        public string BassPresetName => bassPresetIndex>=0 && bassPresetIndex<bassPresets.Count
            ? Path.GetFileNameWithoutExtension(bassPresets[bassPresetIndex]):"INITIALIZING";
        public int BassPresetCount => bassPresets.Count;
        // Retained public API used by KMusicProjectManager and ZPunkSoundUI.
        public int BassPresetIndex => bassPresetIndex;
        public string CurrentBassPresetRelPath => bassPresetIndex >= 0 && bassPresetIndex < bassPresets.Count
            ? bassPresets[bassPresetIndex] : null;
        public bool BassPresetsReady => presetsReady && bass != null &&
            bassPresetIndex >= 0 && loadingPresetIndex < 0;

        // Saved projects may request a patch independently of browser navigation.
        // Return false for a missing path: project restore must NOT apply the old
        // sound parameter overrides to the replacement patch. The Update/startup
        // paths already select the first available BASS preset as a fallback.
        public bool RequestProjectBassPreset(string relPath)
        {
            if (!presetsReady || bass == null || bassPresets.Count == 0)
                return false;
            int index = bassPresets.FindIndex(x =>
                string.Equals(x, relPath, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                if (loadingPresetIndex < 0 && bassPresetIndex < 0)
                    StartCoroutine(LoadBassPreset(0, false));
                return false;
            }
            if (loadingPresetIndex == index ||
                (bassPresetIndex == index && loadingPresetIndex < 0))
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
                    // Keep the user's curated ZOR bank as the normal browser list.
                    // Older/non-ZOR paths in the index are not selectable here.
                    if(path.StartsWith("Bass/ZOR ",StringComparison.OrdinalIgnoreCase) &&
                       path.EndsWith(".helm",StringComparison.OrdinalIgnoreCase))
                        bassPresets.Add(path);
                }
            if(bassPresets.Count==0)
                Debug.LogWarning("[ZPunk] Bass preset list empty; instrument can still play MIDI.");
            int choice=0;
            var song=projects!=null?projects.GetSongForArrangementEditor():null;
            if(song!=null && song.tracks!=null && song.tracks.Count>1 && song.tracks[1]!=null)
            {
                string preferred=song.tracks[1].instrument?.presetRelPath;
                int found=bassPresets.FindIndex(x=>string.Equals(x,preferred,StringComparison.OrdinalIgnoreCase));
                if(found>=0)choice=found;
            }
            int startupSlot=projects!=null?projects.CurrentSlot:-1;
            var startupSong=projects!=null?projects.CurrentSong:null;
            if(bassPresets.Count>0)yield return LoadBassPreset(choice,false);
            // If a project was opened during initial preset loading, Update()
            // will resolve the new song instead of assuming it was restored.
            lastSlot=startupSlot;
            lastSong=startupSong;
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
            if(instrument==null || bassPresets.Count==0)return;
            int index=bassPresets.FindIndex(x=>string.Equals(x,instrument.presetRelPath,StringComparison.OrdinalIgnoreCase));
            // Always re-check on project switches, including when an earlier
            // request is still loading. Stale/empty paths resolve to bank zero.
            if(index<0) index=0;
            StartCoroutine(LoadBassPreset(index,false));
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
            int next=(current+delta+bassPresets.Count)%bassPresets.Count;
            StartCoroutine(LoadBassPreset(next,true));
        }
        private IEnumerator LoadBassPreset(int index,bool persist)
        {
            if(bass==null || bassPatch==null || bassPresets.Count==0)yield break;
            int revision=++loadRevision;
            int choice=Mathf.Clamp(index,0,bassPresets.Count-1);
            var requestedSong=projects!=null?projects.CurrentSong:null;
            int requestedSlot=projects!=null?projects.CurrentSlot:-1;
            loadingPresetIndex=choice;
            // The index may still contain files removed during preset curation.
            // Try the requested entry, then scan from zero for the first valid one.
            HelmPatchFormat patch=null;
            int resolvedIndex=-1;
            for(int attempt=-1;attempt<bassPresets.Count;attempt++)
            {
                int candidate=attempt<0?choice:attempt;
                if(attempt>=0 && candidate==choice) continue;
                var relative="HelmPresets/"+bassPresets[candidate];
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
                if(string.IsNullOrEmpty(json)) continue;
                HelmPatchFormat parsed=null;
                try { parsed=JsonUtility.FromJson<HelmPatchFormat>(json); }
                catch(Exception e) { Debug.LogWarning("[ZPUNK BASS] Invalid preset "+bassPresets[candidate]+": "+e.Message); }
                if(parsed==null || parsed.settings==null) continue;
                patch=parsed;
                resolvedIndex=candidate;
                break;
            }
            if(patch==null)
            {
                loadingPresetIndex=-1;
                Debug.LogError("[ZPUNK BASS] No loadable BASS presets found in HelmPresets.");
                yield break;
            }
            if(resolvedIndex!=choice)
                Debug.LogWarning("[ZPUNK BASS] Missing/invalid patch '"+bassPresets[choice]+
                                 "'; using '"+bassPresets[resolvedIndex]+"'.");
            choice=resolvedIndex;
            // Let native mixer/channel initialization complete on cold launch.
            yield return null;
            yield return null;
            if(revision!=loadRevision)yield break;
            if(presetsReady && projects!=null && (requestedSlot!=projects.CurrentSlot ||
                !ReferenceEquals(requestedSong,projects.CurrentSong)))
            {
                loadingPresetIndex=-1;
                yield break; // A newer project must not receive this older project's sound.
            }
            bassPatch.patchData=patch;
            // Do not allow live sound edits from a previous patch to carry over.
            ZPunkSoundUI.ClearLiveEdits(bass);
            bass.LoadPatch(bassPatch);
            bassPresetIndex=choice;
            loadingPresetIndex=-1;
            // Normalize stale paths only in the currently open song. Disk/project
            // files stay untouched until the user chooses to save normally.
            var liveSong=projects!=null?projects.CurrentSong:null;
            if(!persist && ReferenceEquals(liveSong,requestedSong) && liveSong!=null && liveSong.tracks!=null &&
               liveSong.tracks.Count>1 && liveSong.tracks[1]!=null &&
               liveSong.tracks[1].instrument!=null)
            {
                string oldPath=liveSong.tracks[1].instrument.presetRelPath;
                if(!string.Equals(oldPath,bassPresets[choice],StringComparison.OrdinalIgnoreCase))
                {
                    Debug.LogWarning("[ZPUNK BASS] Saved patch '"+oldPath+
                                     "' unavailable; using '"+bassPresets[choice]+"'.");
                    liveSong.tracks[1].instrument.presetRelPath=bassPresets[choice];
                    liveSong.tracks[1].instrument.presetIndex=choice;
                }
            }
            var song=persist && projects!=null?projects.GetSongForArrangementEditor():null;
            if(song!=null && song.tracks!=null && song.tracks.Count>1 && song.tracks[1]!=null)
            {
                if(!ReferenceEquals(song,projects.CurrentSong))song=ArrangementEditing.IndependentCopy(song);
                song.tracks[1].instrument.presetRelPath=bassPresets[choice];
                song.tracks[1].instrument.presetIndex=bassPresetIndex;
                projects.AcceptArrangementEdit(song);
            }
            PresetChanged?.Invoke();
        }
    }
}
