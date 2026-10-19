namespace LiveCaptionsTranslator.Core.Translation
{
    /// <summary>One previously translated sentence, used as context for LLM-based engines.</summary>
    public sealed record TranslationTurn(string Source, string Translated);

    /// <summary>A single translation request.</summary>
    public sealed class TranslationRequest
    {
        public required string Text { get; init; }

        /// <summary>Target language as configured by the user, e.g. <c>zh-Hans</c>.</summary>
        public required string TargetLanguage { get; init; }

        /// <summary>Source language when known; <c>null</c> lets the engine decide.</summary>
        public string? SourceLanguage { get; init; }

        /// <summary>Recent turns, oldest first. Ignored by non-LLM engines.</summary>
        public IReadOnlyList<TranslationTurn> Context { get; init; } = Array.Empty<TranslationTurn>();

        /// <summary>Prompt template for LLM engines; <c>{0}</c> is replaced by the language.</summary>
        public string? PromptTemplate { get; init; }
    }

    /// <summary>A pluggable translation backend.</summary>
    public interface ITranslationProvider
    {
        /// <summary>Stable identifier, e.g. <c>Google</c>, <c>OpenAI</c>, <c>iflytek-mt</c>.</summary>
        string Id { get; }

        string DisplayName { get; }

        /// <summary>Whether the engine needs credentials before it can be used.</summary>
        bool RequiresConfiguration { get; }

        /// <summary>Whether the engine is an LLM and therefore benefits from context/prompts.</summary>
        bool IsLlmBased { get; }

        Task<string> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default);
    }
}
