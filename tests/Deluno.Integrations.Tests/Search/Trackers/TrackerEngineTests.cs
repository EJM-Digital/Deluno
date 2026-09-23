using System.Net;
using System.Text;
using Deluno.Integrations.Search.Trackers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Integrations.Tests.Search.Trackers;

/// <summary>
/// #606: the engine against a site that behaves like a real tracker: mirrors
/// that turn it away, a sign-in form with a hidden token, a session that
/// expires, and download pages that hold the real link.
/// </summary>
public sealed class TrackerEngineTests
{
    private const string PrivateTracker = """
        id: testtracker
        name: Test Tracker
        type: private
        encoding: UTF-8
        links:
          - https://tracker.example/
        caps:
          categorymappings:
            - {id: 1, cat: Movies/HD, desc: "Movies HD"}
            - {id: 2, cat: TV/HD, desc: "TV HD"}
          modes:
            search: [q]
            movie-search: [q]
        settings:
          - name: username
            type: text
            label: Username
          - name: password
            type: password
            label: Password
        login:
          path: login.php
          method: form
          inputs:
            username: "{{ .Config.username }}"
            password: "{{ .Config.password }}"
          error:
            - selector: div.error
          test:
            path: index.php
            selector: a[href="logout.php"]
        search:
          paths:
            - path: browse.php
          inputs:
            q: "{{ .Keywords }}"
            $raw: "{{ range .Categories }}c{{.}}=1&{{end}}"
          rows:
            selector: table#torrents tr.row
          fields:
            title:
              selector: a.name
            details:
              selector: a.name
              attribute: href
            download:
              selector: a.dl
              attribute: href
            size:
              selector: td.size
            seeders:
              selector: td.seeders
            leechers:
              selector: td.leechers
            date:
              selector: td.added
              filters:
                - name: append
                  args: " +00:00"
                - name: dateparse
                  args: "yyyy-MM-dd HH:mm zzz"
            category:
              selector: a.cat
              attribute: href
              filters:
                - name: querystring
                  args: cat
        """;

    [Fact]
    public async Task Signs_in_through_the_form_with_its_hidden_token_then_searches()
    {
        var site = new FakeTracker();
        var (engine, connection) = Connect(site, PrivateTracker, new() { ["username"] = "james", ["password"] = "secret" });

        var result = await engine.SearchAsync(connection, new TrackerQuery("Night of the Living Dead 1968", "movie", Categories: [2000]), CancellationToken.None);

        Assert.Equal("token-123", site.SubmittedForm!["csrf"]);
        Assert.Equal("james", site.SubmittedForm["username"]);
        Assert.Equal("secret", site.SubmittedForm["password"]);
        Assert.Contains("q=Night+of+the+Living+Dead+1968", site.LastSearch!.Query, StringComparison.Ordinal);
        Assert.Contains("c1=1", site.LastSearch.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("c2=1", site.LastSearch.Query, StringComparison.Ordinal);

        var release = Assert.Single(result.Releases);
        Assert.Equal("Night.of.the.Living.Dead.1968.1080p.BluRay.x264-GRP", release.Title);
        Assert.Equal("https://tracker.example/details.php?id=7", release.DetailsUrl);
        Assert.Equal("https://tracker.example/download.php?id=7", release.DownloadUrl);
        Assert.Equal(8L * 1024 * 1024 * 1024, release.Size);
        Assert.Equal(12, release.Seeders);
        Assert.Equal(15, release.Peers);
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 18, 30, 0, TimeSpan.Zero), release.PublishDate);
        Assert.Equal([2040], release.Categories);
    }

    [Fact]
    public async Task Signs_in_again_when_the_session_has_expired()
    {
        var site = new FakeTracker();
        var (engine, connection) = Connect(site, PrivateTracker, new() { ["username"] = "james", ["password"] = "secret" });
        await engine.SearchAsync(connection, new TrackerQuery("first"), CancellationToken.None);

        site.ExpireSessions();
        var result = await engine.SearchAsync(connection, new TrackerQuery("second"), CancellationToken.None);

        Assert.Equal(2, site.SignIns);
        Assert.Single(result.Releases);
    }

    [Fact]
    public async Task Says_why_the_tracker_refused_the_sign_in()
    {
        var site = new FakeTracker();
        var (engine, connection) = Connect(site, PrivateTracker, new() { ["username"] = "james", ["password"] = "wrong" });

        var failure = await Assert.ThrowsAsync<TrackerException>(() => engine.SearchAsync(connection, new TrackerQuery("x"), CancellationToken.None));

        Assert.Contains("Username or password incorrect", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fetches_the_torrent_file_with_the_signed_in_session()
    {
        var site = new FakeTracker();
        var (engine, connection) = Connect(site, PrivateTracker, new() { ["username"] = "james", ["password"] = "secret" });

        var grab = await engine.ResolveDownloadAsync(connection, "https://tracker.example/download.php?id=7", CancellationToken.None);

        Assert.Null(grab.MagnetUrl);
        Assert.Equal(FakeTracker.Torrent, grab.TorrentFile);
    }

    [Fact]
    public async Task Moves_to_the_next_mirror_when_one_is_behind_a_cloudflare_check()
    {
        var yaml = PublicTracker();
        var requested = new List<string>();
        var (engine, connection) = Connect(request =>
        {
            requested.Add(request.RequestUri!.Host);
            return request.RequestUri.Host == "first.example"
                ? Html(HttpStatusCode.Forbidden, "<html><head><title>Just a moment...</title></head><body class=\"cf-chl\"></body></html>")
                : Html(HttpStatusCode.OK, """<table><tr class="row"><td><a class="name" href="/t/1">Big.Buck.Bunny.2008.1080p</a><a class="mag" href="magnet:?xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567">m</a></td><td class="size">1.5 GB</td><td class="seeders">3</td></tr></table>""");
        }, yaml, []);

        var first = await engine.SearchAsync(connection, new TrackerQuery("big buck bunny"), CancellationToken.None);
        var second = await engine.SearchAsync(connection, new TrackerQuery("big buck bunny"), CancellationToken.None);

        Assert.Single(first.Releases);
        Assert.Single(second.Releases);
        // The mirror that answered is asked first next time.
        Assert.Equal(["first.example", "second.example", "second.example"], requested);
    }

    [Fact]
    public async Task Says_every_mirror_was_tried_when_none_answers()
    {
        var (engine, connection) = Connect(_ => Html(HttpStatusCode.ServiceUnavailable, "down"), PublicTracker(), []);

        var failure = await Assert.ThrowsAsync<TrackerException>(() => engine.SearchAsync(connection, new TrackerQuery("x"), CancellationToken.None));

        Assert.Contains("None of the 2 addresses for Public Test answered", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Finds_the_magnet_on_the_details_page()
    {
        var yaml = PublicTracker() + """

            download:
              selectors:
                - selector: a[href^="https://itorrents"]
                  attribute: href
                - selector: a[href^="magnet:"]
                  attribute: href
            """;
        var (engine, connection) = Connect(request => request.RequestUri!.AbsolutePath switch
        {
            "/t/1" => Html(HttpStatusCode.OK, """<ul><li><a href="https://itorrents.example/x.torrent">torrent</a></li><li><a href="magnet:?xt=urn:btih:ABC">magnet</a></li></ul>"""),
            // The first link is a page, not a torrent, so the magnet is used.
            _ => Html(HttpStatusCode.OK, "<html>not a torrent</html>")
        }, yaml, []);

        var grab = await engine.ResolveDownloadAsync(connection, "https://first.example/t/1", CancellationToken.None);

        Assert.Equal("magnet:?xt=urn:btih:ABC", grab.MagnetUrl);
    }

    private static string PublicTracker() => """
        id: publictest
        name: Public Test
        type: public
        encoding: UTF-8
        links:
          - https://first.example/
          - https://second.example/
        caps:
          categorymappings:
            - {id: 1, cat: Movies, desc: Movies}
          modes:
            search: [q]
        settings: []
        search:
          paths:
            - path: "search/{{ .Keywords }}/"
          rows:
            selector: tr.row
          fields:
            title:
              selector: a.name
            download:
              selector: a.mag
              attribute: href
            size:
              selector: td.size
            seeders:
              selector: td.seeders
            leechers:
              text: 0
        """;

    private static (TrackerEngine Engine, TrackerConnection Connection) Connect(FakeTracker site, string yaml, Dictionary<string, string?> settings)
    {
        var definition = TrackerDefinition.Parse(yaml);
        var engine = new TrackerEngine(cookies => new FakeHandler(request => site.Answer(request, cookies)), TimeProvider.System, NullLogger<TrackerEngine>.Instance);
        return (engine, new TrackerConnection("indexer-1", definition, definition.Links[0], settings));
    }

    private static (TrackerEngine Engine, TrackerConnection Connection) Connect(Func<HttpRequestMessage, HttpResponseMessage> answer, string yaml, Dictionary<string, string?> settings)
    {
        var definition = TrackerDefinition.Parse(yaml);
        var engine = new TrackerEngine(_ => new FakeHandler(answer), TimeProvider.System, NullLogger<TrackerEngine>.Instance);
        return (engine, new TrackerConnection("indexer-1", definition, definition.Links[0], settings));
    }

    private static HttpResponseMessage Html(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = answer(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    /// <summary>A private tracker: a sign-in form with a token, a session cookie, a search page and a .torrent.</summary>
    private sealed class FakeTracker
    {
        public static readonly byte[] Torrent = "d8:announce30:https://tracker.example/announcee"u8.ToArray();

        private readonly HashSet<string> sessions = [];

        public Dictionary<string, string>? SubmittedForm { get; private set; }

        public Uri? LastSearch { get; private set; }

        public int SignIns { get; private set; }

        public void ExpireSessions() => sessions.Clear();

        public HttpResponseMessage Answer(HttpRequestMessage request, CookieContainer cookies)
        {
            var uri = request.RequestUri!;
            var signedIn = cookies.GetCookies(uri)["session"] is { } cookie && sessions.Contains(cookie.Value);
            switch (uri.AbsolutePath)
            {
                case "/login.php" when request.Method == HttpMethod.Get:
                    return Html(HttpStatusCode.OK, """<form action="takelogin.php" method="post"><input type="hidden" name="csrf" value="token-123"><input name="username"><input name="password" type="password"></form>""");

                case "/takelogin.php":
                {
                    var body = request.Content!.ReadAsStringAsync().Result;
                    SubmittedForm = body.Split('&').Select(pair => pair.Split('=')).ToDictionary(pair => WebUtility.UrlDecode(pair[0]), pair => WebUtility.UrlDecode(pair[1]));
                    if (SubmittedForm["password"] != "secret")
                    {
                        return Html(HttpStatusCode.OK, "<div class=\"error\">Username or password incorrect</div>");
                    }

                    SignIns++;
                    var id = Guid.NewGuid().ToString("N");
                    sessions.Add(id);
                    cookies.Add(uri, new Cookie("session", id, "/"));
                    return Html(HttpStatusCode.OK, "<a href=\"logout.php\">Log out</a>");
                }

                case "/index.php":
                    return Html(HttpStatusCode.OK, signedIn ? "<a href=\"logout.php\">Log out</a>" : "<a href=\"login.php\">Log in</a>");

                case "/browse.php":
                    if (!signedIn)
                    {
                        // Where a real tracker redirects: its login page.
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            RequestMessage = new HttpRequestMessage(HttpMethod.Get, new Uri(uri, "/login.php")),
                            Content = new StringContent("<form action=\"takelogin.php\"></form>", Encoding.UTF8, "text/html")
                        };
                    }

                    LastSearch = uri;
                    return Html(HttpStatusCode.OK, """
                        <a href="logout.php">Log out</a>
                        <table id="torrents">
                          <tr class="row">
                            <td><a class="cat" href="browse.php?cat=1">Movies</a></td>
                            <td><a class="name" href="details.php?id=7">Night.of.the.Living.Dead.1968.1080p.BluRay.x264-GRP</a></td>
                            <td><a class="dl" href="download.php?id=7">DL</a></td>
                            <td class="size">8.00 GB</td><td class="seeders">12</td><td class="leechers">3</td>
                            <td class="added">2026-09-20 18:30</td>
                          </tr>
                        </table>
                        """);

                case "/download.php":
                    return signedIn
                        ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Torrent) }
                        : Html(HttpStatusCode.OK, "<a href=\"login.php\">Log in</a>");

                default:
                    return Html(HttpStatusCode.NotFound, "not found");
            }
        }
    }
}
