using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZPunk.DAW
{
    /// <summary>
    /// Factory Drum Designer presets for the 27 bundled DrumKit assets.
    /// Factory values use exactly the detents of the existing UI. Small, bounded
    /// per-kit corrections were derived offline from the supplied WAV files:
    /// onset RMS, spectral centroid, 90 percent energy decay, and headroom.
    /// This adds no sample-analysis cost to a running phone and changes no DSP.
    /// The neutral preset is a strict bypass of all seven per-lane shaping values.
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

        // Tune (semitones), Tone (0..20), Decay (0..24), Pan (-10..10),
        // Trim (dB), Drive (0..20), Punch (0..20). Slot 0 is neutral.
        private struct LaneSpec
        {
            public readonly int tune, tone, decay, pan, trim, drive, punch;
            public LaneSpec(int tune, int tone, int decay, int pan, int trim, int drive, int punch)
            {
                this.tune = tune; this.tone = tone; this.decay = decay;
                this.pan = pan; this.trim = trim; this.drive = drive; this.punch = punch;
            }
        }
        private static readonly LaneSpec[][] Factory = {
            null,
            // TIGHT CLUB
            new[] {
                new LaneSpec(0, 10, 18, 0, 0, 3, 5), // Kick
                new LaneSpec(0, 12, 17, 0, 0, 2, 4), // Snare
                new LaneSpec(0, 11, 16, 0, 0, 2, 3), // Clap
                new LaneSpec(0, 12, 14, -1, 0, 1, 1), // Closed
                new LaneSpec(0, 11, 15, 2, -1, 1, 1), // Open
                new LaneSpec(0, 11, 18, -2, -2, 1, 1), // Ride
                new LaneSpec(0, 10, 14, 1, 0, 2, 2), // Rim
                new LaneSpec(0, 10, 18, 2, -2, 1, 1), // Crash
            },
            // DEEP HOUSE
            new[] {
                new LaneSpec(-1, 8, 23, 0, 0, 2, 2), // Kick
                new LaneSpec(0, 8, 22, 0, -1, 1, 1), // Snare
                new LaneSpec(0, 9, 20, 0, -1, 1, 1), // Clap
                new LaneSpec(0, 10, 18, -2, -1, 0, 1), // Closed
                new LaneSpec(0, 10, 20, 2, -1, 0, 0), // Open
                new LaneSpec(0, 10, 22, -2, -2, 0, 0), // Ride
                new LaneSpec(0, 9, 21, 2, -1, 0, 0), // Rim
                new LaneSpec(0, 9, 23, 1, -2, 0, 0), // Crash
            },
            // HARD TECHNO
            new[] {
                new LaneSpec(-1, 10, 20, 0, 0, 9, 10), // Kick
                new LaneSpec(0, 12, 16, 0, -1, 6, 7), // Snare
                new LaneSpec(0, 11, 17, 0, -1, 5, 5), // Clap
                new LaneSpec(0, 13, 14, -1, -2, 2, 2), // Closed
                new LaneSpec(0, 12, 14, 1, -2, 3, 2), // Open
                new LaneSpec(0, 12, 18, -2, -3, 3, 2), // Ride
                new LaneSpec(0, 11, 15, 2, -1, 5, 6), // Rim
                new LaneSpec(0, 10, 19, 2, -3, 3, 2), // Crash
            },
            // INDUSTRIAL
            new[] {
                new LaneSpec(-2, 8, 20, 0, -1, 11, 11), // Kick
                new LaneSpec(-1, 9, 15, 0, -1, 10, 8), // Snare
                new LaneSpec(-1, 8, 14, 0, -1, 10, 6), // Clap
                new LaneSpec(-1, 9, 12, -2, -2, 5, 3), // Closed
                new LaneSpec(-1, 8, 14, 2, -2, 6, 4), // Open
                new LaneSpec(0, 8, 17, -3, -3, 7, 3), // Ride
                new LaneSpec(-1, 9, 12, 2, -1, 10, 6), // Rim
                new LaneSpec(-1, 7, 17, 3, -3, 8, 4), // Crash
            },
            // WARM ANALOG
            new[] {
                new LaneSpec(-1, 8, 22, 0, 0, 4, 2), // Kick
                new LaneSpec(0, 8, 20, 0, 0, 3, 2), // Snare
                new LaneSpec(0, 9, 20, 0, -1, 3, 2), // Clap
                new LaneSpec(-1, 8, 20, -1, 0, 2, 1), // Closed
                new LaneSpec(-1, 8, 21, 1, -1, 2, 1), // Open
                new LaneSpec(0, 8, 23, -2, -2, 2, 1), // Ride
                new LaneSpec(0, 8, 18, 2, -1, 3, 2), // Rim
                new LaneSpec(0, 8, 22, 1, -3, 2, 1), // Crash
            },
            // CRUNCHY BREAKS
            new[] {
                new LaneSpec(0, 10, 19, 0, -1, 7, 6), // Kick
                new LaneSpec(0, 11, 15, 0, -1, 9, 8), // Snare
                new LaneSpec(0, 11, 15, 0, -1, 7, 5), // Clap
                new LaneSpec(0, 11, 18, -2, -1, 5, 3), // Closed
                new LaneSpec(0, 10, 17, 2, -1, 5, 3), // Open
                new LaneSpec(0, 11, 20, -2, -2, 5, 3), // Ride
                new LaneSpec(0, 11, 16, 3, -1, 7, 4), // Rim
                new LaneSpec(0, 10, 19, 3, -3, 4, 2), // Crash
            },
            // DARK & HEAVY
            new[] {
                new LaneSpec(-2, 7, 23, 0, 0, 7, 7), // Kick
                new LaneSpec(-1, 7, 18, 0, 0, 6, 6), // Snare
                new LaneSpec(0, 7, 18, 0, -1, 5, 5), // Clap
                new LaneSpec(0, 8, 16, -1, -2, 3, 2), // Closed
                new LaneSpec(-1, 7, 17, 2, -2, 4, 2), // Open
                new LaneSpec(-1, 7, 20, -2, -3, 4, 2), // Ride
                new LaneSpec(-1, 7, 15, 2, -1, 5, 4), // Rim
                new LaneSpec(-1, 7, 20, 2, -4, 4, 2), // Crash
            },
        };

        // Each kit matches the serialised ScriptableObject name Kit_01..Kit_27.
        // Corrections per lane: tone knob steps, decay knob steps, trim dB,
        // and drive knob steps. They do not change pitch or spatial position.
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
                int tone = s.tone, decay = s.decay, trim = s.trim, drive = s.drive;
                if (kitIndex >= 0)
                {
                    var correction = KitAdjustments[kitIndex];
                    int pos = lane * 4;
                    tone += correction[pos];
                    decay += correction[pos + 1];
                    trim += correction[pos + 2];
                    drive += correction[pos + 3];
                }
                result.Add(new DrumShapeData {
                    tune = s.tune,
                    tone = Mathf.Clamp(tone, 0, 20) / 20f,
                    decay = .04f + .96f * Mathf.Clamp(decay, 0, 24) / 24f,
                    pan = s.pan / 10f,
                    trim = Mathf.Clamp(trim, -12, 12),
                    drive = Mathf.Clamp(drive, 0, 20) / 20f,
                    punch = Mathf.Clamp(s.punch, 0, 20) / 20f
                });
            }
            return result;
        }

        public static List<DrumShapeData> Clone(List<DrumShapeData> shapes)
        {
            var copy = new List<DrumShapeData>(8);
            for (int i = 0; i < 8; ++i)
            {
                var s = shapes != null && i < shapes.Count ? shapes[i] : null;
                copy.Add(s == null ? new DrumShapeData() : new DrumShapeData {
                    tune = s.tune, tone = s.tone, decay = s.decay,
                    pan = s.pan, trim = s.trim, drive = s.drive, punch = s.punch
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
                    Mathf.Abs(a.punch - b.punch) > epsilon)
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
