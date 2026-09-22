using System.Text.Json;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Media;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Quality.Data;
using Deluno.Series.Data;
using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>
/// #560: a search aimed at one title follows the end of that title's download,
/// such as a release refused after it stalled, a file the processor rejected, or
/// a recovery case tried again. The title is still marked downloading then, and
/// a title marked downloading is not a candidate. So the search could start
/// before the reconciler cleared the mark, find nothing, and leave the title
/// waiting for its next routine search.
/// </summary>
public sealed class ASearchForOneTitleIsNotStoppedByItsEndedDownloadTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-22T10:00:00Z");

    private readonly Mock<IJobQueueRepository> _jobs = new();
    private readonly Mock<IMovieCatalogRepository> _movies = new();
    private readonly Mock<IMediaStateRepository> _mediaState = new();
    private readonly List<string> _calls = [];

    public ASearchForOneTitleIsNotStoppedByItsEndedDownloadTests()
    {
        _mediaState
            .Setup(state => state.SetDownloadingAsync(It.IsAny<MediaKind>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Callback<MediaKind, string, bool, DateTimeOffset, CancellationToken>((kind, id, downloading, _, _) => _calls.Add($"mark {kind} {id} {downloading}"))
            .Returns(Task.CompletedTask);
        _movies
            .Setup(repository => repository.ListEligibleWantedAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<CatalogueFilters?>()))
            .Callback(() => _calls.Add("list candidates"))
            .ReturnsAsync(Array.Empty<MovieWantedItem>());
    }

    [Fact]
    public async Task A_title_with_nothing_in_flight_is_put_back_before_the_search_looks_for_it()
    {
        InFlight();

        await SearchAsync(target: "movie-1");

        Assert.Equal(["mark Movie movie-1 False", "list candidates"], _calls);
    }

    [Fact]
    public async Task A_title_with_a_download_still_on_its_way_keeps_its_mark()
    {
        InFlight(new DownloadInFlight("movie", "movie-1", "Arrival.2016.1080p.WEB"));

        await SearchAsync(target: "movie-1");

        Assert.Equal(["list candidates"], _calls);
    }

    [Fact]
    public async Task A_routine_search_leaves_every_mark_to_the_reconciler()
    {
        await SearchAsync(target: null);

        Assert.Equal(["list candidates"], _calls);
        _jobs.Verify(repository => repository.ListDownloadsInFlightAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private void InFlight(params DownloadInFlight[] downloads)
        => _jobs
            .Setup(repository => repository.ListDownloadsInFlightAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(downloads);

    private Task<string> SearchAsync(string? target)
    {
        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(repository => repository.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var handler = new MoviesLibrarySearchJobHandler(
            libraries.Object,
            Mock.Of<IQualityRepository>(),
            _jobs.Object,
            _movies.Object,
            Mock.Of<ISeriesCatalogRepository>(),
            Mock.Of<IAcquisitionDecisionPipeline>(),
            Mock.Of<IDownloadClientGrabService>(),
            Mock.Of<IActivityFeedRepository>(),
            new FixedClock(Now),
            mediaStateRepository: _mediaState.Object);
        var payload = JsonSerializer.Serialize(new
        {
            libraryId = "library-1",
            libraryName = "Movies",
            mediaType = "movies",
            checkMissing = true,
            checkUpgrades = true,
            maxItems = 1,
            retryDelayHours = 24,
            triggeredBy = target is null ? "schedule" : "health-replacement",
            targetEntityId = target
        });
        return handler.HandleAsync(TestJobs.Create(LibrarySearchJobTypes.For("movies"), payloadJson: payload), CancellationToken.None);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
