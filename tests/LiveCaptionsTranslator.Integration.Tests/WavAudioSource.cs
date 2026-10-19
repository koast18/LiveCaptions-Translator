using LiveCaptionsTranslator.Core.Audio;

namespace LiveCaptionsTranslator.Integration.Tests;

/// <summary>
/// Replays a committed WAV file through the <see cref="IAudioSource"/> contract, so the
/// integration test exercises the exact same provider path a live capture would.
/// </summary>
internal sealed class WavAudioSource : IAudioSource
{
    private readonly byte[] pcm;
    private readonly TimeSpan pacePerChunk;

    public WavAudioSource(byte[] pcm, TimeSpan? pacePerChunk = null)
    {
        this.pcm = pcm;
        this.pacePerChunk = pacePerChunk ?? TimeSpan.FromMilliseconds(10);
    }

    public string Id => "wav-file";

    public string DisplayName => "WAV file";

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

    /// <summary>Emits the whole file in 40 ms chunks, gently paced so the service is not flooded.</summary>
    public async Task EmitAllAsync(CancellationToken cancellationToken = default)
    {
        for (int offset = 0; offset < pcm.Length; offset += AudioFormat.ChunkBytes)
        {
            int count = Math.Min(AudioFormat.ChunkBytes, pcm.Length - offset);
            var chunk = new byte[count];
            Array.Copy(pcm, offset, chunk, 0, count);

            FrameAvailable?.Invoke(this, new AudioFrameEventArgs(chunk));

            if (pacePerChunk > TimeSpan.Zero)
                await Task.Delay(pacePerChunk, cancellationToken);
        }
    }
}

/// <summary>Collects everything the provider publishes.</summary>
internal sealed class CollectingSink : LiveCaptionsTranslator.Core.Asr.IAsrSink
{
    private readonly object gate = new();
    private readonly List<string> partials = [];
    private readonly List<string> finals = [];
    private readonly List<string> notices = [];
    private readonly List<Exception> errors = [];

    public IReadOnlyList<string> Partials
    {
        get
        {
            lock (gate)
                return partials.ToArray();
        }
    }

    public IReadOnlyList<string> Finals
    {
        get
        {
            lock (gate)
                return finals.ToArray();
        }
    }

    public IReadOnlyList<string> Notices
    {
        get
        {
            lock (gate)
                return notices.ToArray();
        }
    }

    public IReadOnlyList<Exception> Errors
    {
        get
        {
            lock (gate)
                return errors.ToArray();
        }
    }

    /// <summary>The best transcript seen, preferring a final result.</summary>
    public string Transcript
    {
        get
        {
            lock (gate)
            {
                if (finals.Count > 0)
                    return finals[^1];

                return partials.Count > 0 ? partials[^1] : string.Empty;
            }
        }
    }

    public void OnPartial(string text)
    {
        lock (gate)
            partials.Add(text);
    }

    public void OnFinal(string text)
    {
        lock (gate)
            finals.Add(text);
    }

    public void OnNotice(string message)
    {
        lock (gate)
            notices.Add(message);
    }

    public void OnError(Exception error)
    {
        lock (gate)
            errors.Add(error);
    }
}
