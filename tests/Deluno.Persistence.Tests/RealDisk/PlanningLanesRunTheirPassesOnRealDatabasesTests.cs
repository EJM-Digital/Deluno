using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Worker.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// The two planning lanes of the real worker on real database files, with automatic jobs switched on: the imports lane
/// (import resume, import automation, sharing reclaim) and the automation lane (collection sync, intake, media probe) each
/// claim their passes, run them and record a result that is not a failure.
/// </summary>
public sealed class PlanningLanesRunTheirPassesOnRealDatabasesTests
{
    private sealed class SwitchAutomationOn(IServiceProvider services) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            using var scope = services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IPlatformSettingsRepository>().SetGlobalAutomationEnabledAsync(true, cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task The_imports_lane_resumes_scans_imports_finished_downloads_and_reclaims_seeding_files_and_the_automation_lane_reads_lists_and_probes_media()
    {
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.AddHostedService<SwitchAutomationOn>();
            services.AddHostedService<DelunoHeartbeatWorker>();
        });
        var film = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021);
        var before = File.ReadAllBytes(film.FilePath);
        string[] imports = [SystemTasks.LibraryImportResume, SystemTasks.ImportAutomation, SystemTasks.SharingReclaim];
        string[] automation = [SystemTasks.IntakeAutomation, SystemTasks.MediaProbe];

        await Wait.UntilAsync(
            async () =>
            {
                using var scope = app.Services.CreateScope();
                var states = await scope.ServiceProvider.GetRequiredService<IJobQueueRepository>().ListSystemTaskStatesAsync(CancellationToken.None);
                return imports.Concat(automation).All(key => states.Any(state => state.ScheduleKey == key && state.LastResult is not null));
            },
            "both planning lanes to run their passes",
            TimeSpan.FromSeconds(120));

        Assert.Equal(before, File.ReadAllBytes(film.FilePath));
        using var final = app.Services.CreateScope();
        var recorded = await final.ServiceProvider.GetRequiredService<IJobQueueRepository>().ListSystemTaskStatesAsync(CancellationToken.None);
        foreach (var key in imports.Concat(automation))
        {
            var state = recorded.Single(row => row.ScheduleKey == key);
            Assert.NotEqual("failed", state.LastResult);
            Assert.True(string.IsNullOrWhiteSpace(state.LastError), $"{key}: {state.LastError}");
            Assert.NotNull(state.NextRunUtc);
        }
    }
}
