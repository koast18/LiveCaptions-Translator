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
    public void Catalog_registers_all_asr_backends()
    {
        var registry = BuildRegistry();

        Assert.Equal(new[] { "windows-livecaptions", "iflytek-iat", "doubao-asr" }, registry.Ids);
        Assert.True(registry.IsRegistered("iflytek-iat"));
        Assert.True(registry.IsRegistered("doubao-asr"));
    }

    [Fact]
    public void Doubao_is_listed_but_unavailable_without_credentials()
    {
        var provider = BuildRegistry().Create("doubao-asr");

        Assert.Equal("doubao-asr", provider.Id);
        Assert.True(provider.RequiresAudioSource);
        Assert.False(provider.LanguageIsHostControlled);

        // Selecting it in the picker must not blow up; it just explains what is missing.
        Assert.False(provider.IsAvailable);
        Assert.False(string.IsNullOrWhiteSpace(provider.UnavailableReason));
        Assert.Contains("device_id", provider.UnavailableReason);
    }

    [Fact]
    public void Doubao_becomes_available_once_credentials_exist()
    {
        var registry = new AsrProviderRegistry();
        ProviderCatalog.RegisterAll(
            registry,
            () => new LiveCaptionsTranslator.Core.Doubao.DoubaoCredentials("1782778396951964", "key", "", ""));

        var provider = registry.Create("doubao-asr");

        Assert.True(provider.IsAvailable);
        Assert.Null(provider.UnavailableReason);
    }

    [Fact]
    public void A_provider_factory_that_returns_null_keeps_the_picker_working()
    {
        var registry = new AsrProviderRegistry();
        ProviderCatalog.RegisterAll(registry, () => null);

        var provider = registry.Create("doubao-asr");

        Assert.False(provider.IsAvailable);
        Assert.NotNull(provider.UnavailableReason);
    }

    [Fact]
    public void The_two_backends_have_the_expected_shape()
    {
        var registry = BuildRegistry();

        // Live Captions reads the OS caption window; iFlytek needs captured audio.
        Assert.False(registry.Create("windows-livecaptions").RequiresAudioSource);
        Assert.True(registry.Create("iflytek-iat").RequiresAudioSource);
    }

    [Theory]
    [InlineData(22621, true, true)]    // Windows 11 22H2 with the component installed
    [InlineData(22631, true, true)]    // Windows 11 23H2
    [InlineData(19045, true, false)]   // Windows 10 22H2: build is too old
    [InlineData(20348, true, false)]   // Server 2022
    [InlineData(22621, false, false)]  // component missing
    public void Live_captions_availability_rule(int build, bool installed, bool expected)
        => Assert.Equal(expected, WindowsLiveCaptionsProvider.IsSupportedOn(build, installed));

    [Fact]
    public void Live_captions_reports_availability_consistently_on_this_machine()
    {
        var provider = BuildRegistry().Create("windows-livecaptions");

        if (provider.IsAvailable)
        {
            Assert.Null(provider.UnavailableReason);
        }
        else
        {
            // A backend that cannot run must say why, so the settings UI can explain it.
            Assert.False(string.IsNullOrWhiteSpace(provider.UnavailableReason));
        }
    }

    [Fact]
    public void The_iflytek_backend_works_without_any_windows_component()
    {
        // This is what makes the app usable on Windows 10: no Live Captions, no problem.
        var provider = BuildRegistry().Create("iflytek-iat");

        Assert.True(provider.IsAvailable);
        Assert.Null(provider.UnavailableReason);
    }

    [Fact]
    public void The_selector_never_picks_a_backend_that_cannot_run()
    {
        var registry = BuildRegistry();
        var selection = AsrProviderSelector.Select(registry, "windows-livecaptions");

        var chosen = registry.Create(selection.ProviderId);
        Assert.True(chosen.IsAvailable, $"selector chose '{selection.ProviderId}', which is not available here");
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
