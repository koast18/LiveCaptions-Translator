using System.Diagnostics;

using LiveCaptionsTranslator.Core.Translation;
using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.App.Tests;

/// <summary>
/// Drives the real caption pipeline - <see cref="Translator.HandleCaptionText"/> through the
/// translation queue to the rolling list - with the stream the Doubao backend produces: a segment
/// that is revised while it is being recognised, a final that corrects it, and a growing stable
/// prefix. What the page must show is the corrected sentence, once, immediately.
/// <para>
/// The tests share one process-wide <see cref="Caption"/>, so this class is the only writer and the
/// test assembly runs its classes sequentially (see AssemblyInfo.cs).
/// </para>
/// </summary>
public class CaptionPipelineTests
{
    private const string EchoId = "TestEcho";

    /// <summary>Stands in for a translation engine: the sentence with a marker, so a translation is unmistakable.</summary>
    private sealed class EchoProvider : ITranslationProvider
    {
        public string Id => EchoId;

        public string DisplayName => "Echo";

        public bool RequiresConfiguration => false;

        public bool IsLlmBased => false;

        public Task<string> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult("T:" + request.Text);
    }

    private static bool prepared;
    private static bool loopsStarted;

    private static Caption Caption => Translator.Caption!;

    private static void Prepare()
    {
        if (prepared)
            return;

        prepared = true;

        if (!Translator.TranslationProviders.IsRegistered(EchoId))
            Translator.TranslationProviders.Register(EchoId, () => new EchoProvider());

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

    [Fact]
    public async Task The_doubao_stream_is_shown_once_in_order_and_without_waiting()
    {
        Prepare();
        ResetDisplay();

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

        // Segment 3 is settled without punctuation. It is still a sentence - the recogniser simply did
        // not punctuate it - and must not be glued to the next one.
        const string Third = "他说他明天要去北京出差";
        Feed(First + Second + "他说他明天要去", stableText: First + Second);
        Feed(First + Second + Third, stableText: First + Second + Third, isFinal: true);

        Assert.Equal([First, Second, Third], Displayed());

        // A recycled session re-sends the whole transcript. Nothing may be shown twice.
        Feed(First + Second + Third, stableText: First + Second + Third, isFinal: true);
        Assert.Equal([First, Second, Third], Displayed());

        // The reported bugs, as invariants: no line is repeated, and no line is the beginning of the
        // next one (the "prefix line now, whole sentence later" duplicate).
        var shown = Displayed();
        Assert.Equal(shown.Count, shown.Distinct().Count());
        for (int i = 1; i < shown.Count; i++)
            Assert.False(shown[i].StartsWith(shown[i - 1], StringComparison.Ordinal),
                $"line {i} repeats the beginning of the previous one: '{shown[i - 1]}' -> '{shown[i]}'");

        // The live line has its own slot: it is never also listed as a finished sentence.
        Assert.DoesNotContain(shown, line => line == Caption.InterimText);

        // Now let the translations run. Each one must land on its own line, in place, in order.
        StartLoops();

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
        Assert.Equal(after.Count, after.Distinct().Count());
        Assert.DoesNotContain(after, line => line == Caption.InterimText);
    }
}
