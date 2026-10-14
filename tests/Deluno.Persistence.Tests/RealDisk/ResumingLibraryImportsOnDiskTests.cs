using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Worker.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// The pass that picks an existing-library import back up when it has stood still for five minutes, run by the real
/// work planner over real library folders and the real job queue: an idle run is queued again, a paused run is left alone,
/// and the pass records itself.
/// </summary>
public sealed class ResumingLibraryImportsOnDiskTests
{
    private static async Task<string> LibraryAsync(ApplicationTestHost app, string name)
    {
        var root = Path.Combine(app.DataRoot, name);
        Directory.CreateDirectory(Path.Combine(root, "Nosferatu (1922)"));
        File.WriteAllBytes(Path.Combine(root, "Nosferatu (1922)", "Nosferatu.1922.1080p.BluRay.x264-HD4U.mkv"), [1, 2, 3, 4]);
        var response = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name, mediaType = "movies", purpose = "collection", rootPath = root, downloadsPath = (string?)null, qualityProfileId = (string?)null,
            importWorkflow = "copy", processorName = (string?)null, processorOutputPath = (string?)null, processorTimeoutMinutes = (int?)null,
            processorFailureMode = (string?)null, autoSearchEnabled = false, missingSearchEnabled = false, upgradeSearchEnabled = false,
            searchIntervalMinutes = (int?)null, retryDelayHours = (int?)null, maxItemsPerRun = (int?)null
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task A_run_idle_for_five_minutes_is_queued_again_and_a_paused_run_is_left_alone_and_the_pass_is_recorded()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var idleLibrary = await LibraryAsync(app, "movies-idle");
        var pausedLibrary = await LibraryAsync(app, "movies-paused");
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var import = services.GetRequiredService<IExistingLibraryImportService>();
        var idle = await import.StartAsync(idleLibrary, CancellationToken.None);
        var paused = await import.StartAsync(pausedLibrary, CancellationToken.None);
        Assert.NotNull(await import.SetStateAsync(pausedLibrary, LibraryImportRunStatuses.Paused, CancellationToken.None));
        var planner = new WorkPlanner(NullLogger<WorkPlanner>.Instance, services.GetRequiredService<IJobQueueRepository>(), new ConfigurationBuilder().Build(), TimeProvider.System);

        // Ten minutes on: the run that was queued now has stood still for longer than five.
        await planner.PlanLibraryImportResumeAsync(
            services.GetRequiredService<IJobScheduler>(),
            import,
            new FixedTimeProvider(DateTimeOffset.UtcNow.AddMinutes(10)),
            CancellationToken.None);

        var queued = (await services.GetRequiredService<IJobQueueRepository>().ListAsync(100, CancellationToken.None))
            .Where(job => job.JobType == "library.import.existing" && job.Source == "library-import-resume").ToArray();
        var job = Assert.Single(queued);
        using var payload = JsonDocument.Parse(job.PayloadJson!);
        Assert.Equal(idle!.Run.Id, payload.RootElement.GetProperty("RunId").GetString());
        Assert.Equal(idleLibrary, payload.RootElement.GetProperty("LibraryId").GetString());
        Assert.DoesNotContain(paused!.Run.Id, job.PayloadJson);
        var recorded = (await services.GetRequiredService<IJobQueueRepository>().ListSystemTaskStatesAsync(CancellationToken.None)).Single(state => state.ScheduleKey == SystemTasks.LibraryImportResume);
        Assert.Equal("completed", recorded.LastResult);
        Assert.True(File.Exists(Path.Combine(app.DataRoot, "movies-idle", "Nosferatu (1922)", "Nosferatu.1922.1080p.BluRay.x264-HD4U.mkv")), "the library is untouched");
    }

    [Fact]
    public async Task A_run_that_has_not_stood_still_long_enough_is_not_queued_again()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var library = await LibraryAsync(app, "movies-busy");
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var import = services.GetRequiredService<IExistingLibraryImportService>();
        await import.StartAsync(library, CancellationToken.None);
        var planner = new WorkPlanner(NullLogger<WorkPlanner>.Instance, services.GetRequiredService<IJobQueueRepository>(), new ConfigurationBuilder().Build(), TimeProvider.System);

        await planner.PlanLibraryImportResumeAsync(services.GetRequiredService<IJobScheduler>(), import, TimeProvider.System, CancellationToken.None);

        Assert.DoesNotContain(await services.GetRequiredService<IJobQueueRepository>().ListAsync(100, CancellationToken.None), job => job.Source == "library-import-resume");
    }
}
