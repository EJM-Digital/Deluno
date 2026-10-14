using System.Text;
using System.Text.Json.Nodes;
using Deluno.Filesystem;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform;
using Deluno.Platform.Data;
using Deluno.Series.Data;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>
/// The library file check on a real temp folder, a real recycle bin and real SQLite databases: a film file that Deluno itself moved
/// to its recycle bin was not removed outside Deluno, so it is not flagged and its title stays wanted; a file that is gone for any
/// other reason is flagged; and "clean up" never touches an ordinary media file, only Deluno's own staging leftovers.
/// </summary>
public sealed class ReconciliationLeavesWhatDelunoDidItselfAloneTests
{
    [Fact]
    public async Task A_film_file_Deluno_moved_to_the_recycle_bin_is_not_flagged_missing_while_one_deleted_by_hand_is()
    {
        using var storage = TestStorage.Create();
        var world = await WorldAsync(storage);
        var recycledFilm = await world.AddFilmAsync("Recycled Film", 2020);
        var deletedFilm = await world.AddFilmAsync("Deleted Film", 2021);
        await world.AddFilmAsync("Untouched Film One", 2022);
        await world.AddFilmAsync("Untouched Film Two", 2023);
        await world.AddFilmAsync("Untouched Film Three", 2024);

        var moved = await world.Bin.StoreRemovedFileAsync(world.Library, recycledFilm.Path, null, CancellationToken.None);
        File.Delete(deletedFilm.Path);
        var report = await world.Service.ScanAsync(CancellationToken.None);

        Assert.NotNull(moved);
        Assert.False(File.Exists(recycledFilm.Path));
        var flagged = report.Issues.Where(issue => issue.Kind == "missingTrackedFile").ToArray();
        Assert.Equal([deletedFilm.Path], flagged.Select(issue => issue.Path).ToArray());
    }

    [Fact]
    public async Task Marking_a_film_missing_is_refused_for_a_file_Deluno_moved_to_its_bin_and_the_title_stays_monitored()
    {
        using var storage = TestStorage.Create();
        var world = await WorldAsync(storage);
        var recycledFilm = await world.AddFilmAsync("Recycled Film", 2020);
        var deletedFilm = await world.AddFilmAsync("Deleted Film", 2021);
        await world.AddFilmAsync("Untouched Film One", 2022);
        await world.AddFilmAsync("Untouched Film Two", 2023);
        await world.AddFilmAsync("Untouched Film Three", 2024);
        await world.Bin.StoreRemovedFileAsync(world.Library, recycledFilm.Path, null, CancellationToken.None);
        File.Delete(deletedFilm.Path);
        var report = await world.Service.ScanAsync(CancellationToken.None);
        var elsewhere = Assert.Single(report.Issues, issue => issue.Kind == "missingTrackedFile");

        // The same repair, pointed at the file that went to the bin instead of the one deleted by hand.
        var result = await world.Service.RepairAsync(
            new FilesystemReconciliationRepairRequest(Retarget(elsewhere.Id, recycledFilm.Path, recycledFilm.Id), "mark-missing"),
            CancellationToken.None);

        Assert.False(result.Repaired);
        Assert.Contains("recycle bin itself", result.Message);
        Assert.True((await world.Movies.GetByIdAsync(recycledFilm.Id, CancellationToken.None))!.Monitored);
        Assert.True((await world.Movies.GetByIdAsync(recycledFilm.Id, CancellationToken.None))!.HasFile);
    }

    [Fact]
    public async Task Cleaning_up_refuses_an_ordinary_media_file_and_leaves_it_on_disk()
    {
        using var storage = TestStorage.Create();
        var world = await WorldAsync(storage);
        var film = await world.AddFilmAsync("Ordinary Film", 2020);
        var artifact = Path.Combine(world.Root, "Other.2024.mkv.deluno-stage-test.tmp");
        await File.WriteAllBytesAsync(artifact, new byte[2048]);
        var report = await world.Service.ScanAsync(CancellationToken.None);
        var leftover = Assert.Single(report.Issues, issue => issue.Kind == "partialImportArtifact");

        var result = await world.Service.RepairAsync(
            new FilesystemReconciliationRepairRequest(Retarget(leftover.Id, film.Path, film.Id), "cleanup-artifact"),
            CancellationToken.None);

        Assert.False(result.Repaired);
        Assert.Contains("not a Deluno staging artifact", result.Message);
        Assert.True(File.Exists(film.Path));
        Assert.Empty(await world.Bin.ListAsync(CancellationToken.None));
    }

    /// <summary>The issue's token with its path (and title) pointed somewhere else: what a stale or hand-made request looks like.</summary>
    private static string Retarget(string token, string path, string entityId)
    {
        var json = JsonNode.Parse(Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(token)))!.AsObject();
        foreach (var name in json.Select(pair => pair.Key).ToArray())
        {
            if (name.Equals("path", StringComparison.OrdinalIgnoreCase))
            {
                json[name] = path;
            }
            else if (name.Equals("entityId", StringComparison.OrdinalIgnoreCase))
            {
                json[name] = entityId;
            }
        }

        return WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(json.ToJsonString()));
    }

    private static async Task<World> WorldAsync(TestStorage storage)
    {
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-14T08:00:00Z"));
        var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
        await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new MoviesSchemaInitializer(storage.Factory, migrator, NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new SeriesSchemaInitializer(storage.Factory, migrator, NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var root = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(root);
        var libraries = new SqliteLibrariesRepository(storage.Factory, time);
        var library = await libraries.CreateLibraryAsync(
            new CreateLibraryRequest(
                Name: "Movies", MediaType: "movies", Purpose: "Main", RootPath: root, DownloadsPath: null,
                QualityProfileId: null, ImportWorkflow: "standard", ProcessorName: null, ProcessorOutputPath: null,
                ProcessorTimeoutMinutes: null, ProcessorFailureMode: null, AutoSearchEnabled: false,
                MissingSearchEnabled: false, UpgradeSearchEnabled: false, SearchIntervalMinutes: 6, RetryDelayHours: 24,
                MaxItemsPerRun: 25),
            CancellationToken.None);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, time);
        var jobs = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var bin = new RecycleBinService(Options.Create(new StoragePathOptions { DataRoot = storage.DataRoot }), time, NullLogger<RecycleBinService>.Instance);
        var service = new FilesystemReconciliationService(
            libraries, movies, new SqliteSeriesCatalogRepository(storage.Factory, time), jobs, time, null, bin);
        return new World(service, movies, bin, library, root);
    }

    private sealed record Film(string Id, string Path);

    private sealed class World(FilesystemReconciliationService service, SqliteMovieCatalogRepository movies, RecycleBinService bin, LibraryItem library, string root)
    {
        public FilesystemReconciliationService Service => service;

        public SqliteMovieCatalogRepository Movies => movies;

        public RecycleBinService Bin => bin;

        public LibraryItem Library => library;

        public string Root => root;

        public async Task<Film> AddFilmAsync(string title, int year)
        {
            var folder = Path.Combine(root, $"{title} ({year})");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"{title} ({year}) - 1080p.mkv");
            await File.WriteAllBytesAsync(path, Enumerable.Range(0, 4096).Select(value => (byte)(value % 200)).ToArray());
            await movies.ImportExistingAsync(
                library.Id, title, year,
                wantedStatus: "covered", wantedReason: "Imported", currentQuality: "WEB 1080p", targetQuality: "WEB 1080p",
                qualityCutoffMet: true, filePath: path, fileSizeBytes: 4096, cancellationToken: CancellationToken.None);
            var id = (await movies.ListAsync(CancellationToken.None)).First(item => item.Title == title).Id;
            return new Film(id, path);
        }
    }
}
