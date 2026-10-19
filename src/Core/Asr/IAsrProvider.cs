using LiveCaptionsTranslator.Core.Audio;

namespace LiveCaptionsTranslator.Core.Asr
{
    /// <summary>Receives incremental recognition output from an <see cref="IAsrProvider"/>.</summary>
    public interface IAsrSink
    {
        /// <summary>
        /// A provisional result that may still be corrected or replaced by a later call.
        /// </summary>
        void OnPartial(string text);

        /// <summary>A finalized segment that will no longer change.</summary>
        void OnFinal(string text);

        /// <summary>
        /// Non-fatal status text the host may surface to the user (reconnects, restarts, ...).
        /// An empty string clears the previous notice.
        /// </summary>
        void OnNotice(string message)
        {
        }

        /// <summary>A recoverable or fatal failure inside the provider.</summary>
        void OnError(Exception error);
    }

    /// <summary>Everything a provider needs to run.</summary>
    public sealed class AsrOptions
    {
        /// <summary>Language id as advertised by <see cref="IAsrProvider.SupportedLanguages"/>.</summary>
        public string LanguageId { get; init; } = "zh_cn";

        /// <summary>Accent/dialect selector, when the provider supports one.</summary>
        public string? Accent { get; init; }

        /// <summary>Free-form provider settings (credentials, endpoints, ...).</summary>
        public IReadOnlyDictionary<string, string> Settings { get; init; } =
            new Dictionary<string, string>();
    }

    /// <summary>
    /// A pluggable speech-recognition backend.
    /// <para>
    /// Providers that recognise audio (<see cref="RequiresAudioSource"/> is <c>true</c>)
    /// subscribe to <see cref="AsrContext.AudioSource"/> themselves; providers that wrap an
    /// external caption pipeline (e.g. Windows Live Captions) ignore it.
    /// </para>
    /// </summary>
    public interface IAsrProvider : IAsyncDisposable
    {
        /// <summary>Stable identifier, e.g. <c>windows-livecaptions</c> or <c>iflytek-iat</c>.</summary>
        string Id { get; }

        string DisplayName { get; }

        /// <summary>Whether the host must supply an <see cref="IAudioSource"/> before starting.</summary>
        bool RequiresAudioSource { get; }

        /// <summary>Languages this provider can be asked to recognise.</summary>
        IReadOnlyList<AsrLanguage> SupportedLanguages { get; }

        Task StartAsync(AsrContext context, CancellationToken cancellationToken = default);

        Task StopAsync();
    }

    /// <summary>Runtime context handed to <see cref="IAsrProvider.StartAsync"/>.</summary>
    public sealed class AsrContext
    {
        public required IAsrSink Sink { get; init; }

        public required AsrOptions Options { get; init; }

        /// <summary>Required when <see cref="IAsrProvider.RequiresAudioSource"/> is <c>true</c>.</summary>
        public IAudioSource? AudioSource { get; init; }
    }
}
