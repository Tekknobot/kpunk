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
        // A user-defined loop length must not be overwritten when clips grow.
        // Older song JSON files deserialize this to false (automatic mode).
        public bool loopLengthCustom = false;
        public bool migratedFromLegacy = false;
        // Phase 3: distinguishes a deliberately edited (possibly empty) DAW song
        // from older Phase 2 snapshots that could be empty accidentally.
        public bool arrangementAuthored = false;
        // Optional 2.0.4+ metadata; old project JSON loads with choice 0 (genre default).
        // Set only through the generator UI. No schema migration or project reset.
        public int sharedProgressionChoice = 0;
        public int sharedProgressionKey = 9;
        public bool sharedProgressionMinor = true;
        public bool legacyChainEnabled = false;
        // Optional 2.3 drum/master dynamics parameters. Old project JSON has neutral defaults.
        public float masterGlue = 0f;
        // Neutral zero defaults keep pre-2.3 project JSON sounding unchanged.
        public float masterDrive = 0f;
        public float masterWidthOffset = 0f; // -1..1 = 70..130% stereo sides
        public float masterOutputTrimDb = 0f; // -12..+3 dB; applied before limiter
        public bool masterFxBypassed = false; // limiter always remains engaged
        // 2.3.5: Optional club dynamics. Zero is a strict bypass for older projects.
        public float kickRumble = 0f;   // 0..1, kick-only low passed tail
        public float bassDuck = 0f;     // 0..1, gentle kick-triggered bass attenuation
        // Optional arrangement PERFORMANCE FX, master stereo bus. Zero = strict bypass
        // for projects created before these controls existed. GATE rate codes remain
        // 0=1/4, 1=1/8, 2=1/16, 3=1/32, 4=half bar, 5=full bar
        // for both GATE and REPEAT. Saved 0..3 rates are unchanged.
        public float performanceDrive = 0f;
        public float performanceGate = 0f;
        public float performanceCrush = 0f;
        public float performanceRepeat = 0f;
        public int performanceGateRate = 2;
        public int performanceRepeatRate = 2;
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
        // Optional for old projects: stable DrumKit asset ID (e.g. Kit_12).
        public string drumKitId;
        public string sampleResourcesPath;
        public float[] sliceStart01;
        public float[] sliceEnd01;
        // Per-lane drum shaping; missing/empty means fully neutral sound for old projects.
        public List<DrumShapeData> drumDesign = new List<DrumShapeData>();
        public int legacyChordMode;
        public float savedMasterVolume = -1f;
        // 2.3.5: Tempo-locked filter movement is opt-in and independent per synth.
        public bool filterMotionEnabled;
        public float filterMotionDepth; // 0..1, modulation size
        public int filterMotionRate;    // 0=1/2 bar, 1=1 bar, 2=2 bars, 3=4 bars
        public float filterMotionCenter = -1f; // normalized Helm cutoff; -1=uncaptured
    }

    [Serializable]
    public sealed class DrumShapeData
    {
        // All values are neutral when this object does not exist on an old song.
        public float tune;         // semitones, -12..+12
        public float tone = 0.5f; // darker / brighter, neutral midpoint
        public float decay = 1f;  // length of hit envelope, neutral at 1
        public float pan;          // left/right, -1..1
        public float trim;         // signed dB, -12..+12
        public float drive;        // 0..1, neutral at 0
        public float punch;        // 0..1, mild per-hit compression
        // 2.3.6: Optional one-at-a-time CHARACTER effect. Old songs load OFF.
        public int characterFx;     // 0=OFF, 1=CRUSH, 2=PITCH, 3=ROOM, 4=METAL, 5=ECHO, 6=GATE
        public float characterAmount; // 0..1, effect depth; 0 is a strict bypass
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
        // Optional, backwards-compatible label for AI-free genre pattern generation.
        public string generatedTag;
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
        // Optional 2.3.4 manual chord edit group. Older project JSON defaults to null.
        // Chord notes remain regular independent NoteEvents for playback/export.
        public string chordGroupId;
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
