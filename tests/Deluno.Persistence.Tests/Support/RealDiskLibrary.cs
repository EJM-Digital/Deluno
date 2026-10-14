using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Filesystem;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Support;

/// <summary>
/// A real Deluno host over a real temp folder, with a movie library and a downloads folder on the same disk.
/// Every file the tests below touch is a real file; the only stand-in is the media probe (a script of what
/// ffprobe would say), because the bytes written are not real video.
/// </summary>
internal sealed class RealDiskLibrary : IAsyncDisposable
{
    private RealDiskLibrary(ApplicationTestHost app, LibraryItem library, string root, string downloads)
    {
        App = app;
        Library = library;
        Root = root;
        Downloads = downloads;
    }

    public ApplicationTestHost App { get; }

    public LibraryItem Library { get; }

    /// <summary>The movie library root.</summary>
    public string Root { get; }

    /// <summary>The downloads folder, on the same disk as the library.</summary>
    public string Downloads { get; }

    /// <summary>The one thing swapped in a real host: what the media probe says (the bytes written are not real video).</summary>
    public static Action<IServiceCollection> UseProbe(IMediaProbeService? probe) => services =>
    {
        if (probe is null) return;
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(IMediaProbeService)) services.RemoveAt(i);
        }

        services.AddSingleton(probe);
    };

    /// <summary>Adds a movie library and a downloads folder, on the same disk, to a host the test started on a real temp folder.</summary>
    public static async Task<RealDiskLibrary> OpenAsync(ApplicationTestHost app)
    {
        var root = Path.Combine(app.DataRoot, "movies");
        var downloads = Path.Combine(app.DataRoot, "downloads");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(downloads);
        var response = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name = "Movies",
            mediaType = "movies",
            purpose = "collection",
            rootPath = root,
            downloadsPath = downloads,
            qualityProfileId = (string?)null,
            importWorkflow = "standard",
            processorName = (string?)null,
            processorOutputPath = (string?)null,
            processorTimeoutMinutes = (int?)null,
            processorFailureMode = (string?)null,
            autoSearchEnabled = false,
            missingSearchEnabled = false,
            upgradeSearchEnabled = false,
            searchIntervalMinutes = (int?)null,
            retryDelayHours = (int?)null,
            maxItemsPerRun = (int?)null
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var libraries = app.Services.GetRequiredService<ILibrariesRepository>();
        var library = (await libraries.ListLibrariesAsync(CancellationToken.None)).Single();
        return new RealDiskLibrary(app, library, root, downloads);
    }

    public IRecycleBinService Bin => App.Services.GetRequiredService<IRecycleBinService>();

    public IMovieCatalogRepository Movies => App.Services.GetRequiredService<IMovieCatalogRepository>();

    /// <summary>Writes a finished download that is old enough for the import to trust.</summary>
    public string WriteDownload(string name, byte fill = 0x11, int length = 4096)
    {
        var path = Path.Combine(Downloads, name);
        File.WriteAllBytes(path, Enumerable.Repeat(fill, length).ToArray());
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        return path;
    }

    public async Task<MovieListItem> AddMovieAsync(string title, int year)
        => await Movies.AddAsync(new CreateMovieRequest(title, year, null), CancellationToken.None);

    public async Task<string> RecordDispatchAsync(string movieId, string releaseName)
    {
        var jobs = App.Services.GetRequiredService<IJobQueueRepository>();
        return await jobs.RecordDownloadDispatchAsync(
            libraryId: Library.Id, mediaType: "movies", entityType: "movie", entityId: movieId,
            releaseName: releaseName, indexerName: "Indexer", downloadClientId: "client-1", downloadClientName: "Client",
            status: "sent", notesJson: null, cancellationToken: CancellationToken.None);
    }

    public static ImportExecuteRequest Request(
        string source,
        string title,
        int year,
        string mode = "copy",
        bool overwrite = false,
        bool allowCopyFallback = true,
        bool forceReplacement = false,
        string? dispatchId = null,
        string? expectedExistingPath = null)
        => new(
            new ImportPreviewRequest(
                SourcePath: source, FileName: null, MediaType: "movies",
                Title: title, Year: year, Genres: [], Tags: [], Studio: null, OriginalLanguage: null),
            mode, overwrite, allowCopyFallback, forceReplacement, dispatchId, expectedExistingPath);

    public async Task<ImportPipelineResult> ImportAsync(ImportExecuteRequest request)
    {
        using var scope = App.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IImportPipelineService>().ExecuteAsync(request, CancellationToken.None);
    }

    public async Task<ImportPreviewResponse> PreviewAsync(ImportPreviewRequest request)
    {
        using var scope = App.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IImportPipelineService>().PreviewAsync(request, CancellationToken.None);
    }

    public async Task CreateRuleAsync(string name, string matchKind, string matchValue, string rootPath, int priority, bool enabled = true)
        => await App.Services.GetRequiredService<ILibrariesRepository>().CreateDestinationRuleAsync(
            new CreateDestinationRuleRequest(name, "movies", matchKind, matchValue, rootPath, null, priority, enabled),
            CancellationToken.None);

    /// <summary>The first import of a film, by copy, so the title holds a real tracked file.</summary>
    public async Task<(MovieListItem Movie, string LibraryFile)> ImportFirstCopyAsync(string title, int year, string release, byte fill = 0x11)
    {
        var movie = await AddMovieAsync(title, year);
        var source = WriteDownload($"{release}.mkv", fill);
        var dispatch = await RecordDispatchAsync(movie.Id, release.Replace('.', ' '));
        var result = await ImportAsync(Request(source, title, year, dispatchId: dispatch));
        Assert.True(result.Succeeded, result.Message);
        return (movie, result.Response!.Preview.DestinationPath);
    }

    /// <summary>Runs SQL on one of Deluno's real SQLite files (for example to make one catalogue write fail the way a full disk would).</summary>
    public async Task SqlAsync(string database, string sql)
    {
        var factory = App.Services.GetRequiredService<Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory>();
        await using var connection = await factory.OpenConnectionAsync(database, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public static string[] FilesUnder(string folder)
        => Directory.Exists(folder) ? Directory.GetFiles(folder, "*", SearchOption.AllDirectories) : [];

    public async ValueTask DisposeAsync() => await App.DisposeAsync();
}

/// <summary>What ffprobe would say about the file, set per test (the bytes written are not real video).</summary>
internal sealed class ScriptedProbe(Func<string, MediaProbeInfo> answer) : IMediaProbeService
{
    public Task<MediaProbeInfo> ProbeAsync(string path, CancellationToken cancellationToken) => Task.FromResult(answer(path));

    /// <summary>Answers one thing for a file in the downloads folder and another for a file in the library.</summary>
    public static ScriptedProbe ByFolder(Func<MediaProbeInfo> incoming, Func<MediaProbeInfo> existing)
        => new(path => path.Contains(Path.DirectorySeparatorChar + "downloads" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? incoming() : existing());

    public static MediaProbeInfo Good(int width = 1920, int height = 1080, double seconds = 7200, long bitrate = 12_000_000)
        => new(
            Status: "succeeded", Tool: "test", Message: null, DurationSeconds: seconds, Container: "matroska", Bitrate: bitrate,
            VideoStreams: [new MediaVideoStreamInfo(0, "h264", "High", width, height, "yuv420p", 23.976, bitrate - 2_000_000, "eng")],
            AudioStreams: [], SubtitleStreams: []);

    public static MediaProbeInfo Failed(string message)
        => new("failed", "test", message, null, null, null, [], [], []);
}
