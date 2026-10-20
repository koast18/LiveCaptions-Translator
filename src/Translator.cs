using System.Diagnostics;
using System.IO;
using System.Text;

using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.translation;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public static class Translator
    {
        private static Caption? caption = null;
        private static Setting? setting = null;

        private static readonly Queue<string> pendingTextQueue = new();
        private static readonly TranslationTaskQueue translationTaskQueue = new();

        private static int idleCount = 0;
        private static int syncCount = 0;

        /// <summary>Every ASR backend this build knows about.</summary>
        public static AsrProviderRegistry AsrProviders { get; } = new();

        /// <summary>Every translation engine this build knows about.</summary>
        public static TranslationProviderRegistry TranslationProviders { get; } = new();

        /// <summary>Every audio input this build knows about.</summary>
        public static AudioSourceRegistry AudioSources { get; } = new();

        /// <summary>The backend currently feeding captions into the translation pipeline.</summary>
        public static IAsrProvider? ActiveAsrProvider { get; private set; }

        /// <summary>The Live Captions provider when it is the active backend, otherwise <c>null</c>.</summary>
        public static WindowsLiveCaptionsProvider? LiveCaptions { get; private set; }

        public static Caption? Caption => caption;
        public static Setting? Setting => setting;

        public static bool LogOnlyFlag { get; set; } = false;
        public static bool FirstUseFlag { get; set; } = false;

        public static event Action? TranslationLogged;

        static Translator()
        {
            if (!File.Exists(Path.Combine(Directory.GetCurrentDirectory(), models.Setting.FILENAME)))
                FirstUseFlag = true;

            caption = Caption.GetInstance();
            setting = Setting.Load();

            ProviderCatalog.RegisterAll(AsrProviders, () => setting?.DoubaoCredentials);
            TranslationProviderCatalog.RegisterAll(TranslationProviders);
            AudioSourceCatalog.RegisterAll(AudioSources);

            ActivateProvider(setting.AsrProviderName);
        }

        /// <summary>
        /// The language the active ASR provider is really recognising, or <c>null</c> when the
        /// provider decides for itself. Used to decide whether a sentence needs translating.
        /// </summary>
        public static string? SourceLanguageHint =>
            ActiveAsrProvider?.LanguageIsHostControlled == true ? setting?.AsrLanguage : null;

        /// <summary>Starts the active ASR provider; captions then flow through <see cref="HandleCaptionText"/>.</summary>
        public static Task StartAsync() => StartActiveProviderAsync();

        /// <summary>
        /// Switches the ASR backend and restarts the caption pipeline on it. Providers that
        /// recognise audio themselves get the configured <see cref="IAudioSource"/> attached.
        /// </summary>
        public static async Task SwitchAsrProviderAsync(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId) || !AsrProviders.IsRegistered(providerId))
                return;

            // Always stop and re-create, even for the same id: the factory reads the current
            // settings, so newly registered credentials (or a changed language) take effect.
            await StopActiveProviderAsync();
            ActivateProvider(providerId);
            await StartActiveProviderAsync();
        }

        /// <summary>
        /// Re-creates the active provider, e.g. after its language or credentials changed. Re-creation
        /// matters: providers capture their configuration when they are constructed, so restarting
        /// the same instance would keep the stale credentials.
        /// </summary>
        public static async Task RestartAsrProviderAsync()
        {
            string? current = ActiveAsrProvider?.Id ?? setting?.AsrProviderName;

            await StopActiveProviderAsync();

            if (current is not null)
                ActivateProvider(current);

            await StartActiveProviderAsync();
        }

        /// <summary>
        /// Explains why a different backend than the configured one is running, if that happened
        /// (typically: Windows Live Captions is absent, so the app fell back to iFlytek).
        /// </summary>
        public static string? AsrFallbackNotice { get; private set; }

        /// <summary>Creates the provider instance for <paramref name="providerId"/>.</summary>
        private static void ActivateProvider(string? providerId)
        {
            var selection = AsrProviderSelector.Select(AsrProviders, providerId);

            AsrFallbackNotice = selection.FellBack ? selection.Reason : null;

            ActiveAsrProvider = AsrProviders.Create(selection.ProviderId);
            LiveCaptions = ActiveAsrProvider as WindowsLiveCaptionsProvider;

            if (setting != null)
                setting.AsrProviderName = selection.ProviderId;
        }

        private static async Task StartActiveProviderAsync()
        {
            var provider = ActiveAsrProvider;
            if (provider == null)
                return;

            IAudioSource? audioSource = null;
            if (provider.RequiresAudioSource)
            {
                string sourceId = setting?.AudioSourceName ?? AudioSourceCatalog.LoopbackId;
                if (!AudioSources.TryCreate(sourceId, out audioSource))
                    AudioSources.TryCreate(AudioSourceCatalog.LoopbackId, out audioSource);
            }

            try
            {
                await provider.StartAsync(new AsrContext
                {
                    Sink = new CaptionSink(),
                    Options = new AsrOptions
                    {
                        LanguageId = setting?.AsrLanguage ?? IflytekBusiness.Default.Language!,
                        Accent = setting?.AsrAccent,
                        Settings = setting?.AsrProviderSettings ?? new Dictionary<string, string>(),
                    },
                    AudioSource = audioSource,
                });

                // Only surfaced once the replacement backend actually started.
                if (AsrFallbackNotice is { Length: > 0 } notice && Caption != null)
                    Caption.DisplayTranslatedCaption = $"[WARNING] {notice}";
            }
            catch (Exception exception)
            {
                if (Caption != null)
                    Caption.DisplayTranslatedCaption =
                        $"[ERROR] {provider.DisplayName} 启动失败：{exception.Message}";
            }
        }

        private static async Task StopActiveProviderAsync()
        {
            var provider = ActiveAsrProvider;
            if (provider == null)
                return;

            try
            {
                await provider.StopAsync();
            }
            catch (Exception)
            {
                // The provider is being replaced; a failing stop must not block the switch.
            }
        }

        /// <summary>Stops the active ASR provider. Used on application exit.</summary>
        public static void Shutdown()
        {
            try
            {
                ActiveAsrProvider?.StopAsync().GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // Nothing useful to do while the process is going away.
            }
        }

        /// <summary>Adapts provider callbacks onto the caption pipeline.</summary>
        private sealed class CaptionSink : IAsrSink
        {
            public void OnPartial(string text) => HandleCaptionText(text);

            public void OnFinal(string text) => HandleCaptionText(text);

            public void OnNotice(string message) => SetNotice(message);

            public void OnError(Exception error) => SetNotice($"[ERROR] {error.Message}");

            private static void SetNotice(string message)
            {
                if (Caption != null)
                    Caption.DisplayTranslatedCaption = message;
            }
        }

        /// <summary>
        /// Turns the raw caption text reported by the active ASR provider into the sentence
        /// that gets translated. This is the upstream <c>SyncLoop</c> body, unchanged.
        /// </summary>
        public static void HandleCaptionText(string fullText)
        {
            if (string.IsNullOrEmpty(fullText))
                return;

            // Preprocess
            fullText = RegexPatterns.Acronym().Replace(fullText, "$1$2");
            fullText = RegexPatterns.AcronymWithWords().Replace(fullText, "$1 $2");
            fullText = RegexPatterns.PunctuationSpace().Replace(fullText, "$1 ");
            fullText = RegexPatterns.CJPunctuationSpace().Replace(fullText, "$1");
            // Note: For certain languages (such as Japanese), LiveCaptions excessively uses `\n`.
            // Replace redundant `\n` within sentences with comma or period.
            fullText = TextUtil.ReplaceNewlines(fullText, TextUtil.MEDIUM_THRESHOLD);

            // Prevent adding the last sentence from previous running to log cards
            // before the first sentence is completed.
            if (fullText.IndexOfAny(TextUtil.PUNC_EOS) == -1 && Caption!.Contexts.Count > 0)
                ClearContexts();

            // Get the last sentence.
            int lastEOSIndex;
            if (Array.IndexOf(TextUtil.PUNC_EOS, fullText[^1]) != -1)
                lastEOSIndex = fullText[0..^1].LastIndexOfAny(TextUtil.PUNC_EOS);
            else
                lastEOSIndex = fullText.LastIndexOfAny(TextUtil.PUNC_EOS);
            string latestCaption = fullText.Substring(lastEOSIndex + 1);

            if (latestCaption.Length == 0)
                return;

            // If the last sentence is too short, extend it by adding the previous sentence.
            // Note: LiveCaptions may generate multiple characters including EOS at once.
            if (lastEOSIndex > 0 && Encoding.UTF8.GetByteCount(latestCaption) < TextUtil.SHORT_THRESHOLD)
            {
                lastEOSIndex = fullText[0..lastEOSIndex].LastIndexOfAny(TextUtil.PUNC_EOS);
                latestCaption = fullText.Substring(lastEOSIndex + 1);
            }

            // `OverlayOriginalCaption`: The sentence to be displayed on Overlay Window.
            Caption.OverlayOriginalCaption = latestCaption;
            for (int historyCount = Math.Min(Setting!.DisplaySentences, Caption.Contexts.Count);
                 historyCount > 0 && lastEOSIndex > 0;
                 historyCount--)
            {
                lastEOSIndex = fullText[0..lastEOSIndex].LastIndexOfAny(TextUtil.PUNC_EOS);
                Caption.OverlayOriginalCaption = fullText.Substring(lastEOSIndex + 1);
            }

            // `DisplayOriginalCaption`: The sentence to be displayed on Main Window.
            if (string.CompareOrdinal(Caption.DisplayOriginalCaption, latestCaption) != 0)
            {
                Caption.DisplayOriginalCaption = latestCaption;
                // If the last sentence is too long, truncate it when displayed.
                Caption.DisplayOriginalCaption =
                    TextUtil.ShortenDisplaySentence(Caption.DisplayOriginalCaption, TextUtil.VERYLONG_THRESHOLD);
            }

            // Prepare for `OriginalCaption`. If Expanded, only retain the complete sentence.
            int lastEOS = latestCaption.LastIndexOfAny(TextUtil.PUNC_EOS);
            if (lastEOS != -1)
                latestCaption = latestCaption.Substring(0, lastEOS + 1);
            // `OriginalCaption`: The sentence to be really translated.
            if (string.CompareOrdinal(Caption.OriginalCaption, latestCaption) != 0)
            {
                Caption.OriginalCaption = latestCaption;

                idleCount = 0;
                if (Array.IndexOf(TextUtil.PUNC_EOS, Caption.OriginalCaption[^1]) != -1)
                {
                    syncCount = 0;
                    pendingTextQueue.Enqueue(Caption.OriginalCaption);
                }
                else if (Encoding.UTF8.GetByteCount(Caption.OriginalCaption) >= TextUtil.SHORT_THRESHOLD)
                    syncCount++;
            }
            else
                idleCount++;

            // `TranslateFlag` determines whether this sentence should be translated.
            // When `OriginalCaption` remains unchanged, `idleCount` +1; when `OriginalCaption` changes, `MaxSyncInterval` +1.
            if (syncCount > Setting.MaxSyncInterval ||
                idleCount == Setting.MaxIdleInterval)
            {
                syncCount = 0;
                pendingTextQueue.Enqueue(Caption.OriginalCaption);
            }
        }

        public static async Task TranslateLoop()
        {
            while (true)
            {
                // Translate
                if (pendingTextQueue.Count > 0)
                {
                    var originalSnapshot = pendingTextQueue.Dequeue();

                    if (LogOnlyFlag)
                    {
                        bool isOverwrite = await IsOverwrite(originalSnapshot);
                        await LogOnly(originalSnapshot, isOverwrite);
                    }
                    else
                    {
                        translationTaskQueue.Enqueue(token => Task.Run(
                            () => Translate(originalSnapshot, token), token), originalSnapshot);
                    }
                }

                Thread.Sleep(40);
            }
        }

        public static async Task DisplayLoop()
        {
            while (true)
            {
                var (translatedText, isChoke) = translationTaskQueue.Output;

                if (LogOnlyFlag)
                {
                    Caption.TranslatedCaption = string.Empty;
                    Caption.DisplayTranslatedCaption = "[Paused]";
                    Caption.OverlayNoticePrefix = "[Paused]";
                    Caption.OverlayCurrentTranslation = string.Empty;
                }
                else if (!string.IsNullOrEmpty(RegexPatterns.NoticePrefix().Replace(
                             translatedText, string.Empty).Trim()) &&
                         string.CompareOrdinal(Caption.TranslatedCaption, translatedText) != 0)
                {
                    // Main page
                    Caption.TranslatedCaption = translatedText;

                    // Nothing to show twice when the translation is the source text.
                    Caption.TranslationRedundant = IsTranslationRedundant(
                        Caption.OriginalCaption, Caption.TranslatedCaption);
                    Caption.DisplayTranslatedCaption =
                        TextUtil.ShortenDisplaySentence(Caption.TranslatedCaption, TextUtil.VERYLONG_THRESHOLD);

                    // Overlay window
                    if (Caption.TranslatedCaption.Contains("[ERROR]") || Caption.TranslatedCaption.Contains("[WARNING]"))
                        Caption.OverlayCurrentTranslation = Caption.TranslatedCaption;
                    else
                    {
                        var match = RegexPatterns.NoticePrefixAndTranslation().Match(Caption.TranslatedCaption);
                        Caption.OverlayNoticePrefix = match.Groups[1].Value.Trim();
                        Caption.OverlayCurrentTranslation = match.Groups[2].Value.Trim();
                    }
                }

                // If the original sentence is a complete sentence, choke for better visual experience.
                if (isChoke)
                    Thread.Sleep(720);
                Thread.Sleep(40);
            }
        }

        public static async Task<(string, bool)> Translate(string text, CancellationToken token = default)
        {
            string translatedText;
            bool isChoke = Array.IndexOf(TextUtil.PUNC_EOS, text[^1]) != -1;

            try
            {
                // Chinese recognition is answered locally: no API call, Simplified output only.
                var plan = TranslationPolicy.Plan(text, SourceLanguageHint, Setting.TargetLanguage);
                if (!plan.Translate)
                    return (plan.Text, isChoke);

                var sw = Setting.MainWindow.LatencyShow ? Stopwatch.StartNew() : null;

                if (Setting.ContextAware && !TranslateAPI.IsLLMBased)
                {
                    translatedText = await TranslateAPI.TranslateFunction($"{Caption.AwareContextsCaption} 🔤 {text} 🔤", token);
                    translatedText = RegexPatterns.TargetSentence().Match(translatedText).Groups[1].Value;
                }
                else
                {
                    translatedText = await TranslateAPI.TranslateFunction(text, token);
                    translatedText = translatedText.Replace("🔤", "");
                }

                if (sw != null)
                {
                    sw.Stop();
                    translatedText = $"[{sw.ElapsedMilliseconds,4} ms] " + translatedText;
                }
            }
            catch (OperationCanceledException ex)
            {
                throw;
            }
            catch (Exception ex)
            {
                return ($"[ERROR] Translation Failed: {ex.Message}", isChoke);
            }

            return (translatedText, isChoke);
        }

        public static async Task Log(string originalText, string translatedText,
            bool isOverwrite = false, CancellationToken token = default)
        {
            string targetLanguage, apiName;
            if (Setting != null)
            {
                targetLanguage = Setting.TargetLanguage;
                apiName = Setting.ApiName;
            }
            else
            {
                targetLanguage = "N/A";
                apiName = "N/A";
            }

            try
            {
                if (isOverwrite)
                    await SQLiteHistoryLogger.DeleteLastTranslation(token);
                await SQLiteHistoryLogger.LogTranslation(originalText, translatedText, targetLanguage, apiName);
                TranslationLogged?.Invoke();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] Logging history failed.", ex.Message, SnackbarType.Error,
                    timeout: 2, closeButton: true);
            }
        }

        public static async Task LogOnly(string originalText,
            bool isOverwrite = false, CancellationToken token = default)
        {
            try
            {
                if (isOverwrite)
                    await SQLiteHistoryLogger.DeleteLastTranslation(token);
                await SQLiteHistoryLogger.LogTranslation(originalText, "N/A", "N/A", "LogOnly");
                TranslationLogged?.Invoke();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] Logging history failed.", ex.Message, SnackbarType.Error,
                    timeout: 2, closeButton: true);
            }
        }

        public static async Task AddContexts(CancellationToken token = default)
        {
            var lastLog = await SQLiteHistoryLogger.LoadLastTranslation(token);
            if (lastLog == null)
                return;

            if (Caption?.Contexts.Count >= Caption.MAX_CONTEXTS)
                Caption.Contexts.Dequeue();
            Caption?.Contexts.Enqueue(lastLog);

            Caption?.OnPropertyChanged("DisplayLogCards");
            Caption?.OnPropertyChanged("OverlayPreviousTranslation");
        }

        /// <summary>
        /// True when the "translation" carries nothing new, i.e. the source was already Simplified
        /// Chinese and the policy skipped the API. Trailing punctuation and the notice prefixes are
        /// ignored so "今天天气不错。" and "今天天气不错" still count as the same line.
        /// </summary>
        internal static bool IsTranslationRedundant(string? originalCaption, string? translatedCaption)
        {
            string original = NormalizeForComparison(originalCaption);
            return original.Length > 0 && original == NormalizeForComparison(translatedCaption);
        }

        private static string NormalizeForComparison(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            string stripped = RegexPatterns.NoticePrefix().Replace(text!, string.Empty);
            return stripped.Trim().TrimEnd('.', '。', '!', '！', '?', '？', ' ', '	');
        }

        public static void ClearContexts()
        {
            Caption?.Contexts.Clear();

            Caption?.OnPropertyChanged("DisplayLogCards");
            Caption?.OnPropertyChanged("OverlayPreviousTranslation");
        }

        // If this text is too similar to the last one, overwrite it when logging.
        public static async Task<bool> IsOverwrite(string originalText, CancellationToken token = default)
        {
            string lastOriginalText = await SQLiteHistoryLogger.LoadLastSourceText(token);
            if (lastOriginalText == null)
                return false;

            int minLen = Math.Min(originalText.Length, lastOriginalText.Length);
            originalText = originalText.Substring(0, minLen);
            lastOriginalText = lastOriginalText.Substring(0, minLen);

            double similarity = TextUtil.Similarity(originalText, lastOriginalText);
            return similarity > TextUtil.SIM_THRESHOLD;
        }
    }
}
