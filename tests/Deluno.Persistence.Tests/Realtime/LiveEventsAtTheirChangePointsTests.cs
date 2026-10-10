using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Realtime;

/// <summary>
/// #1097: every pushed Live event is published where the change happens, after it is saved,
/// and carries its own data. These read what the one realtime publisher was handed.
/// </summary>
public sealed class LiveEventsAtTheirChangePointsTests : IDisposable
{
    private readonly TestStorage _storage = TestStorage.Create();
    private readonly FixedTimeProvider _time = new(DateTimeOffset.Parse("2026-10-01T09:00:00Z"));
    private readonly RecordingRealtimeEventPublisher _realtime = new();
    private readonly SqliteDownloadDispatchesRepository _dispatches;
    private readonly SqliteJobStore _jobs;

    public LiveEventsAtTheirChangePointsTests()
    {
        new JobsSchemaInitializer(
            _storage.Factory,
            new SqliteDatabaseMigrator(_storage.Factory, _time),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        _dispatches = new SqliteDownloadDispatchesRepository(_storage.Factory, _time, _realtime);
        _jobs = new SqliteJobStore(_storage.Factory, _time, _realtime, _dispatches);
    }

    public void Dispose() => _storage.Dispose();

    private async Task<string> NewDispatchAsync()
        => await _jobs.RecordDownloadDispatchAsync(
            "lib-films", "movies", "movie", "movie-dune", "Dune.Part.Two.2024.2160p", "Fixture Indexer",
            "client-1", "qBittorrent", "sent", null, cancellationToken: CancellationToken.None);

    [Fact]
    public async Task A_grab_a_sighting_a_finish_and_an_import_each_push_their_stage_with_the_release_named()
    {
        // Recording the grab is what creates the dispatch, so the first stage is the grab itself.
        var dispatchId = await NewDispatchAsync();

        await _dispatches.RecordDetectionAsync(dispatchId, "hash-1", 100, completed: false, CancellationToken.None);
        await _dispatches.RecordDetectionAsync(dispatchId, "hash-1", 200, completed: true, CancellationToken.None);
        // The client reports the same download on every pass: that is not another move on the journey.
        await _dispatches.RecordDetectionAsync(dispatchId, "hash-1", 300, completed: true, CancellationToken.None);
        await _dispatches.RecordImportOutcomeAsync(dispatchId, "imported", "/films/Dune.mkv", null, null, CancellationToken.None);

        var stages = _realtime.LiveEventsOf<JourneyStageChangedEvent>(LiveEventNames.JourneyStageChanged);
        Assert.Equal(
            ["grab_succeeded", "detection_succeeded", "client_completed", "import_succeeded"],
            stages.Select(stage => stage.Stage).ToArray());
        Assert.All(stages, stage =>
        {
            Assert.Equal(dispatchId, stage.DispatchId);
            Assert.Equal("Dune.Part.Two.2024.2160p", stage.ReleaseName);
            Assert.Equal("movies", stage.MediaType);
            Assert.Equal("lib-films", stage.LibraryId);
            Assert.Equal("movie-dune", stage.EntityId);
        });
    }

    [Fact]
    public async Task A_failed_import_and_a_download_that_never_showed_push_their_reason()
    {
        var failed = await NewDispatchAsync();
        await _dispatches.RecordImportOutcomeAsync(failed, "failed", null, "no_match", "No film matched this file.", CancellationToken.None);

        var neverShown = await NewDispatchAsync();
        await _dispatches.MarkNeverDetectedAsync(neverShown, "The client never showed it.", CancellationToken.None);

        var stages = _realtime.LiveEventsOf<JourneyStageChangedEvent>(LiveEventNames.JourneyStageChanged);
        Assert.Contains(stages, stage => stage.DispatchId == failed && stage.Stage == "import_failed" && stage.Detail == "No film matched this file.");
        Assert.Contains(stages, stage => stage.DispatchId == neverShown && stage.Stage == "never_detected" && stage.Detail == "The client never showed it.");
    }

    [Fact]
    public async Task A_job_that_starts_and_finishes_pushes_both_with_its_plain_name_and_library()
    {
        await _jobs.EnqueueAsync(
            new EnqueueJobRequest("movies.library.search", "test", """{"libraryId":"lib-films","libraryName":"Films"}""", "library", "lib-films"),
            CancellationToken.None);

        var leased = await _jobs.LeaseNextAsync("worker-1", TimeSpan.FromMinutes(5), null, CancellationToken.None);
        Assert.NotNull(leased);
        await _jobs.CompleteAsync(leased!.Id, "worker-1", "Checked Films.", CancellationToken.None);

        var changes = _realtime.LiveEventsOf<JobChangedEvent>(LiveEventNames.JobChanged);
        Assert.Equal(["queued", "started", "finished"], changes.Select(change => change.State).ToArray());
        Assert.All(changes, change =>
        {
            Assert.Equal(leased.Id, change.JobId);
            Assert.Equal("movies.library.search", change.Kind);
            Assert.Equal("lib-films", change.LibraryId);
            Assert.False(string.IsNullOrWhiteSpace(change.Title));
        });
        Assert.Equal("Checked Films.", changes[2].Message);
    }

    // #1209: Queue import showed "Ready to import" for seconds because nothing said the import job had been queued.
    [Fact]
    public async Task An_import_job_pushes_that_it_was_queued_and_started_so_every_screen_moves_its_row()
    {
        var queued = await _jobs.EnqueueAsync(
            new EnqueueJobRequest("filesystem.import.execute", "filesystem", "{}", "movie", null),
            CancellationToken.None);
        var leased = await _jobs.LeaseNextAsync("worker-1", TimeSpan.FromMinutes(5), null, CancellationToken.None);
        Assert.NotNull(leased);

        Assert.Equal(["queued", "running"], _realtime.ImportStates.Where(change => change.JobId == queued.Id).Select(change => change.State).ToArray());
    }

    // #1180: a search handed back to wait its turn went from running to waiting without a word, so a screen kept "running".
    [Fact]
    public async Task A_job_handed_back_to_wait_pushes_that_it_is_waiting()
    {
        await _jobs.EnqueueAsync(
            new EnqueueJobRequest("movies.library.search", "test", """{"libraryId":"lib-films","libraryName":"Films"}""", "library", "lib-films"),
            CancellationToken.None);
        var leased = await _jobs.LeaseNextAsync("worker-1", TimeSpan.FromMinutes(5), null, CancellationToken.None);

        await _jobs.DeferAsync(leased!.Id, "worker-1", _time.GetUtcNow().AddMinutes(1), CancellationToken.None);

        Assert.Equal("waiting", _realtime.LiveEventsOf<JobChangedEvent>(LiveEventNames.JobChanged).Last().State);
    }

    [Fact]
    public async Task A_job_that_fails_says_whether_it_will_be_tried_again()
    {
        await _jobs.EnqueueAsync(
            new EnqueueJobRequest("movies.library.search", "test", """{"libraryId":"lib-films","libraryName":"Films"}""", "library", "lib-films", MaxAttempts: 2),
            CancellationToken.None);

        var leased = await _jobs.LeaseNextAsync("worker-1", TimeSpan.FromMinutes(5), null, CancellationToken.None);
        await _jobs.FailAsync(leased!.Id, "worker-1", "The indexer timed out.", CancellationToken.None);

        var failure = _realtime.LiveEventsOf<JobChangedEvent>(LiveEventNames.JobChanged).Last();
        Assert.Contains(failure.State, new[] { "retrying", "failed" });
        Assert.NotNull(failure.Message);
    }

    [Fact]
    public async Task A_finished_search_cycle_pushes_its_library_and_count()
    {
        await _jobs.RecordSearchCycleRunAsync(
            new RecordSearchCycleRunRequest(
                "lib-films", "Films", "movies", "scheduled", "completed", 12, 3, 9, null,
                _time.GetUtcNow().AddMinutes(-1), _time.GetUtcNow()),
            CancellationToken.None);

        var progress = Assert.Single(_realtime.LiveEventsOf<SearchCycleProgressEvent>(LiveEventNames.SearchCycleProgress));
        Assert.Equal("lib-films", progress.LibraryId);
        Assert.Equal("Films", progress.LibraryName);
        Assert.Equal("finished", progress.State);
        Assert.Equal(12, progress.Checked);
        Assert.Equal(12, progress.Planned);
    }
}
