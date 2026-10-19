using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LiveCaptionsTranslator.Platform.Windows
{
    /// <summary>Captures the default recording device (microphone).</summary>
    public sealed class WasapiMicrophoneAudioSource : WasapiAudioSourceBase
    {
        public const string SourceId = "wasapi-microphone";

        public override string Id => SourceId;

        public override string DisplayName => "麦克风";

        protected override IWaveIn CreateCapture() => new WasapiCapture();
    }
}
