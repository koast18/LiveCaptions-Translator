namespace LiveCaptionsTranslator.Core.Audio
{
    /// <summary>
    /// The single PCM format every <see cref="IAudioSource"/> must emit and every
    /// audio-consuming ASR provider may assume: 16 kHz / mono / 16-bit little-endian.
    /// </summary>
    public static class AudioFormat
    {
        public const int SampleRate = 16000;
        public const int Channels = 1;
        public const int BitsPerSample = 16;

        /// <summary>Frame size used by the streaming ASR protocol (40 ms).</summary>
        public const int ChunkMs = 40;

        public const int ChunkSamples = SampleRate * ChunkMs / 1000;          // 640
        public const int ChunkBytes = ChunkSamples * (BitsPerSample / 8);     // 1280
    }
}
