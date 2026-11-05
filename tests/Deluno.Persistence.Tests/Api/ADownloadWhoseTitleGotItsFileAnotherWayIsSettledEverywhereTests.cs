using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Data;
using Deluno.Worker.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1511, from the rig (Gladiator II): a download's import failed, the title then got its file by a manual import, and the
/// Importing lane, the client row (importFailed) and the recovery case all stayed. Here the same story runs on a real host,
/// real files and the real import pipeline: failed dispatch, failed import job, open case, row reading import failed; then a
/// manual import of the same title (the same release, and a different one); then the row, the case and Activity are settled
/// and stay settled when the passes run again with freshly built services (a restart).
/// </summary>
public sealed class ADownloadWhoseTitleGotItsFileAnotherWayIsSettledEverywhereTests
{
    private const string Release = "Arrival.2016.WEB.1080p-GROUP";
    private const string Client = "qbit-1";

    [Theory]
    [InlineData("Arrival.2016.WEB.1080p-GROUP.mkv")]
    [InlineData("Arrival.2016.BluRay.2160p-OTHER.mkv")]
    public async Task A_failed_import_is_settled_on_the_row_the_case_and_activity_once_the_title_has_its_file_and_stays_so(string importedFileName)
    {
        await using var app = await ApplicationTestHost.StartAsync(
            replaceServices: services => services.Replace(ServiceDescriptor.Singleton<IMediaProbeService, ReadableVideoProbe>()));
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        var library = Path.Combine(app.DataRoot, "movies");
        var downloads = Path.Combine(app.DataRoot, "downloads");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(downloads);
        var made = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name = "Movies", mediaType = "movies", purpose = "collection", rootPath = library, downloadsPath = downloads,
            qualityProfileId = (string?)null, importWorkflow = "standard", processorName = (string?)null,
            processorOutputPath = (string?)null, processorTimeoutMinutes = (int?)null, processorFailureMode = (string?)null,
            autoSearchEnabled = false, missingSearchEnabled = false, upgradeSearchEnabled = false,
            searchIntervalMinutes = (int?)null, retryDelayHours = (int?)null, maxItemsPerRun = (int?)null
        });
        Assert.True(made.IsSuccessStatusCode, await made.Content.ReadAsStringAsync());
        var libraryId = (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var added = await app.Client.PostAsJsonAsync("/api/movies", new { title = "Arrival", releaseYear = 2016, monitored = true });
        added.EnsureSuccessStatusCode();
        var film = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        // The failed download: dispatch, failed import job, open case.
        var failedSource = Path.Combine(downloads, Release, Release + ".mkv");
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var dispatchId = await jobs.RecordDownloadDispatchAsync(
            libraryId, "movies", "movie", film, Release, "Fixture Indexer", Client, "qBittorrent", "sent", null,
            clientExternalId: "hash-1", cancellationToken: CancellationToken.None);
        await app.Services.GetRequiredService<IDownloadDispatchesRepository>()
            .RecordImportOutcomeAsync(dispatchId, "failed", null, "importFailed", "The destination file already exists.", CancellationToken.None);
        var job = await app.Services.GetRequiredService<IJobScheduler>().EnqueueAsync(
            new EnqueueJobRequest("filesystem.import.execute", "download-client",
                JsonSerializer.Serialize(new { Preview = new { SourcePath = failedSource }, DispatchId = dispatchId }), "movie", film),
            CancellationToken.None);
        await FailJobAsync(app, job.Id);
        var openCase = await movies.AddImportRecoveryCaseAsync(
            new CreateMovieImportRecoveryCaseRequest("Arrival", "importFailed", "The import failed.", "Retry the import."), CancellationToken.None);
        await Wait.ClockMovesOnAsync();

        Assert.Equal(DownloadQueueStatuses.ImportFailed, (await RowAsync(app, failedSource)).Status);

        // The title gets its file another way: a manual import through the real pipeline.
        var source = Path.Combine(downloads, importedFileName);
        await File.WriteAllBytesAsync(source, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-1));
        var imported = await app.Services.GetRequiredService<IImportPipelineService>().ExecuteAsync(
            new ImportExecuteRequest(
                new ImportPreviewRequest(source, null, "movies", "Arrival", 2016, null, null, null, null),
                TransferMode: "copy", Overwrite: false, AllowCopyFallback: true),
            CancellationToken.None);
        Assert.True(imported.Succeeded, imported.Message);

        await SettleAsync(app);

        await AssertSettledAsync(app, movies, failedSource, openCase.Id);

        // A restart: the passes are built again from nothing and run again; nothing reopens and nothing is said twice.
        await SettleAsync(app);
        await AssertSettledAsync(app, movies, failedSource, openCase.Id);
    }

    private static async Task AssertSettledAsync(ApplicationTestHost app, IMovieCatalogRepository movies, string failedSource, string caseId)
    {
        Assert.Equal(DownloadQueueStatuses.Imported, (await RowAsync(app, failedSource)).Status);
        var closed = await movies.GetImportRecoveryCaseAsync(caseId, CancellationToken.None);
        Assert.NotEqual("open", closed!.Status);
        var said = (await app.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(200, null, null, CancellationToken.None))
            .Where(line => line.Category == "download.import-failure-settled").ToArray();
        var line = Assert.Single(said);
        Assert.Contains("in the library now", line.Message);
    }

    private static async Task<DownloadQueueItem> RowAsync(ApplicationTestHost app, string sourcePath)
    {
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var item = new DownloadQueueItem(
            "q-1", Client, "qBittorrent", "qbittorrent", "movies", "Arrival", Release, "movies", DownloadQueueStatuses.Completed,
            100, 0, 0, 100, 100, 0, "Fixture Indexer", null, DateTimeOffset.UnixEpoch, SourcePath: sourcePath);
        var snapshot = new DownloadClientTelemetrySnapshot(
            Client, "qBittorrent", "qbittorrent", null, "healthy", null,
            new DownloadClientTelemetryCapabilities(true, true, true, true, true, true, "none"),
            new DownloadTelemetrySummary(0, 0, 1, 0, 0, 0, 0), [item], [], DateTimeOffset.UtcNow);
        var recent = await jobs.ListDownloadDispatchesAsync(500, null, CancellationToken.None);
        var everything = await jobs.ListDownloadDispatchesAsync(500, null, CancellationToken.None, includeArchived: true);
        var libraries = await app.Services.GetRequiredService<Deluno.Libraries.Data.ILibrariesRepository>().ListLibrariesAsync(CancellationToken.None);
        var enriched = DownloadClientTelemetryService.EnrichQueueImportState(
            snapshot, libraries, recent, everything, await jobs.ListAsync(500, CancellationToken.None), [],
            new Dictionary<string, IReadOnlyDictionary<string, string>>());
        return enriched.Queue.Single();
    }

    // What the planner runs each pass, built fresh each time like a restarted host would.
    private static Task SettleAsync(ApplicationTestHost app)
        => new HandledProblemsSettler(
                app.Services.GetRequiredService<IMovieCatalogRepository>(),
                app.Services.GetRequiredService<ISeriesCatalogRepository>(),
                app.Services.GetRequiredService<IJobQueueRepository>(),
                app.Services.GetRequiredService<IDownloadDispatchesRepository>(),
                app.Services.GetRequiredService<IActivityFeedRepository>(),
                app.Services.GetRequiredService<Deluno.Platform.Data.IProcessorRepository>())
            .SettleAsync(CancellationToken.None);

    private static async Task FailJobAsync(ApplicationTestHost app, string jobId)
    {
        var db = app.Services.GetRequiredService<Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory>();
        await using var connection = await db.OpenConnectionAsync("jobs", CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE job_queue SET status = 'failed', completed_utc = @now WHERE id = @id;";
        foreach (var (name, value) in new[] { ("@id", jobId), ("@now", DateTimeOffset.UtcNow.ToString("O")) })
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync();
    }

    private sealed class ReadableVideoProbe : IMediaProbeService
    {
        public Task<MediaProbeInfo> ProbeAsync(string path, CancellationToken cancellationToken)
            => Task.FromResult(new MediaProbeInfo(
                Status: "succeeded", Tool: "test", Message: null, DurationSeconds: 7200, Container: "matroska", Bitrate: 12_000_000,
                VideoStreams: [new MediaVideoStreamInfo(0, "h264", "High", 1920, 1080, "yuv420p", 23.976, 10_000_000, "eng")],
                AudioStreams: [], SubtitleStreams: []));
    }
}
