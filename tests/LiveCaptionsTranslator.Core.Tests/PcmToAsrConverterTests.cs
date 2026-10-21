using LiveCaptionsTranslator.Core.Audio;

namespace LiveCaptionsTranslator.Core.Tests;

public class PcmToAsrConverterTests
{
    private const int SourceRate = 48000;
    private const int TargetRate = AudioFormat.SampleRate;

    private static float[] ConstantStereo(int frames, float value)
    {
        var samples = new float[frames * 2];
        Array.Fill(samples, value);
        return samples;
    }

    [Fact]
    public void Downmixes_stereo_and_resamples_48k_to_16k()
    {
        // 4800 frames @ 48 kHz = 100 ms, which must become 1600 samples @ 16 kHz.
        var converter = new PcmToAsrConverter(SourceRate, 2);

        var pcm = converter.Convert(ConstantStereo(4800, 0.5f));

        Assert.Equal(1600 * sizeof(short), pcm.Length);

        // 0.5f * short.MaxValue == 16383.5 -> rounds to even -> 16384 == 0x4000 (little-endian).
        for (int i = 0; i < pcm.Length; i += 2)
            Assert.Equal(0x4000, pcm[i] | (pcm[i + 1] << 8));
    }

    [Fact]
    public void Output_length_tracks_the_source_duration()
    {
        var converter = new PcmToAsrConverter(SourceRate, 1);

        var pcm = converter.Convert(new float[SourceRate]);   // exactly one second

        Assert.Equal(TargetRate * sizeof(short), pcm.Length);
    }

    [Fact]
    public void Chunked_conversion_matches_single_shot()
    {
        var samples = new float[44100 * 2];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = MathF.Sin(i * 0.01f) * 0.8f;

        var singleShot = new PcmToAsrConverter(44100, 2).Convert(samples);

        var chunked = new PcmToAsrConverter(44100, 2);
        var pieces = new List<byte>();
        const int chunkFrames = 1024;
        for (int offset = 0; offset < 44100; offset += chunkFrames)
        {
            int frames = Math.Min(chunkFrames, 44100 - offset);
            pieces.AddRange(chunked.Convert(samples.AsSpan(offset * 2, frames * 2)));
        }

        Assert.Equal(singleShot, pieces.ToArray());
    }

    [Fact]
    public void Mono_16k_input_is_passed_through_unchanged()
    {
        var converter = new PcmToAsrConverter(AudioFormat.SampleRate, 1);
        var samples = new[] { 0f, 0.25f, -0.25f, 1f, -1f, 0.5f };

        var pcm = converter.Convert(samples);

        // 16 kHz -> 16 kHz is a straight copy. The very last sample is held back until more
        // audio arrives (it is the right-hand side of the next interpolation), which is what
        // keeps a continuous stream gapless.
        Assert.Equal((samples.Length - 1) * sizeof(short), pcm.Length);
        for (int i = 0; i < samples.Length - 1; i++)
            Assert.Equal(PcmToAsrConverter.ToPcm16(samples[i]), BitConverter.ToInt16(pcm, i * 2));
    }

    [Fact]
    public void Held_back_sample_is_emitted_when_the_next_chunk_arrives()
    {
        var converter = new PcmToAsrConverter(AudioFormat.SampleRate, 1);
        var samples = new[] { 1f, -1f, 1f, -1f };

        var first = converter.Convert(samples.AsSpan(0, 2));
        var second = converter.Convert(samples.AsSpan(2, 2));

        // No audio is ever dropped: the sample held back after the first chunk is the first
        // thing the second chunk produces, and only the very last input sample is still pending.
        Assert.Equal(sizeof(short), first.Length);
        Assert.Equal(short.MaxValue, BitConverter.ToInt16(first, 0));

        Assert.Equal(2 * sizeof(short), second.Length);
        Assert.Equal(-short.MaxValue, BitConverter.ToInt16(second, 0));
        Assert.Equal(short.MaxValue, BitConverter.ToInt16(second, 2));

        Assert.Equal(samples.Length - 1, (first.Length + second.Length) / sizeof(short));
    }

    [Fact]
    public void Out_of_range_samples_are_clamped()
    {
        var converter = new PcmToAsrConverter(AudioFormat.SampleRate, 1);

        var pcm = converter.Convert(new[] { 3f, -3f, 0f });

        Assert.Equal(short.MaxValue, BitConverter.ToInt16(pcm, 0));
        Assert.Equal(-short.MaxValue, BitConverter.ToInt16(pcm, 2));
    }

    [Fact]
    public void Incomplete_frames_are_ignored()
    {
        var converter = new PcmToAsrConverter(AudioFormat.SampleRate, 2);

        // Five floats are two and a half stereo frames -> only two whole frames are used,
        // and one of those is held back.
        var pcm = converter.Convert(new[] { 1f, 1f, 1f, 1f, 1f });

        Assert.Equal(sizeof(short), pcm.Length);
    }

    [Fact]
    public void Empty_input_produces_no_output()
    {
        var converter = new PcmToAsrConverter(SourceRate, 2);

        Assert.Empty(converter.Convert(ReadOnlySpan<float>.Empty));
    }
}
