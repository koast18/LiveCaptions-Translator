using LiveCaptionsTranslator.Core.Text;

namespace LiveCaptionsTranslator.Core.Tests;

public class RegexPatternsTests
{
    [Fact]
    public void PunctuationSpace_collapses_spaces_around_latin_punctuation()
        => Assert.Equal(
            "Hello, world!",
            RegexPatterns.PunctuationSpace().Replace("Hello   ,   world   !", "$1 ").TrimEnd());

    [Fact]
    public void CJPunctuationSpace_removes_spaces_around_cjk_punctuation()
        => Assert.Equal(
            "你好，世界。",
            RegexPatterns.CJPunctuationSpace().Replace("你好 ， 世界 。", "$1"));

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("http://example.com", true)]
    [InlineData("example.com", false)]
    public void HttpPrefix_detects_scheme(string url, bool expected)
        => Assert.Equal(expected, RegexPatterns.HttpPrefix().IsMatch(url));

    [Fact]
    public void MultipleSlashes_collapses_runs_of_slashes()
        => Assert.Equal("a/b", RegexPatterns.MultipleSlashes().Replace("a///b", "/"));

    [Fact]
    public void ModelThinking_strips_reasoning_block()
        => Assert.Equal(
            "answer",
            RegexPatterns.ModelThinking().Replace("answer", string.Empty).Trim());

    [Fact]
    public void TargetSentence_extracts_wrapped_sentence()
    {
        var match = RegexPatterns.TargetSentence().Match("🔤 hello world 🔤");

        Assert.True(match.Success);
        Assert.Equal("hello world", match.Groups[1].Value);
    }

    [Fact]
    public void VersionNumber_keeps_only_digits_and_dots()
        => Assert.Equal("1.2.3", RegexPatterns.VersionNumber().Replace("v1.2.3", string.Empty));

    [Fact]
    public void NoticePrefix_matches_bracketed_prefix()
    {
        Assert.Equal("[INFO] ", RegexPatterns.NoticePrefix().Match("[INFO] hello").Value);
        Assert.DoesNotMatch(RegexPatterns.NoticePrefix(), "hello");
    }
}
