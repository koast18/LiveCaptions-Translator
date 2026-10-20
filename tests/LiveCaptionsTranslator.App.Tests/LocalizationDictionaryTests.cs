using System.IO;
using System.Xml.Linq;

namespace LiveCaptionsTranslator.App.Tests;

/// <summary>
/// The two string dictionaries must stay in lockstep: a key that exists in one language but not the
/// other shows raw text (or a key name) in the UI, which is exactly what a half-finished translation
/// looks like to a user.
/// </summary>
public class LocalizationDictionaryTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static Dictionary<string, string> Load(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Localization", fileName);

        Assert.True(File.Exists(path), $"missing dictionary: {path}");

        var document = XDocument.Load(path);
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var element in document.Root!.Elements())
        {
            string? key = element.Attribute(X + "Key")?.Value;
            if (key is null)
                continue;

            entries[key] = element.Value;
        }

        return entries;
    }

    private static Dictionary<string, string> English() => Load("Strings.en.xaml");

    private static Dictionary<string, string> Chinese() => Load("Strings.zh-Hans.xaml");

    [Fact]
    public void Both_dictionaries_are_not_empty()
    {
        Assert.NotEmpty(English());
        Assert.NotEmpty(Chinese());
    }

    [Fact]
    public void The_two_dictionaries_have_exactly_the_same_keys()
    {
        var english = English().Keys.ToHashSet(StringComparer.Ordinal);
        var chinese = Chinese().Keys.ToHashSet(StringComparer.Ordinal);

        var missingInChinese = english.Except(chinese).OrderBy(key => key).ToList();
        var missingInEnglish = chinese.Except(english).OrderBy(key => key).ToList();

        Assert.True(
            missingInChinese.Count == 0,
            "keys missing from Strings.zh-Hans.xaml: " + string.Join(", ", missingInChinese));

        Assert.True(
            missingInEnglish.Count == 0,
            "keys missing from Strings.en.xaml: " + string.Join(", ", missingInEnglish));

        Assert.Equal(english.Count, chinese.Count);
    }

    [Fact]
    public void No_dictionary_value_is_blank()
    {
        foreach (var (fileName, entries) in new[]
                 {
                     ("Strings.en.xaml", English()),
                     ("Strings.zh-Hans.xaml", Chinese()),
                 })
        {
            var blank = entries.Where(pair => string.IsNullOrWhiteSpace(pair.Value))
                .Select(pair => pair.Key)
                .OrderBy(key => key)
                .ToList();

            Assert.True(blank.Count == 0, $"{fileName} has blank values for: " + string.Join(", ", blank));
        }
    }

    [Fact]
    public void Keys_are_namespaced_so_a_file_can_be_found_by_prefix()
    {
        // Convention: Area_Name, e.g. Nav_Caption, Setting_TargetLanguage.
        foreach (string key in English().Keys)
        {
            Assert.Contains('_', key);
            Assert.Matches("^[A-Za-z]+_[A-Za-z0-9_]+$", key);
        }
    }
}
