using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Worker.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The real worker (every lane of it, on real databases) against a fresh install and against a paused one. Nothing automatic is
/// on by default (#936), and pausing automation stops every lane from planning, searching, leasing and importing; turning it on
/// is what starts them. The one marker is a queued import job: a worker that is allowed to work leases it, and one that is not
/// leaves it exactly as it was.
/// </summary>
public sealed class ThePausedWorkerDoesNothingTests
{
    private static readonly TimeSpan Watch = TimeSpan.FromSeconds(12);

    /// <summary>Rows in a table of the jobs database file itself, read beside the running application.</summary>
    private static long RowsOnDisk(ApplicationTestHost app, string table)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(app.DataRoot, "jobs.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public async Task A_fresh_install_runs_nothing_automatic_with_a_wanted_film_a_search_library_and_a_queued_job_waiting()
    {
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddHostedService<DelunoHeartbeatWorker>());
        Assert.False((await app.Client.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("autoStartJobs").GetBoolean(), "a new install starts with automation off");
        var libraryId = await SeedSearchingLibraryWithAWantedFilmAsync(app);
        var marker = await QueueMarkerAsync(app);
        var jobsBefore = await JobTypesAsync(app);
        var jobRowsBefore = RowsOnDisk(app, "job_queue");
        var activityRowsBefore = RowsOnDisk(app, "activity_events");
        var activityBefore = await app.Client.GetStringAsync("/api/activity?take=200");

        // Every lane ticks at least every few seconds; this is several ticks of all of them.
        await Task.Delay(Watch); // reason: lanes tick on their own timers, so watching them is the only way to see a worker that does nothing

        var job = await app.Client.GetFromJsonAsync<JsonElement>($"/api/jobs/{marker}");
        Assert.Equal("queued", job.GetProperty("status").GetString());
        Assert.Equal(0, job.GetProperty("attempts").GetInt32());
        Assert.Equal(jobsBefore, await JobTypesAsync(app));
        Assert.Equal(jobRowsBefore, RowsOnDisk(app, "job_queue"));
        Assert.Equal(activityRowsBefore, RowsOnDisk(app, "activity_events"));
        Assert.Equal(activityBefore, await app.Client.GetStringAsync("/api/activity?take=200"));
        Assert.Contains(libraryId, await app.Client.GetStringAsync("/api/libraries/"));
    }

    [Fact]
    public async Task Pausing_automation_through_the_route_holds_the_worker_and_resuming_lets_it_lease_the_queued_job()
    {
        // Switched on before the worker's first tick, then paused and resumed through the real route.
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.AddHostedService<SwitchAutomationOn>();
            services.AddHostedService<DelunoHeartbeatWorker>();
        });
        Assert.True((await app.Client.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("autoStartJobs").GetBoolean());

        using var paused = await app.Client.PutAsJsonAsync("/api/settings/automation", new { isEnabled = false });
        Assert.Equal(HttpStatusCode.OK, paused.StatusCode);
        Assert.False((await paused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("autoStartJobs").GetBoolean());
        Assert.False((await app.Client.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("autoStartJobs").GetBoolean());

        // The lanes read the switch at most every fifteen seconds, so a pause is felt within that.
        await Task.Delay(TimeSpan.FromSeconds(16)); // reason: the worker caches the switch for fifteen seconds
        var marker = await QueueMarkerAsync(app);
        await Task.Delay(Watch); // reason: lanes tick on their own timers
        Assert.Equal("queued", (await app.Client.GetFromJsonAsync<JsonElement>($"/api/jobs/{marker}")).GetProperty("status").GetString());

        using var resumed = await app.Client.PutAsJsonAsync("/api/settings/automation", new { isEnabled = true });
        Assert.True((await resumed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("autoStartJobs").GetBoolean());
        // An executor lane that found nothing to do sleeps until something wakes it, so once the switch is read again
        // (fifteen seconds) a new job is what wakes it, and it takes the job that was waiting along with it.
        await Task.Delay(TimeSpan.FromSeconds(16)); // reason: the worker caches the switch for fifteen seconds
        var waker = await QueueMarkerAsync(app);
        await Wait.UntilAsync(
            async () => (await app.Client.GetFromJsonAsync<JsonElement>($"/api/jobs/{marker}")).GetProperty("status").GetString() != "queued"
                && (await app.Client.GetFromJsonAsync<JsonElement>($"/api/jobs/{waker}")).GetProperty("status").GetString() != "queued",
            "the resumed worker to lease the waiting job and the new one",
            TimeSpan.FromSeconds(60));
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<string> QueueMarkerAsync(ApplicationTestHost app)
    {
        var job = await app.Services.GetRequiredService<IJobScheduler>().EnqueueAsync(
            new EnqueueJobRequest(
                JobType: "filesystem.import.execute",
                Source: "test",
                PayloadJson: "{\"marker\":\"" + Guid.NewGuid().ToString("N") + "\"}",
                RelatedEntityType: null,
                RelatedEntityId: null),
            CancellationToken.None);
        return job.Id;
    }

    private static async Task<string> JobTypesAsync(ApplicationTestHost app)
    {
        var page = await app.Client.GetFromJsonAsync<JsonElement>("/api/jobs?pageSize=100");
        var items = page.TryGetProperty("items", out var list) ? list : page;
        return string.Join(",", items.EnumerateArray().Select(job => job.GetProperty("jobType").GetString() + ":" + job.GetProperty("status").GetString()).Order());
    }

    private static async Task<string> SeedSearchingLibraryWithAWantedFilmAsync(ApplicationTestHost app)
    {
        var root = Path.Combine(app.DataRoot, "movies");
        Directory.CreateDirectory(root);
        var created = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name = "Films",
            mediaType = "movies",
            purpose = "collection",
            rootPath = root,
            downloadsPath = (string?)null,
            qualityProfileId = (string?)null,
            importWorkflow = "copy",
            processorName = (string?)null,
            processorOutputPath = (string?)null,
            processorTimeoutMinutes = (int?)null,
            processorFailureMode = (string?)null,
            autoSearchEnabled = true,
            missingSearchEnabled = true,
            upgradeSearchEnabled = true,
            searchIntervalMinutes = 1,
            retryDelayHours = 1,
            maxItemsPerRun = 25
        });
        created.EnsureSuccessStatusCode();
        var libraryId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var added = await app.Client.PostAsJsonAsync("/api/movies/", new { title = "Wanted Film", releaseYear = 1977, monitored = true, libraryId, searchForMissingNow = false });
        added.EnsureSuccessStatusCode();
        return libraryId;
    }

    /// <summary>Turns automation on at start, before the worker's first tick (the only way to start the real worker already running).</summary>
    private sealed class SwitchAutomationOn(IServiceProvider services) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            using var scope = services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IPlatformSettingsRepository>().SetGlobalAutomationEnabledAsync(true, cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
