using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.App.Tests;

/// <summary>
/// The caption list is plain data - a list of finished lines plus the sentence on screen - so it can be
/// driven without a window. These tests cover what the page actually renders: the order, which line is
/// highlighted, corrections, repetitions and the line-length splitting.
/// </summary>
public class CaptionDisplayTests
{
    private static Caption Fresh()
    {
        var caption = Caption.GetInstance();
        caption.ClearRecentLines();
        caption.InterimText = null;
        caption.DisplayTranslatedCaption = string.Empty;
        caption.OriginalCaption = string.Empty;
        caption.TranslatedCaption = string.Empty;
        caption.TranslationRedundant = false;
        caption.Notice = string.Empty;
        caption.RebuildRollingLines(0);
        return caption;
    }

    [Fact]
    public void Finished_lines_are_listed_oldest_first_and_only_the_newest_is_highlighted()
    {
        var caption = Fresh();

        caption.AddRecentLine("第一句。");
        caption.AddRecentLine("第二句。");
        caption.DisplayTranslatedCaption = "第三句。";
        caption.RebuildRollingLines(0);

        Assert.Equal(["第一句。", "第二句。", "第三句。"], caption.RollingLines.Select(line => line.Text));
        Assert.Equal([false, false, true], caption.RollingLines.Select(line => line.IsLatest));
    }

    [Fact]
    public void The_sentence_on_screen_is_not_mixed_into_the_finished_lines()
    {
        var caption = Fresh();

        // The real flow: the finished sentence moves into the list, the new one becomes the live line.
        caption.AddRecentLine("已经说完的。");
        caption.DisplayTranslatedCaption = "正在说的。";
        caption.InterimText = "正在说";
        caption.RebuildRollingLines(0);

        // The live line has its own slot in the page; the list holds finished sentences plus the newest
        // finished one - never the half-heard text.
        Assert.Equal(["已经说完的。", "正在说的。"], caption.RollingLines.Select(line => line.Text));
        Assert.DoesNotContain(caption.RollingLines, line => line.Text == "正在说");
    }

    [Fact]
    public void A_line_that_repeats_an_earlier_one_is_kept()
    {
        var caption = Fresh();

        // The recogniser's replays are filtered before they get here. By this point a repeat is what
        // the speaker actually said, so dropping it would lose a subtitle.
        caption.AddRecentLine("说了一句。");
        caption.AddRecentLine("又说一句。");
        caption.AddRecentLine("说了一句。");
        caption.RebuildRollingLines(0);

        Assert.Equal(["说了一句。", "又说一句。", "说了一句。"], caption.RollingLines.Select(line => line.Text));
    }

    [Fact]
    public void The_same_words_said_twice_in_a_row_are_kept()
    {
        var caption = Fresh();

        caption.AddRecentLine("为啥呀？");
        caption.AddRecentLine("为啥呀？");
        caption.RebuildRollingLines(0);

        Assert.Equal(2, caption.RollingLines.Count);
        Assert.All(caption.RollingLines, line => Assert.Equal("为啥呀？", line.Text));
    }

    [Fact]
    public void A_long_line_is_broken_at_a_pause_and_nothing_is_lost()
    {
        var caption = Fresh();
        string long1 = string.Concat(Enumerable.Repeat("他说这个系列会收集那些网络小说里真实存在的内容非常离谱非常夸张，", 2).ToArray());

        caption.AddRecentLine(long1);
        caption.RebuildRollingLines(0);

        Assert.True(caption.RollingLines.Count > 1);

        // Everything but the tail of the sentence is history; the tail is the line being spoken.
        Assert.All(caption.RollingLines.Take(caption.RollingLines.Count - 1), line => Assert.False(line.IsLatest));
        Assert.True(caption.RollingLines[^1].IsLatest);
        Assert.Equal(long1, string.Concat(caption.RollingLines.Select(line => line.Text)));
    }

    [Fact]
    public void Only_a_bounded_number_of_lines_is_retained()
    {
        var caption = Fresh();

        for (int i = 0; i < 260; i++)
            caption.AddRecentLine($"第 {i} 句。");

        caption.RebuildRollingLines(0);

        Assert.True(caption.RollingLines.Count <= 200, $"{caption.RollingLines.Count} lines retained");
        Assert.Equal("第 259 句。", caption.RollingLines[^1].Text);
    }

    [Fact]
    public void A_translation_lands_on_the_line_its_sentence_is_on()
    {
        var caption = Fresh();

        // The sentence was shown while its translation was in flight, then moved into the history.
        caption.AddRecentLine("今天天气不错。", source: "今天天气不错。");
        caption.DisplayTranslatedCaption = "第二句。";

        Assert.True(caption.SetRecentLineText("今天天气不错。", "The weather is nice."));
        caption.RebuildRollingLines(0);

        Assert.Equal(["The weather is nice.", "第二句。"], caption.RollingLines.Select(line => line.Text));
    }

    [Fact]
    public void The_sentence_on_screen_is_not_listed_twice()
    {
        var caption = Fresh();

        caption.AddRecentLine("同一句。");
        caption.DisplayTranslatedCaption = "同一句。";
        caption.RebuildRollingLines(0);

        // It is already the last finished line, so it is highlighted there instead of being added again.
        Assert.Single(caption.RollingLines);
        Assert.True(caption.RollingLines[0].IsLatest);
    }

    [Fact]
    public void A_notice_is_shown_separately_and_never_becomes_a_caption_line()
    {
        var caption = Fresh();

        caption.Notice = "[ERROR] 连接中断";
        caption.AddRecentLine("一句正常的字幕。");
        caption.RebuildRollingLines(0);

        Assert.True(caption.HasNotice);
        Assert.DoesNotContain(caption.RollingLines, line => line.Text.Contains("连接中断"));
    }
}
