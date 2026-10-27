#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;
using ZPunk.DAW;

public static class ZPunkDAWLiveChecks
{
    [MenuItem("ZPUNK/Diagnostics/2.0/Run Live DAW Document Checks")]
    public static void Run()
    {
        var song = new SongData();
        song.tracks.Add(new TrackData { id="drums", name="Drums", kind=TrackKind.Drums });
        song.tracks.Add(new TrackData { id="synth", name="Synth", kind=TrackKind.Synth });
        var drums = song.tracks[0];
        var first = ArrangementEditing.CreateClip(song,drums,0);
        var a = ArrangementEditing.Duplicate(song,first,drums);
        var b = ArrangementEditing.Duplicate(song,a,drums);
        if (first.name != "Drums 1" || a.name != "Drums 2" || b.name != "Drums 3")
            throw new Exception("Sequential clip naming failed");
        var content = ArrangementEditing.FindContent(song,first.contentId);
        content.drums.Add(new DrumEvent { lane=0, startTick=0, velocity=110 });
        song.lengthTicks = 4L*SongData.TicksPerBar4;
        song.arrangementLoopEnabled = true;
        song.loopLengthCustom = true;
        song.loopStartTick = 0;
        song.loopEndTick = 3L*SongData.TicksPerBar4;
        var plan=SongEventPlanner.Build(song);
        if (plan.samples.Count != 1) throw new Exception("Drum event compilation failed");
        var reopened=SongData.FromJson(song.ToJson());
        if (!reopened.loopLengthCustom || reopened.loopEndTick!=song.loopEndTick ||
            reopened.tracks[0].clips.Count!=3)
            throw new Exception("Song serialization failed");
        Debug.Log("[ZPunk] Live DAW document checks passed: independent clips, sequential labels, looping, saving.");
    }
}
#endif
