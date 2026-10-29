using Deluno.Contracts;
using Deluno.Integrations.Metadata;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Movies.Services;
using Deluno.Quality;
using Deluno.Quality.Data;
using Moq;

namespace Deluno.Movies.Tests.Services;

/// <summary>
/// A collection refresh when the metadata provider is down: it is recorded with a plain message and a retry time (the retry
/// interval is six hours), nothing is added, and a cancelled job is cancelled rather than recorded as a failure.
/// </summary>
public sealed class AFailingMetadataProviderLeavesACollectionToRetryTests
{
    private static readonly DateTimeOffset Stamp = DateTimeOffset.Parse("2026-10-29T00:00:00Z");

    private static MovieCollectionItem Collection()
        => new(
            "collection-1", "tmdb", "645", "Nolan Collection", null, null, null, "library-movies", "Movies", "/media/movies",
            Monitored: true, MonitorMovies: true, QualityProfileId: null, QualityProfileName: null, MinimumAvailability: "released",
            SearchOnAdd: true, MemberCount: 2, HeldCount: 0, MissingCount: 2, LastSyncedUtc: null, NextSyncUtc: null, LastSyncError: null,
            CreatedUtc: Stamp, UpdatedUtc: Stamp, Tags: "");

    private static (MovieCollectionService Service, Mock<IMovieCollectionsRepository> Collections, Mock<IMovieCatalogRepository> Movies) Build(Action<Mock<IMetadataProvider>> provider)
    {
        var collections = new Mock<IMovieCollectionsRepository>();
        collections.Setup(repo => repo.GetAsync("collection-1", It.IsAny<CancellationToken>())).ReturnsAsync(Collection());
        var metadata = new Mock<IMetadataProvider>();
        provider(metadata);
        var movies = new Mock<IMovieCatalogRepository>();
        var service = new MovieCollectionService(
            collections.Object, metadata.Object, movies.Object, new Mock<ILibrariesRepository>().Object, new Mock<IQualityRepository>().Object,
            new Mock<IMediaDecisionService>().Object, new Mock<IJobQueueRepository>().Object, new FixedClock(Stamp), null, null);
        return (service, collections, movies);
    }

    [Fact]
    public async Task A_provider_that_answers_nothing_records_a_retry_in_six_hours_and_adds_nothing()
    {
        var (service, collections, movies) = Build(metadata => metadata
            .Setup(provider => provider.GetMovieCollectionAsync("645", It.IsAny<CancellationToken>())).ReturnsAsync((MetadataCollection?)null));

        var result = await service.SyncAsync("collection-1", CancellationToken.None);

        Assert.Equal(0, result.AddedCount);
        Assert.Contains("will try again later", result.Message, StringComparison.Ordinal);
        collections.Verify(repo => repo.RecordSyncResultAsync("collection-1", Stamp.AddHours(6), null, It.Is<string>(message => message.Length > 0), It.IsAny<CancellationToken>()), Times.Once);
        movies.Verify(repo => repo.AddAsync(It.IsAny<CreateMovieRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_provider_that_throws_is_recorded_in_plain_words_with_a_retry_in_six_hours_and_adds_nothing()
    {
        var (service, collections, movies) = Build(metadata => metadata
            .Setup(provider => provider.GetMovieCollectionAsync("645", It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("TMDb is down.")));

        var result = await service.SyncAsync("collection-1", CancellationToken.None);

        Assert.Equal(0, result.AddedCount);
        Assert.StartsWith("Collection refresh failed and will retry later.", result.Message, StringComparison.Ordinal);
        collections.Verify(repo => repo.RecordSyncResultAsync("collection-1", Stamp.AddHours(6), null, It.Is<string>(message => message.Length > 0), It.IsAny<CancellationToken>()), Times.Once);
        movies.Verify(repo => repo.AddAsync(It.IsAny<CreateMovieRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_cancelled_refresh_is_cancelled_and_not_recorded_as_a_failure()
    {
        var (service, collections, _) = Build(metadata => metadata
            .Setup(provider => provider.GetMovieCollectionAsync("645", It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SyncAsync("collection-1", CancellationToken.None));

        collections.Verify(repo => repo.RecordSyncResultAsync(It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
