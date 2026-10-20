using System.Runtime.CompilerServices;

namespace LiveCaptionsTranslator.Localization;

/// <summary>
/// Installs the localisation hooks before anything else in the assembly runs.
/// <para>
/// A module initializer runs when the assembly is loaded, which is earlier than App's constructor.
/// That matters: the constructor saves the settings, which touches <c>Translator</c>'s static
/// constructor, which activates an ASR provider, which already produces user-facing text. Doing
/// this in <c>OnStartup</c> was too late - the fallback notice was built with no translator
/// installed and showed the bare resource key.
/// </para>
/// </summary>
internal static class LocalizationBootstrap
{
    [ModuleInitializer]
    internal static void Install()
    {
        Core.Localization.CoreStrings.Use(LocalizationManager.T);
    }
}
