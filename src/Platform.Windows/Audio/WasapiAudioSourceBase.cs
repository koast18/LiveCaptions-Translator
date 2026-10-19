using LiveCaptionsTranslator.Core.Audio;
using NAudio.Wave;

namespace LiveCaptionsTranslator.Platform.Windows
{
    /// <summary>
    /// Base class for WASAPI capture sources. Whatever the device produces is converted into the
    /// 16 kHz / mono / 16-bit little-endian stream described by <see cref="AudioFormat"/>.
    /// </summary>
    public abstract class WasapiAudioSourceBase : IAudioSource
    {
        private readonly object gate = new();

        private IWaveIn? capture;
        private PcmToAsrConverter? converter;
        private float[] samples = new float[8192];

        public abstract string Id { get; }

        public abstract string DisplayName { get; }

        public bool IsCapturing { get; private set; }

        public event EventHandler<AudioFrameEventArgs>? FrameAvailable;

        /// <summary>
        /// Creates the NAudio capture instance. Called from <see cref="StartAsync"/> so that
        /// constructing a source never touches an audio device.
        /// </summary>
        protected abstract IWaveIn CreateCapture();

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (IsCapturing)
                return Task.CompletedTask;

            capture = CreateCapture();
            converter = new PcmToAsrConverter(capture.WaveFormat.SampleRate, capture.WaveFormat.Channels);
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            capture.StartRecording();
            IsCapturing = true;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            if (!IsCapturing)
                return Task.CompletedTask;

            IsCapturing = false;

            var active = capture;
            capture = null;
            if (active != null)
            {
                active.DataAvailable -= OnDataAvailable;
                active.RecordingStopped -= OnRecordingStopped;
                active.StopRecording();
                active.Dispose();
            }

            converter = null;
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync() => await StopAsync();

        private void OnRecordingStopped(object? sender, StoppedEventArgs e) => IsCapturing = false;

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded == 0)
                return;

            var active = capture;
            if (active == null)
                return;

            int count = ReadSamples(active.WaveFormat, e);
            if (count == 0)
                return;

            byte[] pcm;
            lock (gate)
            {
                if (converter == null)
                    return;

                pcm = converter.Convert(samples.AsSpan(0, count));
            }

            if (pcm.Length > 0)
                FrameAvailable?.Invoke(this, new AudioFrameEventArgs(pcm));
        }

        /// <summary>Normalises a capture buffer into the float sample array. Returns the sample count.</summary>
        private int ReadSamples(WaveFormat format, WaveInEventArgs e)
        {
            if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
            {
                int count = e.BytesRecorded / 4;
                EnsureBuffer(count);
                Buffer.BlockCopy(e.Buffer, 0, samples, 0, count * 4);
                return count;
            }

            if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
            {
                int count = e.BytesRecorded / 2;
                EnsureBuffer(count);
                for (int i = 0; i < count; i++)
                    samples[i] = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;

                return count;
            }

            // Unsupported capture format (e.g. 24-bit or 8-bit PCM); nothing is emitted.
            return 0;
        }

        private void EnsureBuffer(int required)
        {
            if (samples.Length < required)
                samples = new float[Math.Max(required, samples.Length * 2)];
        }
    }
}
