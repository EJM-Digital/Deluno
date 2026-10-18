using System.Reflection;
using Deluno.Contracts;
using Deluno.Integrations.Metadata;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Movies.Services;
using Deluno.Persistence.Tests.Support;
using Deluno.Worker.Jobs;
using Deluno.Worker.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Worker;

/// <summary>
/// Keeping a film collection's membership fresh: the planner queues one refresh for each monitored collection that is due
/// (on the collections lane, apart from the movie library searches), claims it so it is not queued again inside a day, and
/// the handler refreshes the collection the job names and says what it did.
/// </summary>
public sealed class TheMovieCollectionRefreshTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-18T06:00:00Z");

    [Fact]
    public async Task A_monitored_collection_that_is_due_is_queued_for_a_refresh_once_and_not_again_inside_a_day()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        var migrator = new Deluno.Infrastructure.Storage.Migrations.SqliteDatabaseMigrator(storage.Factory, clock);
        await new MoviesSchemaInitializer(storage.Factory, migrator, NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var collections = new SqliteMovieCollectionsRepository(storage.Factory, clock);
        var jobs = new SqliteJobStore(storage.Factory, clock, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var metadata = new MetadataCollection(
            "tmdb", "645", "The Nolan Collection", "A franchise.", null, null,
            [new MetadataCollectionMovie("101", "Held film", 2010, "Held", null, null, "https://example.invalid/movie/101")]);
        var monitored = await collections.UpsertAsync(
            "movies-main", "Movies", @"D:\Movies", "quality-1", "HD", new CreateMovieCollectionRequest("645", "movies-main", Monitored: true), metadata, CancellationToken.None);
        var planner = new WorkPlanner(NullLogger<WorkPlanner>.Instance, jobs, new ConfigurationBuilder().Build(), clock);

        await planner.PlanMovieCollectionSyncAsync(jobs, collections, clock, CancellationToken.None);
        await planner.PlanMovieCollectionSyncAsync(jobs, collections, clock, CancellationToken.None);

        var queued = Assert.Single(await jobs.ListAsync(50, CancellationToken.None), job => job.JobType == MovieCollectionJobTypes.Sync);
        Assert.Equal(monitored.Id, queued.RelatedEntityId);
        Assert.Equal("movie-collection", queued.RelatedEntityType);
        Assert.Equal($"movie-collection.sync:{monitored.Id}", queued.DedupeKey);
        Assert.Contains(monitored.Id, queued.PayloadJson);

        // The planner claimed it, so nothing is due now; a day on, it is due again.
        Assert.Empty(await collections.ClaimDueAsync(clock.GetUtcNow(), TimeSpan.FromHours(24), CancellationToken.None));
        clock.Advance(TimeSpan.FromHours(25));
        Assert.Equal(monitored.Id, Assert.Single(await collections.ClaimDueAsync(clock.GetUtcNow(), TimeSpan.FromHours(24), CancellationToken.None)).Id);
    }

    [Fact]
    public async Task The_handler_refreshes_the_collection_the_job_names_and_returns_what_the_refresh_said()
    {
        var service = Service.Create("The Nolan Collection has 3 films; 1 added.");
        var handler = new MovieCollectionSyncJobHandler(service.Proxy);

        var message = await handler.HandleAsync(Job(relatedEntityId: "collection-1"), CancellationToken.None);

        Assert.Equal("The Nolan Collection has 3 films; 1 added.", message);
        Assert.Equal(["collection-1"], service.Synced);
    }

    [Fact]
    public async Task A_job_that_names_no_collection_is_skipped_in_plain_words_and_refreshes_nothing()
    {
        var service = Service.Create("never used");
        var handler = new MovieCollectionSyncJobHandler(service.Proxy);

        var message = await handler.HandleAsync(Job(relatedEntityId: null), CancellationToken.None);

        Assert.Equal("Movie collection refresh skipped because no collection was linked.", message);
        Assert.Empty(service.Synced);
    }

    [Fact]
    public void The_collections_lane_runs_the_refresh_jobs_and_nothing_a_movie_library_search_waits_behind()
    {
        var lane = Assert.Single(JobLanes.All, item => item.JobTypes.Contains(MovieCollectionJobTypes.Sync));

        Assert.Equal("collections.movies", lane.Name);
        Assert.Equal([MovieCollectionJobTypes.Sync], lane.JobTypes);
        Assert.Equal(MovieCollectionJobTypes.Sync, new MovieCollectionSyncJobHandler(Service.Create("").Proxy).JobType);
    }

    private static JobQueueItem Job(string? relatedEntityId)
        => new(
            Id: "job-1", JobType: MovieCollectionJobTypes.Sync, Source: "collections", Status: "processing", PayloadJson: null, Attempts: 1,
            CreatedUtc: Start, ScheduledUtc: Start, StartedUtc: Start, CompletedUtc: null, LeasedUntilUtc: null, WorkerId: null, LastError: null,
            RelatedEntityType: "movie-collection", RelatedEntityId: relatedEntityId, IdempotencyKey: null, DedupeKey: null, MaxAttempts: 3,
            LastAttemptUtc: null, NextAttemptUtc: null);

    private class Service : DispatchProxy
    {
        private string _message = "";

        public List<string> Synced { get; } = [];

        public IMovieCollectionService Proxy => (IMovieCollectionService)(object)this;

        public static Service Create(string message)
        {
            var service = (Service)(object)DispatchProxy.Create<IMovieCollectionService, Service>();
            service._message = message;
            return service;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IMovieCollectionService.SyncAsync))
            {
                throw new NotSupportedException(targetMethod?.Name);
            }

            var id = (string)args![0]!;
            Synced.Add(id);
            return Task.FromResult(new MovieCollectionSyncResult(id, 3, 1, 0, 0, false, _message));
        }
    }
}
