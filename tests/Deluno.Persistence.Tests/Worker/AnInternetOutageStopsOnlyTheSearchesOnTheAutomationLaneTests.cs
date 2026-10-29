using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Deluno.Contracts.Connectivity;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Worker.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Worker;

/// <summary>
/// One tick of the planning.automation lane on the real job store. With the internet out, no library search is planned (every
/// indexer call would fail and look like nothing being available), while media-probe planning on the same tick carries on.
/// With the internet back the same library is planned. A library whose folder is gone is skipped either way.
/// </summary>
public sealed class AnInternetOutageStopsOnlyTheSearchesOnTheAutomationLaneTests
{
    private sealed class FixedConnectivity(bool outage) : IConnectivityMonitor
    {
        private ConnectivityStatus Status => new(outage, outage ? DateTimeOffset.UtcNow : null, DateTimeOffset.UtcNow, [], outage ? "Offline" : "Online");

        public ConnectivityStatus Current => Status;

        public Task<ConnectivityStatus> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(Status);

        public Task<TimeSpan> OutageOverlapAsync(DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken) => Task.FromResult(TimeSpan.Zero);
    }

    private static async Task<string> SearchingLibraryAsync(ApplicationTestHost app, string name)
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, name)).FullName;
        var response = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name,
            mediaType = "movies",
            purpose = "collection",
            rootPath = root,
            importWorkflow = "copy",
            autoSearchEnabled = true,
            missingSearchEnabled = true,
            upgradeSearchEnabled = false
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task TickAutomationLaneAsync(ApplicationTestHost app)
    {
        var worker = new DelunoHeartbeatWorker(
            NullLogger<DelunoHeartbeatWorker>.Instance,
            app.Services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            app.Services.GetRequiredService<IJobLaneSignal>());
        var lane = JobLanes.All.Single(candidate => candidate.Name == "planning.automation");
        await (Task<LaneTickResult>)typeof(DelunoHeartbeatWorker)
            .GetMethod("RunLaneTickAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(worker, [lane, 1, CancellationToken.None])!;
    }

    private static async Task<(ApplicationTestHost App, string LibraryId)> StartAsync(bool outage)
    {
        var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            for (var i = services.Count - 1; i >= 0; i--)
            {
                if (services[i].ServiceType == typeof(IConnectivityMonitor)) services.RemoveAt(i);
            }

            services.AddSingleton<IConnectivityMonitor>(new FixedConnectivity(outage));
        });
        using (var scope = app.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<Deluno.Platform.Data.IPlatformSettingsRepository>().SetGlobalAutomationEnabledAsync(true, CancellationToken.None);
        }

        return (app, await SearchingLibraryAsync(app, "Films"));
    }

    [Fact]
    public async Task While_the_internet_is_out_no_search_is_planned_but_the_media_probe_is_still_queued()
    {
        var (app, libraryId) = await StartAsync(outage: true);
        await using var _ = app;

        await TickAutomationLaneAsync(app);

        Assert.Equal(0, await ApiScenes.ScalarAsync(app, "jobs", $"SELECT COUNT(*) FROM library_automation_state WHERE library_id = '{libraryId}' AND last_planned_utc IS NOT NULL"));
        Assert.Equal(0, await ApiScenes.ScalarAsync(app, "jobs", "SELECT COUNT(*) FROM job_queue WHERE job_type LIKE 'library.search%';"));
        Assert.Equal(2, await ApiScenes.ScalarAsync(app, "jobs", "SELECT COUNT(*) FROM job_queue WHERE job_type = 'library.media.probe';"));
    }

    [Fact]
    public async Task With_the_internet_back_the_same_library_is_planned_for_search_on_the_same_tick()
    {
        var (app, libraryId) = await StartAsync(outage: false);
        await using var _ = app;

        await TickAutomationLaneAsync(app);

        Assert.Equal(1, await ApiScenes.ScalarAsync(app, "jobs", $"SELECT COUNT(*) FROM library_automation_state WHERE library_id = '{libraryId}' AND last_planned_utc IS NOT NULL"));
        Assert.Equal(2, await ApiScenes.ScalarAsync(app, "jobs", "SELECT COUNT(*) FROM job_queue WHERE job_type = 'library.media.probe';"));
    }
}
