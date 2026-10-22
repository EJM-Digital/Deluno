using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Filesystem;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Connections.Data;
using Deluno.Connections.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Api;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Platform.Contracts;
using Deluno.Series.Data;
using Deluno.Worker.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// Which library a finished download is imported into, and what the import job says, on the real stores: real library
/// folders and a real finished file on disk, the real library, catalogue, dispatch, job and activity stores in a real
/// SQLite file, and the real import planner. Only the download client's answer is a stand-in (the recorded-reply tests
/// of each client cover what a client says). Refs #1296.
/// </summary>
public sealed class TheFinishedItemsLibraryAndImportJobOnRealStoresTests
{
    private const string Release = "Dune.2021.1080p.WEB-DL.DDP5.1.H.264-DELUNO";

    [Fact]
    public async Task The_library_the_grab_was_made_for_wins_over_a_route_category_and_a_downloads_folder_that_hold_the_file()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var a = await LibraryAsync(app, "Films A", "movies");
        var b = await LibraryAsync(app, "Films B", "movies");
        var client = await ClientAsync(app);
        await RouteAsync(app, a.Id, client, "movies-a");

        var run = await RunAsync(app, client, Item(client, a, category: "movies-a", assignedLibraryId: b.Id));

        Assert.Equal(b.Id, run.Request.Preview.LibraryId);
        Assert.True(File.Exists(run.Request.Preview.SourcePath), "planning leaves the finished file where it is");
    }

    [Fact]
    public async Task With_no_assigned_library_the_library_whose_route_category_for_that_client_matches_gets_the_file()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var a = await LibraryAsync(app, "Films A", "movies");
        var b = await LibraryAsync(app, "Films B", "movies");
        var client = await ClientAsync(app);
        await RouteAsync(app, b.Id, client, "Movies-B");

        var run = await RunAsync(app, client, Item(client, a, category: "movies-b"));

        Assert.Equal(b.Id, run.Request.Preview.LibraryId);
        Assert.True(File.Exists(run.Request.Preview.SourcePath), "planning leaves the finished file where it is");
    }

    [Fact]
    public async Task Of_two_libraries_with_the_same_route_category_the_one_whose_downloads_folder_holds_the_file_gets_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var a = await LibraryAsync(app, "Films A", "movies");
        var b = await LibraryAsync(app, "Films B", "movies");
        var client = await ClientAsync(app);
        await RouteAsync(app, a.Id, client, "movies");
        await RouteAsync(app, b.Id, client, "movies");

        var inB = await RunAsync(app, client, Item(client, b, category: "movies"));
        Assert.Equal(b.Id, inB.Request.Preview.LibraryId);
        Assert.True(File.Exists(inB.Request.Preview.SourcePath), "planning leaves the finished file where it is");
    }

    [Fact]
    public async Task Of_two_libraries_with_the_same_route_category_and_no_downloads_folder_match_the_first_listed_gets_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var a = await LibraryAsync(app, "Films A", "movies");
        var b = await LibraryAsync(app, "Films B", "movies");
        var client = await ClientAsync(app);
        await RouteAsync(app, a.Id, client, "movies");
        await RouteAsync(app, b.Id, client, "movies");
        var elsewhere = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Elsewhere")).FullName;

        var run = await RunAsync(app, client, Item(client, elsewhere, category: "movies"));

        var first = (await app.Services.GetRequiredService<ILibrariesRepository>().ListLibrariesAsync(CancellationToken.None))
            .First(library => library.Id == a.Id || library.Id == b.Id);
        Assert.Equal(first.Id, run.Request.Preview.LibraryId);
        Assert.True(File.Exists(run.Request.Preview.SourcePath), "planning leaves the finished file where it is");
    }

    [Fact]
    public async Task With_no_route_match_the_library_of_the_type_whose_downloads_folder_holds_the_file_gets_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var a = await LibraryAsync(app, "Films A", "movies");
        var b = await LibraryAsync(app, "Films B", "movies");
        var client = await ClientAsync(app);
        await RouteAsync(app, a.Id, client, "something-else");

        var run = await RunAsync(app, client, Item(client, b, category: "movies"));

        Assert.Equal(b.Id, run.Request.Preview.LibraryId);
        Assert.True(File.Exists(run.Request.Preview.SourcePath), "planning leaves the finished file where it is");
    }

    [Fact]
    public async Task With_nothing_else_to_go_on_the_first_library_of_the_media_type_gets_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var films = await LibraryAsync(app, "Films", "movies");
        var shows = await LibraryAsync(app, "Shows", "tv");
        var client = await ClientAsync(app);
        var elsewhere = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Elsewhere")).FullName;

        var film = await RunAsync(app, client, Item(client, elsewhere, category: ""));
        Assert.Equal(films.Id, film.Request.Preview.LibraryId);
        Assert.True(File.Exists(film.Request.Preview.SourcePath), "planning leaves the finished file where it is");
    }

    [Fact]
    public async Task A_show_with_nothing_else_to_go_on_goes_to_the_first_tv_library()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await LibraryAsync(app, "Films", "movies");
        var shows = await LibraryAsync(app, "Shows", "tv");
        var client = await ClientAsync(app);
        var elsewhere = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Elsewhere")).FullName;

        var show = await RunAsync(app, client, Item(client, elsewhere, category: "", mediaType: "tv", name: "The.Wire.S01E01.1080p.WEB-DL-DELUNO"));
        Assert.Equal(shows.Id, show.Request.Preview.LibraryId);
        Assert.Equal("series", show.Job.RelatedEntityType);
        Assert.True(File.Exists(show.Request.Preview.SourcePath), "planning leaves the finished file where it is");
    }

    [Fact]
    public async Task A_film_import_is_queued_with_the_catalogues_details_auto_transfer_copy_fallback_and_no_overwrite_without_authorisation()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021);
        var library = (await app.Services.GetRequiredService<ILibrariesRepository>().ListLibrariesAsync(CancellationToken.None)).Single(item => item.Id == film.LibraryId);
        var downloads = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Downloads")).FullName;
        var client = await ClientAsync(app);
        var item = Item(client, downloads, category: "movies", assignedLibraryId: library.Id);
        await DispatchAsync(app, library, client, item, film.MovieId);

        var run = await RunAsync(app, client, item);

        var preview = run.Request.Preview;
        Assert.Equal("Dune", preview.Title);
        Assert.Equal(2021, preview.Year);
        Assert.Equal(library.Id, preview.LibraryId);
        Assert.Equal(item.SourcePath, preview.SourcePath);
        Assert.Equal("auto", run.Request.TransferMode);
        Assert.True(run.Request.AllowCopyFallback);
        Assert.False(run.Request.Overwrite);
        Assert.False(run.Request.ForceReplacement);
        Assert.NotNull(run.Request.DispatchId);
        Assert.Null(run.Request.ExpectedExistingPath);
        Assert.Equal("filesystem.import.execute", run.Job.JobType);
        Assert.Equal("download-client", run.Job.Source);
        Assert.Equal("movie", run.Job.RelatedEntityType);
        Assert.Equal(film.MovieId, run.Job.RelatedEntityId);
        var said = Assert.Single(run.Activity, entry => entry.Category == "filesystem.import.auto-queued");
        Assert.Contains($"Deluno queued it for import into {library.Name}.", said.Message);
        Assert.Equal(run.Job.Id, said.RelatedJobId);
        Assert.True(File.Exists(run.Request.Preview.SourcePath), "planning leaves the finished file where it is");
    }

    [Fact]
    public async Task A_replacement_the_grab_authorised_sets_overwrite_and_carries_the_file_it_replaces_and_a_forced_one_says_so()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021);
        var library = (await app.Services.GetRequiredService<ILibrariesRepository>().ListLibrariesAsync(CancellationToken.None)).Single(item => item.Id == film.LibraryId);
        var downloads = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Downloads")).FullName;
        var client = await ClientAsync(app);
        var item = Item(client, downloads, category: "movies", assignedLibraryId: library.Id);
        await DispatchAsync(app, library, client, item, film.MovieId, replacementAuthorized: true, force: true, expectedPath: film.FilePath);

        var run = await RunAsync(app, client, item);

        Assert.True(run.Request.Overwrite);
        Assert.True(run.Request.ForceReplacement);
        Assert.Equal(film.FilePath, run.Request.ExpectedExistingPath);
        Assert.True(File.Exists(run.Request.Preview.SourcePath), "planning leaves the finished file where it is");
    }

    [Fact]
    public async Task Two_cleaned_videos_in_one_hand_off_folder_queue_no_import_and_one_queues_that_file()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (library, output) = await RefineLibraryAsync(app);
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var folder = Directory.CreateDirectory(Path.Combine(output, Release)).FullName;
        await CleanedVideoAsync(folder, Release + ".mkv");
        await CleanedVideoAsync(folder, "Sample.mkv");

        await PlanAsync(app, null, null);

        Assert.DoesNotContain(await jobs.ListAsync(100, CancellationToken.None), job => job.JobType == "filesystem.import.execute");
    }

    [Fact]
    public async Task One_cleaned_video_in_the_hand_off_folder_is_imported_and_it_is_that_file()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (library, output) = await RefineLibraryAsync(app);
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var folder = Directory.CreateDirectory(Path.Combine(output, Release)).FullName;
        await CleanedVideoAsync(folder, Release + ".mkv");

        await PlanAsync(app, null, null);

        var job = Assert.Single(await jobs.ListAsync(100, CancellationToken.None), candidate => candidate.JobType == "filesystem.import.execute");
        Assert.Contains(Release + ".mkv", job.PayloadJson!.Replace("\\\\", "/"), StringComparison.Ordinal);
    }

    private static async Task CleanedVideoAsync(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        await File.WriteAllBytesAsync(path, new byte[2048]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-30));
    }

    /// <summary>A refine-before-import library with a waiting hand-off for the release, its download folder and a ready folder.</summary>
    private static async Task<(LibraryItem Library, string Output)> RefineLibraryAsync(ApplicationTestHost app)
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var downloads = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Completed")).FullName;
        var output = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Ready")).FullName;
        var source = Directory.CreateDirectory(Path.Combine(downloads, Release)).FullName;
        await File.WriteAllBytesAsync(Path.Combine(source, Release + ".mkv"), new byte[64]);
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
        var processors = app.Services.GetRequiredService<IProcessorRepository>();
        await processors.EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(library.Id, "movies", "client-1", $"hash-{Release}", Release, source, "Weir", DateTimeOffset.UnixEpoch),
            CancellationToken.None);
        return (library, output);
    }

    private sealed record Outcome(ImportExecuteRequest Request, JobQueueItem Job, IReadOnlyList<Deluno.Jobs.Contracts.ActivityEventItem> Activity);

    private sealed record Lib(string Id, string Name, string Downloads);

    private static async Task<Lib> LibraryAsync(ApplicationTestHost app, string name, string mediaType)
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, name.Replace(' ', '-'))).FullName;
        var downloads = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Downloads-" + name.Replace(' ', '-'))).FullName;
        var response = await app.Client.PostAsJsonAsync("/api/libraries", new { name, mediaType, rootPath = root, downloadsPath = downloads });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var library = (await response.Content.ReadFromJsonAsync<LibraryItem>())!;
        return new Lib(library.Id, name, downloads);
    }

    private static async Task<string> ClientAsync(ApplicationTestHost app)
        => (await app.Services.GetRequiredService<IConnectionsRepository>().CreateDownloadClientAsync(
            new CreateDownloadClientRequest("Fixture client", "qbittorrent", "localhost", 8080, null, null, null, "movies", "tv", null, 1, true),
            CancellationToken.None)).Id;

    private static async Task RouteAsync(ApplicationTestHost app, string libraryId, string clientId, string category)
    {
        var routed = await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/routing", new
        {
            sources = Array.Empty<object>(),
            downloadClients = new[] { new { downloadClientId = clientId, priority = 1, category } }
        });
        Assert.True(routed.IsSuccessStatusCode, await routed.Content.ReadAsStringAsync());
    }

    private static DownloadQueueItem Item(string clientId, Lib holdsTheFile, string category, string? assignedLibraryId = null, string mediaType = "movies", string name = Release)
        => Item(clientId, holdsTheFile.Downloads, category, assignedLibraryId, mediaType, name);

    /// <summary>A finished download: a real file in a real folder, reported by the (stand-in) client as completed.</summary>
    private static DownloadQueueItem Item(string clientId, string folder, string category, string? assignedLibraryId = null, string mediaType = "movies", string name = Release)
    {
        var file = Path.Combine(folder, name, name + ".mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, new byte[64]);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-1));
        return new DownloadQueueItem(
            $"hash-{name}", clientId, "Fixture client", "qbittorrent", mediaType, name.Replace('.', ' '), name,
            category, DownloadQueueStatuses.Completed, 100, 0, 0, 100, 100, 0, "Fixture indexer", null,
            DateTimeOffset.UtcNow.AddHours(-2), file, assignedLibraryId);
    }

    private static async Task DispatchAsync(
        ApplicationTestHost app, LibraryItem library, string clientId, DownloadQueueItem item, string movieId,
        bool replacementAuthorized = false, bool force = false, string? expectedPath = null)
        => await app.Services.GetRequiredService<IJobQueueRepository>().RecordDownloadDispatchAsync(
            library.Id, "movies", "movie", movieId, item.ReleaseName, "Fixture indexer",
            clientId, "Fixture client", "sent", null, clientExternalId: item.Id, cancellationToken: CancellationToken.None,
            replacementAuthorized: replacementAuthorized, forceReplacementAuthorized: force, replacementExpectedPath: expectedPath);

    private static async Task<Outcome> RunAsync(ApplicationTestHost app, string clientId, DownloadQueueItem item)
    {
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var before = (await jobs.ListAsync(500, CancellationToken.None)).Select(job => job.Id).ToHashSet();
        await PlanAsync(app, clientId, item);
        var job = Assert.Single(await jobs.ListAsync(500, CancellationToken.None), candidate => !before.Contains(candidate.Id) && candidate.JobType == "filesystem.import.execute");
        var request = JsonSerializer.Deserialize<ImportExecuteRequest>(job.PayloadJson!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var activity = await app.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(100, null, null, CancellationToken.None);
        return new Outcome(request, job, activity);
    }

    /// <summary>One import-planning pass over the real stores; the client (when there is one) reports the item as finished.</summary>
    private static async Task PlanAsync(ApplicationTestHost app, string? clientId, DownloadQueueItem? item)
    {
        var services = app.Services;
        var summary = new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0);
        var overview = new DownloadTelemetryOverview(
            summary,
            item is null || clientId is null ? [] : [new DownloadClientTelemetrySnapshot(
                clientId, "Fixture client", "qbittorrent", "http://127.0.0.1:8080", "healthy", null,
                new DownloadClientTelemetryCapabilities(true, true, true, true, false, true, "password"),
                summary, [item], [], DateTimeOffset.UtcNow)],
            DateTimeOffset.UtcNow);
        var telemetry = AHandOffFollowsTheDownloadItIsForTests.Stub.Create<IDownloadClientTelemetryService>((name, _) => name switch
        {
            "GetFreshOverviewAsync" => overview,
            "RunConfiguredHealthRemediationAsync" => new DownloadHealthRemediationReport(0, 0, 0, 0, 0, []),
            _ => throw new NotSupportedException(name)
        });
        var processors = AHandOffFollowsTheDownloadItIsForTests.Stub.Create<IProcessorConnectionService>((name, _) => throw new NotSupportedException(name));
        await new WorkPlanner(new RaisingLogger(), services.GetRequiredService<IJobQueueRepository>(), services.GetRequiredService<IConfiguration>(), TimeProvider.System)
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

    private sealed class RaisingLogger : Microsoft.Extensions.Logging.ILogger<WorkPlanner>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
            {
                throw new InvalidOperationException(formatter(state, exception), exception);
            }
        }
    }
}
