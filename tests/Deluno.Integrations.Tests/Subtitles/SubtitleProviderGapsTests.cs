using System.Net;
using Deluno.Integrations.Subtitles;
using Deluno.Integrations.Subtitles.Providers;
using static Deluno.Integrations.Tests.Subtitles.ProviderStubs;

namespace Deluno.Integrations.Tests.Subtitles;

/// <summary>
/// The parts of the free-text and login subtitle providers the other provider
/// tests do not reach: what each one says about itself, its language-name
/// tables, the odd shapes of reply it has to read, and its download call. #1268.
/// </summary>
public sealed class SubtitleProviderGapsTests
{
    private static readonly IHttpClientFactory Nothing = Factory(_ => throw new InvalidOperationException("Must not call out."));

    private static SubtitleSearchRequest Episode(string title = "Show", int season = 1, int episode = 2, params string[] languages)
        => new(title, null, season, episode, null, null, null, languages, IsEpisode: true);

    private static SubtitleSearchRequest Film(string title = "Dune", int? year = 2021, params string[] languages)
        => new(title, year, null, null, null, null, null, languages, IsEpisode: false);

    private static readonly SubtitleProviderCredentials KeyOnly = new(ApiKey: "key");
    private static readonly SubtitleProviderCredentials Login = new(Username: "person@example.com", Password: "secret");

    [Fact]
    public void Every_provider_says_who_it_is_and_what_it_needs()
    {
        ISubtitleProvider[] providers =
        [
            new Addic7edSubtitleProvider(Nothing), new AssrtSubtitleProvider(Nothing), new GestdownSubtitleProvider(Nothing),
            new KtuvitSubtitleProvider(Nothing), new OpenSubtitlesSubtitleProvider(Nothing), new PodnapisiSubtitleProvider(Nothing),
            new SubDlSubtitleProvider(Nothing), new SubSourceSubtitleProvider(Nothing), new Subf2mSubtitleProvider(Nothing),
            new SupersubtitlesSubtitleProvider(Nothing), new TitloviSubtitleProvider(Nothing)
        ];

        Assert.Equal(
            ["addic7ed", "assrt", "gestdown", "ktuvit", "opensubtitles", "podnapisi", "subdl", "subsource", "subf2m", "supersubtitles", "titlovi"],
            providers.Select(p => p.Key));
        Assert.Equal(
            ["Addic7ed", "Assrt", "Gestdown", "Ktuvit", "OpenSubtitles.com", "Podnapisi", "SubDL", "SubSource", "Subf2m", "Supersubtitles", "Titlovi"],
            providers.Select(p => p.DisplayName),
            StringComparer.OrdinalIgnoreCase);
        Assert.All(providers, p => Assert.False(string.IsNullOrWhiteSpace(p.Description)));
        const SubtitleCredentialFields Account = SubtitleCredentialFields.Username | SubtitleCredentialFields.Password;
        Assert.Equal(
            [
                (SubtitleProviderScope.TvOnly, SubtitleCredentialFields.None, true),
                (SubtitleProviderScope.Both, SubtitleCredentialFields.ApiKey, false),
                (SubtitleProviderScope.TvOnly, SubtitleCredentialFields.None, false),
                (SubtitleProviderScope.Both, Account, false),
                (SubtitleProviderScope.Both, Account | SubtitleCredentialFields.ApiKey, false),
                (SubtitleProviderScope.Both, Account, true),
                (SubtitleProviderScope.Both, SubtitleCredentialFields.ApiKey, false),
                (SubtitleProviderScope.Both, SubtitleCredentialFields.ApiKey, false),
                (SubtitleProviderScope.Both, SubtitleCredentialFields.None, false),
                (SubtitleProviderScope.TvOnly, SubtitleCredentialFields.None, true),
                (SubtitleProviderScope.Both, Account, false)
            ],
            providers.Select(p => (p.Scope, p.RequiredCredentials, p.CredentialsOptional)));
    }

    // ── Addic7ed ────────────────────────────────────────────────────────────

    private static string Row(string episode, string language, string release, string? link)
        => $"<tr class=\"epeven\"><td>1</td><td>{episode}</td><td></td><td>{language}</td><td>{release}</td><td></td><td></td><td></td><td></td><td>{(link is null ? "" : $"<a href=\"{link}\">x</a>")}</td></tr>";

    [Fact]
    public async Task Addic7ed_names_every_language_by_its_code_and_skips_rows_it_cannot_use()
    {
        var names = new[]
        {
            "English", "Spanish (Spain)", "Spanish", "French", "Portuguese", "Portuguese (Brazilian)", "Italian",
            "German", "Dutch", "Polish", "Greek", "Klingon"
        };
        var rows = string.Concat(names.Select((name, i) => Row("2", name, "rel" + i, i % 2 == 0 ? $"/original/1/{i}" : $"original/1/{i}")))
            + "<tr class=\"epeven\"><td>short row</td></tr>"
            + Row("x", "English", "not-a-number", "/a")
            + Row("2", "English", "no-link", null);
        var provider = new Addic7edSubtitleProvider(Factory(request => request.RequestUri!.AbsolutePath == "/shows.php"
            ? Html("""<table><tr><td><h3><a href="/show/9">Show Extra</a></h3></td></tr></table>""")
            : Html($"<table>{rows}</table>")));

        var found = await provider.SearchAsync(Episode("Show"), SubtitleProviderCredentials.None, CancellationToken.None);

        Assert.Equal(
            ["en", "es", "es", "fr", "pt", "pt-BR", "it", "de", "nl", "pl", "el", "klingon"],
            found.Select(c => c.Language));
        Assert.Equal("https://www.addic7ed.com/original/1/1", found[1].DownloadToken);
        Assert.Equal("https://www.addic7ed.com/original/1/0", found[0].DownloadToken);
    }

    [Fact]
    public async Task Addic7ed_finds_nothing_when_the_pages_are_empty_or_the_episode_is_missing()
    {
        var showsOnly = new Addic7edSubtitleProvider(Factory(_ => Html("")));
        Assert.Empty(await showsOnly.SearchAsync(Episode(), SubtitleProviderCredentials.None, CancellationToken.None));

        var noSeason = new Addic7edSubtitleProvider(Factory(request => request.RequestUri!.AbsolutePath == "/shows.php"
            ? Html("""<table><tr><td><h3><a href="/show/9">Show</a></h3></td></tr></table>""")
            : Html("")));
        Assert.Empty(await noSeason.SearchAsync(Episode(), SubtitleProviderCredentials.None, CancellationToken.None));

        var noEpisodeNumber = new SubtitleSearchRequest("Show", null, 1, null, null, null, null, ["en"], IsEpisode: true);
        Assert.Empty(await new Addic7edSubtitleProvider(Nothing).SearchAsync(noEpisodeNumber, SubtitleProviderCredentials.None, CancellationToken.None));
    }

    [Fact]
    public async Task Addic7ed_downloads_the_file_at_the_candidate_address()
    {
        var provider = new Addic7edSubtitleProvider(Factory(request =>
        {
            Assert.Equal("https://www.addic7ed.com/original/1/0", request.RequestUri!.ToString());
            return Bytes([1, 2, 3]);
        }));

        var bytes = await provider.DownloadAsync(
            new SubtitleCandidate("addic7ed", "https://www.addic7ed.com/original/1/0", "en", false, false),
            SubtitleProviderCredentials.None, CancellationToken.None);

        Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
    }

    // ── Assrt ───────────────────────────────────────────────────────────────

    private static HttpResponseMessage AssrtReply(HttpRequestMessage request, string detail)
        => request.RequestUri!.AbsolutePath.EndsWith("/sub/search", StringComparison.Ordinal)
            ? Json("""{"sub":{"subs":[{"id":null,"videoname":"skipped"},{"id":7,"videoname":"From.Search"},{"id":8,"videoname":"Empty.Detail"}]}}""")
            : request.RequestUri.Query.Contains("id=8", StringComparison.Ordinal)
                ? Json("""{"sub":{"subs":[]}}""")
                : Json(detail);

    [Fact]
    public async Task Assrt_reads_a_single_url_and_each_language_the_subtitle_carries()
    {
        var provider = new AssrtSubtitleProvider(Factory(request => AssrtReply(request,
            """{"sub":{"subs":[{"videoname":null,"url":"https://assrt.example/one","lang":{"langlist":{"lang_chs":"x","lang_cht":" ","lang_eng":"y"}}}]}}""")));

        var found = await provider.SearchAsync(Film(), KeyOnly, CancellationToken.None);

        Assert.Equal(["zh-Hans", "en"], found.Select(c => c.Language));
        Assert.All(found, c => Assert.Equal("https://assrt.example/one", c.DownloadToken));
        Assert.All(found, c => Assert.Equal("From.Search", c.ReleaseName));
    }

    [Fact]
    public async Task Assrt_picks_the_season_pack_file_for_the_episode_asked_for()
    {
        const string pack = """{"sub":{"subs":[{"videoname":"Pack","filelist":[{"f":"Show.S01E01.srt","url":"u1"},{"f":null,"url":"u0"},{"f":"Show.S01E02.srt","url":"u2"}],"lang":{"langlist":{"lang_cht":"z"}}}]}}""";
        var provider = new AssrtSubtitleProvider(Factory(request => AssrtReply(request, pack)));

        var found = await provider.SearchAsync(Episode(), KeyOnly, CancellationToken.None);
        var single = Assert.Single(found);
        Assert.Equal("u2", single.DownloadToken);
        Assert.Equal("zh-Hant", single.Language);
        Assert.Equal("Pack", single.ReleaseName);

        // No file carries the episode: nothing is offered rather than the wrong one.
        Assert.Empty(await provider.SearchAsync(Episode(episode: 9), KeyOnly, CancellationToken.None));
    }

    [Fact]
    public async Task Assrt_takes_a_lone_file_only_when_the_request_is_not_for_one_episode()
    {
        var lone = new AssrtSubtitleProvider(Factory(request => AssrtReply(request,
            """{"sub":{"subs":[{"filelist":[{"f":"a.srt","url":"only"}],"lang":{"langlist":{"lang_eng":"y"}}}]}}""")));
        Assert.Equal("only", Assert.Single(await lone.SearchAsync(Film(), KeyOnly, CancellationToken.None)).DownloadToken);

        var several = new AssrtSubtitleProvider(Factory(request => AssrtReply(request,
            """{"sub":{"subs":[{"filelist":[{"f":"a.srt","url":"a"},{"f":"b.srt","url":"b"}],"lang":{"langlist":{"lang_eng":"y"}}}]}}""")));
        Assert.Empty(await several.SearchAsync(Film(), KeyOnly, CancellationToken.None));

        var noSeasonNumber = new SubtitleSearchRequest("Show", null, null, 2, null, null, null, [], IsEpisode: true);
        Assert.Equal("only", Assert.Single(await lone.SearchAsync(noSeasonNumber, KeyOnly, CancellationToken.None)).DownloadToken);
    }

    [Fact]
    public async Task Assrt_offers_nothing_for_a_subtitle_with_no_language_or_no_link_or_no_key()
    {
        var noLang = new AssrtSubtitleProvider(Factory(request => AssrtReply(request, """{"sub":{"subs":[{"url":"u"}]}}""")));
        Assert.Empty(await noLang.SearchAsync(Film(), KeyOnly, CancellationToken.None));

        var noLink = new AssrtSubtitleProvider(Factory(request => AssrtReply(request,
            """{"sub":{"subs":[{"lang":{"langlist":{"lang_eng":"y"}}}]}}""")));
        Assert.Empty(await noLink.SearchAsync(Film(), KeyOnly, CancellationToken.None));

        var noMatches = new AssrtSubtitleProvider(Factory(_ => Json("""{"sub":{"subs":[]}}""")));
        Assert.Empty(await noMatches.SearchAsync(Film(), KeyOnly, CancellationToken.None));

        Assert.Empty(await new AssrtSubtitleProvider(Nothing).SearchAsync(Film(), SubtitleProviderCredentials.None, CancellationToken.None));
    }

    [Fact]
    public async Task Assrt_downloads_the_file_at_the_candidate_address()
    {
        var provider = new AssrtSubtitleProvider(Factory(_ => Bytes([9])));
        var bytes = await provider.DownloadAsync(new SubtitleCandidate("assrt", "https://assrt.example/f", "en", false, false), KeyOnly, CancellationToken.None);
        Assert.Equal(new byte[] { 9 }, bytes);
    }

    // ── Gestdown ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Gestdown_shortens_each_language_and_skips_one_it_cannot_shorten()
    {
        var asked = new List<string>();
        var provider = new GestdownSubtitleProvider(Factory(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/shows/search/", StringComparison.Ordinal))
            {
                return Json("""{"shows":[{"id":" ","name":"blank"},{"id":"g1","name":"Show"}]}""");
            }

            asked.Add(path);
            return Json("""{"subtitles":[{"url":"/dl/1","version":"WEB","hearingImpaired":true,"downloadCount":4},{"download":"/dl/2"},{"downloadUri":"/dl/3"},{"version":"nolink"}]}""");
        }));

        var found = await provider.SearchAsync(Episode("Show", 1, 2, "ENG", "e", "fr"), SubtitleProviderCredentials.None, CancellationToken.None);

        Assert.Equal(["/subtitles/get/g1/1/2/en", "/subtitles/get/g1/1/2/fr"], asked);
        Assert.Equal(6, found.Count);
        Assert.Equal("/dl/1", found[0].DownloadToken);
        Assert.True(found[0].HearingImpaired);
        Assert.Equal(4, found[0].DownloadCount);
        Assert.False(found[1].HearingImpaired);
    }

    [Fact]
    public async Task Gestdown_finds_nothing_for_a_film_or_an_unknown_show_and_downloads_relative_links()
    {
        Assert.Empty(await new GestdownSubtitleProvider(Nothing).SearchAsync(Film(), SubtitleProviderCredentials.None, CancellationToken.None));

        var unknown = new GestdownSubtitleProvider(Factory(_ => Json("""{"shows":[]}""")));
        Assert.Empty(await unknown.SearchAsync(Episode(), SubtitleProviderCredentials.None, CancellationToken.None));

        var seen = new List<string>();
        var provider = new GestdownSubtitleProvider(Factory(request => { seen.Add(request.RequestUri!.ToString()); return Bytes([5]); }));
        await provider.DownloadAsync(new SubtitleCandidate("gestdown", "/subtitles/download/x", "en", false, false), SubtitleProviderCredentials.None, CancellationToken.None);
        await provider.DownloadAsync(new SubtitleCandidate("gestdown", "https://other.example/y", "en", false, false), SubtitleProviderCredentials.None, CancellationToken.None);
        Assert.Equal(["https://api.gestdown.info/subtitles/download/x", "https://other.example/y"], seen);
    }

    // ── Ktuvit ──────────────────────────────────────────────────────────────

    private static HttpResponseMessage KtuvitReply(HttpRequestMessage request, string searchReply, string? cookie = "Login=abc; path=/")
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/Login", StringComparison.Ordinal))
        {
            var response = Json("""{"d":{"IsSuccess":true}}""");
            if (cookie is not null)
            {
                response.Headers.Add("Set-Cookie", cookie);
            }

            return response;
        }

        return path.EndsWith("/SearchPage_search", StringComparison.Ordinal)
            ? Json(searchReply)
            : Html("""<table><tr><td>Rel</td><td></td><td></td><td></td><td></td><td><input data-sub-id="77"/></td></tr><tr><td>no id</td></tr></table>""");
    }

    [Fact]
    public async Task Ktuvit_reads_a_wcf_answer_whether_the_payload_is_an_object_or_a_string_and_nothing_else()
    {
        var asString = new KtuvitSubtitleProvider(Factory(r => KtuvitReply(r, """{"d":"{\"Films\":[{\"ID\":\"5\",\"ReleaseYear\":null}]}"}""")));
        var found = Assert.Single(await asString.SearchAsync(Film(year: 1999), Login, CancellationToken.None));
        Assert.Equal("5:77", found.DownloadToken);

        // A film of another year is not the one asked for; a blank id is no film.
        var wrongYear = new KtuvitSubtitleProvider(Factory(r => KtuvitReply(r, """{"d":{"Films":[{"ID":"5","ReleaseYear":2000},{"ID":" ","ReleaseYear":1999}]}}""")));
        Assert.Empty(await wrongYear.SearchAsync(Film(year: 1999), Login, CancellationToken.None));

        foreach (var reply in new[] { """{"nod":1}""", """{"d":{"Films":5}}""", """{"d":{"Other":[]}}""", "not json", """{"d":"not json"}""" })
        {
            var provider = new KtuvitSubtitleProvider(Factory(r => KtuvitReply(r, reply)));
            Assert.Empty(await provider.SearchAsync(Film(), Login, CancellationToken.None));
        }
    }

    [Fact]
    public async Task Ktuvit_finds_nothing_when_it_cannot_sign_in_or_the_search_fails_or_the_listing_is_empty()
    {
        const string films = """{"d":{"Films":[{"ID":"5","ReleaseYear":2021}]}}""";

        foreach (var cookie in new[] { null, "Other=1; path=/" })
        {
            var noSession = new KtuvitSubtitleProvider(Factory(r => KtuvitReply(r, films, cookie)));
            Assert.Empty(await noSession.SearchAsync(Film(), Login, CancellationToken.None));
        }

        var refused = new KtuvitSubtitleProvider(Factory(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        Assert.Empty(await refused.SearchAsync(Film(), Login, CancellationToken.None));

        var searchFails = new KtuvitSubtitleProvider(Factory(r => r.RequestUri!.AbsolutePath.EndsWith("/Login", StringComparison.Ordinal)
            ? KtuvitReply(r, films)
            : new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        Assert.Empty(await searchFails.SearchAsync(Film(), Login, CancellationToken.None));

        var emptyListing = new KtuvitSubtitleProvider(Factory(r => r.RequestUri!.AbsolutePath.EndsWith("/Login", StringComparison.Ordinal)
            || r.RequestUri.AbsolutePath.EndsWith("/SearchPage_search", StringComparison.Ordinal) ? KtuvitReply(r, films) : Html("")));
        Assert.Empty(await emptyListing.SearchAsync(Film(), Login, CancellationToken.None));
    }

    [Fact]
    public async Task Ktuvit_download_refuses_a_bad_token_a_refused_sign_in_and_an_answer_without_an_identifier()
    {
        var candidate = new SubtitleCandidate("ktuvit", "5:77", "he", false, false);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new KtuvitSubtitleProvider(Nothing).DownloadAsync(candidate with { DownloadToken = "nocolon" }, Login, CancellationToken.None));

        var refused = new KtuvitSubtitleProvider(Factory(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        await Assert.ThrowsAsync<HttpRequestException>(() => refused.DownloadAsync(candidate, Login, CancellationToken.None));

        foreach (var reply in new[] { """{"d":{"Other":"x"}}""", """{"d":{"DownloadIdentifier":5}}""", """{"nod":1}""", "garbage" })
        {
            var provider = new KtuvitSubtitleProvider(Factory(r => r.RequestUri!.AbsolutePath.EndsWith("/Login", StringComparison.Ordinal)
                ? KtuvitReply(r, "")
                : Json(reply)));
            await Assert.ThrowsAsync<HttpRequestException>(() => provider.DownloadAsync(candidate, Login, CancellationToken.None));
        }

        var stringShaped = new KtuvitSubtitleProvider(Factory(r =>
        {
            var path = r.RequestUri!.AbsolutePath;
            return path.EndsWith("/Login", StringComparison.Ordinal) ? KtuvitReply(r, "")
                : path.EndsWith("/RequestSubtitleDownload", StringComparison.Ordinal) ? Json("""{"d":"{\"DownloadIdentifier\":\"id9\"}"}""")
                : Bytes([4]);
        }));
        Assert.Equal(new byte[] { 4 }, await stringShaped.DownloadAsync(candidate, Login, CancellationToken.None));
    }
}
