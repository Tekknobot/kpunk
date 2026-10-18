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
                    if(path.StartsWith("Bass/",StringComparison.OrdinalIgnoreCase) && path.EndsWith(".helm",StringComparison.OrdinalIgnoreCase))
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
            if(bassPresets.Count>0)yield return LoadBassPreset(choice,false);
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
            int index=bassPresets.FindIndex(x=>string.Equals(x,instrument.presetRelPath,StringComparison.OrdinalIgnoreCase));
            if(index>=0 && index!=bassPresetIndex && index!=loadingPresetIndex)
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
            loadingPresetIndex=choice;
            var relative="HelmPresets/"+bassPresets[choice];
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
            if(string.IsNullOrEmpty(json)) { loadingPresetIndex=-1; yield break; }
            HelmPatchFormat patch;
            try { patch=JsonUtility.FromJson<HelmPatchFormat>(json); }
            catch(Exception e) { loadingPresetIndex=-1; Debug.LogWarning("[ZPunk] Bass preset parse failed: "+e.Message); yield break; }
            if(patch==null || patch.settings==null) { loadingPresetIndex=-1; yield break; }
            // Let native mixer/channel initialization complete on cold launch.
            yield return null;
            yield return null;
            if(revision!=loadRevision)yield break;
            bassPatch.patchData=patch;
            bass.LoadPatch(bassPatch);
            bassPresetIndex=choice;
            loadingPresetIndex=-1;
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
