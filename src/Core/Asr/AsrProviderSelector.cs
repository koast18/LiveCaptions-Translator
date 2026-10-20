using LiveCaptionsTranslator.Core.Localization;
namespace LiveCaptionsTranslator.Core.Asr
{
    /// <summary>Which backend to run, and whether the configured one had to be replaced.</summary>
    /// <param name="ProviderId">Id of the backend the host should start.</param>
    /// <param name="FellBack">True when <paramref name="ProviderId"/> is not what was asked for.</param>
    /// <param name="Reason">Explains the fallback, for the settings UI and the first caption.</param>
    /// <param name="Reason">Rendered text, kept for logs and tests.</param>
    /// <param name="Fallback">The parts the UI needs to render the explanation in the current
    /// language. Rendering eagerly would bake in whatever language was installed when the provider
    /// was activated - which happens before the string dictionary is merged.</param>
    public sealed record AsrSelection(
        string ProviderId,
        bool FellBack,
        string? Reason,
        AsrFallbackNotice? Fallback = null);

    /// <summary>The pieces of "X is unavailable (why), using Y instead".</summary>
    public sealed record AsrFallbackNotice(string Requested, string? Why, string Used)
    {
        public string Render(Func<string, object?[], string> translate) =>
            translate("Asr_FallbackNotice", new object?[] { Requested, Why ?? string.Empty, Used });
    }

    /// <summary>
    /// Chooses the speech-recognition backend to run.
    /// <para>
    /// The Windows Live Captions backend needs a component that only exists on Windows 11 22H2
    /// and later, so on anything older it has to step aside for a backend that can actually run
    /// (iFlytek, which captures audio itself). Keeping this decision in Core makes it testable
    /// without a particular Windows build.
    /// </para>
    /// </summary>
    public static class AsrProviderSelector
    {
        /// <summary>
        /// Returns the requested provider when it is registered and available, otherwise the first
        /// available one in registration order. When nothing is available the requested (or first
        /// registered) provider is returned anyway, so the host can still report a useful error.
        /// </summary>
        public static AsrSelection Select(AsrProviderRegistry registry, string? requestedId)
        {
            ArgumentNullException.ThrowIfNull(registry);

            if (registry.Count == 0)
                throw new InvalidOperationException("No ASR provider is registered.");

            string fallbackId = registry.Ids[0];

            if (!string.IsNullOrWhiteSpace(requestedId) && registry.IsRegistered(requestedId!))
            {
                fallbackId = requestedId!;

                var requested = registry.Create(requestedId!);
                if (requested.IsAvailable)
                    return new AsrSelection(requestedId!, FellBack: false, Reason: null);

                string? reason = requested.UnavailableReason;

                foreach (string id in registry.Ids)
                {
                    if (id == requestedId)
                        continue;

                    var candidate = registry.Create(id);
                    if (!candidate.IsAvailable)
                        continue;

                    var parts = new AsrFallbackNotice(requested.DisplayName, reason, candidate.DisplayName);

                    return new AsrSelection(
                        id,
                        FellBack: true,
                        Reason: CoreStrings.Get("Asr_FallbackNotice", requested.DisplayName, reason ?? string.Empty, candidate.DisplayName),
                        Fallback: parts);
                }

                return new AsrSelection(requestedId!, FellBack: false, Reason: reason);
            }

            foreach (string id in registry.Ids)
            {
                var candidate = registry.Create(id);
                if (candidate.IsAvailable)
                    return new AsrSelection(id, FellBack: false, Reason: null);
            }

            return new AsrSelection(fallbackId, FellBack: false, Reason: null);
        }
    }
}
