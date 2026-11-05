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
            new Factory("ZOR 303 TAPE SQUELCH", "MOTION", false, .38f,.84f,.78f,.60f,.86f,.21f,.61f, .38f,.05f,.68f,.69f,.12f,.35f,2),
            // Original 1990s acid-house / warehouse patches (not song copies).
            // Contrast is intentional: dry raw acid, hollow square, saturated
            // lead, hard techno grit, muted funk, rhythmic rave, late-night dub.
            new Factory("ZOR 90S RAW 303", "HOUSE", false, .36f,.80f,.82f,.31f,.83f,.16f,.49f, .00f,.00f,.00f,.15f,.00f,.18f,0),
            new Factory("ZOR 90S RUBBER", "HOUSE", true, .34f,.74f,.66f,.46f,.80f,.12f,.63f, .13f,.00f,.04f,.18f,.00f,.22f,1),
            new Factory("ZOR 90S DIRTY FUNK", "HOUSE", false, .32f,.89f,.88f,.38f,.92f,.24f,.68f, .66f,.00f,.04f,.17f,.00f,.16f,0),
            new Factory("ZOR 90S ACID SCREAM", "TECHNO", false, .50f,.97f,.92f,.26f,.98f,.36f,.78f, .82f,.00f,.06f,.27f,.00f,.20f,1),
            new Factory("ZOR 90S WAREHOUSE", "TECHNO", false, .30f,.88f,.79f,.29f,.85f,.31f,.59f, .56f,.00f,.12f,.24f,.02f,.16f,0),
            new Factory("ZOR 90S RAVE SQUARE", "TECHNO", true, .46f,.90f,.80f,.22f,.90f,.20f,.71f, .39f,.00f,.18f,.23f,.03f,.15f,1),
            new Factory("ZOR 90S NIGHT ACID", "MOTION", true, .25f,.78f,.69f,.58f,.76f,.09f,.74f, .20f,.00f,.35f,.41f,.04f,.23f,2),
            // New original 90s-inspired sounds.  They opt into the circuit voice,
            // without silently changing previous presets or saved projects.
            new Factory("ZOR TAILSPIN TWIST", "MOTION", false, .39f,.91f,.91f,.35f,.94f,.31f,.82f, .66f,.00f,.17f,.29f,.02f,.19f,1),
            new Factory("ZOR BLOOD RAVE", "TECHNO", false, .32f,.96f,.91f,.26f,.96f,.44f,.61f, .84f,.00f,.05f,.21f,.01f,.16f,1),
            new Factory("ZOR HARD ACID 1992", "TECHNO", true, .35f,.94f,.89f,.22f,.93f,.35f,.67f, .74f,.00f,.04f,.25f,.00f,.16f,1),
            new Factory("ZOR CHICAGO SQUELCH", "HOUSE", false, .29f,.89f,.87f,.40f,.83f,.18f,.71f, .19f,.00f,.00f,.10f,.00f,.15f,0),
            new Factory("ZOR REACTOR SLIDE", "MOTION", true, .48f,.87f,.79f,.48f,.91f,.28f,.95f, .46f,.00f,.24f,.39f,.03f,.23f,2),
            new Factory("ZOR CIRCUIT RAW", "HOUSE", false, .42f,.91f,.85f,.31f,.85f,.18f,.52f, .00f,.00f,.00f,.15f,.00f,.16f,0)
        };

        // Conservative output balancing across factory patches. These trims
        // compensate approximately for the different drive/filter/FX chains;
        // they are not intended as measured loudness normalization.
        // They do NOT touch the user-facing LEVEL control on preset changes.
        private static readonly float[] FactoryOutputTrims = {
            .92f, .73f, .59f, .77f, .58f,
            .84f, .75f, .91f, .71f, .63f,
            .78f, .70f, .55f, .79f, .68f,
            .88f, .83f, .59f, .49f, .62f, .67f, .79f,
            .59f, .49f, .54f, .77f, .61f, .83f
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
            target.vintage303Engine = true; // older saved sounds remain unchanged
            target.circuit303Engine = index >= 22; // only freshly added patches
            target.fxDistortion = p.distortion;
            target.fxDistControlsInitialized = true;
            // Different patch families use different grit/tone blends.
            target.fxDistTone = index == 2 || index == 4 || index == 12 ? .76f :
                index == 7 || index == 8 ? .30f : index == 9 ? .40f :
                index == 17 || index == 19 ? .64f :
                index == 18 ? .72f : index == 21 ? .32f : .50f;
            // INIT is clean because rack DRIVE is zero, not because MIX is reduced.
            // At 100% wet, raising DRIVE immediately reveals the selected
            // distortion type without an additional MIX adjustment.
            target.fxDistMix = index == 0 || index == 15 ? 1f :
                index == 2 || index == 4 || index == 12 || index == 17 || index == 18 ? .92f :
                index == 5 || index == 7 || index == 16 ? .42f : .70f;
            target.fxDistBite = index == 2 ? .72f : index == 12 ? .36f : index == 4 ? .52f :
                index == 18 ? .55f : index == 17 ? .38f : index == 19 ? .44f :
                index == 1 || index == 9 ? .28f : .08f;
            // Every factory patch recalls a deliberate pedal colour, with INIT
            // bypassed. Old saved patches retain type 0 = CLASSIC instead.
            target.fxDistType = index == 17 ? 2 :                   // 90S DIRTY FUNK: clipping
                index == 18 ? 3 :                   // 90S SCREAM: fuzz
                index == 19 ? 4 :                   // 90S WAREHOUSE: metal
                index == 20 ? 5 :                   // 90S RAVE: diode
                index == 21 ? 8 :                   // 90S NIGHT: drive
                index == 16 ? 1 :                   // 90S RUBBER: overdrive
                index == 2 || index == 9 || index == 12 ? 4 : // METAL
                index == 13 ? 7 :                     // COMPRESSOR
                index == 14 ? 8 :                     // DRIVE
                index == 4 ? 3 :                      // FUZZ
                index == 7 ? 7 :                      // COMPRESSOR / deep house
                index == 10 ? 8 :                     // DRIVE / liquid
                index == 3 || index == 6 ? 5 :        // DIODE
                index == 1 || index == 5 || index == 11 ? 1 :
                index == 8 ? 2 : 0;
            target.fx303DriveModel = index != 0 && index != 15; // dry RAW is intentionally clean
            target.fxChorus = p.chorus;
            // New factory selections use much clearer echoes without modifying
            // the quieter delay processing of previously saved projects.
            target.fxDelay = index == 2 ? .38f : Math.Min(1f, p.delay * 1.30f);
            target.fxFeedback = index == 2 ? .48f : p.feedback;
            target.fxReverb = p.reverb;
            target.fxRoom = p.room;
            target.fxDelayRate = p.rate;
            target.fxDelayMode = index == 0 || index == 15 ? 0 :
                index == 8 || index == 11 || index == 14 ? 4 : // tape repeats
                index == 2 || index == 9 || index == 12 ? 5 :  // wide rhythmic pan echoes
                index == 7 || index == 10 || index == 13 ? 6 : // clean digital repeats
                index == 5 ? 3 : 1;
            target.fx303DelayModel = index != 0 && index != 15;
            target.fxDelayTone = target.fxDelayMode == 4 ? .32f :
                target.fxDelayMode == 5 ? .52f : .68f;
            if (index >= 22)
            {
                // Deliberately distinct rack voices, not licensed factory
                // settings. Keep Tailspin-inspired motion and Blood Rave dry.
                switch (index)
                {
                    case 22: // moving, sharp distorted saw with modest echo
                        target.fxDistType = 2; target.fxDistTone = .69f;
                        target.fxDistMix = .90f; target.fxDistBite = .46f;
                        target.fxDelayMode = 4; target.fxDelayTone = .40f; break;
                    case 23: // aggressive late-90s blood-rave style
                        target.fxDistType = 4; target.fxDistTone = .63f;
                        target.fxDistMix = .96f; target.fxDistBite = .54f;
                        target.fxDelayMode = 1; break;
                    case 24: // gnarly square-wave driven acid
                        target.fxDistType = 3; target.fxDistTone = .57f;
                        target.fxDistMix = .88f; target.fxDistBite = .43f;
                        target.fxDelayMode = 1; break;
                    case 25: // low-drive Chicago squelch
                        target.fxDistType = 1; target.fxDistTone = .43f;
                        target.fxDistMix = .37f; target.fxDistBite = .10f;
                        target.fxDelayMode = 1; break;
                    case 26: // high-slide rubber square with echo
                        target.fxDistType = 5; target.fxDistTone = .55f;
                        target.fxDistMix = .66f; target.fxDistBite = .24f;
                        target.fxDelayMode = 4; target.fxDelayTone = .37f; break;
                    case 27: // raw circuit reference, pedal bypassed
                        target.fxDistType = 1; target.fxDistTone = .50f;
                        target.fxDistMix = 1f; target.fxDistBite = 0f;
                        target.fxDelayMode = 0; break;
                }
                target.fx303DriveModel = index != 27;
                target.fx303DelayModel = index != 27;
            }
            target.patchName = p.name;
            target.patchModified = false;
        }
    }
}
