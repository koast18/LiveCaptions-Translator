using System.Net;
using System.Net.Http;
using System.Text;

using LiveCaptionsTranslator.Core.Iflytek;
using LiveCaptionsTranslator.Core.Translation;

namespace LiveCaptionsTranslator.Core.Tests;

public class IflytekMtProviderTests
{
    private static readonly IflytekMtOptions Configured = new()
    {
        ApiKey = "mt-key",
        ApiSecret = "mt-secret",
        AppId = "mt-app",
    };

    private static string Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static string Reply(string translated) =>
        $"{{\"code\":0,\"message\":\"success\",\"sid\":\"its0001\"," +
        $"\"data\":{{\"result\":{{\"from\":\"en\",\"to\":\"cn\",\"text\":\"{Encode(translated)}\"}}}}}}";

    private static (IflytekMtProvider Provider, StubHttpMessageHandler Handler) Build(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        IflytekMtOptions? options = null)
    {
        var handler = new StubHttpMessageHandler(responder);
        var provider = new IflytekMtProvider(
            options ?? Configured,
            () => new HttpClient(handler),
            TimeProvider.System);

        return (provider, handler);
    }

    [Theory]
    [InlineData("zh_cn", "cn")]
    [InlineData("zh-CN", "cn")]
    [InlineData("zh-Hant", "cn")]
    [InlineData("zh_tw", "cn")]
    [InlineData("en", "en")]
    [InlineData("en_us", "en")]
    [InlineData("en-US", "en")]
    [InlineData("ja", "ja")]
    [InlineData("jp", "ja")]
    [InlineData("ko", "ko")]
    [InlineData("kr", "ko")]
    [InlineData("de-DE", "de")]
    [InlineData(null, "en")]
    [InlineData("  ", "en")]
    public void Language_codes_map_onto_iflytek_codes(string? language, string expected)
    {
        Assert.Equal(expected, IflytekMtLanguages.ToIflytek(language));
    }

    [Fact]
    public void Provider_reports_itself_as_needing_configuration()
    {
        var provider = new IflytekMtProvider();

        Assert.Equal("iflytek-mt", provider.Id);
        Assert.True(provider.RequiresConfiguration);
        Assert.False(provider.IsLlmBased);
        Assert.False(provider.IsConfigured);
    }

    [Fact]
    public async Task Without_credentials_the_provider_refuses_to_run()
    {
        var (provider, handler) = Build(_ => StubHttpMessageHandler.Json(Reply("x")), new IflytekMtOptions());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.TranslateAsync(
            new TranslationRequest { Text = "hello", TargetLanguage = "zh-CN" }));

        Assert.Contains("开放平台", error.Message);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Request_is_signed_and_the_reply_is_decoded()
    {
        var (provider, handler) = Build(_ => StubHttpMessageHandler.Json(Reply("今天天气不错。")));

        string result = await provider.TranslateAsync(new TranslationRequest
        {
            Text = "The weather is nice.",
            TargetLanguage = "zh-CN",
            SourceLanguage = "en_us",
        });

        Assert.Equal("今天天气不错。", result);

        var request = handler.LastRequest!;
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://itrans.xf-yun.com/v1/its", request.RequestUri!.ToString());

        // The Authorization header must be exactly what the signing scheme produces.
        string date = request.Headers.GetValues("Date").Single();
        string expectedSignature = IflytekSignature.Sign(
            Configured.ApiSecret, Configured.Host, Configured.Path, date, "POST");
        string expectedAuthorization = IflytekSignature.AuthorizationHeader(
            Configured.ApiKey, expectedSignature);

        Assert.Equal(expectedAuthorization, request.Headers.GetValues("Authorization").Single());
        Assert.Equal(Configured.Host, request.Headers.GetValues("Host").Single());

        string body = handler.LastBody!;
        Assert.Contains("\"app_id\":\"mt-app\"", body);
        Assert.Contains("\"from\":\"en\"", body);
        Assert.Contains("\"to\":\"cn\"", body);
        Assert.Contains(Encode("The weather is nice."), body);
    }

    [Fact]
    public async Task Chinese_target_and_unknown_source_default_to_english_input()
    {
        var (provider, handler) = Build(_ => StubHttpMessageHandler.Json(Reply("你好")));

        await provider.TranslateAsync(new TranslationRequest
        {
            Text = "hello",
            TargetLanguage = "zh-Hans",
        });

        Assert.Contains("\"from\":\"en\"", handler.LastBody);
        Assert.Contains("\"to\":\"cn\"", handler.LastBody);
    }

    [Fact]
    public async Task Error_codes_surface_as_iflytek_api_exceptions()
    {
        var (provider, _) = Build(_ => StubHttpMessageHandler.Json(
            """{"code":10163,"message":"appid is not exist"}"""));

        var error = await Assert.ThrowsAsync<IflytekApiException>(() => provider.TranslateAsync(
            new TranslationRequest { Text = "hello", TargetLanguage = "zh-CN" }));

        Assert.Equal(10163, error.Code);
        Assert.Contains("appid is not exist", error.Message);
    }

    [Fact]
    public async Task Http_failures_are_reported_with_the_body()
    {
        var (provider, _) = Build(_ => StubHttpMessageHandler.Json(
            "unauthorized", HttpStatusCode.Unauthorized));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.TranslateAsync(
            new TranslationRequest { Text = "hello", TargetLanguage = "zh-CN" }));

        Assert.Contains("401", error.Message);
    }

    [Fact]
    public async Task Empty_text_is_returned_without_a_call()
    {
        var (provider, handler) = Build(_ => StubHttpMessageHandler.Json(Reply("x")));

        Assert.Equal(string.Empty, await provider.TranslateAsync(
            new TranslationRequest { Text = string.Empty, TargetLanguage = "zh-CN" }));

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public void Settings_bag_is_read_without_a_committed_default()
    {
        // Unlike IAT there is no anonymous fallback: machine translation needs a real account.
        var empty = IflytekMtOptions.FromSettings(null);
        Assert.False(empty.IsConfigured);
        Assert.Equal(IflytekMtProtocol.DefaultHost, empty.Host);

        var filled = IflytekMtOptions.FromSettings(new Dictionary<string, string>
        {
            [IflytekSettingKeys.ApiKey] = "k",
            [IflytekSettingKeys.ApiSecret] = "s",
            [IflytekSettingKeys.AppId] = "a",
        });

        Assert.True(filled.IsConfigured);
        Assert.Equal("a", filled.AppId);
    }

    [Fact]
    public void Response_without_a_translation_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() => IflytekMtProtocol.ParseResponse("""{"code":0,"data":{}}"""));
    }
}
