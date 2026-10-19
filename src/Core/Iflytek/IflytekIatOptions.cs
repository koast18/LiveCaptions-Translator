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
    }
}
