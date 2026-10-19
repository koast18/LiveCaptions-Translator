namespace LiveCaptionsTranslator.Core.Audio
{
    /// <summary>
    /// Records everything an <see cref="IAudioSource"/> produces into a 16 kHz / mono WAV file.
    /// <para>
    /// The audio source already emits <see cref="AudioFormat"/> PCM, so this is a plain
    /// accumulate-then-write recorder with no format handling of its own.
    /// </para>
    /// </summary>
    public sealed class AudioSourceRecorder : IDisposable
    {
        private readonly IAudioSource source;
        private readonly string path;
        private readonly List<byte> pcm = [];

        private bool recording;
        private long recordedBytes;

        public AudioSourceRecorder(IAudioSource source, string path)
        {
            this.source = source ?? throw new ArgumentNullException(nameof(source));
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            this.path = path;
        }

        public bool IsRecording => recording;

        public long RecordedBytes
        {
            get
            {
                lock (pcm)
                    return recordedBytes;
            }
        }

        public TimeSpan Duration =>
            TimeSpan.FromSeconds((double)RecordedBytes / (AudioFormat.SampleRate * (AudioFormat.BitsPerSample / 8)));

        public void Start()
        {
            if (recording)
                return;

            lock (pcm)
            {
                pcm.Clear();
                recordedBytes = 0;
            }

            source.FrameAvailable += OnFrameAvailable;
            recording = true;
        }

        /// <summary>Stops recording and writes the captured PCM to <see cref="path"/>.</summary>
        public void Stop()
        {
            if (!recording)
                return;

            source.FrameAvailable -= OnFrameAvailable;
            recording = false;

            byte[] captured;
            lock (pcm)
            {
                captured = pcm.ToArray();
                pcm.Clear();
            }

            WavFile.Write(path, captured);
        }

        public void Dispose()
        {
            if (recording)
                Stop();
        }

        private void OnFrameAvailable(object? sender, AudioFrameEventArgs e)
        {
            lock (pcm)
            {
                pcm.AddRange(e.Pcm);
                recordedBytes += e.Pcm.Length;
            }
        }
    }
}
