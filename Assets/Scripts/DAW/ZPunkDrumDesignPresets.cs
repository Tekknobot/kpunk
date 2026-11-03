using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZPunk.DAW
{
    /// <summary>
    /// Factory Drum Designer presets for the 27 bundled DrumKit assets.
    /// Factory voicings deliberately use distinct pitch, brightness, drive and
    /// punch profiles. Modest per-kit tone/drive corrections come from the
    /// supplied samples, with a minimum tone floor for short bright percussion.
    /// Factory presets never set DECAY or TRIM; those controls stay exactly as the
    /// user left them, even when selecting NEUTRAL or changing the active kit.
    /// The shared drum DSP, mixer levels and master processing are not changed.
    /// GLUE is deliberately never modified (it processes the full master mix).
    /// </summary>
    public static class ZPunkDrumDesignPresets
    {
        public static readonly string[] Names = {
            "NEUTRAL", "TIGHT CLUB", "DEEP HOUSE", "HARD TECHNO",
            "INDUSTRIAL", "WARM ANALOG", "CRUNCHY BREAKS", "DARK & HEAVY"
        };

        public const int UserSlotCount = 4;
        private const string UserBankKey = "zpunk.drumDesigner.userPresets.v1";

        // Only these five parameters are factory-controlled. DECAY and TRIM
        // are never changed, including when NEUTRAL is selected.
        // Tune (semitones), Tone/Drive/Punch (0..20), Pan (-10..10).
        private struct LaneSpec
        {
            public readonly int tune, tone, pan, drive, punch;
            public LaneSpec(int tune, int tone, int pan, int drive, int punch)
            {
                this.tune = tune; this.tone = tone; this.pan = pan;
                this.drive = drive; this.punch = punch;
            }
        }
        private static readonly LaneSpec[][] Factory = {
            null, // NEUTRAL
            // TIGHT CLUB — Up-pitched, sharp attacks and crisp tops; little saturation.
            new[] {
                new LaneSpec(2, 15, 0, 1, 13), // Kick
                new LaneSpec(2, 16, 0, 1, 12), // Snare
                new LaneSpec(1, 16, 0, 1, 10), // Clap
                new LaneSpec(2, 18, -2, 0, 6), // Closed
                new LaneSpec(1, 17, 2, 0, 5), // Open
                new LaneSpec(1, 17, -3, 0, 5), // Ride
                new LaneSpec(2, 17, 3, 1, 10), // Rim
                new LaneSpec(1, 17, 3, 0, 4), // Crash
            },
            // DEEP HOUSE — Rounded, low-pitched foundation and gentle, open high percussion.
            new[] {
                new LaneSpec(-2, 7, 0, 2, 2), // Kick
                new LaneSpec(-1, 9, 0, 1, 1), // Snare
                new LaneSpec(-1, 9, 0, 1, 1), // Clap
                new LaneSpec(0, 11, -3, 0, 0), // Closed
                new LaneSpec(-1, 11, 3, 0, 0), // Open
                new LaneSpec(-1, 10, -4, 0, 0), // Ride
                new LaneSpec(-1, 9, 3, 1, 0), // Rim
                new LaneSpec(-1, 10, 4, 0, 0), // Crash
            },
            // HARD TECHNO — Bright attack, strong transient control and assertive saturation.
            new[] {
                new LaneSpec(-1, 14, 0, 6, 14), // Kick
                new LaneSpec(2, 17, 0, 5, 12), // Snare
                new LaneSpec(1, 16, 0, 4, 11), // Clap
                new LaneSpec(1, 18, -3, 2, 4), // Closed
                new LaneSpec(1, 17, 3, 2, 4), // Open
                new LaneSpec(1, 17, -4, 2, 4), // Ride
                new LaneSpec(2, 17, 4, 5, 9), // Rim
                new LaneSpec(0, 17, 4, 2, 4), // Crash
            },
            // INDUSTRIAL — Down-pitched metallic percussion, grit and contrasting upper harmonics.
            new[] {
                new LaneSpec(-3, 9, 0, 7, 12), // Kick
                new LaneSpec(-3, 11, 0, 7, 11), // Snare
                new LaneSpec(-2, 10, 0, 7, 10), // Clap
                new LaneSpec(-2, 14, -4, 5, 5), // Closed
                new LaneSpec(-3, 13, 4, 5, 5), // Open
                new LaneSpec(-2, 12, -5, 5, 5), // Ride
                new LaneSpec(-3, 11, 5, 7, 9), // Rim
                new LaneSpec(-2, 12, 4, 5, 5), // Crash
            },
            // WARM ANALOG — Soft top end, moderate warmth and almost no transient compression.
            new[] {
                new LaneSpec(1, 7, 0, 2, 1), // Kick
                new LaneSpec(1, 8, 0, 2, 1), // Snare
                new LaneSpec(1, 9, 0, 2, 1), // Clap
                new LaneSpec(-1, 9, -2, 1, 0), // Closed
                new LaneSpec(-1, 9, 2, 1, 0), // Open
                new LaneSpec(0, 9, -3, 1, 0), // Ride
                new LaneSpec(0, 9, 3, 2, 1), // Rim
                new LaneSpec(0, 9, 3, 1, 0), // Crash
            },
            // CRUNCHY BREAKS — Up-pitched snares and rim, brisk upper mids and gritty backbeat.
            new[] {
                new LaneSpec(3, 13, 0, 6, 8), // Kick
                new LaneSpec(3, 15, 0, 7, 11), // Snare
                new LaneSpec(2, 15, 0, 6, 9), // Clap
                new LaneSpec(2, 16, -4, 4, 3), // Closed
                new LaneSpec(2, 14, 4, 4, 3), // Open
                new LaneSpec(1, 15, -5, 4, 4), // Ride
                new LaneSpec(3, 16, 5, 6, 8), // Rim
                new LaneSpec(1, 14, 5, 4, 3), // Crash
            },
            // DARK & HEAVY — Deep pitch, dark tone, compressed punch; cymbals remain audible.
            new[] {
                new LaneSpec(-4, 5, 0, 4, 13), // Kick
                new LaneSpec(-3, 7, 0, 3, 10), // Snare
                new LaneSpec(-3, 8, 0, 3, 9), // Clap
                new LaneSpec(-2, 8, -3, 1, 4), // Closed
                new LaneSpec(-2, 8, 3, 2, 4), // Open
                new LaneSpec(-2, 8, -4, 2, 4), // Ride
                new LaneSpec(-3, 7, 4, 3, 8), // Rim
                new LaneSpec(-2, 8, 4, 2, 4), // Crash
            },
        };

        // Each kit matches the serialised ScriptableObject name Kit_01..Kit_27.
        // Four legacy calibration slots per lane: tone, decay, trim and drive.
        // DECAY and TRIM slots are intentionally ignored by factory presets.
        // Neither sample gain nor the shared decay DSP is changed.
        private static readonly sbyte[][] KitAdjustments = {
            // HMA 707: kick/snare/clap/closed/open/ride/rim/crash, 4 calibration steps each
            new sbyte[] { 1, 0, -1, -1, 0, 0, -1, -1, -1, 0, 0, -1, 0, 1, -1, -1, 0, 0, 1, 1, 0, 0, 1, 0, 0, 0, 1, 0, 0, 1, -1, 0 },
            // HMA 808: kick/snare/clap/closed/open/ride/rim/crash, 4 calibration steps each
            new sbyte[] { 2, -1, 0, -1, 0, 1, 1, 0, -1, 0, 0, 0, 0, 1, 1, 0, 0, -1, 0, 1, 0, 2, 0, 0, 2, 1, 1, 0, -1, 3, 0, 0 },
            // HMA 909: kick/snare/clap/closed/open/ride/rim/crash, 4 calibration steps each
            new sbyte[] { -1, -2, -1, 0, 1, 0, 1, 0, 0, 0, 2, 1, 0, 0, -1, -1, 0, 3, 0, 0, 0, 0, 2, 1, 2, 1, 1, 1, 0, 0, 0, 1 },
            // HMA Boombap: kick/snare/clap/closed/open/ride/rim/crash, 4 calibration steps each
            new sbyte[] { 1, -1, -1, -1, 2, 1, 0, -1, 0, 1, 1, 0, 0, 0, 1, 0, 0, 1, 1, 1, 1, 1, -1, -1, 0, 0, 0, -1, 1, -1, 1, 1 },
            // HMA Lofi: kick/snare/clap/closed/open/ride/rim/crash, 4 calibration steps each
            new sbyte[] { -1, 0, 0, -1, 2, -1, 0, -1, 0, 1, 0, -1, 1, 0, 1, 0, 2, 3, 0, 0, 1, -3, 1, 1, 0, -1, 0, -1, -1, 1, 0, -1 },
            // HMA Techno: kick/snare/clap/closed/open/ride/rim/crash, 4 calibration steps each
            new sbyte[] { 0, -2, 0, 0, 0, 1, 2, 0, 0, 0, 2, 1, 0, 1, 1, 1, 0, 2, 0, 0, 0, 0, -1, 0, 1, -3, 0, 0, 0, -2, -1, 1 },
            // HMA Triphop: kick/snare/clap/closed/open/ride/rim/crash, 4 calibration steps each
            new sbyte[] { 0, -1, -1, -1, 0, 0, 0, -1, -1, 3, 3, 1, 0, 0, -1, -1, 0, 2, 2, 1, 1, 0, 2, 0, 2, 0, 1, 1, 0, 1, 2, 1 },
            // ROL 909: kick/snare/clap/closed/open/ride/rim/crash, 4 calibration steps each
            new sbyte[] { 2, -1, -1, -1, 0, -1, 0, -1, -1, 0, 0, -1, 0, 0, -2, -1, 0, 3, 0, -1, 0, 0, -2, -1, 2, 1, -3, -1, 0, 0, -2, -1 },
            // ROL 808: kick/snare/clap/closed/open/ride/rim/crash, 4 calibration steps each
            new sbyte[] { 1, 1, 0, -1, 0, 1, 0, -1, -1, 0, 0, -1, 0, -1, -3, -1, 0, 2, -2, -1, 0, 0, -1, -1, 0, 1, -3, -1, 2, 3, -3, -1 },
            // DMX: kick/snare/clap/closed/open/ride/rim/crash, 4 calibration steps each
            new sbyte[] { -2, 1, 0, -1, 0, 1, -1, -1, 0, 0, -2, -1, 1, -1, -2, -1, 0, 1, -2, -1, 1, 1, -2, -1, 0, 0, -1, -1, 0, 0, -3, -1 },
            // Linn: kick/snare/clap/closed/open/ride/rim/crash, 4 calibration steps each
            new sbyte[] { 0, 1, -1, -1, 1, -1, -1, -1, 1, 0, -2, -1, 0, 1, -3, -1, 0, 0, -3, -1, 0, -2, -3, -1, 0, -1, -3, -1, 0, -1, -3, -1 },
            // Classic 909: kick/snare/clap/closed/open/ride/rim/crash, 4 calibration steps each
            new sbyte[] { 2, -1, 0, 0, 0, 0, 1, 0, -1, 0, 1, 0, 0, 0, 0, 1, 0, 3, 2, 1, 0, 0, 0, 1, 2, 1, -2, 1, 0, 0, 0, 1 },
            // Deep 808: kick/snare/clap/closed/open/ride/rim/crash, 4 calibration steps each
            new sbyte[] { 0, -3, 0, 0, 0, 1, 1, 0, -1, 0, 1, 0, 0, 1, 0, 1, 0, -1, 0, 1, 0, 2, 1, 1, 0, 1, -1, 1, 0, 0, 0, 1 },
            // 707 House: kick/snare/clap/closed/open/ride/rim/crash, 4 calibration steps each
            new sbyte[] { -2, 1, 0, 0, 0, 0, 0, 0, -1, 0, 1, 0, 0, -1, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1 },
            // Acid 606: kick/snare/clap/closed/open/ride/rim/crash, 4 calibration steps each
            new sbyte[] { 0, 1, 1, 0, 0, 1, 1, 0, -1, 0, 1, 0, 0, 1, 1, 1, 0, -3, 0, 1, 0, 1, 1, 1, 0, 0, 0, 1, -1, -1, 0, 1 },
            // Linn Funk: kick/snare/clap/closed/open/ride/rim/crash, 4 calibration steps each
            new sbyte[] { -1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -1, 0, 1, 0, -1, 0, 1, 0, -2, 0, 1, -1, 0, 0, 1, 0, -1, -1, 1 },
            // DMX Electro: kick/snare/clap/closed/open/ride/rim/crash, 4 calibration steps each
            new sbyte[] { -2, 1, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1, -1, 0, 1, 0, 1, 0, 1, 1, 1, 0, 1, 0, 0, 1, 1, 0, 0, 0, 1 },
            // SP12 Boom Bap: kick/snare/clap/closed/open/ride/rim/crash, 4 calibration steps each
            new sbyte[] { -2, 1, 0, 0, 0, -3, -1, 0, 0, 0, 0, 0, 1, -1, 1, 1, 0, 1, 0, 1, 0, -2, -1, 1, 0, 0, 2, 1, 0, 0, 0, 1 },
            // RX5 Dance: kick/snare/clap/closed/open/ride/rim/crash, 4 calibration steps each
            new sbyte[] { -2, 1, 1, 0, 1, -1, 0, 0, 0, 0, 0, 0, 0, -1, 1, 1, 1, -2, 0, 1, 1, 1, 0, 1, 0, 1, 2, 1, 0, 2, 0, 1 },
            // CR78 Vintage: kick/snare/clap/closed/open/ride/rim/crash, 4 calibration steps each
            new sbyte[] { -1, 2, 0, 0, 0, 3, 1, 0, 0, 0, 0, 0, 0, 1, -1, 1, 0, 2, 1, 1, 0, -2, -1, 1, -1, 0, 0, 1, 0, 0, 0, 1 },
            // Simmons Electronic: kick/snare/clap/closed/open/ride/rim/crash, 4 calibration steps each
            new sbyte[] { 2, 1, 1, 0, 1, -2, -1, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, -3, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1 },
            // 90s Eurodance: kick/snare/clap/closed/open/ride/rim/crash, 4 calibration steps each
            new sbyte[] { 2, -1, 0, 0, 0, 0, 0, 0, -1, 0, 1, 0, 0, -1, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 2, 1, 0, 0, 0, 1 },
            // Deep House: kick/snare/clap/closed/open/ride/rim/crash, 4 calibration steps each
            new sbyte[] { -2, -3, -1, 0, 0, -1, 1, 0, 0, 0, 0, 0, 0, -1, -1, 1, 0, 0, 1, 1, 0, -2, -1, 1, 0, 1, -1, 1, 0, 0, 0, 1 },
            // Industrial Techno: kick/snare/clap/closed/open/ride/rim/crash, 4 calibration steps each
            new sbyte[] { 2, -1, 0, 0, 0, -2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, -3, -1, 1, 0, 0, 0, 1, 0, 0, 1, 1, -1, -1, 0, 1 },
            // Old School Hip Hop: kick/snare/clap/closed/open/ride/rim/crash, 4 calibration steps each
            new sbyte[] { -2, -1, 0, 0, 0, -3, -1, 0, 0, 0, 0, 0, 0, -1, 0, 1, 0, 2, 0, 1, 0, -2, -1, 1, 0, 0, 2, 1, 0, 0, 0, 1 },
            // Electro Funk: kick/snare/clap/closed/open/ride/rim/crash, 4 calibration steps each
            new sbyte[] { -2, 1, 1, 0, 2, 1, 0, 0, 0, 0, 0, 0, 0, -1, 0, 1, 0, 0, 0, 1, 0, -3, 0, 1, 0, 0, 0, 1, 0, -1, -1, 1 },
            // ZPUNK Hybrid: kick/snare/clap/closed/open/ride/rim/crash, 4 calibration steps each
            new sbyte[] { 2, -1, 0, 0, 0, -3, -1, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, -3, -1, 1, 0, 0, 0, 1, 0, 1, -1, 1, 0, 0, 0, 1 },
        };

        [Serializable]
        private sealed class SavedBank { public List<SavedPreset> presets = new List<SavedPreset>(); }
        [Serializable]
        private sealed class SavedPreset
        {
            public int slot;
            public List<DrumShapeData> shapes = new List<DrumShapeData>();
        }

        public static string UserLabel(int slot) { return "USER " + (slot + 1); }

        public static List<DrumShapeData> CreateFactory(int index, string kitId)
        {
            if (index < 0 || index >= Names.Length) return null;
            var result = new List<DrumShapeData>(8);
            int kitIndex = -1;
            if (!string.IsNullOrEmpty(kitId) && kitId.StartsWith("Kit_", StringComparison.OrdinalIgnoreCase))
            {
                int n;
                if (int.TryParse(kitId.Substring(4), out n) && n >= 1 && n <= KitAdjustments.Length)
                    kitIndex = n - 1;
            }
            for (int lane = 0; lane < 8; ++lane)
            {
                if (index == 0) { result.Add(new DrumShapeData()); continue; }
                LaneSpec s = Factory[index][lane];
                int tone = s.tone, drive = s.drive;
                if (kitIndex >= 0)
                {
                    var correction = KitAdjustments[kitIndex];
                    int pos = lane * 4;
                    tone += correction[pos];
                    drive += correction[pos + 3];
                }
                // Bright and very short percussion still needs enough bandwidth
                // to remain audible. This caps darkening, not the source gain.
                int minTone = lane == 0 ? 4 : (lane <= 2 || lane == 6 ? 6 : 8);
                result.Add(new DrumShapeData {
                    tune = s.tune,
                    tone = Mathf.Clamp(tone, minTone, 20) / 20f,
                    // Neutral placeholders. UI merges the current lane's values
                    // before applying any factory style, including NEUTRAL.
                    decay = 1f,
                    pan = s.pan / 10f,
                    trim = 0f,
                    drive = Mathf.Clamp(drive, 0, 20) / 20f,
                    punch = Mathf.Clamp(s.punch, 0, 20) / 20f,
                    characterFx = FactoryEffect(index, lane),
                    characterAmount = FactoryAmount(index, lane)
                });
            }
            return result;
        }

        // Each drum can have only one character effect, even in factory kits.
        // DECAY and TRIM are never adjusted by these assignments.
        private static int FactoryEffect(int style, int lane)
        {
            switch (style)
            {
                case 1: return lane == 1 || lane == 2 ? 3 : 0; // tight club snare/clap ambience
                case 2: return lane == 0 ? 2 : (lane == 6 ? 5 : (lane == 2 ? 3 : 0));
                case 3: return lane == 0 ? 2 : (lane == 1 ? 6 : (lane == 6 ? 4 : 0));
                case 4: return lane == 0 ? 2 : (lane == 1 ? 6 : (lane == 2 ? 1 : (lane == 6 ? 4 : 0)));
                case 5: return lane == 3 || lane == 4 ? 1 : (lane == 2 ? 3 : 0);
                case 6: return lane == 1 || lane == 3 ? 1 : (lane == 2 ? 6 : (lane == 6 ? 4 : 0));
                case 7: return lane == 0 ? 2 : (lane == 1 ? 6 : (lane == 6 ? 5 : 0));
                default: return 0;
            }
        }
        private static float FactoryAmount(int style, int lane)
        {
            if (FactoryEffect(style, lane) == 0) return 0f;
            switch (style)
            {
                case 1: return .34f;
                case 2: return lane == 0 ? .36f : .40f;
                case 3: return lane == 0 ? .65f : .42f;
                case 4: return lane == 0 ? .55f : .72f;
                case 5: return .25f;
                case 6: return .62f;
                case 7: return lane == 0 ? .65f : .58f;
                default: return 0f;
            }
        }

        // Applying a factory style changes only sound character. It must never
        // shorten a drum hit or overwrite its independently chosen trim level.
        public static List<DrumShapeData> PreserveDecayAndTrim(
            List<DrumShapeData> factory, List<DrumShapeData> current)
        {
            var result = Clone(factory);
            for (int lane = 0; lane < 8; ++lane)
            {
                var existing = current != null && lane < current.Count ? current[lane] : null;
                if (existing == null) continue;
                result[lane].decay = existing.decay;
                result[lane].trim = existing.trim;
            }
            return result;
        }

        // Factory identification ignores DECAY/TRIM because factory presets do
        // not own either control. User presets still use exact Matches().
        public static bool MatchesFactory(List<DrumShapeData> current, List<DrumShapeData> factory)
        {
            return factory != null && Matches(current, PreserveDecayAndTrim(factory, current));
        }

        public static List<DrumShapeData> Clone(List<DrumShapeData> shapes)
        {
            var copy = new List<DrumShapeData>(8);
            for (int i = 0; i < 8; ++i)
            {
                var s = shapes != null && i < shapes.Count ? shapes[i] : null;
                copy.Add(s == null ? new DrumShapeData() : new DrumShapeData {
                    tune = s.tune, tone = s.tone, decay = s.decay,
                    pan = s.pan, trim = s.trim, drive = s.drive, punch = s.punch,
                    characterFx = s.characterFx, characterAmount = s.characterAmount
                });
            }
            return copy;
        }

        public static bool Matches(List<DrumShapeData> current, List<DrumShapeData> candidate)
        {
            if (candidate == null) return false;
            for (int i = 0; i < 8; ++i)
            {
                var a = current != null && i < current.Count ? current[i] : null;
                var b = i < candidate.Count ? candidate[i] : null;
                if (a == null) a = new DrumShapeData();
                if (b == null) b = new DrumShapeData();
                const float epsilon = .015f;
                if (Mathf.Abs(a.tune - b.tune) > epsilon ||
                    Mathf.Abs(a.tone - b.tone) > epsilon ||
                    Mathf.Abs(a.decay - b.decay) > epsilon ||
                    Mathf.Abs(a.pan - b.pan) > epsilon ||
                    Mathf.Abs(a.trim - b.trim) > epsilon ||
                    Mathf.Abs(a.drive - b.drive) > epsilon ||
                    Mathf.Abs(a.punch - b.punch) > epsilon ||
                    a.characterFx != b.characterFx ||
                    Mathf.Abs(a.characterAmount - b.characterAmount) > epsilon)
                    return false;
            }
            return true;
        }

        private static SavedBank ReadBank()
        {
            try
            {
                string json = PlayerPrefs.GetString(UserBankKey, "");
                if (!string.IsNullOrEmpty(json))
                {
                    var saved = JsonUtility.FromJson<SavedBank>(json);
                    if (saved != null && saved.presets != null) return saved;
                }
            }
            catch (Exception e) { Debug.LogWarning("[ZPUNK Drum Presets] Read: " + e.Message); }
            return new SavedBank();
        }

        public static List<DrumShapeData> LoadUser(int slot)
        {
            if (slot < 0 || slot >= UserSlotCount) return null;
            var bank = ReadBank();
            foreach (var preset in bank.presets)
                if (preset != null && preset.slot == slot && preset.shapes != null && preset.shapes.Count == 8)
                    return Clone(preset.shapes);
            return null;
        }

        public static bool SaveUser(int slot, List<DrumShapeData> shapes)
        {
            if (slot < 0 || slot >= UserSlotCount) return false;
            try
            {
                var bank = ReadBank();
                bank.presets.RemoveAll(p => p == null || p.slot == slot);
                bank.presets.Add(new SavedPreset { slot = slot, shapes = Clone(shapes) });
                PlayerPrefs.SetString(UserBankKey, JsonUtility.ToJson(bank));
                PlayerPrefs.Save();
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ZPUNK Drum Presets] Save failed: " + e.Message);
                return false;
            }
        }
    }
}
