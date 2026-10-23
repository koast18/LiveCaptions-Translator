using System.IO;
using System.Text.RegularExpressions;

using LiveCaptionsTranslator.Core.Doubao;
using LiveCaptionsTranslator.Core.Text;

namespace LiveCaptionsTranslator.Core.Tests;

/// <summary>
/// Replays the raw segment streams recorded from the real Doubao endpoint for four Bilibili clips
/// (a law lecture, a crime-law answer, a round-table conversation and an English TED talk).
/// <para>
/// These are the recordings that exposed the start-time jitter, the re-decoded window and the
/// mid-sentence final. The test drives the production builder and splitter exactly as
/// <c>DoubaoAsrProvider</c> does and asserts the property the user cares about: every spoken
/// sentence is shown once, in order, with nothing lost or repeated.
/// </para>
/// </summary>
public class BilibiliCaptureReplayTests
{
    private static readonly Regex Segment = new(
        @"^\s*(?:(\w+)\s+)?start=([\d.-]+) end=([\d.-]+) interim=(\w+) vad=(\w+) force=(\w+) " +
        @"timeout=(\w+) finish=(\w+) text=(.*)$",
        RegexOptions.Compiled);

    public static TheoryData<string> Captures => new()
    {
        "bili-luoxiang-reading",
        "bili-luoxiang-crime",
        "bili-roundtable",
        "bili-ted-phone",
    };

    [Theory]
    [MemberData(nameof(Captures))]
    public void Each_recorded_bilibili_clip_is_shown_once_in_order(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", name + ".wire.txt");
        Assert.True(File.Exists(path), $"missing capture: {path}");

        var builder = new DoubaoTranscriptBuilder();
        var transcript = new LiveTranscript();
        var lines = new List<string>();
        string settled = string.Empty;

        foreach (var segment in Read(path))
        {
            var (text, isFinal) = builder.Append(segment);

            // Exactly what the provider hands the pipeline: the whole transcript and the part of it
            // the builder considers settled.
            foreach (var update in transcript.Accept(text, builder.Committed, isFinal))
            {
                if (update.Replaces is not null)
                {
                    int at = lines.LastIndexOf(update.Replaces);
                    Assert.True(at >= 0,
                        $"a revision of '{update.Replaces}' has no line on screen");
                    lines[at] = update.Text;
                }
                else
                {
                    lines.Add(update.Text);
                }
            }

            if (isFinal)
                settled = text;
        }

        Assert.NotEmpty(lines);

        // Nothing lost, nothing added: the displayed lines reassemble the transcript the recogniser
        // settled on (a display line is its own line, so whitespace between them is not significant).
        Assert.Equal(WithoutWhitespace(settled), WithoutWhitespace(string.Concat(lines)));

        // A sentence may be repeated by the speaker ("那个", "那个"), but a long block repeated or a
        // line that is the beginning of the next one is the duplicate that was reported.
        var longLines = lines.Where(line => line.Length > 4).ToList();
        Assert.Equal(longLines.Count, longLines.Distinct().Count());

        for (int i = 1; i < lines.Count; i++)
        {
            Assert.False(
                lines[i].Length > lines[i - 1].Length &&
                lines[i].StartsWith(lines[i - 1], StringComparison.Ordinal) &&
                lines[i - 1].Length >= 4,
                $"line {i} repeats the beginning of the previous one: '{lines[i - 1]}' -> '{lines[i]}'");
        }
    }

    private static IEnumerable<DoubaoResultSegment> Read(string path)
    {
        foreach (string line in File.ReadAllLines(path))
        {
            Match match = Segment.Match(line.TrimEnd());
            if (!match.Success)
                continue;

            yield return new DoubaoResultSegment(
                Text: match.Groups[9].Value,
                IsInterim: match.Groups[4].Value == "True",
                IsVadFinished: match.Groups[5].Value == "True",
                IsForceFinished: match.Groups[6].Value == "True",
                IsVadTimeout: match.Groups[7].Value == "True",
                IsStreamFinish: match.Groups[8].Value == "True")
            {
                StartTime = double.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                EndTime = double.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture),
            };
        }
    }

    private static string WithoutWhitespace(string text) =>
        string.Concat(text.Where(character => !char.IsWhiteSpace(character)));
}
