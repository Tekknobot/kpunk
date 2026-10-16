using System;
using System.Collections.Generic;

namespace ZPunk.DAW
{
    /// <summary>
    /// Four genre families, three deliberately different grooves per family.
    /// Writes standard editable DAW drum/MIDI events, never CHOPS or old pattern buffers.
    /// Every generated note is on a 1/16 grid; pitches follow a shared tonic and scale.
    /// Uses a local seeded RNG so applying a pattern doesn't perturb other Unity randomness.
    /// </summary>
    public static class ZPunkGenreGenerator
    {
        public static readonly string[] Genres = { "BOOM BAP", "HOUSE", "TECHNO", "BREAKS" };
        public static readonly string[][] Styles = {
            new[] { "CLASSIC", "DUSTY", "SYNCOPATED" },
            new[] { "CLASSIC", "DEEP", "JACKIN" },
            new[] { "DRIVING", "MINIMAL", "INDUSTRIAL" },
            new[] { "AMEN", "JUNGLE", "HALF-TIME" }
        };
        public static readonly string[] Keys = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
        public static string Label(int genre, int style, int key, bool minor)
        {
            genre = Clamp(genre, 0, Genres.Length - 1);
            return Genres[genre] + " / " + Styles[genre][Clamp(style, 0, 2)] + " / " +
                   Keys[Mod(key, 12)] + (minor ? " MIN" : " MAJ");
        }
        public static string Description(int genre, int style)
        {
            string[][] descriptions = {
                new[] { "Backbeat kick and snare, laid-back hats", "Sparse kicks, ghost notes and dusty spaces", "Offbeat kicks and syncopated 16th-note hats" },
                new[] { "Four-on-floor, claps and offbeat hats", "Deep sparse groove with long melodic tones", "Busy hats, percussion and upbeat stabs" },
                new[] { "Relentless kicks and driving pulses", "Stripped-back machine groove and repetition", "Hard accents, metallic hats and dark motifs" },
                new[] { "Amen-inspired syncopated break grid", "Fast broken kicks, snare ghosts and rolling hats", "Half-time snare hits with breakbeat details" }
            };
            genre = Clamp(genre, 0, 3);
            return descriptions[genre][Clamp(style, 0, 2)];
        }

        public static void Fill(ClipContentData clip, TrackKind instrument, int genre, int style,
                                int bars, int key, bool minor, int variation)
        {
            if (clip == null) throw new ArgumentNullException(nameof(clip));
            if (instrument == TrackKind.Chops) return;
            genre = Clamp(genre, 0, 3);
            style = Clamp(style, 0, 2);
            bars = Clamp(bars, 1, 4);
            key = Mod(key, 12);
            clip.kind = instrument == TrackKind.Drums ? ClipKind.Drums : ClipKind.Midi;
            clip.lengthTicks = bars * SongData.TicksPerBar4;
            clip.generatedTag = Label(genre, style, key, minor) + " / V" + variation;
            if (clip.drums == null) clip.drums = new List<DrumEvent>();
            if (clip.notes == null) clip.notes = new List<NoteEvent>();
            clip.drums.Clear();
            clip.notes.Clear();
            // In a malformed or converted document, never carry obsolete slice events forward.
            if (clip.chops != null) clip.chops.Clear();
            var rng = new Random(unchecked(variation * 11749 + genre * 2129 + style * 103 + (int)instrument * 71 + key * 13));
            for (int bar = 0; bar < bars; bar++)
            {
                if (instrument == TrackKind.Drums) GenerateDrums(clip, genre, style, bar, bars, rng);
                else GenerateNotes(clip, instrument, genre, style, bar, key, minor, rng);
            }
        }
        private static void Drum(ClipContentData clip, int bar, int step, int lane, int velocity, Random rng, double chance = 1.0)
        {
            if (step < 0 || step >= 16 || rng.NextDouble() > chance) return;
            long tick = bar * SongData.TicksPerBar4 + step * SongData.TicksPerSixteenth;
            foreach (var existing in clip.drums)
                if (existing != null && existing.startTick == tick && existing.lane == lane) return;
            int vel = Clamp(velocity + rng.Next(-8, 9), 28, 127);
            clip.drums.Add(new DrumEvent { startTick = tick, lane = lane, velocity = vel, legacyVelocityTier = 2 });
        }
        private static void Line(ClipContentData c, int b, int lane, int[] steps, int v, Random rng, double chance = 1)
        {
            foreach (int step in steps) Drum(c, b, step, lane, v, rng, chance);
        }
        private static void GenerateDrums(ClipContentData c, int g, int s, int b, int bars, Random r)
        {
            bool ending = b == bars - 1 && bars > 1;
            switch (g)
            {
                case 0: // boom bap, intentionally imperfect / asymmetrical accents
                    Line(c,b,0,new[]{0, s == 2 ? 7 : 6, s == 1 ? 11 : 10},110,r, .78);
                    Drum(c,b,0,0,116,r);
                    Line(c,b,1,new[]{4,12},114,r);
                    if (s == 1) { Drum(c,b,6,6,48,r,.55); Drum(c,b,15,1,41,r,.37); }
                    if (s == 2) { Drum(c,b,14,0,80,r,.65); Drum(c,b,11,6,51,r,.66); }
                    for(int i=0;i<16;i+= s==2?2:4) Drum(c,b,i,3,i%4==0?91:58,r,.96);
                    if (s != 1) for(int i=2;i<16;i+=4) Drum(c,b,i,3,64,r,s==2?.9:.55);
                    break;
                case 1: // house, four-on-floor plus style-dependent syncopation
                    Line(c,b,0,new[]{0,4,8,12},118,r);
                    Line(c,b,1,new[]{4,12},108,r);
                    if(s==0) Line(c,b,2,new[]{4,12},88,r,.65);
                    if(s==1) Drum(c,b,14,0,66,r,.25);
                    if(s==2) { Line(c,b,6,new[]{3,11,15},67,r,.9); Drum(c,b,10,0,77,r,.55); }
                    Line(c,b,4,new[]{2,6,10,14},87,r,s==1?.72:.96);
                    for(int i=0;i<16;i+= s==2?1:2) Drum(c,b,i,3, i%4==0?74:56,r,s==1?.56:.82);
                    break;
                case 2: // techno: dominant kick, harder hats, accents and gaps
                    Line(c,b,0,new[]{0,4,8,12},121,r,s==1?.89:1);
                    if(s==1) { Drum(c,b,0,0,123,r); Drum(c,b,12,0,121,r); }
                    if(s!=1) Line(c,b,1,new[]{4,12},99,r,.86);
                    Line(c,b,4,new[]{2,6,10,14},96,r,s==1?.55:.93);
                    for(int i=0;i<16;i+= (s==2?1:2)) Drum(c,b,i,3,i%4==0?102:66,r,s==1?.43:.9);
                    if(s==2) { Line(c,b,6,new[]{3,7,11,15},77,r,.76); Drum(c,b,15,7,79,r,ending?.8:.3); }
                    break;
                default: // Amen-inspired *step patterns*, not an audio sample/copyrighted recording.
                    if(s==2) { // half-time
                        Line(c,b,0,new[]{0,3,10,14},109,r,.88);
                        Drum(c,b,0,0,120,r);
                        Drum(c,b,8,1,122,r);
                        Line(c,b,1,new[]{15},60,r,.7);
                    }
                    else if(s==1) { // jungle variant
                        Line(c,b,0,new[]{0,3,7,10,14},108,r,.85);
                        Line(c,b,1,new[]{4,10,12,15},100,r,.87);
                        Line(c,b,6,new[]{2,9,13},53,r,.78);
                    }
                    else { // Amen-inspired kick/snare offset pattern
                        Line(c,b,0,new[]{0,3,7,10,14},106,r,.83);
                        Drum(c,b,0,0,120,r);
                        Line(c,b,1,new[]{4,9,12},111,r);
                        Line(c,b,6,new[]{6,11,15},51,r,.73);
                    }
                    for(int i=0;i<16;i+=2) Drum(c,b,i,3,i%4==0?91:66,r,.90);
                    Line(c,b,4,new[]{6,14},79,r,s==2?.48:.72);
                    if(ending) Line(c,b,1,new[]{13,14,15},63,r,.86);
                    break;
            }
            if(ending && g != 3)
            {
                Drum(c,b,15,6,58,r,.60);
                Drum(c,b,15,7,64,r,.33);
            }
        }
        private static readonly int[] Major = { 0,2,4,5,7,9,11 };
        private static readonly int[] Minor = { 0,2,3,5,7,8,10 };
        private static int Scale(int degree, bool minor)
        {
            int[] scale = minor ? Minor : Major;
            return 12 * (int)Math.Floor(degree / 7.0) + scale[Mod(degree,7)];
        }
        private static int[] Progression(int genre) => genre == 0 ? new[]{0,5,3,4} :
            genre == 1 ? new[]{0,5,2,6} : genre == 2 ? new[]{0,0,0,4} : new[]{0,5,6,4};
        private static void Note(ClipContentData c, int bar, int step, int lengthSteps, int pitch, int vel, Random rng)
        {
            if(step < 0 || step >= 16 || lengthSteps <= 0) return;
            c.notes.Add(new NoteEvent {
                pitch = Clamp(pitch,0,127),
                startTick = bar * SongData.TicksPerBar4 + step * SongData.TicksPerSixteenth,
                durationTicks = Math.Min(lengthSteps, 16-step) * SongData.TicksPerSixteenth,
                velocity = Clamp(vel + rng.Next(-9,10),25,127)
            });
        }
        private static void GenerateNotes(ClipContentData c, TrackKind kind, int g, int s, int bar, int key, bool minor, Random r)
        {
            int[] progression = Progression(g);
            int rootDegree = progression[bar % 4];
            // Root degree is relative to the selected key, and shared across BASS/SYNTH/PADS.
            int rootSemitones = key + Scale(rootDegree,minor);
            if(kind == TrackKind.Bass)
            {
                int basePitch = 24 + rootSemitones; // A1 for A-minor default
                if (g == 0) // boom bap
                {
                    Note(c,bar,0,3,basePitch,110,r);
                    Note(c,bar,s==2?5:6,2,basePitch + (s==1?7:0),95,r);
                    Note(c,bar,10,3,basePitch,108,r);
                    if(s!=1) Note(c,bar,14,2,basePitch + (s==2?3:0),84,r);
                }
                else if(g==1) // house
                {
                    for(int i=0;i<16;i+=4) Note(c,bar,i, s==1?3:2,basePitch,98,r);
                    if(s==2) for(int i=2;i<16;i+=4) Note(c,bar,i,2,basePitch+7,86,r);
                    if(s==1) Note(c,bar,14,2,basePitch+7,81,r);
                }
                else if(g==2) // techno
                {
                    int[] pos = s == 1 ? new[]{0,8,12} : s == 2 ? new[]{0,3,6,8,11,14} : new[]{0,2,4,6,8,10,12,14};
                    foreach (int i in pos) Note(c,bar,i,s==1?3:1,basePitch+(s==2 && i%4==3?12:0),103,r);
                }
                else // breaks
                {
                    int[] pos=s==2?new[]{0,7,10}:s==1?new[]{0,3,7,11,14}:new[]{0,3,8,10,14};
                    foreach (int i in pos) Note(c,bar,i,i==0?3:2,basePitch+(i==14?7:0),105,r);
                }
                return;
            }
            if(kind == TrackKind.Pads)
            {
                int basePitch = 48 + rootSemitones; // A3 for A-minor default
                if (g == 2 && s == 1)
                {
                    Note(c,bar,0,16,basePitch,72,r);
                    Note(c,bar,0,16,basePitch+7,65,r);
                }
                else
                {
                    int length=(g==0 || g==3 && s==2)?12:16;
                    int start=(g==0?2:0);
                    Note(c,bar,start,length-start,basePitch,75,r);
                    Note(c,bar,start,length-start,basePitch + Scale(rootDegree+2,minor) - Scale(rootDegree,minor),72,r);
                    Note(c,bar,start,length-start,basePitch + Scale(rootDegree+4,minor) - Scale(rootDegree,minor),67,r);
                    if(g==1 && s==2) Note(c,bar,10,3,basePitch+12,69,r);
                }
                return;
            }
            // SYNTH: distinctive little hooks, stabs and machine ostinati.
            int baseNote = 48 + rootSemitones;
            if(g==0)
            {
                Note(c,bar,0,3,baseNote+12,90,r);
                Note(c,bar,6,2,baseNote+Scale(rootDegree+2,minor)-Scale(rootDegree,minor),85,r);
                if(s!=1) Note(c,bar,10,3,baseNote+7,94,r);
                if(s==2) Note(c,bar,14,2,baseNote+12,80,r);
            }
            else if(g==1)
            {
                int[] steps=s==1?new[]{2,10}:new[]{2,6,10,14};
                foreach(int i in steps)
                {
                    Note(c,bar,i,s==1?4:2,baseNote+12,88,r);
                    Note(c,bar,i,s==1?4:2,baseNote+12+Scale(rootDegree+2,minor)-Scale(rootDegree,minor),72,r);
                    if(s==2) Note(c,bar,i,1,baseNote+19,65,r);
                }
            }
            else if(g==2)
            {
                int[] steps=s==1?new[]{0,8}:s==2?new[]{0,3,6,11,14}:new[]{0,2,4,6,8,10,12,14};
                foreach(int i in steps)
                {
                    int degree=s==1?0:(i/2+(s==2?i%3:0))%4;
                    Note(c,bar,i,s==1?3:1,baseNote+12+Scale(degree,minor),94,r);
                }
            }
            else
            {
                int[] steps=s==2?new[]{0,8,13}:s==1?new[]{0,3,6,9,12,15}:new[]{0,3,7,10,14};
                foreach(int i in steps) Note(c,bar,i,s==2?3:1,baseNote+12+Scale((i/3)%5,minor),96,r);
            }
        }
        private static int Clamp(int n, int min, int max) => Math.Max(min,Math.Min(max,n));
        private static int Mod(int n,int d) => ((n%d)+d)%d;
    }
}
