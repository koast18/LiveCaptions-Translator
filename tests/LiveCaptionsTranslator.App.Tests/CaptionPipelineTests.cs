using System.IO;
using System.Diagnostics;

using LiveCaptionsTranslator.Core.Translation;
using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.App.Tests;

/// <summary>
/// Drives the real caption pipeline - <see cref="Translator.HandleCaptionText"/> through the
/// translation queue to the rolling list - with the stream the Doubao backend produces: a segment
/// that is revised while it is being recognised, finals that settle it, and a growing stable prefix
/// (tests/fixtures/doubao-partials.txt is a recording of the real endpoint).
/// <para>
/// The tests share one process-wide <see cref="Caption"/>, so this class is the only writer and the
/// test assembly runs its classes sequentially (see AssemblyInfo.cs).
/// </para>
/// </summary>
public class CaptionPipelineTests
{
    private const string EchoId = "TestEcho";

    /// <summary>
    /// Holds every translation until the test releases it, so the recognised text can be observed
    /// exactly as the page shows it before a translation lands.
    /// </summary>
    private static readonly SemaphoreSlim Gate = new(0, int.MaxValue);

    /// <summary>Stands in for a translation engine: the sentence with a marker, so a translation is unmistakable.</summary>
    private sealed class GatedEchoProvider : ITranslationProvider
    {
        public string Id => EchoId;

        public string DisplayName => "Echo";

        public bool RequiresConfiguration => false;

        public bool IsLlmBased => false;

        public async Task<string> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
        {
            await Gate.WaitAsync(cancellationToken);
            return "T:" + request.Text;
        }
    }

    private static bool registered;
    private static bool loopsStarted;

    private static Caption Caption => Translator.Caption!;

    private static void Prepare()
    {
        if (!registered)
        {
            registered = true;

            if (!Translator.TranslationProviders.IsRegistered(EchoId))
                Translator.TranslationProviders.Register(EchoId, () => new GatedEchoProvider());
        }

        Translator.Setting!.ApiName = EchoId;
        Translator.Setting.TargetLanguage = "en";
        Translator.Setting.AsrLanguage = "en-US";
        Translator.Setting.ContextAware = false;
    }

    private static void ResetDisplay()
    {
        Translator.ResetTranscription();

        var caption = Caption;
        caption.ClearRecentLines();
        caption.InterimText = null;
        caption.DisplayTranslatedCaption = string.Empty;
        caption.OriginalCaption = string.Empty;
        caption.TranslatedCaption = string.Empty;
        caption.TranslationRedundant = false;
        caption.Notice = string.Empty;
        caption.RebuildRollingLines(0);
    }

    private static void StartLoops()
    {
        if (loopsStarted)
            return;

        loopsStarted = true;
        _ = Task.Run(Translator.TranslateLoop);
        _ = Task.Run(Translator.DisplayLoop);
    }

    private static List<string> Displayed() => Caption.RollingLines.Select(line => line.Text).ToList();

    /// <summary>One provider callback: the whole transcript, the settled part of it, and whether it is final.</summary>
    private static void Feed(string transcript, string stableText, bool isFinal = false) =>
        Translator.HandleCaptionText(transcript, isFinal, stableText);

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
                return true;

            await Task.Delay(20);
        }

        return condition();
    }

    private static void AssertNoDuplicateLines(IReadOnlyList<string> shown)
    {
        Assert.Equal(shown.Count, shown.Distinct().Count());

        for (int i = 1; i < shown.Count; i++)
        {
            Assert.False(shown[i].StartsWith(shown[i - 1], StringComparison.Ordinal),
                $"line {i} repeats the beginning of the previous one: '{shown[i - 1]}' -> '{shown[i]}'");
        }
    }

    [Fact]
    public async Task The_doubao_stream_is_shown_once_in_order_and_without_waiting()
    {
        Prepare();
        ResetDisplay();
        StartLoops();

        // Segment 1 is being recognised: it can still change, so nothing is finished yet.
        // The recogniser first hears 汽, which its final pass will correct to 气.
        Feed("今天天汽", stableText: "");
        Feed("今天天汽不错，我们一起去公园散步吧。", stableText: "");

        Assert.Equal("今天天汽不错，我们一起去公园散步吧。", Caption.InterimText);
        Assert.Empty(Displayed());

        // The segment is final: the corrected sentence is on screen at once - before any translation,
        // and exactly once, with no trace of the mis-heard version.
        const string First = "今天天气不错，我们一起去公园散步吧。";
        Feed(First, stableText: First, isFinal: true);

        Assert.Equal([First], Displayed());
        Assert.Null(Caption.InterimText);

        // Segment 2 starts. Its partial text is the live line; a full stop inside a segment that can
        // still change must not become a finished line.
        const string Second = "这个项目的字幕不能重复，延迟要尽量小。";
        Feed(First + "这个项目的字幕", stableText: First);
        Assert.Equal([First], Displayed());
        Assert.Equal("这个项目的字幕", Caption.InterimText);

        Feed(First + Second, stableText: First);
        Assert.Equal([First], Displayed());

        Feed(First + Second, stableText: First + Second, isFinal: true);
        Assert.Equal([First, Second], Displayed());

        // Segment 3 is settled. It must not be glued to the next one.
        const string Third = "他说他明天要去北京出差。";
        Feed(First + Second + "他说他明天要去", stableText: First + Second);
        Feed(First + Second + Third, stableText: First + Second + Third, isFinal: true);

        Assert.Equal([First, Second, Third], Displayed());

        // A recycled session re-sends the whole transcript. Nothing may be shown twice.
        Feed(First + Second + Third, stableText: First + Second + Third, isFinal: true);
        Assert.Equal([First, Second, Third], Displayed());

        AssertNoDuplicateLines(Displayed());
        Assert.DoesNotContain(Displayed(), line => line == Caption.InterimText);

        // Let the translations run. Each one must land on its own line, in place, in order.
        Gate.Release(3);

        Assert.True(
            await WaitUntilAsync(() => Displayed().Count == 3 && Displayed().All(line => line.StartsWith("T:", StringComparison.Ordinal))),
            "the translations never all arrived: " + string.Join(" | ", Displayed()));

        Assert.Equal(["T:" + First, "T:" + Second, "T:" + Third], Displayed());

        // Default settings for a Chinese session: the translation is skipped because the source is
        // already in the target language. The finished sentence must still be shown once - not the
        // live partial in its place, and not the same text in two slots.
        Translator.Setting.TargetLanguage = "zh-CN";

        const string Fourth = "新的句子也要显示一次。";
        Feed(First + Second + Third + Fourth, stableText: First + Second + Third + Fourth, isFinal: true);

        Assert.True(
            await WaitUntilAsync(() => Caption.TranslationRedundant),
            "the redundant translation was never applied");

        var after = Displayed();
        Assert.Equal(4, after.Count);
        Assert.Equal("T:" + Third, after[2]);
        Assert.Equal(Fourth, after[3]);
        AssertNoDuplicateLines(after);
        Assert.DoesNotContain(after, line => line == Caption.InterimText);
    }

    /// <summary>
    /// Replays the recording of the real endpoint. The expected lines are the sentences the service
    /// finalised, in order: no line may be missing, repeated, or be the beginning of the next one.
    /// </summary>
    [Fact]
    public async Task The_recorded_doubao_stream_is_shown_exactly_once()
    {
        Prepare();
        ResetDisplay();
        StartLoops();

        string[] expected =
        [
            "今天天气不错。",
            "我们一起去公园散步吧。",
            "这个项目的字幕不能重复。",
            "颜值要尽量小。",
            "他说他明天。",
            "要去北京出差。",
            "大概 3 天。",
            "以后回来。",
        ];

        string stable = string.Empty;

        foreach (var (kind, text) in ReadCapture())
        {
            // The builder commits a segment when its final arrives, so for a final the settled text is
            // the transcript itself; for a partial it is the last final.
            string stableNow = kind == "final" ? text : stable;

            Feed(text, stableNow, isFinal: kind == "final");

            if (kind == "final")
                stable = text;

            Assert.DoesNotContain(Caption.RollingLines, line => line.Text == Caption.InterimText);
        }

        Assert.Null(Caption.InterimText);
        Assert.Equal(expected, Displayed());
        AssertNoDuplicateLines(Displayed());

        Gate.Release(expected.Length);

        Assert.True(
            await WaitUntilAsync(() => Displayed().Count == expected.Length &&
                                       Displayed().All(line => line.StartsWith("T:", StringComparison.Ordinal))),
            "the translations never all arrived: " + string.Join(" | ", Displayed()));

        Assert.Equal(expected.Select(line => "T:" + line), Displayed());
    }

    /// <summary>Reads the capture written by the integration job, one <c>kind \t at \t text</c> per line.</summary>
    private static IEnumerable<(string Kind, string Text)> ReadCapture()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "doubao-partials.txt");

        foreach (string line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            string[] parts = line.Split('\t');

            Assert.True(parts.Length >= 3, $"unexpected capture line: {line}");
            yield return (parts[0], parts[^1]);
        }
    }
}
