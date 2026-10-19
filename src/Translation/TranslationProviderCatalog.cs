using LiveCaptionsTranslator.apis;

namespace LiveCaptionsTranslator.translation
{
    /// <summary>
    /// The single place that knows which translation engines ship with this build.
    /// <para>
    /// Every engine from upstream is registered from <see cref="TranslateAPI.TRANSLATE_FUNCTIONS"/>,
    /// so nothing can be lost when the list changes, plus the iFlytek machine-translation provider.
    /// </para>
    /// </summary>
    public static class TranslationProviderCatalog
    {
        /// <summary>Engine that needs 开放平台 credentials; hidden until they are filled in.</summary>
        public const string IflytekMtId = IflytekMtProvider.ProviderId;

        public static void RegisterAll(TranslationProviderRegistry registry)
        {
            ArgumentNullException.ThrowIfNull(registry);

            foreach (var (id, translate) in TranslateAPI.TRANSLATE_FUNCTIONS)
            {
                string providerId = id;

                registry.Register(providerId, () => new DelegateTranslationProvider(
                    providerId,
                    TranslateAPI.LLM_BASED_APIS.Contains(providerId),
                    requiresConfiguration: !TranslateAPI.NO_CONFIG_APIS.Contains(providerId),
                    translate));
            }

            registry.Register(IflytekMtId, () => new IflytekMtProvider(
                IflytekMtOptions.FromSettings(Translator.Setting?.IflytekMtSettings),
                () => HttpClients.Resolve(
                    Translator.Setting?.UsesProxy(IflytekMtId) ?? false,
                    Translator.Setting?.HttpProxy,
                    TimeSpan.FromSeconds(15),
                    IflytekMtId)));
        }
    }
}
