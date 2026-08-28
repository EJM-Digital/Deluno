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

        // Past the library's own interval, it comes back round.
        timeProvider.Advance(TimeSpan.FromHours(7));
        await store.PlanLibrarySearchesAsync([library], CancellationToken.None);
        Assert.Equal(2, (await ScanJobsAsync(store)).Count);
    }

    /// <summary>
    /// Out of hours, subtitles wait for the window exactly as a search does —
    /// which is the half of the old arrangement that was right, and had to
    /// survive being separated from the rest of it.
    /// </summary>
    [Fact]
    public async Task Subtitles_wait_for_the_search_window_like_everything_else()
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
        Assert.Empty(await ScanJobsAsync(store));

        // 01:00 the next morning, the window is open.
        timeProvider.Advance(TimeSpan.FromHours(13));
        await store.PlanLibrarySearchesAsync([library], CancellationToken.None);
        Assert.Single(await ScanJobsAsync(store));
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
