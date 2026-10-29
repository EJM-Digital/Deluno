using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Worker.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Worker;

/// <summary>
/// The metadata backfill planner (every minute) on the real host: how far it tops each queue up, which titles it picks, and the
/// selection of stale shows. The real <see cref="WorkPlanner"/> over the real job store and the real catalogue stores in database
/// files, with a clock the test moves. No other app is asked: the planner only queues work for the lane.
/// </summary>
public sealed class TheMetadataBackfillTopUpTests
{
    private static async Task<(ApplicationTestHost App, WorkPlanner Planner, FixedTimeProvider Clock)> StartAsync()
    {
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<TimeProvider>(clock));
        var planner = new WorkPlanner(
            NullLogger<WorkPlanner>.Instance,
            app.Services.GetRequiredService<IJobQueueRepository>(),
            new ConfigurationBuilder().Build(),
            clock);
        return (app, planner, clock);
    }

    private static Task PlanAsync(ApplicationTestHost app, WorkPlanner planner, FixedTimeProvider clock)
        => planner.PlanMetadataRefreshAutomationAsync(
            app.Services.GetRequiredService<IJobScheduler>(),
            app.Services.GetRequiredService<IMovieCatalogRepository>(),
            app.Services.GetRequiredService<ISeriesCatalogRepository>(),
            clock,
            CancellationToken.None);

    private static async Task<int> ActiveAsync(ApplicationTestHost app, string jobType)
        => await app.Services.GetRequiredService<IJobQueueRepository>().CountActiveJobsAsync(jobType, CancellationToken.None);

    [Fact]
    public async Task The_top_up_fills_a_queue_to_200_active_jobs_from_stale_titles_leaves_a_full_queue_alone_and_skips_titles_tried_within_a_day()
    {
        var (app, planner, clock) = await StartAsync();
        await using var _ = app;
        var films = app.Services.GetRequiredService<IMovieCatalogRepository>();
        var scheduler = app.Services.GetRequiredService<IJobScheduler>();
        var stale = new List<string>();
        for (var i = 0; i < 100; i++)
        {
            stale.Add((await films.AddAsync(new CreateMovieRequest($"Imported {i:D3}", 2000 + (i % 20), null), CancellationToken.None)).Id);
        }

        // Tried a moment ago and unmatched: left alone for a day.
        var tried = (await films.AddAsync(new CreateMovieRequest("Totally Obscure Home Video", 1991, null), CancellationToken.None)).Id;
        await films.RecordMetadataAttemptAsync(tried, CancellationToken.None);

        for (var i = 0; i < 180; i++)
        {
            await scheduler.EnqueueAsync(new EnqueueJobRequest("movies.metadata.refresh", "metadata", "{}", "movie", $"already-queued-{i}"), CancellationToken.None);
        }

        await PlanAsync(app, planner, clock);

        Assert.Equal(200, await ActiveAsync(app, "movies.metadata.refresh"));
        var queuedNow = (await app.Services.GetRequiredService<IJobQueueRepository>().ListAsync(500, CancellationToken.None))
            .Where(job => job.JobType == "movies.metadata.refresh" && job.RelatedEntityId is not null && !job.RelatedEntityId.StartsWith("already-queued-", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(20, queuedNow.Count);
        Assert.All(queuedNow, job => Assert.Contains(job.RelatedEntityId, stale));
        Assert.DoesNotContain(queuedNow, job => job.RelatedEntityId == tried);

        // A full queue is left alone, and what is already queued is not queued twice.
        clock.Advance(TimeSpan.FromMinutes(2));
        await PlanAsync(app, planner, clock);
        Assert.Equal(200, await ActiveAsync(app, "movies.metadata.refresh"));
    }

    [Fact]
    public async Task A_settled_library_queues_no_refresh_at_all()
    {
        var (app, planner, clock) = await StartAsync();
        await using var _ = app;
        var films = app.Services.GetRequiredService<IMovieCatalogRepository>();
        for (var i = 0; i < 5; i++)
        {
            await films.AddAsync(new CreateMovieRequest($"Settled {i}", 2010 + i, null), CancellationToken.None);
        }

        var now = clock.GetUtcNow().ToString("O");
        await ApiScenes.ExecuteAsync(app, "movies", $"UPDATE movie_entries SET metadata_provider_id = 'tmdb-1', metadata_updated_utc = '{now}';");

        await PlanAsync(app, planner, clock);

        Assert.Equal(0, await ActiveAsync(app, "movies.metadata.refresh"));
        Assert.Equal(0, await ActiveAsync(app, "series.metadata.refresh"));
    }

    [Fact]
    public async Task For_shows_never_matched_come_first_then_the_oldest_a_show_tried_within_a_day_waits_and_a_forced_refresh_makes_every_show_stale()
    {
        var (app, _, clock) = await StartAsync();
        await using var _ = app;
        var shows = app.Services.GetRequiredService<ISeriesCatalogRepository>();
        var now = clock.GetUtcNow();
        var fresh = await shows.AddAsync(new CreateSeriesRequest("Fresh Show", 2024, null), CancellationToken.None);
        var stale = await shows.AddAsync(new CreateSeriesRequest("Stale Show", 2001, null), CancellationToken.None);
        var never = await shows.AddAsync(new CreateSeriesRequest("Never Matched Show", 1999, null), CancellationToken.None);
        await ApiScenes.ExecuteAsync(app, "series", $"UPDATE series_entries SET metadata_provider_id = 'tmdb-1', metadata_updated_utc = '{now.AddDays(-1):O}' WHERE id = '{fresh.Id}';");
        await ApiScenes.ExecuteAsync(app, "series", $"UPDATE series_entries SET metadata_provider_id = 'tmdb-2', metadata_updated_utc = '{now.AddDays(-90):O}' WHERE id = '{stale.Id}';");
        var staleBefore = now.AddDays(-14);
        var cooldown = now.AddHours(-24);

        var candidates = await shows.ListStaleMetadataCandidatesAsync(staleBefore, cooldown, 10, CancellationToken.None);

        Assert.Equal([never.Id, stale.Id], candidates.Select(candidate => candidate.Id).ToArray());
        Assert.Equal(["Never Matched Show", "Stale Show"], candidates.Select(candidate => candidate.Title).ToArray());
        Assert.Equal(1999, candidates[0].Year);
        Assert.Equal(never.Id, Assert.Single(await shows.ListStaleMetadataCandidatesAsync(staleBefore, cooldown, 1, CancellationToken.None)).Id);

        // Tried and unmatched: waits out the day, then comes round again.
        await shows.RecordMetadataAttemptAsync(never.Id, CancellationToken.None);
        Assert.DoesNotContain(await shows.ListStaleMetadataCandidatesAsync(staleBefore, cooldown, 10, CancellationToken.None), candidate => candidate.Id == never.Id);
        Assert.Contains(await shows.ListStaleMetadataCandidatesAsync(staleBefore, now.AddHours(25), 10, CancellationToken.None), candidate => candidate.Id == never.Id);

        // Refresh everything: even the show matched today is stale again, and the count agrees with the list.
        Assert.Equal(3, await shows.RequestMetadataRefreshForAllAsync(CancellationToken.None));
        var forced = await shows.ListStaleMetadataCandidatesAsync(staleBefore, cooldown, 10, CancellationToken.None);
        Assert.Contains(forced, candidate => candidate.Id == fresh.Id);
        Assert.Equal(forced.Count, await shows.CountStaleMetadataCandidatesAsync(staleBefore, cooldown, CancellationToken.None));
    }
}
