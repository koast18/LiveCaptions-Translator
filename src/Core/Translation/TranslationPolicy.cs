using LiveCaptionsTranslator.Core.Text;

namespace LiveCaptionsTranslator.Core.Translation
{
    /// <summary>What the pipeline should do with a recognised sentence.</summary>
    /// <param name="Translate">Whether a translation provider should be called at all.</param>
    /// <param name="Text">
    /// The text to send to the provider when <paramref name="Translate"/> is <c>true</c>,
    /// otherwise the text to display as-is.
    /// </param>
    public sealed record TranslationPlan(bool Translate, string Text)
    {
        public static TranslationPlan Skip(string text) => new(false, text);

        public static TranslationPlan Call(string text) => new(true, text);
    }

    /// <summary>
    /// Decides whether a recognised sentence needs the translation API.
    /// <para>
    /// Two cases are answered locally, which is what keeps Chinese sessions from burning API
    /// quota and from having their wording rewritten by a machine translator:
    /// </para>
    /// <list type="bullet">
    /// <item>the recognised language is Chinese: the text is only normalised to Simplified;</item>
    /// <item>the target is Simplified Chinese and the text already is Simplified Chinese.</item>
    /// </list>
    /// </summary>
    public static class TranslationPolicy
    {
        public static TranslationPlan Plan(string text, string? sourceLanguageId, string? targetLanguage)
        {
            if (string.IsNullOrEmpty(text))
                return TranslationPlan.Skip(text ?? string.Empty);

            // Recognised as Chinese: never call the API, just make sure the output is Simplified.
            if (IsChinese(sourceLanguageId))
            {
                return TranslationPlan.Skip(
                    ChineseText.IsSimplified(text) ? text : ChineseText.ToSimplified(text));
            }

            // Target is Simplified Chinese and the text is already Simplified Chinese.
            if (IsSimplifiedChineseTarget(targetLanguage) && ChineseText.IsSimplified(text))
                return TranslationPlan.Skip(text);

            return TranslationPlan.Call(text);
        }

        /// <summary>True for any Chinese variant, e.g. <c>zh_cn</c>, <c>zh_tw</c>, <c>zh-Hans</c>.</summary>
        public static bool IsChinese(string? languageId) =>
            languageId is not null &&
            languageId.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

        public static bool IsSimplifiedChineseTarget(string? targetLanguage)
        {
            if (string.IsNullOrWhiteSpace(targetLanguage))
                return false;

            string normalized = targetLanguage.Replace('_', '-');

            return normalized.Equals("zh-Hans", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("zh-SG", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("zh", StringComparison.OrdinalIgnoreCase);
        }
    }
}
