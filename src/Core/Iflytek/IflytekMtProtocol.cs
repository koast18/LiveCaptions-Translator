using System.Text;
using System.Text.Json;

namespace LiveCaptionsTranslator.Core.Iflytek
{
    /// <summary>Language codes used by the iFlytek machine-translation endpoint.</summary>
    public static class IflytekMtLanguages
    {
        /// <summary>Language assumed when the host does not know what was recognised.</summary>
        public const string DefaultSource = "en";

        /// <summary>Chinese target code used by iFlytek (not the BCP 47 <c>zh</c>).</summary>
        public const string Chinese = "cn";

        /// <summary>
        /// Maps a host language id (<c>zh_cn</c>, <c>en-US</c>, <c>ja</c>, ...) onto the code
        /// iFlytek expects (<c>cn</c>, <c>en</c>, <c>ja</c>, ...).
        /// </summary>
        public static string ToIflytek(string? language)
        {
            if (string.IsNullOrWhiteSpace(language))
                return DefaultSource;

            string normalized = language.Replace('_', '-').Trim().ToLowerInvariant();
            int separator = normalized.IndexOf('-');
            string primary = separator > 0 ? normalized[..separator] : normalized;

            return primary switch
            {
                "zh" or "cn" or "chs" or "hans" => Chinese,
                "cht" or "hant" => Chinese,
                "jp" => "ja",
                "kr" => "ko",
                _ => primary,
            };
        }
    }

    /// <summary>Request and response encoding for <c>POST https://itrans.xf-yun.com/v1/its</c>.</summary>
    public static class IflytekMtProtocol
    {
        public const string DefaultHost = "itrans.xf-yun.com";

        public const string DefaultPath = "/v1/its";

        /// <summary>Builds the JSON body: <c>common</c>, <c>business</c>, then the base64 <c>data</c>.</summary>
        public static string BuildRequestBody(string appId, string from, string to, string text)
        {
            ArgumentNullException.ThrowIfNull(text);

            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

            return "{\"common\":{\"app_id\":" + IflytekProtocol.Quote(appId) + "}" +
                   ",\"business\":{\"from\":" + IflytekProtocol.Quote(from) +
                   ",\"to\":" + IflytekProtocol.Quote(to) + "}" +
                   ",\"data\":{\"text\":" + IflytekProtocol.Quote(encoded) + "}}";
        }

        /// <summary>Extracts the translated text, throwing <see cref="IflytekApiException"/> on an error code.</summary>
        public static string ParseResponse(string json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            int code = 0;
            if (root.TryGetProperty("code", out var codeElement) &&
                codeElement.ValueKind == JsonValueKind.Number)
            {
                code = codeElement.GetInt32();
            }

            if (code != 0)
            {
                string message = root.TryGetProperty("message", out var messageElement) &&
                                 messageElement.ValueKind == JsonValueKind.String
                    ? messageElement.GetString()!
                    : string.Empty;

                throw new IflytekApiException(code, message);
            }

            if (!root.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("result", out var result) ||
                result.ValueKind != JsonValueKind.Object ||
                !result.TryGetProperty("text", out var text) ||
                text.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("讯飞机器翻译的响应里没有译文。");
            }

            return Encoding.UTF8.GetString(Convert.FromBase64String(text.GetString()!));
        }
    }

    /// <summary>Credentials and endpoint for the machine-translation service.</summary>
    public sealed record IflytekMtOptions
    {
        public string ApiKey { get; init; } = string.Empty;

        public string ApiSecret { get; init; } = string.Empty;

        /// <summary>iFlytek 开放平台 APPID. Required; the IAT <c>100IME</c> credentials do not work here.</summary>
        public string AppId { get; init; } = string.Empty;

        public string Host { get; init; } = IflytekMtProtocol.DefaultHost;

        public string Path { get; init; } = IflytekMtProtocol.DefaultPath;

        public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

        /// <summary>Whether every field needed to sign a request is present.</summary>
        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(ApiKey) &&
            !string.IsNullOrWhiteSpace(ApiSecret) &&
            !string.IsNullOrWhiteSpace(AppId);

        /// <summary>
        /// Reads the credentials out of a host settings bag. Unlike the IAT provider there is no
        /// committed default: machine translation needs a real 开放平台 account.
        /// </summary>
        public static IflytekMtOptions FromSettings(IReadOnlyDictionary<string, string>? settings)
        {
            if (settings is null)
                return new IflytekMtOptions();

            string Read(string key) =>
                settings.TryGetValue(key, out string? value) ? value?.Trim() ?? string.Empty : string.Empty;

            return new IflytekMtOptions
            {
                ApiKey = Read(IflytekSettingKeys.ApiKey),
                ApiSecret = Read(IflytekSettingKeys.ApiSecret),
                AppId = Read(IflytekSettingKeys.AppId),
                Host = Read(IflytekSettingKeys.Host) is { Length: > 0 } host
                    ? host
                    : IflytekMtProtocol.DefaultHost,
                Path = Read(IflytekSettingKeys.Path) is { Length: > 0 } path
                    ? path
                    : IflytekMtProtocol.DefaultPath,
            };
        }
    }
}
