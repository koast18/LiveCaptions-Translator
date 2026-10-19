using LiveCaptionsTranslator.Core.Audio;

namespace LiveCaptionsTranslator.Core.Tests;

public class AudioSourceRecorderTests : IDisposable
{
    private readonly string directory =
        Path.Combine(Path.GetTempPath(), "lct-recorder-" + Guid.NewGuid().ToString("N"));

    public AudioSourceRecorderTests() => Directory.CreateDirectory(directory);

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private sealed class FakeAudioSource : IAudioSource
    {
        public string Id => "fake";

        public string DisplayName => "Fake source";

        public bool IsCapturing { get; private set; }

        public event EventHandler<AudioFrameEventArgs>? FrameAvailable;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            IsCapturing = true;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            IsCapturing = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Emit(byte[] pcm) => FrameAvailable?.Invoke(this, new AudioFrameEventArgs(pcm));
    }

    [Fact]
    public void Captured_frames_are_written_to_a_readable_wav()
    {
        var source = new FakeAudioSource();
        string path = Path.Combine(directory, "capture.wav");

        var first = new byte[AudioFormat.ChunkBytes];
        var second = new byte[AudioFormat.ChunkBytes];
        Array.Fill(first, (byte)0x11);
        Array.Fill(second, (byte)0x22);

        using (var recorder = new AudioSourceRecorder(source, path))
        {
            recorder.Start();
            source.Emit(first);
            source.Emit(second);

            Assert.True(recorder.IsRecording);
            Assert.Equal(first.Length + second.Length, recorder.RecordedBytes);
        }

        Assert.True(File.Exists(path));

        var audio = WavFile.Read(path);
        Assert.Equal(AudioFormat.SampleRate, audio.SampleRate);
        Assert.Equal(AudioFormat.Channels, audio.Channels);
        Assert.Equal(first.Length + second.Length, audio.Pcm.Length);
        Assert.Equal(first, audio.Pcm[..first.Length]);
        Assert.Equal(second, audio.Pcm[first.Length..]);
    }

    [Fact]
    public void Frames_arriving_after_stop_are_not_recorded()
    {
        var source = new FakeAudioSource();
        string path = Path.Combine(directory, "stopped.wav");

        using var recorder = new AudioSourceRecorder(source, path);
        recorder.Start();
        source.Emit(new byte[8]);
        recorder.Stop();

        Assert.False(recorder.IsRecording);
        source.Emit(new byte[64]);

        Assert.Equal(8, WavFile.Read(path).Pcm.Length);
    }

    [Fact]
    public void Duration_reflects_the_recorded_pcm()
    {
        var source = new FakeAudioSource();
        string path = Path.Combine(directory, "duration.wav");

        using var recorder = new AudioSourceRecorder(source, path);
        recorder.Start();

        // Half a second of 16 kHz mono PCM16, delivered in 40 ms chunks.
        for (int i = 0; i < 12; i++)
            source.Emit(new byte[AudioFormat.ChunkBytes]);

        recorder.Stop();

        Assert.Equal(TimeSpan.FromSeconds(0.48), recorder.Duration);
    }
}
