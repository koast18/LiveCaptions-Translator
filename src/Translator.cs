using System.Diagnostics;
using System.IO;
using System.Text;

using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.translation;
using LiveCaptionsTranslator.utils;
using LiveCaptionsTranslator.Core.Asr;
using LiveCaptionsTranslator.Localization;

namespace LiveCaptionsTranslator
{
    public static class Translator
    {
        private static Caption? caption = null;
        private static Setting? setting = null;

        /// <summary>Written by the recogniser thread, read by the translate loop: guarded.</summary>
        private static readonly Queue<string> pendingTextQueue = new();
        private static readonly object pendingLock = new();

        /// <summary>
        /// Serialises everything that owns the finished line. The recogniser commits on its own thread
        /// while the display loop applies translations, and an interleaving would otherwise put a
        /// translation on the wrong line (or rebuild the list from an older snapshot).
        /// </summary>
        private static readonly object displayLock = new();

        /// <summary>Decides when a sentence is finished; it remembers what it has already consumed.</summary>
        private static readonly Core.Text.LiveTranscript transcript = new();
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


        private static Func<string>? captionStatus;
        private static Func<string>? overlayStatus;

        static Translator()
        {
            LocalizationManager.LanguageChanged += RefreshStatusText;

            if (!File.Exists(Path.Combine(Directory.GetCurrentDirectory(), models.Setting.FILENAME)))
                FirstUseFlag = true;

            caption = Caption.GetInstance();
            setting = Setting.Load();

            ProviderCatalog.RegisterAll(AsrProviders, () => setting?.DoubaoCredentials, LocalizationManager.T);
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
        public static string? AsrFallbackNotice => RenderAsrFallbackNotice();

        /// <summary>
        /// Rendered on demand rather than when the provider is activated: activation happens before
        /// App.InitializeComponent merges the dictionaries, so an eagerly built string would show the
        /// bare resource key.
        /// </summary>
        public static string? RenderAsrFallbackNotice()
        {
            if (asrFallback is null)
                return null;

            string why = AsrProviders.TryCreate(asrFallback.RequestedId, out var requested)
                ? requested.UnavailableReason ?? string.Empty
                : string.Empty;

            return asrFallback.Render((key, args) => LocalizationManager.T(key, args), why);
        }

        private static AsrFallbackNotice? asrFallback;

        /// <summary>
        /// Shows text the app generates rather than text the user's audio produced, and remembers how
        /// to rebuild it: switching language has to re-render it, otherwise the caption keeps the
        /// previous language until the next status event.
        /// </summary>
        private static void SetCaptionStatus(Func<string> caption, Func<string>? overlay = null)
        {
            captionStatus = caption;
            overlayStatus = overlay;

            if (Caption is null)
                return;

            Caption.Notice = caption();
            if (overlay is not null)
                Caption.OverlayNoticePrefix = overlay();
        }

        private static void RefreshStatusText()
        {
            if (Caption is null)
                return;

            if (captionStatus is not null)
                Caption.Notice = captionStatus();
            if (overlayStatus is not null)
                Caption.OverlayNoticePrefix = overlayStatus();
        }

        /// <summary>Creates the provider instance for <paramref name="providerId"/>.</summary>
        private static void ActivateProvider(string? providerId)
        {
            var selection = AsrProviderSelector.Select(AsrProviders, providerId);

            asrFallback = selection.FellBack ? selection.Fallback : null;

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

            // A new backend starts a new transcript: text from the previous one must not be treated
            // as a replay of it, and a half-heard line from it must not stay on screen.
            ResetTranscription();

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
                    Localize = (key, args) => LocalizationManager.T(key, args),
                });

                // Only surfaced when a different backend was actually substituted - this used to run
                // unconditionally, which painted a bare "[WARNING]" over every start.
                if (asrFallback is not null && Caption is not null)
                    SetCaptionStatus(() => LocalizationManager.T("Status_Warning", RenderAsrFallbackNotice() ?? string.Empty));
            }
            catch (Exception exception)
            {
                SetCaptionStatus(() => LocalizationManager.T("Error_ProviderStartFailed", provider.DisplayName, exception.Message));
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
            // Interim results are shown straight away - no translation round trip, no waiting for the
            // segment to be final - and still feed the sync loop, because some backends only emit a
            // final when the whole stream ends.
            // One path only: HandleCaptionText owns the live line, so the page cannot be written by two
            // different pieces of logic at once.
            public void OnPartial(string text, string? stableText = null) =>
                HandleCaptionText(text, isFinal: false, stableText: stableText);

            public void OnFinal(string text, string? stableText = null)
            {
                Caption!.InterimText = null;
                HandleCaptionText(text, isFinal: true, stableText: stableText);
            }

            public void OnNotice(string message) => SetNotice(message);

            public void OnError(Exception error) => SetNotice(LocalizationManager.T("Status_Error", error.Message));

            // A notice is not a caption: it goes to its own slot so it can never be committed to the
            // rolling history, and never overwrites a sentence that is on screen.
            private static void SetNotice(string message)
            {
                if (Caption != null)
                    Caption.Notice = message;
            }
        }


        /// <summary>CJK text takes a full-width full stop; anything else takes an ASCII one.</summary>
        private static bool EndsInCjk(string text)
        {
            foreach (char c in text)
            {
                if (char.IsLetter(c))
                    return c >= '\u4e00' && c <= '\u9fff';
            }

            return true;
        }



        /// <summary>
        /// Turns the raw caption text reported by the active ASR provider into the sentence
        /// that gets translated. This is the upstream <c>SyncLoop</c> body, unchanged.
        /// </summary>
        /// <param name="isFinal">
        /// True for a segment the recogniser will not change again. Such a segment is a sentence even
        /// when the service did not punctuate it, and without a closing mark the splitter would glue
        /// it to the next one - the "several sentences run together" complaint.
        /// </param>
        public static void HandleCaptionText(string fullText, bool isFinal = false, string? stableText = null)
        {
            if (string.IsNullOrEmpty(fullText))
                return;

            fullText = PreprocessCaptionText(fullText);
            string? stable = stableText is null ? null : PreprocessCaptionText(stableText);

            lock (displayLock)
            {
                // The recogniser reports the whole transcript on every callback, so the splitter works
                // on what is new. It decides when a sentence is finished; nothing here re-slices the
                // full text, which is what used to commit the same sentence again and again.
                var updates = transcript.Accept(fullText, stable, isFinal);

                foreach (string sentence in updates)
                {
                    CommitLine(sentence);

                    lock (pendingLock)
                        pendingTextQueue.Enqueue(sentence);
                }

                // The live line is written here too, so the page never sees the live slot newer than
                // the list it belongs to.
                string live = transcript.Pending;
                Caption.InterimText = live.Length > 0 ? live : null;

                Caption.RebuildRollingLines(Setting?.NumContexts ?? 0);
            }
        }

        /// <summary>
        /// The upstream clean-up rules, applied to both the transcript and its stable prefix so the
        /// stable offsets stay valid.
        /// </summary>
        private static string PreprocessCaptionText(string text)
        {
            text = RegexPatterns.Acronym().Replace(text, "$1$2");
            text = RegexPatterns.AcronymWithWords().Replace(text, "$1 $2");
            text = RegexPatterns.PunctuationSpace().Replace(text, "$1 ");
            text = RegexPatterns.CJPunctuationSpace().Replace(text, "$1");
            return TextUtil.ReplaceNewlines(text, TextUtil.MEDIUM_THRESHOLD);
        }

        /// <summary>
        /// Shows a finished sentence at once and hands it to the translator. The recognised text is
        /// what the user sees while the translation is in flight; when it arrives it replaces this same
        /// line. That is what keeps the subtitles continuous instead of blank between two round trips.
        /// </summary>
        private static void CommitLine(string source)
        {
            if (string.IsNullOrWhiteSpace(source))
                return;

            // The sentence that was on screen becomes history, exactly once.
            if (!string.IsNullOrWhiteSpace(Caption.DisplayTranslatedCaption))
                Caption.AddRecentLine(Caption.DisplayTranslatedCaption, Caption.OriginalCaption);

            Caption.OriginalCaption = source;
            Caption.TranslatedCaption = source;
            Caption.DisplayTranslatedCaption = source;

            // Until a translation arrives the line is the source text, so the overlay must not show
            // it a second time as the "translation".
            Caption.TranslationRedundant = true;

            Caption.OverlayOriginalCaption = source;
            Caption.OverlayCurrentTranslation = source;
            Caption.OverlayNoticePrefix = string.Empty;

            // Captions are flowing again: whatever the app reported last is no longer current.
            captionStatus = null;
            overlayStatus = null;
            Caption.Notice = string.Empty;
        }

        /// <summary>
        /// Forgets what has been transcribed so far. Called when the backend or its language changes,
        /// and when the user clears the captions.
        /// </summary>
        public static void ResetTranscription()
        {
            lock (displayLock)
            {
                transcript.Reset();

                lock (pendingLock)
                    pendingTextQueue.Clear();

                if (Caption is not null)
                    Caption.InterimText = null;
            }
        }

        /// <summary>Kept deliberately tiny: the loops only need somewhere to report a broken frame.</summary>
        private static void LogError(string message)
        {
            try { Console.Error.WriteLine(message); }
            catch (Exception) { /* nowhere to report it to */ }
        }

        public static async Task TranslateLoop()
        {
            while (true)
            {
                try
                {
                    await TranslateOnce();
                }
                catch (Exception exception)
                {
                    LogError($"[ERROR] translate: {exception.Message}");
                    await Task.Delay(200);
                }
            }
        }

        private static async Task TranslateOnce()
        {
            string originalText;
            lock (pendingLock)
                originalText = pendingTextQueue.Count == 0 ? string.Empty : pendingTextQueue.Dequeue();

            if (originalText.Length > 0)
            {
                if (LogOnlyFlag)
                {
                    // Recognition still runs and is logged; only the translation is paused.
                    SetCaptionStatus(
                        () => LocalizationManager.T("Caption_Paused"),
                        () => LocalizationManager.T("Caption_Paused"));

                    bool isOverwrite = await IsOverwrite(originalText);
                    await LogOnly(originalText, isOverwrite);
                }
                else
                {
                    translationTaskQueue.Enqueue(
                        token => Task.Run(() => Translate(originalText, token), token),
                        originalText);
                }
            }

            // Short enough that a finished sentence never waits on this loop in any visible way.
            await Task.Delay(10);
        }

        public static async Task DisplayLoop()
        {
            while (true)
            {
                try
                {
                    await DisplayOnce();
                }
                catch (Exception exception)
                {
                    // One bad frame must not stop captions for the rest of the session: before this,
                    // a single exception took the loop down and the page stayed blank while the
                    // history, written on another path, kept filling.
                    LogError($"[ERROR] display: {exception.Message}");
                    await Task.Delay(200);
                }
            }
        }

        private static async Task DisplayOnce()
        {
            // Wait for a result instead of spinning: the queue releases this the moment a translation
            // completes. Every completed result is applied - a single output slot used to overwrite a
            // result that had not been read yet, which silently dropped a whole sentence.
            if (!translationTaskQueue.TryTake(out var first))
            {
                if (!translationTaskQueue.WaitForResult(250))
                    return;

                if (!translationTaskQueue.TryTake(out first))
                    return;
            }

            ApplyTranslation(first);
            while (translationTaskQueue.TryTake(out var next))
                ApplyTranslation(next);
        }

        /// <summary>
        /// Puts a translation on the line its source sentence is already on. The line may still be the
        /// one on screen or it may have moved into the history; either way it is found by the source
        /// text, so results arriving out of order cannot overwrite each other.
        /// </summary>
        private static void ApplyTranslation(TranslationResult result)
        {
            lock (displayLock)
                ApplyTranslationCore(result);
        }

        private static void ApplyTranslationCore(TranslationResult result)
        {
            string sourceText = result.SourceText;
            string translatedText = result.TranslatedText;

            if (string.IsNullOrWhiteSpace(translatedText))
                return;

            bool isCurrent = string.Equals(Caption.OriginalCaption, sourceText, StringComparison.Ordinal);
            if (!isCurrent && !Caption.HasRecentLineSource(sourceText))
            {
                // The recogniser revised or removed this sentence while the translation was in flight;
                // showing it now would put stale text back on screen.
                return;
            }

            if (StatusText.IsStatus(translatedText))
            {
                // An error is not a translation: it must not replace the sentence, and must never be
                // committed to the rolling history as if it were one.
                Caption.Notice = translatedText;
                Caption.RebuildRollingLines(Setting?.NumContexts ?? 0);
                return;
            }

            bool redundant = IsTranslationRedundant(sourceText, translatedText);

            if (isCurrent)
            {
                Caption.TranslatedCaption = translatedText;
                Caption.DisplayTranslatedCaption = translatedText;
                Caption.TranslationRedundant = redundant;

                // Overlay window: the notice prefix is stripped so it renders in its own font.
                Caption.OverlayOriginalCaption = sourceText;
                var match = RegexPatterns.NoticePrefixAndTranslation().Match(translatedText);
                Caption.OverlayNoticePrefix = match.Groups[1].Value.Trim();
                Caption.OverlayCurrentTranslation = match.Groups[2].Value.Trim();
            }
            else
            {
                Caption.SetRecentLineText(sourceText, translatedText);
            }

            Caption.RebuildRollingLines(Setting?.NumContexts ?? 0);
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
                return (LocalizationManager.T("Error_TranslationFailed", ex.Message), isChoke);
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
                SnackbarHost.Show(LocalizationManager.T("Error_LoggingFailed"), ex.Message, SnackbarType.Error,
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
                SnackbarHost.Show(LocalizationManager.T("Error_LoggingFailed"), ex.Message, SnackbarType.Error,
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
        public static bool IsTranslationRedundant(string? originalCaption, string? translatedCaption)
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
            lock (displayLock)
            {
                transcript.Reset();

                lock (pendingLock)
                    pendingTextQueue.Clear();

                Caption?.ClearRecentLines();
                Caption?.Contexts.Clear();

                if (Caption is not null)
                {
                    Caption.InterimText = null;
                    Caption.DisplayTranslatedCaption = string.Empty;
                    Caption.OriginalCaption = string.Empty;
                    Caption.TranslatedCaption = string.Empty;
                    RebuildRollingLinesSetting();
                }

                Caption?.OnPropertyChanged("DisplayLogCards");
                Caption?.OnPropertyChanged("OverlayPreviousTranslation");
            }
        }

        private static void RebuildRollingLinesSetting() =>
            Caption?.RebuildRollingLines(Setting?.NumContexts ?? 0);

        // If this text is too similar to the last one, overwrite it when logging. An identical
        // sentence is a genuine repetition (somebody said it twice), not a correction, and both
        // rows belong in the history.
        public static async Task<bool> IsOverwrite(string originalText, CancellationToken token = default)
        {
            string lastOriginalText = await SQLiteHistoryLogger.LoadLastSourceText(token);
            if (lastOriginalText == null)
                return false;

            if (string.Equals(originalText, lastOriginalText, StringComparison.Ordinal))
                return false;

            int minLen = Math.Min(originalText.Length, lastOriginalText.Length);
            originalText = originalText.Substring(0, minLen);
            lastOriginalText = lastOriginalText.Substring(0, minLen);

            double similarity = TextUtil.Similarity(originalText, lastOriginalText);
            return similarity > TextUtil.SIM_THRESHOLD;
        }
    }
}
