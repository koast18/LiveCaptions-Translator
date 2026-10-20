using System.IO;
using System.Text.RegularExpressions;

namespace LiveCaptionsTranslator.App.Tests;

/// <summary>
/// Guards success criterion 3: every localisable string in the XAML must go through
/// <c>{DynamicResource ...}</c>, so switching the dictionary actually changes it.
/// </summary>
public class XamlLocalizationTests
{
    /// <summary>Values that are deliberately not translated.</summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        // Product and page names
        "LiveCaptions Translator", "LiveCaptions",
        "SettingPage", "CaptionPage", "HistoryPage", "InfoPage",

        // Credential / protocol field names
        "api_key", "api_secret", "app_id", "app_key", "device_id",
        "API Key", "APP ID", "APP Key", "APP Secret",

        // Provider names and technical fragments
        "Baidu", "DeepL", "LMStudio", "LibreTranslate", "MTranServer",
        "Ollama", "OpenAI", "OpenRouter", "Youdao",
        "API", "`api/chat`", "`api/v1`", "`v1/chat/completions`", "`v1`",

        // Language codes and data values
        "de", "en", "fr", "ja", "ko", "1/1",

        // Data shown verbatim: URLs, handles, version number
        "1.1.1.1", "@SakiRinn", "@A-nony-mous",
        "https://github.com/SakiRinn/LiveCaptions-Translator",
        "https://github.com/SakiRinn/LiveCaptions-Translator/wiki",
    };

    // The leading whitespace matters: "SizeToContent="Height"" must not look like Content="Height".
    private static readonly Regex Attribute = new(
        "(?<=\s)(Text|Content|Title|ToolTip|Header|OffContent|OnContent|PlaceholderText)=\"([^\"{}][^\"]*)\"",
        RegexOptions.Compiled);

    private static IEnumerable<string> XamlFiles() =>
        Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "XamlUnderTest"), "*.xaml", SearchOption.AllDirectories);

    [Fact]
    public void The_xaml_files_are_available_to_this_test()
    {
        // Guards against the glob silently matching nothing, which would make the check vacuous.
        var files = XamlFiles().ToList();

        Assert.True(files.Count >= 9, $"expected at least 9 XAML files, found {files.Count}");
    }

    [Fact]
    public void No_localisable_text_is_left_hardcoded_in_xaml()
    {
        var offenders = new List<string>();

        foreach (string file in XamlFiles())
        {
            string name = Path.GetFileName(file);
            foreach (Match match in Attribute.Matches(File.ReadAllText(file)))
            {
                string value = match.Groups[2].Value;

                if (value.Length < 2 || Allowed.Contains(value))
                    continue;

                offenders.Add($"{name}: {match.Groups[1].Value}=\"{value}\"");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "localisable text is still hardcoded:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }
}
