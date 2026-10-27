#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;
using ZPunk.DAW;

public static class ZPunkDAWPhase3Checks
{
    [MenuItem("ZPUNK/Diagnostics/2.0/Run Phase 3 Arrangement Checks")]
    public static void Run()
    {
        try
        {
            var song = new SongData { title = "TEST", lengthTicks = SongData.TicksPerBar4, arrangementAuthored = true };
            var drum = new TrackData { id = "drums", name = "Drums", kind = TrackKind.Drums };
            var synth = new TrackData { id = "synth", name = "Synth", kind = TrackKind.Synth };
            song.tracks.Add(drum);
            song.tracks.Add(synth);
            var original = ArrangementEditing.CreateClip(song, drum, 0);
            var content = ArrangementEditing.FindContent(song, original.contentId);
            content.drums.Add(new DrumEvent { lane = 0, velocity = 110, startTick = 0 });
            var second = ArrangementEditing.Duplicate(song, original, drum);
            Assert(second != null, "Duplicate was not created");
            Assert(second.contentId != original.contentId, "Duplicate must have independent event content");
            Assert(song.contents.Count == 2, "Expected two independent drum contents");
            Assert(ArrangementEditing.NextFreeStart(drum, SongData.TicksPerBar4, SongData.TicksPerBar4)
                == 2 * SongData.TicksPerBar4, "Duplicate should not stack over occupied clips");
            ArrangementEditing.FindContent(song, second.contentId).drums[0].velocity = 40;
            Assert(content.drums[0].velocity == 110, "Editing a duplicate changed its source");
            Assert(ArrangementEditing.SnapDelta(-1900, 1920) == -1920, "Dragging left must produce a negative offset");
            Assert(ArrangementEditing.Move(song, second, 3 * SongData.TicksPerBar4), "Move failed");
            Assert(ArrangementEditing.Resize(song, second, 2 * SongData.TicksPerBar4), "Resize failed");
            Assert(!second.loopEnabled, "Extended steps should be explicit, without a second playback loop");
            Assert(ArrangementEditing.FindContent(song, second.contentId).drums.Count == 2,
                "Resizing must repeat the original hits into editable step content");
            Assert(song.lengthTicks >= 5 * SongData.TicksPerBar4, "Song length did not extend");
            var plan = SongEventPlanner.Build(song);
            Assert(plan.samples.Count == 3, "Expected one original hit and two repeated duplicate hits");
            Assert(plan.samples[0].tick == 0, "Original clip position changed");
            Assert(plan.samples[1].tick == 3 * SongData.TicksPerBar4, "Moved hit timing incorrect");
            Assert(plan.samples[2].tick == 4 * SongData.TicksPerBar4, "Repeated hit timing incorrect");
            var roundtrip = SongData.FromJson(song.ToJson());
            Assert(roundtrip != null && roundtrip.arrangementAuthored, "Authored marker was not serialized");
            Assert(roundtrip != null && roundtrip.tracks.Count == 2 && roundtrip.contents.Count == 2,
                "Song JSON roundtrip did not retain clips and content");
            Assert(SongEventPlanner.Build(roundtrip).samples.Count == 3, "Roundtrip scheduling mismatch");
            Assert(ArrangementEditing.Delete(song, second, drum), "Deletion failed");
            Assert(song.contents.Count == 1 && drum.clips.Count == 1, "Deletion left orphaned independent content");
            // Imported legacy links share content. Deleting one clip must not delete their shared content.
            var linked = new ClipData { id = "linked", contentId = original.contentId,
                name = "linked", startTick = SongData.TicksPerBar4, lengthTicks = SongData.TicksPerBar4 };
            drum.clips.Add(linked);
            ArrangementEditing.Delete(song, original, drum);
            Assert(ArrangementEditing.FindContent(song, linked.contentId) != null, "Deleting a linked clip removed shared content");
            Debug.Log("[ZPunk DAW] Phase 3 PASS: independent duplication, left drag, resize+loop, playback event timing, serialization, and linked-content deletion.");
        }
        catch (Exception e)
        {
            Debug.LogError("[ZPunk DAW] Phase 3 FAILED: " + e);
            throw;
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
#endif
