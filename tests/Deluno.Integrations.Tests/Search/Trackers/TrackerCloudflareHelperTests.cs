using System.Net;
using System.Text;
using System.Text.Json;
using Deluno.Integrations.Search.Trackers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Integrations.Tests.Search.Trackers;

/// <summary>
/// Trackers behind a Cloudflare check, searched through a helper the person
/// runs (#628).
///
/// <para><b>Why this file exists.</b> 1337x answers every request on the rig
/// with a Cloudflare check, and the engine gave up there. FlareSolverr, on the
/// rig, got past it ("Challenge solved!"). The engine now asks such a helper
/// for the page and keeps what it earned for the requests after.</para>
/// </summary>
public sealed class TrackerCloudflareHelperTests
{
    private const string HelperUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/153.0.0.0 Safari/537.36";
    private const string Challenge = "<html><head><title>Just a moment...</title></head><body><div id=\"challenge-platform\"></div></body></html>";
    private const string Results = "<table><tr class=\"row\"><td><a class=\"name\">Big Buck Bunny 2008 1080p</a></td><td><a class=\"mag\" href=\"magnet:?xt=urn:btih:ABC\">m</a></td><td class=\"size\">1 GB</td><td class=\"seeders\">5</td></tr></table>";

    [Fact]
    public async Task A_cleared_session_searches_directly_with_the_helpers_cookie_and_user_agent()
    {
        var site = new CloudflareSite(honoursClearance: true);
        var helper = new FakeHelper(Results);
        var (engine, connection) = Connect(site, helper);

        var first = await engine.SearchAsync(connection, new TrackerQuery("big buck bunny"), CancellationToken.None);
        var second = await engine.SearchAsync(connection, new TrackerQuery("big buck bunny"), CancellationToken.None);

        Assert.Single(first.Releases);
        Assert.Single(second.Releases);
        Assert.Equal(1, helper.Calls);
        Assert.Equal(HelperUserAgent, site.LastUserAgent);
    }

    [Fact]
    public async Task When_Cloudflare_still_turns_the_connection_away_the_helpers_page_is_used()
    {
        var site = new CloudflareSite(honoursClearance: false);
        var helper = new FakeHelper(Results);
        var (engine, connection) = Connect(site, helper);

        var result = await engine.SearchAsync(connection, new TrackerQuery("big buck bunny"), CancellationToken.None);

        Assert.Equal("Big Buck Bunny 2008 1080p", Assert.Single(result.Releases).Title);
    }

    [Fact]
    public async Task Without_a_helper_a_blocked_tracker_says_what_would_get_past_it()
    {
        var (engine, connection) = Connect(new CloudflareSite(honoursClearance: true), helper: null);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => engine.SearchAsync(connection, new TrackerQuery("x"), CancellationToken.None));

        Assert.Contains("Cloudflare helper", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_helper_that_cannot_get_through_leaves_the_tracker_blocked()
    {
        var (engine, connection) = Connect(new CloudflareSite(honoursClearance: true), new FakeHelper(Challenge));

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => engine.SearchAsync(connection, new TrackerQuery("x"), CancellationToken.None));

        Assert.Contains("Cloudflare", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_helper_that_cannot_perform_the_request_fails_the_search_with_its_own_plain_message()
    {
        var (engine, connection) = Connect(new CloudflareSite(honoursClearance: true), new CapabilityRefusingHelper());

        var failure = await Assert.ThrowsAsync<CloudflareHelperCapabilityException>(
            () => engine.SearchAsync(connection, new TrackerQuery("x"), CancellationToken.None));

        Assert.Contains("Byparr cannot", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>The answer FlareSolverr gave for https://1337x.to/ on the rig, trimmed.</summary>
    [Fact]
    public void Reads_a_FlareSolverr_answer()
    {
        const string json = """
            {"status":"ok","message":"Challenge solved!","solution":{"url":"https://1337x.to/","status":200,
             "cookies":[{"name":"cf_clearance","value":"abc","domain":".1337x.to","path":"/"}],
             "userAgent":"Mozilla/5.0 Chrome/153.0.0.0","response":"<title>Torrent Search Engine | 1337x.to</title>"}}
            """;
        var answer = JsonSerializer.Deserialize<FlareSolverrCloudflareHelper.Answer>(json);

        var solution = FlareSolverrCloudflareHelper.Read(answer, new Uri("https://1337x.to/"));

        Assert.NotNull(solution);
        Assert.Equal(HttpStatusCode.OK, solution.Status);
        Assert.Equal("Mozilla/5.0 Chrome/153.0.0.0", solution.UserAgent);
        var cookie = Assert.Single(solution.Cookies);
        Assert.Equal(("cf_clearance", "abc", ".1337x.to"), (cookie.Name, cookie.Value, cookie.Domain));
        Assert.Null(FlareSolverrCloudflareHelper.Read(JsonSerializer.Deserialize<FlareSolverrCloudflareHelper.Answer>("""{"status":"error","message":"timeout"}"""), new Uri("https://1337x.to/")));
    }

    /// <summary>
    /// #1027: the helper at its address is not running, so its connection is
    /// refused. That is the helper's fault, not the tracker's: the message
    /// names the helper, and the second mirror is not tried through the same
    /// dead helper.
    /// </summary>
    [Fact]
    public async Task A_helper_that_is_not_running_is_named_and_no_other_mirror_is_tried()
    {
        var site = new CloudflareSite(honoursClearance: true);
        var (engine, connection) = Connect(site, new UnavailableHelper(), secondMirror: true);

        var failure = await Assert.ThrowsAsync<CloudflareHelperUnavailableException>(
            () => engine.SearchAsync(connection, new TrackerQuery("x"), CancellationToken.None));

        Assert.Equal(
            "cloudflared.example is behind a Cloudflare check, and the Cloudflare helper at http://127.0.0.1:8191 did not answer. Start it, or fix its address in Settings.",
            failure.Message);
        Assert.DoesNotContain("could not be reached", failure.Message, StringComparison.Ordinal);
        Assert.Equal(["cloudflared.example"], site.Hosts);
    }

    private sealed class UnavailableHelper : ICloudflareHelper
    {
        public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken, string? helperUrl = null) => Task.FromResult(true);

        public Task<CloudflareSolution?> FetchAsync(Uri url, CancellationToken cancellationToken, CloudflareHelperProxy? proxy = null, string? helperUrl = null)
            => throw new CloudflareHelperUnavailableException(
                url.Host,
                "http://127.0.0.1:8191",
                new HttpRequestException("refused", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused)));

        public Task<CloudflareSolution?> PostAsync(Uri url, IReadOnlyDictionary<string, string> formData, CancellationToken cancellationToken, CloudflareHelperProxy? proxy = null, string? helperUrl = null)
            => FetchAsync(url, cancellationToken, proxy, helperUrl);

        public Task DestroySessionAsync(string host, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DestroyAllSessionsAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReconcileSessionsAsync(IReadOnlyCollection<string> activeHosts, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static (TrackerEngine Engine, TrackerConnection Connection) Connect(CloudflareSite site, ICloudflareHelper? helper, bool secondMirror = false)
    {
        var definition = TrackerDefinition.Parse($$$"""
            id: cloudflared
            name: Cloudflared
            type: public
            encoding: UTF-8
            links:
              - https://cloudflared.example/
            {{{(secondMirror ? "  - https://cloudflared-two.example/" : "")}}}
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
            """);
        var engine = new TrackerEngine((cookies, _) => new FakeHandler(request => site.Answer(request, cookies)), TimeProvider.System, NullLogger<TrackerEngine>.Instance, helper);
        return (engine, new TrackerConnection("indexer-1", definition, definition.Links[0], new Dictionary<string, string?>()));
    }

    /// <summary>
    /// A site behind Cloudflare: a check for every request, unless it carries
    /// the clearance cookie and the User-Agent that earned it, and even then
    /// when Cloudflare will not honour it for this connection.
    /// </summary>
    private sealed class CloudflareSite(bool honoursClearance)
    {
        public string? LastUserAgent { get; private set; }

        /// <summary>The host of each request the site was asked, in order.</summary>
        public List<string> Hosts { get; } = [];

        public HttpResponseMessage Answer(HttpRequestMessage request, CookieContainer cookies)
        {
            Hosts.Add(request.RequestUri!.Host);
            LastUserAgent = request.Headers.UserAgent.ToString();
            var cleared = cookies.GetCookies(request.RequestUri!)["cf_clearance"] is not null && LastUserAgent == HelperUserAgent;
            return honoursClearance && cleared
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Results, Encoding.UTF8, "text/html") }
                : new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent(Challenge, Encoding.UTF8, "text/html") };
        }
    }

    private sealed class FakeHelper(string html) : ICloudflareHelper
    {
        public int Calls { get; private set; }

        public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken, string? helperUrl = null) => Task.FromResult(true);

        public Task<CloudflareSolution?> FetchAsync(Uri url, CancellationToken cancellationToken, CloudflareHelperProxy? proxy = null, string? helperUrl = null)
        {
            Calls++;
            return Task.FromResult<CloudflareSolution?>(new CloudflareSolution(
                url,
                HttpStatusCode.OK,
                html,
                HelperUserAgent,
                [new Cookie("cf_clearance", "cleared", "/", url.Host)]));
        }

        public Task<CloudflareSolution?> PostAsync(Uri url, IReadOnlyDictionary<string, string> formData, CancellationToken cancellationToken, CloudflareHelperProxy? proxy = null, string? helperUrl = null)
            => FetchAsync(url, cancellationToken, proxy, helperUrl);

        public Task DestroySessionAsync(string host, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DestroyAllSessionsAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReconcileSessionsAsync(IReadOnlyCollection<string> activeHosts, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>A helper standing in for Byparr refusing something it cannot do (#1029) -- e.g. a proxied request.</summary>
    private sealed class CapabilityRefusingHelper : ICloudflareHelper
    {
        public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken, string? helperUrl = null) => Task.FromResult(true);

        public Task<CloudflareSolution?> FetchAsync(Uri url, CancellationToken cancellationToken, CloudflareHelperProxy? proxy = null, string? helperUrl = null)
            => throw new CloudflareHelperCapabilityException($"{url.Host} is assigned a proxy, but Byparr cannot route a request through one. Use FlareSolverr for this tracker instead, or remove its proxy.");

        public Task<CloudflareSolution?> PostAsync(Uri url, IReadOnlyDictionary<string, string> formData, CancellationToken cancellationToken, CloudflareHelperProxy? proxy = null, string? helperUrl = null)
            => FetchAsync(url, cancellationToken, proxy, helperUrl);

        public Task DestroySessionAsync(string host, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DestroyAllSessionsAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReconcileSessionsAsync(IReadOnlyCollection<string> activeHosts, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = answer(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }
}
