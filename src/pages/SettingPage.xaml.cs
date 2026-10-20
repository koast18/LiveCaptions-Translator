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
            DataContext = Translator.Setting;

            Loaded += (s, e) =>
            {
                (App.Current.MainWindow as MainWindow)?.AutoHeightAdjust(maxHeight: (int)App.Current.MainWindow.MinHeight);
                CheckForFirstUse();
            };

            TranslateAPIBox.ItemsSource = Translator.TranslationProviders.Ids;
            TranslateAPIBox.SelectedIndex = 0;

            LoadAPISetting();
            LoadAsrSettings();
            LoadNetworkSettings();
            LoadLanguageSettings();
        }

        private bool loadingNetworkSettings;
        private bool loadingLanguageSettings;

        private void LoadLanguageSettings()
        {
            loadingLanguageSettings = true;
            try
            {
                UiLanguageBox.ItemsSource = LocalizationManager.Available;
                UiLanguageBox.SelectedItem = LocalizationManager.Available
                    .FirstOrDefault(option => option.Id == LocalizationManager.Normalize(Translator.Setting?.UiLanguage))
                    ?? LocalizationManager.Available[0];
            }
            finally
            {
                loadingLanguageSettings = false;
            }
        }

        private void UiLanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (loadingLanguageSettings || UiLanguageBox.SelectedItem is not UiLanguage language)
                return;

            if (Translator.Setting is not null)
                Translator.Setting.UiLanguage = language.Id;

            // No restart: replacing the merged dictionary re-resolves every DynamicResource.
            LocalizationManager.Apply(language.Id);
            RefreshLocalisedText();
        }

        private void LoadNetworkSettings()
        {
            loadingNetworkSettings = true;
            try
            {
                ProxyBox.Text = Translator.Setting?.HttpProxy ?? string.Empty;
                ProxyHintText.Text = ProxyHint(ProxyBox.Text);

                IflytekMtAppIdBox.Text = Translator.Setting?.IflytekMtAppId ?? string.Empty;
                IflytekMtApiKeyBox.Text = Translator.Setting?.IflytekMtApiKey ?? string.Empty;
                IflytekMtApiSecretBox.Text = Translator.Setting?.IflytekMtApiSecret ?? string.Empty;

                DoubaoDeviceIdBox.Text = Translator.Setting?.DoubaoDeviceId ?? string.Empty;
                DoubaoAppKeyBox.Text = Translator.Setting?.DoubaoAppKey ?? string.Empty;
                DoubaoVadBox.Text = (Translator.Setting?.DoubaoVadWindowMs ?? 600).ToString();

                BuildProxyTargets();
            }
            finally
            {
                loadingNetworkSettings = false;
            }
        }

        /// <summary>One checkbox per translation engine, so the user picks who goes through the proxy.</summary>
        private void BuildProxyTargets()
        {
            ProxyTargetsPanel.Children.Clear();

            foreach (string id in Translator.TranslationProviders.Ids)
            {
                var checkBox = new System.Windows.Controls.CheckBox
                {
                    Content = id,
                    Tag = id,
                    Margin = new Thickness(0, 0, 12, 0),
                    FontSize = 12,
                    IsChecked = Translator.Setting?.UsesProxy(id) ?? false,
                };

                checkBox.Checked += ProxyTarget_Changed;
                checkBox.Unchecked += ProxyTarget_Changed;
                ProxyTargetsPanel.Children.Add(checkBox);
            }
        }

        private void ProxyTarget_Changed(object sender, RoutedEventArgs e)
        {
            if (loadingNetworkSettings || Translator.Setting is null)
                return;

            Translator.Setting.ProxyApiNames = ProxyTargetsPanel.Children
                .OfType<System.Windows.Controls.CheckBox>()
                .Where(box => box.IsChecked == true && box.Tag is string)
                .Select(box => (string)box.Tag!)
                .ToList();
        }

        private void ProxyBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (Translator.Setting is null)
                return;

            string value = ProxyBox.Text.Trim();

            if (!HttpClients.TryValidateProxy(value, out string? error))
            {
                ProxyHintText.Text = error;
                return;
            }

            Translator.Setting.HttpProxy = value;
            ProxyHintText.Text = ProxyHint(value);
        }

        /// <summary>Both states of the proxy hint follow the UI language.</summary>
        private static string ProxyHint(string? proxy) =>
            string.IsNullOrWhiteSpace(proxy)
                ? LocalizationManager.T("Setting_ProxyFollowSystem")
                : LocalizationManager.T("Setting_ProxyEnabled", proxy.Trim());

        /// <summary>
        /// Re-renders the text this page assigns from code. {DynamicResource} handles the XAML side
        /// on its own, but these hints are plain strings, so they need a nudge.
        /// </summary>
        private void RefreshLocalisedText()
        {
            if (ProxyBox is not null)
                ProxyHintText.Text = ProxyHint(ProxyBox.Text);
            if (AsrProviderBox is not null)
                RefreshAsrHint();
            if (ButtonText is not null)
                ApplyLiveCaptionsButtonText();
        }

        private void IflytekMtApplyButton_click(object sender, RoutedEventArgs e)
        {
            if (Translator.Setting is null)
                return;

            Translator.Setting.IflytekMtAppId = IflytekMtAppIdBox.Text.Trim();
            Translator.Setting.IflytekMtApiKey = IflytekMtApiKeyBox.Text.Trim();
            Translator.Setting.IflytekMtApiSecret = IflytekMtApiSecretBox.Text.Trim();

            IflytekMtApplyButton.Content = new System.Windows.Controls.TextBlock { Text = "Saved" };
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

            DoubaoCredentialPanel.Visibility = provider?.Id == DoubaoAsrProvider.ProviderId
                ? Visibility.Visible
                : Visibility.Collapsed;

            RefreshAsrHint();
        }

        /// <summary>Rebuilds the hint below the speech controls from the current provider.</summary>
        private void RefreshAsrHint()
        {
            var provider = CreateSelectedProvider();

            string hint = provider switch
            {
                null => string.Empty,
                { Id: WindowsLiveCaptionsProvider.ProviderId } =>
                    LocalizationManager.T("Setting_AsrHintLiveCaptions"),
                { Id: IflytekIatProvider.ProviderId } =>
                    LocalizationManager.T("Setting_AsrHintIflytek"),
                { Id: DoubaoAsrProvider.ProviderId } =>
                    LocalizationManager.T("Setting_AsrHintDoubao"),
                _ => string.Empty,
            };

            if (provider is { IsAvailable: false })
                hint = "\u26a0 " + provider.UnavailableReason + "\n" + hint;

            if (Translator.AsrFallbackNotice is { Length: > 0 } fallback)
                hint = fallback + "\n" + hint;

            AsrHintText.Text = hint;
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

        private async void DoubaoRegisterButton_click(object sender, RoutedEventArgs e)
        {
            if (Translator.Setting is null)
                return;

            DoubaoRegisterButton.IsEnabled = false;

            try
            {
                var registrar = new DoubaoDeviceRegistrar();
                var credentials = await registrar.RegisterAsync();

                DoubaoDeviceIdBox.Text = credentials.DeviceId;
                DoubaoAppKeyBox.Text = credentials.AppKey;

                Translator.Setting.DoubaoDeviceId = credentials.DeviceId;
                Translator.Setting.DoubaoAppKey = credentials.AppKey;

                // Switch explicitly: the picker may still be sitting on the backend the selector
                // fell back to before these credentials existed.
                await Translator.SwitchAsrProviderAsync(DoubaoAsrProvider.ProviderId);

                AsrHintText.Text = LocalizationManager.T("Setting_DoubaoRegistered");
            }
            catch (Exception exception)
            {
                AsrHintText.Text = LocalizationManager.T("Setting_DoubaoRegisterFailed", exception.Message);
            }
            finally
            {
                DoubaoRegisterButton.IsEnabled = true;
            }
        }

        private async void DoubaoApplyButton_click(object sender, RoutedEventArgs e)
        {
            if (Translator.Setting is null)
                return;

            Translator.Setting.DoubaoDeviceId = DoubaoDeviceIdBox.Text.Trim();
            Translator.Setting.DoubaoAppKey = DoubaoAppKeyBox.Text.Trim();

            if (int.TryParse(DoubaoVadBox.Text.Trim(), out int gap) && gap is >= 100 and <= 5000)
                Translator.Setting.DoubaoVadWindowMs = gap;

            await Translator.SwitchAsrProviderAsync(DoubaoAsrProvider.ProviderId);

            DoubaoApplyButton.Content = new System.Windows.Controls.TextBlock { Text = "Saved" };
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

            bool isHidden = window.Current.BoundingRectangle == Rect.Empty;
            if (isHidden)
                LiveCaptionsHandler.RestoreLiveCaptions(window);
            else
                LiveCaptionsHandler.HideLiveCaptions(window);

            ApplyLiveCaptionsButtonText();
        }

        /// <summary>
        /// The button names the action it will perform, so its text has to be recomputed when the
        /// language changes - a {DynamicResource} binding is replaced the first time code sets it.
        /// </summary>
        private void ApplyLiveCaptionsButtonText()
        {
            var window = Translator.LiveCaptions?.Window;
            bool isHidden = window is null || window.Current.BoundingRectangle == Rect.Empty;
            ButtonText.Text = LocalizationManager.T(isHidden ? "Setting_Show" : "Setting_Hide");
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
                ApplyLiveCaptionsButtonText();
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