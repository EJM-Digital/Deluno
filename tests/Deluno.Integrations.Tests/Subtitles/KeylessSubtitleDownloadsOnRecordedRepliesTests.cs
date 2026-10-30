using System.Net;
using System.Text;
using Deluno.Integrations.Subtitles;
using Deluno.Integrations.Subtitles.Providers;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.Subtitles;

/// <summary>
/// #1301: the downloads of the subtitle providers that ask for no account, against what the services really answered
/// (tests/fixtures/recorded/gestdown, subf2m and supersubtitles, made by scripts/recorded-replies/docker/keyless-subtitles.mjs):
/// the file itself, a number the service does not hold, and the redirect Subf2m answers with before its archive.
/// </summary>
public sealed class KeylessSubtitleDownloadsOnRecordedRepliesTests
{
    internal sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>
    /// Answers each call with the recorded reply to the same host (when the recording names one) and path, and follows a recorded redirect the way
    /// the shipped HTTP client does: the redirect is the service's own recorded reply, the second ask is answered by its own recording.
    /// </summary>
    internal sealed class Recorded(params Recording[] recordings) : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            for (var hops = 0; hops < 5; hops++)
            {
                var uri = request.RequestUri!;
                Calls.Add($"{request.Method} {uri.Host}{uri.AbsolutePath}");
                var reply = Find(uri) ?? throw new InvalidOperationException($"No recording answers {request.Method} {uri.Host}{uri.AbsolutePath}.");
                var response = reply.ToResponse();
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    request = new HttpRequestMessage(HttpMethod.Get, location);
                    continue;
                }

                return await Task.FromResult(response);
            }

            throw new InvalidOperationException("Too many redirects.");
        }

        private RecordedReply? Find(Uri uri)
        {
            foreach (var recording in recordings)
            {
                foreach (var exchange in recording.Exchanges)
                {
                    var path = exchange.Request.Path.Split('?')[0];
                    var named = path.StartsWith('/') ? uri.AbsolutePath : uri.Host + uri.AbsolutePath;
                    if (path == named) return exchange.Reply;
                }
            }

            return null;
        }
    }

    private static SubtitleCandidate Candidate(string provider, string token) => new(provider, token, "en", false, false);

    [Fact]
    public async Task Gestdown_downloads_the_subtitle_its_listing_named_as_the_text_file_it_really_sent()
    {
        var recording = RecordedReplies.Gestdown("download");
        var listed = recording.Exchanges[0].Reply.Json()["matchingSubtitles"]![0]!["downloadUri"]!.GetValue<string>();
        var handler = new Recorded(recording);

        var bytes = await new GestdownSubtitleProvider(new Factory(handler)).DownloadAsync(Candidate("gestdown", listed), SubtitleProviderCredentials.None, CancellationToken.None);

        Assert.Equal(["GET /subtitles/download/<scrubbed>".Replace("<scrubbed>", listed.Split('/')[^1])], handler.Calls.Select(call => call.Replace("api.gestdown.info", string.Empty)));
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Equal(recording.Exchanges[1].Reply.Body, text);
        Assert.Contains("-->", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gestdown_answering_an_unknown_subtitle_with_400_is_a_download_that_fails_not_an_empty_file()
    {
        var recording = RecordedReplies.Gestdown("download");
        Assert.Equal(400, recording.Exchanges[2].Reply.Status);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => new GestdownSubtitleProvider(new Factory(new Recorded(recording))).DownloadAsync(
            Candidate("gestdown", "/subtitles/download/00000000000000000000"), SubtitleProviderCredentials.None, CancellationToken.None));

        Assert.Contains("400", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Subf2m_downloads_through_the_redirect_it_answers_with_and_gives_the_archive_it_really_sent()
    {
        var recording = RecordedReplies.Subf2m("sintel-english");
        Assert.Equal(302, recording.Exchanges[4].Reply.Status);
        var handler = new Recorded(recording);

        var bytes = await new Subf2mSubtitleProvider(new Factory(handler)).DownloadAsync(
            Candidate("subf2m", "https://subf2m.co/subtitles/sintel/english/1501625/download"), SubtitleProviderCredentials.None, CancellationToken.None);

        Assert.Equal(["GET subf2m.co/subtitles/sintel/english/1501625/download", "GET isubcdn.com/subtitles/english/2024/159/20/sintel_english-1501625.zip"], handler.Calls);
        Assert.Equal(recording.Exchanges[5].Reply.Bytes, bytes);
        Assert.Equal((byte)'P', bytes[0]);
        Assert.Equal((byte)'K', bytes[1]);
    }

    [Fact]
    public async Task Subf2m_answering_a_subtitle_number_it_does_not_hold_with_404_is_a_download_that_fails()
    {
        var recording = RecordedReplies.Subf2m("download-not-found");
        Assert.Equal(404, recording.Single.Reply.Status);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => new Subf2mSubtitleProvider(new Factory(new Recorded(recording))).DownloadAsync(
            Candidate("subf2m", "https://subf2m.co/subtitles/sintel/english/99999999999/download"), SubtitleProviderCredentials.None, CancellationToken.None));

        Assert.Contains("404", failure.Message, StringComparison.Ordinal);
    }

    // ---- Subf2m's search (#1389): the pages it really answered for Sintel ----

    private static SubtitleSearchRequest Film(params string[] languages)
        => new("Sintel", 2010, null, null, null, null, null, languages.Length == 0 ? ["en"] : languages, IsEpisode: false);

    [Fact]
    public async Task Subf2m_finds_the_subtitles_of_a_film_in_the_asked_language_from_the_links_its_pages_really_carry()
    {
        var recording = RecordedReplies.Subf2m("sintel-english");
        var listed = System.Text.RegularExpressions.Regex.Matches(recording.Exchanges[2].Reply.Body!, """href=['"](/subtitles/sintel/english/(\d+))['"]""")
            .Select(match => "https://subf2m.co" + match.Groups[1].Value + "/download").Distinct().ToArray();
        var handler = new Recorded(recording);

        var found = await new Subf2mSubtitleProvider(new Factory(handler)).SearchAsync(Film(), SubtitleProviderCredentials.None, CancellationToken.None);

        Assert.Equal(5, listed.Length);
        Assert.Equal(listed.Take(3), found.Select(candidate => candidate.DownloadToken));
        Assert.All(found, candidate =>
        {
            Assert.Equal("subf2m", candidate.ProviderKey);
            Assert.Equal("en", candidate.Language);
        });
        Assert.Equal(["GET subf2m.co/subtitles/searchbytitle", "GET subf2m.co/subtitles/sintel/english"], handler.Calls);
    }

    [Fact]
    public async Task A_subtitle_Subf2m_listed_is_downloaded_through_its_redirect_to_the_archive_it_really_sent()
    {
        var recording = RecordedReplies.Subf2m("sintel-english");
        var provider = new Subf2mSubtitleProvider(new Factory(new Recorded(recording)));

        var found = await provider.SearchAsync(Film(), SubtitleProviderCredentials.None, CancellationToken.None);
        var bytes = await provider.DownloadAsync(found.First(candidate => candidate.DownloadToken.Contains("/1501625/", StringComparison.Ordinal)), SubtitleProviderCredentials.None, CancellationToken.None);

        Assert.Equal(recording.Exchanges[5].Reply.Bytes, bytes);
    }

    [Fact]
    public async Task A_title_Subf2m_does_not_have_finds_nothing_and_does_not_ask_its_own_search_page_again()
    {
        var handler = new Recorded(RecordedReplies.Subf2m("search-no-title"));

        var found = await new Subf2mSubtitleProvider(new Factory(handler)).SearchAsync(
            new SubtitleSearchRequest("Zzzz Not A Title Zzzz", null, null, null, null, null, null, ["en"], IsEpisode: false), SubtitleProviderCredentials.None, CancellationToken.None);

        Assert.Empty(found);
        Assert.Equal(["GET subf2m.co/subtitles/searchbytitle"], handler.Calls);
    }

    /// <summary>Answers SuperSubtitles' one address by its action and term, as the site itself tells its calls apart.</summary>
    private sealed class SupersubtitlesSite(Recording recording) : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = request.RequestUri!.Query;
            var action = System.Web.HttpUtility.ParseQueryString(query)["action"];
            var term = System.Web.HttpUtility.ParseQueryString(query)["term"];
            Calls.Add(action ?? "");
            var exchange = recording.Exchanges.First(e =>
            {
                var recorded = System.Web.HttpUtility.ParseQueryString(e.Request.Path.Split('?', 2)[1]);
                return recorded["action"] == action && (term is null || recorded["term"] == term);
            });
            return Task.FromResult(exchange.Reply.ToResponse());
        }
    }

    private static SubtitleSearchRequest Episode(string show)
        => new(show, null, 1, 1, null, null, null, ["en"], IsEpisode: true);

    [Fact]
    public async Task SuperSubtitles_finds_the_show_and_then_reports_the_season_listing_its_site_refused_with_a_Cloudflare_403()
    {
        var site = new SupersubtitlesSite(RecordedReplies.Supersubtitles("show-search"));

        var refused = await Assert.ThrowsAsync<HttpRequestException>(() =>
            new SupersubtitlesSubtitleProvider(new Factory(site)).SearchAsync(Episode("Breaking Bad"), SubtitleProviderCredentials.None, CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("supersubtitles returned HTTP 403 (Forbidden).", refused.Message);
        Assert.Equal(["autoname", "xbmc"], site.Calls);
    }

    [Fact]
    public async Task A_show_SuperSubtitles_does_not_hold_finds_nothing_and_asks_for_no_season_listing()
    {
        var site = new SupersubtitlesSite(RecordedReplies.Supersubtitles("show-search"));

        var found = await new SupersubtitlesSubtitleProvider(new Factory(site)).SearchAsync(Episode("Zzzz Not A Show Zzzz"), SubtitleProviderCredentials.None, CancellationToken.None);

        Assert.Empty(found);
        Assert.Equal(["autoname"], site.Calls);
    }
}
