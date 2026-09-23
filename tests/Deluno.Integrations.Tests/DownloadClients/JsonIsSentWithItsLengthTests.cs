using System.Text;
using Deluno.Infrastructure.Http;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #605: outbound JSON says how long it is. NZBGet's embedded server needs the
/// length; a chunked body with none reached it as "content length is 0", and
/// Deluno could not talk to a real NZBGet at all.
/// </summary>
public sealed class JsonIsSentWithItsLengthTests
{
    [Fact]
    public async Task A_posted_body_carries_its_length_and_is_not_chunked()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);

        await http.PostSizedJsonAsync(new Uri("http://127.0.0.1:6789/jsonrpc"), new { method = "version", @params = Array.Empty<object>(), id = 1 });

        Assert.NotEqual(true, handler.Request!.Headers.TransferEncodingChunked);
        Assert.Equal(Encoding.UTF8.GetByteCount(handler.Body!), handler.Request.Content!.Headers.ContentLength);
        Assert.Equal("application/json", handler.Request.Content.Headers.ContentType!.MediaType);
        // Web defaults, as PostAsJsonAsync used: nothing on the wire changes but the framing.
        Assert.Equal("""{"method":"version","params":[],"id":1}""", handler.Body);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            // Asked for before the body is read, as the socket handler does.
            _ = request.Content!.Headers.ContentLength;
            Body = await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }
}
