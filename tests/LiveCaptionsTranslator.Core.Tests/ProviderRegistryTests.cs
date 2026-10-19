using LiveCaptionsTranslator.Core.Asr;
using LiveCaptionsTranslator.Core.Audio;
using LiveCaptionsTranslator.Core.Translation;

namespace LiveCaptionsTranslator.Core.Tests;

public class ProviderRegistryTests
{
    private sealed class FakeAsrProvider(string id) : IAsrProvider
    {
        public string Id { get; } = id;

        public string DisplayName => $"Fake {Id}";

        public bool RequiresAudioSource => true;

        public IReadOnlyList<AsrLanguage> SupportedLanguages { get; } =
            new[] { new AsrLanguage("zh_cn", "中文（普通话）", "mandarin", Verified: true) };

        public Task StartAsync(AsrContext context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task StopAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeTranslationProvider(string id) : ITranslationProvider
    {
        public string Id { get; } = id;

        public string DisplayName => $"Fake {Id}";

        public bool RequiresConfiguration => false;

        public bool IsLlmBased => false;

        public Task<string> TranslateAsync(
            TranslationRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult($"[{Id}]{request.Text}");
    }

    [Fact]
    public void Asr_registry_creates_registered_provider()
    {
        var registry = new AsrProviderRegistry();
        registry.Register("fake-asr", () => new FakeAsrProvider("fake-asr"));

        Assert.True(registry.IsRegistered("FAKE-ASR"));
        Assert.Equal(new[] { "fake-asr" }, registry.Ids);
        Assert.Equal("fake-asr", registry.Create("fake-asr").Id);
    }

    [Fact]
    public void Asr_registry_rejects_duplicate_ids()
    {
        var registry = new AsrProviderRegistry();
        registry.Register("dup", () => new FakeAsrProvider("dup"));

        Assert.Throws<InvalidOperationException>(
            () => registry.Register("DUP", () => new FakeAsrProvider("dup")));
    }

    [Fact]
    public void Asr_registry_reports_unknown_ids()
    {
        var registry = new AsrProviderRegistry();

        Assert.False(registry.TryCreate("nope", out var provider));
        Assert.Null(provider);
        Assert.Throws<KeyNotFoundException>(() => registry.Create("nope"));
    }

    [Fact]
    public void Asr_registry_describes_all_providers_in_registration_order()
    {
        var registry = new AsrProviderRegistry();
        registry.Register("windows-livecaptions", () => new FakeAsrProvider("windows-livecaptions"));
        registry.Register("iflytek-iat", () => new FakeAsrProvider("iflytek-iat"));

        var described = registry.DescribeAll();

        Assert.Equal(new[] { "windows-livecaptions", "iflytek-iat" }, described.Select(p => p.Id));
        Assert.All(described, p => Assert.True(p.RequiresAudioSource));
    }

    [Fact]
    public void Asr_language_defaults_to_unverified()
    {
        var language = new AsrLanguage("ja", "日本語");

        Assert.False(language.Verified);
        Assert.Null(language.Accent);
    }

    [Fact]
    public async Task Translation_registry_creates_registered_provider()
    {
        var registry = new TranslationProviderRegistry();
        registry.Register("fake-mt", () => new FakeTranslationProvider("fake-mt"));

        var provider = registry.Create("fake-mt");
        var result = await provider.TranslateAsync(
            new TranslationRequest { Text = "hi", TargetLanguage = "zh-Hans" });

        Assert.Equal("[fake-mt]hi", result);
        Assert.False(provider.IsLlmBased);
        Assert.True(registry.IsRegistered("fake-mt"));
        Assert.False(registry.TryCreate("missing", out _));
    }
}
