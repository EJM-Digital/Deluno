using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Integrations.Search.Trackers;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Tests.Recorded;
using Moq;

namespace Deluno.Integrations.Tests.Search.Trackers;

/// <summary>
/// #749: the Cloudflare helper's own protocol completeness -- a configurable
/// timeout instead of a hard-coded 60 seconds, <c>request.post</c> for a
/// tracker whose challenge needs one, and <c>sessions.destroy</c> so a
/// session does not accumulate forever.
/// </summary>
public sealed class FlareSolverrCloudflareHelperTests
{
    private static readonly Uri TargetUrl = new("https://tracker.example.test/search");

    [Fact]
    public async Task Uses_the_configured_timeout_as_the_helpers_maxTimeout()
    {
        var recorder = new RecordedFlareSolverrHandler();
        var (helper, _) = RecordedCreateHelper(recorder, timeoutSeconds: 42);

        await helper.FetchAsync(TargetUrl, CancellationToken.None);

        var request = Assert.Single(recorder.Requests, request => request.Cmd == "request.get");
        Assert.Equal(42_000, request.MaxTimeout);
    }

    [Fact]
    public async Task Clamps_an_out_of_range_configured_timeout_into_1_to_180_seconds()
    {
        var recorder = new RecordedFlareSolverrHandler();
        var (helper, _) = RecordedCreateHelper(recorder, timeoutSeconds: 999);

        await helper.FetchAsync(TargetUrl, CancellationToken.None);

        var request = Assert.Single(recorder.Requests, request => request.Cmd == "request.get");
        Assert.Equal(180_000, request.MaxTimeout);
    }

    [Fact]
    public async Task A_post_fetch_sends_request_post_with_the_given_form_data()
    {
        var recorder = new RecordedFlareSolverrHandler();
        var (helper, _) = RecordedCreateHelper(recorder, timeoutSeconds: 60);

        var solution = await helper.PostAsync(TargetUrl, new Dictionary<string, string> { ["user"] = "me", ["pass"] = "secret" }, CancellationToken.None);

        Assert.NotNull(solution);
        var request = Assert.Single(recorder.Requests, request => request.Cmd == "request.post");
        Assert.Contains("user=me", request.PostData);
        Assert.Contains("pass=secret", request.PostData);
    }

    [Fact]
    public async Task Destroys_the_session_it_created_when_asked_to_clean_up()
    {
        var recorder = new RecordedFlareSolverrHandler();
        var (helper, _) = RecordedCreateHelper(recorder, timeoutSeconds: 60);
        await helper.FetchAsync(TargetUrl, CancellationToken.None);
        Assert.Contains(recorder.Requests, request => request.Cmd == "sessions.create");

        await helper.DestroySessionAsync(TargetUrl.Host, CancellationToken.None);

        var destroy = Assert.Single(recorder.Requests, request => request.Cmd == "sessions.destroy");
        Assert.Equal($"deluno-{TargetUrl.Host}", destroy.Session);
    }

    [Fact]
    public async Task Destroying_a_host_with_no_session_sends_nothing()
    {
        var recorder = new RecordedFlareSolverrHandler();
        var (helper, _) = RecordedCreateHelper(recorder, timeoutSeconds: 60);

        await helper.DestroySessionAsync("never-fetched.example.test", CancellationToken.None);

        Assert.Empty(recorder.Requests);
    }

    [Fact]
    public async Task DestroyAllSessions_destroys_every_session_that_was_created()
    {
        var recorder = new RecordedFlareSolverrHandler();
        var (helper, _) = RecordedCreateHelper(recorder, timeoutSeconds: 60);
        await helper.FetchAsync(TargetUrl, CancellationToken.None);
        await helper.FetchAsync(new Uri("https://other.example.test/"), CancellationToken.None);

        await helper.DestroyAllSessionsAsync(CancellationToken.None);

        var destroyed = recorder.Requests.Where(request => request.Cmd == "sessions.destroy").Select(request => request.Session).ToArray();
        Assert.Contains($"deluno-{TargetUrl.Host}", destroyed);
        Assert.Contains("deluno-other.example.test", destroyed);
    }

    [Fact]
    public async Task Reconcile_destroys_only_sessions_for_hosts_no_longer_active()
    {
        var recorder = new RecordedFlareSolverrHandler();
        var (helper, _) = RecordedCreateHelper(recorder, timeoutSeconds: 60);
        await helper.FetchAsync(TargetUrl, CancellationToken.None);
        await helper.FetchAsync(new Uri("https://removed.example.test/"), CancellationToken.None);

        await helper.ReconcileSessionsAsync([TargetUrl.Host], CancellationToken.None);

        var destroyed = recorder.Requests.Where(request => request.Cmd == "sessions.destroy").Select(request => request.Session).ToArray();
        Assert.DoesNotContain($"deluno-{TargetUrl.Host}", destroyed);
        Assert.Contains("deluno-removed.example.test", destroyed);
    }

    [Fact]
    public async Task A_fetch_with_an_assigned_proxy_attaches_it_to_the_helpers_request()
    {
        var recorder = new RecordedFlareSolverrHandler();
        var (helper, _) = RecordedCreateHelper(recorder, timeoutSeconds: 60);

        await helper.FetchAsync(TargetUrl, CancellationToken.None, new CloudflareHelperProxy("http://192.168.1.1:8080", "user", "pass"));

        var request = Assert.Single(recorder.Requests, request => request.Cmd == "request.get");
        Assert.NotNull(request.Proxy);
        Assert.Equal("http://192.168.1.1:8080", request.Proxy!.Url);
        Assert.Equal("user", request.Proxy.Username);
        Assert.Equal("pass", request.Proxy.Password);
    }

    [Fact]
    public async Task A_fetch_with_no_assigned_proxy_sends_no_proxy_parameter()
    {
        var recorder = new RecordedFlareSolverrHandler();
        var (helper, _) = RecordedCreateHelper(recorder, timeoutSeconds: 60);

        await helper.FetchAsync(TargetUrl, CancellationToken.None);

        var request = Assert.Single(recorder.Requests, request => request.Cmd == "request.get");
        Assert.Null(request.Proxy);
    }

    [Fact]
    public async Task A_proxy_with_no_credentials_sends_only_its_url()
    {
        var recorder = new RecordedFlareSolverrHandler();
        var (helper, _) = RecordedCreateHelper(recorder, timeoutSeconds: 60);

        await helper.FetchAsync(TargetUrl, CancellationToken.None, new CloudflareHelperProxy("socks5://192.168.1.1:1080", null, null));

        var request = Assert.Single(recorder.Requests, request => request.Cmd == "request.get");
        Assert.Equal("socks5://192.168.1.1:1080", request.Proxy!.Url);
        Assert.Null(request.Proxy.Username);
    }

    /// <summary>#1301: the proxy parameter against FlareSolverr 3.5.2's own recorded reply to a request that carries one.</summary>
    [Fact]
    public async Task The_assigned_proxy_travels_with_the_request_and_the_page_FlareSolverr_solved_is_what_comes_back()
    {
        var recorded = RecordedReplies.Flaresolverr("request-get-ok").Single.Reply.Json()["solution"]!;
        var recorder = new RecordedFlareSolverrHandler();
        var (helper, _) = RecordedCreateHelper(recorder, timeoutSeconds: 60);

        var solution = await helper.FetchAsync(TargetUrl, CancellationToken.None, new CloudflareHelperProxy("http://192.168.1.1:8080", "user", "pass"));

        var request = Assert.Single(recorder.Requests, request => request.Cmd == "request.get");
        Assert.Equal(new RecordedProxy("http://192.168.1.1:8080", "user", "pass"), request.Proxy);
        Assert.NotNull(solution);
        Assert.Equal(recorded["response"]!.GetValue<string>(), solution!.Html);
        Assert.Equal((int)solution.Status, recorded["status"]!.GetValue<int>());
        Assert.Equal(recorded["userAgent"]!.GetValue<string>(), solution.UserAgent);
    }

    [Fact]
    public async Task No_helper_configured_returns_null_and_sends_nothing()
    {
        var recorder = new RecordedFlareSolverrHandler();
        var (helper, _) = RecordedCreateHelper(recorder, timeoutSeconds: 60, helperUrl: null);

        var solution = await helper.FetchAsync(TargetUrl, CancellationToken.None);

        Assert.Null(solution);
        Assert.Empty(recorder.Requests);
    }

    [Fact]
    public async Task A_helper_that_refuses_the_connection_says_the_helper_did_not_answer_not_the_tracker()
    {
        var (helper, _) = RecordedCreateHelper(new RefusingHandler(), timeoutSeconds: 60);

        var failure = await Assert.ThrowsAsync<CloudflareHelperUnavailableException>(() => helper.FetchAsync(TargetUrl, CancellationToken.None));

        Assert.Equal(
            "tracker.example.test is behind a Cloudflare check, and the Cloudflare helper at http://127.0.0.1:8191 did not answer. Start it, or fix its address in Preferences, General.",
            failure.Message);
        Assert.IsType<HttpRequestException>(failure.InnerException);
    }

    [Fact]
    public async Task A_helper_that_times_out_solving_gives_no_solution_and_is_not_taken_for_a_tracker_that_answered()
    {
        var timeout = RecordedReplies.Flaresolverr("request-timeout").Single;
        Assert.Equal(500, timeout.Reply.Status);
        var handler = new RecordedFlareSolverrHandler();
        var (helper, _) = RecordedCreateHelper(new HelperAnswering(handler, "request.get", timeout.Reply), timeoutSeconds: 1);

        var solution = await helper.FetchAsync(TargetUrl, CancellationToken.None);

        Assert.Null(solution);
        Assert.Contains(handler.Requests, request => request.Cmd == "sessions.create");
    }

    [Fact]
    public async Task A_helper_that_answers_with_a_body_that_is_not_its_answer_gives_no_solution_instead_of_failing()
    {
        var notJson = RecordedReplies.Flaresolverr("bad-body").Exchanges[0].Reply;
        var (helper, _) = RecordedCreateHelper(new HelperAnswering(new RecordedFlareSolverrHandler(), "request.get", notJson), timeoutSeconds: 60);

        var solution = await helper.FetchAsync(TargetUrl, CancellationToken.None);

        Assert.Null(solution);
    }

    /// <summary>The recorded helper, except that one command is answered with another recorded reply (a timeout, a body that is not its API).</summary>
    private sealed class HelperAnswering(HttpMessageHandler inner, string command, RecordedReply reply) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post && request.Content is not null)
            {
                var body = await request.Content.ReadAsStringAsync(cancellationToken);
                if (body.Contains($"\"{command}\"", StringComparison.Ordinal))
                {
                    return reply.ToResponse();
                }
            }

            return await new HttpMessageInvoker(inner, disposeHandler: false).SendAsync(request, cancellationToken);
        }
    }

    private sealed class RefusingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException(
                "No connection could be made because the target machine actively refused it.",
                new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused));
    }

    private static (FlareSolverrCloudflareHelper Helper, HttpMessageHandler Handler) RecordedCreateHelper(
        HttpMessageHandler handler, int timeoutSeconds, string? helperUrl = "http://127.0.0.1:8191")
    {
        var settings = new Mock<IPlatformSettingsRepository>();
        settings.Setup(instance => instance.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformSettingsSnapshot(
                MovieRootPath: null,
                SeriesRootPath: null,
                DownloadsPath: null,
                IncompleteDownloadsPath: null,
                AutoStartJobs: true,
                EnableNotifications: true,
                RenameOnImport: true,
                UseHardlinks: false,
                CleanupEmptyFolders: true,
                MovieFolderFormat: "{Movie Title} ({Release Year})",
                SeriesFolderFormat: "{Series Title} ({Series Year})",
                MovieFileFormat: "{Movie Title} ({Release Year}) [{Quality}] [{Custom Formats}]",
                EpisodeFileFormat: "{Series Title} - S{season:00}E{episode:00} - {Episode Title}",
                HostBindAddress: "127.0.0.1",
                HostPort: 5099,
                UrlBase: string.Empty,
                RequireAuthentication: true,
                UiTheme: "system",
                DefaultMovieView: "grid",
                DefaultShowView: "grid",
                MetadataCertificationCountry: "US",
                MetadataLanguage: "en",
                MetadataProviderMode: "direct",
                MetadataBrokerUrl: string.Empty,
                MetadataBrokerConfigured: false,
                MetadataTmdbApiKeyConfigured: false,
                MetadataOmdbApiKeyConfigured: true,
                ReleaseNeverGrabPatterns: string.Empty,
                SearchScoringMode: SearchScoringModes.Hybrid,
                ImportRecoveryRetentionDays: 30,
                UpdatedUtc: DateTimeOffset.UtcNow)
            {
                CloudflareHelperUrl = helperUrl,
                CloudflareHelperTimeoutSeconds = timeoutSeconds
            });

        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(instance => instance.CreateClient(FlareSolverrCloudflareHelper.HttpClientName))
            .Returns(new HttpClient(handler));

        return (new FlareSolverrCloudflareHelper(httpClientFactory.Object, settings.Object), handler);
    }

    /// <summary>
    /// Records every command sent to the helper's own API and answers it with FlareSolverr's real reply to that command
    /// (tests/fixtures/recorded/flaresolverr, #1237): the index page for the kind probe, `session-created`, the page it solved,
    /// `request.post`, and `session-destroyed`.
    /// </summary>
    private sealed class RecordedFlareSolverrHandler : HttpMessageHandler
    {
        public List<RecordedCommand> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                // #1029's "auto" kind probe: a plain GET at the helper's own
                // address, answered the way real FlareSolverr answers it, so
                // every test here that never sets a kind keeps exercising
                // today's FlareSolverr behaviour rather than Byparr's.
                return RecordedReplies.Flaresolverr("index").Single.Reply.ToResponse();
            }

            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            Requests.Add(new RecordedCommand(
                root.GetProperty("cmd").GetString()!,
                root.TryGetProperty("session", out var session) ? session.GetString() : null,
                root.TryGetProperty("maxTimeout", out var maxTimeout) ? maxTimeout.GetInt32() : null,
                root.TryGetProperty("postData", out var postData) ? postData.GetString() : null,
                root.TryGetProperty("proxy", out var proxy)
                    ? new RecordedProxy(
                        proxy.GetProperty("url").GetString()!,
                        proxy.TryGetProperty("username", out var username) ? username.GetString() : null,
                        proxy.TryGetProperty("password", out var password) ? password.GetString() : null)
                    : null));

            var cmd = root.GetProperty("cmd").GetString();
            return cmd switch
            {
                "sessions.create" => RecordedReplies.Flaresolverr("session-created").Exchanges[0].Reply.ToResponse(),
                "sessions.destroy" => RecordedReplies.Flaresolverr("session-destroyed").Exchanges[0].Reply.ToResponse(),
                "request.post" => RecordedReplies.Flaresolverr("request-post").Single.Reply.ToResponse(),
                _ => RecordedReplies.Flaresolverr("request-get-ok").Single.Reply.ToResponse()
            };
        }
    }

    private sealed record RecordedCommand(string Cmd, string? Session, int? MaxTimeout, string? PostData, RecordedProxy? Proxy);

    private sealed record RecordedProxy(string Url, string? Username, string? Password);
}
