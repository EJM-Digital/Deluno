using System.Collections.Concurrent;
using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The server-side pushes of the engines area, at the moments the register names them: a queue row is announced when its job is
/// added, removed or changes status; a download-health record that is written, ignored or forgotten announces DownloadHealthChanged
/// with the client it concerns; and a search that ends with no release asks for one AttentionChanged "noRelease" for movies and for
/// series. Each runs the real store on a real database file with a publisher that records what it was told.
/// </summary>
public sealed class EnginesRealtimeQueueHealthAndAttentionRowsTests
{
    /// <summary>Remembers the queue pushes, in order, beside everything the recording publisher keeps.</summary>
    private sealed class Spy : NullRealtimeEventPublisher, IRealtimeEventPublisher
    {
        private readonly ConcurrentQueue<string> queue = new();
        private readonly ConcurrentQueue<(string Name, object Payload)> dashboard = new();

        public IReadOnlyList<string> Queue => queue.ToArray();

        public IReadOnlyList<(string Name, object Payload)> Dashboard => dashboard.ToArray();

        public new Task PublishQueueItemAddedAsync(string id, string title, string type, string status, CancellationToken cancellationToken)
        {
            queue.Enqueue($"added:{id}:{status}");
            return Task.CompletedTask;
        }

        public new Task PublishQueueItemRemovedAsync(string id, CancellationToken cancellationToken)
        {
            queue.Enqueue($"removed:{id}");
            return Task.CompletedTask;
        }

        public new Task PublishQueueItemStatusChangedAsync(string id, string status, string? errorMessage, CancellationToken cancellationToken)
        {
            queue.Enqueue($"status:{id}:{status}");
            return Task.CompletedTask;
        }

        public Task PublishDashboardEventAsync(string eventName, object payload, CancellationToken cancellationToken)
        {
            dashboard.Enqueue((eventName, payload));
            return Task.CompletedTask;
        }

        public Task PublishLiveEventAsync(string eventName, object payload, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static async Task<(ApplicationTestHost App, Spy Spy)> StartAsync()
    {
        var spy = new Spy();
        var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
            services.Replace(ServiceDescriptor.Singleton<IRealtimeEventPublisher>(spy)));
        return (app, spy);
    }

    [Fact]
    public async Task A_job_that_is_added_completes_or_fails_is_announced_on_the_queue_each_time()
    {
        var (app, spy) = await StartAsync();
        await using var _ = app;
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var scheduler = app.Services.GetRequiredService<IJobScheduler>();

        var finishing = await scheduler.EnqueueAsync(new EnqueueJobRequest("filesystem.import.execute", "test", "{\"n\":1}", null, null), CancellationToken.None);
        Assert.Contains($"added:{finishing.Id}:queued", spy.Queue);

        var leased = await jobs.LeaseBatchAsync("worker-test", TimeSpan.FromMinutes(2), ["filesystem.import.execute"], 5, CancellationToken.None);
        Assert.Contains(leased, job => job.Id == finishing.Id);
        await jobs.CompleteAsync(finishing.Id, "worker-test", "Done.", CancellationToken.None);
        Assert.Contains($"removed:{finishing.Id}", spy.Queue);

        var failing = await scheduler.EnqueueAsync(new EnqueueJobRequest("filesystem.import.execute", "test", "{\"n\":2}", null, null), CancellationToken.None);
        await jobs.LeaseBatchAsync("worker-test", TimeSpan.FromMinutes(2), ["filesystem.import.execute"], 5, CancellationToken.None);
        await jobs.FailAsync(failing.Id, "worker-test", "The import broke.", CancellationToken.None);

        Assert.Contains($"removed:{failing.Id}", spy.Queue);
        Assert.Contains(spy.Queue, entry => entry.StartsWith($"status:{failing.Id}:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_download_health_record_that_is_written_ignored_or_forgotten_announces_the_client_it_concerns()
    {
        var (app, spy) = await StartAsync();
        await using var _ = app;
        var health = app.Services.GetRequiredService<IDownloadHealthRepository>();
        IReadOnlyList<(string Name, object Payload)> Changed() => spy.Dashboard.Where(item => item.Name == LiveEventNames.DownloadHealthChanged).ToArray();

        await health.RecordDownloadHealthObservationsAsync(
            "client-1", ["item-1"],
            [new DownloadHealthObservation("client-1", "item-1", "Stuck.Film.2020.1080p-DELUNO", DownloadHealthStrikes.ClientStalled, "warning", "No throughput for 30 minutes.")],
            DownloadHealthStrikes.DefaultWindow, TimeSpan.FromHours(72), CancellationToken.None);
        var afterWrite = Changed();
        Assert.Equal("client-1", ((DownloadHealthChangedEvent)Assert.Single(afterWrite).Payload).ClientId);

        await health.IgnoreDownloadHealthFindingAsync("client-1", "item-1", DownloadHealthStrikes.ClientStalled, 7, CancellationToken.None);
        Assert.True(Changed().Count > afterWrite.Count, "ignoring a finding pushed nothing");

        var beforeForget = Changed().Count;
        Assert.True(await health.ForgetDownloadClientAsync("client-1", CancellationToken.None) > 0);
        Assert.True(Changed().Count > beforeForget, "forgetting a client pushed nothing");
    }

    [Fact]
    public async Task A_search_that_ends_with_no_release_asks_for_one_noRelease_attention_push_for_a_film_and_for_a_show()
    {
        var (app, spy) = await StartAsync();
        await using var _ = app;
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        var film = await movies.AddAsync(new CreateMovieRequest("Quiet Film", 2020, null), CancellationToken.None);
        int Pushes() => spy.Dashboard.Count(item => item.Name == LiveEventNames.AttentionChanged && ((AttentionChangedEvent)item.Payload).Reason.Contains("noRelease"));

        await movies.RecordSearchAttemptAsync(
            film.Id, libraryId, "manual", "no-release", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(6), "no-release", null, null, null, CancellationToken.None);
        await Wait.UntilAsync(() => Pushes() == 1, "the movie search to push AttentionChanged noRelease");

        var shows = await ApiScenes.CreateLibraryAsync(app, "tv");
        var series = app.Services.GetRequiredService<ISeriesCatalogRepository>();
        var show = await series.AddAsync(new CreateSeriesRequest("Quiet Show", 2021, null), CancellationToken.None);
        await series.RecordSearchAttemptAsync(
            show.Id, null, shows, "manual", "no-release", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(6), "no-release", null, null, null, CancellationToken.None);
        await Wait.UntilAsync(() => Pushes() == 2, "the series search to push AttentionChanged noRelease");
    }
}
