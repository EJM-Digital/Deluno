using System.Net;
using System.Net.Http.Json;
using System.Text;
using Deluno.Integrations.Subtitles;
using Deluno.Integrations.Subtitles.Providers;

namespace Deluno.Integrations.Tests.Subtitles;

/// <summary>
/// The rest of #837's own named list (Addic7ed, Ktuvit), each against a
/// recorded shape of its real API/HTML response — not a live call, the same
/// discipline <see cref="SubtitleProviderFixtureTests"/> already applies to
/// Titlovi, Assrt and Supersubtitles.
/// </summary>
public sealed class Addic7edAndKtuvitSubtitleProviderTests
{
    // ── Addic7ed ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Addic7ed_resolves_the_show_id_then_reads_the_season_table()
    {
        var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/shows.php")
            {
                return Html("""
                    <html><body><table><tr>
                    <td><h3><a href="/show/1234">Example Show</a></h3></td>
                    <td><h3><a href="/show/5678">Another Show</a></h3></td>
                    </tr></table></body></html>
                    """);
            }

            if (path == "/ajax_loadShow.php")
            {
                Assert.Contains("show=1234", request.RequestUri!.Query);
                Assert.Contains("season=1", request.RequestUri!.Query);
                return Html("""
                    <table>
                    <tr class="epeven completed">
                        <td>1</td><td>2</td><td></td><td>English</td><td>WEB-DL</td>
                        <td></td><td></td><td></td><td></td><td><a href="/original/1234/0">Download</a></td>
                    </tr>
                    <tr class="epeven completed">
                        <td>1</td><td>3</td><td></td><td>English</td><td>WEB-DL</td>
                        <td></td><td></td><td></td><td></td><td><a href="/original/1234/1">Download</a></td>
                    </tr>
                    </table>
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var provider = new Addic7edSubtitleProvider(new StubFactory(handler));
        var request = new SubtitleSearchRequest("Example Show", null, 1, 2, null, null, null, ["en"], IsEpisode: true);

        var results = await provider.SearchAsync(request, SubtitleProviderCredentials.None, CancellationToken.None);

        // Episode 3's row is a different episode and must not appear for a
        // search asking about episode 2.
        var found = Assert.Single(results);
        Assert.Equal("en", found.Language);
        Assert.Equal("https://www.addic7ed.com/original/1234/0", found.DownloadToken);
        Assert.Equal("WEB-DL", found.ReleaseName);
    }

    [Fact]
    public async Task Addic7ed_answers_nothing_for_a_movie_request()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("Movies are out of scope; must not call out."));
        var provider = new Addic7edSubtitleProvider(new StubFactory(handler));

        var results = await provider.SearchAsync(
            new SubtitleSearchRequest("A Film", 2020, null, null, null, null, null, ["en"], IsEpisode: false),
            SubtitleProviderCredentials.None,
            CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task Addic7ed_with_no_matching_show_finds_nothing()
    {
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/shows.php")
            {
                return Html("""<html><body><table><tr><td><h3><a href="/show/1">Unrelated Show</a></h3></td></tr></table></body></html>""");
            }

            throw new InvalidOperationException("No matching show; must not go further.");
        });

        var provider = new Addic7edSubtitleProvider(new StubFactory(handler));
        var results = await provider.SearchAsync(
            new SubtitleSearchRequest("Nothing Like It", null, 1, 1, null, null, null, ["en"], IsEpisode: true),
            SubtitleProviderCredentials.None,
            CancellationToken.None);

        Assert.Empty(results);
    }

    // ── Ktuvit ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Ktuvit_signs_in_searches_and_reads_the_movie_subtitle_table()
    {
        var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/Login", StringComparison.Ordinal))
            {
                var response = Json("""{"d":{"IsSuccess":true}}""");
                response.Headers.Add("Set-Cookie", "Login=session-token-1; path=/; HttpOnly");
                return response;
            }

            if (path.EndsWith("/SearchPage_search", StringComparison.Ordinal))
            {
                return Json("""{"d":{"Films":[{"ID":"55","ReleaseYear":2021}]}}""");
            }

            if (path.EndsWith("/MovieInfo.aspx", StringComparison.Ordinal))
            {
                Assert.Contains("ID=55", request.RequestUri!.Query);
                return Html("""
                    <table id="subtitlesList"><tbody>
                    <tr><td>Dune.2021.1080p.WEB-DL</td><td></td><td></td><td></td><td></td>
                        <td><a data-subtitle-id="9001">Download</a></td></tr>
                    </tbody></table>
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var provider = new KtuvitSubtitleProvider(new StubFactory(handler));
        var request = new SubtitleSearchRequest("Dune", 2021, null, null, null, null, null, ["he"], IsEpisode: false);
        var credentials = new SubtitleProviderCredentials(Username: "person@example.com", Password: "secret");

        var results = await provider.SearchAsync(request, credentials, CancellationToken.None);

        var found = Assert.Single(results);
        Assert.Equal("he", found.Language);
        Assert.Equal("55:9001", found.DownloadToken);
        Assert.Equal("Dune.2021.1080p.WEB-DL", found.ReleaseName);
    }

    [Fact]
    public async Task Ktuvit_search_asks_nothing_without_credentials()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("Should never call out with no credentials."));
        var provider = new KtuvitSubtitleProvider(new StubFactory(handler));

        var results = await provider.SearchAsync(
            new SubtitleSearchRequest("Dune", 2021, null, null, null, null, null, ["he"], IsEpisode: false),
            SubtitleProviderCredentials.None,
            CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task Ktuvit_reads_the_episode_listing_by_series_id_season_and_episode()
    {
        var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/Login", StringComparison.Ordinal))
            {
                var response = Json("""{"d":{"IsSuccess":true}}""");
                response.Headers.Add("Set-Cookie", "Login=session-token-2; path=/");
                return response;
            }

            if (path.EndsWith("/SearchPage_search", StringComparison.Ordinal))
            {
                return Json("""{"d":{"Films":[{"ID":"77","ReleaseYear":null}]}}""");
            }

            if (path.EndsWith("/GetModuleAjax.ashx", StringComparison.Ordinal))
            {
                Assert.Contains("SeriesID=77", request.RequestUri!.Query);
                Assert.Contains("Season=1", request.RequestUri!.Query);
                Assert.Contains("Episode=2", request.RequestUri!.Query);
                return Html("""
                    <table><tr>
                        <td>Show.S01E02.WEB</td><td></td><td></td><td></td><td></td>
                        <td><input type="hidden" data-sub-id="4242" /></td>
                    </tr></table>
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var provider = new KtuvitSubtitleProvider(new StubFactory(handler));
        var request = new SubtitleSearchRequest("Show", null, 1, 2, null, null, null, ["he"], IsEpisode: true);
        var credentials = new SubtitleProviderCredentials(Username: "person@example.com", Password: "secret");

        var results = await provider.SearchAsync(request, credentials, CancellationToken.None);

        var found = Assert.Single(results);
        Assert.Equal("77:4242", found.DownloadToken);
    }

    [Fact]
    public async Task Ktuvit_download_signs_in_again_requests_an_identifier_and_fetches_the_file()
    {
        var downloadBytes = "1\n00:00:00,000 --> 00:00:01,000\nHello\n"u8.ToArray();
        var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/Login", StringComparison.Ordinal))
            {
                var response = Json("""{"d":{"IsSuccess":true}}""");
                response.Headers.Add("Set-Cookie", "Login=session-token-3; path=/");
                return response;
            }

            if (path.EndsWith("/RequestSubtitleDownload", StringComparison.Ordinal))
            {
                return Json("""{"d":{"DownloadIdentifier":"ident-1"}}""");
            }

            if (path.EndsWith("/DownloadFile.ashx", StringComparison.Ordinal))
            {
                Assert.Contains("DownloadIdentifier=ident-1", request.RequestUri!.Query);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(downloadBytes) };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var provider = new KtuvitSubtitleProvider(new StubFactory(handler));
        var candidate = new SubtitleCandidate("ktuvit", "55:9001", "he", false, false);
        var credentials = new SubtitleProviderCredentials(Username: "person@example.com", Password: "secret");

        var bytes = await provider.DownloadAsync(candidate, credentials, CancellationToken.None);

        Assert.Equal(downloadBytes, bytes);
    }

    // ── registry ordering ────────────────────────────────────────────────────

    [Fact]
    public void Registry_places_the_no_account_addic7ed_before_the_account_needing_ktuvit()
    {
        var registry = new SubtitleProviderRegistry([
            new KtuvitSubtitleProvider(new StubFactory(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)))),
            new Addic7edSubtitleProvider(new StubFactory(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))))
        ]);

        var keys = registry.All.Select(provider => provider.Key).ToArray();

        Assert.Equal(["addic7ed", "ktuvit"], keys);
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Html(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

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
