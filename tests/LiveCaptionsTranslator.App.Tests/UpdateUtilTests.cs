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
