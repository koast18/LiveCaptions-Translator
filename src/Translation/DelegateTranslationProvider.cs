using LiveCaptionsTranslator.apis;

namespace LiveCaptionsTranslator.translation
{
    /// <summary>
    /// Adapts one of the legacy engines in <see cref="TranslateAPI.TRANSLATE_FUNCTIONS"/> onto the
    /// <see cref="ITranslationProvider"/> contract.
    /// <para>
    /// Those engines read their configuration straight from <see cref="Translator.Setting"/>, so
    /// only the text is taken from the request. The point of the adapter is that the host no
    /// longer needs to know the engine list, and that new engines can be added without touching
    /// the translation pipeline.
    /// </para>
    /// </summary>
    internal sealed class DelegateTranslationProvider : ITranslationProvider
    {
        private readonly Func<string, CancellationToken, Task<string>> translate;

        public DelegateTranslationProvider(
            string id,
            bool isLlmBased,
            bool requiresConfiguration,
            Func<string, CancellationToken, Task<string>> translate)
        {
            Id = id;
            IsLlmBased = isLlmBased;
            RequiresConfiguration = requiresConfiguration;
            this.translate = translate;
        }

        public string Id { get; }

        public string DisplayName => Id;

        public bool RequiresConfiguration { get; }

        public bool IsLlmBased { get; }

        public Task<string> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken = default) =>
            translate(request.Text, cancellationToken);
    }
}
