using System.IO;
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

    [Fact]
    public void UiLanguage_survives_a_round_trip_through_setting_json()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lct-setting-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                {
                  "UiLanguage": "zh-Hans"
                }
                """);

            // Setting.Load is the same code path the app takes on startup, so this is the round
            // trip that matters: a file on disk becomes a Setting whose UiLanguage is Chinese.
            var setting = Setting.Load(path);

            Assert.Equal("zh-Hans", setting.UiLanguage);
            Assert.Equal(LocalizationManager.SimplifiedChinese, setting.UiLanguage);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_language_written_to_a_setting_file_can_be_read_back()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lct-setting-{Guid.NewGuid():N}.json");
        try
        {
            var saved = new Setting();
            saved.Save(path);

            Assert.Contains("\"UiLanguage\": \"en\"", File.ReadAllText(path));
            Assert.Equal("en", Setting.Load(path).UiLanguage);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
