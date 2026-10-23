using System.Net;
using Deluno.Integrations.Metadata;
using Deluno.Integrations.Subtitles;
using Deluno.Integrations.Subtitles.Providers;
using Deluno.Integrations.Trakt;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.Subtitles;

/// <summary>
/// #1301: what Deluno does when a service that needs a key or an account refuses the one it was given, against that service's own recorded
/// refusal (tests/fixtures/recorded/opensubtitles, subdl, subsource, titlovi, ktuvit, trakt and tvdbapi, made by
/// scripts/recorded-replies/docker/subtitle-refusals.mjs with a made-up key, so each reply is the real service saying no). The replies to a
/// right key or account (a sign-in that works, a search that finds something) need an account and are listed in tests/fixtures/recorded/MISSING.md.
/// </summary>
public sealed class ARefusedKeyOrLoginOnEachServiceTests
{
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>Answers each call with the recorded reply to the same method and path (the query is not compared, the address is a made-up key's).</summary>
    private sealed class Recorded(params Recording[] recordings) : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            foreach (var recording in recordings)
            {
                foreach (var exchange in recording.Exchanges)
                {
                    if (exchange.Request.Method == request.Method.Method && exchange.Request.Path.Split('?')[0] == request.RequestUri.AbsolutePath)
                    {
                        // OpenSubtitles redirects a query not written in its canonical form; the redirect is followed by the client, so answer the second ask.
                        if (exchange.Reply.Status == 301 && recording.Exchanges.FirstOrDefault(other => other.Reply.Status != 301 && other.Request.Path.Split('?')[0] == request.RequestUri.AbsolutePath && other.Request.Path.Contains("languages=en&query", StringComparison.Ordinal)) is { } canonical)
                        {
                            return Task.FromResult(canonical.Reply.ToResponse());
                        }

                        return Task.FromResult(exchange.Reply.ToResponse());
                    }
                }
            }

            throw new InvalidOperationException($"No recording answers {request.Method} {request.RequestUri.AbsolutePath}.");
        }
    }

    private static readonly SubtitleSearchRequest Film = new("Sintel", 2010, null, null, null, null, null, ["en"], IsEpisode: false);

    // ---- OpenSubtitles ----

    [Fact]
    public async Task OpenSubtitles_refuses_an_unknown_key_on_the_search_and_the_login_and_both_are_a_403_not_an_empty_answer()
    {
        var recording = RecordedReplies.Opensubtitles("wrong-key");
        Assert.Equal([301, 403, 403, 503], recording.Exchanges.Select(e => e.Reply.Status));
        var handler = new Recorded(recording);
        var provider = new OpenSubtitlesSubtitleProvider(new Factory(handler));
        var credentials = new SubtitleProviderCredentials("nobody", "wrong", "not-a-key");

        var search = await Assert.ThrowsAsync<HttpRequestException>(() => provider.SearchAsync(Film, credentials, CancellationToken.None));
        var download = await Assert.ThrowsAsync<HttpRequestException>(() => provider.DownloadAsync(
            new SubtitleCandidate("opensubtitles", "1", "en", false, false), credentials, CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, search.StatusCode);
        // The download signs in first, and the sign-in is what is refused: nothing is asked for the file.
        Assert.Equal(HttpStatusCode.Forbidden, download.StatusCode);
        Assert.Contains("POST /api/v1/login", handler.Calls);
        Assert.DoesNotContain("POST /api/v1/download", handler.Calls);
    }

    [Fact]
    public async Task OpenSubtitles_asks_nothing_without_a_key_and_a_download_without_one_says_so()
    {
        var handler = new Recorded(RecordedReplies.Opensubtitles("wrong-key"));
        var provider = new OpenSubtitlesSubtitleProvider(new Factory(handler));

        var found = await provider.SearchAsync(Film, new SubtitleProviderCredentials("nobody", "wrong", null), CancellationToken.None);
        var download = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.DownloadAsync(
            new SubtitleCandidate("opensubtitles", "1", "en", false, false), SubtitleProviderCredentials.None, CancellationToken.None));

        Assert.Empty(found);
        Assert.Contains("needs an API key", download.Message);
        Assert.Empty(handler.Calls);
    }

    // ---- SubDL and SubSource ----

    [Fact]
    public async Task SubDL_and_SubSource_refuse_an_unknown_key_with_their_own_401_and_403_which_Deluno_does_not_read_as_nothing_found()
    {
        var subdl = RecordedReplies.Subdl("wrong-key");
        var subsource = RecordedReplies.Subsource("wrong-key");
        Assert.Equal(403, subdl.Single.Reply.Status);
        Assert.Equal(401, subsource.Single.Reply.Status);
        var credentials = new SubtitleProviderCredentials(ApiKey: "not-a-key");

        // SubDL answers 403 with a JSON body whose status is false: a refused key, thrown as the status.
        var refusedBySubDl = await Assert.ThrowsAsync<HttpRequestException>(() => new SubDlSubtitleProvider(new Factory(new Recorded(subdl))).SearchAsync(Film, credentials, CancellationToken.None));
        var refusedBySubSource = await Assert.ThrowsAsync<HttpRequestException>(() => new SubSourceSubtitleProvider(new Factory(new Recorded(subsource))).SearchAsync(Film, credentials, CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, refusedBySubDl.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, refusedBySubSource.StatusCode);
    }

    [Fact]
    public async Task SubDL_and_SubSource_ask_nothing_without_a_key()
    {
        var subdl = new Recorded(RecordedReplies.Subdl("wrong-key"));
        var subsource = new Recorded(RecordedReplies.Subsource("wrong-key"));

        var a = await new SubDlSubtitleProvider(new Factory(subdl)).SearchAsync(Film, SubtitleProviderCredentials.None, CancellationToken.None);
        var b = await new SubSourceSubtitleProvider(new Factory(subsource)).SearchAsync(Film, SubtitleProviderCredentials.None, CancellationToken.None);

        Assert.Empty(a);
        Assert.Empty(b);
        Assert.Empty(subdl.Calls);
        Assert.Empty(subsource.Calls);
    }

    // ---- Titlovi and Ktuvit ----

    [Fact]
    public async Task Titlovi_refusing_the_sign_in_with_a_401_is_a_search_that_asks_nothing_more_and_finds_nothing()
    {
        var recording = RecordedReplies.Titlovi("wrong-login");
        Assert.Equal(401, recording.Single.Reply.Status);
        var handler = new Recorded(recording);

        var found = await new TitloviSubtitleProvider(new Factory(handler)).SearchAsync(
            Film, new SubtitleProviderCredentials("nobody", "wrong", null), CancellationToken.None);

        Assert.Empty(found);
        Assert.Equal(["POST /api/subtitles/gettoken"], handler.Calls);
    }

    [Fact]
    public async Task Ktuvit_answering_a_wrong_password_with_200_and_IsSuccess_false_and_no_cookie_finds_nothing_and_a_download_says_the_sign_in_was_refused()
    {
        var recording = RecordedReplies.Ktuvit("wrong-login");
        Assert.Equal(200, recording.Single.Reply.Status);
        Assert.False(recording.Single.Reply.Json()["d"]!.GetValue<string>().Contains("\"IsSuccess\":true", StringComparison.Ordinal));
        var handler = new Recorded(recording);
        var provider = new KtuvitSubtitleProvider(new Factory(handler));
        var credentials = new SubtitleProviderCredentials("nobody@example.com", "wrong", null);

        var found = await provider.SearchAsync(Film, credentials, CancellationToken.None);
        var download = await Assert.ThrowsAsync<HttpRequestException>(() => provider.DownloadAsync(
            new SubtitleCandidate("ktuvit", "1:2", "he", false, false), credentials, CancellationToken.None));

        Assert.Empty(found);
        Assert.Contains("refused the sign-in", download.Message);
        Assert.All(handler.Calls, call => Assert.Equal("POST /Services/MembershipService.svc/Login", call));
    }

    // ---- Trakt ----

    private static TraktClient Trakt(Recorded handler) => new(new Factory(handler));

    [Fact]
    public async Task Trakt_refusing_an_unknown_client_id_is_a_401_that_starting_the_device_sign_in_throws_and_not_a_code()
    {
        var recording = RecordedReplies.Trakt("unknown-client-id");
        Assert.Equal(401, recording.Exchanges[0].Reply.Status);
        var handler = new Recorded(recording);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => Trakt(handler).StartDeviceAuthorizationAsync("not-a-client", CancellationToken.None));

        Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
        Assert.Equal(["POST /oauth/device/code"], handler.Calls);
    }

    [Fact]
    public async Task Trakt_answering_a_poll_for_a_device_code_it_does_not_know_with_404_is_a_refusal_not_a_wait()
    {
        var recording = RecordedReplies.Trakt("unknown-client-id");
        Assert.Equal(404, recording.Exchanges[1].Reply.Status);

        var result = await Trakt(new Recorded(recording)).PollDeviceTokenAsync("not-a-client", "not-a-secret", "not-a-device-code", CancellationToken.None);

        Assert.Equal(TraktDevicePollOutcome.Denied, result.Outcome);
        Assert.Null(result.Tokens);
    }

    [Fact]
    public async Task Trakt_answering_a_refresh_with_a_refresh_token_it_does_not_know_with_400_is_no_new_tokens_so_the_person_must_reconnect()
    {
        var recording = RecordedReplies.Trakt("unknown-client-id");
        Assert.Equal(400, recording.Exchanges[2].Reply.Status);
        Assert.Equal("invalid_grant", recording.Exchanges[2].Reply.Json()["error"]!.GetValue<string>());

        var tokens = await Trakt(new Recorded(recording)).RefreshAsync("not-a-client", "not-a-secret", "not-a-refresh-token", CancellationToken.None);

        Assert.Null(tokens);
    }

    [Fact]
    public async Task A_Trakt_read_sends_its_version_key_and_bearer_headers_and_a_key_Trakt_does_not_know_is_its_403_thrown_after_one_attempt()
    {
        var recording = RecordedReplies.Trakt("unknown-client-id");
        Assert.Equal(403, recording.Exchanges[3].Reply.Status);
        var sent = new List<HttpRequestMessage>();
        var handler = new Recorded(recording);
        var client = new TraktClient(new Factory(new Inspecting(handler, sent)));

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("not-a-client", "not-a-token", "/movies/trending?limit=1", CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, failure.StatusCode);
        var request = Assert.Single(sent);
        Assert.Equal("2", Assert.Single(request.Headers.GetValues("trakt-api-version")));
        Assert.Equal("not-a-client", Assert.Single(request.Headers.GetValues("trakt-api-key")));
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("not-a-token", request.Headers.Authorization.Parameter);
        Assert.Equal(["GET /movies/trending"], handler.Calls);
    }

    private sealed class Inspecting(HttpMessageHandler inner, List<HttpRequestMessage> sent) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            sent.Add(request);
            return new HttpMessageInvoker(inner, disposeHandler: false).SendAsync(request, cancellationToken);
        }
    }

    // ---- TheTVDB ----

    [Fact]
    public async Task TheTVDB_refusing_the_key_at_login_with_401_gives_no_series_and_the_login_is_not_repeated_for_the_one_search()
    {
        var recording = RecordedReplies.Tvdbapi("wrong-key");
        Assert.Equal(401, recording.Single.Reply.Status);
        var handler = new Recorded(recording);
        var client = new TvdbDirectClient(new HttpClient(handler), "not-a-key", null);

        var found = await client.SearchAsync("Breaking Bad", null, CancellationToken.None);

        Assert.Empty(found);
        Assert.Equal(["POST /v4/login"], handler.Calls);
    }

    private sealed class Answering(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(answer(request));
    }

    private sealed class NotAnswering : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("No connection could be made because the target machine actively refused it.");
    }

    [Fact]
    public async Task Starting_the_device_sign_in_with_Trakt_down_or_failing_throws_whatever_the_failure_is_and_never_hands_out_a_code()
    {
        // The wrong client id is Trakt's recorded 401 (above); a server error and a service that does not answer are failures with no reply to record.
        var serverError = new TraktClient(new Factory(new Answering(_ => new HttpResponseMessage(HttpStatusCode.BadGateway))));

        var failed = await Assert.ThrowsAsync<HttpRequestException>(() => serverError.StartDeviceAuthorizationAsync("not-a-client", CancellationToken.None));
        var down = await Assert.ThrowsAsync<HttpRequestException>(() => new TraktClient(new Factory(new NotAnswering())).StartDeviceAuthorizationAsync("not-a-client", CancellationToken.None));

        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        Assert.Contains("refused", down.Message);
    }

    [Fact]
    public async Task A_refresh_that_cannot_reach_Trakt_throws_and_is_never_taken_for_a_dead_refresh_token()
    {
        var down = await Assert.ThrowsAsync<HttpRequestException>(() => new TraktClient(new Factory(new NotAnswering())).RefreshAsync("not-a-client", "not-a-secret", "not-a-refresh-token", CancellationToken.None));

        Assert.Contains("refused", down.Message);
        Assert.Null(await Trakt(new Recorded(RecordedReplies.Trakt("unknown-client-id"))).RefreshAsync("not-a-client", "not-a-secret", "not-a-refresh-token", CancellationToken.None));
    }
}
