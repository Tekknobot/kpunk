using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace ZPunk.DAW
{
    /// <summary>Standard MIDI file 1, one track per ZPUNK arrangement track.
    /// Uses the same clip expansion as the song player (including linked/looped clips).
    /// MIDI carries note/trigger data, not Helm patches, samples, or audio effects.</summary>
    public static class ZPunkArrangementMidiExport
    {
        private sealed class Entry
        {
            public long tick;
            public byte priority;
            public byte[] bytes;
        }

        private static readonly int[] GMDrums = { 36, 38, 39, 42, 46, 51, 37, 49 };

        public static byte[] Create(SongData song)
        {
            if (song == null || song.tracks == null) throw new ArgumentException("No song to export.");
            var compiled = SongEventPlanner.Build(song);
            if (compiled.samples.Count == 0 && compiled.midi.Count == 0)
                throw new InvalidOperationException("There are no notes or hits in this song.");

            var tracks = new List<byte[]>();
            int micros = Mathf.RoundToInt(60000000f / Mathf.Clamp(song.bpm, 1f, 400f));
            int numerator = Mathf.Clamp(song.numerator, 1, 32);
            int denominator = song.denominator > 0 ? song.denominator : 4;
            int power = 0;
            while ((1 << power) < denominator && power < 7) power++;
            // Meta track: tempo/time signature, so DAWs import at the correct timing.
            var meta = new List<Entry>();
            meta.Add(New(0, 0, new byte[] { 0xFF, 0x51, 0x03,
                (byte)(micros >> 16), (byte)(micros >> 8), (byte)micros }));
            meta.Add(New(0, 1, new byte[] { 0xFF, 0x58, 0x04,
                (byte)numerator, (byte)power, 24, 8 }));
            tracks.Add(TrackBytes(meta, song.lengthTicks));

            for (int i = 0; i < song.tracks.Count; i++)
            {
                var track = song.tracks[i];
                if (track == null) continue;
                // GM drums are channel 10. Chops use a separate melodic channel,
                // with C1 = slice 1; the receiving DAW must map these to its sampler.
                int channel = track.kind == TrackKind.Drums ? 9 :
                    track.kind == TrackKind.Bass ? 0 :
                    track.kind == TrackKind.Synth ? 1 :
                    track.kind == TrackKind.Pads ? 2 : 3;
                var events = new List<Entry>();
                var name = Encoding.UTF8.GetBytes(string.IsNullOrEmpty(track.name) ? track.kind.ToString() : track.name);
                var nameBytes = new List<byte> { 0xFF, 0x03 };
                WriteVariable(nameBytes, name.Length);
                nameBytes.AddRange(name);
                events.Add(New(0, 0, nameBytes.ToArray()));
                foreach (var e in compiled.midi)
                {
                    if (e.trackIndex != i) continue;
                    bool on = e.type == SongEventPlanner.EventType.NoteOn;
                    events.Add(New(e.tick, on ? (byte)2 : (byte)1,
                        new byte[] { (byte)((on ? 0x90 : 0x80) + channel), (byte)e.value,
                            (byte)(on ? Mathf.Clamp(e.velocity, 1, 127) : 0) }));
                }
                foreach (var e in compiled.samples)
                {
                    if (e.trackIndex != i) continue;
                    int pitch = e.type == SongEventPlanner.EventType.Drum
                        ? GMDrums[Mathf.Clamp(e.value, 0, GMDrums.Length - 1)]
                        : 36 + Mathf.Clamp(e.value - 1, 0, 15);
                    long off = Math.Min(Math.Max(1L, song.lengthTicks), e.tick + SongData.TicksPerSixteenth / 2);
                    events.Add(New(e.tick, 2, new byte[] { (byte)(0x90 + channel),
                        (byte)pitch, (byte)Mathf.Clamp(e.velocity, 1, 127) }));
                    events.Add(New(off, 1, new byte[] { (byte)(0x80 + channel), (byte)pitch, 0 }));
                }
                tracks.Add(TrackBytes(events, song.lengthTicks));
            }
            using (var output = new MemoryStream())
            {
                using (var writer = new BinaryWriter(output))
                {
                    writer.Write(Encoding.ASCII.GetBytes("MThd"));
                    WriteBE(writer, 6);
                    WriteBE(writer, (ushort)1);
                    WriteBE(writer, (ushort)tracks.Count);
                    WriteBE(writer, (ushort)SongData.TicksPerQuarter);
                    foreach (var data in tracks)
                    {
                        writer.Write(Encoding.ASCII.GetBytes("MTrk"));
                        WriteBE(writer, data.Length);
                        writer.Write(data);
                    }
                }
                return output.ToArray();
            }
        }

        private static Entry New(long tick, byte priority, byte[] data)
            => new Entry { tick = Math.Max(0L, tick), priority = priority, bytes = data };

        private static byte[] TrackBytes(List<Entry> events, long endTick)
        {
            events.Sort((a, b) => a.tick != b.tick ? a.tick.CompareTo(b.tick) : a.priority.CompareTo(b.priority));
            using (var stream = new MemoryStream())
            {
                long last = 0;
                foreach (var e in events)
                {
                    long at = Math.Max(last, Math.Min(e.tick, 0x0FFFFFFFL));
                    WriteVariable(stream, at - last);
                    stream.Write(e.bytes, 0, e.bytes.Length);
                    last = at;
                }
                WriteVariable(stream, Math.Max(0, Math.Min(endTick, 0x0FFFFFFFL) - last));
                stream.WriteByte(0xFF); stream.WriteByte(0x2F); stream.WriteByte(0);
                return stream.ToArray();
            }
        }
        private static void WriteVariable(List<byte> output, long value)
        {
            using (var m = new MemoryStream()) { WriteVariable(m, value); output.AddRange(m.ToArray()); }
        }
        private static void WriteVariable(Stream stream, long input)
        {
            uint value = (uint)Math.Max(0, Math.Min(0x0FFFFFFFL, input));
            uint buffer = value & 0x7F;
            while ((value >>= 7) > 0) { buffer <<= 8; buffer |= (value & 0x7F) | 0x80; }
            while (true)
            {
                stream.WriteByte((byte)buffer);
                if ((buffer & 0x80) == 0) break;
                buffer >>= 8;
            }
        }
        private static void WriteBE(BinaryWriter writer, int value)
        {
            writer.Write((byte)(value >> 24)); writer.Write((byte)(value >> 16));
            writer.Write((byte)(value >> 8)); writer.Write((byte)value);
        }
        private static void WriteBE(BinaryWriter writer, ushort value)
        {
            writer.Write((byte)(value >> 8)); writer.Write((byte)value);
        }
    }
}
