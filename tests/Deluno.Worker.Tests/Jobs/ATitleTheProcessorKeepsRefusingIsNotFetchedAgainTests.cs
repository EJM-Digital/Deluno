using System.Runtime.CompilerServices;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Quality.Data;
using Deluno.Series.Data;
using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>
/// #583: after a processor has refused five copies of a title, the scheduled
/// search stops fetching more.
///
/// <para><b>Why this file exists.</b> #576 stopped only the search queued
/// straight after a rejection, and its recovery case said "Deluno stopped
/// looking for another". The title was still missing, so the next scheduled
/// search grabbed the next release, the processor refused it, and the rounds
/// went on, each one a full download, with no total counting them.</para>
/// </summary>
public sealed class ATitleTheProcessorKeepsRefusingIsNotFetchedAgainTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-23T10:00:00Z");

    private readonly Mock<IMovieCatalogRepository> _movies = new();
    private readonly Mock<ISeriesCatalogRepository> _series = new();
    private readonly Mock<IAcquisitionDecisionPipeline> _pipeline = new();
    private readonly List<BlockedRelease> _blocklist = [];
    private readonly List<(string Outcome, string? Said)> _attempts = [];

    public ATitleTheProcessorKeepsRefusingIsNotFetchedAgainTests()
    {
        _movies
            .Setup(repository => repository.ListEligibleWantedAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<CatalogueFilters?>()))
            .ReturnsAsync([Blank<MovieWantedItem>() with { MovieId = "movie-1", Title = "The Terror", WantedStatus = WantedStatuses.Missing }]);
        _movies
            .Setup(repository => repository.RecordSearchAttemptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string, DateTimeOffset, DateTimeOffset?, string?, string?, string?, string?, CancellationToken>(
                (_, _, _, outcome, _, _, said, _, _, _, _) => _attempts.Add((outcome, said)))
            .Returns(Task.CompletedTask);
        _series
            .Setup(repository => repository.RecordSearchAttemptAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string?, string, string, string, DateTimeOffset, DateTimeOffset?, string?, string?, string?, string?, CancellationToken>(
                (_, _, _, _, outcome, _, _, said, _, _, _, _) => _attempts.Add((outcome, said)))
            .Returns(Task.CompletedTask);

        // Asking the indexers is the thing a held search must not do. Reaching
        // it at all is the answer, so it stops the test there.
        _pipeline
            .Setup(pipeline => pipeline.PlanAsync(It.IsAny<AcquisitionDecisionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Searched());
    }

    [Fact]
    public async Task A_scheduled_search_holds_a_film_the_processor_has_refused_five_copies_of()
    {
        Refused("movies", "movie-1", 5);

        await SearchMoviesAsync(triggeredBy: "schedule");

        var (outcome, said) = Assert.Single(_attempts);
        Assert.Equal("held", outcome);
        Assert.Contains("refused 5 copies", said);
    }

    [Fact]
    public async Task Four_refusals_are_not_enough_to_stop_looking()
    {
        Refused("movies", "movie-1", 4);

        await Assert.ThrowsAsync<Searched>(() => SearchMoviesAsync(triggeredBy: "schedule"));
    }

    /// <summary>
    /// The way out the recovery case names: unblock one, and the count falls
    /// under the limit. The hold is counted from the blocklist, not stored.
    /// </summary>
    [Fact]
    public async Task Unblocking_a_refused_copy_lets_the_search_resume()
    {
        Refused("movies", "movie-1", 5);
        _blocklist.RemoveAt(0);

        await Assert.ThrowsAsync<Searched>(() => SearchMoviesAsync(triggeredBy: "schedule"));
    }

    [Fact]
    public async Task A_search_a_person_asks_for_is_theirs_to_make()
    {
        Refused("movies", "movie-1", 5);

        await Assert.ThrowsAsync<Searched>(() => SearchMoviesAsync(triggeredBy: "manual"));
    }

    [Fact]
    public async Task Refusals_for_other_reasons_or_other_titles_do_not_count()
    {
        Refused("movies", "movie-2", 5);
        for (var index = 0; index < 5; index++)
        {
            _blocklist.Add(Refusal("movies", "movie-1", index) with { ReasonCode = ImportFailurePolicy.NoVideoStream });
        }

        await Assert.ThrowsAsync<Searched>(() => SearchMoviesAsync(triggeredBy: "schedule"));
    }

    [Fact]
    public async Task An_episode_the_processor_keeps_refusing_is_held_before_any_indexer_is_asked()
    {
        Refused("tv", "episode-1", 5);
        var handler = new EpisodeSearchJobHandler(
            Mock.Of<ILibrariesRepository>(),
            Mock.Of<IQualityRepository>(),
            _series.Object,
            Mock.Of<IJobQueueRepository>(),
            _pipeline.Object,
            Mock.Of<IDownloadClientGrabService>(),
            Mock.Of<IActivityFeedRepository>(),
            new FixedClock(Now),
            blockedReleases: Blocklist());
        var payload = JsonSerializer.Serialize(new
        {
            episodeId = "episode-1",
            seriesId = "series-1",
            libraryId = "library-tv",
            title = "Pioneer One",
            seasonNumber = 1,
            episodeNumber = 1
        });

        await handler.HandleAsync(TestJobs.Create("episode.search", payloadJson: payload), CancellationToken.None);

        Assert.Equal("held", Assert.Single(_attempts).Outcome);
    }

    [Fact]
    public void A_refusal_only_proposed_is_not_counted()
        => Assert.Equal(0, ProcessorRefusals.Count([Refusal("movies", "movie-1", 0) with { State = BlockedReleaseStates.Proposed }]));

    private void Refused(string mediaType, string entityId, int copies)
    {
        for (var index = 0; index < copies; index++)
        {
            _blocklist.Add(Refusal(mediaType, entityId, index));
        }
    }

    private static BlockedRelease Refusal(string mediaType, string entityId, int copy)
        => new(
            $"block-{entityId}-{copy}",
            BlockedReleaseKeys.For($"The.Terror.1963.Copy{copy}-GRP", "Soak Torznab (local)"),
            $"The.Terror.1963.Copy{copy}-GRP",
            "Soak Torznab (local)",
            mediaType,
            entityId,
            "The Terror",
            ImportFailurePolicy.ProcessorRejected,
            "Weir could not process it and removed it.",
            null,
            null,
            null,
            Now);

    private IBlockedReleaseRepository Blocklist()
    {
        var blocklist = new Mock<IBlockedReleaseRepository>();
        blocklist.Setup(repository => repository.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _blocklist.ToArray());
        blocklist.Setup(repository => repository.ListForAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string mediaType, string entityId, CancellationToken _) => _blocklist
                .Where(release => release.MediaType == mediaType && release.EntityId == entityId)
                .ToArray());
        return blocklist.Object;
    }

    private Task<string> SearchMoviesAsync(string triggeredBy)
    {
        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(repository => repository.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var handler = new MoviesLibrarySearchJobHandler(
            libraries.Object,
            Mock.Of<IQualityRepository>(),
            Mock.Of<IJobQueueRepository>(),
            _movies.Object,
            _series.Object,
            _pipeline.Object,
            Mock.Of<IDownloadClientGrabService>(),
            Mock.Of<IActivityFeedRepository>(),
            new FixedClock(Now),
            blockedReleases: Blocklist());
        var payload = JsonSerializer.Serialize(new
        {
            libraryId = "library-1",
            libraryName = "Movies",
            mediaType = "movies",
            checkMissing = true,
            checkUpgrades = true,
            maxItems = 10,
            retryDelayHours = 24,
            triggeredBy
        });
        return handler.HandleAsync(TestJobs.Create(LibrarySearchJobTypes.For("movies"), payloadJson: payload), CancellationToken.None);
    }

    private static T Blank<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    private sealed class Searched : Exception;

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
