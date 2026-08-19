using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Jobs;

/// <summary>
/// Covers the two pieces the metadata backfill rests on: counting the jobs
/// still to be worked, and selecting stale candidates in SQL rather than by
/// materialising the catalogue.
/// </summary>
public sealed class MetadataBackfillPersistenceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-19T00:00:00Z");

    [Fact]
    public async Task CountActiveJobsAsync_counts_work_still_to_be_done_and_ignores_exhausted_and_completed()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(Now);
        await InitializeJobsAsync(storage, timeProvider);
        var store = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());

        var queued = await EnqueueAsync(store, "movies.metadata.refresh", "movie-1");
        await EnqueueAsync(store, "movies.metadata.refresh", "movie-2");
        var otherType = await EnqueueAsync(store, "series.metadata.refresh", "series-1");

        Assert.Equal(2, await store.CountActiveJobsAsync("movies.metadata.refresh", CancellationToken.None));
        Assert.Equal(1, await store.CountActiveJobsAsync("series.metadata.refresh", CancellationToken.None));

        // A completed job is no longer work to be done.
        await store.CompleteAsync(queued.Id, "worker-a", "done", CancellationToken.None);
        Assert.Equal(1, await store.CountActiveJobsAsync("movies.metadata.refresh", CancellationToken.None));

        // A job that has exhausted its attempts must not be counted, or the
        // depth would stay permanently inflated and stall every future top-up.
        await ExhaustAttemptsAsync(storage, otherType.Id);
        Assert.Equal(0, await store.CountActiveJobsAsync("series.metadata.refresh", CancellationToken.None));
    }

    [Fact]
    public async Task ListStaleMetadataCandidatesAsync_returns_never_matched_first_then_oldest_and_respects_take()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(Now);
        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);

        var fresh = await repository.AddAsync(new CreateMovieRequest("Fresh", 2024, null), CancellationToken.None);
        var stale = await repository.AddAsync(new CreateMovieRequest("Stale", 2001, null), CancellationToken.None);
        var neverMatched = await repository.AddAsync(new CreateMovieRequest("Never Matched", 1999, null), CancellationToken.None);

        // Fresh: matched today. Stale: matched long ago. NeverMatched: left as-is.
        await SetMetadataStateAsync(storage, fresh.Id, providerId: "tmdb-1", updatedUtc: Now);
        await SetMetadataStateAsync(storage, stale.Id, providerId: "tmdb-2", updatedUtc: Now.AddDays(-90));

        var staleBefore = Now.AddDays(-14);
        var neverAttempted = Now;
        var candidates = await repository.ListStaleMetadataCandidatesAsync(staleBefore, neverAttempted, take: 10, CancellationToken.None);

        // The freshly-matched one is not a candidate at all.
        Assert.DoesNotContain(candidates, c => c.Id == fresh.Id);
        Assert.Equal(2, candidates.Count);

        // Never-matched sorts ahead of merely-stale: a title with no metadata at
        // all is what a user notices first after an import.
        Assert.Equal(neverMatched.Id, candidates[0].Id);
        Assert.Equal(stale.Id, candidates[1].Id);

        // The payload fields the job needs come back populated.
        Assert.Equal("Never Matched", candidates[0].Title);
        Assert.Equal(1999, candidates[0].Year);

        // take is honoured in SQL.
        var limited = await repository.ListStaleMetadataCandidatesAsync(staleBefore, neverAttempted, take: 1, CancellationToken.None);
        Assert.Single(limited);
        Assert.Equal(neverMatched.Id, limited[0].Id);
    }

    /// <summary>
    /// The regression this whole change exists for: a backlog far larger than
    /// the old fixed 30-per-pass allocation must be selectable in one go, so
    /// the backfill is bounded by queue depth rather than by a constant that
    /// turned 20,000 items into ~167 days.
    /// </summary>
    [Fact]
    public async Task ListStaleMetadataCandidatesAsync_can_return_far_more_than_the_old_thirty_per_pass_cap()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(Now);
        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);

        for (var i = 0; i < 250; i++)
        {
            await repository.AddAsync(new CreateMovieRequest($"Imported {i:D4}", 2000 + (i % 20), null), CancellationToken.None);
        }

        var candidates = await repository.ListStaleMetadataCandidatesAsync(Now.AddDays(-14), Now, take: 200, CancellationToken.None);

        Assert.Equal(200, candidates.Count);
        Assert.True(candidates.Count > 30, "A backlog must not be capped at the old 30-per-pass allocation.");
    }

    /// <summary>
    /// An entry the provider cannot match never gets a success timestamp, so it
    /// stays stale forever. Before the attempt cooldown that meant it was
    /// re-selected by every single top-up — at a 1-minute interval and 20,000
    /// items, a permanent hot loop of pointless provider lookups. Recording the
    /// attempt has to take it out of the running even though nothing matched.
    /// </summary>
    [Fact]
    public async Task An_entry_that_cannot_be_matched_is_not_reselected_until_the_cooldown_lapses()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(Now);
        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);

        var unmatchable = await repository.AddAsync(
            new CreateMovieRequest("Totally Obscure Home Video", 1991, null),
            CancellationToken.None);

        var staleBefore = Now.AddDays(-14);

        // Selected on the first pass, as expected.
        var first = await repository.ListStaleMetadataCandidatesAsync(
            staleBefore, retryAttemptsBefore: Now, take: 50, CancellationToken.None);
        Assert.Contains(first, c => c.Id == unmatchable.Id);

        // The refresh runs and finds no match, so metadata_updated_utc is never
        // written — only the attempt is recorded.
        await repository.RecordMetadataAttemptAsync(unmatchable.Id, CancellationToken.None);

        // Still stale, but must no longer be selected inside the cooldown.
        var withinCooldown = await repository.ListStaleMetadataCandidatesAsync(
            staleBefore, retryAttemptsBefore: Now.AddHours(-24), take: 50, CancellationToken.None);
        Assert.DoesNotContain(withinCooldown, c => c.Id == unmatchable.Id);

        // Once the cooldown lapses it is eligible again, so a title that becomes
        // matchable later is not abandoned permanently.
        var afterCooldown = await repository.ListStaleMetadataCandidatesAsync(
            staleBefore, retryAttemptsBefore: Now.AddHours(25), take: 50, CancellationToken.None);
        Assert.Contains(afterCooldown, c => c.Id == unmatchable.Id);
    }


    /// <summary>
    /// "Refresh everything" is one statement, not a page of the catalogue. It
    /// used to load every row, take the first few hundred, and queue a job each
    /// — which on a 20,000-item library covered a couple of percent and said
    /// nothing about the rest.
    /// </summary>
    [Fact]
    public async Task Requesting_a_refresh_for_everything_makes_even_freshly_matched_entries_stale_again()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(Now);
        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);

        var fresh = await repository.AddAsync(new CreateMovieRequest("Fresh", 2024, null), CancellationToken.None);
        await repository.AddAsync(new CreateMovieRequest("Also Fresh", 2023, null), CancellationToken.None);
        // Refreshed a day ago: recent enough not to be stale, and before the
        // refresh request that follows.
        await SetMetadataStateAsync(storage, fresh.Id, providerId: "tmdb-1", updatedUtc: Now.AddDays(-1));

        var staleBefore = Now.AddDays(-14);
        var retryAttemptsBefore = Now.AddHours(-24);

        Assert.DoesNotContain(
            await repository.ListStaleMetadataCandidatesAsync(staleBefore, retryAttemptsBefore, 50, CancellationToken.None),
            candidate => candidate.Id == fresh.Id);

        Assert.Equal(2, await repository.RequestMetadataRefreshForAllAsync(CancellationToken.None));

        var candidates = await repository.ListStaleMetadataCandidatesAsync(
            staleBefore, retryAttemptsBefore, 50, CancellationToken.None);
        Assert.Contains(candidates, candidate => candidate.Id == fresh.Id);

        // The count has to agree with the list, or the endpoint reports a
        // "still to go" figure the planner will never act on.
        Assert.Equal(
            candidates.Count,
            await repository.CountStaleMetadataCandidatesAsync(staleBefore, retryAttemptsBefore, CancellationToken.None));

        // What "forced" must not do: destroy the record of when the entry was
        // genuinely last refreshed.
        var refreshed = Assert.Single(
            await repository.ListAsync(CancellationToken.None),
            item => item.Id == fresh.Id);
        Assert.Equal(Now.AddDays(-1), refreshed.MetadataUpdatedUtc);
    }

    /// <summary>
    /// The unhappy path for a forced refresh. A title the provider cannot match
    /// never gets a success timestamp, so the refresh request alone would keep
    /// selecting it on every pass — the same hot loop the attempt cooldown was
    /// introduced to stop, reintroduced by the force flag. One attempt after the
    /// request has to be enough to take it out of the running.
    /// </summary>
    [Fact]
    public async Task A_forced_refresh_of_an_unmatchable_entry_stops_after_one_attempt()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(Now);
        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);

        var unmatchable = await repository.AddAsync(
            new CreateMovieRequest("Totally Obscure Home Video", 1991, null),
            CancellationToken.None);

        await repository.RequestMetadataRefreshForAllAsync(CancellationToken.None);

        var staleBefore = Now.AddDays(-14);
        var retryAttemptsBefore = Now.AddHours(-24);

        Assert.Contains(
            await repository.ListStaleMetadataCandidatesAsync(staleBefore, retryAttemptsBefore, 50, CancellationToken.None),
            candidate => candidate.Id == unmatchable.Id);

        // The refresh runs and matches nothing, so only the attempt is recorded.
        await repository.RecordMetadataAttemptAsync(unmatchable.Id, CancellationToken.None);

        Assert.DoesNotContain(
            await repository.ListStaleMetadataCandidatesAsync(staleBefore, retryAttemptsBefore, 50, CancellationToken.None),
            candidate => candidate.Id == unmatchable.Id);
        Assert.Equal(
            0,
            await repository.CountStaleMetadataCandidatesAsync(staleBefore, retryAttemptsBefore, CancellationToken.None));

        // And it is still not abandoned forever: once the ordinary cooldown
        // lapses it comes back round.
        Assert.Contains(
            await repository.ListStaleMetadataCandidatesAsync(staleBefore, Now.AddHours(25), 50, CancellationToken.None),
            candidate => candidate.Id == unmatchable.Id);
    }

    /// <summary>
    /// A requested refresh jumps the queue: the user pressed a button, and the
    /// entries they asked about should not sit behind a routine 14-day sweep.
    /// </summary>
    [Fact]
    public async Task Requested_refreshes_are_selected_before_merely_stale_entries()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(Now);
        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);

        var requested = await repository.AddAsync(new CreateMovieRequest("Requested", 2024, null), CancellationToken.None);
        await SetMetadataStateAsync(storage, requested.Id, providerId: "tmdb-1", updatedUtc: Now.AddDays(-1));
        await repository.RequestMetadataRefreshForAllAsync(CancellationToken.None);

        // Added after the request, so it is merely stale rather than requested.
        await repository.AddAsync(new CreateMovieRequest("Never Matched", 1999, null), CancellationToken.None);

        var candidates = await repository.ListStaleMetadataCandidatesAsync(
            Now.AddDays(-14), Now.AddHours(-24), 50, CancellationToken.None);

        Assert.Equal(2, candidates.Count);
        Assert.Equal(requested.Id, candidates[0].Id);
    }

    private static async Task<JobQueueItem> EnqueueAsync(SqliteJobStore store, string jobType, string relatedEntityId)
        => await store.EnqueueAsync(
            new EnqueueJobRequest(
                JobType: jobType,
                Source: "metadata",
                PayloadJson: "{}",
                RelatedEntityType: "movie",
                RelatedEntityId: relatedEntityId),
            CancellationToken.None);

    private static async Task ExhaustAttemptsAsync(TestStorage storage, string jobId)
    {
        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs, CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE job_queue SET status = 'failed', attempts = max_attempts WHERE id = @id;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = jobId;
        command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SetMetadataStateAsync(TestStorage storage, string movieId, string providerId, DateTimeOffset updatedUtc)
    {
        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Movies, CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE movie_entries
            SET metadata_provider_id = @providerId, metadata_updated_utc = @updatedUtc
            WHERE id = @id;
            """;
        foreach (var (name, value) in new (string, object)[]
                 {
                     ("@providerId", providerId),
                     ("@updatedUtc", updatedUtc.ToString("O")),
                     ("@id", movieId)
                 })
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task InitializeJobsAsync(TestStorage storage, TimeProvider timeProvider)
        => await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
}
