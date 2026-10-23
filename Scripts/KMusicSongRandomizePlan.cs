using System;
using UnityEngine;
using KMusic.Core;

[Serializable]
public class KMusicSongRandomizePlanData
{
    public int version = 4;
    public int barCount = 1;
    public int tonicSemitone = 0;
    public int isMinor = 1;
    public int rootValueId = 1;
    public int melodyBaseOctave = 1;
    public int style = 1;
    public int syncMask = 0;
    public int[] chordDegrees;
    public int[] phraseKinds;
    public int[] chordRhythmKinds;
    public int[] chordModes;
    public int[] chordVariationKinds;
}


public enum KMusicGenreStyle
{
    House = 1,
    Techno = 2,
}

public enum KMusicRandomizeRequester
{
    Keys = 1,
    Chords = 2,
}

public enum KMusicPhraseKind
{
    Hook = 0,
    Answer = 1,
    Ascend = 2,
    Descend = 3,
    Arp = 4,
    Sustain = 5,
    Sparse = 6,
    Pulse = 7,
    HouseStab = 8,
    HouseLift = 9,
    HouseCall = 10,
}

public enum KMusicChordRhythmKind
{
    Hold = 0,
    OffbeatStabs = 1,
    PushPattern = 2,
    AnthemLift = 3,
    LateOffbeats = 4,
    DeepSparse = 5,
    Bounce = 6,
    DenseGroove = 7,
}

public enum KMusicChordVariationKind
{
    None = 0,
    Anticipate = 1,
    SkipFirst = 2,
    TailHold = 3,
    SplitPulse = 4,
}

public static class KMusicSongRandomizePlan
{
    private const string PrefKey = "kmusic.randomize.songplan.v4";
    private const int SyncMaskKeys = 1;
    private const int SyncMaskChords = 2;
    private static KMusicSongRandomizePlanData _cached;

    public static KMusicSongRandomizePlanData EnsurePlan(int barCount)
    {
        barCount = Mathf.Clamp(barCount, 1, 64);

        if (_cached != null && _cached.barCount == barCount)
            return Clone(_cached);

        var loaded = Load();
        if (loaded != null && loaded.barCount == barCount)
        {
            _cached = loaded;
            return Clone(_cached);
        }

        _cached = Generate(barCount);
        Save(_cached);
        return Clone(_cached);
    }

    public static KMusicSongRandomizePlanData ForceNewPlan(int barCount)
    {
        _cached = Generate(Mathf.Clamp(barCount, 1, 64));
        Save(_cached);
        return Clone(_cached);
    }

    public static KMusicSongRandomizePlanData AcquireLinkedPlan(int barCount, KMusicRandomizeRequester requester)
    {
        barCount = Mathf.Clamp(barCount, 1, 64);
        int requesterMask = requester == KMusicRandomizeRequester.Chords ? SyncMaskChords : SyncMaskKeys;

        var loaded = Load();
        if (loaded != null && loaded.barCount == barCount)
        {
            if ((loaded.syncMask & requesterMask) == 0 && loaded.syncMask != 0)
            {
                loaded.syncMask |= requesterMask;
                _cached = loaded;
                Save(_cached);
                return Clone(_cached);
            }
        }

        _cached = Generate(barCount);
        _cached.syncMask = requesterMask;
        Save(_cached);
        return Clone(_cached);
    }

    public static int[] GetScaleOffsets(bool isMinor)
    {
        return isMinor ? new[] { 0, 2, 3, 5, 7, 8, 10 } : new[] { 0, 2, 4, 5, 7, 9, 11 };
    }

    public static int DegreeToScaleIndex(int degree)
    {
        int d = Mathf.Max(1, degree) - 1;
        return d % 7;
    }

    public static int DegreeToSemitone(int tonicSemitone, bool isMinor, int degree)
    {
        int[] scale = GetScaleOffsets(isMinor);
        int idx = DegreeToScaleIndex(degree);
        return PositiveMod(tonicSemitone + scale[idx], 12);
    }

    public static int DegreeToValueId(int rootValueId, bool isMinor, int degree, int maxValueId)
    {
        int[] scale = GetScaleOffsets(isMinor);
        int idx = DegreeToScaleIndex(degree);
        int valueId = rootValueId + scale[idx];
        return Mathf.Clamp(valueId, 1, Mathf.Max(1, maxValueId));
    }

    public static int FindNearestValueIdForPitchClass(int preferredValueId, int pitchClass, int maxValueId)
    {
        preferredValueId = Mathf.Clamp(preferredValueId, 1, Mathf.Max(1, maxValueId));
        int best = preferredValueId;
        int bestDist = int.MaxValue;
        for (int i = 1; i <= Mathf.Max(1, maxValueId); i++)
        {
            int pc = PositiveMod(i - 1, 12);
            if (pc != PositiveMod(pitchClass, 12))
                continue;
            int dist = Mathf.Abs(i - preferredValueId);
            if (dist < bestDist)
            {
                best = i;
                bestDist = dist;
            }
        }
        return best;
    }

    public static int[] BuildChordPitchClasses(int tonicSemitone, bool isMinor, int degree)
    {
        int root = DegreeToSemitone(tonicSemitone, isMinor, degree);
        int third = DegreeToSemitone(tonicSemitone, isMinor, degree + 2);
        int fifth = DegreeToSemitone(tonicSemitone, isMinor, degree + 4);
        return new[] { root, third, fifth };
    }

    public static KMusicChordRhythmKind GetChordRhythm(KMusicSongRandomizePlanData plan, int barIndex)
    {
        if (plan == null || plan.chordRhythmKinds == null || plan.chordRhythmKinds.Length <= 0)
            return KMusicChordRhythmKind.OffbeatStabs;
        int idx = Mathf.Clamp(barIndex, 0, plan.chordRhythmKinds.Length - 1);
        return (KMusicChordRhythmKind)plan.chordRhythmKinds[idx];
    }

    public static int GetChordMode(KMusicSongRandomizePlanData plan, int barIndex, int fallbackMode)
    {
        if (plan == null || plan.chordModes == null || plan.chordModes.Length <= 0)
            return fallbackMode;
        int idx = Mathf.Clamp(barIndex, 0, plan.chordModes.Length - 1);
        return plan.chordModes[idx];
    }

    public static KMusicChordVariationKind GetChordVariation(KMusicSongRandomizePlanData plan, int barIndex)
    {
        if (plan == null || plan.chordVariationKinds == null || plan.chordVariationKinds.Length <= 0)
            return KMusicChordVariationKind.None;
        int idx = Mathf.Clamp(barIndex, 0, plan.chordVariationKinds.Length - 1);
        return (KMusicChordVariationKind)plan.chordVariationKinds[idx];
    }

    private static int ChooseHouseChordMode(bool isMinor, int sectionBar, bool lastBar)
    {
        if (lastBar)
            return UnityEngine.Random.value < 0.65f ? 2 : (isMinor ? 1 : 0); // 7th or tonic feel

        float roll = UnityEngine.Random.value;
        if (isMinor)
        {
            if (sectionBar == 0) return roll < 0.50f ? 1 : (roll < 0.85f ? 2 : 3);
            if (sectionBar == 1) return roll < 0.35f ? 1 : (roll < 0.80f ? 2 : 3);
            if (sectionBar == 2) return roll < 0.20f ? 1 : (roll < 0.70f ? 2 : 3);
            return roll < 0.40f ? 1 : (roll < 0.82f ? 2 : 3);
        }

        if (sectionBar == 0) return roll < 0.58f ? 0 : (roll < 0.86f ? 2 : 3);
        if (sectionBar == 1) return roll < 0.40f ? 0 : (roll < 0.78f ? 2 : 3);
        if (sectionBar == 2) return roll < 0.28f ? 0 : (roll < 0.72f ? 2 : 3);
        return roll < 0.44f ? 0 : (roll < 0.84f ? 2 : 3);
    }

    private static int ChooseTechnoChordMode(bool isMinor, int sectionBar, bool lastBar)
    {
        float roll = UnityEngine.Random.value;
        if (lastBar)
            return isMinor ? (roll < 0.60f ? 1 : 2) : (roll < 0.55f ? 0 : 3);

        if (isMinor)
        {
            if (sectionBar == 0) return roll < 0.62f ? 1 : (roll < 0.84f ? 2 : 3);
            if (sectionBar == 1) return roll < 0.44f ? 1 : (roll < 0.78f ? 3 : 2);
            if (sectionBar == 2) return roll < 0.34f ? 1 : (roll < 0.72f ? 2 : 3);
            return roll < 0.48f ? 1 : (roll < 0.82f ? 2 : 3);
        }

        if (sectionBar == 0) return roll < 0.56f ? 0 : (roll < 0.82f ? 3 : 2);
        if (sectionBar == 1) return roll < 0.38f ? 0 : (roll < 0.70f ? 3 : 2);
        if (sectionBar == 2) return roll < 0.28f ? 0 : (roll < 0.64f ? 2 : 3);
        return roll < 0.42f ? 0 : (roll < 0.74f ? 3 : 2);
    }

    private static KMusicSongRandomizePlanData Generate(int barCount)
    {
        return UnityEngine.Random.value < 0.5f
            ? GenerateHouse(barCount)
            : GenerateTechno(barCount);
    }

    private static KMusicSongRandomizePlanData GenerateHouse(int barCount)
    {
        bool isMinor = UnityEngine.Random.value < 0.84f;
        int tonicSemitone = (isMinor
            ? new[] { 0, 2, 4, 5, 7, 9 }
            : new[] { 0, 2, 5, 7, 9 })[UnityEngine.Random.Range(0, isMinor ? 6 : 5)];

        int rootBaseOctave = UnityEngine.Random.value < 0.60f ? 24 : 36;
        int rootValueId = 1 + tonicSemitone + rootBaseOctave;
        int melodyBaseOctave = UnityEngine.Random.value < 0.65f ? 3 : 4;

        int[][] templatesMajor =
        {
            new[] { 1, 5, 6, 4 },
            new[] { 1, 3, 4, 5 },
            new[] { 6, 4, 1, 5 },
            new[] { 1, 4, 2, 5 },
            new[] { 1, 6, 4, 5 },
            new[] { 1, 5, 4, 4 },
        };
        int[][] templatesMinor =
        {
            new[] { 1, 6, 3, 7 },
            new[] { 1, 7, 6, 7 },
            new[] { 1, 4, 1, 5 },
            new[] { 6, 7, 1, 7 },
            new[] { 1, 6, 7, 7 },
            new[] { 1, 3, 7, 6 },
            new[] { 1, 4, 6, 5 },
            new[] { 1, 7, 1, 6 },
        };

        int[] template = (isMinor ? templatesMinor : templatesMajor)[UnityEngine.Random.Range(0, isMinor ? templatesMinor.Length : templatesMajor.Length)];

        int[] degrees = new int[barCount];
        int[] phrases = new int[barCount];
        int[] rhythms = new int[barCount];
        int[] chordModes = new int[barCount];
        int[] chordVariations = new int[barCount];

        KMusicChordRhythmKind[] grooveFamilyA =
        {
            KMusicChordRhythmKind.OffbeatStabs,
            KMusicChordRhythmKind.Bounce,
            KMusicChordRhythmKind.LateOffbeats,
            KMusicChordRhythmKind.PushPattern,
        };

        KMusicChordRhythmKind[] grooveFamilyB =
        {
            KMusicChordRhythmKind.DeepSparse,
            KMusicChordRhythmKind.OffbeatStabs,
            KMusicChordRhythmKind.AnthemLift,
            KMusicChordRhythmKind.DenseGroove,
        };

        bool useFamilyA = UnityEngine.Random.value < 0.5f;
        int phraseGroup = Mathf.Max(2, UnityEngine.Random.value < 0.55f ? 2 : 4);
        KMusicChordRhythmKind previousRhythm = KMusicChordRhythmKind.OffbeatStabs;
        int previousMode = isMinor ? 1 : 0;

        for (int bar = 0; bar < barCount; bar++)
        {
            int sectionBar = bar % 4;
            int degree = template[sectionBar];

            if (bar >= 4)
            {
                if (sectionBar == 0 && UnityEngine.Random.value < 0.28f)
                    degree = 1;
                else if (sectionBar == 1 && UnityEngine.Random.value < 0.38f)
                    degree = isMinor ? 7 : 3;
                else if (sectionBar == 2 && UnityEngine.Random.value < 0.34f)
                    degree = isMinor ? 6 : 2;
                else if (sectionBar == 3 && UnityEngine.Random.value < 0.30f)
                    degree = isMinor ? 5 : 4;
            }
            if (bar == barCount - 1)
                degree = 1;
            degrees[bar] = degree;

            KMusicPhraseKind phrase;
            switch (sectionBar)
            {
                default:
                case 0:
                    phrase = (bar == 0 || UnityEngine.Random.value < 0.50f) ? KMusicPhraseKind.HouseCall : KMusicPhraseKind.Hook;
                    break;
                case 1:
                    phrase = UnityEngine.Random.value < 0.45f ? KMusicPhraseKind.Answer : KMusicPhraseKind.HouseStab;
                    break;
                case 2:
                    phrase = UnityEngine.Random.value < 0.40f ? KMusicPhraseKind.HouseLift : KMusicPhraseKind.Arp;
                    break;
                case 3:
                    phrase = (bar == barCount - 1)
                        ? KMusicPhraseKind.Sustain
                        : (UnityEngine.Random.value < 0.55f ? KMusicPhraseKind.Pulse : KMusicPhraseKind.HouseStab);
                    break;
            }
            phrases[bar] = (int)phrase;

            var family = useFamilyA ? grooveFamilyA : grooveFamilyB;
            KMusicChordRhythmKind rhythm = family[Mathf.Clamp(sectionBar, 0, family.Length - 1)];
            if ((bar % phraseGroup) == 1)
            {
                if (rhythm == KMusicChordRhythmKind.OffbeatStabs) rhythm = UnityEngine.Random.value < 0.65f ? KMusicChordRhythmKind.LateOffbeats : KMusicChordRhythmKind.Bounce;
                else if (rhythm == KMusicChordRhythmKind.Bounce) rhythm = UnityEngine.Random.value < 0.55f ? KMusicChordRhythmKind.PushPattern : KMusicChordRhythmKind.OffbeatStabs;
                else if (rhythm == KMusicChordRhythmKind.DeepSparse) rhythm = UnityEngine.Random.value < 0.60f ? KMusicChordRhythmKind.OffbeatStabs : KMusicChordRhythmKind.Bounce;
                else if (rhythm == KMusicChordRhythmKind.DenseGroove) rhythm = UnityEngine.Random.value < 0.55f ? KMusicChordRhythmKind.AnthemLift : KMusicChordRhythmKind.PushPattern;
            }
            else if ((bar % phraseGroup) >= 2)
            {
                float shiftRoll = UnityEngine.Random.value;
                if (shiftRoll < 0.22f) rhythm = KMusicChordRhythmKind.Bounce;
                else if (shiftRoll < 0.34f) rhythm = KMusicChordRhythmKind.DeepSparse;
                else if (shiftRoll < 0.44f) rhythm = KMusicChordRhythmKind.PushPattern;
            }

            if (sectionBar == 3 && UnityEngine.Random.value < 0.20f)
                rhythm = KMusicChordRhythmKind.Hold;
            if (bar == barCount - 1)
                rhythm = UnityEngine.Random.value < 0.58f ? KMusicChordRhythmKind.Hold : KMusicChordRhythmKind.LateOffbeats;
            if (bar > 0 && rhythm == previousRhythm && UnityEngine.Random.value < 0.70f)
            {
                rhythm = rhythm switch
                {
                    KMusicChordRhythmKind.OffbeatStabs => KMusicChordRhythmKind.LateOffbeats,
                    KMusicChordRhythmKind.LateOffbeats => KMusicChordRhythmKind.Bounce,
                    KMusicChordRhythmKind.Bounce => KMusicChordRhythmKind.PushPattern,
                    KMusicChordRhythmKind.DeepSparse => KMusicChordRhythmKind.OffbeatStabs,
                    KMusicChordRhythmKind.DenseGroove => KMusicChordRhythmKind.AnthemLift,
                    _ => KMusicChordRhythmKind.OffbeatStabs,
                };
            }
            rhythms[bar] = (int)rhythm;
            previousRhythm = rhythm;

            int chordMode = ChooseHouseChordMode(isMinor, sectionBar, bar == barCount - 1);
            if (bar > 0 && chordMode == previousMode && UnityEngine.Random.value < 0.65f)
            {
                chordMode = chordMode switch
                {
                    1 => 2,
                    2 => 3,
                    3 => isMinor ? 1 : 0,
                    _ => 2,
                };
            }
            chordModes[bar] = chordMode;
            previousMode = chordMode;

            KMusicChordVariationKind variation = KMusicChordVariationKind.None;
            float vRoll = UnityEngine.Random.value;
            if (bar == barCount - 1)
                variation = KMusicChordVariationKind.TailHold;
            else if (rhythm == KMusicChordRhythmKind.OffbeatStabs || rhythm == KMusicChordRhythmKind.LateOffbeats)
                variation = vRoll < 0.22f ? KMusicChordVariationKind.SkipFirst : (vRoll < 0.48f ? KMusicChordVariationKind.Anticipate : KMusicChordVariationKind.None);
            else if (rhythm == KMusicChordRhythmKind.Bounce || rhythm == KMusicChordRhythmKind.PushPattern)
                variation = vRoll < 0.35f ? KMusicChordVariationKind.SplitPulse : (vRoll < 0.52f ? KMusicChordVariationKind.Anticipate : KMusicChordVariationKind.None);
            else if (rhythm == KMusicChordRhythmKind.Hold)
                variation = vRoll < 0.70f ? KMusicChordVariationKind.TailHold : KMusicChordVariationKind.None;
            chordVariations[bar] = (int)variation;

            if ((bar % phraseGroup) == phraseGroup - 1 && UnityEngine.Random.value < 0.18f)
                useFamilyA = !useFamilyA;
        }

        return new KMusicSongRandomizePlanData
        {
            version = 4,
            barCount = barCount,
            tonicSemitone = tonicSemitone,
            isMinor = isMinor ? 1 : 0,
            rootValueId = Mathf.Max(1, rootValueId),
            melodyBaseOctave = melodyBaseOctave,
            style = (int)KMusicGenreStyle.House,
            syncMask = 0,
            chordDegrees = degrees,
            phraseKinds = phrases,
            chordRhythmKinds = rhythms,
            chordModes = chordModes,
            chordVariationKinds = chordVariations,
        };
    }

    private static KMusicSongRandomizePlanData GenerateTechno(int barCount)
    {
        bool isMinor = UnityEngine.Random.value < 0.92f;
        int tonicSemitone = (isMinor
            ? new[] { 0, 2, 3, 5, 7, 8, 10 }
            : new[] { 0, 2, 5, 7, 9, 10 })[UnityEngine.Random.Range(0, isMinor ? 7 : 6)];

        int rootBaseOctave = UnityEngine.Random.value < 0.72f ? 12 : 24;
        int rootValueId = 1 + tonicSemitone + rootBaseOctave;
        int melodyBaseOctave = UnityEngine.Random.value < 0.62f ? 2 : 3;

        int[][] templatesMinor =
        {
            new[] { 1, 7, 6, 7 },
            new[] { 1, 4, 1, 7 },
            new[] { 1, 6, 1, 7 },
            new[] { 1, 5, 7, 6 },
            new[] { 1, 1, 7, 6 },
            new[] { 1, 3, 1, 7 },
        };
        int[][] templatesMajor =
        {
            new[] { 1, 7, 6, 5 },
            new[] { 1, 5, 4, 5 },
            new[] { 1, 4, 1, 5 },
            new[] { 1, 2, 7, 5 },
        };

        int[] template = (isMinor ? templatesMinor : templatesMajor)[UnityEngine.Random.Range(0, isMinor ? templatesMinor.Length : templatesMajor.Length)];

        int[] degrees = new int[barCount];
        int[] phrases = new int[barCount];
        int[] rhythms = new int[barCount];
        int[] chordModes = new int[barCount];
        int[] chordVariations = new int[barCount];

        KMusicChordRhythmKind[] grooveFamilyA =
        {
            KMusicChordRhythmKind.DeepSparse,
            KMusicChordRhythmKind.Bounce,
            KMusicChordRhythmKind.PushPattern,
            KMusicChordRhythmKind.DenseGroove,
        };

        KMusicChordRhythmKind[] grooveFamilyB =
        {
            KMusicChordRhythmKind.Bounce,
            KMusicChordRhythmKind.LateOffbeats,
            KMusicChordRhythmKind.DeepSparse,
            KMusicChordRhythmKind.Hold,
        };

        bool useFamilyA = UnityEngine.Random.value < 0.58f;
        KMusicChordRhythmKind previousRhythm = KMusicChordRhythmKind.DeepSparse;
        int previousMode = isMinor ? 1 : 0;

        for (int bar = 0; bar < barCount; bar++)
        {
            int sectionBar = bar % 4;
            int degree = template[sectionBar];
            if (bar >= 4 && sectionBar == 2 && UnityEngine.Random.value < 0.35f)
                degree = isMinor ? 1 : 5;
            if (bar == barCount - 1)
                degree = 1;
            degrees[bar] = degree;

            KMusicPhraseKind phrase = sectionBar switch
            {
                0 => (UnityEngine.Random.value < 0.52f ? KMusicPhraseKind.Pulse : KMusicPhraseKind.Sparse),
                1 => (UnityEngine.Random.value < 0.50f ? KMusicPhraseKind.Arp : KMusicPhraseKind.Descend),
                2 => (UnityEngine.Random.value < 0.46f ? KMusicPhraseKind.Ascend : KMusicPhraseKind.Hook),
                _ => (bar == barCount - 1 ? KMusicPhraseKind.Sustain : (UnityEngine.Random.value < 0.55f ? KMusicPhraseKind.Pulse : KMusicPhraseKind.Answer)),
            };
            phrases[bar] = (int)phrase;

            var family = useFamilyA ? grooveFamilyA : grooveFamilyB;
            KMusicChordRhythmKind rhythm = family[Mathf.Clamp(sectionBar, 0, family.Length - 1)];
            float grooveRoll = UnityEngine.Random.value;
            if (sectionBar == 1 && grooveRoll < 0.30f) rhythm = KMusicChordRhythmKind.LateOffbeats;
            else if (sectionBar == 2 && grooveRoll < 0.25f) rhythm = KMusicChordRhythmKind.DenseGroove;
            else if (sectionBar == 3 && grooveRoll < 0.38f) rhythm = KMusicChordRhythmKind.Hold;

            if (bar == barCount - 1)
                rhythm = UnityEngine.Random.value < 0.55f ? KMusicChordRhythmKind.Hold : KMusicChordRhythmKind.DeepSparse;
            if (bar > 0 && rhythm == previousRhythm && UnityEngine.Random.value < 0.66f)
            {
                rhythm = rhythm switch
                {
                    KMusicChordRhythmKind.DeepSparse => KMusicChordRhythmKind.Bounce,
                    KMusicChordRhythmKind.Bounce => KMusicChordRhythmKind.PushPattern,
                    KMusicChordRhythmKind.PushPattern => KMusicChordRhythmKind.DenseGroove,
                    KMusicChordRhythmKind.DenseGroove => KMusicChordRhythmKind.LateOffbeats,
                    KMusicChordRhythmKind.Hold => KMusicChordRhythmKind.DeepSparse,
                    _ => KMusicChordRhythmKind.DeepSparse,
                };
            }
            rhythms[bar] = (int)rhythm;
            previousRhythm = rhythm;

            int chordMode = ChooseTechnoChordMode(isMinor, sectionBar, bar == barCount - 1);
            if (bar > 0 && chordMode == previousMode && UnityEngine.Random.value < 0.60f)
                chordMode = chordMode switch { 1 => 2, 2 => 3, 3 => isMinor ? 1 : 0, _ => 2 };
            chordModes[bar] = chordMode;
            previousMode = chordMode;

            KMusicChordVariationKind variation = KMusicChordVariationKind.None;
            float vRoll = UnityEngine.Random.value;
            if (bar == barCount - 1)
                variation = KMusicChordVariationKind.TailHold;
            else if (rhythm == KMusicChordRhythmKind.DeepSparse || rhythm == KMusicChordRhythmKind.Hold)
                variation = vRoll < 0.44f ? KMusicChordVariationKind.TailHold : KMusicChordVariationKind.None;
            else if (rhythm == KMusicChordRhythmKind.Bounce || rhythm == KMusicChordRhythmKind.PushPattern)
                variation = vRoll < 0.38f ? KMusicChordVariationKind.SplitPulse : (vRoll < 0.60f ? KMusicChordVariationKind.Anticipate : KMusicChordVariationKind.None);
            else
                variation = vRoll < 0.28f ? KMusicChordVariationKind.SkipFirst : (vRoll < 0.54f ? KMusicChordVariationKind.Anticipate : KMusicChordVariationKind.None);
            chordVariations[bar] = (int)variation;

            if ((bar % 4) == 3 && UnityEngine.Random.value < 0.24f)
                useFamilyA = !useFamilyA;
        }

        return new KMusicSongRandomizePlanData
        {
            version = 4,
            barCount = barCount,
            tonicSemitone = tonicSemitone,
            isMinor = isMinor ? 1 : 0,
            rootValueId = Mathf.Max(1, rootValueId),
            melodyBaseOctave = melodyBaseOctave,
            style = (int)KMusicGenreStyle.Techno,
            syncMask = 0,
            chordDegrees = degrees,
            phraseKinds = phrases,
            chordRhythmKinds = rhythms,
            chordModes = chordModes,
            chordVariationKinds = chordVariations,
        };
    }

    private static void Save(KMusicSongRandomizePlanData data)
    {
        if (data == null)
            return;
        ProjectPrefs.SetGlobalString(PrefKey, JsonUtility.ToJson(data));
        ProjectPrefs.SaveGlobal();
    }

    private static KMusicSongRandomizePlanData Load()
    {
        string json = ProjectPrefs.GetGlobalString(PrefKey, "");
        if (string.IsNullOrEmpty(json))
            return null;
        try
        {
            var loaded = JsonUtility.FromJson<KMusicSongRandomizePlanData>(json);
            if (loaded != null && loaded.version <= 0)
                loaded.version = 1;
            if (loaded != null && loaded.version < 3)
            {
                if (loaded.chordModes == null || loaded.chordModes.Length != loaded.barCount)
                    loaded.chordModes = new int[Mathf.Max(1, loaded.barCount)];
                if (loaded.chordVariationKinds == null || loaded.chordVariationKinds.Length != loaded.barCount)
                    loaded.chordVariationKinds = new int[Mathf.Max(1, loaded.barCount)];
            }
            if (loaded != null && loaded.version < 4)
            {
                loaded.syncMask = 0;
                if (loaded.style <= 0)
                    loaded.style = (int)KMusicGenreStyle.House;
            }
            return loaded;
        }
        catch
        {
            return null;
        }
    }

    private static KMusicSongRandomizePlanData Clone(KMusicSongRandomizePlanData data)
    {
        if (data == null)
            return null;
        var clone = new KMusicSongRandomizePlanData();
        clone.version = data.version;
        clone.barCount = data.barCount;
        clone.tonicSemitone = data.tonicSemitone;
        clone.isMinor = data.isMinor;
        clone.rootValueId = data.rootValueId;
        clone.melodyBaseOctave = data.melodyBaseOctave;
        clone.style = data.style;
        clone.syncMask = data.syncMask;
        clone.chordDegrees = data.chordDegrees != null ? (int[])data.chordDegrees.Clone() : null;
        clone.phraseKinds = data.phraseKinds != null ? (int[])data.phraseKinds.Clone() : null;
        clone.chordRhythmKinds = data.chordRhythmKinds != null ? (int[])data.chordRhythmKinds.Clone() : null;
        clone.chordModes = data.chordModes != null ? (int[])data.chordModes.Clone() : null;
        clone.chordVariationKinds = data.chordVariationKinds != null ? (int[])data.chordVariationKinds.Clone() : null;
        return clone;
    }

    private static int PositiveMod(int value, int mod)
    {
        if (mod <= 0)
            return 0;
        int result = value % mod;
        return result < 0 ? result + mod : result;
    }
}
