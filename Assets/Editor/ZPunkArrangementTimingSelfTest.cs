#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;
using ZPunk.DAW;

/// <summary>
/// Synthetic, sample-free boundary checks. Proves tick placement and compiled
/// drum/MIDI/chop event adjacency; actual AudioSource/Helm jitter requires device tests.
/// </summary>
public static class ZPunkArrangementTimingSelfTest
{
    [MenuItem("ZPUNK/Diagnostics/Test Arrangement Boundaries (2.2.2)")]
    public static void Run()
    {
        try
        {
            TestMidiAdjacentAndNoteOrder();
            TestDrumsAndChops();
            TestFreeSpacePlacementAndResize();
            Debug.Log("[ZPUNK/Timing] PASS: adjacent MIDI, drum and chop clip ticks, " +
                "same-tick NoteOff/NoteOn, duplication and resize placement.");
        }
        catch (Exception ex)
        {
            Debug.LogError("[ZPUNK/Timing] FAIL: " + ex);
        }
    }

    private static SongData Fresh(TrackKind kind)
    {
        var song = new SongData { bpm = 123.5f, lengthTicks = SongData.TicksPerBar4 };
        song.tracks.Add(new TrackData { name = kind.ToString(), kind = kind });
        return song;
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception(description);
    }

    private static void TestMidiAdjacentAndNoteOrder()
    {
        var song = Fresh(TrackKind.Bass);
        var track = song.tracks[0];
        var first = ArrangementEditing.CreateClip(song, track, 0);
        Check(first != null, "create first MIDI clip");
        var content = ArrangementEditing.FindContent(song, first.contentId);
        content.notes.Add(new NoteEvent { pitch = 48, startTick = 0,
            durationTicks = SongData.TicksPerBar4, velocity = 110 });
        var second = ArrangementEditing.Duplicate(song, first, track);
        Check(second != null && second.startTick == first.startTick + first.lengthTicks,
            "duplicate must begin at the exact previous clip end");
        var plan = SongEventPlanner.Build(song);
        long boundary = SongData.TicksPerBar4;
        Check(plan.midi.Count == 4, "two full-bar MIDI notes should compile to four events");
        Check(plan.midi[0].tick == 0 && plan.midi[0].type == SongEventPlanner.EventType.NoteOn,
            "first note starts at tick zero");
        Check(plan.midi[1].tick == boundary &&
            plan.midi[1].type == SongEventPlanner.EventType.NoteOff,
            "previous MIDI note releases exactly at clip end");
        Check(plan.midi[2].tick == boundary &&
            plan.midi[2].type == SongEventPlanner.EventType.NoteOn,
            "next MIDI note begins on identical tick, after NoteOff");
        Check(plan.midi[3].tick == 2 * boundary &&
            plan.midi[3].type == SongEventPlanner.EventType.NoteOff,
            "duplicate note length unchanged");
        double secondsPerTick = 60.0 / song.bpm / SongData.TicksPerQuarter;
        Check(Math.Abs((plan.midi[2].tick - plan.midi[1].tick) * secondsPerTick) < 1e-12,
            "adjacent note boundary must have zero DSP-time separation");
    }

    private static void TestDrumsAndChops()
    {
        foreach (TrackKind kind in new[] { TrackKind.Drums, TrackKind.Chops })
        {
            var song = Fresh(kind);
            var track = song.tracks[0];
            var first = ArrangementEditing.CreateClip(song, track, 0);
            var content = ArrangementEditing.FindContent(song, first.contentId);
            if (kind == TrackKind.Drums)
                content.drums.Add(new DrumEvent { lane = 0, startTick = 0, velocity = 100 });
            else
                content.chops.Add(new ChopEvent { sliceId = 1, startTick = 0, velocity = 100 });
            var second = ArrangementEditing.Duplicate(song, first, track);
            Check(second.startTick == SongData.TicksPerBar4,
                kind + ": adjacent duplicate start tick");
            var events = SongEventPlanner.Build(song).samples;
            Check(events.Count == 2 && events[0].tick == 0 &&
                events[1].tick == SongData.TicksPerBar4,
                kind + ": no missing or shifted event at the boundary");
        }
    }

    private static void TestFreeSpacePlacementAndResize()
    {
        var song = Fresh(TrackKind.Drums);
        var track = song.tracks[0];
        var first = ArrangementEditing.CreateClip(song, track, 0);
        var second = ArrangementEditing.Duplicate(song, first, track);
        Check(ArrangementEditing.NextFreeStart(track, 0, SongData.TicksPerBar4) ==
            2 * SongData.TicksPerBar4, "new clip skips two occupied bars");
        Check(ArrangementEditing.Resize(song, second, 2 * SongData.TicksPerBar4),
            "resize returns success");
        var third = ArrangementEditing.Duplicate(song, second, track);
        Check(third != null && third.startTick == second.startTick + second.lengthTicks,
            "resized clip duplicate touches its exact tick boundary");
        Check(ArrangementEditing.Snap(1937, SongData.TicksPerSixteenth) == 1920,
            "snap is based on musical ticks, not screen pixels");
    }
}
#endif
