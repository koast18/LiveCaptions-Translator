using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.App.Tests;

/// <summary>
/// The update prompt used to compare the release tag and the assembly version as strings, so
/// "0.2.1" vs "1.7.1300.1822" looked like a new version on every start.
/// </summary>
public class UpdateUtilTests
{
    [Theory]
    [InlineData("0.2.2", "0.2.1.0", true)]
    [InlineData("0.2.1", "0.2.1.0", false)]
    [InlineData("0.2.1", "0.2.1", false)]
    [InlineData("0.2.1", "1.7.1300.1822", false)]
    [InlineData("1.7.1400.0", "1.7.1300.1822", true)]
    [InlineData("1.8", "1.7.1300.1822", true)]
    public void Only_a_strictly_newer_release_prompts(string latest, string current, bool expected)
        => Assert.Equal(expected, UpdateUtil.IsNewerRelease(latest, current));

    [Theory]
    [InlineData(null, "0.2.1")]
    [InlineData("", "0.2.1")]
    [InlineData("   ", "0.2.1")]
    [InlineData("not-a-version", "0.2.1")]
    [InlineData("0.2.1", null)]
    [InlineData("0.2.1", "")]
    [InlineData("0.2.1", "garbage")]
    public void Unparsable_values_never_prompt(string? latest, string? current)
        => Assert.False(UpdateUtil.IsNewerRelease(latest, current));
}

/// <summary>
/// The caption page used to show the same sentence twice whenever the source was already
/// Simplified Chinese and the translation policy skipped the API.
/// </summary>
public class RedundantTranslationTests
{
    [Theory]
    [InlineData("今天天气不错。", "今天天气不错。")]
    [InlineData("今天天气不错。", "今天天气不错")]
    [InlineData("今天天气不错", "今天天气不错。")]
    [InlineData("今天天气不错。", "[  12 ms] 今天天气不错。")]
    [InlineData("  今天天气不错。  ", "今天天气不错。")]
    public void Identical_lines_are_redundant(string original, string translated)
        => Assert.True(LiveCaptionsTranslator.Translator.IsTranslationRedundant(original, translated));

    [Theory]
    [InlineData("今天天气不错。", "The weather is nice.")]
    [InlineData("我们一起去公园", "我们一起去公园散步吧。")]
    [InlineData("", "")]
    [InlineData("今天天气", "")]
    public void Different_lines_are_not(string original, string translated)
        => Assert.False(LiveCaptionsTranslator.Translator.IsTranslationRedundant(original, translated));
}
