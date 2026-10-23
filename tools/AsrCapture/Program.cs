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
            Console.Error.WriteLine("usage: AsrCapture <input.wav> <capture.txt> [--seconds N] [--vad MS] [--speed X] [--attempts N]");
            Console.Error.WriteLine("       AsrCapture --replay <capture.txt>");
            return 2;
        }

        string input = args[0];
        string output = args[1];

        double seconds = 0;
        int vad = 600;
        double speed = 1;
        int attempts = 6;
        for (int i = 2; i + 1 < args.Length; i++)
        {
            if (args[i] == "--seconds" && double.TryParse(args[i + 1], out double value))
                seconds = value;
            if (args[i] == "--vad" && int.TryParse(args[i + 1], out int window))
                vad = window;
            if (args[i] == "--speed" && double.TryParse(args[i + 1], out double rate) && rate > 0)
                speed = rate;
            if (args[i] == "--attempts" && int.TryParse(args[i + 1], out int count) && count > 0)
                attempts = count;
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

        string wirePath = Path.ChangeExtension(output, ".wire.txt");
        File.WriteAllText(wirePath, string.Empty, new UTF8Encoding(false));

        var credentials = await new DoubaoDeviceRegistrar().RegisterAsync();
        if (!credentials.IsComplete)
        {
            Console.Error.WriteLine("device registration did not return a usable credential");
            return 1;
        }

        // The endpoint rejects a freshly opened session now and then (50700000, service discovery),
        // sometimes for minutes at a time.

        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            var sink = new RecordingSink();
            var source = new WavAudioSource(pcm, speed);

            var wire = new WireLog(wirePath);

            await using var provider = new DoubaoAsrProvider(
                credentials,
                transportFactory: () => new LoggingTransport(new WebSocketDoubaoTransport(), wire),
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

            // A run that only produced notices (the endpoint answered 50700000 for the whole
            // window) is not evidence; retry it.
            if (sink.Events.Any(item => item.Kind is "partial" or "final"))
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

    /// <summary>The display puts each sentence on its own line, so joining spaces are not significant.</summary>
    private static string WithoutWhitespace(string text) =>
        string.Concat(text.Where(character => !char.IsWhiteSpace(character)));

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
        var committed = new List<string>();
        var sessions = new List<(int Lines, int Finals, bool Assembles)>();
        var session = new SessionState();

        foreach (var (kind, _, text) in events)
        {
            if (kind == "notice")
            {
                // A recycled or reconnected session starts a fresh transcript; the display keeps the
                // lines it already showed. Compare each session with its own final transcript.
                CloseSession(session, sessions);
                session = new SessionState();
                continue;
            }

            if (kind is not ("partial" or "final"))
                continue;

            string stableNow = kind == "final" ? text : session.Stable;

            foreach (var update in session.Transcript.Accept(text, stableNow, kind == "final"))
            {
                if (update.Replaces is not null)
                {
                    int at = session.Lines.LastIndexOf(update.Replaces);
                    if (at >= 0)
                        session.Lines[at] = update.Text;
                    else
                        session.Lines.Add(update.Text);

                    int index = committed.LastIndexOf(update.Replaces);
                    if (index >= 0)
                        committed[index] = update.Text;
                    else
                        committed.Add(update.Text);
                }
                else
                {
                    session.Lines.Add(update.Text);
                    committed.Add(update.Text);
                }
            }

            if (kind == "final")
            {
                session.Stable = text;
                session.Settled = text;
                session.Finals++;
            }
        }

        CloseSession(session, sessions);

        // A speaker can say the same sentence twice in a row; that is speech, not a duplicated line.
        bool duplicated = committed
            .Select((line, index) => (line, index))
            .Where(item => item.line.Length > 4)
            .GroupBy(item => item.line)
            .Any(group => group.Select(item => item.index).Max() - group.Select(item => item.index).Min() > 1);
        bool nested = committed.Zip(committed.Skip(1)).Any(pair =>
            pair.Second.Length > pair.First.Length &&
            pair.Second.StartsWith(pair.First, StringComparison.Ordinal) &&
            pair.First.Length >= 4);
        bool liveLeft = session.Transcript.Pending.Length > 0;
        bool assembles = sessions.All(item => item.Assembles);

        if (committed.Count <= 80)
        {
            Console.WriteLine();
            Console.WriteLine($"committed {committed.Count} sentences:");
            foreach (string sentence in committed)
                Console.WriteLine($"  {sentence}");
        }

        Console.WriteLine();
        Console.WriteLine($"sessions: {sessions.Count}, lines: {committed.Count}");
        for (int i = 0; i < sessions.Count; i++)
            Console.WriteLine($"  session {i + 1}: {sessions[i].Lines} lines, {sessions[i].Finals} finals, assembles={sessions[i].Assembles}");

        Console.WriteLine($"each session assembles its transcript: {assembles}");
        Console.WriteLine($"duplicate long sentence: {duplicated}");
        Console.WriteLine($"line begins with previous: {nested}");
        Console.WriteLine($"live text left: '{session.Transcript.Pending}'");

        string linesPath = Path.ChangeExtension(capturePath, ".lines.txt");
        File.WriteAllLines(linesPath, committed, new UTF8Encoding(false));
        Console.WriteLine($"lines -> {linesPath}");

        return assembles && !duplicated && !nested && !liveLeft;
    }

    private static void CloseSession(SessionState session, List<(int Lines, int Finals, bool Assembles)> sessions)
    {
        if (session.Finals == 0 && session.Lines.Count == 0)
            return;

        bool assembles = session.Finals == 0 ||
                         WithoutWhitespace(string.Concat(session.Lines)) == WithoutWhitespace(session.Settled);

        sessions.Add((session.Lines.Count, session.Finals, assembles));
    }

    private sealed class SessionState
    {
        public LiveTranscript Transcript { get; } = new();

        public List<string> Lines { get; } = [];

        public string Stable { get; set; } = string.Empty;

        public string Settled { get; set; } = string.Empty;

        public int Finals { get; set; }
    }
}

/// <summary>Records every result entry the service sends, with its segment metadata.</summary>
internal sealed class WireLog
{
    private readonly object gate = new();
    private readonly string path;

    public WireLog(string path) => this.path = path;

    public void Write(string line)
    {
        lock (gate)
            File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
    }
}

internal sealed class LoggingTransport : IDoubaoTransport
{
    private readonly IDoubaoTransport inner;
    private readonly WireLog wire;

    public LoggingTransport(IDoubaoTransport inner, WireLog wire)
    {
        this.inner = inner;
        this.wire = wire;
    }

    public bool IsOpen => inner.IsOpen;

    public Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken) =>
        inner.ConnectAsync(uri, headers, cancellationToken);

    public Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) =>
        inner.SendAsync(payload, cancellationToken);

    public async Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken)
    {
        byte[]? data = await inner.ReceiveAsync(cancellationToken);
        if (data is null)
        {
            wire.Write("closed");
            return null;
        }

        try
        {
            var response = DoubaoAsrResponse.Parse(data);
            foreach (var segment in DoubaoResultParser.Parse(response.ResultJson))
            {
                wire.Write(
                    $"{response.MessageType} start={segment.StartTime:0.###} end={segment.EndTime:0.###} " +
                    $"interim={segment.IsInterim} vad={segment.IsVadFinished} force={segment.IsForceFinished} " +
                    $"timeout={segment.IsVadTimeout} finish={segment.IsStreamFinish} text={segment.Text}");
            }
        }
        catch (Exception exception)
        {
            wire.Write($"unparsed {data.Length} bytes ({exception.GetType().Name})");
        }

        return data;
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}

/// <summary>Plays a WAV buffer in 40 ms chunks, paced like a real capture device.</summary>
internal sealed class WavAudioSource : IAudioSource
{
    private readonly byte[] pcm;
    private readonly double speed;

    public WavAudioSource(byte[] pcm, double speed = 1)
    {
        this.pcm = pcm;
        this.speed = speed <= 0 ? 1 : speed;
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
            // A speed above 1 is only for long recordings; the timestamps the provider sends still
            // advance at real time, so the service sees the same audio, just delivered faster.
            double dueMs = (offset + count) * 1000.0 / (AudioFormat.SampleRate * 2) / speed;
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
            Events.Add(("notice", clock.ElapsedMilliseconds, message));
        }

        Console.Error.WriteLine("notice: " + message);
    }

    public void OnError(Exception error)
    {
        lock (gate)
            Events.Add(("error", clock.ElapsedMilliseconds, error.Message));
    }
}
