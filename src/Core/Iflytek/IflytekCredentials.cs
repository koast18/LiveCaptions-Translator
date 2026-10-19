namespace LiveCaptionsTranslator.Core.Iflytek
{
    /// <summary>
    /// Connection details for the iFlytek IAT endpoint.
    /// <para>
    /// The defaults are the anonymous credentials that 讯飞输入法's own web client embeds
    /// (<c>app_id 100IME</c>). They are committed on purpose so the app works out of the box,
    /// but they belong to iFlytek, can be rotated at any time, and are not covered by any
    /// public API agreement - see the README. Every field is overridable so the credentials
    /// can be swapped without a code change.
    /// </para>
    /// </summary>
    public sealed record IflytekCredentials
    {
        public const string DefaultApiKey = "15461402826c8360ebcf1270cbba88e5";

        public const string DefaultApiSecret = "sH8v2wSSxxHTy1UNfN6dFaSqVjlmR7BB";

        public const string DefaultHost = "100ime-iat-api.xfyun.cn";

        public const string DefaultPath = "/v2/iat";

        public const string DefaultAppId = "100IME";

        public string ApiKey { get; init; } = DefaultApiKey;

        public string ApiSecret { get; init; } = DefaultApiSecret;

        public string Host { get; init; } = DefaultHost;

        public string Path { get; init; } = DefaultPath;

        public string AppId { get; init; } = DefaultAppId;

        public static IflytekCredentials Default { get; } = new();
    }
}
