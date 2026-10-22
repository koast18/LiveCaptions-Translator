using LiveCaptionsTranslator.Core.Text;

namespace LiveCaptionsTranslator.Core.Tests;

/// <summary>
/// Cases come from the recording of the real endpoints (tests/fixtures/transcript-partials.txt and
/// doubao-partials.txt) and from the failures a user reported: a backend that never punctuates, a
/// transcript that is re-reported so sentences stacked up, and a final pass that corrects a
/// sentence after it was already shown.
/// </summary>
public class LiveTranscriptTests
{
    private static string[] Texts(IEnumerable<TranscriptUpdate> updates) =>
        updates.Where(update => !update.Retract).Select(update => update.Text).ToArray();

    [Fact]
    public void The_recorded_stream_yields_exactly_one_sentence()
    {
        var transcript = new LiveTranscript();
        var committed = new List<TranscriptUpdate>();

        foreach (var (kind, text) in Recorded())
        {
            committed.AddRange(transcript.Accept(text, isFinal: kind == "final"));
        }

        string expected = Recorded().Last().Text;
        Assert.Contains(expected, Texts(committed));
        Assert.All(Texts(committed), line => Assert.False(string.IsNullOrWhiteSpace(line)));
        Assert.Empty(transcript.Pending);
    }

    [Fact]
    public void A_backend_that_never_punctuates_is_split_at_pauses()
    {
        string text = string.Concat(Enumerable.Repeat("他说这个系列会收集那些网络小说里真实存在的内容非常离谱非常夸张，", 4));

        var transcript = new LiveTranscript();
        var committed = new List<TranscriptUpdate>();

        for (int length = 2; length <= text.Length; length += 2)
            committed.AddRange(transcript.Accept(text[..length], isFinal: false));

        committed.AddRange(transcript.Accept(text, isFinal: true));

        string[] lines = Texts(committed);
        Assert.True(lines.Length > 1, "expected the text to be split");
        Assert.All(lines, line => Assert.False(string.IsNullOrWhiteSpace(line)));
        Assert.All(lines, line => Assert.True(line.Length <= LiveTranscript.MaxLineChars + 2, $"{line.Length} chars"));
        Assert.Equal(text, string.Concat(lines));
    }

    [Fact]
    public void Replaying_the_same_cumulative_text_never_commits_twice()
    {
        const string Sentence = "他说这个系列会收集那些网络小说里真实存在的内容非常离谱非常夸张。";
        var transcript = new LiveTranscript();

        // A written full stop finishes the sentence immediately - waiting for the final would only add
        // latency. What must never happen is committing it again on the replay.
        Assert.Equal([Sentence], Texts(transcript.Accept(Sentence, isFinal: false)));
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

        // Different words: a new session, not a correction of what is on screen.
        Assert.Empty(committed);
        Assert.Equal("重新开始的一段", transcript.Pending);
    }

    [Fact]
    public void A_final_without_punctuation_still_commits_what_was_said()
    {
        var transcript = new LiveTranscript();

        transcript.Accept("这句话没有句号", isFinal: false);
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

        Assert.Empty(transcript.Accept(input, isFinal: false));
        Assert.All(Texts(transcript.Accept(input, isFinal: true)), line => Assert.False(string.IsNullOrWhiteSpace(line)));
    }

    [Fact]
    public void A_single_character_is_kept_until_the_segment_ends()
    {
        var transcript = new LiveTranscript();

        Assert.Empty(transcript.Accept("我", isFinal: false));
        Assert.Equal(["我"], Texts(transcript.Accept("我", isFinal: true)));
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
        Assert.Equal(["缺钱了。"], Texts(replay));
        Assert.Empty(replay.Where(update => update.Replaces is not null));
    }

    [Fact]
    public void A_whole_session_resent_at_the_end_is_dropped()
    {
        // What a session end can look like: one final carrying everything that was already said.
        string[] sentences = ["第一句话。", "第二句话。", "第三句话。"];

        var transcript = new LiveTranscript();
        var committed = new List<TranscriptUpdate>();
        foreach (string sentence in sentences)
            committed.AddRange(transcript.Accept(sentence, isFinal: true));

        Assert.Equal(sentences, Texts(committed));

        // The service then repeats the lot in one final.
        var replay = transcript.Accept(string.Concat(sentences), isFinal: true);

        Assert.Empty(replay);
    }

    [Fact]
    public void A_short_unconfirmed_line_is_left_alone()
    {
        var transcript = new LiveTranscript();
        transcript.Accept("还在说", isFinal: false);

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
        var committed = new List<TranscriptUpdate>();

        committed.AddRange(transcript.Accept(Part, isFinal: false));
        Assert.Empty(committed);

        committed.AddRange(transcript.Accept(Full, isFinal: true));

        Assert.Equal([Full], Texts(committed));
        Assert.Single(committed);
    }

    [Fact]
    public void A_corrected_sentence_replaces_the_one_already_shown()
    {
        // The third pass fixes a mis-heard character after the sentence was committed on its full
        // stop. Both the old line and the correction on screen would be the reported duplicate.
        const string Heard = "今天天汽不错，我们一起去公园散步吧。";
        const string Corrected = "今天天气不错，我们一起去公园散步吧。";

        var transcript = new LiveTranscript();
        var first = transcript.Accept(Heard, isFinal: false);

        Assert.Equal([Heard], Texts(first));

        var revision = transcript.Accept(Corrected, isFinal: true);

        Assert.Equal([Corrected], Texts(revision));
        Assert.Equal(Heard, revision[0].Replaces);
        Assert.Equal([Corrected], transcript.Committed);
    }

    [Fact]
    public void A_sentence_that_grows_after_being_committed_replaces_itself()
    {
        // A live revision of the shape "嗯。" -> "嗯，我们开始吧。": the sentence was committed at the
        // first full stop and the recogniser then extended it.
        var transcript = new LiveTranscript();

        Assert.Equal(["嗯。"], Texts(transcript.Accept("嗯。", isFinal: false)));

        var revision = transcript.Accept("嗯，我们开始吧。", isFinal: false);

        Assert.Equal(["嗯，我们开始吧。"], Texts(revision));
        Assert.Equal("嗯。", revision[0].Replaces);
    }

    [Fact]
    public void A_revision_that_deletes_a_sentence_retracts_it()
    {
        const string First = "第一句话。第二句话。";

        var transcript = new LiveTranscript();
        Assert.Equal(["第一句话。", "第二句话。"], Texts(transcript.Accept(First, isFinal: false)));

        // The service re-sends the segment without the second sentence.
        var revision = transcript.Accept("第一句话。", isFinal: true);

        var retracted = revision.Where(update => update.Retract).Select(update => update.Text);
        Assert.Contains("第二句话。", retracted);
        Assert.DoesNotContain("第二句话。", transcript.Committed);
    }

    [Fact]
    public void The_same_sentence_said_twice_is_committed_twice()
    {
        // "谢谢大家。" twice in a row is speech, not a replay: the second one must not be swallowed.
        var transcript = new LiveTranscript();

        Assert.Equal(["谢谢大家。"], Texts(transcript.Accept("谢谢大家。", isFinal: true)));
        Assert.Equal(["谢谢大家。"], Texts(transcript.Accept("谢谢大家。谢谢大家。", isFinal: true)));
    }
}
