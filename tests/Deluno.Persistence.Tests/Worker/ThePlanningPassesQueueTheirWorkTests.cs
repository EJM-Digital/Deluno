using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Data;
using Deluno.Worker.Intake;
using Deluno.Worker.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Worker;

/// <summary>
/// The scheduled planning passes that queue work for a lane rather than doing it: the metadata top-up, the media-probe
/// slices and the list check. Each is run by the real <see cref="WorkPlanner"/> against the real job store (a real SQLite
/// file) and a clock the test moves, and each leaves its result where the System screen reads it.
/// </summary>
public sealed class ThePlanningPassesQueueTheirWorkTests
{
    [Fact]
    public async Task The_metadata_top_up_queues_a_refresh_for_each_film_and_show_with_stale_details_once_and_records_the_pass()
    {
        var (app, planner, clock) = await StartAsync();
        await using var _ = app;
        var film = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021);
        var show = await ApiScenes.ShowOnDiskAsync(app, "The Wire", 2002);
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();

        await RunAsync(app, planner, clock);

        var queued = await jobs.ListAsync(100, CancellationToken.None);
        var filmJob = Assert.Single(queued, job => job.JobType == "movies.metadata.refresh");
        Assert.Equal(film.MovieId, filmJob.RelatedEntityId);
        Assert.Equal("movie", filmJob.RelatedEntityType);
        var showJob = Assert.Single(queued, job => job.JobType == "series.metadata.refresh");
        Assert.Equal(show.SeriesId, showJob.RelatedEntityId);
        Assert.Equal("series", showJob.RelatedEntityType);
        var state = await StateAsync(jobs, SystemTasks.MetadataRefresh);
        Assert.Equal("completed", state.LastResult);
        Assert.Equal(clock.GetUtcNow() + SystemTasks.IntervalFor(SystemTasks.MetadataRefresh), state.NextRunUtc);

        // Inside its minute the pass is not claimed again; once it is due, the title already queued is not queued twice.
        await RunAsync(app, planner, clock);
        clock.Advance(TimeSpan.FromMinutes(2));
        await RunAsync(app, planner, clock);
        Assert.Single(await jobs.ListAsync(100, CancellationToken.None), job => job.JobType == "movies.metadata.refresh");
        Assert.Single(await jobs.ListAsync(100, CancellationToken.None), job => job.JobType == "series.metadata.refresh");
    }

    [Fact]
    public async Task The_media_probe_pass_queues_one_slice_for_movies_and_one_for_shows_and_never_stacks_a_second_chain()
    {
        var (app, planner, clock) = await StartAsync();
        await using var _ = app;
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var scheduler = app.Services.GetRequiredService<IJobScheduler>();

        await planner.PlanMediaProbeAsync(scheduler, CancellationToken.None);

        var probes = (await jobs.ListAsync(100, CancellationToken.None)).Where(job => job.JobType == "library.media.probe").ToList();
        Assert.Equal(["media-probe:movie", "media-probe:series"], probes.Select(job => job.DedupeKey!).Order().ToArray());
        Assert.Equal(["movie", "series"], probes.Select(job => job.RelatedEntityType!).Order().ToArray());
        var state = await StateAsync(jobs, SystemTasks.MediaProbe);
        Assert.Equal("completed", state.LastResult);
        Assert.Equal(clock.GetUtcNow() + TimeSpan.FromMinutes(30), state.NextRunUtc);

        // Not due again inside half an hour; due again after it, but the slice still waiting is not doubled.
        await planner.PlanMediaProbeAsync(scheduler, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(31));
        await planner.PlanMediaProbeAsync(scheduler, CancellationToken.None);
        Assert.Equal(2, (await jobs.ListAsync(100, CancellationToken.None)).Count(job => job.JobType == "library.media.probe"));
    }

    [Fact]
    public async Task The_list_check_asks_for_the_due_syncs_once_per_interval_and_a_failure_is_recorded_in_plain_words_then_tried_again()
    {
        var (app, planner, clock) = await StartAsync();
        await using var _ = app;
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var intake = new CountingIntake();

        await planner.PlanIntakeAutomationAsync(intake, CancellationToken.None);
        await planner.PlanIntakeAutomationAsync(intake, CancellationToken.None);

        Assert.Equal(1, intake.Calls);
        var state = await StateAsync(jobs, SystemTasks.IntakeAutomation);
        Assert.Equal("completed", state.LastResult);
        Assert.Equal(clock.GetUtcNow() + TimeSpan.FromMinutes(5), state.NextRunUtc);

        clock.Advance(TimeSpan.FromMinutes(6));
        intake.Throw = new InvalidOperationException("the lists could not be read");
        await planner.PlanIntakeAutomationAsync(intake, CancellationToken.None);
        var failed = await StateAsync(jobs, SystemTasks.IntakeAutomation);
        Assert.Equal("failed", failed.LastResult);
        Assert.False(string.IsNullOrWhiteSpace(failed.LastError));

        clock.Advance(TimeSpan.FromMinutes(6));
        intake.Throw = null;
        await planner.PlanIntakeAutomationAsync(intake, CancellationToken.None);
        Assert.Equal(3, intake.Calls);
        Assert.Equal("completed", (await StateAsync(jobs, SystemTasks.IntakeAutomation)).LastResult);
    }

    private static Task RunAsync(ApplicationTestHost app, WorkPlanner planner, FixedTimeProvider clock)
        => planner.PlanMetadataRefreshAutomationAsync(
            app.Services.GetRequiredService<IJobScheduler>(),
            app.Services.GetRequiredService<IMovieCatalogRepository>(),
            app.Services.GetRequiredService<ISeriesCatalogRepository>(),
            clock,
            CancellationToken.None);

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

    private static async Task<SystemTaskState> StateAsync(IJobQueueRepository jobs, string key)
        => (await jobs.ListSystemTaskStatesAsync(CancellationToken.None)).Single(state => state.ScheduleKey == key);

    private sealed class CountingIntake : IIntakeSyncService
    {
        public int Calls { get; private set; }

        public Exception? Throw { get; set; }

        public Task<int> PlanDueSyncJobsAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Throw is null ? Task.FromResult(0) : throw Throw;
        }

        public Task<IntakeSyncRunResult> RunAsync(string sourceId, string? relatedJobId, bool manual, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
