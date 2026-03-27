using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Exports a standard MIDI bundle beside a saved project JSON.
/// Files produced:
/// - project_xxx.mid           (full arrangement, multi-track)
/// - project_xxx_drums.mid
/// - project_xxx_keys.mid
/// - project_xxx_chords.mid
/// - project_xxx_chops.mid
/// </summary>
public static class KMusicMidiExporter
{
    private const int TicksPerQuarter = 480;
    private const int SixteenthTicks = TicksPerQuarter / 4;

    // Roland Cloud TR-909 / common GM drum mapping.
    // Order expected from zpunk drum lanes:
    // Kick, Snare, Clap, Closed Hat, Open Hat, Ride, Rim, Crash
    private static readonly int[] DrumNotes = { 36, 38, 39, 42, 46, 51, 37, 49 };
    private static readonly int[] DrumVelocities = { 54, 90, 122 };

    public static void ExportProjectMidiBundle(string projectJsonPath, object projectData, float bpm)
    {
        if (string.IsNullOrEmpty(projectJsonPath) || projectData == null)
            return;

        try
        {
            string dir = Path.GetDirectoryName(projectJsonPath);
            string baseName = Path.GetFileNameWithoutExtension(projectJsonPath);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(baseName))
                return;

            object patternsObj = GetField(projectData, "patterns");
            object chainObj = GetField(projectData, "chain");
            object drumsObj = GetField(projectData, "drums");
            object seqObj = GetField(projectData, "seq");
            object padObj = GetField(projectData, "pad");
            object samplerObj = GetField(projectData, "sampler");

            var arrangement = BuildArrangement(patternsObj, chainObj, drumsObj, seqObj, padObj, samplerObj);

            Debug.Log("[MIDI] drums=" + arrangement.drums.Count +
                      " keys=" + arrangement.keys.Count +
                      " chords=" + arrangement.chords.Count +
                      " chops=" + arrangement.chops.Count);

            string fullPath = Path.Combine(dir, baseName + ".mid");
            File.WriteAllBytes(fullPath, BuildMidiFile(bpm, arrangement.drums, arrangement.keys, arrangement.chords, arrangement.chops));

            File.WriteAllBytes(Path.Combine(dir, baseName + "_drums.mid"), BuildMidiFile(bpm, arrangement.drums));
            File.WriteAllBytes(Path.Combine(dir, baseName + "_keys.mid"), BuildMidiFile(bpm, arrangement.keys));
            File.WriteAllBytes(Path.Combine(dir, baseName + "_chords.mid"), BuildMidiFile(bpm, arrangement.chords));
            File.WriteAllBytes(Path.Combine(dir, baseName + "_chops.mid"), BuildMidiFile(bpm, arrangement.chops));
        }
        catch (Exception e)
        {
            Debug.LogWarning("[MIDI] Export failed: " + e);
        }
    }

    private static Arrangement BuildArrangement(object patternsObj, object chainObj, object drumsObj, object seqObj, object padObj, object samplerObj)
    {
        var arrangement = new Arrangement();

        List<PatternFrame> bars = BuildPatternFrames(patternsObj, chainObj);

        if (bars.Count == 0)
        {
            Debug.LogWarning("[MIDI] No pattern frames found, falling back to live frame.");
            bars.Add(BuildLiveFrame(drumsObj, seqObj, padObj, samplerObj));
        }

        Debug.Log("[MIDI] bar frames=" + bars.Count);

        for (int barIndex = 0; barIndex < bars.Count; barIndex++)
        {
            var frame = bars[barIndex];
            int barTick = barIndex * 16 * SixteenthTicks;

            EmitDrums(arrangement.drums, frame.drumMask, frame.drumVelocityData, barTick);
            EmitMonophonic(arrangement.keys, frame.seqSteps, frame.seqRuns, barTick, 60, 0, 96);
            EmitChords(arrangement.chords, frame.padSteps, frame.padRuns, frame.padChordMode, barTick, 1, 84);
            EmitChops(arrangement.chops, frame.sampleSteps, barTick, 2, 48);
        }

        return arrangement;
    }

    private static List<PatternFrame> BuildPatternFrames(object patternsObj, object chainObj)
    {
        var frames = new List<PatternFrame>();
        if (patternsObj == null)
        {
            Debug.LogWarning("[MIDI] patternsObj is null.");
            return frames;
        }

        int[] ids = ToIntArray(GetField(patternsObj, "ids"));
        var byId = new Dictionary<int, PatternFrame>();

        var drumMaskB64 = ToStringList(GetField(patternsObj, "drumMaskB64"));
        var drumVelB64 = ToStringList(GetField(patternsObj, "drumVelocityB64"));
        var sampleSteps = ToWrappedIntArrayList(GetField(patternsObj, "sampleSteps"));
        var seqSteps = ToWrappedIntArrayList(GetField(patternsObj, "seqSteps"));
        var seqRuns = ToWrappedIntArrayList(GetField(patternsObj, "seqRuns"));
        var padSteps = ToWrappedIntArrayList(GetField(patternsObj, "padSteps"));
        var padRuns = ToWrappedIntArrayList(GetField(patternsObj, "padRuns"));
        var padModes = ToIntList(GetField(patternsObj, "padChordModes"));

        Debug.Log("[MIDI] patterns ids=" + ids.Length +
                  " drumMaskB64=" + drumMaskB64.Length +
                  " drumVelB64=" + drumVelB64.Length +
                  " sampleSteps=" + sampleSteps.Count +
                  " seqSteps=" + seqSteps.Count +
                  " seqRuns=" + seqRuns.Count +
                  " padSteps=" + padSteps.Count +
                  " padRuns=" + padRuns.Count +
                  " padModes=" + padModes.Count);

        for (int i = 0; i < ids.Length; i++)
        {
            byId[ids[i]] = new PatternFrame
            {
                drumMask = EnsureLength(FromB64(drumMaskB64, i), 16),
                drumVelocityData = EnsureLength(FromB64(drumVelB64, i), 32),
                sampleSteps = FromList(sampleSteps, i, 16),
                seqSteps = FromList(seqSteps, i, 16),
                seqRuns = FromList(seqRuns, i, 16),
                padSteps = FromList(padSteps, i, 16),
                padRuns = FromList(padRuns, i, 16),
                padChordMode = (i < padModes.Count) ? padModes[i] : 0,
            };
        }

        if (chainObj == null)
        {
            Debug.LogWarning("[MIDI] chainObj is null.");
            return frames;
        }

        int length = Mathf.Clamp(GetInt(chainObj, "length", 0), 0, 64);
        int[] slots = ToIntArray(GetField(chainObj, "slots"));

        Debug.Log("[MIDI] chain length=" + length + " slots=" + slots.Length);

        if (length <= 0 || slots.Length == 0)
            return frames;

        for (int i = 0; i < length; i++)
        {
            int pid = (i < slots.Length) ? slots[i] : -1;
            if (pid >= 0 && byId.TryGetValue(pid, out var frame))
                frames.Add(frame);
            else
                frames.Add(new PatternFrame());
        }

        return frames;
    }

    private static PatternFrame BuildLiveFrame(object drumsObj, object seqObj, object padObj, object samplerObj)
    {
        var frame = new PatternFrame
        {
            drumMask = EnsureLength(
                FromB64(GetString(drumsObj, "stepMaskB64") ?? GetString(drumsObj, "drumMaskB64")),
                16),
            drumVelocityData = EnsureLength(
                FromB64(GetString(drumsObj, "stepVelocityB64") ?? GetString(drumsObj, "drumVelocityB64")),
                32),
            sampleSteps = ToIntArray(
                GetField(samplerObj, "stepGrid") ??
                GetField(samplerObj, "steps"),
                16),
            seqSteps = ToIntArray(
                GetField(seqObj, "stepGrid") ??
                GetField(seqObj, "steps"),
                16),
            seqRuns = ToIntArray(
                GetField(seqObj, "runs") ??
                GetField(seqObj, "noteRuns"),
                16),
            padSteps = ToIntArray(
                GetField(padObj, "stepGrid") ??
                GetField(padObj, "steps"),
                16),
            padRuns = ToIntArray(
                GetField(padObj, "runs") ??
                GetField(padObj, "noteRuns"),
                16),
            padChordMode = GetInt(padObj, "chordMode", 0),
        };

        Debug.Log("[MIDI] live frame drumMask=" + (frame.drumMask == null ? -1 : frame.drumMask.Length) +
                  " drumVel=" + (frame.drumVelocityData == null ? -1 : frame.drumVelocityData.Length));

        return frame;
    }

    private static void EmitDrums(List<MidiNote> notes, byte[] mask, byte[] velocityData, int barTick)
    {
        if (mask == null || mask.Length == 0)
            return;

        for (int step = 0; step < Math.Min(16, mask.Length); step++)
        {
            byte stepMask = mask[step];
            if (stepMask == 0)
                continue;

            ushort packed = 0;
            if (velocityData != null && velocityData.Length >= (step * 2 + 2))
                packed = (ushort)(velocityData[step * 2] | (velocityData[step * 2 + 1] << 8));

            for (int lane = 0; lane < 8; lane++)
            {
                if ((stepMask & (1 << lane)) == 0)
                    continue;

                int tier = (packed >> (lane * 2)) & 0x3;
                tier = Mathf.Clamp(tier, 0, 2);

                notes.Add(new MidiNote
                {
                    start = barTick + step * SixteenthTicks,
                    length = Math.Max(1, SixteenthTicks / 2),
                    channel = 9, // MIDI ch 10
                    pitch = DrumNotes[lane],
                    velocity = DrumVelocities[tier]
                });
            }
        }
    }

    private static void EmitMonophonic(List<MidiNote> notes, int[] steps, int[] runs, int barTick, int baseMidi, int channel, int maxVelocity)
    {
        if (steps == null || steps.Length == 0)
            return;

        for (int step = 0; step < Math.Min(16, steps.Length); step++)
        {
            int valueId = steps[step];
            if (valueId <= 0)
                continue;

            bool isContinuation = step > 0 && steps[step - 1] == valueId && (runs == null || runs[step] <= 0);
            if (isContinuation)
                continue;

            int run = 1;
            if (runs != null && step < runs.Length && runs[step] > 0)
            {
                run = runs[step];
            }
            else
            {
                int s = step + 1;
                while (s < Math.Min(16, steps.Length) && steps[s] == valueId)
                {
                    run++;
                    s++;
                }
            }

            notes.Add(new MidiNote
            {
                start = barTick + step * SixteenthTicks,
                length = Math.Max(1, run * SixteenthTicks),
                channel = channel,
                pitch = Mathf.Clamp(baseMidi + Math.Max(0, valueId - 1), 0, 127),
                velocity = maxVelocity
            });
        }
    }

    private static void EmitChords(List<MidiNote> notes, int[] steps, int[] runs, int chordMode, int barTick, int channel, int velocity)
    {
        if (steps == null || steps.Length == 0)
            return;

        for (int step = 0; step < Math.Min(16, steps.Length); step++)
        {
            int valueId = steps[step];
            if (valueId <= 0)
                continue;

            bool isContinuation = step > 0 && steps[step - 1] == valueId && (runs == null || runs[step] <= 0);
            if (isContinuation)
                continue;

            int run = 1;
            if (runs != null && step < runs.Length && runs[step] > 0)
            {
                run = runs[step];
            }
            else
            {
                int s = step + 1;
                while (s < Math.Min(16, steps.Length) && steps[s] == valueId)
                {
                    run++;
                    s++;
                }
            }

            int root = Mathf.Clamp(60 + Math.Max(0, valueId - 1), 0, 127);
            int[] chord = chordMode switch
            {
                1 => new[] { root, root + 3, root + 7 },
                2 => new[] { root, root + 4, root + 7, root + 10 },
                3 => new[] { root, root + 2, root + 7 },
                _ => new[] { root, root + 4, root + 7 },
            };

            foreach (int pitch in chord)
            {
                notes.Add(new MidiNote
                {
                    start = barTick + step * SixteenthTicks,
                    length = Math.Max(1, run * SixteenthTicks),
                    channel = channel,
                    pitch = Mathf.Clamp(pitch, 0, 127),
                    velocity = velocity
                });
            }
        }
    }

    private static void EmitChops(List<MidiNote> notes, int[] steps, int barTick, int channel, int baseMidi)
    {
        if (steps == null || steps.Length == 0)
            return;

        for (int step = 0; step < Math.Min(16, steps.Length); step++)
        {
            int chopId = steps[step];
            if (chopId <= 0)
                continue;

            notes.Add(new MidiNote
            {
                start = barTick + step * SixteenthTicks,
                length = Math.Max(1, SixteenthTicks),
                channel = channel,
                pitch = Mathf.Clamp(baseMidi + chopId - 1, 0, 127),
                velocity = 100
            });
        }
    }

    private static byte[] BuildMidiFile(float bpm, params List<MidiNote>[] noteTracks)
    {
        bpm = Mathf.Clamp(bpm, 1f, 400f);

        var tracks = new List<byte[]>();
        tracks.Add(BuildTempoTrack(bpm));

        if (noteTracks != null)
        {
            foreach (var notes in noteTracks)
            {
                if (notes != null && notes.Count > 0)
                    tracks.Add(BuildNoteTrack(notes));
            }
        }

        if (tracks.Count == 1)
            tracks.Add(BuildEmptyTrack());

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        WriteAscii(bw, "MThd");
        WriteBE32(bw, 6);
        WriteBE16(bw, 1);
        WriteBE16(bw, tracks.Count);
        WriteBE16(bw, TicksPerQuarter);

        foreach (var track in tracks)
            bw.Write(track);

        return ms.ToArray();
    }

    private static byte[] BuildTempoTrack(float bpm)
    {
        using var content = new MemoryStream();
        using var bw = new BinaryWriter(content);

        int mpqn = Mathf.RoundToInt(60000000f / bpm);

        WriteVarLen(bw, 0);
        bw.Write((byte)0xFF);
        bw.Write((byte)0x51);
        bw.Write((byte)0x03);
        bw.Write((byte)((mpqn >> 16) & 0xFF));
        bw.Write((byte)((mpqn >> 8) & 0xFF));
        bw.Write((byte)(mpqn & 0xFF));

        WriteVarLen(bw, 0);
        bw.Write((byte)0xFF);
        bw.Write((byte)0x58);
        bw.Write((byte)0x04);
        bw.Write((byte)0x04);
        bw.Write((byte)0x02);
        bw.Write((byte)0x18);
        bw.Write((byte)0x08);

        WriteVarLen(bw, 0);
        bw.Write((byte)0xFF);
        bw.Write((byte)0x2F);
        bw.Write((byte)0x00);

        return WrapTrack(content.ToArray());
    }

    private static byte[] BuildEmptyTrack()
    {
        using var content = new MemoryStream();
        using var bw = new BinaryWriter(content);

        WriteVarLen(bw, 0);
        bw.Write((byte)0xFF);
        bw.Write((byte)0x2F);
        bw.Write((byte)0x00);

        return WrapTrack(content.ToArray());
    }

    private static byte[] BuildNoteTrack(List<MidiNote> notes)
    {
        var events = new List<MidiEvent>();

        foreach (var src in notes)
        {
            int start = Mathf.Max(0, src.start);
            int length = Mathf.Max(1, src.length);
            int end = start + length;

            int channel = src.channel & 0x0F;
            int pitch = Mathf.Clamp(src.pitch, 0, 127);
            int velocity = Mathf.Clamp(src.velocity, 1, 127);

            events.Add(new MidiEvent
            {
                tick = start,
                status = (byte)(0x90 | channel),
                data1 = (byte)pitch,
                data2 = (byte)velocity,
                sort = 1
            });

            events.Add(new MidiEvent
            {
                tick = end,
                status = (byte)(0x80 | channel),
                data1 = (byte)pitch,
                data2 = 0,
                sort = 0
            });
        }

        events.Sort((a, b) =>
        {
            int cmp = a.tick.CompareTo(b.tick);
            if (cmp != 0) return cmp;

            cmp = a.sort.CompareTo(b.sort);
            if (cmp != 0) return cmp;

            cmp = a.status.CompareTo(b.status);
            if (cmp != 0) return cmp;

            return a.data1.CompareTo(b.data1);
        });

        using var content = new MemoryStream();
        using var bw = new BinaryWriter(content);

        int lastTick = 0;

        foreach (var e in events)
        {
            int delta = Mathf.Max(0, e.tick - lastTick);
            WriteVarLen(bw, delta);
            bw.Write(e.status);
            bw.Write(e.data1);
            bw.Write(e.data2);
            lastTick = e.tick;
        }

        WriteVarLen(bw, 0);
        bw.Write((byte)0xFF);
        bw.Write((byte)0x2F);
        bw.Write((byte)0x00);

        return WrapTrack(content.ToArray());
    }

    private static byte[] WrapTrack(byte[] content)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        WriteAscii(bw, "MTrk");
        WriteBE32(bw, content.Length);
        bw.Write(content);
        return ms.ToArray();
    }

    private static void WriteAscii(BinaryWriter bw, string s)
    {
        bw.Write(System.Text.Encoding.ASCII.GetBytes(s));
    }

    private static void WriteBE16(BinaryWriter bw, int value)
    {
        bw.Write((byte)((value >> 8) & 0xFF));
        bw.Write((byte)(value & 0xFF));
    }

    private static void WriteBE32(BinaryWriter bw, int value)
    {
        bw.Write((byte)((value >> 24) & 0xFF));
        bw.Write((byte)((value >> 16) & 0xFF));
        bw.Write((byte)((value >> 8) & 0xFF));
        bw.Write((byte)(value & 0xFF));
    }

    private static void WriteVarLen(BinaryWriter bw, int value)
    {
        value = Mathf.Max(0, value);

        int buffer = value & 0x7F;
        while ((value >>= 7) > 0)
        {
            buffer <<= 8;
            buffer |= ((value & 0x7F) | 0x80);
        }

        while (true)
        {
            bw.Write((byte)(buffer & 0xFF));
            if ((buffer & 0x80) != 0)
                buffer >>= 8;
            else
                break;
        }
    }

    private static object GetField(object obj, string name)
    {
        if (obj == null || string.IsNullOrEmpty(name))
            return null;

        Type t = obj.GetType();

        var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (f != null)
            return f.GetValue(obj);

        var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (p != null && p.GetIndexParameters().Length == 0)
            return p.GetValue(obj);

        return null;
    }

    private static string GetString(object obj, string fieldName)
    {
        object v = GetField(obj, fieldName);
        return v?.ToString();
    }

    private static int GetInt(object obj, string fieldName, int fallback)
    {
        object v = GetField(obj, fieldName);
        if (v == null)
            return fallback;

        if (v is int i) return i;
        if (v is long l) return (int)l;
        if (v is short s) return s;
        if (v is byte b) return b;
        if (int.TryParse(v.ToString(), out int parsed)) return parsed;

        return fallback;
    }

    private static int[] ToIntArray(object obj, int expected = -1)
    {
        if (obj == null)
            return expected > 0 ? new int[expected] : Array.Empty<int>();

        if (obj is int[] ints)
            return ResizeIfNeeded(ints, expected);

        if (obj is List<int> listInts)
            return ResizeIfNeeded(listInts.ToArray(), expected);

        if (obj is short[] shorts)
        {
            int[] arr = new int[shorts.Length];
            for (int i = 0; i < shorts.Length; i++) arr[i] = shorts[i];
            return ResizeIfNeeded(arr, expected);
        }

        if (obj is byte[] bytes)
        {
            int[] arr = new int[bytes.Length];
            for (int i = 0; i < bytes.Length; i++) arr[i] = bytes[i];
            return ResizeIfNeeded(arr, expected);
        }

        if (obj is IEnumerable enumerable && obj is not string)
        {
            var vals = new List<int>();
            foreach (var item in enumerable)
            {
                if (item == null)
                {
                    vals.Add(0);
                    continue;
                }

                if (item is int ii) vals.Add(ii);
                else if (item is long ll) vals.Add((int)ll);
                else if (item is short ss) vals.Add(ss);
                else if (item is byte bb) vals.Add(bb);
                else if (int.TryParse(item.ToString(), out int parsed)) vals.Add(parsed);
            }
            return ResizeIfNeeded(vals.ToArray(), expected);
        }

        return expected > 0 ? new int[expected] : Array.Empty<int>();
    }

    private static List<int> ToIntList(object obj)
    {
        if (obj == null)
            return new List<int>();

        if (obj is List<int> list)
            return new List<int>(list);

        if (obj is int[] arr)
            return new List<int>(arr);

        if (obj is IEnumerable enumerable && obj is not string)
        {
            var outList = new List<int>();
            foreach (var item in enumerable)
            {
                if (item == null) continue;
                if (item is int i) outList.Add(i);
                else if (item is long l) outList.Add((int)l);
                else if (item is short s) outList.Add(s);
                else if (item is byte b) outList.Add(b);
                else if (int.TryParse(item.ToString(), out int parsed)) outList.Add(parsed);
            }
            return outList;
        }

        return new List<int>();
    }

    private static string[] ToStringList(object obj)
    {
        if (obj == null)
            return Array.Empty<string>();

        if (obj is string[] arr)
            return arr;

        if (obj is List<string> list)
            return list.ToArray();

        if (obj is IEnumerable enumerable && obj is not string)
        {
            var outList = new List<string>();
            foreach (var item in enumerable)
                outList.Add(item?.ToString() ?? string.Empty);
            return outList.ToArray();
        }

        return Array.Empty<string>();
    }

    private static List<int[]> ToWrappedIntArrayList(object obj)
    {
        var outList = new List<int[]>();
        if (obj is IEnumerable enumerable && obj is not string)
        {
            foreach (var item in enumerable)
            {
                object inner = GetField(item, "v") ??
                               GetField(item, "value") ??
                               GetField(item, "data") ??
                               item;

                outList.Add(ToIntArray(inner));
            }
        }
        return outList;
    }

    private static int[] FromList(List<int[]> list, int index, int expected)
    {
        if (list == null || index < 0 || index >= list.Count)
            return new int[expected];

        return ToIntArray(list[index], expected);
    }

    private static byte[] FromB64(string value)
    {
        try
        {
            return string.IsNullOrEmpty(value) ? null : Convert.FromBase64String(value);
        }
        catch
        {
            return null;
        }
    }

    private static byte[] FromB64(string[] values, int index)
    {
        if (values == null || index < 0 || index >= values.Length)
            return null;

        return FromB64(values[index]);
    }

    private static byte[] EnsureLength(byte[] data, int expected)
    {
        if (expected <= 0)
            return data ?? Array.Empty<byte>();

        var result = new byte[expected];
        if (data != null && data.Length > 0)
            Array.Copy(data, result, Math.Min(data.Length, expected));
        return result;
    }

    private static int[] ResizeIfNeeded(int[] source, int expected)
    {
        if (expected <= 0)
            return source ?? Array.Empty<int>();

        var copy = new int[expected];
        if (source != null && source.Length > 0)
            Array.Copy(source, copy, Math.Min(source.Length, expected));
        return copy;
    }

    private sealed class PatternFrame
    {
        public byte[] drumMask = new byte[16];
        public byte[] drumVelocityData = new byte[32];
        public int[] sampleSteps = new int[16];
        public int[] seqSteps = new int[16];
        public int[] seqRuns = new int[16];
        public int[] padSteps = new int[16];
        public int[] padRuns = new int[16];
        public int padChordMode;
    }

    private sealed class Arrangement
    {
        public readonly List<MidiNote> drums = new();
        public readonly List<MidiNote> keys = new();
        public readonly List<MidiNote> chords = new();
        public readonly List<MidiNote> chops = new();
    }

    private struct MidiNote
    {
        public int start;
        public int length;
        public int channel;
        public int pitch;
        public int velocity;
    }

    private struct MidiEvent
    {
        public int tick;
        public byte status;
        public byte data1;
        public byte data2;
        public int sort;
    }
}