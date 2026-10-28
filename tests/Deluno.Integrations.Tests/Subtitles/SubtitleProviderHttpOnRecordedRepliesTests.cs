using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Deluno.Integrations.Subtitles;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.Subtitles;

/// <summary>
/// #1301: the four calls every subtitle provider shares (<see cref="SubtitleProviderHttp"/>) against what the services really answered
/// (tests/fixtures/recorded/gestdown, subf2m, supersubtitles and opensubtitles): a reply read, a 404 that is "nothing here", a page that is
/// not the JSON promised, a refusal that throws with its status, and the two outcomes (a rate limit, a silent site) that are not "nothing".
/// A 429 and a silent site have no body of any service: they are the status line and a handler that never answers.
/// </summary>
public sealed class SubtitleProviderHttpOnRecordedRepliesTests
{
    private sealed class Replying(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(answer(request));
    }

    private sealed class Silent : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new OperationCanceledException();
    }

    private static HttpClient Client(HttpMessageHandler handler) => new(handler, disposeHandler: false);

    private static HttpResponseMessage Reply(Recording recording, int exchange) => recording.Exchanges[exchange].Reply.ToResponse();

    private static HttpResponseMessage RateLimited(string retryAfterSeconds)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", retryAfterSeconds);
        return response;
    }

    // ---- GetJsonAsync ----

    [Fact]
    public async Task A_json_reply_from_a_service_is_read_into_the_shape_asked_for()
    {
        var recording = RecordedReplies.Gestdown("breaking-bad-s01e01-english");
        using var client = Client(new Replying(_ => Reply(recording, 0)));

        var shows = await SubtitleProviderHttp.GetJsonAsync<JsonObject>(client, "https://api.gestdown.info/shows/search/Breaking%20Bad", "gestdown", CancellationToken.None);

        Assert.NotNull(shows);
        Assert.Equal(recording.Exchanges[0].Reply.Json()["shows"]!.AsArray().Count, shows!["shows"]!.AsArray().Count);
    }

    [Fact]
    public async Task A_404_from_a_service_is_nothing_found_and_a_page_that_is_not_json_is_nothing_too_not_a_failure()
    {
        var notFound = RecordedReplies.Gestdown("show-not-found");
        Assert.Equal(404, notFound.Exchanges[0].Reply.Status);
        using var missing = Client(new Replying(_ => Reply(notFound, 0)));
        Assert.Null(await SubtitleProviderHttp.GetJsonAsync<JsonObject>(missing, "https://api.gestdown.info/shows/search/x", "gestdown", CancellationToken.None));

        var html = RecordedReplies.Subf2m("sintel-english");
        Assert.StartsWith("text/html", html.Exchanges[0].Reply.Headers["content-type"], StringComparison.Ordinal);
        using var page = Client(new Replying(_ => Reply(html, 0)));
        Assert.Null(await SubtitleProviderHttp.GetJsonAsync<JsonObject>(page, "https://subf2m.co/subtitles/searchbytitle", "subf2m", CancellationToken.None));
    }

    [Fact]
    public async Task A_refusal_throws_with_its_status_so_it_is_recorded_as_a_failure_and_not_as_nothing_found()
    {
        var refused = RecordedReplies.Opensubtitles("wrong-key");
        var index = refused.Exchanges.ToList().FindIndex(exchange => exchange.Reply.Status == 403);
        using var client = Client(new Replying(_ => Reply(refused, index)));

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => SubtitleProviderHttp.GetJsonAsync<JsonObject>(client, "https://api.opensubtitles.com/api/v1/subtitles", "opensubtitles", CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, failure.StatusCode);
        Assert.Contains("opensubtitles returned HTTP 403", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rate_limit_throws_its_own_exception_with_the_wait_the_service_asked_for_and_a_silent_site_is_unreachable_not_empty()
    {
        using var limited = Client(new Replying(_ => RateLimited("90")));
        var limit = await Assert.ThrowsAsync<SubtitleProviderRateLimitedException>(() => SubtitleProviderHttp.GetJsonAsync<JsonObject>(limited, "https://x.example/api", "subdl", CancellationToken.None));
        Assert.Equal("subdl", limit.ProviderKey);
        Assert.Equal(TimeSpan.FromSeconds(90), limit.RetryAfter);

        using var silent = Client(new Silent());
        var quiet = await Assert.ThrowsAsync<HttpRequestException>(() => SubtitleProviderHttp.GetJsonAsync<JsonObject>(silent, "https://x.example/api", "subdl", CancellationToken.None));
        Assert.Equal("subdl did not answer in time.", quiet.Message);
    }

    // ---- SendJsonAsync ----

    [Fact]
    public async Task A_posted_request_a_service_refuses_throws_with_the_refusal_and_sends_the_body_it_was_given()
    {
        var refused = RecordedReplies.Opensubtitles("wrong-key");
        var index = refused.Exchanges.ToList().FindIndex(exchange => exchange.Request.Method == "POST" && exchange.Reply.Status == 403);
        string? sent = null;
        using var client = Client(new Replying(request => { sent = request.Content!.ReadAsStringAsync().Result; return Reply(refused, index); }));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.opensubtitles.com/api/v1/login") { Content = new StringContent("{\"username\":\"u\",\"password\":\"p\"}", Encoding.UTF8, "application/json") };

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => SubtitleProviderHttp.SendJsonAsync<JsonObject>(client, request, "opensubtitles", CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, failure.StatusCode);
        Assert.Equal("{\"username\":\"u\",\"password\":\"p\"}", sent);
    }

    [Fact]
    public async Task A_posted_request_answered_with_a_404_is_nothing_found()
    {
        var notFound = RecordedReplies.Gestdown("show-not-found");
        using var client = Client(new Replying(_ => Reply(notFound, 0)));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.gestdown.info/x") { Content = new StringContent("{}") };

        Assert.Null(await SubtitleProviderHttp.SendJsonAsync<JsonObject>(client, request, "gestdown", CancellationToken.None));
    }

    // ---- GetTextAsync ----

    [Fact]
    public async Task A_page_is_read_as_the_text_the_site_sent()
    {
        var recording = RecordedReplies.Subf2m("sintel-english");
        using var client = Client(new Replying(_ => Reply(recording, 1)));

        var text = await SubtitleProviderHttp.GetTextAsync(client, "https://subf2m.co/subtitles/sintel", "subf2m", CancellationToken.None);

        Assert.Equal(recording.Exchanges[1].Reply.Body, text);
        Assert.NotEmpty(text);
    }

    [Fact]
    public async Task A_page_the_site_does_not_have_reads_as_empty_and_a_page_it_refuses_throws_with_the_status()
    {
        var gone = RecordedReplies.Subf2m("download-not-found");
        using var missing = Client(new Replying(_ => Reply(gone, 0)));
        Assert.Equal(string.Empty, await SubtitleProviderHttp.GetTextAsync(missing, "https://subf2m.co/subtitles/sintel/english/1/download", "subf2m", CancellationToken.None));

        var blocked = RecordedReplies.Supersubtitles("show-search");
        Assert.Equal(403, blocked.Exchanges[1].Reply.Status);
        using var client = Client(new Replying(_ => Reply(blocked, 1)));
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => SubtitleProviderHttp.GetTextAsync(client, "https://feliratok.eu/index.php", "supersubtitles", CancellationToken.None));
        Assert.Equal(HttpStatusCode.Forbidden, failure.StatusCode);
    }

    [Fact]
    public async Task A_page_read_that_is_rate_limited_or_never_answered_is_a_failure_and_not_an_empty_page()
    {
        using var limited = Client(new Replying(_ => RateLimited("30")));
        var limit = await Assert.ThrowsAsync<SubtitleProviderRateLimitedException>(() => SubtitleProviderHttp.GetTextAsync(limited, "https://x.example/", "subf2m", CancellationToken.None));
        Assert.Equal(TimeSpan.FromSeconds(30), limit.RetryAfter);

        using var silent = Client(new Silent());
        var quiet = await Assert.ThrowsAsync<HttpRequestException>(() => SubtitleProviderHttp.GetTextAsync(silent, "https://x.example/", "subf2m", CancellationToken.None));
        Assert.Equal("subf2m did not answer in time.", quiet.Message);
    }

    // ---- GetBytesAsync ----

    [Fact]
    public async Task A_download_gives_the_bytes_the_service_sent_exactly()
    {
        var recording = RecordedReplies.Gestdown("download");
        using var client = Client(new Replying(_ => Reply(recording, 1)));

        var bytes = await SubtitleProviderHttp.GetBytesAsync(client, "https://api.gestdown.info/subtitles/download/x", "gestdown", CancellationToken.None);

        Assert.Equal(recording.Exchanges[1].Reply.Body, Encoding.UTF8.GetString(bytes));
        Assert.True(bytes.Length > 0);
    }

    [Fact]
    public async Task A_download_that_is_refused_throws_and_never_returns_the_error_page_as_a_subtitle()
    {
        var recording = RecordedReplies.Gestdown("download");
        Assert.Equal(400, recording.Exchanges[2].Reply.Status);
        using var client = Client(new Replying(_ => Reply(recording, 2)));

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => SubtitleProviderHttp.GetBytesAsync(client, "https://api.gestdown.info/subtitles/download/0", "gestdown", CancellationToken.None));

        Assert.Equal(HttpStatusCode.BadRequest, failure.StatusCode);
    }

    [Fact]
    public async Task A_download_that_is_rate_limited_says_so_with_the_wait_asked_for()
    {
        using var client = Client(new Replying(_ => RateLimited("120")));

        var limit = await Assert.ThrowsAsync<SubtitleProviderRateLimitedException>(() => SubtitleProviderHttp.GetBytesAsync(client, "https://x.example/f", "gestdown", CancellationToken.None));

        Assert.Equal(TimeSpan.FromSeconds(120), limit.RetryAfter);
    }
}
