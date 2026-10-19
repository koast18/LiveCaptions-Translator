using LiveCaptionsTranslator.Core.Audio;

namespace LiveCaptionsTranslator.Core.Tests;

public class WavFileTests : IDisposable
{
    private readonly string directory =
        Path.Combine(Path.GetTempPath(), "lct-tests-" + Guid.NewGuid().ToString("N"));

    public WavFileTests() => Directory.CreateDirectory(directory);

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private string PathFor(string name) => Path.Combine(directory, name);

    private static byte[] Ramp(int samples)
    {
        var pcm = new byte[samples * sizeof(short)];
        for (int i = 0; i < samples; i++)
        {
            short value = (short)(i - (samples / 2));
            pcm[i * 2] = (byte)(value & 0xFF);
            pcm[(i * 2) + 1] = (byte)((value >> 8) & 0xFF);
        }

        return pcm;
    }

    [Fact]
    public void Written_file_reads_back_with_the_same_samples_and_format()
    {
        var pcm = Ramp(1600);
        string path = PathFor("round-trip.wav");

        WavFile.Write(path, pcm);
        var audio = WavFile.Read(path);

        Assert.Equal(pcm, audio.Pcm);
        Assert.Equal(AudioFormat.SampleRate, audio.SampleRate);
        Assert.Equal(AudioFormat.Channels, audio.Channels);
        Assert.Equal(AudioFormat.BitsPerSample, audio.BitsPerSample);
    }

    [Fact]
    public void Written_file_has_a_valid_riff_header()
    {
        string path = PathFor("header.wav");
        WavFile.Write(path, Ramp(10));

        var bytes = File.ReadAllBytes(path);

        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
        Assert.Equal("fmt ", System.Text.Encoding.ASCII.GetString(bytes, 12, 4));
        Assert.Equal(16, BitConverter.ToInt32(bytes, 16));        // fmt chunk size
        Assert.Equal(1, BitConverter.ToInt16(bytes, 20));         // PCM
        Assert.Equal(1, BitConverter.ToInt16(bytes, 22));         // mono
        Assert.Equal(16000, BitConverter.ToInt32(bytes, 24));     // sample rate
        Assert.Equal(32000, BitConverter.ToInt32(bytes, 28));     // byte rate
        Assert.Equal(2, BitConverter.ToInt16(bytes, 32));         // block align
        Assert.Equal(16, BitConverter.ToInt16(bytes, 34));        // bits per sample
        Assert.Equal("data", System.Text.Encoding.ASCII.GetString(bytes, 36, 4));
        Assert.Equal(10 * sizeof(short), BitConverter.ToInt32(bytes, 40));
        Assert.Equal(44 + (10 * sizeof(short)), bytes.Length);
    }

    [Fact]
    public void Duration_follows_the_sample_rate()
    {
        string path = PathFor("duration.wav");

        // 1.5 s of 16 kHz mono PCM16
        WavFile.Write(path, new byte[(int)(1.5 * 16000 * sizeof(short))]);

        Assert.Equal(TimeSpan.FromSeconds(1.5), WavFile.Read(path).Duration);
    }

    [Fact]
    public void Unknown_chunks_are_skipped()
    {
        var pcm = Ramp(8);
        string path = PathFor("with-list.wav");
        WavFile.Write(path, pcm);

        // Splice a LIST chunk in front of `data`, the way most editors do.
        var original = File.ReadAllBytes(path);
        int dataOffset = 36;
        using (var stream = File.Create(path))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(original, 0, dataOffset);
            writer.Write("LIST"u8);
            writer.Write(6);
            writer.Write("INFO"u8);
            writer.Write((short)0);
            writer.Write(original, dataOffset, original.Length - dataOffset);
        }

        Assert.Equal(pcm, WavFile.Read(path).Pcm);
    }

    [Fact]
    public void Non_riff_input_is_rejected()
    {
        string path = PathFor("bogus.wav");
        File.WriteAllBytes(path, "not a wave file at all"u8.ToArray());

        Assert.Throws<InvalidDataException>(() => WavFile.Read(path));
    }
}
