using System.Net;
using System.Net.Http;

namespace LiveCaptionsTranslator.Core.Tests;

/// <summary>Captures the outgoing request so tests can assert on it, and replies with a canned body.</summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> responder;

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => this.responder = responder;

    public HttpRequestMessage? LastRequest { get; private set; }

    public string? LastBody { get; private set; }

    public int CallCount { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        CallCount++;
        LastRequest = request;
        LastBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);

        return responder(request);
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
}
