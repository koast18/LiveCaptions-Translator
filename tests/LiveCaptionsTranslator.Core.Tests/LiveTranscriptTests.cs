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
        Assert.Single(committed);
        Assert.Equal(expected, committed[0]);
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

        Assert.Empty(transcript.Accept(Sentence, isFinal: false));
        Assert.Empty(transcript.Accept(Sentence, isFinal: false));
        Assert.Equal([Sentence], transcript.Accept(Sentence, isFinal: true));

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

    private static List<(string Kind, string Text)> Recorded()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "transcript-partials.txt");
        Assert.True(File.Exists(path), path);

        var events = new List<(string, string)>();
        foreach (string line in File.ReadAllLines(path))
        {
            int tab = line.IndexOf('\t');
            if (tab <= 0)
                continue;

            events.Add((line[..tab], line[(tab + 1)..]));
        }

        return events;
    }
}
