using LiveCaptionsTranslator.Core.Asr;
using LiveCaptionsTranslator.Core.Audio;
using LiveCaptionsTranslator.Core.Iflytek;

namespace LiveCaptionsTranslator.Integration.Tests;

/// <summary>
/// Talks to the real iFlytek IAT endpoint with a committed speech sample.
/// <para>
/// This is the only test that proves the hand-rolled protocol actually works end to end: the
/// unit tests compare against golden vectors produced by the reference implementation, but only
/// this one shows that the service accepts our frames and returns the expected words.
/// </para>
/// </summary>
public class IflytekIatIntegrationTests
{
    /// <summary>What the committed sample says.</summary>
    private const string ExpectedSentence = "今天天气不错，我们一起去公园散步吧。";

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public void The_committed_sample_is_16k_mono_pcm16()
    {
        var audio = WavFile.Read(FixturePath("iflytek-zh-cn-16k.wav"));

        Assert.Equal(AudioFormat.SampleRate, audio.SampleRate);
        Assert.Equal(AudioFormat.Channels, audio.Channels);
        Assert.Equal(AudioFormat.BitsPerSample, audio.BitsPerSample);
        Assert.True(audio.Pcm.Length > 16000 * 2, "sample should be longer than one second");
    }

    [Fact]
    public async Task The_real_endpoint_recognises_the_committed_chinese_sample()
    {
        var audio = WavFile.Read(FixturePath("iflytek-zh-cn-16k.wav"));
        var source = new WavAudioSource(audio.Pcm);
        var sink = new CollectingSink();

        await using var provider = new IflytekIatProvider(new IflytekIatOptions
        {
            ReceiveTimeout = TimeSpan.FromSeconds(20),
            GracefulStopTimeout = TimeSpan.FromSeconds(25),
            ReconnectDelay = TimeSpan.FromSeconds(1),
            MaxReconnectAttempts = 2,
        });

        await provider.StartAsync(new AsrContext
        {
            Sink = sink,
            Options = new AsrOptions { LanguageId = "zh_cn", Accent = "mandarin" },
            AudioSource = source,
        });

        await source.EmitAllAsync();
        await provider.StopAsync();

        Assert.True(
            sink.Errors.Count == 0,
            "provider reported errors: " + string.Join(" | ", sink.Errors.Select(e => e.Message)));

        string transcript = sink.Transcript;

        Assert.False(
            string.IsNullOrWhiteSpace(transcript),
            "the service returned no transcript; notices: " + string.Join(" | ", sink.Notices));

        // Exact punctuation is the service's business, the words are ours.
        Assert.Contains("今天天气", transcript);
        Assert.Contains("公园", transcript);
        Assert.Contains("散步", transcript);
    }

    [Fact]
    public async Task The_same_sample_through_the_provider_matches_the_expected_sentence()
    {
        var audio = WavFile.Read(FixturePath("iflytek-zh-cn-16k.wav"));
        var source = new WavAudioSource(audio.Pcm);
        var sink = new CollectingSink();

        await using var provider = new IflytekIatProvider(new IflytekIatOptions
        {
            ReceiveTimeout = TimeSpan.FromSeconds(20),
            GracefulStopTimeout = TimeSpan.FromSeconds(25),
            ReconnectDelay = TimeSpan.FromSeconds(1),
            MaxReconnectAttempts = 2,
        });

        await provider.StartAsync(new AsrContext
        {
            Sink = sink,
            Options = new AsrOptions { LanguageId = "zh_cn", Accent = "mandarin" },
            AudioSource = source,
        });

        await source.EmitAllAsync();
        await provider.StopAsync();

        // Compare without punctuation so a stray comma cannot fail the build.
        string normalized = new string(sink.Transcript.Where(c => !char.IsPunctuation(c)).ToArray());
        string expected = new string(ExpectedSentence.Where(c => !char.IsPunctuation(c)).ToArray());

        Assert.Equal(expected, normalized);
    }
}
