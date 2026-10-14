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
/// #1029: Byparr answers FlareSolverr's <c>/v1</c> shape but its source says
/// it only supports GET -- <c>sessions.create</c>/<c>sessions.destroy</c>,
/// <c>request.post</c>'s <c>postData</c> and a per-request <c>proxy</c> are
/// all silently ignored rather than refused. Deluno tells FlareSolverr and
/// Byparr apart (by probing, or by what the person says) and never sends
/// Byparr a request it cannot perform -- it says so in plain words instead.
/// </summary>
public sealed class CloudflareHelperKindTests
{
    private static readonly Uri TargetUrl = new("https://tracker.example.test/search");

    [Fact]
    public async Task Auto_detects_Byparr_from_its_own_landing_page_and_skips_sessions_create()
    {
        var handler = new FakeHandler(landingPageBody: "Byparr is alive and kicking!");
        var (helper, _) = CreateHelper(handler, CloudflareHelperKinds.Auto);

        var solution = await helper.FetchAsync(TargetUrl, CancellationToken.None);

        Assert.NotNull(solution);
        Assert.DoesNotContain(handler.Commands, command => command == "sessions.create");
        Assert.Contains(handler.Commands, command => command == "request.get");
    }

    [Fact]
    public async Task Auto_detects_FlareSolverr_when_the_landing_page_does_not_say_Byparr()
    {
        var handler = new FakeHandler(landingPageBody: RecordedReplies.Flaresolverr("index").Single.Reply.Body!);
        var (helper, _) = CreateHelper(handler, CloudflareHelperKinds.Auto);

        await helper.FetchAsync(TargetUrl, CancellationToken.None);

        Assert.Contains(handler.Commands, command => command == "sessions.create");
    }

    [Fact]
    public async Task A_POST_through_Byparr_is_refused_before_it_is_sent_with_a_plain_message()
    {
        var handler = new FakeHandler(landingPageBody: "Byparr");
        var (helper, _) = CreateHelper(handler, CloudflareHelperKinds.Byparr);

        var failure = await Assert.ThrowsAsync<CloudflareHelperCapabilityException>(
            () => helper.PostAsync(TargetUrl, new Dictionary<string, string> { ["user"] = "me" }, CancellationToken.None));

        Assert.Contains("Byparr cannot send", failure.Message, StringComparison.Ordinal);
        Assert.Contains("FlareSolverr", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Commands, command => command == "request.post");
    }

    [Fact]
    public async Task A_proxied_fetch_through_Byparr_is_refused_before_it_is_sent_with_a_plain_message()
    {
        var handler = new FakeHandler(landingPageBody: "Byparr");
        var (helper, _) = CreateHelper(handler, CloudflareHelperKinds.Byparr);

        var failure = await Assert.ThrowsAsync<CloudflareHelperCapabilityException>(
            () => helper.FetchAsync(TargetUrl, CancellationToken.None, new CloudflareHelperProxy("http://192.168.1.1:8080", null, null)));

        Assert.Contains("Byparr cannot route", failure.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Commands);
    }

    [Fact]
    public async Task An_unproxied_GET_through_Byparr_goes_through_without_a_sessions_call()
    {
        var handler = new FakeHandler(landingPageBody: "Byparr");
        var (helper, _) = CreateHelper(handler, CloudflareHelperKinds.Byparr);

        var solution = await helper.FetchAsync(TargetUrl, CancellationToken.None);

        Assert.NotNull(solution);
        Assert.DoesNotContain(handler.Commands, command => command is "sessions.create" or "sessions.destroy");
    }

    [Fact]
    public async Task Explicitly_choosing_FlareSolverr_skips_the_probe_even_against_a_Byparr_landing_page()
    {
        var handler = new FakeHandler(landingPageBody: "Byparr is alive and kicking!");
        var (helper, _) = CreateHelper(handler, CloudflareHelperKinds.FlareSolverr);

        await helper.FetchAsync(TargetUrl, CancellationToken.None);

        Assert.Contains(handler.Commands, command => command == "sessions.create");
    }

    private static (FlareSolverrCloudflareHelper Helper, FakeHandler Handler) CreateHelper(FakeHandler handler, string kind)
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
                CloudflareHelperUrl = "http://127.0.0.1:8191",
                CloudflareHelperTimeoutSeconds = 60,
                CloudflareHelperKind = kind
            });

        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(instance => instance.CreateClient(FlareSolverrCloudflareHelper.HttpClientName))
            .Returns(new HttpClient(handler));

        return (new FlareSolverrCloudflareHelper(httpClientFactory.Object, settings.Object), handler);
    }

    /// <summary>
    /// Answers a GET (the "auto" kind probe) with <paramref name="landingPageBody"/>
    /// and every POST command with a successful solve, recording each POST
    /// command's <c>cmd</c> so a test can assert which ones were (or were not) sent.
    /// </summary>
    private sealed class FakeHandler(string landingPageBody) : HttpMessageHandler
    {
        public List<string> Commands { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(landingPageBody) };
            }

            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            Commands.Add(document.RootElement.GetProperty("cmd").GetString()!);

            return RecordedReplies.Flaresolverr("request-get-ok").Single.Reply.ToResponse();
        }
    }
}
