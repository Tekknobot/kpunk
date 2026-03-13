using System;

namespace KMusic.Core
{
    /// <summary>
    /// One-bar snapshot in your current architecture:
    /// - Drums: 16-byte bitmask (8 lanes)
    /// - Chops: 16 ints (1..16 chopId, 0 empty)
    /// - Keys:  16 ints (StepGrid valueId, 0 empty)
    /// </summary>
    [Serializable]
    public sealed class PatternData
    {
        public const int Steps = 16;

        public byte[] drumMask;   // len 16
        public byte[] drumVelocityData; // len 32 packed 2-bit tiers
        public int[] sampleSteps; // len 16
        public int[] seqSteps;    // len 16
        public int[] padSteps;    // len 16
        public int padChordMode;

        public PatternData()
        {
            drumMask = new byte[Steps];
            drumVelocityData = new byte[Steps * 2];
            sampleSteps = new int[Steps];
            seqSteps = new int[Steps];
            padSteps = new int[Steps];
            padChordMode = 0;
        }

        public PatternData Clone()
        {
            var p = new PatternData();
            if (drumMask != null) Array.Copy(drumMask, p.drumMask, Math.Min(drumMask.Length, p.drumMask.Length));
            if (drumVelocityData != null) Array.Copy(drumVelocityData, p.drumVelocityData, Math.Min(drumVelocityData.Length, p.drumVelocityData.Length));
            if (sampleSteps != null) Array.Copy(sampleSteps, p.sampleSteps, Math.Min(sampleSteps.Length, p.sampleSteps.Length));
            if (seqSteps != null) Array.Copy(seqSteps, p.seqSteps, Math.Min(seqSteps.Length, p.seqSteps.Length));
            if (padSteps != null) Array.Copy(padSteps, p.padSteps, Math.Min(padSteps.Length, p.padSteps.Length));
            p.padChordMode = padChordMode;
            return p;
        }
    }
}
