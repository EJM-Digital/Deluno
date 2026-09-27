using System.Net;
using System.Text;
using Deluno.Integrations.Subtitles;
using Deluno.Integrations.Subtitles.Providers;

namespace Deluno.Integrations.Tests.Subtitles;

/// <summary>
/// The three providers added for #837 (Titlovi, Assrt, Supersubtitles), each
/// against a recorded shape of its real API response — not a live call, so
/// these stay green when a site is briefly down and would fail loudly the
/// moment a real response shape changes.
/// </summary>
public sealed class SubtitleProviderFixtureTests
{
    [Fact]
    public async Task Titlovi_signs_in_then_searches_and_skips_whole_season_packs()
    {
        var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/gettoken", StringComparison.Ordinal))
            {
                return Json("""{"Token":"tok-1","UserId":"42","ExpirationDate":"2099-01-01T00:00:00Z"}""");
            }

            if (path.EndsWith("/search", StringComparison.Ordinal))
            {
                return Json("""
                    {
                      "SubtitleResults": [
                        { "Lang": "Srpski", "Season": 1, "Episode": 2, "Link": "https://titlovi.example/dl/1", "Release": "Show.S01E02.WEB", "DownloadCount": 40 },
                        { "Lang": "Hrvatski", "Season": 1, "Episode": 0, "Link": "https://titlovi.example/dl/pack", "Release": "Show.S01.WEB", "DownloadCount": 5 }
                      ]
                    }
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var provider = new TitloviSubtitleProvider(new StubFactory(handler));
        var request = new SubtitleSearchRequest("Show", null, 1, 2, null, null, null, ["sr", "hr"], IsEpisode: true);
        var credentials = new SubtitleProviderCredentials(Username: "person", Password: "secret");

        var results = await provider.SearchAsync(request, credentials, CancellationToken.None);

        // Episode 0 is a whole-season archive, not this episode's own track.
        var found = Assert.Single(results);
        Assert.Equal("sr", found.Language);
        Assert.Equal("https://titlovi.example/dl/1", found.DownloadToken);
        Assert.Equal("Show.S01E02.WEB", found.ReleaseName);
    }

    [Fact]
    public async Task Titlovi_search_asks_nothing_without_credentials()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("Should never call out with no credentials."));
        var provider = new TitloviSubtitleProvider(new StubFactory(handler));

        var results = await provider.SearchAsync(
            new SubtitleSearchRequest("Show", null, 1, 2, null, null, null, ["sr"], IsEpisode: true),
            SubtitleProviderCredentials.None,
            CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task Assrt_resolves_each_match_through_its_own_detail_call()
    {
        var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/sub/search", StringComparison.Ordinal))
            {
                return Json("""{"sub":{"subs":[{"id":123,"videoname":"Dune.2021.1080p"}]}}""");
            }

            if (path.EndsWith("/sub/detail", StringComparison.Ordinal))
            {
                Assert.Contains("id=123", request.RequestUri!.Query);
                return Json("""
                    {
                      "sub": {
                        "subs": [
                          {
                            "videoname": "Dune.2021.1080p",
                            "url": "https://assrt.example/file/1.srt",
                            "lang": { "langlist": { "lang_chs": "简体中文", "lang_eng": "English" } }
                          }
                        ]
                      }
                    }
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var provider = new AssrtSubtitleProvider(new StubFactory(handler));
        var request = new SubtitleSearchRequest("Dune", 2021, null, null, null, null, null, ["en", "zh-Hans"], IsEpisode: false);
        var credentials = new SubtitleProviderCredentials(ApiKey: "token-abc");

        var results = await provider.SearchAsync(request, credentials, CancellationToken.None);

        // One detail record naming two languages becomes one candidate per
        // language, both pointing at the same file.
        Assert.Equal(2, results.Count);
        Assert.Contains(results, candidate => candidate.Language == "en" && candidate.DownloadToken == "https://assrt.example/file/1.srt");
        Assert.Contains(results, candidate => candidate.Language == "zh-Hans" && candidate.DownloadToken == "https://assrt.example/file/1.srt");
    }

    [Fact]
    public async Task Assrt_season_pack_picks_the_file_matching_the_wanted_episode()
    {
        var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/sub/search", StringComparison.Ordinal))
            {
                return Json("""{"sub":{"subs":[{"id":9,"videoname":"Show S01"}]}}""");
            }

            return Json("""
                {
                  "sub": {
                    "subs": [
                      {
                        "videoname": "Show S01",
                        "filelist": [
                          { "f": "Show.S01E01.srt", "url": "https://assrt.example/file/e01.srt" },
                          { "f": "Show.S01E02.srt", "url": "https://assrt.example/file/e02.srt" }
                        ],
                        "lang": { "langlist": { "lang_eng": "English" } }
                      }
                    ]
                  }
                }
                """);
        });

        var provider = new AssrtSubtitleProvider(new StubFactory(handler));
        var request = new SubtitleSearchRequest("Show", null, 1, 2, null, null, null, ["en"], IsEpisode: true);

        var results = await provider.SearchAsync(request, new SubtitleProviderCredentials(ApiKey: "tok"), CancellationToken.None);

        var found = Assert.Single(results);
        Assert.Equal("https://assrt.example/file/e02.srt", found.DownloadToken);
    }

    [Fact]
    public async Task Supersubtitles_resolves_the_show_id_then_reads_the_season_list_and_skips_season_packs()
    {
        var handler = new RecordingHandler(request =>
        {
            var query = request.RequestUri!.Query;
            if (query.Contains("action=autoname", StringComparison.Ordinal))
            {
                return Json("""[{"ID":"3725","name":"Severance (2022)"}]""");
            }

            if (query.Contains("action=xbmc", StringComparison.Ordinal))
            {
                Assert.Contains("sid=3725", query);
                return Json("""
                    {
                      "10": { "language": "Angol", "fnev": "Severance.S01E02.WEB.srt", "felirat": "555", "feltolto": "uploader1", "ep": "2" },
                      "11": { "language": "Magyar", "fnev": "Severance.S01.pack.srt", "felirat": "556", "feltolto": "uploader2", "ep": "-1" }
                    }
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var provider = new SupersubtitlesSubtitleProvider(new StubFactory(handler));
        var request = new SubtitleSearchRequest("Severance", null, 1, 2, null, null, null, ["en"], IsEpisode: true);

        var results = await provider.SearchAsync(request, SubtitleProviderCredentials.None, CancellationToken.None);

        var found = Assert.Single(results);
        Assert.Equal("en", found.Language);
        Assert.Equal("https://www.feliratok.eu/index.php?action=letolt&felirat=555", found.DownloadToken);
    }

    [Fact]
    public async Task Supersubtitles_answers_nothing_for_a_movie_request()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("Movies have no API here; must not call out."));
        var provider = new SupersubtitlesSubtitleProvider(new StubFactory(handler));

        var results = await provider.SearchAsync(
            new SubtitleSearchRequest("A Film", 2020, null, null, null, null, null, ["en"], IsEpisode: false),
            SubtitleProviderCredentials.None,
            CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public void Registry_orders_the_three_new_providers_after_the_original_six()
    {
        var registry = new SubtitleProviderRegistry([
            new AssrtSubtitleProvider(new StubFactory(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)))),
            new TitloviSubtitleProvider(new StubFactory(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)))),
            new SupersubtitlesSubtitleProvider(new StubFactory(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))))
        ]);

        var keys = registry.All.Select(provider => provider.Key).ToArray();

        // Supersubtitles needs no account, like Gestdown and Podnapisi, so it
        // is found before the two that do (Titlovi, Assrt).
        Assert.Equal(["supersubtitles", "titlovi", "assrt"], keys);
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
