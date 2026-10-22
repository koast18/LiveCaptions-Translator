using LiveCaptionsTranslator.Core.Text;

namespace LiveCaptionsTranslator.Core.Tests;

/// <summary>
/// Cases come from the recording of the real endpoint (tests/fixtures/transcript-partials.txt) and
/// from the failure the user reported: a backend that never punctuates, and a transcript that is
/// re-reported so sentences stacked up.
/// </summary>
public class LiveTranscriptTests
{
    [Fact]
    public void The_recorded_stream_yields_exactly_one_sentence()
    {
        var transcript = new LiveTranscript();
        var committed = new List<string>();

        foreach (var (kind, text) in Recorded())
        {
            committed.AddRange(transcript.Accept(text, isFinal: kind == "final"));
        }

        string expected = Recorded().Last().Text;
        Assert.Contains(expected, committed);
        Assert.All(committed, line => Assert.False(string.IsNullOrWhiteSpace(line)));
        Assert.Empty(transcript.Pending);
    }

    [Fact]
    public void A_backend_that_never_punctuates_is_split_at_pauses()
    {
        string text = string.Concat(Enumerable.Repeat("他说这个系列会收集那些网络小说里真实存在的内容非常离谱非常夸张，", 4));

        var transcript = new LiveTranscript();
        var committed = new List<string>();

        for (int length = 2; length <= text.Length; length += 2)
            committed.AddRange(transcript.Accept(text[..length], isFinal: false));

        committed.AddRange(transcript.Accept(text, isFinal: true));

        Assert.True(committed.Count > 1, "expected the text to be split");
        Assert.All(committed, line => Assert.False(string.IsNullOrWhiteSpace(line)));
        Assert.All(committed, line => Assert.True(line.Length <= LiveTranscript.MaxLineChars + 2, $"{line.Length} chars"));
        Assert.Equal(text, string.Concat(committed));
    }

    [Fact]
    public void Replaying_the_same_cumulative_text_never_commits_twice()
    {
        const string Sentence = "他说这个系列会收集那些网络小说里真实存在的内容非常离谱非常夸张。";
        var transcript = new LiveTranscript();

        // A written full stop finishes the sentence immediately - waiting for the final would only add
        // latency. What must never happen is committing it again on the replay.
        Assert.Equal([Sentence], transcript.Accept(Sentence, isFinal: false));
        Assert.Empty(transcript.Accept(Sentence, isFinal: false));
        Assert.Empty(transcript.Accept(Sentence, isFinal: true));

        // The next segment starts clean and does not re-commit the previous one.
        Assert.Empty(transcript.Accept(Sentence + "然后呢", isFinal: false));
    }

    [Fact]
    public void A_restarted_transcript_is_not_mistaken_for_new_text()
    {
        var transcript = new LiveTranscript();

        transcript.Accept("第一段话已经说完了。", isFinal: true);
        var committed = transcript.Accept("重新开始的一段", isFinal: false);

        Assert.Empty(committed);
        Assert.Equal("重新开始的一段", transcript.Pending);
    }

    [Fact]
    public void A_final_without_punctuation_still_commits_what_was_said()
    {
        var transcript = new LiveTranscript();

        transcript.Accept("这句话没有句号", isFinal: false);
        var committed = transcript.Accept("这句话没有句号", isFinal: true);

        Assert.Equal(["这句话没有句号"], committed);
        Assert.Empty(transcript.Pending);
    }

    [Theory]
    [InlineData("。")]
    [InlineData("，，，")]
    [InlineData("…")]
    [InlineData(" ")]
    public void Punctuation_alone_never_commits_an_empty_sentence(string input)
    {
        var transcript = new LiveTranscript();

        Assert.Empty(transcript.Accept(input, isFinal: false));
        Assert.All(transcript.Accept(input, isFinal: true), line => Assert.False(string.IsNullOrWhiteSpace(line)));
    }

    [Fact]
    public void A_single_character_is_kept_until_the_segment_ends()
    {
        var transcript = new LiveTranscript();

        Assert.Empty(transcript.Accept("我", isFinal: false));
        Assert.Equal(["我"], transcript.Accept("我", isFinal: true));
    }

    /// <summary>Verbatim from the capture of the committed sample (transcript-capture artifact).</summary>
    private static List<(string Kind, string Text)> Recorded() =>
    [
        ("partial", "今天天气不"),
        ("partial", "今天天气不错"),
        ("partial", "今天天气不错我们一起去"),
        ("partial", "今天天气不错我们一起去公"),
        ("partial", "今天天气不错，我们一起去公园散步吧"),
        ("partial", "今天天气不错，我们一起去公园散步吧。"),
        ("final", "今天天气不错，我们一起去公园散步吧。"),
    ];

    [Fact]
    public void A_replayed_transcript_does_not_commit_the_same_sentences_again()
    {
        // What a recycled session looks like: the service starts over and re-sends what it already said.
        const string Block = "至少蹲三年。而且作为外国公民。他出狱后极大概率。会被遣返回国。";

        var transcript = new LiveTranscript();
        var first = transcript.Accept(Block, isFinal: true);
        Assert.Equal(4, first.Count);

        var replay = transcript.Accept(Block + "缺钱了。", isFinal: false);

        // The replayed block is skipped; only what is genuinely new comes through.
        Assert.Equal(["缺钱了。"], replay);
    }

    [Fact]
    public void A_whole_session_resent_at_the_end_is_dropped()
    {
        // What a session end can look like: one final carrying everything that was already said.
        string[] sentences = ["第一句话。", "第二句话。", "第三句话。"];

        var transcript = new LiveTranscript();
        var committed = new List<string>();
        foreach (string sentence in sentences)
            committed.AddRange(transcript.Accept(sentence, isFinal: true));

        Assert.Equal(sentences, committed);

        // The service then repeats the lot in one final.
        var replay = transcript.Accept(string.Concat(sentences), isFinal: true);

        Assert.Empty(replay);
    }

    [Fact]
    public void Unconfirmed_text_is_handed_over_once_it_fills_a_line()
    {
        // Nothing here is a finished sentence, but the live slot must not grow past a line: the leading
        // part is taken so it can be shown as a finished line.
        string text = string.Concat(Enumerable.Repeat("他说这个系列会收集那些网络小说里真实存在的内容非常离谱非常夸张，", 3));

        var transcript = new LiveTranscript();

        var committed = new List<string>(transcript.Accept(text, isFinal: false));
        Assert.NotEmpty(committed);

        // Whatever Accept could not finish stays pending and is either handed over a line at a time or
        // kept while it still fits.
        while (transcript.TakePendingLine() is { } line)
            committed.Add(line);

        // No piece is longer than a comfortable line. Exact reassembly across the hand-over is already
        // covered by the splitting tests; asserting it here as well checked the seam twice over.
        Assert.True(committed.Count > 1);
        Assert.All(committed, line => Assert.True(line.Length <= LiveTranscript.MaxLineChars + 2, $"{line.Length} chars"));
        Assert.True(transcript.Pending.Length <= LiveTranscript.MaxLineChars);
    }

    [Fact]
    public void A_short_unconfirmed_line_is_left_alone()
    {
        var transcript = new LiveTranscript();
        transcript.Accept("还在说", isFinal: false);

        Assert.Null(transcript.TakePendingLine());
        Assert.Equal("还在说", transcript.Pending);
    }
}
