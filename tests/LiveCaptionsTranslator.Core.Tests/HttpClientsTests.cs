using LiveCaptionsTranslator.Core.Net;

namespace LiveCaptionsTranslator.Core.Tests;

public class HttpClientsTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("http://127.0.0.1:7897", true)]
    [InlineData("https://proxy.example:8080", true)]
    [InlineData("socks5://127.0.0.1:1080", true)]
    [InlineData("socks4://127.0.0.1:1080", true)]
    [InlineData("ftp://127.0.0.1:21", false)]
    [InlineData("not a url", false)]
    [InlineData("127.0.0.1:7897", false)]
    public void Proxy_validation(string? proxyUrl, bool expected)
    {
        Assert.Equal(expected, HttpClients.TryValidateProxy(proxyUrl, out string? error));
        Assert.Equal(expected, error is null);
    }

    [Fact]
    public void Blank_proxy_falls_back_to_the_system_default()
    {
        // No proxy configured must behave exactly like upstream: follow the machine settings.
        Assert.Same(
            HttpClients.SystemDefault(TimeSpan.FromSeconds(8), "engine"),
            HttpClients.Resolve(useProxy: false, configuredProxy: null, TimeSpan.FromSeconds(8), "engine"));

        Assert.Same(
            HttpClients.SystemDefault(TimeSpan.FromSeconds(8), "engine"),
            HttpClients.Resolve(useProxy: true, configuredProxy: "  ", TimeSpan.FromSeconds(8), "engine"));
    }

    [Fact]
    public void Configured_proxy_is_used_only_by_the_selected_engines()
    {
        var viaProxy = HttpClients.Resolve(true, "http://127.0.0.1:7897", TimeSpan.FromSeconds(8), "engine-a");
        var direct = HttpClients.Resolve(false, "http://127.0.0.1:7897", TimeSpan.FromSeconds(8), "engine-a");

        Assert.NotSame(viaProxy, direct);
        Assert.Same(viaProxy, HttpClients.For("http://127.0.0.1:7897", TimeSpan.FromSeconds(8), "engine-a"));
        Assert.Same(direct, HttpClients.Direct(TimeSpan.FromSeconds(8), "engine-a"));
    }

    [Fact]
    public void Clients_are_cached_and_scoped()
    {
        var a = HttpClients.Resolve(true, "http://127.0.0.1:7897", TimeSpan.FromSeconds(8), "engine-a");
        var aAgain = HttpClients.Resolve(true, "http://127.0.0.1:7897", TimeSpan.FromSeconds(8), "engine-a");
        var b = HttpClients.Resolve(true, "http://127.0.0.1:7897", TimeSpan.FromSeconds(8), "engine-b");

        Assert.Same(a, aAgain);

        // Each engine gets its own instance: upstream mutates DefaultRequestHeaders per call.
        Assert.NotSame(a, b);
    }

    [Fact]
    public void Different_timeouts_do_not_share_a_client()
    {
        var shortTimeout = HttpClients.Direct(TimeSpan.FromSeconds(8), "engine");
        var longTimeout = HttpClients.Direct(TimeSpan.FromSeconds(30), "engine");

        Assert.NotSame(shortTimeout, longTimeout);
        Assert.Equal(TimeSpan.FromSeconds(8), shortTimeout.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(30), longTimeout.Timeout);
    }
}
