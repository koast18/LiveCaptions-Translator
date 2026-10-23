using System.IO;
using LiveCaptionsTranslator.Core.Text;

namespace LiveCaptionsTranslator.Core.Tests;

/// <summary>
/// Replays the recording of the real Doubao endpoint (tests/fixtures/doubao-partials.txt).
/// <para>
/// The service settles one segment at a time and re-sends the whole transcript on every callback,
/// so every final is one more sentence. This is the ground truth the display pipeline is judged
/// against: each final becomes exactly one line, in its final wording.
/// </para>
/// </summary>
public class DoubaoCaptureReplayTests
{
    private static IEnumerable<(string Kind, string Text)> Capture()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "doubao-partials.txt");
        Assert.True(File.Exists(path), $"missing capture: {path}");

        foreach (string line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            string[] parts = line.Split('\t');
            Assert.True(parts.Length >= 3, $"unexpected capture line: {line}");
            yield return (parts[0], parts[^1]);
        }
    }

    [Fact]
    public void Each_final_of_the_recorded_stream_becomes_exactly_one_line()
    {
        var transcript = new LiveTranscript();
        var committed = new List<string>();
        string stable = string.Empty;

        foreach (var (kind, text) in Capture())
        {
            string stableNow = kind == "final" ? text : stable;
            committed.AddRange(transcript.Accept(text, stableNow, kind == "final"));

            if (kind == "final")
                stable = text;
        }

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

        Assert.Equal(expected, committed);
        Assert.Empty(transcript.Pending);

        // The lines reassemble the final transcript exactly: nothing was dropped, nothing was added.
        Assert.Equal(string.Concat(expected), string.Concat(committed));
    }

    [Fact]
    public void A_corrected_segment_never_appears_in_its_first_wording()
    {
        // The first callback of a segment is only the beginning: the service's final pass extends and
        // fixes it. Committing the partial would show a prefix line and then the whole sentence again.
        var transcript = new LiveTranscript();

        Assert.Empty(transcript.Accept("今天", stableText: ""));
        Assert.Equal("今天", transcript.Pending);

        var committed = transcript.Accept("今天天气不错。", stableText: "今天天气不错。", isFinal: true);

        Assert.Equal(["今天天气不错。"], committed);
        Assert.DoesNotContain("今天", transcript.Committed);
    }
}
