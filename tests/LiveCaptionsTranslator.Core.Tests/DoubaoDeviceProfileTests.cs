using System.Net;
using System.Net.Http;
using System.Text.Json;

using LiveCaptionsTranslator.Core.Doubao;

namespace LiveCaptionsTranslator.Core.Tests;

public class DoubaoDeviceProfileTests
{
    private const string RegisterResponse =
        """{"device_id":7412345678901234,"device_id_str":"7412345678901234","install_id":7412345678901235,"install_id_str":"7412345678901235"}""";

    private const string SettingsResponse =
        """{"message":"success","data":{"settings":{"asr_config":{"app_key":"test-app-key","app_id":"401734"}}}}""";

    [Fact]
    public void Register_body_is_valid_json_with_the_expected_fingerprint()
    {
        string body = DoubaoDeviceProfile.BuildRegisterBody(
            "cdid-value", "openudid-value", "clientudid-value", 1_700_000_000_000);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        Assert.Equal("ss_app_log", root.GetProperty("magic_tag").GetString());
        Assert.Equal(1_700_000_000_000, root.GetProperty("_gen_time").GetInt64());

        var header = root.GetProperty("header");
        Assert.Equal(401734, header.GetProperty("aid").GetInt32());
        Assert.Equal("oime", header.GetProperty("app_name").GetString());
        Assert.Equal("com.bytedance.android.doubaoime", header.GetProperty("package").GetString());
        Assert.Equal("android", header.GetProperty("device_platform").GetString());
        Assert.Equal("Pixel 7 Pro", header.GetProperty("device_model").GetString());
        Assert.Equal("cdid-value", header.GetProperty("cdid").GetString());
        Assert.Equal("openudid-value", header.GetProperty("openudid").GetString());
        Assert.Equal("clientudid-value", header.GetProperty("clientudid").GetString());

        // The registration body carries zeros; the real ids only exist after the response.
        Assert.Equal("0", header.GetProperty("device_id").GetString());
        Assert.Equal("0", header.GetProperty("install_id").GetString());
    }

    [Fact]
    public void Register_query_carries_the_signed_parameters()
    {
        string query = DoubaoDeviceProfile.BuildRegisterQuery("cdid-value", 1_700_000_000_000);

        Assert.Contains("aid=401734", query);
        Assert.Contains("app_name=oime", query);
        Assert.Contains("ssmix=a", query);
        Assert.Contains("_rticket=1700000000000", query);
        Assert.Contains("cdid=cdid-value", query);
        Assert.Contains("device_platform=android", query);
    }

    [Fact]
    public void Settings_query_carries_the_device_id()
    {
        string query = DoubaoDeviceProfile.BuildSettingsQuery("7412345678901234", "cdid-value", 1_700_000_000_000);

        Assert.Contains("device_id=7412345678901234", query);
        Assert.Contains("aid=401734", query);
        Assert.Contains("cdid=cdid-value", query);
    }

    [Fact]
    public void Stub_hash_is_the_upper_case_md5_of_the_body()
    {
        // Verified against `hashlib.md5(b"body=null").hexdigest().upper()`.
        Assert.Equal("46C03B52742B3F2615A3ABDF1636B754", DoubaoDeviceProfile.StubHash("body=null"));

        Assert.Equal("body=null", DoubaoDeviceProfile.SettingsBody);
        Assert.Equal(32, DoubaoDeviceProfile.StubHash("x").Length);
    }

    [Fact]
    public void Generated_ids_look_like_the_original_client()
    {
        Assert.Equal(16, DoubaoDeviceProfile.NewOpenUdid().Length);
        Assert.Matches("^[0-9a-f]{16}$", DoubaoDeviceProfile.NewOpenUdid());

        Assert.True(Guid.TryParse(DoubaoDeviceProfile.NewUdid(), out _));
        Assert.NotEqual(DoubaoDeviceProfile.NewUdid(), DoubaoDeviceProfile.NewUdid());
    }

    [Theory]
    [InlineData("""{"device_id_str":"123"}""", "123")]
    [InlineData("""{"device_id":"456"}""", "456")]
    [InlineData("""{"device_id":789}""", "789")]
    [InlineData("""{"device_id_str":"123","device_id":456}""", "123")]
    public void Device_id_is_read_from_either_field(string json, string expected)
        => Assert.Equal(expected, DoubaoRegistrationParser.ParseDeviceId(json));

    [Fact]
    public void Missing_device_id_is_reported_not_crashed()
    {
        var error = Assert.Throws<DoubaoRegistrationException>(
            () => DoubaoRegistrationParser.ParseDeviceId("""{"message":"blocked"}"""));

        Assert.Contains("device_id", error.Message);
    }

    [Fact]
    public void App_key_is_read_from_the_settings_path()
        => Assert.Equal("test-app-key", DoubaoRegistrationParser.ParseAppKey(SettingsResponse));

    [Theory]
    [InlineData("""{"data":{}}""")]
    [InlineData("""{"data":{"settings":{}}}""")]
    [InlineData("""{"data":{"settings":{"asr_config":{}}}}""")]
    [InlineData("""{"data":{"settings":{"asr_config":{"app_key":""}}}}""")]
    [InlineData("""{}""")]
    public void Missing_app_key_is_reported_not_crashed(string json)
    {
        var error = Assert.Throws<DoubaoRegistrationException>(
            () => DoubaoRegistrationParser.ParseAppKey(json));

        Assert.Contains("app_key", error.Message);
    }

    [Fact]
    public void Install_id_is_optional()
    {
        Assert.Equal(string.Empty, DoubaoRegistrationParser.ParseInstallId("""{"device_id":"1"}"""));
        Assert.Equal("9", DoubaoRegistrationParser.ParseInstallId("""{"install_id":"9"}"""));
    }

    [Fact]
    public void Credentials_are_only_complete_with_both_values()
    {
        Assert.True(new DoubaoCredentials("1", "key", "", "").IsComplete);
        Assert.False(new DoubaoCredentials("1", "", "", "").IsComplete);
        Assert.False(new DoubaoCredentials("", "key", "", "").IsComplete);
    }

    [Fact]
    public async Task Registering_hits_both_endpoints_and_returns_the_credentials()
    {
        var requested = new List<string>();

        var handler = new StubHttpMessageHandler(request =>
        {
            requested.Add(request.RequestUri!.ToString());

            if (request.RequestUri.Host == "log.snssdk.com")
                return StubHttpMessageHandler.Json(RegisterResponse);

            return StubHttpMessageHandler.Json(SettingsResponse);
        });

        var registrar = new DoubaoDeviceRegistrar(
            () => new HttpClient(handler),
            TimeProvider.System);

        var credentials = await registrar.RegisterAsync();

        Assert.Equal("7412345678901234", credentials.DeviceId);
        Assert.Equal("test-app-key", credentials.AppKey);
        Assert.Equal("7412345678901235", credentials.InstallId);
        Assert.True(credentials.IsComplete);

        Assert.Equal(2, requested.Count);
        Assert.StartsWith("https://log.snssdk.com/service/2/device_register/?", requested[0]);
        Assert.StartsWith("https://is.snssdk.com/service/settings/v3/?", requested[1]);
        Assert.Contains("device_id=7412345678901234", requested[1]);
    }

    [Fact]
    public async Task Registration_sends_the_mobile_user_agent_and_stub_header()
    {
        var seen = new List<(string Host, string? UserAgent, string? Stub, string? Body)>();

        var handler = new StubHttpMessageHandler(request =>
        {
            string body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            string? stub = request.Headers.TryGetValues("x-ss-stub", out var values) ? values.First() : null;
            // .NET parses User-Agent into product + comment, so join the parts back together.
            string? agent = request.Headers.TryGetValues("User-Agent", out var agents) ? string.Join(" ", agents) : null;
            seen.Add((request.RequestUri!.Host, agent, stub, body));

            return request.RequestUri.Host == "log.snssdk.com"
                ? StubHttpMessageHandler.Json(RegisterResponse)
                : StubHttpMessageHandler.Json(SettingsResponse);
        });

        await new DoubaoDeviceRegistrar(() => new HttpClient(handler)).RegisterAsync();

        Assert.Equal(2, seen.Count);

        Assert.Equal("log.snssdk.com", seen[0].Host);
        Assert.Equal(DoubaoDeviceProfile.UserAgent, seen[0].UserAgent);
        Assert.Null(seen[0].Stub);

        Assert.Equal("is.snssdk.com", seen[1].Host);
        Assert.Equal("body=null", seen[1].Body);
        Assert.Equal("46C03B52742B3F2615A3ABDF1636B754", seen[1].Stub);
    }

    [Fact]
    public async Task Http_failures_are_wrapped_and_never_escape_as_raw_exceptions()
    {
        var handler = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json("""{"message":"blocked"}""", HttpStatusCode.Forbidden));

        var registrar = new DoubaoDeviceRegistrar(() => new HttpClient(handler));

        var error = await Assert.ThrowsAsync<DoubaoRegistrationException>(() => registrar.RegisterAsync());

        Assert.Contains("403", error.Message);
    }

    [Fact]
    public async Task A_non_json_response_is_reported_as_a_registration_failure()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>nope</html>"),
        });

        var registrar = new DoubaoDeviceRegistrar(() => new HttpClient(handler));

        await Assert.ThrowsAsync<DoubaoRegistrationException>(() => registrar.RegisterAsync());
    }
}
