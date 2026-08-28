using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Jobs;

/// <summary>
/// Reading files for subtitles is planned by the library cycle, and by nothing
/// else.
///
/// DESIGN-002 rule 3: no second scheduler, no second lane, no second worker.
/// Weir's Subber ships all three, and a second idea of when a window is open
/// is the shape that produced most of the defects this codebase has had to
/// unpick. So these are written against the one planner.
/// </summary>
public sealed class LibraryCycleSubtitleScanTests
{
    [Fact]
    public async Task A_shelf_that_wants_subtitles_gets_one_scan_queued_and_not_two()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-27T01:00:00Z"));
        var store = await CreateStoreAsync(storage, timeProvider);
        var library = Library(wantsSubtitles: true);

        Assert.True(await store.RequestLibrarySearchAsync(library, CancellationToken.None));
        await store.PlanLibrarySearchesAsync([library], CancellationToken.None);

        var queued = await ScanJobsAsync(store);
        Assert.Single(queued);
        Assert.Equal("library", queued[0].RelatedEntityType);
        Assert.Equal("movies-main", queued[0].RelatedEntityId);

        // A second pass while the first is still queued must not stack another
        // on top of it.
        await store.PlanLibrarySearchesAsync([library], CancellationToken.None);
        Assert.Single(await ScanJobsAsync(store));
    }

    [Fact]
    public async Task A_shelf_that_wants_no_subtitles_is_never_asked_to_read_its_files()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-27T01:00:00Z"));
        var store = await CreateStoreAsync(storage, timeProvider);
        var library = Library(wantsSubtitles: false);

        Assert.True(await store.RequestLibrarySearchAsync(library, CancellationToken.None));
        await store.PlanLibrarySearchesAsync([library], CancellationToken.None);

        // This is what keeps the feature free for everybody not using it. No
        // languages asked for, no job, no disk read, nothing in Activity.
        Assert.Empty(await ScanJobsAsync(store));
    }

    /// <summary>
    /// A manual search request against a library with both search kinds off is
    /// consumed, not retried forever.
    ///
    /// It was meant to be already, and was not: the code cleared a local flag
    /// and by doing so skipped the only branch that writes
    /// <c>search_requested = 0</c>. Both libraries on the lab rig had been
    /// sitting at "requested" for days, re-entering the cycle every thirty
    /// seconds and doing nothing — which is exactly why nobody noticed, until
    /// #301 gave the cycle something to do and it queued a subtitle scan on
    /// every one of those ticks.
    /// </summary>
    [Fact]
    public async Task A_manual_request_with_no_searches_enabled_is_consumed_rather_than_repeating_forever()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-27T01:00:00Z"));
        var store = await CreateStoreAsync(storage, timeProvider);
        var library = Library(wantsSubtitles: true) with
        {
            AutoSearchEnabled = false,
            MissingSearchEnabled = false,
            UpgradeSearchEnabled = false
        };

        Assert.True(await store.RequestLibrarySearchAsync(library, CancellationToken.None));
        await store.PlanLibrarySearchesAsync([library], CancellationToken.None);

        var states = await store.ListLibraryAutomationStatesAsync(CancellationToken.None);
        Assert.True(states.TryGetValue("movies-main", out var state));
        Assert.False(state!.SearchRequested);
    }

    /// <summary>
    /// A complete library with searching turned off still gets its subtitles.
    ///
    /// <para>This is the Bazarr user: nothing left to download, and English
    /// wanted on all of it. Subtitle work was planned inside the release-search
    /// branch, so "Search automatically" — which that screen calls <i>keep this
    /// library manual</i>, meaning manual <b>releases</b> — silently turned
    /// subtitles off too, and nothing said so.</para>
    ///
    /// <para>Found on the rig: the fetch that proved the whole feature works
    /// could not be made to happen without pressing "search now" first.</para>
    /// </summary>
    [Fact]
    public async Task A_shelf_with_searching_off_still_has_its_subtitles_fetched()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-27T01:00:00Z"));
        var store = await CreateStoreAsync(storage, timeProvider);
        var library = Library(wantsSubtitles: true) with
        {
            AutoSearchEnabled = false,
            MissingSearchEnabled = false,
            UpgradeSearchEnabled = false
        };

        await store.PlanLibrarySearchesAsync([library], CancellationToken.None);

        Assert.Single(await ScanJobsAsync(store));
        Assert.Single(await SearchJobsAsync(store));

        // And the library still reads as paused, because it is: paused is a
        // statement about searching for releases, which this one is not doing.
        var states = await store.ListLibraryAutomationStatesAsync(CancellationToken.None);
        Assert.Equal("paused", states["movies-main"].Status);
    }

    /// <summary>
    /// Searching on, but neither missing nor upgrade selected — the other half
    /// of the same defect, and the one that survives a library being resumed.
    /// </summary>
    [Fact]
    public async Task A_shelf_with_no_search_kinds_selected_still_has_its_subtitles_fetched()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-27T01:00:00Z"));
        var store = await CreateStoreAsync(storage, timeProvider);
        var library = Library(wantsSubtitles: true) with
        {
            MissingSearchEnabled = false,
            UpgradeSearchEnabled = false
        };

        await store.PlanLibrarySearchesAsync([library], CancellationToken.None);

        Assert.Single(await ScanJobsAsync(store));
        Assert.Single(await SearchJobsAsync(store));
    }

    /// <summary>
    /// The clock, which is the other half of decoupling it.
    ///
    /// <para>Once the jobs have run, the planner must not queue them again on
    /// its next tick. Deduping only covers a job that is still active; a library
    /// whose subtitle pass has completed is protected by its own cursor and by
    /// nothing else. Without one, a paused shelf asks its providers twice a
    /// minute, for ever.</para>
    /// </summary>
    [Fact]
    public async Task Subtitles_wait_out_the_interval_rather_than_re_queueing_on_every_tick()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-27T01:00:00Z"));
        var store = await CreateStoreAsync(storage, timeProvider);
        var library = Library(wantsSubtitles: true) with { AutoSearchEnabled = false };

        await store.PlanLibrarySearchesAsync([library], CancellationToken.None);
        Assert.Single(await ScanJobsAsync(store));

        // Finished, so nothing is deduping it any more and the cursor is the
        // only thing left holding it back.
        await FinishEverythingAsync(store);

        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await store.PlanLibrarySearchesAsync([library], CancellationToken.None);
        Assert.Single(await ScanJobsAsync(store));

        // Past subtitles' own cadence, it comes back round.
        timeProvider.Advance(TimeSpan.FromMinutes(6));
        await store.PlanLibrarySearchesAsync([library], CancellationToken.None);
        Assert.Equal(2, (await ScanJobsAsync(store)).Count);
    }

    /// <summary>
    /// The search window does not hold subtitles back.
    ///
    /// <para>It used to, and that was the first fix stopping half way. A window
    /// exists to keep you off an indexer at peak; subtitle providers are
    /// different servers with their own rate-limit backoff on the Connection.
    /// the owner: <i>"I dont agree that it shares a cycle or schedule and this was
    /// told to you back when I said nothing should be shared or have to wait for
    /// another process."</i></para>
    /// </summary>
    [Fact]
    public async Task Subtitles_do_not_wait_for_the_release_search_window()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-27T12:00:00Z"));
        var store = await CreateStoreAsync(storage, timeProvider);
        var library = Library(wantsSubtitles: true) with
        {
            AutoSearchEnabled = false,
            SearchWindowStartHour = 1,
            SearchWindowEndHour = 5
        };

        await store.PlanLibrarySearchesAsync([library], CancellationToken.None);

        Assert.Single(await ScanJobsAsync(store));
        Assert.Single(await SearchJobsAsync(store));
    }

    /// <summary>
    /// The cadence is subtitles' own, not the library's indexer interval.
    ///
    /// <para>The rig ran a twelve-hour interval, so a newly imported episode had
    /// half a day to wait before anything looked at it. That number is how often
    /// to go back to an indexer; a subtitle pass costs one query when there is
    /// nothing to do.</para>
    /// </summary>
    [Fact]
    public async Task Subtitles_keep_their_own_cadence_rather_than_the_search_interval()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-27T01:00:00Z"));
        var store = await CreateStoreAsync(storage, timeProvider);
        // Twelve hours between indexer visits, as the rig had it.
        var library = Library(wantsSubtitles: true) with { AutoSearchEnabled = false, SearchIntervalHours = 12 };

        await store.PlanLibrarySearchesAsync([library], CancellationToken.None);
        Assert.Single(await ScanJobsAsync(store));
        await FinishEverythingAsync(store);

        // Well inside subtitles' own cadence: still nothing new.
        timeProvider.Advance(TimeSpan.FromMinutes(2));
        await store.PlanLibrarySearchesAsync([library], CancellationToken.None);
        Assert.Single(await ScanJobsAsync(store));

        // Past it, and many hours short of the indexer interval.
        timeProvider.Advance(TimeSpan.FromMinutes(4));
        await store.PlanLibrarySearchesAsync([library], CancellationToken.None);
        Assert.Equal(2, (await ScanJobsAsync(store)).Count);
    }

    /// <summary>
    /// A file landing makes subtitles due at once. Bazarr fetches on import, and
    /// the moment a reader is actually watching is not the moment to be slower.
    /// </summary>
    [Theory]
    [InlineData("filesystem.import.execute")]
    [InlineData("library.import.existing")]
    public async Task An_import_stops_subtitles_waiting(string importJobType)
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-27T01:00:00Z"));
        var store = await CreateStoreAsync(storage, timeProvider);
        var library = Library(wantsSubtitles: true) with { AutoSearchEnabled = false };

        await store.PlanLibrarySearchesAsync([library], CancellationToken.None);
        Assert.Single(await ScanJobsAsync(store));
        await FinishEverythingAsync(store);

        // A minute on, the cadence has not come round, so nothing would happen.
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await store.PlanLibrarySearchesAsync([library], CancellationToken.None);
        Assert.Single(await ScanJobsAsync(store));

        // Then a file arrives.
        var import = await store.EnqueueAsync(
            new EnqueueJobRequest(
                JobType: importJobType,
                Source: "test",
                PayloadJson: "{}",
                RelatedEntityType: "library",
                RelatedEntityId: "movies-main"),
            CancellationToken.None);
        await store.CompleteAsync(import.Id, "test-worker", "imported", CancellationToken.None);

        await store.PlanLibrarySearchesAsync([library], CancellationToken.None);
        Assert.Equal(2, (await ScanJobsAsync(store)).Count);
    }

    /// <summary>
    /// A shelf that asked for no languages is not woken by an import either.
    ///
    /// <para>The import trigger is deliberately blunt about <i>which</i> library,
    /// because only one of the two import job types carries a library id and
    /// teaching the other to would have meant a second writer. This is the
    /// assertion that keeps bluntness from costing anything.</para>
    /// </summary>
    [Fact]
    public async Task An_import_does_not_wake_a_shelf_that_wants_no_subtitles()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-27T01:00:00Z"));
        var store = await CreateStoreAsync(storage, timeProvider);
        var library = Library(wantsSubtitles: false) with { AutoSearchEnabled = false };

        await store.PlanLibrarySearchesAsync([library], CancellationToken.None);
        var import = await store.EnqueueAsync(
            new EnqueueJobRequest(
                JobType: "filesystem.import.execute",
                Source: "test",
                PayloadJson: "{}",
                RelatedEntityType: "library",
                RelatedEntityId: "movies-main"),
            CancellationToken.None);
        await store.CompleteAsync(import.Id, "test-worker", "imported", CancellationToken.None);

        await store.PlanLibrarySearchesAsync([library], CancellationToken.None);
        Assert.Empty(await ScanJobsAsync(store));
    }

    private static LibraryAutomationPlanItem Library(bool wantsSubtitles)
        => new(
            LibraryId: "movies-main",
            LibraryName: "Movies",
            MediaType: "movies",
            AutoSearchEnabled: true,
            MissingSearchEnabled: true,
            UpgradeSearchEnabled: true,
            SearchIntervalHours: 6,
            RetryDelayHours: 24,
            MaxItemsPerRun: 25,
            SearchWindowStartHour: null,
            SearchWindowEndHour: null,
            WantsSubtitles: wantsSubtitles);

    private static async Task<IReadOnlyList<JobQueueItem>> ScanJobsAsync(SqliteJobStore store)
    {
        var jobs = await store.ListAsync(100, CancellationToken.None);
        return jobs.Where(job => job.JobType == "library.subtitles.scan").ToArray();
    }

    private static async Task<IReadOnlyList<JobQueueItem>> SearchJobsAsync(SqliteJobStore store)
    {
        var jobs = await store.ListAsync(100, CancellationToken.None);
        return jobs.Where(job => job.JobType == "library.subtitles.search").ToArray();
    }

    /// <summary>
    /// Finishes everything outstanding, so the next assertion is about what the
    /// planner decided rather than about what was still in flight to dedupe
    /// against. Completed jobs stay in the queue table, so the counts still add
    /// up across rounds.
    /// </summary>
    private static async Task FinishEverythingAsync(SqliteJobStore store)
    {
        foreach (var job in await store.ListAsync(100, CancellationToken.None))
        {
            await store.CompleteAsync(job.Id, "test-worker", "done", CancellationToken.None);
        }
    }

    private static async Task<SqliteJobStore> CreateStoreAsync(TestStorage storage, TimeProvider timeProvider)
    {
        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        return new SqliteJobStore(
            storage.Factory,
            timeProvider,
            new RecordingRealtimeEventPublisher(),
            new NullDownloadDispatchesRepository());
    }
}
