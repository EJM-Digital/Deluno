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
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Quality.Data;
using Deluno.Series.Data;
using Deluno.Worker.HealthChecks.Checks;
using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>
/// #1002: "Pause automatic searching while more than N downloads are waiting".
/// Off for everyone until turned on; only the routine schedule is held; a pause
/// and its end are each said in Activity once, not on every cycle; an unreadable
/// queue never pauses silently.
/// </summary>
public sealed class AutomaticSearchingPausesWhileManyDownloadsWaitTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-29T10:00:00Z");

    private readonly Mock<IMovieCatalogRepository> _movies = new();
    private readonly Mock<IActivityFeedRepository> _activity = new();
    private readonly List<string> _activityLines = [];
    private readonly SearchQueuePauseMemory _memory = new();
    private bool _enabled = true;
    private int _threshold = 2;
    private int _waitingFilms = 3;
    private int _waitingTv = 0;
    private bool _clientDown;

    public AutomaticSearchingPausesWhileManyDownloadsWaitTests()
    {
        _movies
            .Setup(repository => repository.ListEligibleWantedAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<CatalogueFilters?>()))
            .ReturnsAsync(Array.Empty<MovieWantedItem>());
        _activity
            .Setup(repository => repository.RecordActivityAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string?, string?, string?, string?, CancellationToken>((category, message, _, _, _, _, _) =>
            {
                if (category == "library.search.queuePause")
                {
                    _activityLines.Add(message);
                }
            })
            .ReturnsAsync((ActivityEventItem)null!);
    }

    [Fact]
    public async Task It_is_off_by_default_so_a_long_queue_changes_nothing()
    {
        _enabled = false;

        var result = await ScheduledAsync("movies");

        Assert.DoesNotContain("Held", result);
        Assert.Empty(_activityLines);
        _movies.Verify(repository => repository.ListEligibleWantedAsync(
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<bool>(),
            It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<CatalogueFilters?>()), Times.Once);
    }

    [Fact]
    public async Task Above_the_limit_the_routine_search_is_held_and_at_the_limit_it_runs()
    {
        _waitingFilms = 3;
        var held = await ScheduledAsync("movies");
        Assert.StartsWith("Held", held);
        _movies.Verify(repository => repository.ListEligibleWantedAsync(
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<bool>(),
            It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<CatalogueFilters?>()), Times.Never);

        _waitingFilms = 2;
        var ran = await ScheduledAsync("movies");
        Assert.DoesNotContain("Held", ran);
    }

    [Fact]
    public async Task A_long_film_queue_does_not_hold_back_tv()
    {
        _waitingFilms = 50;
        _waitingTv = 0;

        var result = await ScheduledAsync("tv");

        Assert.DoesNotContain("Held", result);
    }

    [Fact]
    public async Task A_pause_is_said_once_and_its_end_is_said_once()
    {
        _waitingFilms = 5;
        await ScheduledAsync("movies");
        await ScheduledAsync("movies");
        await ScheduledAsync("movies");
        Assert.Single(_activityLines);
        Assert.Contains("paused", _activityLines[0]);

        _waitingFilms = 1;
        await ScheduledAsync("movies");
        await ScheduledAsync("movies");
        Assert.Equal(2, _activityLines.Count);
        Assert.Contains("running again", _activityLines[1]);
    }

    [Fact]
    public async Task A_search_a_person_starts_or_aims_at_one_title_is_never_held()
    {
        _waitingFilms = 50;

        var manual = await SearchAsync("movies", triggeredBy: "manual", target: null);
        var targeted = await SearchAsync("movies", triggeredBy: "manual", target: "movie-1");

        Assert.DoesNotContain("Held", manual);
        Assert.DoesNotContain("Held", targeted);
        Assert.Empty(_activityLines);
    }

    [Fact]
    public async Task An_unreadable_queue_does_not_pause_and_says_so_once()
    {
        _clientDown = true;

        var first = await ScheduledAsync("movies");
        var second = await ScheduledAsync("movies");

        Assert.DoesNotContain("Held", first);
        Assert.DoesNotContain("Held", second);
        Assert.Single(_activityLines);
        Assert.Contains("could not read", _activityLines[0]);
    }

    [Fact]
    public async Task Health_shows_a_pause_and_an_unreadable_queue_and_is_silent_when_off()
    {
        var check = new SearchQueuePauseHealthCheck(BuildGate());

        _waitingFilms = 9;
        var paused = await check.RunAsync(CancellationToken.None);
        Assert.Contains(paused, outcome => outcome.Id == "search:queue-pause:movies" && outcome.Status == HealthCheckStatuses.Problem && outcome.Message.Contains("paused"));
        Assert.Contains(paused, outcome => outcome.Id == "search:queue-pause:tv" && outcome.Status == HealthCheckStatuses.Ok);

        _clientDown = true;
        var unreadable = await check.RunAsync(CancellationToken.None);
        Assert.All(unreadable, outcome => Assert.Equal(HealthCheckStatuses.Problem, outcome.Status));
        Assert.All(unreadable, outcome => Assert.Contains("could not read", outcome.Message));

        _enabled = false;
        Assert.Empty(await check.RunAsync(CancellationToken.None));
    }

    private Task<string> ScheduledAsync(string mediaType) => SearchAsync(mediaType, "schedule", null);

    private ISearchQueuePauseGate BuildGate()
    {
        var settings = new Mock<IPlatformSettingsRepository>();
        settings.Setup(repository => repository.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ((PlatformSettingsSnapshot)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PlatformSettingsSnapshot))) with
            {
                PauseSearchWhenQueueLongEnabled = _enabled,
                PauseSearchWhenQueueLongThreshold = _threshold
            });
        var telemetry = new Mock<IDownloadClientTelemetryService>();
        telemetry.Setup(service => service.GetOverviewAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new DownloadTelemetryOverview(
                new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0),
                [Snapshot()],
                Now));
        return new SearchQueuePauseGate(settings.Object, telemetry.Object, _memory);
    }

    private DownloadClientTelemetrySnapshot Snapshot()
    {
        var queue = new List<DownloadQueueItem>();
        for (var index = 0; index < _waitingFilms; index++) queue.Add(Item($"f{index}", "movies", "downloading"));
        for (var index = 0; index < _waitingTv; index++) queue.Add(Item($"t{index}", "tv", "queued"));
        queue.Add(Item("done", "movies", "completed"));
        var snapshot = new DownloadClientTelemetrySnapshot(
            "client-1", "Client", "qbittorrent", null, _clientDown ? "degraded" : "healthy", null,
            new DownloadClientTelemetryCapabilities(true, true, true, true, true, true, "form"),
            new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0),
            _clientDown ? [] : queue, [], Now);
        return _clientDown
            ? snapshot with { LastFailure = IntegrationFailureFactory.FromLegacy("download-client", "client-1", "Client", "telemetry", "network", "refused") }
            : snapshot;
    }

    private static DownloadQueueItem Item(string id, string mediaType, string status)
        => new(id, "client-1", "Client", "qbittorrent", mediaType, id, id, mediaType, status, 0.1, 0, 0, 100, 10, 0, "", null, Now);

    private Task<string> SearchAsync(string mediaType, string triggeredBy, string? target)
    {
        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(repository => repository.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var jobs = new Mock<IJobQueueRepository>();
        jobs.Setup(repository => repository.ListDownloadsInFlightAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<DownloadInFlight>());
        var gate = BuildGate();
        var payload = JsonSerializer.Serialize(new
        {
            libraryId = "library-1",
            libraryName = mediaType == "movies" ? "Movies" : "Shows",
            mediaType,
            checkMissing = true,
            checkUpgrades = true,
            maxItems = 1,
            retryDelayHours = 24,
            triggeredBy,
            targetEntityId = target
        });
        var job = TestJobs.Create(LibrarySearchJobTypes.For(mediaType), payloadJson: payload);
        LibrarySearchJobHandler handler = mediaType == "movies"
            ? new MoviesLibrarySearchJobHandler(
                libraries.Object, Mock.Of<IQualityRepository>(), jobs.Object, _movies.Object,
                Mock.Of<ISeriesCatalogRepository>(), Mock.Of<IAcquisitionDecisionPipeline>(),
                Mock.Of<IDownloadClientGrabService>(), _activity.Object, new FixedClock(Now),
                mediaStateRepository: Mock.Of<IMediaStateRepository>(), queuePauseGate: gate)
            : new TvLibrarySearchJobHandler(
                libraries.Object, Mock.Of<IQualityRepository>(), jobs.Object, _movies.Object,
                Mock.Of<ISeriesCatalogRepository>(), Mock.Of<IAcquisitionDecisionPipeline>(),
                Mock.Of<IDownloadClientGrabService>(), _activity.Object, new FixedClock(Now),
                mediaStateRepository: Mock.Of<IMediaStateRepository>(), queuePauseGate: gate);
        return RunAsync(handler, job);
    }

    // Past the pause gate the handler runs the real search, which these stubs do not
    // fully feed. Reaching that code at all is the "not held" answer under test.
    private static async Task<string> RunAsync(LibrarySearchJobHandler handler, JobQueueItem job)
    {
        try
        {
            return await handler.HandleAsync(job, CancellationToken.None);
        }
        catch (Exception exception) when (exception is NullReferenceException or ArgumentNullException)
        {
            return "ran";
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
