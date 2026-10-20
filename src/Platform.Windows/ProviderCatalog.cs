using LiveCaptionsTranslator.Core.Asr;
using LiveCaptionsTranslator.Core.Doubao;
using LiveCaptionsTranslator.Core.Iflytek;

namespace LiveCaptionsTranslator.Platform.Windows
{
    /// <summary>
    /// Central place that knows which ASR backends ship with this build.
    /// Adding a backend means adding one registration here.
    /// </summary>
    public static class ProviderCatalog
    {
        /// <summary>Registers every built-in ASR provider on <paramref name="registry"/>.</summary>
        /// <param name="doubaoCredentials">
        /// The device_id/app_key pair registered for the Doubao provider, or a factory returning
        /// <c>null</c> when none exists yet - in which case the provider reports itself unavailable
        /// instead of breaking the picker.
        /// </param>
        public static void RegisterAll(
            AsrProviderRegistry registry,
            Func<DoubaoCredentials?>? doubaoCredentials = null)
        {
            ArgumentNullException.ThrowIfNull(registry);

            registry.Register(
                WindowsLiveCaptionsProvider.ProviderId,
                () => new WindowsLiveCaptionsProvider());

            registry.Register(
                IflytekIatProvider.ProviderId,
                () => new IflytekIatProvider());

            registry.Register(
                DoubaoAsrProvider.ProviderId,
                () => new DoubaoAsrProvider(doubaoCredentials?.Invoke()));
        }
    }
}
