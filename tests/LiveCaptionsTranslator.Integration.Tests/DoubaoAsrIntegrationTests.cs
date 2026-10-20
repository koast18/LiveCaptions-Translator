using LiveCaptionsTranslator.Core.Asr;
using LiveCaptionsTranslator.Core.Audio;
using LiveCaptionsTranslator.Core.Doubao;

namespace LiveCaptionsTranslator.Integration.Tests;

/// <summary>
/// Talks to the real Doubao endpoint with the committed speech sample.
/// <para>
/// The device is registered from scratch here (the registration is pure C#, no Python and no
/// committed credential), then the sample is captured, Opus encoded, streamed and the transcript
/// asserted. This is the only test that proves the whole hand-rolled chain works.
/// </para>
/// </summary>
public class DoubaoAsrIntegrationTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static WavAudio ReadFixture() =>
        WavFile.Read(FixturePath("iflytek-zh-cn-16k.wav"));

    [Fact]
    public void The_committed_sample_is_the_expected_format()
    {
        var audio = ReadFixture();

        Assert.Equal(AudioFormat.SampleRate, audio.SampleRate);
        Assert.Equal(AudioFormat.Channels, audio.Channels);
        Assert.Equal(AudioFormat.BitsPerSample, audio.BitsPerSample);
    }

    [Fact]
    public async Task Registration_produces_a_usable_device_pair()
    {
        var credentials = await new DoubaoDeviceRegistrar().RegisterAsync();

        Assert.True(credentials.IsComplete, "registration did not return a device_id/app_key pair");
        Assert.False(string.IsNullOrWhiteSpace(credentials.DeviceId));
        Assert.False(string.IsNullOrWhiteSpace(credentials.AppKey));

        // The device id is a decimal string, the app key a short opaque token.
        Assert.All(credentials.DeviceId, character => Assert.True(char.IsDigit(character)));
        Assert.InRange(credentials.AppKey.Length, 4, 64);
    }

    [Fact]
    public async Task The_real_endpoint_recognises_the_committed_chinese_sample()
    {
        var credentials = await new DoubaoDeviceRegistrar().RegisterAsync();
        Assert.True(credentials.IsComplete, "could not register a device; the endpoint was not exercised");

        var audio = ReadFixture();
        var source = new WavAudioSource(audio.Pcm);
        var sink = new CollectingSink();

        await using var provider = new DoubaoAsrProvider(
            credentials,
            streamOptions: new DoubaoStreamOptions
            {
                MaxConsecutiveFailures = 1,
                ReconnectDelay = TimeSpan.FromMilliseconds(200),
            });

        await provider.StartAsync(new AsrContext
        {
            Sink = sink,
            Options = new AsrOptions(),
            AudioSource = source,
        });

        await source.EmitAllAsync();
        await provider.StopAsync();

        Assert.True(
            sink.Errors.Count == 0,
            "provider reported errors: " + string.Join(" | ", sink.Errors.Select(error => error.Message)));

        // The service reports problems as notices rather than exceptions.
        string notices = string.Join(" | ", sink.Notices);
        string transcript = sink.Transcript;

        Assert.False(
            string.IsNullOrWhiteSpace(transcript),
            $"the service returned no transcript. notices: {notices}");

        // Exact punctuation is the service's business; the words are what matter.
        Assert.Contains("今天天气", transcript);
        Assert.DoesNotContain("失败", notices);
    }
}
