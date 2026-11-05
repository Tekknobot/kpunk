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
            new Factory("ZOR MOTION NIGHT", "MOTION", false, .34f,.76f,.50f,.68f,.54f,.12f,.70f, .19f,.44f,.37f,.48f,.62f,.86f,2)
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
            target.outputLevelSet = true;
            target.outputLevel = 1f;
            target.fxDistortion = p.distortion;
            target.fxDistControlsInitialized = true;
            // Different patch families use different grit/tone blends.
            target.fxDistTone = index == 2 || index == 4 ? .76f :
                index == 7 || index == 8 ? .30f : index == 9 ? .40f : .50f;
            target.fxDistMix = index == 0 ? .85f :
                index == 2 || index == 4 ? .92f :
                index == 5 || index == 7 ? .42f : .70f;
            target.fxDistBite = index == 2 ? .72f : index == 4 ? .52f :
                index == 1 || index == 9 ? .28f : .08f;
            target.fxChorus = p.chorus;
            target.fxDelay = p.delay;
            target.fxFeedback = p.feedback;
            target.fxReverb = p.reverb;
            target.fxRoom = p.room;
            target.fxDelayRate = p.rate;
            target.patchName = p.name;
            target.patchModified = false;
        }
    }
}
