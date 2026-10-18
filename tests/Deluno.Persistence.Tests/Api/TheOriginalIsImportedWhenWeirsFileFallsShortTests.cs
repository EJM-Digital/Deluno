using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Processors;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.RealDisk;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Series.Data;
using Deluno.Tests.Recorded;
using Deluno.Worker.Jobs;
using Deluno.Worker.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using static Deluno.Persistence.Tests.Api.AHandOffFollowsTheDownloadItIsForTests;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// "Import the original", end to end on real files: Weir reports a hand-off finished (the report its real rc.13 sent, replayed
/// from tests/fixtures/recorded/deluno), what it made is far shorter than the download, so the import refuses it and opens a
/// case; the person presses Import original; the hand-off is released and the planner does not send it to Weir again; and the
/// unprocessed download is imported once into the library with its own bytes, the case closed and the original left in place.
/// </summary>
public sealed class TheOriginalIsImportedWhenWeirsFileFallsShortTests
{
    private const string VmOutputRoot = @"C:\Weir\Ready\Movies";
    private const string Client = "qbittorrent-1";

    private static Exchange Recorded(string state) => RecordedReplies.Deluno(state).Exchanges[0];

    private static string OnDiskText(ApplicationTestHost app, string database, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(app.DataRoot, database)};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
    }

    [Fact]
    public async Task Import_original_after_a_short_file_from_Weir_imports_the_unprocessed_download_once_and_never_sends_it_to_Weir_again()
    {
        // Weir's processed file is a 3 minute stub of a 2 hour film; the download it was given is whole.
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: RealDiskLibrary.UseProbe(new ScriptedProbe(path =>
            path.Contains("Refined", StringComparison.OrdinalIgnoreCase) ? ScriptedProbe.Good(seconds: 180) : ScriptedProbe.Good(seconds: 7200))));
        var report = Recorded("events-completed");
        var release = JsonNode.Parse(report.Request.Body!)!["releaseName"]!.GetValue<string>();
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var downloads = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Completed")).FullName;
        var output = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Refined")).FullName;
        var downloadFolder = Directory.CreateDirectory(Path.Combine(downloads, release)).FullName;
        var originalFile = Path.Combine(downloadFolder, release + ".mkv");
        await File.WriteAllBytesAsync(originalFile, Enumerable.Repeat((byte)0x5A, 8192).ToArray());
        File.SetLastWriteTimeUtc(originalFile, DateTime.UtcNow.AddMinutes(-10));
        var response = await app.Client.PostAsJsonAsync("/api/libraries", new
        {
            name = "Films",
            mediaType = "movies",
            rootPath = root,
            downloadsPath = downloads,
            importWorkflow = "refine-before-import",
            processorName = "Weir",
            processorOutputPath = output
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var library = (await response.Content.ReadFromJsonAsync<LibraryItem>())!;
        var movie = await app.Services.GetRequiredService<IMovieCatalogRepository>().AddAsync(new CreateMovieRequest("Weir Film", 2020, null), CancellationToken.None);
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var dispatchId = await jobs.RecordDownloadDispatchAsync(
            library.Id, "movies", "movie", movie.Id, release, "Fixture", Client, "qBittorrent", "sent", null,
            clientExternalId: $"hash-{release}", cancellationToken: CancellationToken.None);
        var processors = app.Services.GetRequiredService<IProcessorRepository>();
        var handoff = await processors.EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(library.Id, "movies", Client, $"hash-{release}", release, downloadFolder, "Weir"),
            CancellationToken.None);

        // Weir reports the hand-off finished, exactly as it sent it; what it made is a short file in its output folder.
        var processedFile = await ReportAsync(app, library.Id, handoff.Id, report, output);
        File.SetLastWriteTimeUtc(processedFile, DateTime.UtcNow.AddMinutes(-2));
        Assert.Equal(ProcessorHandoffStatuses.Completed, OnDiskText(app, "platform.db", $"SELECT status FROM processor_handoffs WHERE id = '{handoff.Id}'"));

        // The import of that file is refused, and a case is opened that offers Import original.
        using (var scope = app.Services.CreateScope())
        {
            var refused = await scope.ServiceProvider.GetRequiredService<IImportPipelineService>().ExecuteAsync(
                new ImportExecuteRequest(
                    new ImportPreviewRequest(processedFile, null, "movies", "Weir Film", 2020, null, null, null, null, LibraryId: library.Id),
                    "copy", false, true, DispatchId: dispatchId, ProcessedFromPath: downloadFolder, HandoffId: handoff.Id),
                CancellationToken.None);
            Assert.False(refused.Succeeded, refused.Message);
        }

        var cases = (await app.Services.GetRequiredService<IMovieCatalogRepository>().GetImportRecoverySummaryAsync(CancellationToken.None)).RecentCases;
        var fault = Assert.Single(cases, item => item.FailureKind == ImportFailurePolicy.ProcessedFileIsLess);
        Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));

        // The person presses Import original.
        var pressed = await app.Client.PostAsJsonAsync($"/api/movies/import-recovery/{fault.Id}/import-original", new { });
        Assert.True(pressed.IsSuccessStatusCode, await pressed.Content.ReadAsStringAsync());
        Assert.Equal(ProcessorHandoffStatuses.Released, OnDiskText(app, "platform.db", $"SELECT status FROM processor_handoffs WHERE id = '{handoff.Id}'"));
        Assert.Equal("resolved", OnDiskText(app, "movies.db", $"SELECT status FROM movie_import_recovery_cases WHERE id = '{fault.Id}'"));

        // The next planner pass does not send it to Weir again; it queues the unprocessed download for import.
        var submissions = 0;
        await PlanAsync(app, library.Id, release, downloadFolder, () => submissions++);
        Assert.Equal(0, submissions);
        // Weir's own report queued the import of its short file (which the direct import above stands in for); the planner queues the original.
        var queued = Assert.Single(await jobs.ListAsync(100, CancellationToken.None), job => job.JobType == "filesystem.import.execute" && job.Source == "download-client");
        using (var scope = app.Services.CreateScope())
        {
            var handler = scope.ServiceProvider.GetServices<IJobHandler>().OfType<FilesystemImportExecuteJobHandler>().Single();
            await handler.HandleAsync(queued, CancellationToken.None);
        }

        // The original lands once in the library, with its own bytes; the download it came from is still there.
        var landed = Assert.Single(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        Assert.Equal(File.ReadAllBytes(originalFile), File.ReadAllBytes(landed));
        Assert.True(File.Exists(originalFile), "importing the original leaves the download in place");
        Assert.Equal(ProcessorHandoffStatuses.Released, OnDiskText(app, "platform.db", $"SELECT status FROM processor_handoffs WHERE id = '{handoff.Id}'"));
    }

    private static async Task<string> ReportAsync(ApplicationTestHost app, string libraryId, string handoffId, Exchange recorded, string output)
    {
        var body = JsonNode.Parse(recorded.Request.Body!)!.AsObject();
        body["libraryId"] = libraryId;
        body["handoffId"] = handoffId;
        string Local(string path)
        {
            var relative = path.StartsWith(VmOutputRoot, StringComparison.OrdinalIgnoreCase) ? path[VmOutputRoot.Length..].TrimStart('\\') : path;
            return Path.Combine(output, Path.Combine(relative.Split('\\')));
        }

        if (body["outputPath"] is { } outputPath)
        {
            body["outputPath"] = Local(outputPath.GetValue<string>());
        }

        var files = (JsonArray)body["outputFiles"]!;
        var local = files.Select(f => Local(f!.GetValue<string>())).ToArray();
        body["outputFiles"] = new JsonArray(local.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray());
        foreach (var file in local)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllBytesAsync(file, new byte[2048]);
        }

        var response = await app.Client.PostAsync("/api/integrations/processors/events", new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal((HttpStatusCode)recorded.Reply.Status, response.StatusCode);
        return local[0];
    }

    /// <summary>One real planner pass over a finished download; anything it would send to the processor is only counted.</summary>
    private static async Task PlanAsync(ApplicationTestHost app, string libraryId, string release, string downloadFolder, Action submitted)
    {
        var services = app.Services;
        var summary = new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0);
        var item = new DownloadQueueItem(
            $"hash-{release}", Client, "qBittorrent", "qbittorrent", "movies", "Weir Film", release,
            "deluno-movies", DownloadQueueStatuses.Completed, 100, 0, 0, 8192, 8192, 0, "Fixture", null,
            DateTimeOffset.UtcNow.AddHours(-2), downloadFolder, libraryId);
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
        var processors = Stub.Create<IProcessorConnectionService>((name, _) =>
        {
            switch (name)
            {
                case "SubmitAsync":
                    submitted();
                    return new ProcessorSubmissionResult(true, ProcessorHandoffStatuses.Submitted, "Accepted.", 202);
                case "GetStatusSupportAsync":
                    return ProcessorStatusSupport.None;
                default:
                    throw new NotSupportedException(name);
            }
        });

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
}
