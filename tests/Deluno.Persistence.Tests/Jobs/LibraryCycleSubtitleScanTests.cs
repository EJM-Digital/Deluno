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
