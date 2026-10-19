using LiveCaptionsTranslator.Core.Text;

namespace LiveCaptionsTranslator.Core.Tests;

public class TextUtilTests
{
    [Theory]
    [InlineData('中', true)]
    [InlineData('。', true)]
    [InlineData('あ', true)]
    [InlineData('ア', true)]
    [InlineData('a', false)]
    [InlineData('1', false)]
    [InlineData('é', false)]
    public void IsCJChar_classifies_character_ranges(char ch, bool expected)
        => Assert.Equal(expected, TextUtil.isCJChar(ch));

    [Fact]
    public void ShortenDisplaySentence_drops_leading_sentences_until_under_threshold()
        => Assert.Equal(" D.", TextUtil.ShortenDisplaySentence("A. B. C. D.", 5));

    [Fact]
    public void ShortenDisplaySentence_keeps_text_when_no_punctuation_can_be_dropped()
        => Assert.Equal("abcdef", TextUtil.ShortenDisplaySentence("abcdef", 3));

    [Fact]
    public void ReplaceNewlines_joins_short_lines_with_a_dash()
        => Assert.Equal("Hello—World", TextUtil.ReplaceNewlines("Hello\nWorld", 40));

    [Fact]
    public void ReplaceNewlines_terminates_long_latin_lines_with_a_period()
        => Assert.Equal(
            "Hello world this is a long line. second",
            TextUtil.ReplaceNewlines("Hello world this is a long line\nsecond", 10));

    [Fact]
    public void ReplaceNewlines_terminates_long_cjk_lines_with_a_full_stop()
        => Assert.Equal(
            "这是一个很长的中文句子。下一句",
            TextUtil.ReplaceNewlines("这是一个很长的中文句子\n下一句", 10));

    [Theory]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("abc", "abc", 0)]
    [InlineData("", "abc", 3)]
    [InlineData("abc", "", 3)]
    [InlineData("", "", 0)]
    public void LevenshteinDistance_matches_known_values(string a, string b, int expected)
        => Assert.Equal(expected, TextUtil.LevenshteinDistance(a, b));

    [Fact]
    public void Similarity_is_one_for_prefixes()
        => Assert.Equal(1.0, TextUtil.Similarity("今天天气", "今天天气不错"));

    [Fact]
    public void Similarity_reflects_edit_distance()
        => Assert.Equal(1.0 - 1.0 / 3.0, TextUtil.Similarity("abc", "abd"), 10);

    [Theory]
    [InlineData("https://example.com//a///b/", "https://example.com/a/b")]
    [InlineData("http://example.com/", "http://example.com")]
    [InlineData("example.com//x", "example.com/x")]
    public void NormalizeUrl_collapses_repeated_slashes_and_trailing_slash(string input, string expected)
        => Assert.Equal(expected, TextUtil.NormalizeUrl(input));
}
