using LiveCaptionsTranslator.Core.Text;

namespace LiveCaptionsTranslator.Core.Tests;

/// <summary>
/// Cases come from recordings of the real endpoints (tests/fixtures/transcript-partials.txt and
/// doubao-partials.txt) and from the failures a user reported: a backend that never punctuates, a
/// transcript that is re-reported so sentences stacked up, and a second/third pass that corrects a
/// sentence after it was first heard.
/// </summary>
public class LiveTranscriptTests
{
    private static string[] Texts(IEnumerable<TranscriptUpdate> updates) =>
        updates.Select(update => update.Text).ToArray();

    /// <summary>Verbatim from the capture of the committed iFlytek sample.</summary>
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

    // ---------------------------------------------------------------- streaming backends

    [Fact]
    public void The_recorded_stream_yields_exactly_one_sentence()
    {
        var transcript = new LiveTranscript();
        var committed = new List<string>();

        foreach (var (kind, text) in Recorded())
            committed.AddRange(Texts(transcript.Accept(text, isFinal: kind == "final")));

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
            committed.AddRange(Texts(transcript.Accept(text[..length])));

        committed.AddRange(Texts(transcript.Accept(text, isFinal: true)));

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
        Assert.Equal([Sentence], Texts(transcript.Accept(Sentence)));
        Assert.Empty(transcript.Accept(Sentence));
        Assert.Empty(transcript.Accept(Sentence, isFinal: true));

        // The next segment starts clean and does not re-commit the previous one.
        Assert.Empty(transcript.Accept(Sentence + "然后呢"));
    }

    [Fact]
    public void A_restarted_transcript_is_not_mistaken_for_new_text()
    {
        var transcript = new LiveTranscript();

        transcript.Accept("第一段话已经说完了。", isFinal: true);
        var committed = transcript.Accept("重新开始的一段");

        Assert.Empty(committed);
        Assert.Equal("重新开始的一段", transcript.Pending);
    }

    [Fact]
    public void A_final_without_punctuation_still_commits_what_was_said()
    {
        var transcript = new LiveTranscript();

        transcript.Accept("这句话没有句号");
        var committed = transcript.Accept("这句话没有句号", isFinal: true);

        Assert.Equal(["这句话没有句号"], Texts(committed));
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

        Assert.Empty(transcript.Accept(input));
        Assert.All(Texts(transcript.Accept(input, isFinal: true)), line => Assert.False(string.IsNullOrWhiteSpace(line)));
    }

    [Fact]
    public void A_single_character_is_kept_until_the_segment_ends()
    {
        var transcript = new LiveTranscript();

        Assert.Empty(transcript.Accept("我"));
        Assert.Equal(["我"], Texts(transcript.Accept("我", isFinal: true)));
    }

    [Fact]
    public void A_replayed_transcript_does_not_commit_the_same_sentences_again()
    {
        // What a recycled session looks like: the service starts over and re-sends what it already said.
        const string Block = "至少蹲三年。而且作为外国公民。他出狱后极大概率。会被遣返回国。";

        var transcript = new LiveTranscript();
        var first = transcript.Accept(Block, isFinal: true);
        Assert.Equal(4, first.Count);

        var replay = transcript.Accept(Block + "缺钱了。");

        // The replayed block is skipped; only what is genuinely new comes through.
        Assert.Equal(["缺钱了。"], Texts(replay));
    }

    [Fact]
    public void A_whole_session_resent_at_the_end_is_dropped()
    {
        // What a session end can look like: one final carrying everything that was already said.
        string[] sentences = ["第一句话。", "第二句话。", "第三句话。"];

        var transcript = new LiveTranscript();
        var committed = new List<string>();
        foreach (string sentence in sentences)
            committed.AddRange(Texts(transcript.Accept(sentence, isFinal: true)));

        Assert.Equal(sentences, committed);

        // The service then repeats the lot in one final.
        Assert.Empty(transcript.Accept(string.Concat(sentences), isFinal: true));
    }

    [Fact]
    public void A_short_unconfirmed_line_is_left_alone()
    {
        var transcript = new LiveTranscript();
        transcript.Accept("还在说");

        Assert.Null(transcript.TakePendingLine());
        Assert.Equal("还在说", transcript.Pending);
    }

    [Fact]
    public void A_comma_is_not_a_sentence_boundary()
    {
        // The reported duplicate: a line that was a prefix of the next one. It came from treating a pause
        // inside the sentence as the end of it.
        const string Part = "韩媒指挥体系将变为韩国军官担任总司令，负责指挥美国军官担任副司令，";
        const string Full = Part + "提供支援的结构。";

        var transcript = new LiveTranscript();

        Assert.Empty(transcript.Accept(Part));
        Assert.Equal([Full], Texts(transcript.Accept(Full, isFinal: true)));
    }

    [Fact]
    public void The_same_sentence_said_twice_is_committed_twice()
    {
        // "谢谢大家。" twice in a row is speech, not a replay: the second one must not be swallowed.
        var transcript = new LiveTranscript();

        Assert.Equal(["谢谢大家。"], Texts(transcript.Accept("谢谢大家。", isFinal: true)));
        Assert.Equal(["谢谢大家。"], Texts(transcript.Accept("谢谢大家。谢谢大家。", isFinal: true)));
    }

    // ---------------------------------------------------------------- backends that report stable text

    [Fact]
    public void Nothing_is_committed_while_the_segment_can_still_be_revised()
    {
        // Doubao sends the segment it is still working on; the builder reports no stable text until
        // the segment ends.
        var transcript = new LiveTranscript();

        Assert.Empty(transcript.Accept("今天天气不错。", stableText: ""));
        Assert.Equal("今天天气不错。", transcript.Pending);
        Assert.Empty(transcript.Committed);
    }

    [Fact]
    public void A_corrected_sentence_is_committed_once_in_its_corrected_wording()
    {
        // The recogniser hears the first character wrong and its final pass fixes it. Because nothing
        // was committed while the sentence was unstable, only the corrected sentence reaches the page.
        var transcript = new LiveTranscript();

        Assert.Empty(transcript.Accept("今天天汽不错，我们一起去公园散步吧。", stableText: ""));
        Assert.Empty(transcript.Accept("今天天气不错，我们一起去公园散步吧。", stableText: ""));

        string final = "今天天气不错，我们一起去公园散步吧。";
        var committed = Texts(transcript.Accept(final, stableText: final, isFinal: true));

        Assert.Equal([final], committed);
        Assert.Empty(transcript.Pending);
        Assert.Equal([final], transcript.Committed);
    }

    [Fact]
    public void A_segment_is_committed_when_the_next_one_starts()
    {
        // The stable text grows as soon as the service moves on to the next segment; that is the
        // moment the previous sentence is shown and translated.
        const string First = "今天天气不错，我们一起去公园散步吧。";
        const string Second = "这个项目的字幕不能重复。";

        var transcript = new LiveTranscript();

        Assert.Empty(transcript.Accept(First, stableText: ""));

        // The second segment has started: everything before it is stable.
        var committed = Texts(transcript.Accept(First + "这个项目", stableText: First));

        Assert.Equal([First], committed);
        Assert.Equal("这个项目", transcript.Pending);

        committed = Texts(transcript.Accept(First + Second, stableText: First + Second, isFinal: true));

        Assert.Equal([Second], committed);
        Assert.Equal([First, Second], transcript.Committed);
    }

    [Fact]
    public void A_stable_final_without_punctuation_is_still_a_sentence()
    {
        var transcript = new LiveTranscript();

        Assert.Empty(transcript.Accept("这句话没有句号", stableText: ""));
        Assert.Equal(["这句话没有句号"], Texts(transcript.Accept("这句话没有句号", stableText: "这句话没有句号")));
    }

    [Fact]
    public void A_whole_replayed_transcript_is_not_committed_again()
    {
        const string Block = "至少蹲三年。而且作为外国公民。会被遣返回国。";

        var transcript = new LiveTranscript();
        Assert.Equal(3, transcript.Accept(Block, stableText: Block, isFinal: true).Count);

        // A recycled session re-sends the block from the start.
        Assert.Empty(transcript.Accept(Block, stableText: Block));
        Assert.Empty(transcript.Accept(Block, stableText: Block, isFinal: true));

        // Only what follows the replay is new.
        Assert.Equal(["缺钱了。"], Texts(transcript.Accept(Block + "缺钱了。", stableText: Block + "缺钱了。", isFinal: true)));
    }

    [Fact]
    public void An_english_sentence_ends_at_a_period_but_a_decimal_does_not()
    {
        // English recognition has no full-width stops; without the ASCII period it was one endless
        // sentence. A period inside a number must not split it.
        var transcript = new LiveTranscript();

        Assert.Equal(
            ["It costs 3.5 dollars.", "One more question!"],
            Texts(transcript.Accept("It costs 3.5 dollars. One more question!", isFinal: true)));
    }

    [Fact]
    public void A_trailing_period_waits_for_the_next_chunk()
    {
        var transcript = new LiveTranscript();

        Assert.Empty(transcript.Accept("It costs 3."));
        Assert.Empty(transcript.Accept("It costs 3.5"));
        Assert.Equal(["It costs 3.5 dollars."], Texts(transcript.Accept("It costs 3.5 dollars. ")));
    }

    [Fact]
    public void A_settled_sentence_extended_by_the_next_pass_replaces_itself()
    {
        // Recorded behaviour: a segment is finalised mid-sentence, then the accurate pass appends the
        // question mark and the next sentence to the same segment.
        var transcript = new LiveTranscript();

        Assert.Equal(
            ["就是你能，你能忍多大"],
            Texts(transcript.Accept("就是你能，你能忍多大", stableText: "就是你能，你能忍多大", isFinal: true)));

        var updates = transcript.Accept(
            "就是你能，你能忍多大？是不是有点怂啊？",
            stableText: "就是你能，你能忍多大？是不是有点怂啊？",
            isFinal: true);

        Assert.Equal(["就是你能，你能忍多大？", "是不是有点怂啊？"], Texts(updates));
        Assert.Equal("就是你能，你能忍多大", updates[0].Replaces);
        Assert.Null(updates[1].Replaces);
        Assert.Equal(["就是你能，你能忍多大？", "是不是有点怂啊？"], transcript.Committed);
    }

    [Fact]
    public void A_mis_heard_word_corrected_after_the_sentence_was_shown_replaces_it()
    {
        var transcript = new LiveTranscript();

        transcript.Accept("颜值要尽量小。", stableText: "颜值要尽量小。", isFinal: true);

        var updates = transcript.Accept(
            "延迟要尽量小。", stableText: "延迟要尽量小。", isFinal: true);

        Assert.Equal(["延迟要尽量小。"], Texts(updates));
        Assert.Equal("颜值要尽量小。", updates[0].Replaces);
        Assert.Equal(["延迟要尽量小。"], transcript.Committed);
    }

    [Fact]
    public void A_stable_prefix_that_ends_mid_sentence_does_not_cut_a_word()
    {
        // The service can settle a segment without punctuation. It is a finished sentence, not half of
        // the next one, and the live line starts after it.
        var transcript = new LiveTranscript();

        var committed = Texts(transcript.Accept("今天天气不错我们", stableText: "今天天气不错"));

        Assert.Equal(["今天天气不错"], committed);
        Assert.Equal("我们", transcript.Pending);
    }
}
