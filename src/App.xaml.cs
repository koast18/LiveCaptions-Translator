using System.IO;
using System.Windows;
using System.Windows.Threading;

using Microsoft.Win32;

using LiveCaptionsTranslator.Localization;

using Wpf.Ui.Appearance;

namespace LiveCaptionsTranslator
{
    public partial class App : Application
    {
        App()
        {
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            Translator.Setting?.Save();

            Task.Run(Translator.StartAsync);
            Task.Run(() => Translator.TranslateLoop());
            Task.Run(() => Translator.DisplayLoop());
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            // Not in the constructor: App.xaml's resource dictionary is only merged by
            // InitializeComponent(), which runs after the constructor, so anything applied there
            // is thrown away again.
            ApplyTheme();
            LocalizationManager.Apply(Translator.Setting?.UiLanguage);

            base.OnStartup(e);

            // Report the *final* state, once the startup window exists and the dispatcher is idle.
            string? probePath = Environment.GetEnvironmentVariable("LCT_THEME_PROBE");
            if (!string.IsNullOrWhiteSpace(probePath))
            {
                Dispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    new Action(() => WriteThemeProbe(probePath)));
            }
        }

        /// <summary>
        /// Follows the Windows light/dark setting. The registry is read directly rather than relying
        /// on the toolkit's own detection, which resolves to the wrong value on Windows 10.
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

        /// <summary>
        /// Opt-in diagnostics: set <c>LCT_THEME_PROBE</c> to a file path and the app records the
        /// brushes its resource dictionary actually resolved to. The CI job uses this to prove that
        /// light and dark really differ.
        /// </summary>
        private void WriteThemeProbe(string path)
        {
            try
            {
                var lines = new List<string>
                {
                    $"AppsUseLightTheme={ReadAppsUseLightTheme()}",
                };

                foreach (string key in new[]
                         {
                             "ApplicationBackgroundBrush",
                             "ControlFillColorDefaultBrush",
                             "TextFillColorPrimaryBrush",
                         })
                {
                    object? brush = TryFindResource(key);
                    lines.Add($"{key}={brush?.ToString() ?? "<missing>"}");
                }

                File.WriteAllLines(path, lines);
            }
            catch (Exception)
            {
                // Diagnostics must never break the app.
            }
        }

        private static int ReadAppsUseLightTheme()
        {
            try
            {
                using RegistryKey? personalize = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

                return personalize?.GetValue("AppsUseLightTheme") is int value ? value : -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private static void OnProcessExit(object sender, EventArgs e)
        {
            Translator.Shutdown();
        }
    }
}
