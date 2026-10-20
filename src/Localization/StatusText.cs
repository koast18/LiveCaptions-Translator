namespace LiveCaptionsTranslator.Localization;

/// <summary>
/// Recognises the lines the app generates itself (errors, warnings) as opposed to translated
/// speech. The "[ERROR]"/"[WARNING]" markers are part of the translated text now, so matching the
/// English literals alone would stop working the moment the UI is Chinese.
/// </summary>
public static class StatusText
{
    // Computed per call, not cached: the markers depend on the language in force, and a cached
    // array built while English was applied would stop matching after a switch to Chinese.
    public static bool IsError(string? text) =>
        ContainsAny(text, Markers("Error_TranslationFailed", "Status_Error", "[ERROR]"));

    public static bool IsWarning(string? text) =>
        ContainsAny(text, Markers("Status_Warning", "[WARNING]"));

    public static bool IsStatus(string? text) => IsError(text) || IsWarning(text);

    private static bool ContainsAny(string? text, string[] markers) =>
        text is not null && markers.Any(marker => text.Contains(marker, StringComparison.Ordinal));

    /// <summary>The part of a template before its placeholder, in every language we ship.</summary>
    private static string[] Markers(params string[] keys)
    {
        var markers = new List<string>();
        foreach (string key in keys)
        {
            // A literal, not a key.
            if (key.StartsWith('['))
            {
                markers.Add(key);
                continue;
            }

            string template = LocalizationManager.T(key);
            string prefix = template.Split("{0}")[0].Trim();
            if (prefix.Length > 0)
                markers.Add(prefix);
        }

        return markers.ToArray();
    }
}
