using LiveCaptionsTranslator.Core.Iflytek;

namespace LiveCaptionsTranslator.Core.Tests;

public class IflytekSignatureTests
{
    private static readonly IflytekCredentials Credentials = new()
    {
        ApiKey = Golden.String("apiKey"),
        ApiSecret = Golden.String("apiSecret"),
        Host = Golden.String("host"),
        Path = Golden.String("path"),
        AppId = Golden.String("appId"),
    };

    [Fact]
    public void Credentials_defaults_match_the_reference_client()
    {
        Assert.Equal(Golden.String("apiKey"), IflytekCredentials.DefaultApiKey);
        Assert.Equal(Golden.String("apiSecret"), IflytekCredentials.DefaultApiSecret);
        Assert.Equal(Golden.String("host"), IflytekCredentials.DefaultHost);
        Assert.Equal(Golden.String("path"), IflytekCredentials.DefaultPath);
        Assert.Equal(Golden.String("appId"), IflytekCredentials.DefaultAppId);
    }

    [Fact]
    public void Signed_url_matches_the_python_reference_byte_for_byte()
    {
        int checked_ = 0;

        foreach (var vector in Golden.Items("signatures"))
        {
            long unixTime = vector.GetProperty("unixTime").GetInt64();
            string expectedDate = vector.GetProperty("date").GetString()!;
            string expectedSigningString = vector.GetProperty("signingString").GetString()!;
            string expectedSignature = vector.GetProperty("signature").GetString()!;
            string expectedAuthorization = vector.GetProperty("authorization").GetString()!;
            string expectedUrl = vector.GetProperty("url").GetString()!;

            var timestamp = DateTimeOffset.FromUnixTimeSeconds(unixTime);

            Assert.Equal(expectedDate, IflytekSignature.FormatDate(timestamp));
            Assert.Equal(
                expectedSigningString,
                IflytekSignature.SigningString(Credentials.Host, Credentials.Path, expectedDate));
            Assert.Equal(
                expectedSignature,
                IflytekSignature.Sign(Credentials.ApiSecret, Credentials.Host, Credentials.Path, expectedDate));
            Assert.Equal(
                expectedAuthorization,
                IflytekSignature.AuthorizationHeader(Credentials.ApiKey, expectedSignature));
            Assert.Equal(expectedUrl, IflytekSignature.BuildSignedUrl(Credentials, expectedDate));
            Assert.Equal(expectedUrl, IflytekSignature.BuildSignedUrl(Credentials, timestamp));

            checked_++;
        }

        Assert.Equal(3, checked_);
    }

    [Fact]
    public void Signing_string_has_no_trailing_newline()
    {
        string signingString = IflytekSignature.SigningString("h", "/p", "d");

        Assert.Equal("host: h\ndate: d\nGET /p HTTP/1.1", signingString);
        Assert.DoesNotContain('\r', signingString);
    }
}
