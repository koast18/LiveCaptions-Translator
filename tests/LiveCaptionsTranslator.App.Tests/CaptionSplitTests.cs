using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.App.Tests;

/// <summary>
/// The recogniser can run for a long time without punctuating, and the highlighted line used to grow
/// into a wall of text. Long text is broken at a pause instead - never truncated.
/// </summary>
public class CaptionSplitTests
{
    [Fact]
    public void Short_text_stays_on_one_line()
    {
        Assert.Equal(new[] { "今天天气不错。" }, Caption.SplitForDisplay("今天天气不错。", 42));
    }

    [Fact]
    public void Long_text_is_broken_after_a_pause()
    {
        string text = "第一句话说的是天气很好我们一起去公园散步吧，第二句话说的是路上遇到了老朋友聊了很久才回家。";

        var parts = Caption.SplitForDisplay(text, 20);

        Assert.True(parts.Count > 1);
        Assert.All(parts, part => Assert.True(part.Length <= 20 + 20, part));
        Assert.Contains(parts, part => part.EndsWith('，'));
    }

    [Fact]
    public void Nothing_is_lost_or_truncated()
    {
        string text = "一二三四五六七八九十，甲乙丙丁戊己庚辛壬癸，子丑寅卯辰巳午未申酉。";

        var parts = Caption.SplitForDisplay(text, 12);

        Assert.Equal(text.Replace(" ", string.Empty), string.Concat(parts).Replace(" ", string.Empty));
    }

    [Fact]
    public void Without_any_pause_the_text_is_kept_whole_rather_than_cut()
    {
        string text = new('字', 80);

        var parts = Caption.SplitForDisplay(text, 20);

        Assert.Single(parts);
        Assert.Equal(text, parts[0]);
    }
}
