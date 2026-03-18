using System;
using UnityEngine;
using KMusic.Core;

[Serializable]
public class KMusicSongRandomizePlanData
{
    public int version = 2;
    public int barCount = 1;
    public int tonicSemitone = 0;
    public int isMinor = 1;
    public int rootValueId = 1;
    public int melodyBaseOctave = 1;
    public int style = 1;
    public int[] chordDegrees;
    public int[] phraseKinds;
    public int[] chordRhythmKinds;
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

public static class KMusicSongRandomizePlan
{
    private const string PrefKey = "kmusic.randomize.songplan.v2";
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

    private static KMusicSongRandomizePlanData Generate(int barCount)
    {
        // House mode: minor-leaning, strong center, loopable 4-bar language.
        bool isMinor = UnityEngine.Random.value < 0.82f;
        int tonicSemitone = (isMinor
            ? new[] { 0, 2, 4, 5, 7, 9 }
            : new[] { 0, 2, 5, 7, 9 })[UnityEngine.Random.Range(0, isMinor ? 6 : 5)];

        int rootBaseOctave = UnityEngine.Random.value < 0.75f ? 12 : 0;
        int rootValueId = 1 + tonicSemitone + rootBaseOctave;
        int melodyBaseOctave = UnityEngine.Random.value < 0.70f ? 2 : 1;

        int[][] templatesMajor =
        {
            new[] { 1, 5, 6, 4 },
            new[] { 1, 3, 4, 5 },
            new[] { 6, 4, 1, 5 },
            new[] { 1, 4, 2, 5 },
        };
        int[][] templatesMinor =
        {
            new[] { 1, 6, 3, 7 },
            new[] { 1, 7, 6, 7 },
            new[] { 1, 4, 1, 5 },
            new[] { 6, 7, 1, 7 },
            new[] { 1, 6, 7, 7 },
        };

        int[] template = (isMinor ? templatesMinor : templatesMajor)[UnityEngine.Random.Range(0, isMinor ? templatesMinor.Length : templatesMajor.Length)];

        int[] degrees = new int[barCount];
        int[] phrases = new int[barCount];
        int[] rhythms = new int[barCount];

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
        KMusicChordRhythmKind baseRhythm = useFamilyA ? grooveFamilyA[0] : grooveFamilyB[0];

        for (int bar = 0; bar < barCount; bar++)
        {
            int sectionBar = bar % 4;
            int degree = template[sectionBar];

            if (bar >= 4)
            {
                if (sectionBar == 1 && UnityEngine.Random.value < 0.35f)
                    degree = isMinor ? 7 : 3;
                if (sectionBar == 2 && UnityEngine.Random.value < 0.30f)
                    degree = isMinor ? 6 : 2;
            }
            if (bar == barCount - 1)
                degree = 1;
            degrees[bar] = degree;

            KMusicPhraseKind phrase;
            switch (sectionBar)
            {
                default:
                case 0:
                    phrase = (bar == 0 || UnityEngine.Random.value < 0.55f) ? KMusicPhraseKind.HouseCall : KMusicPhraseKind.Hook;
                    break;
                case 1:
                    phrase = UnityEngine.Random.value < 0.5f ? KMusicPhraseKind.Answer : KMusicPhraseKind.HouseStab;
                    break;
                case 2:
                    phrase = UnityEngine.Random.value < 0.5f ? KMusicPhraseKind.HouseLift : KMusicPhraseKind.Arp;
                    break;
                case 3:
                    phrase = (bar == barCount - 1)
                        ? KMusicPhraseKind.Sustain
                        : (UnityEngine.Random.value < 0.6f ? KMusicPhraseKind.Pulse : KMusicPhraseKind.HouseStab);
                    break;
            }
            phrases[bar] = (int)phrase;

            if (bar % phraseGroup == 0)
            {
                var family = useFamilyA ? grooveFamilyA : grooveFamilyB;
                int familyIndex = Mathf.Clamp(sectionBar, 0, family.Length - 1);
                baseRhythm = family[familyIndex];
                if (UnityEngine.Random.value < 0.18f)
                {
                    useFamilyA = !useFamilyA;
                    family = useFamilyA ? grooveFamilyA : grooveFamilyB;
                    baseRhythm = family[familyIndex];
                }
            }

            KMusicChordRhythmKind rhythm = baseRhythm;
            if ((bar % phraseGroup) == 1)
            {
                if (rhythm == KMusicChordRhythmKind.OffbeatStabs && UnityEngine.Random.value < 0.7f)
                    rhythm = KMusicChordRhythmKind.LateOffbeats;
                else if (rhythm == KMusicChordRhythmKind.Bounce && UnityEngine.Random.value < 0.55f)
                    rhythm = KMusicChordRhythmKind.PushPattern;
                else if (rhythm == KMusicChordRhythmKind.DeepSparse && UnityEngine.Random.value < 0.6f)
                    rhythm = KMusicChordRhythmKind.OffbeatStabs;
                else if (rhythm == KMusicChordRhythmKind.DenseGroove && UnityEngine.Random.value < 0.5f)
                    rhythm = KMusicChordRhythmKind.AnthemLift;
            }
            else if ((bar % phraseGroup) >= 2 && UnityEngine.Random.value < 0.35f)
            {
                rhythm = UnityEngine.Random.value < 0.5f ? KMusicChordRhythmKind.Bounce : KMusicChordRhythmKind.DeepSparse;
            }

            if (sectionBar == 3 && UnityEngine.Random.value < 0.25f)
                rhythm = KMusicChordRhythmKind.Hold;
            if (bar == barCount - 1)
                rhythm = UnityEngine.Random.value < 0.55f ? KMusicChordRhythmKind.Hold : KMusicChordRhythmKind.LateOffbeats;

            rhythms[bar] = (int)rhythm;
        }

        return new KMusicSongRandomizePlanData
        {
            version = 2,
            barCount = barCount,
            tonicSemitone = tonicSemitone,
            isMinor = isMinor ? 1 : 0,
            rootValueId = Mathf.Max(1, rootValueId),
            melodyBaseOctave = melodyBaseOctave,
            style = 1,
            chordDegrees = degrees,
            phraseKinds = phrases,
            chordRhythmKinds = rhythms,
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
        clone.chordDegrees = data.chordDegrees != null ? (int[])data.chordDegrees.Clone() : null;
        clone.phraseKinds = data.phraseKinds != null ? (int[])data.phraseKinds.Clone() : null;
        clone.chordRhythmKinds = data.chordRhythmKinds != null ? (int[])data.chordRhythmKinds.Clone() : null;
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
