using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ZPunk.DAW
{
    /// <summary>
    /// Real-time mixed audio capture. Attach ONLY to the active AudioListener.
    /// Audio callback queues copied float blocks; all file I/O runs on Unity's main thread.
    /// Discards no samples silently: an overrun aborts the export.
    /// </summary>
    [RequireComponent(typeof(AudioListener))]
    public sealed class ZPunkWavCapture : MonoBehaviour
    {
        private sealed class Block { public float[] buffer; public int count; public long frame; public int channels; }
        private readonly object gate = new object();
        private readonly Queue<Block> ready = new Queue<Block>();
        private readonly Stack<Block> available = new Stack<Block>();
        private volatile bool active;
        private volatile bool overflow;
        private double targetDsp;
        private long wantedFrames;
        private int sampleRate;
        private int channels;
        private long writtenFrames;
        private FileStream file;
        private BinaryWriter writer;
        private string location;
        private byte[] pcm = new byte[16384];
        private const int MaxQueued = 256;
        public bool HasOverflow => overflow;
        public bool IsActive => active;

        public void Begin(string path, double songStartDsp, double lengthSeconds)
        {
            Abort();
            sampleRate = AudioSettings.outputSampleRate;
            if (sampleRate < 8000) throw new InvalidOperationException("Audio output unavailable.");
            targetDsp = songStartDsp;
            wantedFrames = (long)Math.Ceiling(sampleRate * lengthSeconds);
            if (wantedFrames < 1 || wantedFrames > (int.MaxValue - 44L) / 4)
                throw new InvalidOperationException("WAV export exceeds the supported length.");
            location = path;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 65536);
            writer = new BinaryWriter(file);
            writer.Write(new byte[44]); // reserved RIFF header
            channels = 0;
            writtenFrames = 0;
            overflow = false;
            lock (gate) { active = true; }
        }

        // Captures after all sources/audio mixers are combined at the listener.
        private void OnAudioFilterRead(float[] data, int inputChannels)
        {
            if (!active || data == null || inputChannels < 1) return;
            double blockDsp = AudioSettings.dspTime;
            int frames = data.Length / inputChannels;
            long blockFrame = (long)Math.Round((blockDsp - targetDsp) * sampleRate);
            int skip = (int)Math.Min(frames, Math.Max(0L, -blockFrame));
            int take = (int)Math.Min(frames - (long)skip, wantedFrames - Math.Max(0L, blockFrame));
            if (take <= 0) return;
            if (inputChannels > 2) { overflow = true; return; }
            int samples = take * inputChannels;
            Block block;
            lock (gate)
            {
                if (!active) return;
                if (ready.Count >= MaxQueued) { overflow = true; return; }
                block = available.Count != 0 ? available.Pop() : new Block();
            }
            if (block.buffer == null || block.buffer.Length < samples)
                block.buffer = new float[samples];
            Array.Copy(data, skip * inputChannels, block.buffer, 0, samples);
            block.count = samples;
            block.frame = Math.Max(0L, blockFrame);
            block.channels = inputChannels;
            lock (gate)
            {
                if (active) ready.Enqueue(block);
                else available.Push(block);
            }
        }

        private void Update() { if (writer != null) Flush(); }

        private void Flush()
        {
            while (true)
            {
                Block block;
                lock (gate)
                {
                    if (ready.Count == 0) break;
                    block = ready.Dequeue();
                }
                if (channels == 0) channels = block.channels;
                if (channels != block.channels) overflow = true;
                if (!overflow)
                {
                    long start = Math.Max(writtenFrames, block.frame);
                    PadTo(start);
                    int frameOffset = (int)(start - block.frame);
                    int frames = Math.Min((block.count / channels) - frameOffset,
                        (int)Math.Min(int.MaxValue, wantedFrames - writtenFrames));
                    if (frames > 0) WritePcm(block.buffer, frameOffset * channels, frames * channels);
                }
                lock (gate) available.Push(block);
            }
        }
        private void PadTo(long frame)
        {
            if (writer == null || channels == 0) return;
            long gap = Math.Min(wantedFrames, frame) - writtenFrames;
            if (gap <= 0) return;
            byte[] silence = new byte[8192];
            long missing = gap * channels * 2;
            while (missing > 0)
            {
                int count = (int)Math.Min(silence.Length, missing);
                writer.Write(silence, 0, count);
                missing -= count;
            }
            writtenFrames += gap;
        }
        private void WritePcm(float[] source, int index, int count)
        {
            int byteCount = count * 2;
            if (pcm.Length < byteCount) pcm = new byte[byteCount];
            for (int n = 0; n < count; n++)
            {
                // Safety for platforms where the export tap runs before the
                // listener master processor. Never serialize unprotected peaks.
                float v = Mathf.Clamp(source[index + n], -0.90f, 0.90f);
                short sample = (short)Mathf.RoundToInt(v * (v < 0 ? 32768f : 32767f));
                pcm[n * 2] = (byte)sample;
                pcm[n * 2 + 1] = (byte)(sample >> 8);
            }
            writer.Write(pcm, 0, byteCount);
            writtenFrames += count / channels;
        }
        public bool Complete()
        {
            lock (gate) active = false;
            if (writer == null) return false;
            try
            {
                Flush();
                if (overflow || channels == 0 || writtenFrames == 0) throw new IOException(
                    overflow ? "Audio capture overrun or changed channel count." : "AudioListener returned no audio.");
                PadTo(wantedFrames);
                long pcmBytes = writtenFrames * channels * 2;
                writer.BaseStream.Seek(0, SeekOrigin.Begin);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                writer.Write((int)(36 + pcmBytes));
                writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16); writer.Write((short)1); writer.Write((short)channels);
                writer.Write(sampleRate); writer.Write(sampleRate * channels * 2);
                writer.Write((short)(channels * 2)); writer.Write((short)16);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write((int)pcmBytes);
                writer.Flush();
                // Preserve the finished file if this listener is destroyed later.
                location = null;
                return true;
            }
            finally { Close(); }
        }
        public void Abort()
        {
            lock (gate) { active = false; ready.Clear(); }
            Close();
            if (!string.IsNullOrEmpty(location))
            {
                try { if (File.Exists(location)) File.Delete(location); } catch { }
            }
        }
        private void Close()
        {
            writer?.Dispose(); writer = null;
            file?.Dispose(); file = null;
        }
        private void OnDestroy() { Abort(); }
    }
}
