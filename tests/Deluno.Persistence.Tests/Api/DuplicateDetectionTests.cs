using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Infrastructure.Storage;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Finding the same film twice.
///
/// <para><b>Why this file exists.</b> #419. The lab held two Big Buck Bunny
/// entries and two Sintel entries, and <c>GET /api/movies/duplicates</c>
/// returned <c>[]</c>. It grouped by movie id — <c>HAVING COUNT(DISTINCT
/// library_id) &gt; 1</c> — so it answered "one row in two libraries", and two
/// rows for one film have two ids. The single feature named "duplicates" could
/// not, by construction, find the duplicates people actually get.</para>
///
/// <para>Both kinds are worth reporting and they are different problems: one
/// film in two libraries is usually deliberate, the same film twice is always
/// wrong.</para>
/// </summary>
public sealed class DuplicateDetectionTests
{
    [Fact]
    public async Task Two_rows_for_one_film_are_found_by_title_and_year_when_neither_has_an_id()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await CreateLibraryAsync(app);
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();

        // The exact shape the lab produced: a real film added by search, and a
        // phantom named after the release it came from.
        await ImportAsync(app, movies, libraryId, "Big Buck Bunny", 2008);
        await ImportAsync(app, movies, libraryId, "Big.Buck.Bunny.2008.2160p.WEB-DL.x265-DELUNO", 2008);

        var report = await ReadDuplicatesAsync(app);
        var groups = report.GetProperty("sameFilmTwice");

        var group = Assert.Single(groups.EnumerateArray());
        Assert.Equal("title-and-year", group.GetProperty("matchedOn").GetString());
        Assert.Equal(2, group.GetProperty("entries").GetArrayLength());
    }

    /// <summary>
    /// Two rows cannot share an IMDb id, so a duplicate always involves at least
    /// one row that has none — which is what a phantom entry is.
    ///
    /// <para>Worth holding, because it is the reason this detection matches on
    /// title and year rather than on ids: matching on ids would be looking for
    /// something the schema forbids.</para>
    /// </summary>
    [Fact]
    public async Task Two_rows_cannot_share_an_imdb_id_so_matching_is_by_name()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await CreateLibraryAsync(app);
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();

        await ImportAsync(app, movies, libraryId, "Arrival", 2016, "tt2543164");
        await ImportAsync(app, movies, libraryId, "Sintel", 2010);

        var second = (await movies.ListAsync(CancellationToken.None)).First(item => item.Title == "Sintel");

        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
            () => SetImdbIdAsync(app, second.Id, "tt2543164"));
    }

    [Fact]
    public async Task A_library_with_no_duplicates_reports_none()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await CreateLibraryAsync(app);
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();

        await ImportAsync(app, movies, libraryId, "Arrival", 2016, "tt2543164");
        await ImportAsync(app, movies, libraryId, "Sintel", 2010, "tt1727587");

        var report = await ReadDuplicatesAsync(app);

        Assert.Empty(report.GetProperty("sameFilmTwice").EnumerateArray());
        Assert.Empty(report.GetProperty("sameFilmInTwoLibraries").EnumerateArray());
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<JsonElement> ReadDuplicatesAsync(ApplicationTestHost app)
    {
        var response = await app.Client.GetAsync("/api/movies/duplicates");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task ImportAsync(
        ApplicationTestHost app,
        IMovieCatalogRepository movies,
        string libraryId,
        string title,
        int year,
        string? imdbId = null)
    {
        await movies.ImportExistingAsync(
            libraryId,
            title,
            year,
            "covered",
            "It is on disk.",
            "WEB 1080p",
            "WEB 1080p",
            true,
            false,
            Path.Combine(app.DataRoot, "films", $"{title} ({year})", $"{title} ({year}).mkv"),
            1024,
            CancellationToken.None);

        if (string.IsNullOrWhiteSpace(imdbId))
        {
            return;
        }

        // Set in SQL because the import path does not carry an id — which is the
        // whole reason phantom rows have none. Keyed on the release year and the
        // absence of an id, so it always lands on the row just written.
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = await factory.OpenConnectionAsync(DelunoDatabaseNames.Movies, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE movie_entries
            SET imdb_id = $imdbId
            WHERE id = (
                SELECT id FROM movie_entries
                WHERE imdb_id IS NULL
                ORDER BY created_utc DESC, id DESC
                LIMIT 1
            );
            """;
        var idParameter = command.CreateParameter();
        idParameter.ParameterName = "$imdbId";
        idParameter.Value = imdbId;
        command.Parameters.Add(idParameter);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task SetImdbIdAsync(ApplicationTestHost app, string movieId, string imdbId)
    {
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = await factory.OpenConnectionAsync(DelunoDatabaseNames.Movies, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE movie_entries SET imdb_id = $imdbId WHERE id = $id;";
        var idParameter = command.CreateParameter();
        idParameter.ParameterName = "$imdbId";
        idParameter.Value = imdbId;
        command.Parameters.Add(idParameter);
        var rowParameter = command.CreateParameter();
        rowParameter.ParameterName = "$id";
        rowParameter.Value = movieId;
        command.Parameters.Add(rowParameter);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task<string> CreateLibraryAsync(ApplicationTestHost app)
    {
        var root = Path.Combine(app.DataRoot, "films");
        Directory.CreateDirectory(root);

        var libraries = app.Services.GetRequiredService<ILibrariesRepository>();
        var library = await libraries.CreateLibraryAsync(
            new CreateLibraryRequest(
                Name: "Films",
                MediaType: "movies",
                Purpose: "collection",
                RootPath: root,
                DownloadsPath: null,
                QualityProfileId: null,
                ImportWorkflow: "copy",
                ProcessorName: null,
                ProcessorOutputPath: null,
                ProcessorTimeoutMinutes: null,
                ProcessorFailureMode: null,
                AutoSearchEnabled: false,
                MissingSearchEnabled: false,
                UpgradeSearchEnabled: false,
                SearchIntervalHours: null,
                RetryDelayHours: null,
                MaxItemsPerRun: null),
            CancellationToken.None);
        return library.Id;
    }
}
