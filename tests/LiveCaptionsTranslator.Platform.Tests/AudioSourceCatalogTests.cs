using LiveCaptionsTranslator.Core.Audio;
using LiveCaptionsTranslator.Platform.Windows;

namespace LiveCaptionsTranslator.Platform.Tests;

public class AudioSourceCatalogTests
{
    [Fact]
    public void Catalog_exposes_loopback_and_microphone()
    {
        Assert.Equal(new[] { "wasapi-loopback", "wasapi-microphone" }, AudioSourceCatalog.Ids);
    }

    [Theory]
    [InlineData("wasapi-loopback", "系统声音（WASAPI 环回）")]
    [InlineData("wasapi-microphone", "麦克风")]
    public void Creating_a_source_does_not_open_an_audio_device(string id, string displayName)
    {
        // Constructing must stay side-effect free: the device is only opened from StartAsync.
        var source = AudioSourceCatalog.Create(id);

        Assert.Equal(id, source.Id);
        Assert.Equal(displayName, source.DisplayName);
        Assert.False(source.IsCapturing);
    }

    [Fact]
    public void Unknown_ids_are_rejected()
    {
        Assert.False(AudioSourceCatalog.TryCreate("nope", out var source));
        Assert.Null(source);
        Assert.Throws<KeyNotFoundException>(() => AudioSourceCatalog.Create("nope"));
    }

    [Fact]
    public async Task Sources_are_disposable_without_having_been_started()
    {
        foreach (string id in AudioSourceCatalog.Ids)
        {
            var source = AudioSourceCatalog.Create(id);
            await source.DisposeAsync();
            await source.StopAsync();
        }
    }

    [Fact]
    public void Audio_format_is_the_one_asr_providers_expect()
    {
        Assert.Equal(16000, AudioFormat.SampleRate);
        Assert.Equal(1, AudioFormat.Channels);
        Assert.Equal(16, AudioFormat.BitsPerSample);
        Assert.Equal(40, AudioFormat.ChunkMs);
        Assert.Equal(640, AudioFormat.ChunkSamples);
        Assert.Equal(1280, AudioFormat.ChunkBytes);
    }
}
