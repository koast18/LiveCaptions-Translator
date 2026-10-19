using System.Windows.Automation;

using LiveCaptionsTranslator.Core.Asr;

namespace LiveCaptionsTranslator.Platform.Windows
{
    /// <summary>
    /// Wraps the OS-provided Windows Live Captions pipeline as an <see cref="IAsrProvider"/>.
    /// <para>
    /// This provider never touches audio: Windows Live Captions does the capture and the
    /// on-device recognition, and this class reads the recognised text out of its window
    /// through UI Automation. The recognised text is forwarded verbatim to the sink, which
    /// keeps the upstream caption-processing behaviour unchanged.
    /// </para>
    /// </summary>
    public sealed class WindowsLiveCaptionsProvider : IAsrProvider
    {
        public const string ProviderId = "windows-livecaptions";

        /// <summary>
        /// Languages Windows Live Captions can be configured for. The language that is actually
        /// used is picked inside Windows Live Captions itself, so none of these are marked as
        /// verified by this project.
        /// </summary>
        private static readonly AsrLanguage[] Languages =
        {
            new("zh_cn", "中文（简体）"),
            new("zh_tw", "中文（繁體）"),
            new("en_us", "English (US)"),
            new("ja", "日本語"),
            new("ko", "한국어"),
            new("fr", "Français"),
            new("de", "Deutsch"),
            new("es", "Español"),
            new("it", "Italiano"),
            new("pt", "Português"),
            new("ru", "Русский"),
            new("nl", "Nederlands"),
            new("pl", "Polski"),
            new("sv", "Svenska"),
            new("da", "Dansk"),
            new("fi", "Suomi"),
            new("nb", "Norsk"),
        };

        private CancellationTokenSource? cancellation;
        private Task? pollLoop;

        public string Id => ProviderId;

        public string DisplayName => "Windows Live Captions";

        public bool RequiresAudioSource => false;

        public IReadOnlyList<AsrLanguage> SupportedLanguages => Languages;

        /// <summary>UI Automation handle of <c>LiveCaptions.exe</c> while it is running.</summary>
        public AutomationElement? Window { get; private set; }

        public Task StartAsync(AsrContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            Window = LiveCaptionsHandler.LaunchLiveCaptions();
            LiveCaptionsHandler.FixLiveCaptions(Window);
            LiveCaptionsHandler.HideLiveCaptions(Window);

            cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            pollLoop = Task.Run(() => PollAsync(context.Sink, cancellation.Token));
            return Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            if (cancellation != null)
            {
                // ConfigureAwait(false): StopAsync is also called synchronously from the UI
                // thread on application exit, so the continuations must not need that thread.
                await cancellation.CancelAsync().ConfigureAwait(false);
            }

            if (pollLoop != null)
            {
                try
                {
                    await pollLoop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }

                pollLoop = null;
            }

            cancellation?.Dispose();
            cancellation = null;

            if (Window != null)
            {
                try
                {
                    LiveCaptionsHandler.RestoreLiveCaptions(Window);
                    LiveCaptionsHandler.KillLiveCaptions(Window);
                }
                catch (ElementNotAvailableException)
                {
                }

                Window = null;
            }
        }

        public async ValueTask DisposeAsync() => await StopAsync();

        /// <summary>
        /// Polls the Live Captions window and forwards whatever it has recognised.
        /// </summary>
        private async Task PollAsync(IAsrSink sink, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (Window == null)
                    {
                        sink.OnNotice("[WARNING] LiveCaptions was unexpectedly closed, restarting...");
                        Window = LiveCaptionsHandler.LaunchLiveCaptions();
                        sink.OnNotice(string.Empty);
                    }

                    // Touching Current verifies the process is still alive; a dead window
                    // throws ElementNotAvailableException.
                    _ = Window.Current.Name;

                    // Get the text recognized by LiveCaptions (10-20ms)
                    var text = LiveCaptionsHandler.GetCaptions(Window);
                    if (!string.IsNullOrEmpty(text))
                    {
                        sink.OnPartial(text);
                    }
                }
                catch (ElementNotAvailableException)
                {
                    Window = null;
                    continue;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    sink.OnError(ex);
                }

                try
                {
                    await Task.Delay(25, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
