using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Movies.Data;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Support;

/// <summary>
/// The set-up the route tests share: a real library folder with a real video file in it, a film or a
/// show recorded against it through the catalogue, and direct reads of the real SQLite files so a
/// test can see what is stored and not only what a route answered.
/// </summary>
internal static class ApiScenes
{
    public sealed record Film(string LibraryId, string MovieId, string FilePath, string FolderPath);

    public sealed record Show(string LibraryId, string SeriesId, string EpisodeId, string FilePath, string FolderPath);

    public static async Task<string> CreateLibraryAsync(ApplicationTestHost app, string mediaType, string? folder = null, string? name = null)
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, folder ?? (mediaType == "tv" ? "Shows" : "Films"))).FullName;
        var response = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name = name ?? (mediaType == "tv" ? "Shows" : "Films"),
            mediaType,
            purpose = "collection",
            rootPath = root,
            importWorkflow = "copy",
            autoSearchEnabled = false,
            missingSearchEnabled = false,
            upgradeSearchEnabled = false
        });
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"POST /api/libraries/ returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    public static async Task<string> LibraryRootAsync(ApplicationTestHost app, string libraryId)
        => (await app.Client.GetFromJsonAsync<JsonElement>("/api/libraries")).EnumerateArray()
            .First(library => library.GetProperty("id").GetString() == libraryId).GetProperty("rootPath").GetString()!;

    public static async Task<Film> FilmOnDiskAsync(ApplicationTestHost app, string title = "Dune", int year = 2021, string? libraryId = null, string quality = "Bluray-1080p")
    {
        libraryId ??= await CreateLibraryAsync(app, "movies");
        var root = await LibraryRootAsync(app, libraryId);
        var folder = Path.Combine(root, $"{title} ({year})");
        var filePath = Path.Combine(folder, $"{title} ({year}) [{quality}].mkv");
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(filePath, [1, 2, 3, 4]);
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        await movies.ImportExistingAsync(
            libraryId, title, year,
            wantedStatus: "covered",
            wantedReason: "Imported from your existing library.",
            currentQuality: quality,
            targetQuality: quality,
            qualityCutoffMet: true,
            filePath: filePath,
            fileSizeBytes: 4,
            cancellationToken: CancellationToken.None);
        var movieId = (await movies.ListAsync(CancellationToken.None)).Single(movie => movie.Title == title).Id;
        return new Film(libraryId, movieId, filePath, folder);
    }

    public static async Task<Show> ShowOnDiskAsync(ApplicationTestHost app, string title = "The Wire", int year = 2002, string? libraryId = null)
    {
        libraryId ??= await CreateLibraryAsync(app, "tv");
        var root = await LibraryRootAsync(app, libraryId);
        var folder = Path.Combine(root, title);
        var filePath = Path.Combine(folder, "Season 01", $"{title} S01E01.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllBytesAsync(filePath, [1, 2, 3, 4]);
        var series = app.Services.GetRequiredService<ISeriesCatalogRepository>();
        await series.ImportExistingAsync(
            libraryId, title, year, "covered", "Imported.", "Bluray-1080p", "Bluray-1080p", true, null, null,
            [new ImportedEpisodeItem(1, 1, HasFile: true, FilePath: filePath, FileSizeBytes: 4)],
            CancellationToken.None);
        var seriesId = (await series.ListAsync(CancellationToken.None)).Single(item => item.Title == title).Id;
        var episodeId = (await series.GetInventoryDetailAsync(seriesId, CancellationToken.None))!.Episodes.Single().EpisodeId;
        return new Show(libraryId, seriesId, episodeId, filePath, folder);
    }

    public static async Task<long> ScalarAsync(ApplicationTestHost app, string database, string sql)
    {
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = (SqliteConnection)await factory.OpenConnectionAsync(database, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    public static async Task<string?> TextAsync(ApplicationTestHost app, string database, string sql)
    {
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = (SqliteConnection)await factory.OpenConnectionAsync(database, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    public static async Task ExecuteAsync(ApplicationTestHost app, string database, string sql)
    {
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = (SqliteConnection)await factory.OpenConnectionAsync(database, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
