using System;

namespace ZPunk.DAW
{
    /// <summary>
    /// Factory 303 patches are code-only: no prefabs or bank files required.
    /// Each entry recalls BOTH the oscillator/filter and the dedicated FX rack.
    /// Selecting a preset copies values into the project's existing AcidState.
    /// </summary>
    public static class ZPunkAcidPresets
    {
        private sealed class Factory
        {
            public readonly string name, category;
            public readonly bool square;
            public readonly float cutoff, resonance, env, decay, accent, drive, glide;
            public readonly float distortion, chorus, delay, feedback, reverb, room;
            public readonly int rate;
            public Factory(string name, string category, bool square,
                float cutoff, float resonance, float env, float decay, float accent,
                float drive, float glide, float distortion, float chorus, float delay,
                float feedback, float reverb, float room, int rate)
            {
                this.name = name; this.category = category; this.square = square;
                this.cutoff = cutoff; this.resonance = resonance; this.env = env;
                this.decay = decay; this.accent = accent; this.drive = drive;
                this.glide = glide; this.distortion = distortion; this.chorus = chorus;
                this.delay = delay; this.feedback = feedback; this.reverb = reverb;
                this.room = room; this.rate = rate;
            }
        }

        // Balanced, deliberately varied starting points. No licensed Roland patches.
        private static readonly Factory[] Banks = {
            // name, group, square, cutoff, resonance, env, decay, accent, drive, glide,
            // rack drive, chorus, delay, feedback, reverb, room, rate
            new Factory("ZOR 303 INIT", "ALL", false, .52f,.72f,.60f,.40f,.72f,.20f,.45f, 0f,0f,0f,0f,0f,0f,0),
            new Factory("ZOR TECHNO WAREHOUSE", "TECHNO", false, .37f,.88f,.82f,.32f,.85f,.28f,.55f, .46f,.05f,.24f,.43f,.10f,.28f,0),
            new Factory("ZOR TECHNO STEEL", "TECHNO", true, .44f,.91f,.72f,.22f,.90f,.43f,.48f, .65f,.00f,.13f,.25f,.06f,.19f,1),
            new Factory("ZOR TECHNO PULSE", "TECHNO", false, .56f,.77f,.57f,.18f,.70f,.22f,.70f, .30f,.17f,.20f,.45f,.08f,.23f,1),
            new Factory("ZOR TECHNO RAVE", "TECHNO", false, .68f,.85f,.83f,.31f,.86f,.46f,.62f, .57f,.10f,.37f,.46f,.15f,.34f,0),
            new Factory("ZOR HOUSE RUBBER", "HOUSE", true, .57f,.70f,.49f,.51f,.59f,.08f,.39f, .08f,.36f,.14f,.18f,.24f,.40f,2),
            new Factory("ZOR HOUSE SQUELCH", "HOUSE", false, .63f,.94f,.80f,.26f,.80f,.20f,.31f, .18f,.06f,.32f,.24f,.16f,.37f,0),
            new Factory("ZOR HOUSE DEEP", "HOUSE", true, .36f,.54f,.43f,.71f,.48f,.06f,.54f, .04f,.31f,.12f,.24f,.37f,.70f,2),
            new Factory("ZOR MOTION DUB", "MOTION", true, .30f,.68f,.58f,.67f,.72f,.14f,.72f, .22f,.22f,.61f,.72f,.28f,.61f,2),
            new Factory("ZOR MOTION TUNNEL", "MOTION", false, .46f,.82f,.64f,.55f,.75f,.24f,.72f, .41f,.39f,.51f,.64f,.42f,.78f,3),
            new Factory("ZOR MOTION LIQUID", "MOTION", true, .63f,.69f,.80f,.43f,.62f,.07f,.85f, .03f,.66f,.31f,.35f,.47f,.63f,0),
            new Factory("ZOR MOTION NIGHT", "MOTION", false, .34f,.76f,.50f,.68f,.54f,.12f,.70f, .19f,.44f,.37f,.48f,.62f,.86f,2),
            // Opt-in demonstration patches for the new pedal/echo voicing.
            new Factory("ZOR 303 METAL EDGE", "TECHNO", false, .48f,.88f,.78f,.31f,.84f,.32f,.55f, .76f,.00f,.46f,.55f,.05f,.22f,0),
            new Factory("ZOR 303 COMP PULSE", "HOUSE", true, .53f,.72f,.57f,.40f,.76f,.15f,.43f, .52f,.00f,.22f,.31f,.10f,.25f,1),
            new Factory("ZOR 303 TAPE SQUELCH", "MOTION", false, .38f,.84f,.78f,.60f,.86f,.21f,.61f, .38f,.05f,.68f,.69f,.12f,.35f,2)
        };

        // Conservative output balancing across factory patches. These trims
        // compensate approximately for the different drive/filter/FX chains;
        // they are not intended as measured loudness normalization.
        // They do NOT touch the user-facing LEVEL control on preset changes.
        private static readonly float[] FactoryOutputTrims = {
            .92f, .73f, .59f, .77f, .58f,
            .84f, .75f, .91f, .71f, .63f,
            .78f, .70f, .55f, .79f, .68f
        };

        public static int Count => Banks.Length;
        public static string NameAt(int index) => index >= 0 && index < Count ? Banks[index].name : null;
        public static string CategoryAt(int index) => index >= 0 && index < Count ? Banks[index].category : null;
        public static int IndexOf(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            for (int i = 0; i < Count; ++i)
                if (string.Equals(name, Banks[i].name, StringComparison.Ordinal)) return i;
            return -1;
        }
        public static string RateLabel(int rate)
        {
            switch (rate)
            {
                case 1: return "1/16";
                case 2: return "1/4";
                case 3: return "3/16";
                default: return "1/8";
            }
        }
        public static string DistortionLabel(int type)
        {
            switch (type)
            {
                case 1: return "OVERDRIVE";
                case 2: return "DISTORTION";
                case 3: return "FUZZ";
                case 4: return "METAL";
                case 5: return "DIODE";
                case 6: return "HARD CLIP";
                case 7: return "COMPRESSOR";
                case 8: return "DRIVE";
                default: return "CLASSIC";
            }
        }
        public static string DelayModeLabel(int mode)
        {
            switch (mode)
            {
                case 1: return "PING-PONG";
                case 2: return "DUB";
                case 3: return "STEREO";
                case 4: return "TAPE ECHO";
                case 5: return "PAN DELAY";
                case 6: return "DIGITAL";
                default: return "ORIGINAL";
            }
        }
        public static void Load(int index, AcidState target)
        {
            if (target == null || index < 0 || index >= Count) return;
            var p = Banks[index];
            target.square = p.square;
            target.tune = .5f;
            target.cutoff = p.cutoff;
            target.resonance = p.resonance;
            target.envMod = p.env;
            target.decay = p.decay;
            target.accent = p.accent;
            target.drive = p.drive;
            target.glide = p.glide;
            // Presets never overwrite a level the user lowered. The first
            // preset applies a comfortable 72% default only for untouched
            // unity-gain ACID tracks (including tracks made in older builds).
            if (!target.outputLevelUserAdjusted &&
                (!target.outputLevelSet || target.outputLevel >= .999f))
            {
                target.outputLevelSet = true;
                target.outputLevel = .72f;
            }
            target.patchOutputTrimSet = true;
            target.patchOutputTrim = FactoryOutputTrims[index];
            target.refined303Engine = true;
            target.fxDistortion = p.distortion;
            target.fxDistControlsInitialized = true;
            // Different patch families use different grit/tone blends.
            target.fxDistTone = index == 2 || index == 4 || index == 12 ? .76f :
                index == 7 || index == 8 ? .30f : index == 9 ? .40f : .50f;
            target.fxDistMix = index == 0 ? .85f :
                index == 2 || index == 4 || index == 12 ? .92f :
                index == 5 || index == 7 ? .42f : .70f;
            target.fxDistBite = index == 2 ? .72f : index == 12 ? .36f : index == 4 ? .52f :
                index == 1 || index == 9 ? .28f : .08f;
            // Every factory patch recalls a deliberate pedal colour, with INIT
            // bypassed. Old saved patches retain type 0 = CLASSIC instead.
            target.fxDistType = index == 2 || index == 9 || index == 12 ? 4 : // METAL
                index == 13 ? 7 :                     // COMPRESSOR
                index == 14 ? 8 :                     // DRIVE
                index == 4 ? 3 :                      // FUZZ
                index == 7 ? 7 :                      // COMPRESSOR / deep house
                index == 10 ? 8 :                     // DRIVE / liquid
                index == 3 || index == 6 ? 5 :        // DIODE
                index == 1 || index == 5 || index == 11 ? 1 :
                index == 8 ? 2 : 0;
            target.fx303DriveModel = index != 0; // factory patches use new 303 FX
            target.fxChorus = p.chorus;
            // New factory selections use much clearer echoes without modifying
            // the quieter delay processing of previously saved projects.
            target.fxDelay = index == 2 ? .38f : Math.Min(1f, p.delay * 1.30f);
            target.fxFeedback = index == 2 ? .48f : p.feedback;
            target.fxReverb = p.reverb;
            target.fxRoom = p.room;
            target.fxDelayRate = p.rate;
            target.fxDelayMode = index == 0 ? 0 :
                index == 8 || index == 11 || index == 14 ? 4 : // tape repeats
                index == 2 || index == 9 || index == 12 ? 5 :  // wide rhythmic pan echoes
                index == 7 || index == 10 || index == 13 ? 6 : // clean digital repeats
                index == 5 ? 3 : 1;
            target.fx303DelayModel = index != 0;
            target.fxDelayTone = target.fxDelayMode == 4 ? .32f :
                target.fxDelayMode == 5 ? .52f : .68f;
            target.patchName = p.name;
            target.patchModified = false;
        }
    }
}
