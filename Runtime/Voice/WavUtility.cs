using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace SplatPresso.Voice
{
    /// <summary>Encodes float samples (-1..1) as 16-bit PCM: RIFF/WAV files and raw little-endian PCM16.</summary>
    public static class WavUtility
    {
        const int kHeaderBytes = 44;

        /// <summary>A complete 16-bit PCM RIFF/WAV file of <paramref name="samples"/> (interleaved when multi-channel).</summary>
        public static byte[] FromSamples(float[] samples, int sampleRate, int channels) =>
            FromSamples(samples, samples != null ? samples.Length : 0, sampleRate, channels);

        /// <summary>A complete 16-bit PCM RIFF/WAV file of the first <paramref name="count"/> samples.</summary>
        public static byte[] FromSamples(float[] samples, int count, int sampleRate, int channels)
        {
            if (samples == null)
                samples = Array.Empty<float>();
            count = Mathf.Clamp(count, 0, samples.Length);
            channels = Mathf.Max(1, channels);
            int byteCount = count * 2;
            var bytes = new byte[kHeaderBytes + byteCount];
            using (var stream = new MemoryStream(bytes))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + byteCount);
                writer.Write(Encoding.ASCII.GetBytes("WAVE"));
                writer.Write(Encoding.ASCII.GetBytes("fmt "));
                writer.Write(16);                             // fmt chunk size
                writer.Write((short)1);                       // PCM
                writer.Write((short)channels);
                writer.Write(sampleRate);
                writer.Write(sampleRate * channels * 2);      // byte rate
                writer.Write((short)(channels * 2));          // block align
                writer.Write((short)16);                      // bits per sample
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(byteCount);
                writer.Flush();
            }
            WritePcm16(samples, 0, count, bytes, kHeaderBytes);
            return bytes;
        }

        /// <summary>Writes a WAV file (via a temp file + move so a crash never leaves a half-written file).</summary>
        public static void WriteFile(string path, float[] samples, int count, int sampleRate, int channels = 1)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, FromSamples(samples, count, sampleRate, channels));
            if (File.Exists(path))
                File.Delete(path);
            File.Move(tmp, path);
        }

        /// <summary>Little-endian PCM16 bytes of <paramref name="count"/> samples starting at <paramref name="offset"/>.</summary>
        public static byte[] ToPcm16(float[] samples, int offset, int count)
        {
            var bytes = new byte[count * 2];
            WritePcm16(samples, offset, count, bytes, 0);
            return bytes;
        }

        /// <summary>Quantizes one sample to PCM16 (clamped; NaN becomes silence).</summary>
        public static short ToPcm16Sample(float f)
        {
            if (float.IsNaN(f))
                return 0;
            if (f > 1f) f = 1f;
            else if (f < -1f) f = -1f;
            return (short)Mathf.RoundToInt(f * 32767f);
        }

        static void WritePcm16(float[] samples, int offset, int count, byte[] dest, int destOffset)
        {
            for (int i = 0; i < count; i++)
            {
                short s = ToPcm16Sample(samples[offset + i]);
                dest[destOffset + i * 2] = (byte)(s & 0xFF);
                dest[destOffset + i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }
        }
    }
}
