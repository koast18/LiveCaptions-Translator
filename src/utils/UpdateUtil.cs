using System.Net.Http;
using System.Text.Json;

namespace LiveCaptionsTranslator.utils
{
    public static class UpdateUtil
    {
        public const string GitHubRepoUrl = "https://github.com/koast18/LiveCaptions-Translator";
        public const string GitHubReleasesUrl = "https://github.com/koast18/LiveCaptions-Translator/releases";
        public const string GitHubLatestReleaseApi = "https://api.github.com/repos/koast18/LiveCaptions-Translator/releases/latest";

        public static async Task<string> GetLatestVersion()
        {
            string apiUrl = GitHubLatestReleaseApi;

            // GitHub is unreachable from some networks, so the update check follows the proxy
            // whenever one is configured - it never talks to a domestic-only service.
            var client = HttpClients.Resolve(
                useProxy: true,
                Translator.Setting?.HttpProxy,
                TimeSpan.FromSeconds(3),
                "UpdateUtil");

            using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            request.Headers.UserAgent.ParseAdd("LiveCaptions-Translator");

            using var message = await client.SendAsync(request);
            message.EnsureSuccessStatusCode();
            var response = await message.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(response);
            var latestVersionRaw = doc.RootElement.GetProperty("tag_name").GetString();
            var latestVersion = string.IsNullOrEmpty(latestVersionRaw)
                ? String.Empty
                : RegexPatterns.VersionNumber().Replace(latestVersionRaw, "");
            return latestVersion;
        }


    }
}
