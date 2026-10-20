using System.IO;
using System.Windows;

namespace LiveCaptionsTranslator.Localization
{
    /// <summary>
    /// Swaps the UI language at runtime by replacing the merged string dictionary.
    /// <para>
    /// Text in XAML is bound with <c>{DynamicResource Key}</c>, so replacing the dictionary updates
    /// every open window immediately - no restart, and no <c>StaticResource</c> lookups to hunt down.
    /// </para>
    /// </summary>
    public static class LocalizationManager
    {
        public const string English = "en";

        public const string SimplifiedChinese = "zh-Hans";

        public const string Default = English;

        /// <summary>
        /// Path inside the assembly. The dictionaries live under src/ and the project sits at the
        /// repository root, so the compiled resource keeps that prefix - a Uri without it throws
        /// IOException at startup.
        /// </summary>
        private const string DictionaryPrefix = "src/Localization/Strings.";

        /// <summary>Languages offered in the settings page, in display order.</summary>
        public static IReadOnlyList<UiLanguage> Available { get; } = new[]
        {
            new UiLanguage(English, "English"),
            new UiLanguage(SimplifiedChinese, "简体中文"),
        };

        /// <summary>The language currently applied.</summary>
        public static string Current { get; private set; } = Default;

        /// <summary>Maps any value onto a language we actually ship, defaulting to English.</summary>
        public static string Normalize(string? language) =>
            Available.Any(option => string.Equals(option.Id, language, StringComparison.OrdinalIgnoreCase))
                ? Available.First(option => string.Equals(option.Id, language, StringComparison.OrdinalIgnoreCase)).Id
                : Default;

        /// <summary>Replaces the merged string dictionary. Safe to call before any window exists.</summary>
        public static void Apply(string? language)
        {
            string id = Normalize(language);
            Current = id;

            var dictionaries = Application.Current?.Resources.MergedDictionaries;
            if (dictionaries is null)
                return;

            for (int index = dictionaries.Count - 1; index >= 0; index--)
            {
                string? source = dictionaries[index].Source?.OriginalString;
                if (source is not null && source.Contains(DictionaryPrefix, StringComparison.OrdinalIgnoreCase))
                    dictionaries.RemoveAt(index);
            }

            try
            {
                dictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri(
                        $"pack://application:,,,/LiveCaptionsTranslator;component/{DictionaryPrefix}{id}.xaml",
                        UriKind.Absolute),
                });
            }
            catch (IOException) when (id != Default)
            {
                // A broken dictionary must never take the window down: keep English instead.
                Apply(Default);
            }
        }

        /// <summary>
        /// Looks a key up for code-behind messages. Falls back to the key itself, so a missing entry
        /// is visible instead of blank.
        /// </summary>
        public static string T(string key)
        {
            object? value = Application.Current?.TryFindResource(key);
            return value as string ?? key;
        }

        /// <summary>Formats a localised template with <see cref="string.Format(string, object?)"/>.</summary>
        public static string T(string key, params object?[] args)
        {
            string template = T(key);

            try
            {
                return string.Format(template, args);
            }
            catch (FormatException)
            {
                return template;
            }
        }
    }

    /// <summary>One selectable UI language.</summary>
    public sealed record UiLanguage(string Id, string DisplayName);
}
