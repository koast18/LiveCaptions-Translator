namespace LiveCaptionsTranslator.Core.Audio
{
    /// <summary>Carries one chunk of <see cref="AudioFormat"/> PCM.</summary>
    public sealed class AudioFrameEventArgs : EventArgs
    {
        public AudioFrameEventArgs(byte[] pcm)
        {
            Pcm = pcm;
        }

        /// <summary>16 kHz / mono / 16-bit little-endian PCM.</summary>
        public byte[] Pcm { get; }
    }

    /// <summary>
    /// A source of 16 kHz / mono / 16-bit little-endian PCM.
    /// <para>
    /// Implementations live in the platform layer (e.g. WASAPI loopback / microphone)
    /// so that this abstraction stays free of any Windows or UI dependency.
    /// </para>
    /// </summary>
    public interface IAudioSource : IAsyncDisposable
    {
        /// <summary>Stable identifier, e.g. <c>wasapi-loopback</c>.</summary>
        string Id { get; }

        /// <summary>Human readable name shown in the settings UI.</summary>
        string DisplayName { get; }

        bool IsCapturing { get; }

        /// <summary>Raised for every captured chunk. Handlers must not block.</summary>
        event EventHandler<AudioFrameEventArgs>? FrameAvailable;

        Task StartAsync(CancellationToken cancellationToken = default);

        Task StopAsync();
    }
}
