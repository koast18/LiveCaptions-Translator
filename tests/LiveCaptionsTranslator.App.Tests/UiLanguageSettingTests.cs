using System.Text.Json;

using LiveCaptionsTranslator.Localization;
using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.App.Tests;

/// <summary>
/// Guards criterion 1: the chosen language is part of setting.json, so it survives a restart.
/// The property is checked by reading the serialised output rather than by re-deserialising, because
/// setting a property raises Setting.OnPropertyChanged which touches Translator (and would start the
/// caption pipeline inside a test).
/// </summary>
public class UiLanguageSettingTests
{
    [Fact]
    public void UiLanguage_is_serialised_with_its_default()
    {
        var setting = new Setting();

        string json = JsonSerializer.Serialize(setting);

        Assert.Contains("\"UiLanguage\":\"en\"", json);
    }

    [Fact]
    public void The_default_is_english()
    {
        Assert.Equal("en", new Setting().UiLanguage);
        Assert.Equal(LocalizationManager.English, new Setting().UiLanguage);
    }

    [Theory]
    [InlineData("en", "en")]
    [InlineData("zh-Hans", "zh-Hans")]
    [InlineData("ZH-HANS", "zh-Hans")]
    [InlineData("zh", "en")]        // unknown values fall back rather than showing blank text
    [InlineData("fr", "en")]
    [InlineData(null, "en")]
    [InlineData("", "en")]
    public void An_unknown_language_falls_back_to_english(string? configured, string expected)
        => Assert.Equal(expected, LocalizationManager.Normalize(configured));

    [Fact]
    public void Every_offered_language_has_a_dictionary()
    {
        // The picker must not offer a language we do not ship.
        Assert.Equal(2, LocalizationManager.Available.Count);
        Assert.Contains(LocalizationManager.Available, option => option.Id == "en");
        Assert.Contains(LocalizationManager.Available, option => option.Id == "zh-Hans");
    }
}
