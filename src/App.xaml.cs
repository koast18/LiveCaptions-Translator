using System.Windows;

using Microsoft.Win32;

using Wpf.Ui.Appearance;

namespace LiveCaptionsTranslator
{
    public partial class App : Application
    {
        App()
        {
            // The theme has to be resolved before the first window is created: a window built with
            // the wrong dictionary keeps the wrong brushes for its whole lifetime.
            ApplyTheme();

            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            Translator.Setting?.Save();

            Task.Run(Translator.StartAsync);
            Task.Run(() => Translator.TranslateLoop());
            Task.Run(() => Translator.DisplayLoop());
        }

        /// <summary>
        /// Follows the Windows light/dark setting. The registry is read directly instead of relying
        /// on the toolkit's own detection, which has been unreliable on Windows 10.
        /// </summary>
        private static void ApplyTheme()
        {
            try
            {
                ApplicationThemeManager.Apply(GetSystemTheme());
            }
            catch (Exception)
            {
                // A theme failure must never abort startup.
                ApplicationThemeManager.Apply(ApplicationTheme.Dark);
            }
        }

        private static ApplicationTheme GetSystemTheme()
        {
            try
            {
                using RegistryKey? personalize = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

                object? value = personalize?.GetValue("AppsUseLightTheme");

                // No value at all (older Windows) means the dark default this app ships with.
                return value is int light && light != 0
                    ? ApplicationTheme.Light
                    : ApplicationTheme.Dark;
            }
            catch (Exception)
            {
                return ApplicationTheme.Dark;
            }
        }

        private static void OnProcessExit(object sender, EventArgs e)
        {
            Translator.Shutdown();
        }
    }
}
