using Deluno.Tests.Recorded;
using Deluno.Integrations.Metadata;
using Deluno.Intake.Contracts;
using Deluno.Intake.Data;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Data;
using Deluno.Platform.Data;
using Deluno.Quality;
using Deluno.Series.Data;
using Deluno.Worker.Intake;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Integrations.Tests.Intake;

/// <summary>
/// The IMDb list provider (#1292). On 22 Oct 2026 an anonymous request for a public IMDb list and its /export both came back
/// HTTP 202 with an empty page (an AWS WAF bot check), recorded in tests/fixtures/recorded/imdb. Before the fix Deluno read
/// that as an empty CSV and reported a successful sync of zero titles.
/// </summary>
public sealed class ImdbListProviderTests
{
    private const string Refusal = "IMDb refused the request";

    [Theory]
    [InlineData("ls055592025")]
    [InlineData("https://www.imdb.com/list/ls055592025/")]
    [InlineData("https://www.imdb.com/list/ls055592025/export")]
    public async Task TestSourceAsync_says_plainly_that_imdb_refused_the_request_and_offers_the_alternative(string feed)
    {
        var requested = new List<Uri>();
        var service = CreateService(requested, "bot-check-export", out var source, feed);

        var result = await service.TestSourceAsync(source, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains(Refusal, result.Message);
        Assert.Contains("CSV", result.Message);
        Assert.DoesNotContain("202", result.Message);
        Assert.Equal("https://www.imdb.com/list/ls055592025/export", Assert.Single(requested).ToString());
    }

    [Fact]
    public async Task TestSourceAsync_reads_the_list_page_bot_check_as_a_refusal_too()
    {
        // The list page answers exactly like the export, so a person who pastes a link that is the page gets the same plain words.
        var service = CreateService([], "bot-check-list-page", out var source, "https://www.imdb.com/list/ls055592025/");

        var result = await service.TestSourceAsync(source, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains(Refusal, result.Message);
    }

    [Fact]
    public async Task RunAsync_records_an_error_and_never_a_successful_sync_of_zero_titles()
    {
        var source = Source("ls055592025");
        var intake = new Mock<IIntakeRepository>();
        intake.Setup(repo => repo.GetIntakeSourceAsync(source.Id, It.IsAny<CancellationToken>())).ReturnsAsync(source);
        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Library()]);
        var service = CreateService(intake, libraries, new Capturing([], "bot-check-export"));

        var result = await service.RunAsync(source.Id, null, manual: true, CancellationToken.None);

        Assert.Equal("error", result.Status);
        Assert.Contains(Refusal, result.Summary);
        intake.Verify(repo => repo.RecordIntakeSourceSyncResultAsync(
            source.Id, It.IsAny<DateTimeOffset>(), "error", It.Is<string?>(s => s != null && s.Contains(Refusal)), It.IsAny<CancellationToken>()), Times.Once);
        intake.Verify(repo => repo.RecordIntakeSourceSyncResultAsync(
            source.Id, It.IsAny<DateTimeOffset>(), It.Is<string>(s => s != "error"), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static IntakeSyncService CreateService(List<Uri> requested, string state, out IntakeSourceItem source, string feed)
    {
        var item = Source(feed);
        source = item;
        var intake = new Mock<IIntakeRepository>();
        intake.Setup(repo => repo.GetIntakeSourceAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Library()]);
        return CreateService(intake, libraries, new Capturing(requested, state));
    }

    private static IntakeSyncService CreateService(Mock<IIntakeRepository> intake, Mock<ILibrariesRepository> libraries, HttpMessageHandler handler)
        => new(
            new Mock<IPlatformSettingsRepository>().Object,
            libraries.Object,
            intake.Object,
            new Mock<IJobScheduler>().Object,
            new Mock<IJobQueueRepository>().Object,
            new Mock<IMovieCatalogRepository>().Object,
            new Mock<ISeriesCatalogRepository>().Object,
            new Mock<IMetadataProvider>().Object,
            new Mock<IMediaDecisionService>().Object,
            new Mock<IActivityFeedRepository>().Object,
            new ConfigurationBuilder().Build(),
            TimeProvider.System,
            new Factory(handler),
            NullLogger<IntakeSyncService>.Instance);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(5) };
    }

    /// <summary>Answers any call with the recorded IMDb reply and notes the address that was asked.</summary>
    private sealed class Capturing(List<Uri> requested, string state) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requested.Add(request.RequestUri!);
            return Task.FromResult(RecordedReplies.Imdb(state).Single.Reply.ToResponse());
        }
    }

    private static IntakeSourceItem Source(string feed)
        => new(
            Id: "source-imdb", Name: "IMDb top list", Provider: "imdb", FeedUrl: feed, MediaType: "movies",
            LibraryId: "library-movies", LibraryName: "Movies", QualityProfileId: null, QualityProfileName: null,
            RequiredGenres: string.Empty, MinimumRating: null, MinimumYear: null, MaximumAgeDays: null,
            AllowedCertifications: string.Empty, Audience: "any", SyncIntervalHours: 12, LastSyncUtc: null,
            LastSyncStatus: "never", LastSyncSummary: null, SearchOnAdd: false, IsEnabled: true,
            CreatedUtc: DateTimeOffset.Parse("2026-10-22T00:00:00Z"), UpdatedUtc: DateTimeOffset.Parse("2026-10-22T00:00:00Z"));

    private static LibraryItem Library()
        => new(
            Id: "library-movies", Name: "Movies", MediaType: "movies", Purpose: "Movie collection", RootPath: "D:\\Media\\Movies",
            DownloadsPath: null, QualityProfileId: null, QualityProfileName: null, CutoffQuality: "WEBDL-1080p",
            UpgradeUntilCutoff: true, UpgradeUnknownItems: true, ImportWorkflow: "direct", ProcessorName: null,
            ProcessorOutputPath: null, ProcessorTimeoutMinutes: 60, ProcessorFailureMode: "hold", AutoSearchEnabled: true,
            MissingSearchEnabled: true, UpgradeSearchEnabled: true, SearchIntervalMinutes: 6, RetryDelayHours: 12,
            MaxItemsPerRun: 50, SearchWindowStartHour: null, SearchWindowEndHour: null, AutomationStatus: "active",
            SearchRequested: false, LastSearchedUtc: null, NextSearchUtc: null,
            CreatedUtc: DateTimeOffset.Parse("2026-10-22T00:00:00Z"), UpdatedUtc: DateTimeOffset.Parse("2026-10-22T00:00:00Z"));
}
