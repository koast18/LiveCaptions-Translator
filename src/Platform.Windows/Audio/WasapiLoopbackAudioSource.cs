using NAudio.Wave;

namespace LiveCaptionsTranslator.Platform.Windows
{
    /// <summary>Captures whatever the machine is currently playing (WASAPI loopback).</summary>
    public sealed class WasapiLoopbackAudioSource : WasapiAudioSourceBase
    {
        public const string SourceId = "wasapi-loopback";

        public override string Id => SourceId;

        public override string DisplayName => "系统声音（WASAPI 环回）";

        protected override IWaveIn CreateCapture() => new WasapiLoopbackCapture();
    }
}
