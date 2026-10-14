using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Movies.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Recovery.Contracts;
using Deluno.Recovery.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// The clean-up of old resolved import-recovery cases, run by the real service against real database files: resolved
/// cases older than the keep period go, recent and open ones stay, and the source file a case names is never touched.
/// </summary>
public sealed class ImportRecoveryRetentionOnDiskTests
{
    [Fact]
    public async Task Resolved_cases_older_than_the_keep_period_are_removed_while_recent_and_open_ones_and_the_files_they_name_stay()
    {
        var app = await ApplicationTestHost.StartAsync();
        Directory.CreateDirectory(Path.Combine(app.DataRoot, "downloads"));
        await using var world = await RealDiskLibrary.OpenAsync(app);
        var named = world.WriteDownload("Sintel.2010.WEB.1080p.mkv");
        async Task<string> CaseAsync(string title) => (await world.Movies.AddImportRecoveryCaseAsync(
            new CreateMovieImportRecoveryCaseRequest(title, "unmatched", "Deluno could not match this file.", "Choose the film.", $"{{\"SourcePath\":\"{named.Replace("\\", "\\\\")}\"}}"),
            CancellationToken.None)).Id;
        var oldResolved = await CaseAsync("Old and resolved");
        var recentResolved = await CaseAsync("Recent and resolved");
        var stillOpen = await CaseAsync("Old and still open");
        await world.Movies.ResolveImportRecoveryCaseAsync(oldResolved, "resolved", CancellationToken.None);
        await world.Movies.ResolveImportRecoveryCaseAsync(recentResolved, "resolved", CancellationToken.None);
        await world.SqlAsync("movies", $"UPDATE movie_import_recovery_cases SET resolved_utc = '{DateTimeOffset.UtcNow.AddDays(-45):O}', detected_utc = '{DateTimeOffset.UtcNow.AddDays(-60):O}' WHERE id = '{oldResolved}';");
        await world.SqlAsync("movies", $"UPDATE movie_import_recovery_cases SET detected_utc = '{DateTimeOffset.UtcNow.AddDays(-90):O}' WHERE id = '{stillOpen}';");
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var service = new ImportRecoveryRetentionService(
            services.GetRequiredService<IMovieImportRecoveryRetentionRepository>(),
            services.GetRequiredService<ISeriesImportRecoveryRetentionRepository>(),
            services.GetRequiredService<IPlatformSettingsRepository>(),
            TimeProvider.System,
            NullLogger<ImportRecoveryRetentionService>.Instance,
            services.GetRequiredService<IJobQueueRepository>());

        await service.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(async () => (await services.GetRequiredService<IJobQueueRepository>().ListSystemTaskStatesAsync(CancellationToken.None))
            .Any(state => state.ScheduleKey == SystemTasks.ImportRecoveryRetention && state.LastResult is "completed" or "failed"), "the clean-up pass to be recorded");
        await service.StopAsync(CancellationToken.None);

        async Task<long> RowsAsync(string id) => Convert.ToInt64(await world.ScalarAsync("movies", $"SELECT COUNT(*) FROM movie_import_recovery_cases WHERE id = '{id}';"));
        Assert.Equal(0, await RowsAsync(oldResolved));
        Assert.Equal(1, await RowsAsync(recentResolved));
        Assert.Equal(1, await RowsAsync(stillOpen));
        Assert.True(File.Exists(named), "the file a case names is never touched by clearing old cases");
        var state = (await services.GetRequiredService<IJobQueueRepository>().ListSystemTaskStatesAsync(CancellationToken.None)).Single(item => item.ScheduleKey == SystemTasks.ImportRecoveryRetention);
        Assert.Equal("completed", state.LastResult);
    }
}
