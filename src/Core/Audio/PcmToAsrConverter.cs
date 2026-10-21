namespace LiveCaptionsTranslator.Core.Audio
{
    /// <summary>
    /// Converts interleaved 32-bit float PCM at any sample rate / channel count into the
    /// 16 kHz / mono / 16-bit little-endian stream that ASR providers expect.
    /// <para>
    /// Downsampling is preceded by a low-pass, because dropping 48 kHz to 16 kHz by plain
    /// interpolation folds everything above 8 kHz back into the speech band as hiss and makes the
    /// recogniser noticeably worse - the reason the same audio transcribed better in a phone keyboard.
    /// </para>
    /// <para>
    /// The converter is stateful, so a continuous capture can be fed in arbitrary chunks:
    /// the resampling position and the samples still needed for interpolation are carried
    /// over between calls. Resampling is linear interpolation, matching the reference
    /// implementation used to validate the streaming protocol.
    /// </para>
    /// <para>
    /// The trailing partial interpolation window (at most one source frame) is dropped, which
    /// is inaudible for a live stream and keeps chunked and single-shot conversion identical.
    /// </para>
    /// </summary>
    public sealed class PcmToAsrConverter
    {
        private readonly int sourceChannels;
        private readonly double step;

        private float[] mono = new float[8192];
        private int monoCount;
        private double position;

        /// <summary>Anti-aliasing low-pass, applied before the samples are decimated.</summary>
        private readonly Biquad lowPass;

        public PcmToAsrConverter(int sourceSampleRate, int sourceChannels)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceSampleRate);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceChannels);

            SourceSampleRate = sourceSampleRate;
            this.sourceChannels = sourceChannels;
            step = (double)sourceSampleRate / AudioFormat.SampleRate;

            // Anything above half the target rate must go before it can fold back into the signal.
            lowPass = Biquad.LowPass(sourceSampleRate, AudioFormat.SampleRate * 0.45, 0.7071);
        }

        public int SourceSampleRate { get; }

        public int SourceChannels => sourceChannels;

        /// <summary>Converts one chunk of interleaved float samples into 16 kHz mono PCM16.</summary>
        public byte[] Convert(ReadOnlySpan<float> interleaved)
        {
            int frames = interleaved.Length / sourceChannels;
            if (frames == 0)
                return [];

            AppendMono(interleaved, frames);
            return Resample();
        }

        private void AppendMono(ReadOnlySpan<float> interleaved, int frames)
        {
            EnsureCapacity(monoCount + frames);

            if (sourceChannels == 1)
            {
                interleaved[..frames].CopyTo(mono.AsSpan(monoCount));
            }
            else
            {
                var destination = mono.AsSpan(monoCount, frames);
                for (int frame = 0; frame < frames; frame++)
                {
                    int baseIndex = frame * sourceChannels;
                    float sum = 0f;
                    for (int channel = 0; channel < sourceChannels; channel++)
                        sum += interleaved[baseIndex + channel];

                    destination[frame] = sum / sourceChannels;
                }
            }

            monoCount += frames;

            if (step > 1.05)
                Filter(frames);
        }

        private void Filter(int frames)
        {
            var span = mono.AsSpan(monoCount - frames, frames);
            for (int index = 0; index < span.Length; index++)
                span[index] = lowPass.Process(span[index]);
        }

        private byte[] Resample()
        {
            int capacity = (int)((monoCount - position) / step) + 1;
            if (capacity <= 0)
                return [];

            var output = new byte[capacity * sizeof(short)];
            int written = 0;

            while (true)
            {
                int index = (int)position;

                // Interpolation needs the next sample; leave it for the following chunk.
                if (index + 1 >= monoCount)
                    break;

                double fraction = position - index;
                float sample = (float)((mono[index] * (1 - fraction)) + (mono[index + 1] * fraction));
                short value = ToPcm16(sample);

                output[written++] = (byte)(value & 0xFF);
                output[written++] = (byte)((value >> 8) & 0xFF);
                position += step;
            }

            Compact();

            return written == output.Length ? output : output[..written];
        }

        /// <summary>Drops the mono samples that can no longer contribute to a future output.</summary>
        private void Compact()
        {
            // `position` may already have run past the end of the buffer: the next output sample
            // needs source samples that only arrive with the following chunk. Only the samples
            // that are really there may be dropped, and the fractional offset must survive -
            // losing it would shift every later chunk and drift the audio.
            int drop = Math.Min((int)position, monoCount);
            if (drop <= 0)
                return;

            if (drop < monoCount)
                Array.Copy(mono, drop, mono, 0, monoCount - drop);

            monoCount -= drop;
            position -= drop;
        }

        private void EnsureCapacity(int required)
        {
            if (mono.Length >= required)
                return;

            int size = mono.Length;
            while (size < required)
                size *= 2;

            Array.Resize(ref mono, size);
        }

        public static short ToPcm16(float sample)
        {
            float clamped = Math.Clamp(sample, -1f, 1f);
            return (short)MathF.Round(clamped * short.MaxValue);
        }
    }
}
