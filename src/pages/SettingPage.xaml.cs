using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wpf.Ui.Appearance;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;
using Wpf.Ui.Controls;

namespace LiveCaptionsTranslator
{
    public partial class SettingPage : Page
    {
        private static SettingWindow? SettingWindow;

        public SettingPage()
        {
            InitializeComponent();
            ApplicationThemeManager.ApplySystemTheme();
            DataContext = Translator.Setting;

            Loaded += (s, e) =>
            {
                (App.Current.MainWindow as MainWindow)?.AutoHeightAdjust(maxHeight: (int)App.Current.MainWindow.MinHeight);
                CheckForFirstUse();
            };

            TranslateAPIBox.ItemsSource = Translator.Setting?.Configs.Keys;
            TranslateAPIBox.SelectedIndex = 0;

            LoadAPISetting();
            LoadAsrSettings();
        }

        private IReadOnlyList<AsrLanguage> asrLanguages = Array.Empty<AsrLanguage>();
        private bool loadingAsrSettings;

        private void LoadAsrSettings()
        {
            loadingAsrSettings = true;
            try
            {
                AsrProviderBox.ItemsSource = Translator.AsrProviders.Ids;
                AsrProviderBox.SelectedItem = Translator.ActiveAsrProvider?.Id
                    ?? WindowsLiveCaptionsProvider.ProviderId;

                AudioSourceBox.ItemsSource = AudioSourceCatalog.Ids;
                AudioSourceBox.SelectedItem = Translator.Setting?.AudioSourceName ?? AudioSourceCatalog.LoopbackId;

                IflytekApiKeyBox.Text = Translator.Setting?.IflytekApiKey ?? string.Empty;
                IflytekApiSecretBox.Text = Translator.Setting?.IflytekApiSecret ?? string.Empty;

                RefreshAsrLanguageBox();
            }
            finally
            {
                loadingAsrSettings = false;
            }
        }

        private void RefreshAsrLanguageBox()
        {
            var provider = CreateSelectedProvider();
            asrLanguages = provider?.SupportedLanguages ?? Array.Empty<AsrLanguage>();

            AsrLanguageBox.ItemsSource = asrLanguages
                .Select(language => language.Accent is null
                    ? language.DisplayName
                    : $"{language.DisplayName} ({language.Accent})")
                .ToList();

            string? languageId = Translator.Setting?.AsrLanguage;
            string? accent = Translator.Setting?.AsrAccent;
            int index = -1;

            for (int i = 0; i < asrLanguages.Count; i++)
            {
                if (asrLanguages[i].Id != languageId)
                    continue;

                if (accent is not null && asrLanguages[i].Accent is not null && asrLanguages[i].Accent != accent)
                    continue;

                index = i;
                break;
            }

            AsrLanguageBox.SelectedIndex = index >= 0 ? index : (asrLanguages.Count > 0 ? 0 : -1);

            bool needsAudio = provider?.RequiresAudioSource ?? false;
            AudioSourceBox.IsEnabled = needsAudio;
            AudioSourceBox.Opacity = needsAudio ? 1.0 : 0.5;

            IflytekCredentialPanel.Visibility = provider?.Id == IflytekIatProvider.ProviderId
                ? Visibility.Visible
                : Visibility.Collapsed;

            AsrHintText.Text = provider switch
            {
                null => string.Empty,
                { Id: WindowsLiveCaptionsProvider.ProviderId } =>
                    "Windows Live Captions does the capture and recognition; choose the source language inside Live Captions itself.",
                { Id: IflytekIatProvider.ProviderId } =>
                    "Streams the selected audio source to iFlytek. Only zh_cn has been exercised so far - the other languages are accepted by the endpoint but not verified against a real speech sample.",
                _ => string.Empty,
            };
        }

        private IAsrProvider? CreateSelectedProvider() =>
            AsrProviderBox.SelectedItem is string id && Translator.AsrProviders.TryCreate(id, out var provider)
                ? provider
                : null;

        private async void AsrProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (loadingAsrSettings || AsrProviderBox.SelectedItem is not string id)
                return;

            loadingAsrSettings = true;
            try
            {
                RefreshAsrLanguageBox();
            }
            finally
            {
                loadingAsrSettings = false;
            }

            await Translator.SwitchAsrProviderAsync(id);
        }

        private async void AsrLanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (loadingAsrSettings)
                return;

            int index = AsrLanguageBox.SelectedIndex;
            if (index < 0 || index >= asrLanguages.Count || Translator.Setting is null)
                return;

            Translator.Setting.AsrLanguage = asrLanguages[index].Id;
            Translator.Setting.AsrAccent = asrLanguages[index].Accent;

            await Translator.RestartAsrProviderAsync();
        }

        private async void AudioSourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (loadingAsrSettings || AudioSourceBox.SelectedItem is not string sourceId || Translator.Setting is null)
                return;

            Translator.Setting.AudioSourceName = sourceId;

            await Translator.RestartAsrProviderAsync();
        }

        private async void AsrApplyButton_click(object sender, RoutedEventArgs e)
        {
            if (Translator.Setting is null)
                return;

            Translator.Setting.IflytekApiKey = IflytekApiKeyBox.Text.Trim();
            Translator.Setting.IflytekApiSecret = IflytekApiSecretBox.Text.Trim();

            await Translator.RestartAsrProviderAsync();
        }

        private void LiveCaptionsButton_click(object sender, RoutedEventArgs e)
        {
            var window = Translator.LiveCaptions?.Window;
            if (window == null)
                return;

            var button = sender as Wpf.Ui.Controls.Button;
            var text = ButtonText.Text;

            bool isHide = window.Current.BoundingRectangle == Rect.Empty;
            if (isHide)
            {
                LiveCaptionsHandler.RestoreLiveCaptions(window);
                ButtonText.Text = "Hide";
            }
            else
            {
                LiveCaptionsHandler.HideLiveCaptions(window);
                ButtonText.Text = "Show";
            }
        }

        private void TranslateAPIBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            LoadAPISetting();
        }

        private void TargetLangBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TargetLangBox.SelectedItem != null)
                Translator.Setting.TargetLanguage = TargetLangBox.SelectedItem.ToString();
        }

        private void TargetLangBox_LostFocus(object sender, RoutedEventArgs e)
        {
            Translator.Setting.TargetLanguage = TargetLangBox.Text;
        }

        private void APISettingButton_click(object sender, RoutedEventArgs e)
        {
            if (SettingWindow != null && SettingWindow.IsLoaded)
                SettingWindow.Activate();
            else
            {
                SettingWindow = new SettingWindow();
                SettingWindow.Closed += (sender, args) => SettingWindow = null;
                SettingWindow.Show();
            }
        }

        private void Contexts_ValueChanged(object sender, NumberBoxValueChangedEventArgs args)
        {
            if (Translator.Setting.DisplaySentences > Translator.Setting.NumContexts)
                Translator.Setting.DisplaySentences = Translator.Setting.NumContexts;
        }

        private void DisplaySentences_ValueChanged(object sender, NumberBoxValueChangedEventArgs args)
        {
            if (Translator.Setting.DisplaySentences > Translator.Setting.NumContexts)
                Translator.Setting.NumContexts = Translator.Setting.DisplaySentences;
            Translator.Caption.OnPropertyChanged("DisplayLogCards");
            Translator.Caption.OnPropertyChanged("OverlayPreviousTranslation");
        }

        private void LiveCaptionsInfo_MouseEnter(object sender, MouseEventArgs e)
        {
            LiveCaptionsInfoFlyout.Show();
        }

        private void LiveCaptionsInfo_MouseLeave(object sender, MouseEventArgs e)
        {
            LiveCaptionsInfoFlyout.Hide();
        }

        private void FrequencyInfo_MouseEnter(object sender, MouseEventArgs e)
        {
            FrequencyInfoFlyout.Show();
        }

        private void FrequencyInfo_MouseLeave(object sender, MouseEventArgs e)
        {
            FrequencyInfoFlyout.Hide();
        }

        private void TranslateAPIInfo_MouseEnter(object sender, MouseEventArgs e)
        {
            TranslateAPIInfoFlyout.Show();
        }

        private void TranslateAPIInfo_MouseLeave(object sender, MouseEventArgs e)
        {
            TranslateAPIInfoFlyout.Hide();
        }

        private void TargetLangInfo_MouseEnter(object sender, MouseEventArgs e)
        {
            TargetLangInfoFlyout.Show();
        }

        private void TargetLangInfo_MouseLeave(object sender, MouseEventArgs e)
        {
            TargetLangInfoFlyout.Hide();
        }

        private void CaptionLogMaxInfo_MouseEnter(object sender, MouseEventArgs e)
        {
            CaptionLogMaxInfoFlyout.Show();
        }

        private void CaptionLogMaxInfo_MouseLeave(object sender, MouseEventArgs e)
        {
            CaptionLogMaxInfoFlyout.Hide();
        }

        private void ContextAwareInfo_MouseEnter(object sender, MouseEventArgs e)
        {
            ContextAwareInfoFlyout.Show();
        }

        private void ContextAwareInfo_MouseLeave(object sender, MouseEventArgs e)
        {
            ContextAwareInfoFlyout.Hide();
        }

        private void CheckForFirstUse()
        {
            if (Translator.FirstUseFlag)
                ButtonText.Text = "Hide";
        }

        public void LoadAPISetting()
        {
            var configType = Translator.Setting[Translator.Setting.ApiName].GetType();
            var languagesProp = configType.GetProperty(
                "SupportedLanguages", BindingFlags.Public | BindingFlags.Static);

            // Traverse base classes to find `SupportedLanguages`
            while (configType != null && languagesProp == null)
            {
                configType = configType.BaseType;
                languagesProp = configType.GetProperty(
                    "SupportedLanguages", BindingFlags.Public | BindingFlags.Static);
            }
            if (languagesProp == null)
                languagesProp = typeof(TranslateAPIConfig).GetProperty(
                    "SupportedLanguages", BindingFlags.Public | BindingFlags.Static);

            var supportedLanguages = (Dictionary<string, string>)languagesProp.GetValue(null);
            TargetLangBox.ItemsSource = supportedLanguages.Keys;

            string targetLang = Translator.Setting.TargetLanguage;
            if (!supportedLanguages.ContainsKey(targetLang))
                supportedLanguages[targetLang] = targetLang;    // add custom language to supported languages
            TargetLangBox.SelectedItem = targetLang;
        }
    }
}