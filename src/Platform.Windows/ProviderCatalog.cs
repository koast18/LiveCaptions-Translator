using LiveCaptionsTranslator.Core.Asr;
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
        public static void RegisterAll(AsrProviderRegistry registry)
        {
            ArgumentNullException.ThrowIfNull(registry);

            registry.Register(
                WindowsLiveCaptionsProvider.ProviderId,
                () => new WindowsLiveCaptionsProvider());

            registry.Register(
                IflytekIatProvider.ProviderId,
                () => new IflytekIatProvider());
        }
    }
}
