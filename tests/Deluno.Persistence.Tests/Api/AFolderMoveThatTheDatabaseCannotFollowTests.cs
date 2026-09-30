using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1067 / #1078: a folder has been moved on disk and the database then refuses to follow (the
/// "database is locked" case). The folder is put back, the reason is plain and recorded, and the
/// rest of the batch carries on. The refusal is injected with a trigger.
/// </summary>
public sealed class AFolderMoveThatTheDatabaseCannotFollowTests
{
    [Fact]
    public async Task A_movie_rename_whose_records_cannot_follow_is_put_back_and_the_batch_carries_on()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var libraryId = await CreateLibraryAsync(app, "movies", root, "Films");
        var dune = await AddMovieAsync(app, libraryId, "Dune", 2021, Path.Combine(root, "dune old", "Dune.mkv"));
        var arrival = await AddMovieAsync(app, libraryId, "Arrival", 2016, Path.Combine(root, "arrival old", "Arrival.mkv"));
        await Sql(app, DelunoDatabaseNames.Movies,
            "CREATE TRIGGER fail_dune BEFORE UPDATE OF file_path ON movie_wanted_state WHEN OLD.file_path LIKE '%dune old%' BEGIN SELECT RAISE(ABORT, 'database is locked'); END;");

        var apply = await PostAsync(app, "/api/movies/bulk/rename-apply", new { movieIds = new[] { dune, arrival } });

        Assert.Equal(1, apply.GetProperty("renamedCount").GetInt32());
        var failure = Assert.Single(apply.GetProperty("failures").EnumerateArray());
        Assert.Contains("put back", failure.GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(root, "dune old", "Dune.mkv")));
        Assert.False(Directory.Exists(Path.Combine(root, "arrival old")));
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        Assert.Equal(Path.Combine(root, "dune old", "Dune.mkv"), (await movies.GetByIdAsync(dune, default))!.FilePath);
        Assert.Contains("rename-records-failed", await ActivityText(app));
    }

    [Fact]
    public async Task A_show_rename_whose_records_cannot_follow_is_put_back_and_the_batch_carries_on()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Shows")).FullName;
        var libraryId = await CreateLibraryAsync(app, "tv", root, "Shows");
        var wire = await AddSeriesAsync(app, libraryId, "The Wire", 2002, Path.Combine(root, "the wire old", "Season 01", "The Wire S01E01.mkv"));
        var lost = await AddSeriesAsync(app, libraryId, "Lost", 2004, Path.Combine(root, "lost old", "Season 01", "Lost S01E01.mkv"));
        await Sql(app, DelunoDatabaseNames.Series,
            "CREATE TRIGGER fail_wire BEFORE UPDATE OF file_path ON episode_entries WHEN OLD.file_path LIKE '%the wire old%' BEGIN SELECT RAISE(ABORT, 'database is locked'); END;");

        var apply = await PostAsync(app, "/api/series/bulk/rename-apply", new { seriesIds = new[] { wire, lost } });

        Assert.Equal(1, apply.GetProperty("renamedCount").GetInt32());
        var failure = Assert.Single(apply.GetProperty("failures").EnumerateArray());
        Assert.Contains("put back", failure.GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(root, "the wire old", "Season 01", "The Wire S01E01.mkv")));
        Assert.False(Directory.Exists(Path.Combine(root, "lost old")));
        Assert.Contains("rename-records-failed", await ActivityText(app));
    }

    [Fact]
    public async Task A_reassign_whose_records_cannot_follow_the_folder_puts_it_back_and_the_batch_carries_on()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var from = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var to = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Kids")).FullName;
        var fromId = await CreateLibraryAsync(app, "movies", from, "Films");
        var toId = await CreateLibraryAsync(app, "movies", to, "Kids");
        var dune = await AddMovieAsync(app, fromId, "Dune", 2021, Path.Combine(from, "Dune (2021)", "Dune.mkv"));
        var arrival = await AddMovieAsync(app, fromId, "Arrival", 2016, Path.Combine(from, "Arrival (2016)", "Arrival.mkv"));
        await Sql(app, DelunoDatabaseNames.Movies,
            "CREATE TRIGGER fail_dune BEFORE UPDATE OF file_path ON movie_wanted_state WHEN OLD.file_path LIKE '%Dune (2021)%' AND NEW.file_path LIKE '%Kids%' BEGIN SELECT RAISE(ABORT, 'database is locked'); END;");

        var result = await PostAsync(app, "/api/movies/bulk/reassign-library",
            new { movieIds = new[] { dune, arrival }, fromLibraryId = fromId, toLibraryId = toId, moveFiles = true });

        Assert.Equal(1, result.GetProperty("reassigned").GetInt32());
        Assert.Contains("put back", string.Join(" ", result.GetProperty("moveFailureReasons").EnumerateArray().Select(item => item.GetString())), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(from, "Dune (2021)", "Dune.mkv")));
        Assert.True(File.Exists(Path.Combine(to, "Arrival (2016)", "Arrival.mkv")));
    }

    [Fact]
    public async Task A_reassign_whose_final_library_update_fails_puts_every_moved_folder_back_and_says_so()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var from = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var to = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Kids")).FullName;
        var fromId = await CreateLibraryAsync(app, "movies", from, "Films");
        var toId = await CreateLibraryAsync(app, "movies", to, "Kids");
        var dune = await AddMovieAsync(app, fromId, "Dune", 2021, Path.Combine(from, "Dune (2021)", "Dune.mkv"));
        await Sql(app, DelunoDatabaseNames.Movies,
            "CREATE TRIGGER fail_library BEFORE UPDATE OF library_id ON movie_wanted_state BEGIN SELECT RAISE(ABORT, 'database is locked'); END;");

        var result = await PostAsync(app, "/api/movies/bulk/reassign-library",
            new { movieIds = new[] { dune }, fromLibraryId = fromId, toLibraryId = toId, moveFiles = true });

        Assert.Equal(0, result.GetProperty("reassigned").GetInt32());
        Assert.True(File.Exists(Path.Combine(from, "Dune (2021)", "Dune.mkv")));
        Assert.False(Directory.Exists(Path.Combine(to, "Dune (2021)")));
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        Assert.Equal(Path.Combine(from, "Dune (2021)", "Dune.mkv"), (await movies.GetByIdAsync(dune, default))!.FilePath);
    }

    private static async Task<string> ActivityText(ApplicationTestHost app)
    {
        var response = await app.Client.GetAsync("/api/activity");
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task Sql(ApplicationTestHost app, string database, string sql)
    {
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = await factory.OpenConnectionAsync(database, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task<JsonElement> PostAsync(ApplicationTestHost app, string url, object body)
    {
        var response = await app.Client.PostAsJsonAsync(url, body);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<string> AddMovieAsync(ApplicationTestHost app, string libraryId, string title, int year, string filePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllBytesAsync(filePath, [1, 2, 3, 4]);
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        await movies.ImportExistingAsync(
            libraryId, title, year,
            wantedStatus: "covered",
            wantedReason: "Imported from your existing library.",
            currentQuality: "Bluray-1080p",
            targetQuality: "Bluray-1080p",
            qualityCutoffMet: true,
            filePath: filePath,
            fileSizeBytes: 4,
            cancellationToken: CancellationToken.None);
        return (await movies.ListAsync(CancellationToken.None)).Single(movie => movie.Title == title).Id;
    }

    private static async Task<string> AddSeriesAsync(ApplicationTestHost app, string libraryId, string title, int year, string episodePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(episodePath)!);
        await File.WriteAllBytesAsync(episodePath, [1, 2, 3, 4]);
        var series = app.Services.GetRequiredService<ISeriesCatalogRepository>();
        await series.ImportExistingAsync(
            libraryId, title, year,
            wantedStatus: "covered",
            wantedReason: "Imported from your existing library.",
            currentQuality: "Bluray-1080p",
            targetQuality: "Bluray-1080p",
            qualityCutoffMet: true,
            filePath: null,
            fileSizeBytes: null,
            episodes: [new ImportedEpisodeItem(1, 1, HasFile: true, FilePath: episodePath, FileSizeBytes: 4)],
            cancellationToken: CancellationToken.None);
        return (await series.ListAsync(CancellationToken.None)).Single(item => item.Title == title).Id;
    }

    private static async Task<string> CreateLibraryAsync(ApplicationTestHost app, string mediaType, string rootPath, string name)
    {
        var response = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name,
            mediaType,
            purpose = "collection",
            rootPath,
            downloadsPath = (string?)null,
            qualityProfileId = (string?)null,
            importWorkflow = "copy",
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
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }
}
