using LiveCaptionsTranslator.Core.Audio;

namespace LiveCaptionsTranslator.Core.Tests;

/// <summary>The audio side of the plugin seam, shaped like the ASR and translation registries.</summary>
public class AudioSourceRegistryTests
{
    private sealed class FakeSource(string id) : IAudioSource
    {
        public string Id { get; } = id;

        public string DisplayName => $"Fake {Id}";

        public bool IsCapturing => false;

        public event EventHandler<AudioFrameEventArgs>? FrameAvailable
        {
            add { }
            remove { }
        }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void Registers_and_creates_sources()
    {
        var registry = new AudioSourceRegistry();
        registry.Register("wasapi-loopback", () => new FakeSource("wasapi-loopback"));

        Assert.True(registry.IsRegistered("WASAPI-LOOPBACK"));
        Assert.Equal(new[] { "wasapi-loopback" }, registry.Ids);
        Assert.Equal("wasapi-loopback", registry.Create("wasapi-loopback").Id);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void Duplicate_ids_are_rejected()
    {
        var registry = new AudioSourceRegistry();
        registry.Register("dup", () => new FakeSource("dup"));

        Assert.Throws<InvalidOperationException>(
            () => registry.Register("DUP", () => new FakeSource("dup")));
    }

    [Fact]
    public void Unknown_ids_are_reported()
    {
        var registry = new AudioSourceRegistry();

        Assert.False(registry.TryCreate("nope", out var source));
        Assert.Null(source);
        Assert.Throws<KeyNotFoundException>(() => registry.Create("nope"));
    }
}
