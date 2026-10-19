using LiveCaptionsTranslator.Core.Translation;

namespace LiveCaptionsTranslator.Core.Tests;

public class TranslationPolicyTests
{
    private const string SimplifiedSentence = "今天天气不错，我们一起去公园散步吧。";
    private const string TraditionalSentence = "我們一起去看電影";
    private const string TraditionalSentenceSimplified = "我们一起去看电影";
    private const string EnglishSentence = "The weather is nice today.";

    [Fact]
    public void Chinese_recognition_never_calls_the_translation_api()
    {
        var plan = TranslationPolicy.Plan(SimplifiedSentence, "zh_cn", "zh-CN");

        Assert.False(plan.Translate);
        Assert.Equal(SimplifiedSentence, plan.Text);
    }

    [Fact]
    public void Chinese_recognition_normalises_traditional_output_to_simplified()
    {
        var plan = TranslationPolicy.Plan(TraditionalSentence, "zh_cn", "zh-CN");

        Assert.False(plan.Translate);
        Assert.Equal(TraditionalSentenceSimplified, plan.Text);
    }

    [Theory]
    [InlineData("zh_cn")]
    [InlineData("zh_tw")]
    [InlineData("zh-Hans")]
    [InlineData("ZH-CN")]
    public void Every_chinese_variant_is_treated_as_chinese(string languageId)
    {
        var plan = TranslationPolicy.Plan(TraditionalSentence, languageId, "zh-CN");

        Assert.False(plan.Translate);
        Assert.Equal(TraditionalSentenceSimplified, plan.Text);
    }

    [Fact]
    public void Chinese_target_with_already_simplified_text_is_skipped()
    {
        var plan = TranslationPolicy.Plan(SimplifiedSentence, "en_us", "zh-CN");

        Assert.False(plan.Translate);
        Assert.Equal(SimplifiedSentence, plan.Text);
    }

    [Fact]
    public void Non_chinese_text_is_sent_to_the_translation_api()
    {
        var plan = TranslationPolicy.Plan(EnglishSentence, "en_us", "zh-CN");

        Assert.True(plan.Translate);
        Assert.Equal(EnglishSentence, plan.Text);
    }

    [Fact]
    public void Traditional_text_with_a_chinese_target_is_translated_not_just_converted()
    {
        // The source language is not Chinese here, so the engine decides how to render it.
        var plan = TranslationPolicy.Plan(TraditionalSentence, "ja", "zh-CN");

        Assert.True(plan.Translate);
    }

    [Fact]
    public void Unknown_source_language_falls_through_to_translation()
    {
        var plan = TranslationPolicy.Plan(EnglishSentence, null, "zh-CN");

        Assert.True(plan.Translate);
    }

    [Fact]
    public void Empty_text_is_skipped()
    {
        Assert.False(TranslationPolicy.Plan(string.Empty, "en_us", "zh-CN").Translate);
        Assert.Equal(string.Empty, TranslationPolicy.Plan(string.Empty, "en_us", "zh-CN").Text);
    }

    [Theory]
    [InlineData("zh", true)]
    [InlineData("zh-CN", true)]
    [InlineData("zh-Hans", true)]
    [InlineData("zh_tw", true)]
    [InlineData("en", false)]
    [InlineData("ja", false)]
    [InlineData(null, false)]
    public void Chinese_language_detection(string? languageId, bool expected)
    {
        Assert.Equal(expected, TranslationPolicy.IsChinese(languageId));
    }

    [Theory]
    [InlineData("zh-CN", true)]
    [InlineData("zh_cn", true)]
    [InlineData("zh-Hans", true)]
    [InlineData("zh", true)]
    [InlineData("zh-TW", false)]
    [InlineData("zh-Hant", false)]
    [InlineData("en", false)]
    [InlineData(null, false)]
    public void Simplified_chinese_target_detection(string? target, bool expected)
    {
        Assert.Equal(expected, TranslationPolicy.IsSimplifiedChineseTarget(target));
    }
}
