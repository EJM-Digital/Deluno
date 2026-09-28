using System.Net;
using System.Text;
using Deluno.Integrations.Subtitles;
using Deluno.Integrations.Subtitles.Providers;

namespace Deluno.Integrations.Tests.Subtitles;

/// <summary>
/// #946: OpenSubtitles answers a spent daily download allowance two ways —
/// HTTP 406 on <c>/download</c> with a reset time in the body, and a plain
/// 200 that is missing the link it promised. Both have to become the same
/// typed signal (<see cref="SubtitleProviderQuotaExhaustedException"/>) so
/// <c>SubtitleFetchService</c> can pause the provider instead of asking it
/// again every cycle.
/// </summary>
public sealed class OpenSubtitlesSubtitleProviderTests
{
    private static readonly SubtitleCandidate Candidate = new("opensubtitles", "12345", "en", false, false);
    private static readonly SubtitleProviderCredentials Credentials = new(Username: "person", Password: "secret", ApiKey: "key");

    [Fact]
    public async Task A_406_answer_becomes_a_quota_exhausted_exception_with_the_reset_time()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal)
            ? Json("""{"token":"tok"}""")
            : new HttpResponseMessage(HttpStatusCode.NotAcceptable)
            {
                Content = new StringContent(
                    """{"requests":100,"remaining":0,"message":"Quota reached.","reset_time_utc":"2026-04-30T00:00:00.000Z"}""",
                    Encoding.UTF8,
                    "application/json")
            });
        var provider = new OpenSubtitlesSubtitleProvider(new StubFactory(handler));

        var exception = await Assert.ThrowsAsync<SubtitleProviderQuotaExhaustedException>(
            () => provider.DownloadAsync(Candidate, Credentials, CancellationToken.None));

        Assert.Equal("opensubtitles", exception.ProviderKey);
        Assert.Equal(DateTimeOffset.Parse("2026-04-30T00:00:00.000Z"), exception.ResetUtc);
    }

    [Fact]
    public async Task An_accepted_response_with_no_link_becomes_a_quota_exhausted_exception_without_a_reset_time()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal)
            ? Json("""{"token":"tok"}""")
            : Json("""{"link":null}"""));
        var provider = new OpenSubtitlesSubtitleProvider(new StubFactory(handler));

        var exception = await Assert.ThrowsAsync<SubtitleProviderQuotaExhaustedException>(
            () => provider.DownloadAsync(Candidate, Credentials, CancellationToken.None));

        Assert.Equal("opensubtitles", exception.ProviderKey);
        Assert.Null(exception.ResetUtc);
    }

    [Fact]
    public async Task A_real_link_downloads_normally()
    {
        var downloadBytes = Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:03,000\nHello.\n");
        var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/login", StringComparison.Ordinal)) return Json("""{"token":"tok"}""");
            if (path.EndsWith("/download", StringComparison.Ordinal)) return Json("""{"link":"https://opensubtitles.example/file/1.srt"}""");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(downloadBytes) };
        });
        var provider = new OpenSubtitlesSubtitleProvider(new StubFactory(handler));

        var bytes = await provider.DownloadAsync(Candidate, Credentials, CancellationToken.None);

        Assert.Equal(downloadBytes, bytes);
    }

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
