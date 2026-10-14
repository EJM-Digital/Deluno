using System.Net;
using Deluno.Contracts;
using Deluno.Integrations.Search.Trackers;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Worker.HealthChecks.Checks;
using Deluno.Tests.Recorded;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>
/// #1027: the Cloudflare helper set in Preferences, General is checked on Health, so a
/// helper that is not running shows up before an indexer test trips on it.
/// </summary>
public sealed class CloudflareHelperHealthCheckTests
{
    [Fact]
    public async Task With_no_helper_address_set_it_is_silent()
    {
        var check = Check(helperUrl: null, new AnsweringHandler(HttpStatusCode.OK));

        Assert.Empty(await check.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_helper_that_answers_is_ok()
    {
        var check = Check("http://127.0.0.1:8191", new AnsweringHandler(HttpStatusCode.OK, RecordedReplies.Flaresolverr("index").Single.Reply.Body!));

        var outcome = Assert.Single(await check.RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Ok, outcome.Status);
        Assert.Contains("FlareSolverr answered at 127.0.0.1:8191", outcome.Message);
    }

    [Fact]
    public async Task A_helper_that_refuses_the_connection_is_a_plain_words_problem()
    {
        var check = Check("http://127.0.0.1:8191", new RefusingHandler());

        var outcome = Assert.Single(await check.RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Equal("system:cloudflare-helper", outcome.Id);
        Assert.Contains("Cloudflare helper at 127.0.0.1:8191 did not answer", outcome.Message);
        Assert.Contains("Start the helper", outcome.Fix);
    }

    [Fact]
    public async Task A_helper_that_answers_with_an_error_is_a_problem()
    {
        var check = Check("http://127.0.0.1:8191", new AnsweringHandler(HttpStatusCode.InternalServerError));

        var outcome = Assert.Single(await check.RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
    }

    private static CloudflareHelperHealthCheck Check(string? helperUrl, HttpMessageHandler handler)
    {
        var settings = new Mock<IPlatformSettingsRepository>();
        settings.Setup(instance => instance.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultSettings() with { CloudflareHelperUrl = helperUrl });
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(instance => instance.CreateClient(FlareSolverrCloudflareHelper.HttpClientName))
            .Returns(new HttpClient(handler));
        return new CloudflareHelperHealthCheck(settings.Object, factory.Object);
    }

    private sealed class AnsweringHandler(HttpStatusCode status, string body = "") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }

    private sealed class RefusingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException(
                "No connection could be made because the target machine actively refused it.",
                new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused));
    }

    private static PlatformSettingsSnapshot DefaultSettings() => new(
        MovieRootPath: null,
        SeriesRootPath: null,
        DownloadsPath: null,
        IncompleteDownloadsPath: null,
        AutoStartJobs: true,
        EnableNotifications: true,
        RenameOnImport: true,
        UseHardlinks: true,
        CleanupEmptyFolders: true,
        MovieFolderFormat: "{Movie}",
        SeriesFolderFormat: "{Series}",
        MovieFileFormat: "{Movie}",
        EpisodeFileFormat: "{Episode}",
        HostBindAddress: "127.0.0.1",
        HostPort: 7979,
        UrlBase: string.Empty,
        RequireAuthentication: false,
        UiTheme: "dark",
        DefaultMovieView: "poster",
        DefaultShowView: "poster",
        MetadataCertificationCountry: "US",
        MetadataLanguage: "en",
        MetadataProviderMode: "auto",
        MetadataBrokerUrl: string.Empty,
        MetadataBrokerConfigured: false,
        MetadataTmdbApiKeyConfigured: false,
        MetadataOmdbApiKeyConfigured: false,
        ReleaseNeverGrabPatterns: string.Empty,
        SearchScoringMode: "balanced",
        ImportRecoveryRetentionDays: 30,
        UpdatedUtc: DateTimeOffset.UnixEpoch);
}
