using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace LiveCaptionsTranslator.Core.Iflytek
{
    /// <summary>
    /// The WebSocket handshake signature iFlytek expects: HMAC-SHA256 over
    /// <c>host</c>/<c>date</c>/<c>request-line</c>, base64 encoded, then base64 encoded again
    /// into the <c>authorization</c> query parameter.
    /// </summary>
    public static class IflytekSignature
    {
        /// <summary>Renders a timestamp the way the service requires, e.g. <c>Sat, 17 Oct 2026 15:09:03 GMT</c>.</summary>
        public static string FormatDate(DateTimeOffset timestamp) =>
            timestamp.UtcDateTime.ToString("R", CultureInfo.InvariantCulture);

        public static string SigningString(string host, string path, string date, string method = "GET") =>
            $"host: {host}\ndate: {date}\n{method} {path} HTTP/1.1";

        /// <summary>Returns <c>base64(HMAC-SHA256(api_secret, signing string))</c>.</summary>
        public static string Sign(
            string apiSecret,
            string host,
            string path,
            string date,
            string method = "GET")
        {
            string origin = SigningString(host, path, date, method);

            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(apiSecret));
            return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(origin)));
        }

        public static string AuthorizationHeader(string apiKey, string signature) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"api_key=\"{apiKey}\", algorithm=\"hmac-sha256\", " +
                $"headers=\"host date request-line\", signature=\"{signature}\""));

        public static string BuildSignedUrl(
            IflytekCredentials credentials,
            string date,
            string scheme = "wss")
        {
            ArgumentNullException.ThrowIfNull(credentials);

            string signature = Sign(credentials.ApiSecret, credentials.Host, credentials.Path, date);
            string authorization = AuthorizationHeader(credentials.ApiKey, signature);

            return $"{scheme}://{credentials.Host}{credentials.Path}" +
                   $"?authorization={Uri.EscapeDataString(authorization)}" +
                   $"&date={Uri.EscapeDataString(date)}" +
                   $"&host={Uri.EscapeDataString(credentials.Host)}";
        }

        public static string BuildSignedUrl(
            IflytekCredentials credentials,
            DateTimeOffset timestamp,
            string scheme = "wss") =>
            BuildSignedUrl(credentials, FormatDate(timestamp), scheme);
    }
}
