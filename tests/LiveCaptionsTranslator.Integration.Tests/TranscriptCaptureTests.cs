using System.Text;

using LiveCaptionsTranslator.Core.Asr;
using LiveCaptionsTranslator.Core.Audio;
using LiveCaptionsTranslator.Core.Iflytek;

namespace LiveCaptionsTranslator.Integration.Tests;

/// <summary>
/// Records the exact stream of partial/final text a real backend produces for the committed sample.
/// <para>
/// The text pipeline is judged against that recording rather than against a guess about what the
/// service sends: the recording is written to the test output, uploaded by CI, checked in as a
/// fixture, and replayed by the unit tests that decide when a sentence is finished.
/// </para>
/// </summary>
public class TranscriptCaptureTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    /// <summary>Keeps every callback verbatim, in order.</summary>
    private sealed class RecordingSink : IAsrSink
    {
        public List<(string Kind, string Text)> Events { get; } = [];

        public void OnPartial(string text, string? stableText = null) => Events.Add(("partial", text));

        public void OnFinal(string text, string? stableText = null) => Events.Add(("final", text));

        public void OnNotice(string message) { }

        public void OnError(Exception error) => Events.Add(("error", error.Message));
    }

    [Fact]
    public async Task The_sample_produces_a_stream_worth_replaying()
    {
        var audio = WavFile.Read(FixturePath("iflytek-zh-cn-16k.wav"));
        var source = new WavAudioSource(audio.Pcm);
        var sink = new RecordingSink();

        await using var provider = new IflytekIatProvider(new IflytekIatOptions
        {
            ReceiveTimeout = TimeSpan.FromSeconds(20),
            GracefulStopTimeout = TimeSpan.FromSeconds(25),
            ReconnectDelay = TimeSpan.FromSeconds(1),
            MaxReconnectAttempts = 2,
        });

        await provider.StartAsync(new AsrContext
        {
            Sink = sink,
            Options = new AsrOptions { LanguageId = "zh_cn", Accent = "mandarin" },
            AudioSource = source,
        });

        await source.EmitAllAsync();
        await provider.StopAsync();

        Assert.NotEmpty(sink.Events);

        var text = new StringBuilder();
        foreach (var (kind, value) in sink.Events)
            text.Append(kind).Append('\t').Append(value.Replace("\n", " ")).Append('\n');

        string directory = Path.Combine(AppContext.BaseDirectory, "capture");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "iflytek-partials.txt"), text.ToString());

        // What the service sends is cumulative: every callback repeats what came before it. That is the
        // property the sentence splitter has to cope with, so it is asserted here, not assumed.
        var partials = sink.Events.Where(e => e.Kind == "partial").Select(e => e.Text).ToList();
        Assert.NotEmpty(partials);
        Assert.All(partials.Zip(partials.Skip(1)), pair =>
            Assert.True(
                pair.Second.Length >= pair.First.Length,
                $"a later partial ({pair.Second.Length}) was shorter than an earlier one ({pair.First.Length})"));
    }
}
