using System.Net.Http;
using System.Text;

using LiveCaptionsTranslator.Core.Iflytek;
using LiveCaptionsTranslator.Core.Net;

namespace LiveCaptionsTranslator.Core.Translation
{
    /// <summary>
    /// Translation through iFlytek's machine-translation endpoint
    /// (<c>POST https://itrans.xf-yun.com/v1/its</c>).
    /// <para>
    /// This needs a real iFlytek 开放平台 account with the machine-translation service enabled.
    /// The anonymous <c>100IME</c> credentials used by the speech-recognition provider do not
    /// work here, so the provider reports itself as unusable until credentials are filled in -
    /// which must never affect the other engines.
    /// </para>
    /// </summary>
    public sealed class IflytekMtProvider : ITranslationProvider
    {
        public const string ProviderId = "iflytek-mt";

        private readonly IflytekMtOptions options;
        private readonly Func<HttpClient> clientFactory;
        private readonly TimeProvider timeProvider;

        public IflytekMtProvider(
            IflytekMtOptions? options = null,
            Func<HttpClient>? clientFactory = null,
            TimeProvider? timeProvider = null)
        {
            this.options = options ?? new IflytekMtOptions();
            this.clientFactory = clientFactory ?? (() => HttpClients.SystemDefault(this.options.Timeout));
            this.timeProvider = timeProvider ?? TimeProvider.System;
        }

        public string Id => ProviderId;

        public string DisplayName => "讯飞机器翻译";

        /// <summary>True: without 开放平台 credentials this engine cannot be used at all.</summary>
        public bool RequiresConfiguration => true;

        public bool IsLlmBased => false;

        public bool IsConfigured => options.IsConfigured;

        public async Task<string> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (!options.IsConfigured)
            {
                throw new InvalidOperationException(
                    "讯飞机器翻译需要在设置里填写开放平台的 APPID / APIKey / APISecret。");
            }

            if (string.IsNullOrWhiteSpace(request.Text))
                return string.Empty;

            string from = IflytekMtLanguages.ToIflytek(request.SourceLanguage);
            string to = IflytekMtLanguages.ToIflytek(request.TargetLanguage);
            string date = IflytekSignature.FormatDate(timeProvider.GetUtcNow());

            string signature = IflytekSignature.Sign(
                options.ApiSecret, options.Host, options.Path, date, "POST");

            using var message = new HttpRequestMessage(
                HttpMethod.Post, $"https://{options.Host}{options.Path}");

            message.Headers.TryAddWithoutValidation(
                "Authorization", IflytekSignature.AuthorizationHeader(options.ApiKey, signature));
            message.Headers.TryAddWithoutValidation("Date", date);
            message.Headers.TryAddWithoutValidation("Host", options.Host);

            message.Content = new StringContent(
                IflytekMtProtocol.BuildRequestBody(options.AppId, from, to, request.Text),
                Encoding.UTF8,
                "application/json");

            message.Content.Headers.TryAddWithoutValidation("Accept", "application/json,version=1.0");

            var client = clientFactory();
            using var response = await client.SendAsync(message, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"讯飞机器翻译返回 HTTP {(int)response.StatusCode}：{body}");
            }

            return IflytekMtProtocol.ParseResponse(body);
        }
    }
}
