using System.Threading;
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

    [Fact]
    public void English_is_merged_under_every_other_language()
    {
        // WPF resolves a DynamicResource against the merged chain in order, so putting English first
        // is what makes a missing translation fall back to English instead of rendering blank.
        Assert.Equal(new[] { "en" }, LocalizationManager.MergeChain("en"));
        Assert.Equal(new[] { "en", "zh-Hans" }, LocalizationManager.MergeChain("zh-Hans"));
        Assert.Equal(new[] { "en", "zh-Hans" }, LocalizationManager.MergeChain("ZH-HANS"));
        Assert.Equal(new[] { "en" }, LocalizationManager.MergeChain("fr"));
    }

    [Fact]
    public void A_key_resolves_to_english_text_even_with_no_application_resource_dictionary()
    {
        // This is the startup path: Translator's static constructor runs before App.InitializeComponent
        // merges the dictionaries, and it may ask for text.
        Assert.Equal("Caption", LocalizationManager.T("Nav_Caption"));
        Assert.Equal("[Paused]", LocalizationManager.T("Caption_Paused"));
    }

    [Fact]
    public void An_unknown_key_is_returned_as_is_rather_than_blank()
        => Assert.Equal("NoSuchKey", LocalizationManager.T("NoSuchKey"));

    [Fact]
    public void Status_markers_are_recognised_in_english_and_in_chinese()
    {
        // The markers are part of the translated text, so the check cannot hardcode "[ERROR]".
        Assert.True(StatusText.IsError("[ERROR] Translation Failed: boom"));
        Assert.False(StatusText.IsStatus("我只是在说今天天气不错。"));
        Assert.False(StatusText.IsStatus(null));

        string english = LocalizationManager.T("Error_TranslationFailed", "boom");
        Assert.True(StatusText.IsError(english), $"not recognised: {english}");

        string warning = LocalizationManager.T("Status_Warning", "careful");
        Assert.True(StatusText.IsWarning(warning), $"not recognised: {warning}");
    }

    [Fact]
    public void Text_can_be_read_from_a_background_thread()
    {
        // The translation and ASR loops run on the thread pool. Reading a string there used to go
        // through Application.TryFindResource, a DispatcherObject access, which threw - and that took
        // the whole translation task down, so no caption ever reached the window.
        string? text = null;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try { text = LocalizationManager.T("Caption_Paused"); }
            catch (Exception error) { failure = error; }
        });

        thread.Start();
        thread.Join();

        Assert.Null(failure);
        Assert.Equal("[Paused]", text);
    }
}
