using System.Net;
using Deluno.Integrations.Subtitles;
using Deluno.Integrations.Subtitles.Providers;
using static Deluno.Integrations.Tests.Subtitles.ProviderStubs;

namespace Deluno.Integrations.Tests.Subtitles;

/// <summary>
/// The key-and-login subtitle providers' search replies, flags and downloads,
/// read from the shapes each service answers with. #1268.
/// </summary>
public sealed class KeyedSubtitleProviderGapsTests
{
    private static readonly IHttpClientFactory Nothing = Factory(_ => throw new InvalidOperationException("Must not call out."));

    private static SubtitleSearchRequest Episode(string title = "Show", int season = 1, int episode = 2, params string[] languages)
        => new(title, null, season, episode, null, null, null, languages, IsEpisode: true);

    private static SubtitleSearchRequest Film(string title = "Dune", int? year = 2021, params string[] languages)
        => new(title, year, null, null, null, null, null, languages, IsEpisode: false);

    private static SubtitleSearchRequest EpisodeNoNumbers()
        => new("Show", null, null, null, null, null, null, [], IsEpisode: true);

    private static readonly SubtitleProviderCredentials KeyOnly = new(ApiKey: "key");
    private static readonly SubtitleProviderCredentials Login = new(Username: "person@example.com", Password: "secret");

    [Fact]
    public async Task OpenSubtitles_reads_a_search_reply_and_skips_entries_with_no_file()
    {
        var provider = new OpenSubtitlesSubtitleProvider(Factory(request =>
        {
            Assert.Contains("year=2021", request.RequestUri!.Query);
            Assert.Equal("key", request.Headers.GetValues("Api-Key").Single());
            return Json("""
                {"data":[
                  {"attributes":{"language":" EN ","hearing_impaired":true,"foreign_parts_only":true,"release":"Rel","download_count":9,"uploader":{"name":"up"},"files":[{"file_id":null},{"file_id":42}]}},
                  {"attributes":{"files":[]}},
                  {"attributes":null},
                  {"attributes":{"files":[{"file_id":43}]}}
                ]}
                """);
        }));

        var found = await provider.SearchAsync(Film("Dune", 2021, "en"), KeyOnly, CancellationToken.None);

        Assert.Equal(2, found.Count);
        Assert.Equal("42", found[0].DownloadToken);
        Assert.Equal("en", found[0].Language);
        Assert.True(found[0].HearingImpaired);
        Assert.True(found[0].Forced);
        Assert.Equal("up", found[0].Uploader);
        Assert.Equal(9, found[0].DownloadCount);
        Assert.Equal("", found[1].Language);
        Assert.False(found[1].Forced);
    }

    [Fact]
    public async Task OpenSubtitles_asks_for_an_episode_by_season_and_number_and_needs_a_key()
    {
        string? query = null;
        var provider = new OpenSubtitlesSubtitleProvider(Factory(request => { query = request.RequestUri!.Query; return Json("""{"data":[]}"""); }));
        await provider.SearchAsync(Episode("Show", 3, 4), KeyOnly, CancellationToken.None);
        Assert.Contains("season_number=3", query);
        Assert.Contains("episode_number=4", query);
        Assert.DoesNotContain("languages=", query);

        await provider.SearchAsync(EpisodeNoNumbers(), KeyOnly, CancellationToken.None);
        Assert.DoesNotContain("season_number", query);

        await provider.SearchAsync(Film("Dune", null), KeyOnly, CancellationToken.None);
        Assert.DoesNotContain("year=", query);

        var none = new OpenSubtitlesSubtitleProvider(Nothing);
        Assert.Empty(await none.SearchAsync(Film(), SubtitleProviderCredentials.None, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            none.DownloadAsync(new SubtitleCandidate("opensubtitles", "1", "en", false, false), SubtitleProviderCredentials.None, CancellationToken.None));
    }

    [Fact]
    public async Task OpenSubtitles_downloads_without_a_login_when_only_a_key_is_held()
    {
        var provider = new OpenSubtitlesSubtitleProvider(Factory(request =>
        {
            Assert.DoesNotContain("/login", request.RequestUri!.AbsolutePath);
            return request.RequestUri.AbsolutePath.EndsWith("/download", StringComparison.Ordinal)
                ? Json("""{"link":"https://files.example/s.srt"}""")
                : Bytes([7]);
        }));

        var bytes = await provider.DownloadAsync(new SubtitleCandidate("opensubtitles", "5", "en", false, false), KeyOnly, CancellationToken.None);

        Assert.Equal(new byte[] { 7 }, bytes);
    }

    [Fact]
    public async Task OpenSubtitles_carries_on_without_a_token_when_the_login_gives_none()
    {
        var provider = new OpenSubtitlesSubtitleProvider(Factory(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path.EndsWith("/login", StringComparison.Ordinal) ? Json("""{"token":" "}""")
                : path.EndsWith("/download", StringComparison.Ordinal) ? Json("""{"link":"https://files.example/s.srt"}""")
                : Bytes([8]);
        }));

        var withLogin = new SubtitleProviderCredentials("person", "secret", "key");
        Assert.Equal(new byte[] { 8 }, await provider.DownloadAsync(new SubtitleCandidate("opensubtitles", "5", "en", false, false), withLogin, CancellationToken.None));
    }

    [Fact]
    public async Task SubDl_reads_a_search_reply_and_downloads()
    {
        string? query = null;
        var provider = new SubDlSubtitleProvider(Factory(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/subtitles", StringComparison.Ordinal))
            {
                query = request.RequestUri.Query;
                return Json("""
                    {"status":true,"subtitles":[
                      {"url":"/subtitle/1.zip","lang":"EN","hi":true,"release_name":"Rel","author":"a"},
                      {"download_link":"https://dl.example/2.zip","language":"fr"},
                      {"url":" "},
                      {"url":"/subtitle/3.zip"}
                    ]}
                    """);
            }

            return Bytes([3]);
        }));

        var found = await provider.SearchAsync(Episode("Show", 2, 5, "en", "fr"), KeyOnly, CancellationToken.None);

        Assert.Contains("season_number=2", query);
        Assert.Contains("episode_number=5", query);
        Assert.Contains("type=tv", query);
        Assert.Equal(3, found.Count);
        Assert.StartsWith("http", found[0].DownloadToken);
        Assert.EndsWith("/subtitle/1.zip", found[0].DownloadToken);
        Assert.Equal("https://dl.example/2.zip", found[1].DownloadToken);
        Assert.Equal(["en", "fr", ""], found.Select(c => c.Language));
        Assert.True(found[0].HearingImpaired);
        Assert.Equal(new byte[] { 3 }, await provider.DownloadAsync(found[1], KeyOnly, CancellationToken.None));
    }

    [Fact]
    public async Task SubDl_finds_nothing_without_a_key_or_with_a_failed_status_and_asks_for_a_film_by_name()
    {
        Assert.Empty(await new SubDlSubtitleProvider(Nothing).SearchAsync(Film(), SubtitleProviderCredentials.None, CancellationToken.None));

        string? query = null;
        var failed = new SubDlSubtitleProvider(Factory(request => { query = request.RequestUri!.Query; return Json("""{"status":false}"""); }));
        Assert.Empty(await failed.SearchAsync(Film("Dune", 2021), KeyOnly, CancellationToken.None));
        Assert.Contains("type=movie", query);
        Assert.DoesNotContain("languages=", query);

        var noList = new SubDlSubtitleProvider(Factory(_ => Json("""{"status":true}""")));
        Assert.Empty(await noList.SearchAsync(Episode(), KeyOnly, CancellationToken.None));

        await failed.SearchAsync(EpisodeNoNumbers(), KeyOnly, CancellationToken.None);
        Assert.DoesNotContain("season_number", query);
    }

    [Fact]
    public async Task SubSource_reads_whichever_list_and_link_names_the_reply_uses()
    {
        string? query = null;
        var provider = new SubSourceSubtitleProvider(Factory(request =>
        {
            query = request.RequestUri!.Query;
            Assert.Equal("key", request.Headers.GetValues("X-API-Key").Single());
            return Json("""
                {"data":[
                  {"download_url":"d1","lang":"EN","hi":true,"release_name":"R"},
                  {"downloadLink":"d2","language":"fr","hearing_impaired":true},
                  {"url":"d3"},
                  {"url":""}
                ]}
                """);
        }));

        var found = await provider.SearchAsync(Episode("Show", 1, 2, "en", "fr"), KeyOnly, CancellationToken.None);

        Assert.Contains("season=1", query);
        Assert.Contains("episode=2", query);
        Assert.Equal(["d1", "d2", "d3"], found.Select(c => c.DownloadToken));
        Assert.Equal(["en", "fr", ""], found.Select(c => c.Language));
        Assert.Equal([true, true, false], found.Select(c => c.HearingImpaired));
    }

    [Fact]
    public async Task SubSource_falls_back_through_its_list_names_and_downloads_with_the_key()
    {
        Assert.Empty(await new SubSourceSubtitleProvider(Nothing).SearchAsync(Film(), SubtitleProviderCredentials.None, CancellationToken.None));

        var results = new SubSourceSubtitleProvider(Factory(_ => Json("""{"results":[{"url":"r1","lang":"de"}]}""")));
        Assert.Equal("r1", Assert.Single(await results.SearchAsync(Film(), KeyOnly, CancellationToken.None)).DownloadToken);

        var subtitles = new SubSourceSubtitleProvider(Factory(_ => Json("""{"subtitles":[{"url":"s1"}]}""")));
        Assert.Equal("s1", Assert.Single(await subtitles.SearchAsync(EpisodeNoNumbers(), KeyOnly, CancellationToken.None)).DownloadToken);

        var none = new SubSourceSubtitleProvider(Factory(_ => Json("{}")));
        Assert.Empty(await none.SearchAsync(Film(), KeyOnly, CancellationToken.None));

        var download = new SubSourceSubtitleProvider(Factory(request =>
        {
            Assert.Equal("key", request.Headers.GetValues("X-API-Key").Single());
            return Bytes([6]);
        }));
        Assert.Equal(new byte[] { 6 }, await download.DownloadAsync(new SubtitleCandidate("subsource", "https://x.example/f", "en", false, false), KeyOnly, CancellationToken.None));
    }

    [Fact]
    public async Task Podnapisi_reads_a_search_reply_with_its_flags()
    {
        string? query = null;
        var provider = new PodnapisiSubtitleProvider(Factory(request =>
        {
            query = request.RequestUri!.Query;
            return Json("""
                {"data":[
                  {"id":"a1","language":" EN ","hearing_impaired":true,"flags":["forced"],"releases":["R1","R2"],"author":"au","downloads":12},
                  {"subtitle_id":"b2","language":"fr","flags":["hearing_impaired"]},
                  {"id":"c3","language":"de","flags":["other"]},
                  {"id":"d4","language":"es"},
                  {"id":" ","language":"it"},
                  {"id":"e5","language":" "}
                ]}
                """);
        }));

        var found = await provider.SearchAsync(Episode("Show", 1, 2, "EN", " ", "fr"), Login, CancellationToken.None);

        Assert.Contains("keywords=Show", query);
        Assert.Contains("languages=en|fr", Uri.UnescapeDataString(query!));
        Assert.Contains("season=1", query);
        Assert.Contains("episode=2", query);
        Assert.Equal(["a1", "b2", "c3", "d4"], found.Select(c => c.DownloadToken));
        Assert.Equal([true, true, false, false], found.Select(c => c.HearingImpaired));
        Assert.Equal([true, false, false, false], found.Select(c => c.Forced));
        Assert.Equal("R1", found[0].ReleaseName);
        Assert.Equal("au", found[0].Uploader);
        Assert.Equal(12, found[0].DownloadCount);
    }

    [Fact]
    public async Task Podnapisi_asks_for_a_film_by_year_and_downloads_by_id()
    {
        var queries = new List<string>();
        var provider = new PodnapisiSubtitleProvider(Factory(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/download", StringComparison.Ordinal))
            {
                Assert.NotNull(request.Headers.Authorization);
                return Bytes([2]);
            }

            queries.Add(request.RequestUri.Query);
            return Json("{}");
        }));

        Assert.Empty(await provider.SearchAsync(Film("Dune", 2021), SubtitleProviderCredentials.None, CancellationToken.None));
        Assert.Empty(await provider.SearchAsync(Film("Dune", null), SubtitleProviderCredentials.None, CancellationToken.None));
        var partial = new SubtitleSearchRequest("Show", null, null, null, null, null, null, ["  "], IsEpisode: true);
        Assert.Empty(await provider.SearchAsync(partial, SubtitleProviderCredentials.None, CancellationToken.None));

        Assert.Contains("year=2021", queries[0]);
        Assert.DoesNotContain("year=", queries[1]);
        Assert.DoesNotContain("season=", queries[2]);
        Assert.DoesNotContain("languages=", queries[2]);

        var bytes = await provider.DownloadAsync(new SubtitleCandidate("podnapisi", "a1", "en", false, false), Login, CancellationToken.None);
        Assert.Equal(new byte[] { 2 }, bytes);
    }

    [Fact]
    public async Task Supersubtitles_names_its_languages_and_downloads()
    {
        var provider = new SupersubtitlesSubtitleProvider(Factory(request =>
        {
            var query = request.RequestUri!.Query;
            return query.Contains("autoname", StringComparison.Ordinal) ? Json("""[{"ID":"4","name":"Show"}]""")
                : query.Contains("xbmc", StringComparison.Ordinal) ? Json("""{"a":{"language":"Magyar","felirat":"1","ep":"2"},"b":{"language":"Angol","felirat":"2","ep":"2"},"c":{"language":"Deutsch","felirat":"3"},"d":{"language":"Magyar","felirat":"4","ep":"-1"},"e":{"language":"","felirat":"5"}}""")
                : Bytes([1]);
        }));

        var found = await provider.SearchAsync(Episode("Show"), SubtitleProviderCredentials.None, CancellationToken.None);

        Assert.Equal(["hu", "en", "deutsch"], found.Select(c => c.Language));
        Assert.Equal(new byte[] { 1 }, await provider.DownloadAsync(found[0], SubtitleProviderCredentials.None, CancellationToken.None));
    }

    [Fact]
    public async Task Titlovi_names_its_languages_and_finds_nothing_when_the_sign_in_gives_no_session()
    {
        var provider = new TitloviSubtitleProvider(Factory(request => request.RequestUri!.AbsolutePath.EndsWith("/gettoken", StringComparison.Ordinal)
            ? Json("""{"Token":"t","UserId":"1"}""")
            : Json("""{"SubtitleResults":[{"Lang":"Srpski","Link":"https://t.example/l1"},{"Lang":"Hrvatski","Link":"https://t.example/l2"},{"Lang":"Bosanski","Link":"https://t.example/l3"},{"Lang":"Slovenski","Link":"https://t.example/l4"},{"Lang":"Makedonski","Link":"https://t.example/l5"},{"Lang":"Deutsch","Link":"https://t.example/l6"},{"Lang":"","Link":"x"},{"Lang":"Srpski","Link":""}]}""")));

        var found = await provider.SearchAsync(Film("Dune", 2021, "sr"), Login, CancellationToken.None);
        Assert.Equal(["sr", "hr", "bs", "sl", "mk", "deutsch"], found.Select(c => c.Language));

        foreach (var reply in new[] { "{}", """{"Token":"t"}""", "garbage" })
        {
            var noSession = new TitloviSubtitleProvider(Factory(_ => Json(reply)));
            Assert.Empty(await noSession.SearchAsync(Film(), Login, CancellationToken.None));
        }

        var refused = new TitloviSubtitleProvider(Factory(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        Assert.Empty(await refused.SearchAsync(Film(), Login, CancellationToken.None));

        var bytes = new TitloviSubtitleProvider(Factory(_ => Bytes([1])));
        Assert.Equal(new byte[] { 1 }, await bytes.DownloadAsync(found[0], Login, CancellationToken.None));
    }

    [Fact]
    public async Task Subf2m_lists_up_to_three_subtitles_per_language_and_downloads()
    {
        var provider = new Subf2mSubtitleProvider(Factory(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path.StartsWith("/subtitles/searchbytitle", StringComparison.Ordinal)
                ? Html("""<a href="/subtitles/searchbytitle?query=x">self</a><a href="/subtitles/dune-2021">Dune</a>""")
                : path.EndsWith("/english", StringComparison.Ordinal)
                    ? Html("""<a href="/subtitles/dune-2021/english/1">1</a><a href='/subtitles/dune-2021/english/1'>dup</a><a href="/subtitles/dune-2021/english/2">2</a><a href="/subtitles/dune-2021/english/3">3</a><a href="/subtitles/dune-2021/english/4">4</a><a href="/subtitles/other/english/9">other</a>""")
                    : Bytes([1]);
        }));

        var found = await provider.SearchAsync(Film("Dune", 2021), SubtitleProviderCredentials.None, CancellationToken.None);

        Assert.Equal(3, found.Count);
        Assert.All(found, c => Assert.Equal("en", c.Language));
        Assert.EndsWith("/subtitles/dune-2021/english/1/download", found[0].DownloadToken);
        Assert.Equal(new byte[] { 1 }, await provider.DownloadAsync(found[0], SubtitleProviderCredentials.None, CancellationToken.None));
    }
}
