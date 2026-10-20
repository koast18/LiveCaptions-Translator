using System.IO;
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
            ApplicationTheme theme;
            int registryValue;

            try
            {
                (theme, registryValue) = GetSystemTheme();
                ApplicationThemeManager.Apply(theme);
            }
            catch (Exception)
            {
                // A theme failure must never abort startup.
                theme = ApplicationTheme.Dark;
                registryValue = -1;
                ApplicationThemeManager.Apply(ApplicationTheme.Dark);
            }

            WriteThemeProbe(theme, registryValue);
        }

        /// <summary>
        /// Opt-in diagnostics: set <c>LCT_THEME_PROBE</c> to a file path and the app records which
        /// theme it applied plus the brushes the resource dictionary actually resolved to.
        /// </summary>
        private static void WriteThemeProbe(ApplicationTheme theme, int registryValue)
        {
            string? path = Environment.GetEnvironmentVariable("LCT_THEME_PROBE");
            if (string.IsNullOrWhiteSpace(path))
                return;

            try
            {
                var lines = new List<string>
                {
                    $"theme={theme}",
                    $"AppsUseLightTheme={registryValue}",
                };

                foreach (string key in new[]
                         {
                             "ApplicationBackgroundBrush",
                             "ControlFillColorDefaultBrush",
                             "TextFillColorPrimaryBrush",
                         })
                {
                    object? brush = Current?.TryFindResource(key);
                    lines.Add($"{key}={brush?.ToString() ?? "<missing>"}");
                }

                File.WriteAllLines(path, lines);
            }
            catch (Exception)
            {
                // Diagnostics must never break the app.
            }
        }

        private static (ApplicationTheme Theme, int RegistryValue) GetSystemTheme()
        {
            try
            {
                using RegistryKey? personalize = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

                object? value = personalize?.GetValue("AppsUseLightTheme");
                int raw = value is int number ? number : -1;

                // No value at all (older Windows) means the dark default this app ships with.
                return (raw == 1 ? ApplicationTheme.Light : ApplicationTheme.Dark, raw);
            }
            catch (Exception)
            {
                return (ApplicationTheme.Dark, -1);
            }
        }

        private static void OnProcessExit(object sender, EventArgs e)
        {
            Translator.Shutdown();
        }
    }
}
