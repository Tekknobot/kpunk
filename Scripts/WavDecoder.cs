using System;
using System.IO;
using UnityEngine;

namespace KMusic
{
    /// <summary>
    /// Tiny WAV loader (PCM 16-bit / 24-bit / 32-bit float) -> AudioClip.
    /// Supports RIFF/WAVE little-endian .wav files.
    /// </summary>
    public static class WavDecoder
    {
        public static AudioClip LoadFromFile(string path, string clipName = null)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                throw new FileNotFoundException("WavDecoder: file not found", path);

            var bytes = File.ReadAllBytes(path);
            return LoadFromBytes(bytes, clipName ?? Path.GetFileNameWithoutExtension(path));
        }

        public static AudioClip LoadFromBytes(byte[] bytes, string clipName = "wav")
        {
            if (bytes == null || bytes.Length < 44)
                throw new ArgumentException("WavDecoder: invalid wav bytes");

            int offset = 0;

            // RIFF header
            if (ReadFourCC(bytes, 0) != "RIFF" || ReadFourCC(bytes, 8) != "WAVE")
                throw new Exception("WavDecoder: not a RIFF/WAVE file");

            offset = 12;

            // Find 'fmt ' and 'data' chunks
            int fmtOffset = -1, fmtSize = 0;
            int dataOffset = -1, dataSize = 0;

            while (offset + 8 <= bytes.Length)
            {
                string id = ReadFourCC(bytes, offset);
                int size = ReadIntLE(bytes, offset + 4);
                int chunkData = offset + 8;

                if (id == "fmt ")
                {
                    fmtOffset = chunkData;
                    fmtSize = size;
                }
                else if (id == "data")
                {
                    dataOffset = chunkData;
                    dataSize = size;
                    break; // we can stop once we hit data
                }

                // Chunks are word-aligned
                offset = chunkData + size + (size % 2);
            }

            if (fmtOffset < 0 || dataOffset < 0)
                throw new Exception("WavDecoder: missing fmt or data chunk");

            ushort audioFormat = ReadUShortLE(bytes, fmtOffset + 0);
            ushort channels = ReadUShortLE(bytes, fmtOffset + 2);
            int sampleRate = ReadIntLE(bytes, fmtOffset + 4);
            ushort bitsPerSample = ReadUShortLE(bytes, fmtOffset + 14);

            bool isPCM = (audioFormat == 1);
            bool isFloat = (audioFormat == 3);

            if (!isPCM && !isFloat)
                throw new Exception($"WavDecoder: unsupported format code {audioFormat} (need PCM=1 or FLOAT=3)");

            if (channels < 1 || channels > 8)
                throw new Exception($"WavDecoder: unsupported channels {channels}");

            if (sampleRate <= 0)
                throw new Exception("WavDecoder: invalid sampleRate");

            int bytesPerSample = bitsPerSample / 8;
            if (bytesPerSample <= 0) throw new Exception("WavDecoder: invalid bitsPerSample");

            int frameSize = bytesPerSample * channels;
            if (frameSize <= 0) throw new Exception("WavDecoder: invalid frameSize");

            int frameCount = dataSize / frameSize;
            if (frameCount <= 0) throw new Exception("WavDecoder: no audio frames");

            var samples = new float[frameCount * channels];

            int src = dataOffset;
            int dst = 0;

            if (isFloat && bitsPerSample == 32)
            {
                for (int i = 0; i < frameCount * channels; i++)
                {
                    samples[dst++] = ReadFloatLE(bytes, src);
                    src += 4;
                }
            }
            else if (isPCM && bitsPerSample == 16)
            {
                for (int i = 0; i < frameCount * channels; i++)
                {
                    short v = (short)ReadUShortLE(bytes, src);
                    samples[dst++] = v / 32768f;
                    src += 2;
                }
            }
            else if (isPCM && bitsPerSample == 24)
            {
                for (int i = 0; i < frameCount * channels; i++)
                {
                    int v = ReadInt24LE(bytes, src);
                    samples[dst++] = v / 8388608f;
                    src += 3;
                }
            }
            else if (isPCM && bitsPerSample == 32)
            {
                for (int i = 0; i < frameCount * channels; i++)
                {
                    int v = ReadIntLE(bytes, src);
                    samples[dst++] = v / 2147483648f;
                    src += 4;
                }
            }
            else
            {
                throw new Exception($"WavDecoder: unsupported bitsPerSample {bitsPerSample} for format {audioFormat}");
            }

            var clip = AudioClip.Create(clipName, frameCount, channels, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        private static string ReadFourCC(byte[] b, int o)
        {
            return System.Text.Encoding.ASCII.GetString(b, o, 4);
        }

        private static int ReadIntLE(byte[] b, int o)
        {
            return b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);
        }

        private static ushort ReadUShortLE(byte[] b, int o)
        {
            return (ushort)(b[o] | (b[o + 1] << 8));
        }

        private static float ReadFloatLE(byte[] b, int o)
        {
            var tmp = new byte[4];
            Buffer.BlockCopy(b, o, tmp, 0, 4);
            return BitConverter.ToSingle(tmp, 0);
        }

        private static int ReadInt24LE(byte[] b, int o)
        {
            int v = b[o] | (b[o + 1] << 8) | (b[o + 2] << 16);
            // sign extend
            if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
            return v;
        }
    }
}
