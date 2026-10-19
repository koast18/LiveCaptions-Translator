using LiveCaptionsTranslator.Core.Audio;

namespace LiveCaptionsTranslator.Platform.Windows
{
    /// <summary>
    /// Central place that knows which audio inputs ship with this build.
    /// Creating a source is cheap and never opens an audio device; the device is only
    /// touched from <see cref="IAudioSource.StartAsync"/>.
    /// </summary>
    public static class AudioSourceCatalog
    {
        public const string LoopbackId = WasapiLoopbackAudioSource.SourceId;

        public const string MicrophoneId = WasapiMicrophoneAudioSource.SourceId;

        public static IReadOnlyList<string> Ids { get; } = new[] { LoopbackId, MicrophoneId };

        public static IAudioSource Create(string id) => id switch
        {
            LoopbackId => new WasapiLoopbackAudioSource(),
            MicrophoneId => new WasapiMicrophoneAudioSource(),
            _ => throw new KeyNotFoundException($"Audio source '{id}' is not registered."),
        };

        public static bool TryCreate(string id, out IAudioSource? source)
        {
            switch (id)
            {
                case LoopbackId:
                    source = new WasapiLoopbackAudioSource();
                    return true;
                case MicrophoneId:
                    source = new WasapiMicrophoneAudioSource();
                    return true;
                default:
                    source = null;
                    return false;
            }
        }
    }
}
