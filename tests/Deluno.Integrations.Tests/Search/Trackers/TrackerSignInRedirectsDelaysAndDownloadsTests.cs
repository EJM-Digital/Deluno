using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Infrastructure.Resilience;
using Deluno.Integrations.Search;
using Deluno.Integrations.Search.Trackers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Integrations.Tests.Search.Trackers;

/// <summary>
/// #1301: the tracker engine's sign-in methods, cookie sign-in, redirect limit, request delay, download resolving, test connection and
/// recent-releases feed. A tracker site has no recorded reply to replay (a private one needs an account), so the pages here are a
/// site written for the test and the engine, definition parser and templates are the real ones.
/// </summary>
public sealed class TrackerSignInRedirectsDelaysAndDownloadsTests
{
    private const string Head = """
        id: sitefortest
        name: Site For Test
        type: private
        encoding: UTF-8
        links:
          - https://tracker.example/
        caps:
          categorymappings:
            - {id: 1, cat: Movies, desc: Movies}
          modes:
            search: [q]
        settings:
          - name: cookie
            type: text
            label: Cookie
        """;

    private const string Tail = """
        search:
          paths:
            - path: browse.php
          rows:
            selector: tr.row
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
              text: 0
        """;

    private const string ResultsPage = """<table><tr class="row"><td><a class="name" href="/t/1">Night.of.the.Living.Dead.1968.1080p</a><a class="dl" href="/dl/1">dl</a></td><td class="size">1.5 GB</td><td class="seeders">9</td></tr></table>""";

    private static readonly byte[] Torrent = "d8:announce30:https://tracker.example/announcee"u8.ToArray();

    private static string Site(string login, string extra = "") => Head + "\n" + login + "\n" + extra + "\n" + Tail;

    private sealed record Call(HttpMethod Method, Uri Uri, string Body, string Cookie);

    private sealed class Handler(Func<Call, HttpResponseMessage> answer, List<Call> calls, CookieContainer jar) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var call = new Call(request.Method, request.RequestUri!, body, jar.GetCookieHeader(request.RequestUri!));
            lock (calls) calls.Add(call);
            var response = answer(call);
            response.RequestMessage ??= request;
            return response;
        }
    }

    private static (TrackerEngine Engine, TrackerConnection Connection, List<Call> Calls) Connect(
        string yaml, Func<Call, HttpResponseMessage> answer, Dictionary<string, string?>? settings = null, TimeProvider? clock = null)
    {
        var calls = new List<Call>();
        var definition = TrackerDefinition.Parse(yaml);
        var engine = new TrackerEngine((jar, _) => new Handler(answer, calls, jar), clock ?? TimeProvider.System, NullLogger<TrackerEngine>.Instance);
        return (engine, new TrackerConnection("indexer-1", definition, definition.Links[0], settings ?? new Dictionary<string, string?>()), calls);
    }

    private static HttpResponseMessage Html(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    // ---- cookie sign-in (tr16) -------------------------------------------------------------------------------------------

    private static readonly string CookieSite = Site("""
        login:
          method: cookie
          path: login.php
          test:
            path: index.php
            selector: a.logout
        """);

    [Fact]
    public async Task A_cookie_site_with_no_cookie_saved_says_to_paste_one_before_asking_the_site_anything()
    {
        var (engine, connection, calls) = Connect(CookieSite, _ => Html(ResultsPage));

        var failure = await Assert.ThrowsAsync<TrackerException>(() => engine.SearchAsync(connection, new TrackerQuery("dune"), CancellationToken.None));

        Assert.Equal("Site For Test signs in with a cookie; paste one from a browser where you are signed in.", failure.Message);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task A_cookie_the_site_does_not_accept_is_named_as_such_and_an_accepted_one_is_sent_on_every_request()
    {
        var expired = Connect(CookieSite, call => Html(call.Uri.AbsolutePath == "/index.php" ? "<a href=\"/login.php\">sign in</a>" : ResultsPage),
            new() { ["cookie"] = "session=old" });
        var fresh = Connect(CookieSite, call => Html(call.Uri.AbsolutePath == "/index.php" ? "<a class=\"logout\" href=\"/out\">out</a>" : ResultsPage),
            new() { ["cookie"] = "session=fresh" });

        var refused = await Assert.ThrowsAsync<TrackerException>(() => expired.Engine.SearchAsync(expired.Connection, new TrackerQuery("dune"), CancellationToken.None));
        var result = await fresh.Engine.SearchAsync(fresh.Connection, new TrackerQuery("dune"), CancellationToken.None);

        Assert.Equal("Site For Test did not accept the cookie. Copy a fresh one from a signed-in browser.", refused.Message);
        Assert.Single(result.Releases);
        Assert.All(fresh.Calls, call => Assert.Contains("session=fresh", call.Cookie, StringComparison.Ordinal));
    }

    // ---- sign-in methods (tr17) ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_get_sign_in_sends_its_inputs_in_the_address()
    {
        var (engine, connection, calls) = Connect(Site("""
            login:
              path: login.php
              method: get
              inputs:
                username: someone
                password: hunter2
            """), call => Html(ResultsPage));

        await engine.SearchAsync(connection, new TrackerQuery("dune"), CancellationToken.None);

        var login = calls.First();
        Assert.Equal(HttpMethod.Get, login.Method);
        Assert.Equal("/login.php", login.Uri.AbsolutePath);
        Assert.Contains("username=someone", login.Uri.Query, StringComparison.Ordinal);
        Assert.Contains("password=hunter2", login.Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sign_in_with_no_method_posts_its_inputs_as_a_form()
    {
        var (engine, connection, calls) = Connect(Site("""
            login:
              path: login.php
              inputs:
                username: someone
                password: hunter2
            """), call => Html(ResultsPage));

        await engine.SearchAsync(connection, new TrackerQuery("dune"), CancellationToken.None);

        var login = calls.First();
        Assert.Equal(HttpMethod.Post, login.Method);
        Assert.Equal("/login.php", login.Uri.AbsolutePath);
        Assert.Contains("username=someone", login.Body, StringComparison.Ordinal);
        Assert.Contains("password=hunter2", login.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_oneurl_sign_in_is_a_single_get_of_the_path_followed_by_the_value()
    {
        var (engine, connection, calls) = Connect(Site("""
            login:
              path: auth/
              method: oneurl
              inputs:
                oneurl: abc123
            """), call => Html(ResultsPage));

        await engine.SearchAsync(connection, new TrackerQuery("dune"), CancellationToken.None);

        var login = calls.First();
        Assert.Equal(HttpMethod.Get, login.Method);
        Assert.Equal("/auth/abc123", login.Uri.AbsolutePath);
    }

    [Fact]
    public async Task A_form_sign_in_reads_a_value_off_the_page_by_selector_and_posts_to_its_submit_path_not_the_forms_own()
    {
        var (engine, connection, calls) = Connect(Site("""
            login:
              path: login.php
              method: form
              form: form#signin
              submitpath: do-login.php
              selectorinputs:
                csrf:
                  selector: input[name=t]
                  attribute: value
              inputs:
                username: someone
                password: hunter2
            """), call => call.Uri.AbsolutePath == "/login.php"
                ? Html("""<form id="signin" action="/ignored.php" method="post"><input name="t" value="tok-9"><input name="hidden" value="h"></form>""")
                : Html(ResultsPage));

        await engine.SearchAsync(connection, new TrackerQuery("dune"), CancellationToken.None);

        var submit = calls.Single(call => call.Method == HttpMethod.Post);
        Assert.Equal("/do-login.php", submit.Uri.AbsolutePath);
        Assert.Contains("csrf=tok-9", submit.Body, StringComparison.Ordinal);
        Assert.Contains("hidden=h", submit.Body, StringComparison.Ordinal);
        Assert.Contains("username=someone", submit.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(calls, call => call.Uri.AbsolutePath == "/ignored.php");
    }

    // ---- redirects (tr23) ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_page_that_keeps_redirecting_is_stopped_after_eight_hops_with_a_plain_message()
    {
        var hops = 0;
        var (engine, connection, calls) = Connect(Site(string.Empty), call => Redirect("/browse.php?hop=" + Interlocked.Increment(ref hops)));

        var failure = await Assert.ThrowsAsync<TrackerException>(() => engine.SearchAsync(connection, new TrackerQuery("dune"), CancellationToken.None));

        Assert.Contains("redirected too many times", failure.Message, StringComparison.Ordinal);
        Assert.Equal(9, calls.Count);
    }

    // ---- request delay (tr27) --------------------------------------------------------------------------------------------

    private sealed class RecordingClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.Parse("2026-10-29T00:00:00Z");
        public List<TimeSpan> Waits { get; } = [];
        public override DateTimeOffset GetUtcNow() => now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Waits.Add(dueTime);
            now += dueTime;
            return base.CreateTimer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    [Fact]
    public async Task A_definitions_request_delay_holds_the_next_request_to_that_many_seconds_after_the_last()
    {
        var clock = new RecordingClock();
        var (engine, connection, calls) = Connect(Site(string.Empty).Replace("encoding: UTF-8", "encoding: UTF-8\nrequestDelay: 4.1"), call => Html(ResultsPage), clock: clock);
        var noDelay = Connect(Site(string.Empty), call => Html(ResultsPage), clock: clock);

        await engine.SearchAsync(connection, new TrackerQuery("one"), CancellationToken.None);
        var afterFirst = clock.Waits.Count;
        await engine.SearchAsync(connection, new TrackerQuery("two"), CancellationToken.None);
        await noDelay.Engine.SearchAsync(noDelay.Connection, new TrackerQuery("one"), CancellationToken.None);
        await noDelay.Engine.SearchAsync(noDelay.Connection, new TrackerQuery("two"), CancellationToken.None);

        Assert.Equal(0, afterFirst);
        var wait = Assert.Single(clock.Waits);
        Assert.Equal(TimeSpan.FromSeconds(4.1), wait);
        Assert.Equal(2, calls.Count);
    }

    // ---- download resolving (tr29) ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_download_page_whose_first_link_is_not_a_torrent_falls_through_to_the_next_selector_and_a_magnet_is_taken_as_is()
    {
        var (engine, connection, calls) = Connect(Site(string.Empty, """
            download:
              selectors:
                - selector: a.first
                  attribute: href
                - selector: a.second
                  attribute: href
                - selector: a.mag
                  attribute: href
            """), call => call.Uri.AbsolutePath switch
        {
            "/t/1" => Html("""<a class="first" href="/web/page">x</a><a class="second" href="/web/other">y</a><a class="mag" href="magnet:?xt=urn:btih:ABCDEF">m</a>"""),
            _ => Html("<html>not a torrent</html>")
        });

        var grab = await engine.ResolveDownloadAsync(connection, "https://tracker.example/t/1", CancellationToken.None);

        Assert.Equal("magnet:?xt=urn:btih:ABCDEF", grab.MagnetUrl);
        Assert.Contains(calls, call => call.Uri.AbsolutePath == "/web/page");
        Assert.Contains(calls, call => call.Uri.AbsolutePath == "/web/other");
    }

    [Fact]
    public async Task A_selector_list_that_finds_no_torrent_says_there_was_no_working_link_and_a_web_page_in_place_of_a_torrent_is_named()
    {
        var withSelectors = Connect(Site(string.Empty, """
            download:
              selectors:
                - selector: a.first
                  attribute: href
            """), call => call.Uri.AbsolutePath == "/t/1" ? Html("""<a class="first" href="/web/page">x</a>""") : Html("<html>sign in</html>"));
        var plain = Connect(Site(string.Empty), call => Html("<html>sign in</html>"));

        var none = await Assert.ThrowsAsync<TrackerException>(() => withSelectors.Engine.ResolveDownloadAsync(withSelectors.Connection, "https://tracker.example/t/1", CancellationToken.None));
        var page = await Assert.ThrowsAsync<TrackerException>(() => plain.Engine.ResolveDownloadAsync(plain.Connection, "https://tracker.example/dl/1", CancellationToken.None));

        Assert.Equal("Site For Test's download page had no working download link.", none.Message);
        Assert.Equal("Site For Test sent a web page instead of a torrent. Its sign-in may have expired.", page.Message);
    }

    [Fact]
    public async Task A_hash_on_the_page_of_a_before_request_becomes_a_magnet_with_the_title_and_the_before_request_is_made_first()
    {
        var (engine, connection, calls) = Connect(Site(string.Empty, """
            download:
              before:
                path: "hash/1"
                method: get
              infohash:
                usebeforeresponse: true
                hash:
                  selector: span.hash
                title:
                  selector: h1
            """), call => call.Uri.AbsolutePath == "/hash/1"
                ? Html("""<h1>Night of the Living Dead</h1><span class="hash">0123456789ABCDEF0123456789ABCDEF01234567</span>""")
                : Html("<html>details</html>"));

        var grab = await engine.ResolveDownloadAsync(connection, "https://tracker.example/t/1", CancellationToken.None);

        Assert.StartsWith("magnet:?xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567", grab.MagnetUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Night", grab.MagnetUrl, StringComparison.Ordinal);
        Assert.Equal("/hash/1", calls.First().Uri.AbsolutePath);
    }

    [Fact]
    public async Task A_definitions_download_headers_go_with_every_download_request()
    {
        var (engine, connection, calls) = Connect(Site(string.Empty, """
            download:
              headers:
                X-Secret: ["token-abc"]
            """), call => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Torrent) });
        HttpRequestMessage? seen = null;
        var engineWithHeaders = new TrackerEngine((_, _) => new Capture(r => seen = r), TimeProvider.System, NullLogger<TrackerEngine>.Instance);

        var grab = await engineWithHeaders.ResolveDownloadAsync(connection, "https://tracker.example/dl/1", CancellationToken.None);

        Assert.NotNull(grab.TorrentFile);
        Assert.True(seen!.Headers.Contains("X-Secret"));
        Assert.Empty(calls);
    }

    private sealed class Capture(Action<HttpRequestMessage> seen) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            seen(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Torrent) });
        }
    }

    // ---- test connection and the recent feed (tr30, tr34, tn41) ----------------------------------------------------------

    private static readonly TrackerDefinitionCatalog Catalog = new(overrideDirectory: null, NullLogger<TrackerDefinitionCatalog>.Instance);

    private static IndexerItem Tracker(string definitionId = "yts", string baseUrl = "https://yts.example/")
        => new(
            Id: "indexer-1", Name: "A tracker", Protocol: "tracker", Privacy: "public", BaseUrl: baseUrl, ApiKey: null, Priority: 1, Categories: "2000,5000",
            Tags: "", MediaScope: "both", IsEnabled: true, HealthStatus: "healthy", LastHealthMessage: null, LastHealthFailureCategory: null, LastHealthLatencyMs: null,
            LastHealthTestUtc: null, ConsecutiveFailures: 0, RateLimitedUntilUtc: null, DisabledReason: null, CreatedUtc: DateTimeOffset.UtcNow, UpdatedUtc: DateTimeOffset.UtcNow)
        { DefinitionId = definitionId };

    private static TrackerRelease Release(string title) => new() { Title = title, DownloadUrl = "https://yts.example/dl/" + title.Length };

    [Fact]
    public async Task A_tracker_test_says_how_many_releases_it_read_that_its_page_was_empty_that_its_rows_were_unreadable_and_that_it_did_not_answer_in_time()
    {
        var engine = new Mock<ITrackerEngine>();
        var next = new Queue<Func<TrackerSearchResult>>();
        engine.Setup(e => e.SearchAsync(It.IsAny<TrackerConnection>(), It.IsAny<TrackerQuery>(), It.IsAny<CancellationToken>()))
            .Returns<TrackerConnection, TrackerQuery, CancellationToken>((_, _, _) => Task.FromResult(next.Dequeue()()));
        var indexers = new TrackerIndexers(Catalog, engine.Object);
        next.Enqueue(() => new TrackerSearchResult([Release("one"), Release("three")], [], 0));
        next.Enqueue(() => new TrackerSearchResult([], [], 0));
        next.Enqueue(() => new TrackerSearchResult([], ["row 1: no title"], 5));

        var healthy = await indexers.TestAsync(Tracker(), CancellationToken.None);
        var empty = await indexers.TestAsync(Tracker(), CancellationToken.None);
        var unreadable = await indexers.TestAsync(Tracker(), CancellationToken.None);

        Assert.Equal(("healthy", "Reached yts.example and read 2 releases from its latest page.", null), healthy);
        Assert.Equal(("healthy", "Reached yts.example; its latest page had nothing in these categories.", null), empty);
        Assert.Equal("degraded", unreadable.HealthStatus);
        Assert.Equal("unexpected-response", unreadable.FailureCategory);
        Assert.Contains("could not read its results: row 1: no title", unreadable.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tracker_test_names_a_refused_sign_in_as_auth_a_tracker_that_is_down_as_unreachable_and_a_slow_one_as_not_answering_in_time()
    {
        var engine = new Mock<ITrackerEngine>();
        var next = new Queue<Func<CancellationToken, Task<TrackerSearchResult>>>();
        engine.Setup(e => e.SearchAsync(It.IsAny<TrackerConnection>(), It.IsAny<TrackerQuery>(), It.IsAny<CancellationToken>()))
            .Returns<TrackerConnection, TrackerQuery, CancellationToken>((_, _, token) => next.Dequeue()(token));
        var indexers = new TrackerIndexers(Catalog, engine.Object, budget: TimeSpan.FromMilliseconds(50));
        next.Enqueue(_ => throw new TrackerException("A tracker did not accept the sign-in. Check the username and password."));
        next.Enqueue(_ => throw new TrackerException("A tracker is not answering."));
        next.Enqueue(async token =>
        {
            await Task.Delay(Timeout.Infinite, token); // reason: a tracker that never answers; the test's own 50 ms budget cancels the token
            return null!;
        });

        var refused = await indexers.TestAsync(Tracker(), CancellationToken.None);
        var down = await indexers.TestAsync(Tracker(), CancellationToken.None);
        var slow = await indexers.TestAsync(Tracker(), CancellationToken.None);

        Assert.Equal("auth", refused.FailureCategory);
        Assert.Equal("degraded", refused.HealthStatus);
        Assert.Equal("connectivity", down.FailureCategory);
        Assert.Equal("unreachable", down.HealthStatus);
        Assert.Equal("unreachable", slow.HealthStatus);
        Assert.Contains("did not answer within", slow.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_trackers_recent_feed_is_a_search_with_no_words_in_the_indexers_categories_and_an_unreadable_one_is_not_read_as_empty()
    {
        TrackerQuery? asked = null;
        var calls = 0;
        var engine = new Mock<ITrackerEngine>();
        engine.Setup(e => e.SearchAsync(It.IsAny<TrackerConnection>(), It.IsAny<TrackerQuery>(), It.IsAny<CancellationToken>()))
            .Returns<TrackerConnection, TrackerQuery, CancellationToken>((_, query, _) =>
            {
                asked ??= query;
                return ++calls == 1
                    ? Task.FromResult(new TrackerSearchResult([Release("Night.of.the.Living.Dead.1968.1080p-GRP"), Release("Dune.2021.2160p-GRP")], [], 0))
                    : throw new TrackerException("A tracker is not answering.");
            });
        var trackerIndexers = new TrackerIndexers(Catalog, engine.Object);
        var throttle = new Mock<IOutboundRequestThrottle>();
        throttle.Setup(t => t.TryAcquireAsync(It.IsAny<string>(), It.IsAny<OutboundRate>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(TimeSpan.Zero);
        var reader = new IndexerFeedReader(Mock.Of<IHttpClientFactory>(), new PassThrough(), throttle.Object, NullLogger<IndexerFeedReader>.Instance, trackerIndexers);

        var feed = await reader.ReadRecentAsync(Tracker(), CancellationToken.None);
        var down = await reader.ReadRecentAsync(Tracker(), CancellationToken.None);

        Assert.True(feed.Read, feed.Problem);
        Assert.Equal(["Night.of.the.Living.Dead.1968.1080p-GRP", "Dune.2021.2160p-GRP"], feed.Releases.Select(release => release.ReleaseName));
        Assert.NotNull(asked);
        Assert.Equal(string.Empty, asked!.Keywords);
        Assert.Equal(TrackerCategories.Parse("2000,5000"), asked.Categories);
        Assert.False(down.Read);
        Assert.Contains("not answering", down.Problem, StringComparison.Ordinal);
    }

    private sealed class PassThrough : IIntegrationResiliencePolicy
    {
        public async Task<IntegrationResilienceResult<T>> ExecuteAsync<T>(IntegrationResilienceRequest request, Func<CancellationToken, Task<T>> operation, Func<T, IntegrationResilienceOutcome> classify, CancellationToken cancellationToken)
            => new(await operation(cancellationToken), false, false, 1, null, null);

        public bool IsCircuitOpen(string key, out DateTimeOffset retryAfterUtc)
        {
            retryAfterUtc = default;
            return false;
        }

        public Task CloseCircuitsAsync(string serviceType, string serviceId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
