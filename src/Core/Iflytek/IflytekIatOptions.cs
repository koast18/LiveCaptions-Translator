using LiveCaptionsTranslator.Core.Asr;
using LiveCaptionsTranslator.Core.Audio;

namespace LiveCaptionsTranslator.Core.Iflytek
{
    /// <summary>Thrown when the service answers with a non-zero <c>code</c>.</summary>
    public sealed class IflytekIatException : Exception
    {
        public IflytekIatException(int code, string message)
            : base($"[{code}] {message}")
        {
            Code = code;
            Message = message;
        }

        public int Code { get; }

        public new string Message { get; }
    }

    /// <summary>Well-known keys a host puts into <see cref="AsrOptions.Settings"/> for this provider.</summary>
    public static class IflytekSettingKeys
    {
        public const string ApiKey = "api_key";

        public const string ApiSecret = "api_secret";

        public const string Host = "host";

        public const string Path = "path";

        public const string AppId = "app_id";
    }

    /// <summary>Tunables for the IAT provider.</summary>
    public sealed class IflytekIatOptions
    {
        public IflytekCredentials Credentials { get; init; } = IflytekCredentials.Default;

        public IflytekBusiness Business { get; init; } = IflytekBusiness.Default;

        public int ChunkBytes { get; init; } = AudioFormat.ChunkBytes;

        /// <summary>How long to wait for the next server message before giving up on a session.</summary>
        public TimeSpan ReceiveTimeout { get; init; } = TimeSpan.FromSeconds(30);

        /// <summary>Delay between reconnect attempts.</summary>
        public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);

        public int MaxReconnectAttempts { get; init; } = 5;

        /// <summary>How long a graceful stop waits for the final frame to be flushed.</summary>
        public TimeSpan GracefulStopTimeout { get; init; } = TimeSpan.FromSeconds(3);

        /// <summary>Prepend the 200 ms of silence the reference client uses.</summary>
        public bool PadWithLeadingSilence { get; init; } = true;

        /// <summary>
        /// Builds the provider configuration from the generic host settings: the language and
        /// accent come from <see cref="AsrOptions.LanguageId"/>/<see cref="AsrOptions.Accent"/>,
        /// the credentials from <see cref="AsrOptions.Settings"/>. Anything missing falls back to
        /// the committed defaults, so the provider works with no configuration at all.
        /// </summary>
        public static IflytekIatOptions FromAsrOptions(AsrOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            return new IflytekIatOptions
            {
                Credentials = new IflytekCredentials
                {
                    ApiKey = Read(options, IflytekSettingKeys.ApiKey, IflytekCredentials.DefaultApiKey),
                    ApiSecret = Read(options, IflytekSettingKeys.ApiSecret, IflytekCredentials.DefaultApiSecret),
                    Host = Read(options, IflytekSettingKeys.Host, IflytekCredentials.DefaultHost),
                    Path = Read(options, IflytekSettingKeys.Path, IflytekCredentials.DefaultPath),
                    AppId = Read(options, IflytekSettingKeys.AppId, IflytekCredentials.DefaultAppId),
                },
                Business = IflytekBusiness.Default with
                {
                    Language = string.IsNullOrWhiteSpace(options.LanguageId)
                        ? IflytekBusiness.Default.Language
                        : options.LanguageId,
                    Accent = string.IsNullOrWhiteSpace(options.Accent)
                        ? IflytekBusiness.Default.Accent
                        : options.Accent,
                },
            };
        }

        private static string Read(AsrOptions options, string key, string fallback) =>
            options.Settings.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : fallback;
    }
}
