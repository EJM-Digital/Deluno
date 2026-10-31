using System.Net;
using System.Text;

namespace Deluno.Integrations.Tests.Subtitles;

/// <summary>A canned-reply HTTP seam shared by the subtitle provider tests.</summary>
internal static class ProviderStubs
{
    public static IHttpClientFactory Factory(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => new StubFactory(new StubHandler(respond));

    public static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Html(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    public static HttpResponseMessage Bytes(byte[] body)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
