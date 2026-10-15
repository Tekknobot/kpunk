using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZPunk.DAW
{
    // Phase 1: pure, scene-independent song document. Unity JsonUtility-compatible:
    // no dictionaries, interface fields, or polymorphic serialized collections.
    public enum TrackKind { Drums = 0, Bass = 1, Synth = 2, Pads = 3, Chops = 4 }
    public enum ClipKind { Midi = 0, Drums = 1, Chops = 2 }

    [Serializable]
    public sealed class SongData
    {
        public const int CurrentSchema = 2;
        public const int TicksPerQuarter = 480;
        public const int TicksPerSixteenth = TicksPerQuarter / 4;
        public const int TicksPerBar4 = TicksPerQuarter * 4;

        public int schemaVersion = CurrentSchema;
        public string title = "UNTITLED";
        public float bpm = 120f;
        public int numerator = 4;
        public int denominator = 4;
        public long lengthTicks = TicksPerBar4;
        public bool arrangementLoopEnabled = false;
        public long loopStartTick = 0;
        public long loopEndTick = TicksPerBar4;
        public bool migratedFromLegacy = false;
        // Phase 3: distinguishes a deliberately edited (possibly empty) DAW song
        // from older Phase 2 snapshots that could be empty accidentally.
        public bool arrangementAuthored = false;
        public bool legacyChainEnabled = false;
        public List<TrackData> tracks = new List<TrackData>();
        public List<ClipContentData> contents = new List<ClipContentData>();

        public string ToJson(bool pretty = false) { return JsonUtility.ToJson(this, pretty); }

        public static SongData FromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var song = JsonUtility.FromJson<SongData>(json);
            if (song == null || song.schemaVersion != CurrentSchema)
                throw new NotSupportedException("Unsupported ZPunk song schema. This version expects " + CurrentSchema + ".");
            if (song.tracks == null || song.contents == null)
                throw new FormatException("ZPunk song has missing track or content lists.");
            return song;
        }
    }

    [Serializable]
    public sealed class TrackData
    {
        public string id;
        public string name;
        public TrackKind kind;
        public bool mute;
        public bool solo;
        public float volume = 1f;
        public float pan = 0f;
        public string colorHex;
        public InstrumentState instrument = new InstrumentState();
        public List<ClipData> clips = new List<ClipData>();
    }

    [Serializable]
    public sealed class InstrumentState
    {
        public string presetRelPath;
        public int presetIndex;
        public int drumKitIndex;
        public string sampleResourcesPath;
        public float[] sliceStart01;
        public float[] sliceEnd01;
        public int legacyChordMode;
        public float savedMasterVolume = -1f;
    }

    [Serializable]
    public sealed class ClipData
    {
        public string id;
        public string name;
        public string contentId;
        public long startTick;
        public long lengthTicks;
        public long sourceOffsetTick;
        public bool loopEnabled;
    }

    // Linked clips reference the same contentId. Copying content gives an independent edit.
    // The union is deliberate: JsonUtility does not support polymorphic List<T> reliably.
    [Serializable]
    public sealed class ClipContentData
    {
        public string id;
        public ClipKind kind;
        public long lengthTicks = SongData.TicksPerBar4;
        public List<NoteEvent> notes = new List<NoteEvent>();
        public List<DrumEvent> drums = new List<DrumEvent>();
        public List<ChopEvent> chops = new List<ChopEvent>();
    }

    [Serializable]
    public sealed class NoteEvent
    {
        public int pitch;
        public long startTick;
        public long durationTicks;
        public int velocity;
    }

    [Serializable]
    public sealed class DrumEvent
    {
        public int lane; // 0..7: original ZPunk drum lane order
        public long startTick;
        public int velocity; // 1..127, derived from legacy 2-bit tier
        public int legacyVelocityTier;
    }

    [Serializable]
    public sealed class ChopEvent
    {
        public int sliceId; // legacy 1-based chop/slice identifier
        public long startTick;
        public int velocity;
    }
}
