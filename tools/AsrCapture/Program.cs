using System.IO;
using System.Diagnostics;
using System.Text;

using LiveCaptionsTranslator.Core.Asr;
using LiveCaptionsTranslator.Core.Audio;
using LiveCaptionsTranslator.Core.Doubao;
using LiveCaptionsTranslator.Core.Text;

namespace LiveCaptionsTranslator.Tools.AsrCapture;

/// <summary>
/// Runs a real WAV through the Doubao endpoint the app uses and records the exact callback stream.
/// <para>
/// The recording is what the caption pipeline sees, so it can be replayed offline by the unit and UI
/// tests. Audio is never committed: it stays on the machine that produced the capture.
/// </para>
/// <para>
/// Usage: <c>AsrCapture &lt;input.wav&gt; &lt;capture.txt&gt; [--seconds N]</c>
/// </para>
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--replay")
            return Replay(args[1]);

        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: AsrCapture <input.wav> <capture.txt> [--seconds N] [--vad MS]");
            Console.Error.WriteLine("       AsrCapture --replay <capture.txt>");
            return 2;
        }

        string input = args[0];
        string output = args[1];

        double seconds = 0;
        int vad = 600;
        for (int i = 2; i + 1 < args.Length; i++)
        {
            if (args[i] == "--seconds" && double.TryParse(args[i + 1], out double value))
                seconds = value;
            if (args[i] == "--vad" && int.TryParse(args[i + 1], out int window))
                vad = window;
        }

        var audio = WavFile.Read(input);
        if (audio.SampleRate != AudioFormat.SampleRate ||
            audio.Channels != AudioFormat.Channels ||
            audio.BitsPerSample != AudioFormat.BitsPerSample)
        {
            Console.Error.WriteLine(
                $"expected {AudioFormat.SampleRate} Hz mono {AudioFormat.BitsPerSample}-bit PCM, " +
                $"got {audio.SampleRate} Hz {audio.Channels} ch {audio.BitsPerSample} bit");
            return 2;
        }

        byte[] pcm = audio.Pcm;
        if (seconds > 0)
        {
            int maxBytes = (int)(seconds * AudioFormat.SampleRate * 2);
            if (pcm.Length > maxBytes)
                pcm = pcm[..maxBytes];
        }

        Console.WriteLine($"audio: {pcm.Length / (AudioFormat.SampleRate * 2.0):0.0} s");

        var credentials = await new DoubaoDeviceRegistrar().RegisterAsync();
        if (!credentials.IsComplete)
        {
            Console.Error.WriteLine("device registration did not return a usable credential");
            return 1;
        }

        // The endpoint rejects a freshly opened session now and then (50700000, service discovery).
        const int attempts = 6;

        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            var sink = new RecordingSink();
            var source = new WavAudioSource(pcm);

            await using var provider = new DoubaoAsrProvider(
                credentials,
                streamOptions: new DoubaoStreamOptions
                {
                    MaxConsecutiveFailures = 1,
                    ReconnectDelay = TimeSpan.FromSeconds(1),
                });

            await provider.StartAsync(new AsrContext
            {
                Sink = sink,
                Options = new AsrOptions
                {
                    // The window the app ships with; a larger one gives the recogniser the pauses a
                    // person makes instead of cutting at every breath.
                    Settings = new Dictionary<string, string>
                    {
                        [DoubaoSettingKeys.VadWindowMs] = vad.ToString(),
                    },
                },
                AudioSource = source,
                // Surface the provider's reason instead of its resource key: the transport and the
                // session handshake report different failures and only the message tells them apart.
                Localize = (key, arguments) => arguments.Length == 0
                    ? key
                    : key + ": " + string.Join(" | ", arguments.Select(value => value?.ToString())),
            });

            await source.EmitAllAsync();
            await provider.StopAsync();

            if (sink.Events.Count > 0)
            {
                File.WriteAllText(output, Render(sink.Events), new UTF8Encoding(false));
                Console.WriteLine($"captured {sink.Events.Count} callbacks -> {output}");
                PrintReplay(sink.Events, output);
                return 0;
            }

            Console.WriteLine($"attempt {attempt}: no transcript ({string.Join(" | ", sink.Notices)})");
            if (attempt < attempts)
                await Task.Delay(TimeSpan.FromSeconds(5));
        }

        Console.Error.WriteLine("the endpoint returned no transcript");
        return 1;
    }

    /// <summary>Re-runs a recording through the splitter without touching the network.</summary>
    private static int Replay(string capturePath)
    {
        var events = new List<(string Kind, long At, string Text)>();

        foreach (string line in File.ReadAllLines(capturePath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            string[] parts = line.Split('	');
            if (parts.Length < 3)
                continue;

            events.Add((parts[0], long.TryParse(parts[1], out long at) ? at : 0, parts[^1]));
        }

        return PrintReplay(events, capturePath) ? 0 : 1;
    }

    private static string Render(IEnumerable<(string Kind, long At, string Text)> events)
    {
        var text = new StringBuilder();
        foreach (var (kind, at, value) in events)
            text.Append(kind).Append('\t').Append(at).Append('\t')
                .Append(value.Replace("\n", " ")).Append('\n');

        return text.ToString();
    }

    /// <summary>Runs the capture through the same splitter the app uses, so a regression is visible here.</summary>
    private static bool PrintReplay(IReadOnlyList<(string Kind, long At, string Text)> events, string capturePath)
    {
        var transcript = new LiveTranscript();
        var committed = new List<string>();
        string stable = string.Empty;

        foreach (var (kind, _, text) in events)
        {
            string stableNow = kind == "final" ? text : stable;
            committed.AddRange(transcript.Accept(text, stableNow, kind == "final"));

            if (kind == "final")
                stable = text;
        }

        Console.WriteLine();
        Console.WriteLine($"committed {committed.Count} sentences:");
        foreach (string sentence in committed)
            Console.WriteLine($"  {sentence}");

        string final = events.LastOrDefault(e => e.Kind == "final").Text ?? string.Empty;
        bool assembles = string.Concat(committed) == final;
        bool duplicated = committed.Count != committed.Distinct().Count();
        bool nested = committed.Zip(committed.Skip(1)).Any(pair =>
            pair.Second.StartsWith(pair.First, StringComparison.Ordinal));
        bool liveLeft = transcript.Pending.Length > 0;

        Console.WriteLine();
        Console.WriteLine($"assembles final transcript: {assembles}");
        Console.WriteLine($"duplicate sentence: {duplicated}");
        Console.WriteLine($"line begins with previous: {nested}");
        Console.WriteLine($"live text left: '{transcript.Pending}'");

        string linesPath = Path.ChangeExtension(capturePath, ".lines.txt");
        File.WriteAllLines(linesPath, committed, new UTF8Encoding(false));
        Console.WriteLine($"lines -> {linesPath}");

        return assembles && !duplicated && !nested && !liveLeft;
    }
}

/// <summary>Plays a WAV buffer in 40 ms chunks, paced like a real capture device.</summary>
internal sealed class WavAudioSource : IAudioSource
{
    private readonly byte[] pcm;

    public WavAudioSource(byte[] pcm) => this.pcm = pcm;

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

    public async Task EmitAllAsync(CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();

        for (int offset = 0; offset < pcm.Length; offset += AudioFormat.ChunkBytes)
        {
            int count = Math.Min(AudioFormat.ChunkBytes, pcm.Length - offset);
            var chunk = new byte[count];
            Array.Copy(pcm, offset, chunk, 0, count);

            FrameAvailable?.Invoke(this, new AudioFrameEventArgs(chunk));

            // Real time, so the service's VAD sees the same pauses a microphone would produce.
            double dueMs = (offset + count) * 1000.0 / (AudioFormat.SampleRate * 2);
            int wait = (int)Math.Round(dueMs - clock.Elapsed.TotalMilliseconds);
            if (wait > 0)
                await Task.Delay(wait, cancellationToken);
        }
    }
}

/// <summary>Everything the provider published, with the wall-clock time it arrived.</summary>
internal sealed class RecordingSink : IAsrSink
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly object gate = new();

    public List<(string Kind, long At, string Text)> Events { get; } = [];

    public List<string> Notices { get; } = [];

    public void OnPartial(string text, string? stableText = null)
    {
        lock (gate)
            Events.Add(("partial", clock.ElapsedMilliseconds, text));
    }

    public void OnFinal(string text, string? stableText = null)
    {
        lock (gate)
            Events.Add(("final", clock.ElapsedMilliseconds, text));
    }

    public void OnNotice(string message)
    {
        lock (gate)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            Notices.Add(message);
        }

        Console.Error.WriteLine("notice: " + message);
    }

    public void OnError(Exception error)
    {
        lock (gate)
            Events.Add(("error", clock.ElapsedMilliseconds, error.Message));
    }
}
