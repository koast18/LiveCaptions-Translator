using LiveCaptionsTranslator.Core.Localization;
namespace LiveCaptionsTranslator.Core.Asr
{
    /// <summary>Which backend to run, and whether the configured one had to be replaced.</summary>
    /// <param name="ProviderId">Id of the backend the host should start.</param>
    /// <param name="FellBack">True when <paramref name="ProviderId"/> is not what was asked for.</param>
    /// <param name="Fallback">Everything the UI needs to explain the switch. No rendered text is
    /// carried: activation happens before the string dictionary is merged, so anything rendered
    /// here would freeze the language - or worse, the bare resource key.</param>
    public sealed record AsrSelection(string ProviderId, bool FellBack, AsrFallbackNotice? Fallback = null);

    /// <summary>
    /// "X is unavailable (why), using Y instead", as identities rather than prose. The reason is
    /// re-read from the provider when the notice is rendered, which is after startup.
    /// </summary>
    public sealed record AsrFallbackNotice(string RequestedId, string RequestedName, string UsedName)
    {
        public string Render(Func<string, object?[], string> translate, string why) =>
            translate("Asr_FallbackNotice", new object?[] { RequestedName, why, UsedName });
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
                    return new AsrSelection(requestedId!, FellBack: false);

                // The reason is deliberately not read here: activation runs before the string
                // dictionary is merged, so it would come back as a bare resource key.

                foreach (string id in registry.Ids)
                {
                    if (id == requestedId)
                        continue;

                    var candidate = registry.Create(id);
                    if (!candidate.IsAvailable)
                        continue;

                    return new AsrSelection(
                        id,
                        FellBack: true,
                        Fallback: new AsrFallbackNotice(requested.Id, requested.DisplayName, candidate.DisplayName));
                }

                return new AsrSelection(requestedId!, FellBack: false);
            }

            foreach (string id in registry.Ids)
            {
                var candidate = registry.Create(id);
                if (candidate.IsAvailable)
                    return new AsrSelection(id, FellBack: false);
            }

            return new AsrSelection(fallbackId, FellBack: false);
        }
    }
}
