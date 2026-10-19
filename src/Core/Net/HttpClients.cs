using System.Globalization;
using System.Net;

namespace LiveCaptionsTranslator.Core.Net
{
    /// <summary>
    /// Hands out the <see cref="HttpClient"/> an engine should use.
    /// <para>
    /// Some translation engines are simply not reachable from every network (Google, OpenAI,
    /// OpenRouter and DeepL all need a proxy in mainland China, while Baidu, Youdao and iFlytek
    /// do not), so the host configures one proxy URL and picks which engines go through it.
    /// </para>
    /// <para>
    /// When no proxy is configured at all the system default applies, which is exactly what the
    /// upstream project always did - configuring nothing cannot change existing behaviour.
    /// </para>
    /// </summary>
    public static class HttpClients
    {
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

        private static readonly object Gate = new();
        private static readonly Dictionary<string, HttpClient> Cache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The client for one engine: through the proxy when the user asked for it, direct when a
        /// proxy exists but this engine was not selected, and the system default when no proxy has
        /// been configured.
        /// </summary>
        public static HttpClient Resolve(
            bool useProxy,
            string? configuredProxy,
            TimeSpan? timeout = null,
            string? scope = null)
        {
            if (string.IsNullOrWhiteSpace(configuredProxy))
                return SystemDefault(timeout, scope);

            return useProxy
                ? For(configuredProxy, timeout, scope)
                : Direct(timeout, scope);
        }

        /// <summary>Client that follows the machine's proxy settings.</summary>
        public static HttpClient SystemDefault(TimeSpan? timeout = null, string? scope = null) =>
            Get(null, useSystemProxy: true, timeout, scope);

        /// <summary>Client that ignores every proxy and connects straight out.</summary>
        public static HttpClient Direct(TimeSpan? timeout = null, string? scope = null) =>
            Get(null, useSystemProxy: false, timeout, scope);

        /// <summary>A client routed through <paramref name="proxyUrl"/>, cached per URL.</summary>
        public static HttpClient For(string proxyUrl, TimeSpan? timeout = null, string? scope = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(proxyUrl);

            return Get(proxyUrl.Trim(), useSystemProxy: false, timeout, scope);
        }

        /// <summary>
        /// Checks a user supplied proxy string. Accepts <c>http</c>, <c>https</c>, <c>socks4</c>,
        /// <c>socks4a</c> and <c>socks5</c> URLs.
        /// </summary>
        public static bool TryValidateProxy(string? proxyUrl, out string? error)
        {
            error = null;

            if (string.IsNullOrWhiteSpace(proxyUrl))
                return true;

            if (!Uri.TryCreate(proxyUrl.Trim(), UriKind.Absolute, out var uri))
            {
                error = "代理地址不是合法的 URL，例如 http://127.0.0.1:7897 或 socks5://127.0.0.1:1080";
                return false;
            }

            if (uri.Scheme is not ("http" or "https" or "socks4" or "socks4a" or "socks5"))
            {
                error = $"不支持的代理协议 {uri.Scheme}，请用 http、https 或 socks5";
                return false;
            }

            if (uri.Port <= 0)
            {
                error = "代理地址缺少端口";
                return false;
            }

            return true;
        }

        private static HttpClient Get(
            string? proxyUrl,
            bool useSystemProxy,
            TimeSpan? timeout,
            string? scope)
        {
            TimeSpan effective = timeout ?? DefaultTimeout;
            string key = string.Create(
                CultureInfo.InvariantCulture,
                $"{scope ?? "-"}|{proxyUrl ?? "-"}|{useSystemProxy}|{effective.TotalSeconds}");

            lock (Gate)
            {
                if (Cache.TryGetValue(key, out var cached))
                    return cached;

                var client = Create(proxyUrl, useSystemProxy, effective);
                Cache[key] = client;
                return client;
            }
        }

        private static HttpClient Create(string? proxyUrl, bool useSystemProxy, TimeSpan timeout)
        {
            var handler = new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(10),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            };

            if (useSystemProxy)
            {
                // Leave the handler alone: .NET then uses the machine's proxy settings.
            }
            else if (proxyUrl is not null)
            {
                handler.Proxy = new WebProxy(proxyUrl);
                handler.UseProxy = true;
            }
            else
            {
                handler.UseProxy = false;
            }

            return new HttpClient(handler)
            {
                Timeout = timeout,
            };
        }
    }
}
