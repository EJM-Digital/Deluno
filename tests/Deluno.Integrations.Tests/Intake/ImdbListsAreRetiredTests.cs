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
/// IMDb lists are retired (#1292): IMDb answers programs with a bot check, so no IMDb list can be read. A saved one that
/// is still enabled (a database restored from before the retirement) records one plain error and never a successful sync.
/// </summary>
public sealed class ImdbListsAreRetiredTests
{
    private const string Note = "IMDb lists are no longer supported because IMDb blocks programs; use MDBList or Trakt to import an IMDb list.";

    [Fact]
    public async Task RunAsync_records_the_plain_note_as_an_error_and_never_asks_imdb()
    {
        var source = Source("ls055592025");
        var intake = new Mock<IIntakeRepository>();
        intake.Setup(repo => repo.GetIntakeSourceAsync(source.Id, It.IsAny<CancellationToken>())).ReturnsAsync(source);
        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Library()]);
        var asked = new List<Uri>();
        var service = CreateService(intake, libraries, new Capturing(asked));

        var result = await service.RunAsync(source.Id, null, manual: true, CancellationToken.None);

        Assert.Equal("error", result.Status);
        Assert.Contains(Note, result.Summary);
        Assert.Empty(asked);
        intake.Verify(repo => repo.RecordIntakeSourceSyncResultAsync(
            source.Id, It.IsAny<DateTimeOffset>(), "error", It.Is<string?>(s => s != null && s.Contains(Note)), It.IsAny<CancellationToken>()), Times.Once);
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

    /// <summary>Notes any address asked and answers with a server error.</summary>
    private sealed class Capturing(List<Uri> requested) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requested.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
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
