using System.Net;
using System.Net.Http;
using System.Text;

using LiveCaptionsTranslator.Core.Net;

namespace LiveCaptionsTranslator.Core.Doubao
{
    /// <summary>
    /// Registers a fresh device with the IME's endpoints and exchanges it for an ASR token.
    /// <para>
    /// Both calls are plain HTTP: the registration body is ordinary JSON (the IME's ttEncrypt layer
    /// is only used by other endpoints) and the settings call signs its fixed body with its MD5.
    /// No native dependency, no external process.
    /// </para>
    /// </summary>
    public sealed class DoubaoDeviceRegistrar
    {
        private readonly Func<HttpClient> clientFactory;
        private readonly TimeProvider clock;

        public DoubaoDeviceRegistrar(Func<HttpClient>? clientFactory = null, TimeProvider? clock = null)
        {
            this.clientFactory = clientFactory ?? (() => HttpClients.SystemDefault(TimeSpan.FromSeconds(15)));
            this.clock = clock ?? TimeProvider.System;
        }

        /// <summary>Generates a device fingerprint, registers it, and returns the ASR credentials.</summary>
        public async Task<DoubaoCredentials> RegisterAsync(CancellationToken cancellationToken = default)
        {
            string cdid = DoubaoDeviceProfile.NewUdid();
            string openudid = DoubaoDeviceProfile.NewOpenUdid();
            string clientudid = DoubaoDeviceProfile.NewUdid();

            long registeredAt = clock.GetUtcNow().ToUnixTimeMilliseconds();

            string registerJson = await PostAsync(
                DoubaoDeviceProfile.RegisterUrl +
                "?" + DoubaoDeviceProfile.BuildRegisterQuery(cdid, registeredAt),
                DoubaoDeviceProfile.BuildRegisterBody(cdid, openudid, clientudid, registeredAt),
                "application/json",
                extraHeaders: null,
                cancellationToken);

            string deviceId;
            string installId;

            try
            {
                deviceId = DoubaoRegistrationParser.ParseDeviceId(registerJson);
                installId = DoubaoRegistrationParser.ParseInstallId(registerJson);
            }
            catch (Exception exception) when (exception is not DoubaoRegistrationException)
            {
                throw new DoubaoRegistrationException($"device_register 响应无法解析：{exception.Message}", exception);
            }

            long tokenAt = clock.GetUtcNow().ToUnixTimeMilliseconds();

            string settingsJson = await PostAsync(
                DoubaoDeviceProfile.SettingsUrl +
                "?" + DoubaoDeviceProfile.BuildSettingsQuery(deviceId, cdid, tokenAt),
                DoubaoDeviceProfile.SettingsBody,
                "application/x-www-form-urlencoded",
                new[] { new KeyValuePair<string, string>("x-ss-stub", DoubaoDeviceProfile.StubHash(DoubaoDeviceProfile.SettingsBody)) },
                cancellationToken);

            string appKey;

            try
            {
                appKey = DoubaoRegistrationParser.ParseAppKey(settingsJson);
            }
            catch (Exception exception) when (exception is not DoubaoRegistrationException)
            {
                throw new DoubaoRegistrationException($"settings 响应无法解析：{exception.Message}", exception);
            }

            return new DoubaoCredentials(deviceId, appKey, installId, cdid);
        }

        private async Task<string> PostAsync(
            string url,
            string body,
            string contentType,
            IReadOnlyList<KeyValuePair<string, string>>? extraHeaders,
            CancellationToken cancellationToken)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.TryAddWithoutValidation("User-Agent", DoubaoDeviceProfile.UserAgent);

                if (extraHeaders is not null)
                {
                    foreach (var header in extraHeaders)
                        request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                request.Content = new StringContent(body, Encoding.UTF8, contentType);

                var client = clientFactory();
                using var response = await client.SendAsync(request, cancellationToken);
                string payload = await response.Content.ReadAsStringAsync(cancellationToken);

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new DoubaoRegistrationException(
                        $"{url} 返回 HTTP {(int)response.StatusCode}：{Truncate(payload)}");
                }

                return payload;
            }
            catch (DoubaoRegistrationException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new DoubaoRegistrationException($"请求 {url} 失败：{exception.Message}", exception);
            }
        }

        private static string Truncate(string value) =>
            value.Length <= 200 ? value : value[..200] + "…";
    }
}
