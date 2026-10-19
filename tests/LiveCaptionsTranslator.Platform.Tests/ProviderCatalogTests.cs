using LiveCaptionsTranslator.Core.Asr;
using LiveCaptionsTranslator.Platform.Windows;

namespace LiveCaptionsTranslator.Platform.Tests;

public class ProviderCatalogTests
{
    private static AsrProviderRegistry BuildRegistry()
    {
        var registry = new AsrProviderRegistry();
        ProviderCatalog.RegisterAll(registry);
        return registry;
    }

    [Fact]
    public void Catalog_registers_the_windows_livecaptions_provider()
    {
        var registry = BuildRegistry();

        Assert.True(registry.IsRegistered(WindowsLiveCaptionsProvider.ProviderId));
        Assert.Contains(WindowsLiveCaptionsProvider.ProviderId, registry.Ids);
    }

    [Fact]
    public void Catalog_registers_both_asr_backends()
    {
        var registry = BuildRegistry();

        Assert.Equal(new[] { "windows-livecaptions", "iflytek-iat" }, registry.Ids);
        Assert.True(registry.IsRegistered("iflytek-iat"));
    }

    [Fact]
    public void The_two_backends_have_the_expected_shape()
    {
        var registry = BuildRegistry();

        // Live Captions reads the OS caption window; iFlytek needs captured audio.
        Assert.False(registry.Create("windows-livecaptions").RequiresAudioSource);
        Assert.True(registry.Create("iflytek-iat").RequiresAudioSource);
    }

    [Fact]
    public void Verified_language_state_survives_the_catalog()
    {
        var registry = BuildRegistry();

        // Only the language covered by the CI integration sample is verified, and that flag has
        // to reach the settings UI through the registry.
        var iflytek = registry.Create("iflytek-iat");
        Assert.Contains(iflytek.SupportedLanguages, language => language.Verified && language.Id == "zh_cn");
        Assert.True(iflytek.LanguageIsHostControlled);

        // Live Captions picks its own language in the OS, so nothing can be verified from here.
        var liveCaptions = registry.Create("windows-livecaptions");
        Assert.All(liveCaptions.SupportedLanguages, language => Assert.False(language.Verified));
        Assert.False(liveCaptions.LanguageIsHostControlled);
    }

    [Fact]
    public void Windows_livecaptions_provider_reports_its_contract()
    {
        var provider = BuildRegistry().Create(WindowsLiveCaptionsProvider.ProviderId);

        Assert.Equal("windows-livecaptions", provider.Id);
        Assert.Equal("Windows Live Captions", provider.DisplayName);

        // It reads the OS caption window, so it must never ask the host for audio.
        Assert.False(provider.RequiresAudioSource);
        Assert.NotEmpty(provider.SupportedLanguages);
    }

    [Fact]
    public void Windows_livecaptions_languages_are_unverified()
    {
        // The language is picked inside Windows Live Captions itself, so this project cannot
        // claim any of them as verified against a real speech sample.
        var provider = BuildRegistry().Create(WindowsLiveCaptionsProvider.ProviderId);

        Assert.All(provider.SupportedLanguages, language => Assert.False(language.Verified));
    }

    [Fact]
    public void Catalog_registration_is_idempotent_per_registry()
    {
        var registry = BuildRegistry();

        // Registering twice on the same registry must fail loudly instead of silently replacing.
        Assert.Throws<InvalidOperationException>(() => ProviderCatalog.RegisterAll(registry));
    }
}
