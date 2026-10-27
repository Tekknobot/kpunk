#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;
using ZPunk.DAW;

/// <summary>Non-audio checks for the 128-note editor model, 16 chops and manual song loops.</summary>
public static class ZPunkDAWClipRangeLoopChecks
{
    [MenuItem("ZPUNK/Diagnostics/2.0/Run Clip Range and Loop Checks")]
    public static void Run()
    {
        var song = new SongData { arrangementAuthored = true, lengthTicks = 5 * SongData.TicksPerBar4 };
        var drums = new TrackData { id = "drums", kind = TrackKind.Drums, name = "Drums" };
        var synth = new TrackData { id = "synth", kind = TrackKind.Synth, name = "Synth" };
        var chops = new TrackData { id = "chops", kind = TrackKind.Chops, name = "Chops" };
        song.tracks.Add(drums);
        song.tracks.Add(synth);
        song.tracks.Add(chops);

        var d = ArrangementEditing.CreateClip(song, drums, 0);
        var m = ArrangementEditing.CreateClip(song, synth, 0);
        var c = ArrangementEditing.CreateClip(song, chops, 0);
        ArrangementEditing.FindContent(song, d.contentId).drums.Add(
            new DrumEvent { lane = 7, startTick = 0, velocity = 100 });
        var notes = ArrangementEditing.FindContent(song, m.contentId).notes;
        notes.Add(new NoteEvent { pitch = 0, startTick = 0, durationTicks = 120, velocity = 100 });
        notes.Add(new NoteEvent { pitch = 127, startTick = 120, durationTicks = 120, velocity = 100 });
        ArrangementEditing.FindContent(song, c.contentId).chops.Add(
            new ChopEvent { sliceId = 16, startTick = 0, velocity = 100 });

        var compiled = SongEventPlanner.Build(song);
        Expect(compiled.samples.Count == 2, "The eighth drum lane or sixteenth chop is not scheduled.");
        Expect(compiled.midi.Count == 4 && compiled.midi.Exists(e => e.value == 0) &&
            compiled.midi.Exists(e => e.value == 127), "The full 0..127 MIDI pitch range is not playable.");

        song.loopLengthCustom = true;
        song.arrangementLoopEnabled = true;
        song.loopStartTick = 0;
        song.loopEndTick = 4 * SongData.TicksPerBar4;
        var snapshot = SongData.FromJson(song.ToJson());
        Expect(snapshot.loopLengthCustom && snapshot.loopEndTick == 4 * SongData.TicksPerBar4,
            "Four-bar loop length did not survive project JSON serialization.");
        ArrangementEditing.ExtendSong(snapshot, 9 * SongData.TicksPerBar4);
        Expect(snapshot.loopEndTick == 4 * SongData.TicksPerBar4,
            "Adding later clips must not overwrite a custom four-bar loop.");
        Debug.Log("[ZPunk 2.0] Clip range and custom-loop checks passed. Test sound and touch input in Play Mode.");
        EditorUtility.DisplayDialog("ZPunk 2.0", "Clip range and custom loop checks passed.", "OK");
    }

    private static void Expect(bool condition, string error)
    {
        if (!condition) throw new Exception("[ZPunk checks] " + error);
    }
}
#endif
