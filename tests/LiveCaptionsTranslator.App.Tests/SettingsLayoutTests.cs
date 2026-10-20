using System.IO;
using System.Xml.Linq;

namespace LiveCaptionsTranslator.App.Tests;

/// <summary>
/// Guards the settings page against the failure it actually had: five Auto columns plus the
/// flyout bodies made the row about 2000 px wide, so the page ran past the window and the last
/// settings could not be reached. A row that cannot fit the content area is a bug, not a
/// preference, so it is asserted here instead of being left to a screenshot.
/// </summary>
public class SettingsLayoutTests
{
    /// <summary>
    /// The main window opens at 750 px. Take away the navigation rail and the page margin and
    /// roughly this much is left for the settings themselves.
    /// </summary>
    private const int ContentBudget = 660;

    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static XElement Load() =>
        XDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "XamlUnderTest", "pages", "SettingPage.xaml"))).Root!;

    private static string? Attribute(XElement element, string name) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;

    /// <summary>Width a child insists on, or 0 when it can shrink.</summary>
    private static int FixedWidth(XElement element)
    {
        string? width = Attribute(element, "Width");
        if (width is null || width.EndsWith('*'))
            return 0;
        return double.TryParse(width, out double value) && value >= 20 ? (int)value : 0;
    }

    private static int HorizontalMargin(XElement element) =>
        (Attribute(element, "Margin") ?? string.Empty)
            .Split(',')
            .Skip(2)   // Margin="left,top,right,bottom" - the horizontal part is left+right
            .Select(part => double.TryParse(part, out double value) ? (int)value : 0)
            .DefaultIfEmpty(0)
            .Sum();

    [Fact]
    public void The_xaml_probably_lives_where_this_test_expects()
    {
        Assert.True(File.Exists(Path.Combine(
            AppContext.BaseDirectory, "XamlUnderTest", "pages", "SettingPage.xaml")));
    }

    [Fact]
    public void No_row_of_settings_needs_more_width_than_the_window_offers()
    {
        var offenders = new List<string>();

        foreach (XElement panel in Load().Descendants())
        {
            string local = panel.Name.LocalName;

            // A WrapPanel is allowed to exceed the budget: that is the point of wrapping.
            if (local is not ("StackPanel" or "Grid" or "UniformGrid"))
                continue;
            if (Attribute(panel, "Orientation") != "Horizontal")
                continue;

            int needed = panel.Elements().Sum(child => FixedWidth(child) + HorizontalMargin(child));
            if (needed > ContentBudget)
                offenders.Add($"{needed}px in a row of {panel.Elements().Count()} controls");
        }

        Assert.True(
            offenders.Count == 0,
            $"a settings row cannot fit {ContentBudget}px:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void A_flyout_body_never_decides_how_wide_a_column_is()
    {
        // Width on a flyout body used to stretch the Auto column it sat in. MaxWidth caps the
        // popup without contributing to the measurement.
        var bad = Load()
            .Descendants()
            .Where(element => element.Name.LocalName == "TextBlock")
            .Where(element => element.Ancestors().Any(a => a.Name.LocalName == "Flyout"))
            .Where(element => Attribute(element, "Width") is not null && FixedWidth(element) > 200)
            .Select(element => Attribute(element, "Width")!)
            .ToList();

        Assert.True(bad.Count == 0, "flyout bodies still use Width: " + string.Join(", ", bad));
    }
}
