using LiveCaptionsTranslator.Core.Text;

namespace LiveCaptionsTranslator.Core.Tests;

public class ChineseTextTests
{
    private const string SimplifiedSentence = "今天天气不错，我们一起去公园散步吧。";
    private const string TraditionalSentence = "我們一起去看電影";
    private const string TraditionalSentenceSimplified = "我们一起去看电影";

    [Fact]
    public void Table_is_loaded()
    {
        // OpenCC's TSCharacters table has a few thousand entries.
        Assert.True(ChineseText.TableSize > 3000, $"table only has {ChineseText.TableSize} entries");
    }

    [Theory]
    [InlineData("們", "们")]
    [InlineData("說", "说")]
    [InlineData("這", "这")]
    [InlineData("麼", "么")]
    [InlineData("語", "语")]
    [InlineData("電", "电")]
    [InlineData("腦", "脑")]
    [InlineData("體", "体")]
    [InlineData("會", "会")]
    [InlineData("學", "学")]
    [InlineData("國", "国")]
    [InlineData("臺", "台")]
    public void Single_characters_are_converted(string traditional, string simplified)
    {
        Assert.Equal(simplified, ChineseText.ToSimplified(traditional));
    }

    [Fact]
    public void Whole_sentences_are_converted()
    {
        Assert.Equal(TraditionalSentenceSimplified, ChineseText.ToSimplified(TraditionalSentence));
    }

    [Fact]
    public void Vocabulary_is_not_rewritten()
    {
        // Character level conversion only: 電腦 becomes 电脑, never 计算机.
        Assert.Equal("电脑", ChineseText.ToSimplified("電腦"));
    }

    [Fact]
    public void Non_chinese_characters_pass_through()
    {
        Assert.Equal("Hello 世界 123!?", ChineseText.ToSimplified("Hello 世界 123!?"));
    }

    [Fact]
    public void Already_simplified_text_is_returned_unchanged()
    {
        Assert.Same(SimplifiedSentence, ChineseText.ToSimplified(SimplifiedSentence));
    }

    [Fact]
    public void Empty_input_is_handled()
    {
        Assert.Equal(string.Empty, ChineseText.ToSimplified(string.Empty));
        Assert.Equal(string.Empty, ChineseText.ToSimplified(null));
    }

    [Fact]
    public void Simplified_text_is_recognised()
    {
        Assert.True(ChineseText.IsSimplified(SimplifiedSentence));
        Assert.False(ChineseText.IsTraditional(SimplifiedSentence));
    }

    [Fact]
    public void Traditional_text_is_recognised()
    {
        Assert.True(ChineseText.IsTraditional(TraditionalSentence));
        Assert.False(ChineseText.IsSimplified(TraditionalSentence));
    }

    [Fact]
    public void Non_chinese_text_is_neither()
    {
        Assert.False(ChineseText.IsSimplified("Hello world"));
        Assert.False(ChineseText.IsTraditional("Hello world"));
        Assert.False(ChineseText.HasHan("Hello world"));
    }

    [Theory]
    [InlineData(SimplifiedSentence, 16)]
    [InlineData("Hello 世界", 2)]
    [InlineData("Hello", 0)]
    [InlineData("", 0)]
    public void Han_characters_are_counted(string text, int expected)
    {
        Assert.Equal(expected, ChineseText.HanCount(text));
    }

    [Fact]
    public void Mostly_han_distinguishes_scripts()
    {
        Assert.True(ChineseText.IsMostlyHan(SimplifiedSentence));
        Assert.True(ChineseText.IsMostlyHan("Hello 世界 你好吗"));
        Assert.False(ChineseText.IsMostlyHan("Hello 世界"));
        Assert.False(ChineseText.IsMostlyHan("Hello world"));
        Assert.False(ChineseText.IsMostlyHan(""));
        Assert.False(ChineseText.IsMostlyHan(null));
    }

    [Fact]
    public void Punctuation_spaces_and_digits_do_not_count_as_letters()
    {
        // Three Han characters and three Latin letters -> exactly half.
        Assert.True(ChineseText.IsMostlyHan("你好吗 abc", 0.5));
        Assert.False(ChineseText.IsMostlyHan("你好吗 abcdef", 0.5));
        Assert.True(ChineseText.IsMostlyHan("你好吗 1234567890", 0.5));
    }

    [Fact]
    public void Supplementary_plane_han_is_recognised()
    {
        // U+20000 is the first Extension B ideograph.
        Assert.True(ChineseText.HasHan(char.ConvertFromUtf32(0x20000)));
    }
}
