using System.Net.Http.Json;
using Deluno.Filesystem;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Processors;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Series.Data;
using Deluno.Worker.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using static Deluno.Persistence.Tests.Api.AHandOffFollowsTheDownloadItIsForTests;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1519: a finished single-file torrent (qBittorrent names it with its ".mkv", spaces and " - ", and its content path is the
/// file itself, in the category folder) is handed to the processor like a folder one, or says why not. The row shape is the
/// recorded <c>finished-single-file</c> reply: name with the extension, content path the file.
/// </summary>
public sealed class AFinishedSingleFileDownloadIsHandedOverTests
{
    private const string Client = "qbittorrent-1";
    private const string Hash = "d725bf5a29bcf10e3f7d2b8ebbb221b1fe0e783c";
    private const string Indexer = "Spider-Man Homecoming (2017) 1080p H264 ITA ENG AC3 5.1 BluRay - LoZio - MIRCrew";

    private sealed record Scene(LibraryItem Library, string File, DownloadQueueItem Item);

    private static async Task<Scene> SceneAsync(ApplicationTestHost app)
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var downloads = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Completed", "Movies")).FullName;
        var ready = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Ready")).FullName;
        var response = await app.Client.PostAsJsonAsync("/api/libraries", new { name = "Films", mediaType = "movies", rootPath = root, downloadsPath = downloads, importWorkflow = "direct-import" });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var library = (await response.Content.ReadFromJsonAsync<LibraryItem>())!;
        var workflow = await app.Client.PutAsJsonAsync($"/api/libraries/{library.Id}/workflow", new
        {
            importWorkflow = "refine-before-import", processorName = "Weir", processorOutputPath = ready,
            processorTimeoutMinutes = (int?)null, processorFailureMode = (string?)null
        });
        Assert.True(workflow.IsSuccessStatusCode, await workflow.Content.ReadAsStringAsync());

        var movie = await app.Services.GetRequiredService<IMovieCatalogRepository>().AddAsync(new CreateMovieRequest("Spider-Man Homecoming", 2017, null), CancellationToken.None);
        await app.Services.GetRequiredService<IJobQueueRepository>().RecordDownloadDispatchAsync(
            library.Id, "movies", "movie", movie.Id, Indexer, "Fixture", Client, "qBittorrent", "sent", null,
            clientExternalId: Hash, cancellationToken: CancellationToken.None);
        var file = Path.Combine(downloads, Indexer + ".mkv");
        await File.WriteAllBytesAsync(file, Enumerable.Repeat((byte)0x31, 8192).ToArray());
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-10));

        // The recorded single-file reply: the client's name is the file's name, extension included.
        var item = new DownloadQueueItem(
            Hash, Client, "qBittorrent", "qbittorrent", "movies", "Spider-Man Homecoming", Path.GetFileName(file),
            "deluno-movies", DownloadQueueStatuses.Completed, 100, 0, 0, 8192, 8192, 0, "Fixture", null,
            DateTimeOffset.UtcNow.AddHours(-2), file, null);
        return new Scene(library, file, item);
    }

    private static async Task PlanAsync(ApplicationTestHost app, DownloadQueueItem item)
    {
        var summary = new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0);
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

        var services = app.Services;
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

    [Fact]
    public async Task A_single_file_the_client_still_holds_open_is_handed_to_the_processor()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);

        // A seeding client can hold its finished file so that Deluno cannot open it; a folder download is never opened.
        using var heldByClient = new FileStream(scene.File, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await PlanAsync(app, scene.Item);

        Assert.Equal(1, await ApiScenes.ScalarAsync(app, "platform", "SELECT COUNT(*) FROM processor_handoffs"));
    }

    [Fact]
    public async Task A_single_file_that_is_free_is_handed_to_the_processor()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);

        await PlanAsync(app, scene.Item);

        Assert.Equal(1, await ApiScenes.ScalarAsync(app, "platform", "SELECT COUNT(*) FROM processor_handoffs"));
    }

    [Fact]
    public async Task A_single_file_the_client_holds_and_is_still_changing_is_waited_for_and_says_so_once()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        File.SetLastWriteTimeUtc(scene.File, DateTime.UtcNow);

        using var heldByClient = new FileStream(scene.File, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await PlanAsync(app, scene.Item);
        await PlanAsync(app, scene.Item);

        Assert.Equal(0, await ApiScenes.ScalarAsync(app, "platform", "SELECT COUNT(*) FROM processor_handoffs"));
        var told = await app.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(20, null, null, CancellationToken.None);
        Assert.Single(told, activity => activity.Category == WorkPlanner.HeldByFileCheckCategory);
    }

    [Fact]
    public async Task A_finished_download_with_no_library_says_why_once_instead_of_waiting_silently()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var elsewhere = scene.Item with { MediaType = "tv", Category = "other", SourcePath = Path.Combine(app.DataRoot, "Elsewhere", "Some.Film.mkv") };
        Directory.CreateDirectory(Path.GetDirectoryName(elsewhere.SourcePath)!);
        await File.WriteAllBytesAsync(elsewhere.SourcePath!, new byte[64]);
        File.SetLastWriteTimeUtc(elsewhere.SourcePath!, DateTime.UtcNow.AddMinutes(-10));

        await PlanAsync(app, elsewhere);
        await PlanAsync(app, elsewhere);

        var told = await app.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(20, null, null, CancellationToken.None);
        var line = Assert.Single(told, activity => activity.Category == "processing.not-handed-over");
        Assert.Contains("found no library", line.Message);
    }

    [Fact]
    public async Task A_finished_download_whose_client_does_not_say_where_its_files_are_says_so_once()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var unplaced = scene.Item with { SourcePath = null };

        await PlanAsync(app, unplaced);
        await PlanAsync(app, unplaced);

        Assert.Equal(0, await ApiScenes.ScalarAsync(app, "platform", "SELECT COUNT(*) FROM processor_handoffs"));
        var told = await app.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(20, null, null, CancellationToken.None);
        var line = Assert.Single(told, activity => activity.Category == "processing.not-handed-over");
        Assert.Contains("did not say where its files are", line.Message);
    }

    [Fact]
    public async Task A_finished_single_file_with_no_hand_off_reads_not_handed_over_after_ten_minutes_whatever_the_client_calls_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var dispatches = (await app.Services.GetRequiredService<IJobQueueRepository>().ListDownloadDispatchesAsync(50, null, CancellationToken.None, includeArchived: true))
            .Select(dispatch => dispatch with { ClientCompletedUtc = DateTimeOffset.UtcNow.AddMinutes(-30) })
            .ToArray();
        var libraries = await app.Services.GetRequiredService<ILibrariesRepository>().ListLibrariesAsync(CancellationToken.None);
        var snapshot = new DownloadClientTelemetrySnapshot(
            Client, "qBittorrent", "qbittorrent", "http://127.0.0.1:8080", "healthy", null,
            new DownloadClientTelemetryCapabilities(true, true, true, true, false, true, "password"),
            new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0), [scene.Item with { LibraryId = scene.Library.Id }], [], DateTimeOffset.UtcNow);

        var enriched = DownloadClientTelemetryService.EnrichQueueImportState(
            snapshot, libraries, dispatches, dispatches, [], [],
            new Dictionary<string, IReadOnlyDictionary<string, string>>(), null);

        Assert.Equal(DownloadQueueStatuses.NotHandedOver, Assert.Single(enriched.Queue).Status);
    }
}
