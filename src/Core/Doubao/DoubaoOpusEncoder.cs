using Concentus;
using Concentus.Enums;

namespace LiveCaptionsTranslator.Core.Doubao
{
    /// <summary>
    /// Encodes 16 kHz mono PCM into the 20 ms Opus packets the ASR socket expects.
    /// <para>
    /// Opus comes from Concentus, a managed port of libopus. The package can bind to a native
    /// libopus if one happens to be next to the app, so the managed path is forced on explicitly:
    /// this project promises a build without native dependencies.
    /// </para>
    /// </summary>
    public sealed class DoubaoOpusEncoder : IDisposable
    {
        public const int SampleRate = 16000;

        public const int Channels = 1;

        public const int FrameMilliseconds = 20;

        /// <summary>Samples in one 20 ms frame: 320.</summary>
        public const int FrameSamples = SampleRate * FrameMilliseconds / 1000;

        public const int DefaultBitrate = 32000;

        /// <summary>Largest packet Opus can emit; the buffer has to be at least this big.</summary>
        public const int MaxPacketBytes = 1275;

        private readonly IOpusEncoder encoder;
        private readonly short[] frame = new short[FrameSamples];

        public DoubaoOpusEncoder(int bitrate = DefaultBitrate)
        {
            OpusCodecFactory.AttemptToUseNativeLibrary = false;

            // VOIP instead of AUDIO: the encoder spends its bits on intelligible speech rather than
            // on reproducing music, which is what the recogniser is listening for.
            encoder = OpusCodecFactory.CreateEncoder(
                SampleRate, Channels, OpusApplication.OPUS_APPLICATION_VOIP);
            encoder.Bitrate = bitrate;
            encoder.Complexity = 10;
        }

        public int Bitrate => encoder.Bitrate;

        /// <summary>Encodes exactly one frame of <see cref="FrameSamples"/> samples.</summary>
        public byte[] EncodeFrame(ReadOnlySpan<short> samples)
        {
            if (samples.Length != FrameSamples)
            {
                throw new ArgumentException(
                    $"expected exactly {FrameSamples} samples, got {samples.Length}", nameof(samples));
            }

            return EncodeCore(samples);
        }

        /// <summary>
        /// Encodes any length of PCM, splitting it into 20 ms frames. A trailing partial frame is
        /// zero padded, so the packet count is always <c>ceil(samples / 320)</c>.
        /// </summary>
        public IReadOnlyList<byte[]> Encode(ReadOnlySpan<short> pcm)
        {
            if (pcm.IsEmpty)
                return Array.Empty<byte[]>();

            var packets = new List<byte[]>((pcm.Length + FrameSamples - 1) / FrameSamples);

            for (int offset = 0; offset < pcm.Length; offset += FrameSamples)
            {
                int count = Math.Min(FrameSamples, pcm.Length - offset);
                packets.Add(EncodeCore(pcm.Slice(offset, count)));
            }

            return packets;
        }

        public void Dispose() => encoder.Dispose();

        private byte[] EncodeCore(ReadOnlySpan<short> samples)
        {
            // Clear first: a short trailing frame must not reuse the previous frame's tail.
            frame.AsSpan().Clear();
            samples.CopyTo(frame);

            var output = new byte[MaxPacketBytes];
            int written = encoder.Encode(frame, FrameSamples, output, output.Length);

            if (written <= 0)
                throw new InvalidOperationException($"Opus 编码返回了 {written} 字节");

            return output[..written];
        }
    }
}
