namespace LiveCaptionsTranslator.Core.Localization;

/// <summary>
/// Lets Core talk to the user without depending on WPF. The host installs the real translator at
/// startup; when nobody has installed one - unit tests, a headless harness - <see cref="Get"/>
/// returns the key itself so a missing translation is visible rather than silent.
/// </summary>
public static class CoreStrings
{
    private static Func<string, string> translate = key => key;

    public static void Use(Func<string, string> translator) => translate = translator;

    public static string Get(string key) => translate(key);

    public static string Get(string key, params object?[] args)
    {
        string template = translate(key);
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
