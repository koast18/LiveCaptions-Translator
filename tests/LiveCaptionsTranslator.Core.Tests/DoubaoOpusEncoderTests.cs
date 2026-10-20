using LiveCaptionsTranslator.Core.Doubao;

namespace LiveCaptionsTranslator.Core.Tests;

public class DoubaoOpusEncoderTests
{
    private static short[] Silence(int samples) => new short[samples];

    private static short[] Tone(int samples, double frequency = 440)
    {
        var pcm = new short[samples];

        for (int i = 0; i < samples; i++)
        {
            double value = Math.Sin(2 * Math.PI * frequency * i / DoubaoOpusEncoder.SampleRate);
            pcm[i] = (short)(value * 12000);
        }

        return pcm;
    }

    [Fact]
    public void Frame_geometry_matches_the_session_configuration()
    {
        Assert.Equal(16000, DoubaoOpusEncoder.SampleRate);
        Assert.Equal(20, DoubaoOpusEncoder.FrameMilliseconds);
        Assert.Equal(320, DoubaoOpusEncoder.FrameSamples);
        Assert.Equal(32000, DoubaoOpusEncoder.DefaultBitrate);
        Assert.Equal(1275, DoubaoOpusEncoder.MaxPacketBytes);
    }

    [Fact]
    public void One_second_produces_fifty_packets()
    {
        using var encoder = new DoubaoOpusEncoder();

        var packets = encoder.Encode(Silence(16000));

        Assert.Equal(50, packets.Count);
    }

    [Theory]
    [InlineData(320, 1)]
    [InlineData(321, 2)]
    [InlineData(640, 2)]
    [InlineData(1000, 4)]
    [InlineData(1, 1)]
    public void Packet_count_is_ceil_of_samples_over_frame_size(int samples, int expected)
    {
        using var encoder = new DoubaoOpusEncoder();

        Assert.Equal(expected, encoder.Encode(Silence(samples)).Count);
    }

    [Fact]
    public void Every_packet_is_a_plausible_opus_packet()
    {
        using var encoder = new DoubaoOpusEncoder();

        var packets = encoder.Encode(Tone(16000));

        Assert.Equal(50, packets.Count);

        foreach (var packet in packets)
        {
            Assert.NotEmpty(packet);
            Assert.True(packet.Length <= DoubaoOpusEncoder.MaxPacketBytes,
                $"packet of {packet.Length} bytes exceeds the Opus maximum");
        }
    }

    [Fact]
    public void Empty_input_produces_no_packets()
    {
        using var encoder = new DoubaoOpusEncoder();

        Assert.Empty(encoder.Encode(ReadOnlySpan<short>.Empty));
    }

    [Fact]
    public void A_single_frame_can_be_encoded_directly()
    {
        using var encoder = new DoubaoOpusEncoder();

        byte[] packet = encoder.EncodeFrame(Tone(DoubaoOpusEncoder.FrameSamples));

        Assert.NotEmpty(packet);
        Assert.True(packet.Length <= DoubaoOpusEncoder.MaxPacketBytes);
    }

    [Fact]
    public void A_wrong_frame_size_is_rejected()
    {
        using var encoder = new DoubaoOpusEncoder();

        Assert.Throws<ArgumentException>(() => encoder.EncodeFrame(Silence(319)));
        Assert.Throws<ArgumentException>(() => encoder.EncodeFrame(Silence(321)));
    }

    [Fact]
    public void A_partial_trailing_frame_is_padded_not_dropped()
    {
        using var encoder = new DoubaoOpusEncoder();

        // 400 samples = one full frame plus 80 samples of tail.
        var packets = encoder.Encode(Tone(400));

        Assert.Equal(2, packets.Count);
        Assert.All(packets, packet => Assert.NotEmpty(packet));
    }

    [Fact]
    public void Bitrate_is_configurable()
    {
        using var encoder = new DoubaoOpusEncoder(bitrate: 24000);

        Assert.Equal(24000, encoder.Bitrate);
    }

    [Fact]
    public void Silence_and_speech_do_not_encode_to_the_same_size()
    {
        // A sanity check that this is a real encoder and not a pass-through.
        using var encoder = new DoubaoOpusEncoder();

        int silenceBytes = encoder.Encode(Silence(16000)).Sum(packet => packet.Length);
        int toneBytes = encoder.Encode(Tone(16000)).Sum(packet => packet.Length);

        Assert.True(toneBytes > silenceBytes,
            $"expected speech to encode larger than silence, got {toneBytes} vs {silenceBytes}");
    }

    [Fact]
    public void The_encoder_stays_average_inside_the_bitrate_budget()
    {
        using var encoder = new DoubaoOpusEncoder();

        // One second at 32 kbit/s is at most 4000 bytes; CBR/VBR may undershoot but must not
        // blow past the budget by a wide margin.
        int totalBytes = encoder.Encode(Tone(16000)).Sum(packet => packet.Length);

        Assert.InRange(totalBytes, 500, 8000);
    }
}
