using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.Core.Translation;
using LiveCaptionsTranslator.translation;

namespace LiveCaptionsTranslator.App.Tests;

/// <summary>
/// The translation side of the plugin seam: every engine upstream shipped must still be
/// reachable through the registry, plus the new iFlytek machine-translation provider.
/// </summary>
public class TranslationProviderCatalogTests
{
    private static TranslationProviderRegistry Build()
    {
        var registry = new TranslationProviderRegistry();
        TranslationProviderCatalog.RegisterAll(registry);
        return registry;
    }

    [Fact]
    public void Upstream_ships_the_engines_this_project_promises_to_keep()
    {
        // Guard against the dictionary being emptied or renamed under us.
        Assert.True(
            TranslateAPI.TRANSLATE_FUNCTIONS.Count >= 11,
            $"expected at least 11 engines, found {TranslateAPI.TRANSLATE_FUNCTIONS.Count}");
    }

    [Fact]
    public void Every_upstream_engine_is_still_registered()
    {
        var registry = Build();

        foreach (string id in TranslateAPI.TRANSLATE_FUNCTIONS.Keys)
            Assert.True(registry.IsRegistered(id), $"engine '{id}' disappeared from the registry");
    }

    [Theory]
    [InlineData("Google")]
    [InlineData("Google2")]
    [InlineData("Ollama")]
    [InlineData("OpenAI")]
    [InlineData("LMStudio")]
    [InlineData("DeepL")]
    [InlineData("OpenRouter")]
    [InlineData("Youdao")]
    [InlineData("MTranServer")]
    [InlineData("Baidu")]
    [InlineData("LibreTranslate")]
    public void The_documented_engines_are_selectable(string engine)
    {
        Assert.True(Build().IsRegistered(engine), $"engine '{engine}' is not selectable");
    }

    [Fact]
    public void Iflytek_machine_translation_is_registered()
    {
        var registry = Build();

        Assert.True(registry.IsRegistered("iflytek-mt"));
        Assert.Contains("iflytek-mt", registry.Ids);
        Assert.Equal(TranslateAPI.TRANSLATE_FUNCTIONS.Count + 1, registry.Count);
    }

    [Fact]
    public void Engine_metadata_matches_the_upstream_lists()
    {
        var registry = Build();

        Assert.True(registry.Create("OpenAI").IsLlmBased);
        Assert.True(registry.Create("Ollama").IsLlmBased);
        Assert.False(registry.Create("Google").IsLlmBased);

        Assert.False(registry.Create("Google").RequiresConfiguration);
        Assert.True(registry.Create("OpenAI").RequiresConfiguration);

        // Machine translation needs a real 开放平台 account.
        var mt = registry.Create("iflytek-mt");
        Assert.True(mt.RequiresConfiguration);
        Assert.False(mt.IsLlmBased);
    }

    [Fact]
    public void Selecting_an_engine_resolves_its_provider()
    {
        var registry = Build();

        foreach (string id in registry.Ids)
        {
            var provider = registry.Create(id);
            Assert.Equal(id, provider.Id);
        }
    }
}
