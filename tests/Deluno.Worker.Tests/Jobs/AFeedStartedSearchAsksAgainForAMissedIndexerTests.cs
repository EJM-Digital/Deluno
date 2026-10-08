using System.Runtime.CompilerServices;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Quality.Contracts;
using Deluno.Quality.Data;
using Deluno.Series.Data;
using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>
/// #1184: the feed watch searches a title because one indexer showed a new release. That search must wait for that
/// indexer, and if the indexer still could not be asked, the title is searched again shortly rather than waiting out
/// the normal retry window. Searches the feed watch did not start keep today's behaviour.
/// </summary>
public sealed class AFeedStartedSearchAsksAgainForAMissedIndexerTests
{
    private static readonly TimeSpan AskAgainAfter = TimeSpan.FromMinutes(2);

    private readonly List<AcquisitionDecisionRequest> _requests = [];
    private readonly List<EnqueueJobRequest> _queued = [];
    private readonly List<DateTimeOffset> _nextEligible = [];

    [Fact]
    public async Task A_feed_search_that_missed_the_indexer_waits_for_it_and_asks_again_shortly_not_after_the_retry_window()
    {
        await SearchAsync(triggeredBy: "rss", priorityIndexerIds: ["usenet-indexer"], missed: "usenet-indexer", outcome: "held");

        // The indexer that started the search is waited for.
        Assert.Equal(["usenet-indexer"], Assert.Single(_requests).PatientIndexerIds);

        // It is asked again after its interval, as the same targeted feed search.
        var again = Assert.Single(_queued);
        Assert.Equal("rss", again.Source);
        Assert.Equal("movie-bunny", again.RelatedEntityId);
        Assert.NotNull(again.ScheduledUtc);
        Assert.InRange(again.ScheduledUtc!.Value - DateTimeOffset.UtcNow, AskAgainAfter - TimeSpan.FromSeconds(30), AskAgainAfter + TimeSpan.FromSeconds(5));
        using var payload = JsonDocument.Parse(again.PayloadJson!);
        Assert.Equal(1, payload.RootElement.GetProperty("followUp").GetInt32());
        Assert.Equal("usenet-indexer", payload.RootElement.GetProperty("priorityIndexerIds")[0].GetString());

        // The title is not parked for the normal retry window (24 hours here).
        Assert.NotEmpty(_nextEligible);
        Assert.All(_nextEligible, next => Assert.True(next - DateTimeOffset.UtcNow < TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task A_feed_search_that_heard_from_every_indexer_keeps_the_normal_retry_window()
    {
        await SearchAsync(triggeredBy: "rss", priorityIndexerIds: ["usenet-indexer"], missed: null, outcome: "held");

        Assert.Empty(_queued);
        Assert.NotEmpty(_nextEligible);
        Assert.All(_nextEligible, next => Assert.True(next - DateTimeOffset.UtcNow > TimeSpan.FromHours(23)));
    }

    [Fact]
    public async Task A_search_the_feed_watch_did_not_start_keeps_the_normal_retry_window_even_when_an_indexer_was_missed()
    {
        await SearchAsync(triggeredBy: "schedule", priorityIndexerIds: null, missed: "usenet-indexer", outcome: "held");

        Assert.Empty(Assert.Single(_requests).PatientIndexerIds ?? []);
        Assert.Empty(_queued);
        Assert.NotEmpty(_nextEligible);
        Assert.All(_nextEligible, next => Assert.True(next - DateTimeOffset.UtcNow > TimeSpan.FromHours(23)));
    }

    [Fact]
    public async Task A_feed_search_stops_asking_again_after_three_follow_ups()
    {
        await SearchAsync(triggeredBy: "rss", priorityIndexerIds: ["usenet-indexer"], missed: "usenet-indexer", outcome: "held", followUp: 3);

        Assert.Empty(_queued);
        Assert.NotEmpty(_nextEligible);
        Assert.All(_nextEligible, next => Assert.True(next - DateTimeOffset.UtcNow > TimeSpan.FromHours(23)));
    }

    private async Task SearchAsync(string triggeredBy, string[]? priorityIndexerIds, string? missed, string outcome, int followUp = 0)
    {
        var plan = new MediaSearchPlan(
            null,
            [],
            "Nothing taken.",
            IndexersNotHeardFrom: missed is null ? null : [new IndexerNotHeardFrom(missed, "Usenet indexer", AskAgainAfter)]);
        var pipeline = new Mock<IAcquisitionDecisionPipeline>();
        pipeline
            .Setup(p => p.PlanAsync(It.IsAny<AcquisitionDecisionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AcquisitionDecisionRequest, CancellationToken>((request, _) => _requests.Add(request))
            .ReturnsAsync(new AcquisitionDecisionPlan(plan, Deluno.Quality.MediaPolicyCatalog.CurrentVersion, outcome, "Nothing taken.", 1, 1, null, false, null, []));

        var movies = new Mock<IMovieCatalogRepository>();
        movies
            .Setup(repository => repository.ListEligibleWantedAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<CatalogueFilters?>()))
            .ReturnsAsync([new MovieWantedItem(
                "movie-bunny", "Big Buck Bunny", 2008, null, "library-movies", "missing", "not here", false,
                null, "WEB 1080p", false, null, null, null, null, false, null, DateTimeOffset.UnixEpoch)]);

        var jobQueue = new Mock<IJobQueueRepository>();
        jobQueue
            .Setup(repository => repository.RecordSearchRetryWindowAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string, string, DateTimeOffset, DateTimeOffset, string, CancellationToken>(
                (_, _, _, _, _, next, _, _, _) => _nextEligible.Add(next))
            .Returns(Task.CompletedTask);

        var scheduler = new Mock<IJobScheduler>();
        scheduler
            .Setup(s => s.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()))
            .Callback<EnqueueJobRequest, CancellationToken>((request, _) => _queued.Add(request))
            .ReturnsAsync((EnqueueJobRequest request, CancellationToken _) => (JobQueueItem)RuntimeHelpers.GetUninitializedObject(typeof(JobQueueItem)));

        var handler = new MoviesLibrarySearchJobHandler(
            Libraries().Object,
            Profiles().Object,
            jobQueue.Object,
            movies.Object,
            Mock.Of<ISeriesCatalogRepository>(),
            pipeline.Object,
            Mock.Of<IDownloadClientGrabService>(),
            Mock.Of<IActivityFeedRepository>(),
            TimeProvider.System,
            jobScheduler: scheduler.Object);
        var payload = JsonSerializer.Serialize(new
        {
            libraryId = "library-movies",
            libraryName = "Movies",
            mediaType = "movies",
            checkMissing = true,
            checkUpgrades = true,
            maxItems = 1,
            retryDelayHours = 24,
            triggeredBy,
            targetEntityId = "movie-bunny",
            priorityIndexerIds,
            followUp
        });

        await handler.HandleAsync(TestJobs.Create(LibrarySearchJobTypes.For("movies"), payloadJson: payload), CancellationToken.None);
    }

    private static Mock<ILibrariesRepository> Libraries()
    {
        var library = (LibraryItem)RuntimeHelpers.GetUninitializedObject(typeof(LibraryItem)) with
        {
            Id = "library-movies",
            Name = "movies",
            MediaType = "movies",
            QualityProfileId = "profile-standard",
            RetryDelayHours = 24
        };
        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(repository => repository.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([library]);
        return libraries;
    }

    private static Mock<IQualityRepository> Profiles()
    {
        var quality = new Mock<IQualityRepository>();
        quality.Setup(repository => repository.ListCustomFormatsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        quality
            .Setup(repository => repository.ListQualityProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new QualityProfileItem(
                    "profile-standard", "Standard", "movies", "WEB 1080p", "WEB 1080p,Bluray 1080p", string.Empty,
                    true, false, false, null, null, false, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)
            ]);
        return quality;
    }
}
