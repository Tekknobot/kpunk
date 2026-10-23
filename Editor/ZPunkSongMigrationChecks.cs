using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using KMusic.Core;
using ZPunk.DAW;

// Editor-only checks: can run without modifying scenes, slot files or PlayerPrefs.
public static class ZPunkSongMigrationChecks
{
    [MenuItem("ZPunk/2.0/Run Song Migration Checks")]
    public static void Run()
    {
        var bank = new PatternBank.PatternBankSave();
        bank.ids.Add(4);
        bank.ids.Add(9);
        var one = new PatternData();
        one.drumMask[0] = 1;
        one.drumVelocityData[0] = 2; // Kick high velocity, tier 2.
        one.seqSteps[0] = 12;
        one.seqSteps[1] = 12;
        one.seqSteps[2] = 12;
        one.seqRuns[0] = 3;
        one.padSteps[0] = 8;
        one.padRuns[0] = 2;
        one.padChordMode = 1; // minor
        one.sampleSteps[6] = 7;
        var two = new PatternData();
        two.seqSteps[0] = 1;
        two.seqRuns[0] = 1;
        Add(bank, one);
        Add(bank, two);

        var chain = new ChainStateSave { length = 4, enabled = true };
        for (int i = 0; i < chain.slots.Length; i++) chain.slots[i] = -1;
        chain.slots[0] = 4;
        chain.slots[1] = 4;
        chain.slots[3] = 9;

        var song = LegacySongConverter.Convert(bank, chain, null, new LegacySongConverter.Settings
        {
            title = "Migration Fixture",
            bpm = 132,
            kitIndex = 3,
            synthPresetPath = "Synth/Fixture",
            sampleResourcesPath = "Samples/Fixture"
        });
        Assert(song.tracks.Count == 5, "Must create five lanes including empty bass lane");
        Assert(song.lengthTicks == 4 * SongData.TicksPerBar4, "Must preserve blank bar at index 2");
        Assert(song.tracks[1].clips.Count == 0, "New bass lane should be empty");
        Assert(song.tracks[2].clips.Count == 3, "Must preserve repeated synth patterns and later pattern");
        Assert(song.tracks[2].clips[0].contentId == song.tracks[2].clips[1].contentId,
            "Repeated patterns should link to one content object");
        Assert(song.tracks[2].clips[2].startTick == 3 * SongData.TicksPerBar4,
            "Must not collapse silent bars");
        Assert(song.tracks[0].instrument.drumKitIndex == 3, "Must preserve drum kit");
        Assert(song.tracks[4].instrument.sampleResourcesPath == "Samples/Fixture", "Must preserve source path");
        var synth = Content(song, "legacy/pattern-4/synth");
        Assert(synth.notes.Count == 1 && synth.notes[0].pitch == 32 &&
            synth.notes[0].durationTicks == 3 * SongData.TicksPerSixteenth,
            "Must preserve live synth pitch mapping and held note length");
        var pads = Content(song, "legacy/pattern-4/pads");
        Assert(pads.notes.Count == 3 && pads.notes[0].pitch == 28 && pads.notes[1].pitch == 31,
            "Must bake minor chord into polyphonic MIDI notes");
        var drums = Content(song, "legacy/pattern-4/drums");
        Assert(drums.drums.Count == 1 && drums.drums[0].velocity == 122,
            "Must preserve 2-bit high velocity drum tier");
        Assert(Content(song, "legacy/pattern-4/chops").chops[0].sliceId == 7,
            "Must preserve chop slice identifiers");
        var restored = SongData.FromJson(song.ToJson());
        Assert(restored.tracks[2].clips.Count == 3 && restored.contents.Count == song.contents.Count,
            "Must survive JSON round trip");

        var emptyChain = new ChainStateSave { length = 16 };
        for (int i = 0; i < emptyChain.slots.Length; i++) emptyChain.slots[i] = -1;
        var live = new PatternData();
        live.seqSteps[0] = 3;
        var liveSong = LegacySongConverter.Convert(bank, emptyChain, live, null);
        Assert(liveSong.lengthTicks == SongData.TicksPerBar4,
            "Empty chain should fall back to one live bar, not 16 blank bars");
        Assert(liveSong.tracks[2].clips.Count == 1, "Live grid should migrate");
        Debug.Log("[ZPunk 2.0] All song migration checks passed.");
        EditorUtility.DisplayDialog("ZPunk 2.0", "Song migration checks passed. See Console for details.", "OK");
    }

    private static void Add(PatternBank.PatternBankSave bank, PatternData p)
    {
        bank.drumMaskB64.Add(Convert.ToBase64String(p.drumMask));
        bank.drumVelocityB64.Add(Convert.ToBase64String(p.drumVelocityData));
        bank.sampleSteps.Add(new PatternBank.PatternBankSave.IntArrayWrap(p.sampleSteps));
        bank.seqSteps.Add(new PatternBank.PatternBankSave.IntArrayWrap(p.seqSteps));
        bank.seqRuns.Add(new PatternBank.PatternBankSave.IntArrayWrap(p.seqRuns));
        bank.padSteps.Add(new PatternBank.PatternBankSave.IntArrayWrap(p.padSteps));
        bank.padRuns.Add(new PatternBank.PatternBankSave.IntArrayWrap(p.padRuns));
        bank.padChordModes.Add(p.padChordMode);
    }

    private static ClipContentData Content(SongData song, string id)
    {
        foreach (var content in song.contents)
            if (content.id == id) return content;
        throw new Exception("Missing content: " + id);
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new Exception("[ZPunk Migration] " + message);
    }
}
