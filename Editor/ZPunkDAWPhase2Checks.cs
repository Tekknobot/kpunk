using System;
using UnityEditor;
using UnityEngine;
using ZPunk.DAW;

/// <summary>Editor-only, deterministic song event planner checks; no audio devices required.</summary>
public static class ZPunkDAWPhase2Checks
{
    [MenuItem("ZPunk/2.0/Run Phase 2 Arrangement Checks")]
    public static void Run()
    {
        var song = new SongData { lengthTicks = 4 * SongData.TicksPerBar4, bpm = 128 };
        var drums = new TrackData { id = "drums", name = "Drums", kind = TrackKind.Drums };
        var synth = new TrackData { id = "synth", name = "Synth", kind = TrackKind.Synth };
        song.tracks.Add(drums);
        song.tracks.Add(synth);

        var drumPattern = new ClipContentData { id = "d", kind = ClipKind.Drums, lengthTicks = SongData.TicksPerBar4 };
        drumPattern.drums.Add(new DrumEvent { lane = 0, startTick = 0, velocity = 122 });
        drumPattern.drums.Add(new DrumEvent { lane = 1, startTick = SongData.TicksPerSixteenth * 4, velocity = 90 });
        var melody = new ClipContentData { id = "m", kind = ClipKind.Midi, lengthTicks = SongData.TicksPerBar4 };
        melody.notes.Add(new NoteEvent { pitch = 60, startTick = 0, durationTicks = 360, velocity = 100 });
        melody.notes.Add(new NoteEvent { pitch = 64, startTick = 480, durationTicks = 1800, velocity = 90 });
        song.contents.Add(drumPattern);
        song.contents.Add(melody);

        drums.clips.Add(new ClipData { id = "d1", contentId = "d", startTick = 0, lengthTicks = 2 * SongData.TicksPerBar4, loopEnabled = true });
        // A one-bar MIDI clip at bar 3: the long second note must be trimmed at its clip edge.
        synth.clips.Add(new ClipData { id = "m1", contentId = "m", startTick = 2 * SongData.TicksPerBar4, lengthTicks = SongData.TicksPerBar4 });
        var plan = SongEventPlanner.Build(song);

        Expect(plan.samples.Count == 4, "Looped drum clip must produce four hits across two bars.");
        Expect(plan.samples[0].tick == 0 && plan.samples[1].tick == 480 &&
            plan.samples[2].tick == 1920 && plan.samples[3].tick == 2400,
            "Drum events must retain bar-relative timing and repeated placement.");
        Expect(plan.midi.Count == 4, "Two synth notes must produce two on/off pairs.");
        Expect(plan.midi[0].tick == 3840 && plan.midi[0].value == 60,
            "Synth notes must begin at their own clip start, independently of drums.");
        Expect(plan.midi[3].type == SongEventPlanner.EventType.NoteOff &&
            plan.midi[3].tick == 5760 && plan.midi[3].value == 64,
            "Long MIDI notes must stop at the end of their non-looping clip.");

        synth.clips.Clear();
        synth.clips.Add(new ClipData { id = "offset", contentId = "m", startTick = 0,
            lengthTicks = 480, sourceOffsetTick = 480 });
        plan = SongEventPlanner.Build(song);
        Expect(plan.midi.Count == 2 && plan.midi[0].tick == 0 && plan.midi[1].tick == 480,
            "Offset clips must skip earlier notes and trim notes at placement boundaries.");

        synth.clips[0].contentId = "missing";
        plan = SongEventPlanner.Build(song);
        Expect(plan.skippedClips == 1, "Missing clip content must be ignored safely.");

        // Serialization should preserve independent clip placement and flags.
        var restored = SongData.FromJson(song.ToJson());
        Expect(restored.tracks.Count == 2 && restored.tracks[0].clips[0].loopEnabled,
            "Round-trip serialization must retain the DAW track and clip structure.");

        // Regression: a freshly painted live drum/pad pattern must be playable
        // without assigning chain slots or saving a project first.
        var livePattern = new KMusic.Core.PatternData();
        livePattern.drumMask[0] = 1;
        livePattern.drumMask[4] = 1;
        livePattern.drumMask[8] = 1;
        livePattern.drumMask[12] = 1;
        livePattern.padSteps[0] = 40;
        livePattern.padRuns[0] = 1;
        var liveSong = LegacySongConverter.Convert(null, null, livePattern,
            new LegacySongConverter.Settings { bpm = 120f });
        var livePlan = SongEventPlanner.Build(liveSong);
        Expect(livePlan.samples.Count == 4 && livePlan.midi.Count >= 2,
            "A live four-on-the-floor pattern and pad chord must compile into audible events.");

        // Regression: an enabled chain may reference a saved but empty pattern, even
        // while a freshly painted live grid contains valid notes. Those chain events
        // must be considered empty so the runtime can choose its live-grid fallback.
        var blankBank = new KMusic.Core.PatternBank.PatternBankSave();
        blankBank.ids.Add(0);
        blankBank.drumMaskB64.Add(Convert.ToBase64String(new byte[16]));
        blankBank.drumVelocityB64.Add(Convert.ToBase64String(new byte[32]));
        var blankChain = new KMusic.Core.ChainStateSave { enabled = true, length = 2 };
        blankChain.slots[0] = 0;
        var blankSong = LegacySongConverter.Convert(blankBank, blankChain, livePattern,
            new LegacySongConverter.Settings { bpm = 120f });
        Expect(!SongEventPlanner.HasEvents(blankSong) && SongEventPlanner.HasEvents(liveSong),
            "Empty saved chain must be detected while preserving a playable live-grid preview.");

        // Guard against an empty song snapshot taking precedence over a newly
        // programmed four-on-the-floor beat in the old editor.
        var emptySavedSong = new SongData { migratedFromLegacy = false };
        Expect(!SongEventPlanner.HasEvents(emptySavedSong) && SongEventPlanner.HasEvents(liveSong),
            "An empty native song must not suppress the live-grid fallback.");

        Debug.Log("[ZPunk 2.0] Phase 2 arrangement event checks passed.");
        EditorUtility.DisplayDialog("ZPunk 2.0", "Phase 2 arrangement checks passed. DSP/audio must also be tested in Play Mode.", "OK");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception("[ZPunk Phase 2] " + message);
    }
}
