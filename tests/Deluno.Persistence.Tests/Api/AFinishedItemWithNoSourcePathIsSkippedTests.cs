using System.Net.Http.Json;
using Deluno.Filesystem;
using Deluno.Integrations.DownloadClients;
using Deluno.Platform.Contracts;
using Deluno.Integrations.Processors;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Series.Data;
using Deluno.Worker.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using static Deluno.Persistence.Tests.Api.AHandOffFollowsTheDownloadItIsForTests;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// One real planner pass over a finished download (#1329): a finished item the client gives no source path for has nothing to
/// import, so the planner queues no import and hands nothing to a processor; the same item with a path to a real file in the
/// library's downloads folder does queue its import.
/// </summary>
public sealed class AFinishedItemWithNoSourcePathIsSkippedTests
{
    private const string Client = "qbittorrent-1";

    private static async Task PlanAsync(ApplicationTestHost app, string libraryId, string release, string? sourcePath)
    {
        var services = app.Services;
        var summary = new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0);
        var item = new DownloadQueueItem(
            $"hash-{release}", Client, "qBittorrent", "qbittorrent", "movies", "Pathless Film", release,
            "deluno-movies", DownloadQueueStatuses.Completed, 100, 0, 0, 8192, 8192, 0, "Fixture", null,
            DateTimeOffset.UtcNow.AddHours(-2), sourcePath, libraryId);
        var overview = new DownloadTelemetryOverview(
            summary,
            [new DownloadClientTelemetrySnapshot(
                Client, "qBittorrent", "qbittorrent", "http://127.0.0.1:8080", "healthy", null,
                new DownloadClientTelemetryCapabilities(true, true, true, true, false, true, "password"),
                summary, [item], [], DateTimeOffset.UtcNow)],
            DateTimeOffset.UtcNow);
        var telemetry = Stub.Create<IDownloadClientTelemetryService>((name, _) => name switch
        {
            "GetFreshOverviewAsync" => overview,
            "RunConfiguredHealthRemediationAsync" => new DownloadHealthRemediationReport(0, 0, 0, 0, 0, []),
            _ => throw new NotSupportedException(name)
        });
        var processors = Stub.Create<IProcessorConnectionService>((name, _) => throw new NotSupportedException(name));

        await new WorkPlanner(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkPlanner>.Instance,
                services.GetRequiredService<IJobQueueRepository>(),
                services.GetRequiredService<IConfiguration>(),
                TimeProvider.System)
            .PlanImportAutomationAsync(
                services.GetRequiredService<IJobScheduler>(),
                services.GetRequiredService<IProcessorRepository>(),
                services.GetRequiredService<ILibrariesRepository>(),
                services.GetRequiredService<ILibraryAvailabilityService>(),
                telemetry,
                processors,
                services.GetRequiredService<IActivityFeedRepository>(),
                services.GetRequiredService<IMovieCatalogRepository>(),
                services.GetRequiredService<ISeriesCatalogRepository>(),
                TimeProvider.System,
                CancellationToken.None);
    }

    /// <summary>The planner claims its scheduled pass once per interval, so each case gets its own host and a first pass of its own.</summary>
    private static async Task<long> ImportJobsQueuedAsync(bool clientGivesAPath)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var downloads = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Completed")).FullName;
        var response = await app.Client.PostAsJsonAsync("/api/libraries", new { name = "Films", mediaType = "movies", rootPath = root, downloadsPath = downloads, importWorkflow = "copy" });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var library = (await response.Content.ReadFromJsonAsync<LibraryItem>())!;
        var movie = await app.Services.GetRequiredService<IMovieCatalogRepository>().AddAsync(new CreateMovieRequest("Pathless Film", 2020, null), CancellationToken.None);
        const string release = "Pathless.Film.2020.1080p.WEB-DL-GRP";
        await app.Services.GetRequiredService<IJobQueueRepository>().RecordDownloadDispatchAsync(
            library.Id, "movies", "movie", movie.Id, release, "Fixture", Client, "qBittorrent", "sent", null,
            clientExternalId: $"hash-{release}", cancellationToken: CancellationToken.None);
        var folder = Directory.CreateDirectory(Path.Combine(downloads, release)).FullName;
        var file = Path.Combine(folder, release + ".mkv");
        await File.WriteAllBytesAsync(file, Enumerable.Repeat((byte)0x31, 8192).ToArray());
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-10));

        await PlanAsync(app, library.Id, release, clientGivesAPath ? file : null);

        return await ApiScenes.ScalarAsync(app, "jobs", "SELECT COUNT(*) FROM job_queue WHERE job_type = 'filesystem.import.execute'");
    }

    [Fact]
    public async Task A_completed_item_with_no_source_path_queues_no_import_and_the_same_item_with_a_file_to_import_does()
    {
        Assert.Equal(1, await ImportJobsQueuedAsync(clientGivesAPath: true));
        Assert.Equal(0, await ImportJobsQueuedAsync(clientGivesAPath: false));
    }
}
