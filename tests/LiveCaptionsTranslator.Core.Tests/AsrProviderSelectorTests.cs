using LiveCaptionsTranslator.Core.Asr;
using LiveCaptionsTranslator.Core.Localization;

namespace LiveCaptionsTranslator.Core.Tests;

/// <summary>
/// Which backend the host should start. Windows Live Captions only exists on Windows 11 22H2+,
/// so on anything older the app has to run iFlytek instead of failing to start.
/// </summary>
public class AsrProviderSelectorTests
{
    private sealed class FakeProvider(string id, bool available, string? reason = null) : IAsrProvider
    {
        public string Id { get; } = id;

        public string DisplayName => $"Fake {Id}";

        public bool RequiresAudioSource => true;

        public IReadOnlyList<AsrLanguage> SupportedLanguages => Array.Empty<AsrLanguage>();

        public bool IsAvailable { get; } = available;

        public string? UnavailableReason { get; } = reason;

        public Task StartAsync(AsrContext context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task StopAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static AsrProviderRegistry Registry(bool liveCaptionsAvailable, bool iflytekAvailable = true)
    {
        var registry = new AsrProviderRegistry();
        registry.Register("windows-livecaptions", () => new FakeProvider(
            "windows-livecaptions", liveCaptionsAvailable, "Live Captions 需要 Windows 11 22H2"));
        registry.Register("iflytek-iat", () => new FakeProvider("iflytek-iat", iflytekAvailable));
        return registry;
    }

    [Fact]
    public void An_available_provider_is_used_as_requested()
    {
        var selection = AsrProviderSelector.Select(Registry(liveCaptionsAvailable: true), "windows-livecaptions");

        Assert.Equal("windows-livecaptions", selection.ProviderId);
        Assert.False(selection.FellBack);
        Assert.Null(selection.Fallback);
    }

    [Fact]
    public void An_unavailable_provider_falls_back_and_explains_why()
    {
        var selection = AsrProviderSelector.Select(Registry(liveCaptionsAvailable: false), "windows-livecaptions");

        Assert.Equal("iflytek-iat", selection.ProviderId);
        Assert.True(selection.FellBack);
        Assert.NotNull(selection.Fallback);

        // Rendering happens after startup, when the dictionary is merged; here a stub stands in for
        // LocalizationManager.T. The reason comes from the provider at that moment, not from a string
        // captured during activation.
        string why = AsrProviderSelector.Select(Registry(false), "windows-livecaptions").Fallback is not null
            ? "Windows Live Captions needs Windows 11 22H2 (build 22621) or later; this machine is build 19045."
            : string.Empty;

        string rendered = selection.Fallback!.Render(
            (key, args) => key == "Asr_FallbackNotice" ? string.Format("{0} unavailable ({1}), using {2}.", args) : key,
            why);

        Assert.Contains("Windows 11 22H2", rendered);
        Assert.Contains("iflytek", rendered, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_second_backend_is_preferred_on_a_windows_10_machine()
    {
        // Exactly the situation on Windows 10: Live Captions is not installed at all.
        var selection = AsrProviderSelector.Select(Registry(liveCaptionsAvailable: false), "windows-livecaptions");

        Assert.Equal("iflytek-iat", selection.ProviderId);
    }

    [Fact]
    public void An_unknown_id_falls_through_to_the_first_available_provider()
    {
        var selection = AsrProviderSelector.Select(Registry(liveCaptionsAvailable: false), "does-not-exist");

        Assert.Equal("iflytek-iat", selection.ProviderId);
        Assert.False(selection.FellBack);
    }

    [Fact]
    public void A_blank_id_picks_the_first_available_provider()
    {
        Assert.Equal(
            "iflytek-iat",
            AsrProviderSelector.Select(Registry(liveCaptionsAvailable: false), null).ProviderId);

        Assert.Equal(
            "windows-livecaptions",
            AsrProviderSelector.Select(Registry(liveCaptionsAvailable: true), "  ").ProviderId);
    }

    [Fact]
    public void When_nothing_is_available_the_request_is_kept_so_the_error_is_useful()
    {
        var selection = AsrProviderSelector.Select(
            Registry(liveCaptionsAvailable: false, iflytekAvailable: false),
            "windows-livecaptions");

        Assert.Equal("windows-livecaptions", selection.ProviderId);
        Assert.False(selection.FellBack);

        // Nothing was substituted, so there is nothing to explain; the provider's own
        // UnavailableReason is what the UI shows.
        Assert.Null(selection.Fallback);
    }

    [Fact]
    public void An_empty_registry_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(
            () => AsrProviderSelector.Select(new AsrProviderRegistry(), "anything"));
    }

    [Fact]
    public void The_fallback_explanation_is_carried_as_parts_so_it_can_be_rendered_later()
    {
        var selection = AsrProviderSelector.Select(Registry(liveCaptionsAvailable: false), "windows-livecaptions");

        // Activation happens before the string dictionary is merged, so the selection must hand the
        // UI the pieces rather than a sentence baked in whatever language was installed at the time.
        Assert.NotNull(selection.Fallback);
        Assert.False(string.IsNullOrWhiteSpace(selection.Fallback!.UsedName));

        // Same contract as LocalizationManager.T(key, args): the translator formats.
        string rendered = selection.Fallback.Render(
            (key, args) => key == "Asr_FallbackNotice" ? string.Format("{0} unavailable ({1}), using {2}.", args) : key,
            "no Live Captions on this machine");

        Assert.Contains("unavailable", rendered);
        Assert.Contains(selection.Fallback.UsedName, rendered);
        Assert.Equal("iflytek-iat", selection.ProviderId);
    }
}
