using System.Text;

namespace LiveCaptionsTranslator.Core.Audio
{
    /// <summary>Raw PCM plus the format it was stored in.</summary>
    public sealed record WavAudio(byte[] Pcm, int SampleRate, int Channels, int BitsPerSample)
    {
        public int BytesPerSecond => SampleRate * Channels * BitsPerSample / 8;

        public TimeSpan Duration =>
            BytesPerSecond == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)Pcm.Length / BytesPerSecond);
    }

    /// <summary>Minimal RIFF/WAVE reader and writer for the uncompressed PCM this app produces.</summary>
    public static class WavFile
    {
        private const int FormatPcm = 1;
        private const int FormatIeeeFloat = 3;

        public static void Write(
            string path,
            ReadOnlySpan<byte> pcm,
            int sampleRate = AudioFormat.SampleRate,
            int channels = AudioFormat.Channels,
            int bitsPerSample = AudioFormat.BitsPerSample)
        {
            using var stream = File.Create(path);
            Write(stream, pcm, sampleRate, channels, bitsPerSample);
        }

        public static void Write(
            Stream stream,
            ReadOnlySpan<byte> pcm,
            int sampleRate = AudioFormat.SampleRate,
            int channels = AudioFormat.Channels,
            int bitsPerSample = AudioFormat.BitsPerSample)
        {
            int blockAlign = channels * bitsPerSample / 8;

            using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
            writer.Write("RIFF"u8);
            writer.Write(36 + pcm.Length);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)FormatPcm);
            writer.Write((short)channels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * blockAlign);
            writer.Write((short)blockAlign);
            writer.Write((short)bitsPerSample);
            writer.Write("data"u8);
            writer.Write(pcm.Length);
            writer.Write(pcm);
        }

        public static WavAudio Read(string path)
        {
            using var stream = File.OpenRead(path);
            return Read(stream);
        }

        public static WavAudio Read(Stream stream)
        {
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

            if (new string(reader.ReadChars(4)) != "RIFF")
                throw new InvalidDataException("Not a RIFF file.");

            reader.ReadInt32();

            if (new string(reader.ReadChars(4)) != "WAVE")
                throw new InvalidDataException("Not a WAVE file.");

            int format = FormatPcm;
            int sampleRate = 0;
            int channels = 0;
            int bitsPerSample = 0;
            byte[]? data = null;

            while (stream.Position + 8 <= stream.Length)
            {
                string chunkId = new(reader.ReadChars(4));
                int chunkSize = reader.ReadInt32();
                long next = stream.Position + chunkSize + (chunkSize % 2);

                if (chunkId == "fmt ")
                {
                    format = reader.ReadInt16();
                    channels = reader.ReadInt16();
                    sampleRate = reader.ReadInt32();
                    reader.ReadInt32();   // byte rate
                    reader.ReadInt16();   // block align
                    bitsPerSample = reader.ReadInt16();
                }
                else if (chunkId == "data")
                {
                    data = reader.ReadBytes(chunkSize);
                }

                stream.Position = next;
            }

            if (data == null)
                throw new InvalidDataException("WAVE file has no data chunk.");

            if (format != FormatPcm && format != FormatIeeeFloat)
                throw new InvalidDataException($"Unsupported WAVE format tag {format}.");

            return new WavAudio(data, sampleRate, channels, bitsPerSample);
        }
    }
}
