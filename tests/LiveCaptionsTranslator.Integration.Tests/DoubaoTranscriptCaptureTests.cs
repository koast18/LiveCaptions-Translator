using System.Text;

using LiveCaptionsTranslator.Core.Asr;
using LiveCaptionsTranslator.Core.Audio;
using LiveCaptionsTranslator.Core.Doubao;

namespace LiveCaptionsTranslator.Integration.Tests;

/// <summary>
/// Records the exact partial/final stream the Doubao IME endpoint produces for a multi-sentence
/// sample, paced at real time so the service's VAD sees the same silence a person would.
/// <para>
/// The recording is the ground truth the display pipeline is judged against: it is uploaded by CI,
/// checked in as <c>doubao-partials.txt</c> and replayed by the app tests. Guessing what the service
/// sends is what produced the duplicate-line bugs in the first place.
/// </para>
/// </summary>
[Trait("Category", "Secondary")]
public class DoubaoTranscriptCaptureTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    /// <summary>Keeps every callback verbatim, in order, with the wall-clock time it arrived.</summary>
    private sealed class RecordingSink : IAsrSink
    {
        private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        private readonly object gate = new();

        public List<(string Kind, long AtMs, string Text)> Events { get; } = [];

        public void OnPartial(string text)
        {
            lock (gate)
                Events.Add(("partial", clock.ElapsedMilliseconds, text));
        }

        public void OnFinal(string text)
        {
            lock (gate)
                Events.Add(("final", clock.ElapsedMilliseconds, text));
        }

        public void OnNotice(string message) { }

        public void OnError(Exception error)
        {
            lock (gate)
                Events.Add(("error", clock.ElapsedMilliseconds, error.Message));
        }
    }

    [Fact]
    public async Task The_multisentence_sample_produces_a_recorded_stream()
    {
        var audio = WavFile.Read(FixturePath("doubao-zh-cn-multisentence-16k.wav"));
        var source = new WavAudioSource(audio.Pcm, pacePerChunk: TimeSpan.FromMilliseconds(40));
        var sink = new RecordingSink();

        var credentials = await new DoubaoDeviceRegistrar().RegisterAsync();

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
            Options = new AsrOptions(),
            AudioSource = source,
        });

        await source.EmitAllAsync();
        await provider.StopAsync();

        // The endpoint is flaky for freshly registered devices; an empty capture is a service
        // problem, but a partially written one is still evidence, so write it either way.
        var text = new StringBuilder();
        foreach (var (kind, at, value) in sink.Events)
            text.Append(kind).Append('\t').Append(at).Append('\t')
                .Append(value.Replace("\n", " ")).Append('\n');

        string directory = Path.Combine(AppContext.BaseDirectory, "capture");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "doubao-partials.txt"), text.ToString());

        Assert.NotEmpty(sink.Events);

        // Every callback carries the whole transcript so far. A callback that is shorter than the
        // previous one, without being a revision of it, is what the splitter cannot cope with.
        var texts = sink.Events.Where(e => e.Kind is "partial" or "final").Select(e => e.Text).ToList();
        Assert.NotEmpty(texts);
        Assert.All(texts.Zip(texts.Skip(1)), pair =>
            Assert.True(
                pair.Second.Length >= pair.First.Length || pair.First.StartsWith(pair.Second, StringComparison.Ordinal),
                $"transcript shrank without being a prefix revision: '{pair.First}' -> '{pair.Second}'"));
    }
}
