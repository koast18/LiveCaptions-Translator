using LiveCaptionsTranslator.Core.Asr;
using LiveCaptionsTranslator.Core.Iflytek;

namespace LiveCaptionsTranslator.Core.Tests;

/// <summary>
/// The host hands language, accent and credentials to the provider through the generic
/// <see cref="AsrOptions"/>, so these mappings are what the settings UI actually drives.
/// </summary>
public class IflytekIatOptionsTests
{
    [Fact]
    public void Defaults_are_used_when_nothing_is_configured()
    {
        var options = IflytekIatOptions.FromAsrOptions(new AsrOptions());

        Assert.Equal(IflytekCredentials.DefaultApiKey, options.Credentials.ApiKey);
        Assert.Equal(IflytekCredentials.DefaultApiSecret, options.Credentials.ApiSecret);
        Assert.Equal(IflytekCredentials.DefaultHost, options.Credentials.Host);
        Assert.Equal(IflytekCredentials.DefaultPath, options.Credentials.Path);
        Assert.Equal(IflytekCredentials.DefaultAppId, options.Credentials.AppId);
    }

    [Fact]
    public void Language_and_accent_come_from_the_host_options()
    {
        var options = IflytekIatOptions.FromAsrOptions(new AsrOptions
        {
            LanguageId = "en_us",
            Accent = "mandarin",
        });

        Assert.Equal("en_us", options.Business.Language);
        Assert.Equal("mandarin", options.Business.Accent);

        // Everything else keeps the reference defaults.
        Assert.Equal("iat", options.Business.Domain);
        Assert.Equal(3000, options.Business.VadEos);
        Assert.Equal("wpgs", options.Business.Dwa);
    }

    [Fact]
    public void Blank_language_and_accent_fall_back_to_the_defaults()
    {
        var options = IflytekIatOptions.FromAsrOptions(new AsrOptions
        {
            LanguageId = "  ",
            Accent = null,
        });

        Assert.Equal(IflytekBusiness.Default.Language, options.Business.Language);
        Assert.Equal(IflytekBusiness.Default.Accent, options.Business.Accent);
    }

    [Fact]
    public void Credentials_come_from_the_settings_bag()
    {
        var options = IflytekIatOptions.FromAsrOptions(new AsrOptions
        {
            Settings = new Dictionary<string, string>
            {
                [IflytekSettingKeys.ApiKey] = "my-key",
                [IflytekSettingKeys.ApiSecret] = "my-secret",
                [IflytekSettingKeys.Host] = "iat-api.xfyun.cn",
                [IflytekSettingKeys.Path] = "/v2/iat",
                [IflytekSettingKeys.AppId] = "MYAPP",
            },
        });

        Assert.Equal("my-key", options.Credentials.ApiKey);
        Assert.Equal("my-secret", options.Credentials.ApiSecret);
        Assert.Equal("iat-api.xfyun.cn", options.Credentials.Host);
        Assert.Equal("MYAPP", options.Credentials.AppId);
    }

    [Fact]
    public void Blank_credentials_fall_back_to_the_committed_defaults()
    {
        var options = IflytekIatOptions.FromAsrOptions(new AsrOptions
        {
            Settings = new Dictionary<string, string>
            {
                [IflytekSettingKeys.ApiKey] = string.Empty,
                [IflytekSettingKeys.ApiSecret] = "   ",
            },
        });

        Assert.Equal(IflytekCredentials.DefaultApiKey, options.Credentials.ApiKey);
        Assert.Equal(IflytekCredentials.DefaultApiSecret, options.Credentials.ApiSecret);
    }

    [Fact]
    public void Configured_credentials_change_the_signed_url()
    {
        var options = IflytekIatOptions.FromAsrOptions(new AsrOptions
        {
            Settings = new Dictionary<string, string> { [IflytekSettingKeys.ApiKey] = "rotated-key" },
        });

        var timestamp = DateTimeOffset.FromUnixTimeSeconds(946684800);
        string url = IflytekSignature.BuildSignedUrl(options.Credentials, timestamp);

        Assert.NotEqual(
            IflytekSignature.BuildSignedUrl(IflytekCredentials.Default, timestamp),
            url);
        Assert.Contains("100ime-iat-api.xfyun.cn", url);
    }

    [Fact]
    public async Task Provider_takes_its_settings_from_the_host_context()
    {
        var transports = new List<FakeTransport>();

        await using var provider = new IflytekIatProvider(null, () =>
        {
            var transport = new FakeTransport();
            transports.Add(transport);
            return transport;
        });

        var audio = new FakeAudioSource();

        await provider.StartAsync(new AsrContext
        {
            Sink = new RecordingSink(),
            Options = new AsrOptions
            {
                LanguageId = "en_us",
                Accent = "mandarin",
                Settings = new Dictionary<string, string>
                {
                    [IflytekSettingKeys.ApiKey] = "host-key",
                    [IflytekSettingKeys.AppId] = "HOSTAPP",
                },
            },
            AudioSource = audio,
        });

        audio.EmitChunk();
        await WaitFor(() => transports.Count > 0 && transports[0].Sent.Count > 0);

        var frame = System.Text.Json.JsonDocument.Parse(transports[0].Sent[0]).RootElement;

        Assert.Equal("en_us", frame.GetProperty("business").GetProperty("language").GetString());
        Assert.Equal("HOSTAPP", frame.GetProperty("common").GetProperty("app_id").GetString());

        // The handshake URL is signed with the host credentials, not the committed defaults.
        Assert.Contains("authorization=", transports[0].Url);
        Assert.Equal(
            IflytekSignature.BuildSignedUrl(
                new IflytekCredentials { ApiKey = "host-key", AppId = "HOSTAPP" },
                DateTimeOffset.UtcNow).Split('?')[0],
            transports[0].Url!.Split('?')[0]);

        await provider.StopAsync();
    }

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 8000)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException("condition was not met in time");

            await Task.Delay(20);
        }
    }
}
