using System.Threading.Channels;

using LiveCaptionsTranslator.Core.Asr;
using LiveCaptionsTranslator.Core.Audio;
using LiveCaptionsTranslator.Core.Iflytek;

namespace LiveCaptionsTranslator.Core.Tests;

internal sealed class FakeAudioSource : IAudioSource
{
    public string Id => "fake-audio";

    public string DisplayName => "Fake audio source";

    public bool IsCapturing { get; private set; }

    public int StartCount { get; private set; }

    public event EventHandler<AudioFrameEventArgs>? FrameAvailable;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        IsCapturing = true;
        StartCount++;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        IsCapturing = false;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Emit(byte[] pcm) => FrameAvailable?.Invoke(this, new AudioFrameEventArgs(pcm));

    public void EmitChunk() => Emit(new byte[AudioFormat.ChunkBytes]);
}

internal sealed class RecordingSink : IAsrSink
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

/// <summary>In-memory stand-in for the WebSocket, so reconnect behaviour is testable.</summary>
internal sealed class FakeTransport : IIflytekTransport
{
    private readonly Channel<string?> messages = Channel.CreateUnbounded<string?>();

    private readonly List<string> sent = [];

    public string? Url { get; private set; }

    public bool IsOpen { get; private set; }

    public bool ThrowOnSend { get; set; }

    public int ConnectCount { get; private set; }

    public IReadOnlyList<string> Sent
    {
        get
        {
            lock (sent)
                return sent.ToArray();
        }
    }

    public Task ConnectAsync(Uri url, CancellationToken cancellationToken)
    {
        Url = url.ToString();
        IsOpen = true;
        ConnectCount++;
        return Task.CompletedTask;
    }

    public Task SendAsync(string frame, CancellationToken cancellationToken)
    {
        if (ThrowOnSend)
            throw new IOException("simulated send failure");

        lock (sent)
            sent.Add(frame);

        return Task.CompletedTask;
    }

    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken) =>
        await messages.Reader.ReadAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        messages.Writer.TryComplete();
        IsOpen = false;
        return ValueTask.CompletedTask;
    }

    public void Enqueue(string json) => messages.Writer.TryWrite(json);

    /// <summary>Simulates the server dropping the connection.</summary>
    public void Drop() => messages.Writer.TryComplete();
}
