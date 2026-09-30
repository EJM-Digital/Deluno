using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Media;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Movies;

/// <summary>
/// #1065: a film kept in two libraries stops being wanted only in the library
/// whose file was deleted; the other copy stays monitored and keeps being
/// searched and upgraded. A film in one library behaves as it always did.
/// </summary>
public sealed class MovieNotWantedHereTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-30T00:00:00Z");

    [Fact]
    public async Task Deleting_the_file_in_one_of_two_libraries_stops_wanting_it_only_there_via_the_Files_tab()
    {
        using var storage = TestStorage.Create();
        var (movies, shared) = await CreateAsync(storage);
        var id = await SeedTwoLibrariesAsync(storage, movies);

        var outcome = await movies.ClearFileAsync(id, $"/4k/{id}.mkv", CancellationToken.None);

        Assert.True(outcome);
        Assert.True(outcome.OnlyThatLibrary);
        Assert.Equal("lib-4k", outcome.LibraryId);
        Assert.Equal(["lib-hd"], outcome.OtherLibraryIds);
        await AssertOnlyHdIsStillWantedAsync(storage, movies, shared, id);
    }

    [Fact]
    public async Task Deleting_the_file_in_one_of_two_libraries_stops_wanting_it_only_there_via_the_file_check()
    {
        using var storage = TestStorage.Create();
        var (movies, shared) = await CreateAsync(storage);
        var id = await SeedTwoLibrariesAsync(storage, movies);

        var outcome = await movies.MarkTrackedFileGoneAsync(id, "lib-4k", $"/4k/{id}.mkv", CancellationToken.None);

        Assert.True(outcome.OnlyThatLibrary);
        await AssertOnlyHdIsStillWantedAsync(storage, movies, shared, id);
    }

    [Fact]
    public async Task A_film_in_one_library_is_unmonitored_as_before_and_no_library_flag_is_set()
    {
        using var storage = TestStorage.Create();
        var (movies, _) = await CreateAsync(storage);
        var added = await movies.AddAsync(new CreateMovieRequest("Solo", 2001, null), CancellationToken.None);
        await movies.EnsureWantedStateAsync(added.Id, "lib-hd", WantedStatuses.Covered, "x", true, "WEB 1080p", "WEB 1080p", true, CancellationToken.None);
        await SetPathsAsync(storage, "/hd/");

        var outcome = await movies.ClearFileAsync(added.Id, $"/hd/{added.Id}.mkv", CancellationToken.None);

        Assert.True(outcome);
        Assert.False(outcome.OnlyThatLibrary);
        Assert.False((await movies.GetByIdAsync(added.Id, CancellationToken.None))!.Monitored);
        Assert.Empty(await movies.ListNotWantedLibraryIdsAsync(added.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Deleting_the_last_wanted_copy_unmonitors_the_whole_title()
    {
        using var storage = TestStorage.Create();
        var (movies, _) = await CreateAsync(storage);
        var id = await SeedTwoLibrariesAsync(storage, movies);

        Assert.True((await movies.ClearFileAsync(id, $"/4k/{id}.mkv", CancellationToken.None)).OnlyThatLibrary);
        var second = await movies.ClearFileAsync(id, $"/hd/{id}.mkv", CancellationToken.None);

        Assert.True(second);
        Assert.False(second.OnlyThatLibrary);
        Assert.False((await movies.GetByIdAsync(id, CancellationToken.None))!.Monitored);
    }

    [Fact]
    public async Task Want_it_again_makes_that_library_searchable_again_and_only_that_library()
    {
        using var storage = TestStorage.Create();
        var (movies, shared) = await CreateAsync(storage);
        var id = await SeedTwoLibrariesAsync(storage, movies);
        await movies.ClearFileAsync(id, $"/4k/{id}.mkv", CancellationToken.None);
        Assert.Empty(await movies.ListEligibleWantedAsync("lib-4k", 10, Now, true, CancellationToken.None));

        Assert.False(await movies.WantAgainInLibraryAsync(id, "lib-hd", CancellationToken.None));
        Assert.True(await movies.WantAgainInLibraryAsync(id, "lib-4k", CancellationToken.None));

        Assert.Empty(await movies.ListNotWantedLibraryIdsAsync(id, CancellationToken.None));
        var again = await movies.ListEligibleWantedAsync("lib-4k", 10, Now, true, CancellationToken.None);
        Assert.Equal(id, Assert.Single(again).MovieId);
        Assert.Equal("missing", again[0].WantedStatus);
        Assert.Equal(id, Assert.Single(await shared.ListEligibleWantedAsync(MediaKind.Movie, "lib-4k", 10, Now, true, CancellationToken.None)).Id);
    }

    [Fact]
    public async Task Monitoring_the_whole_title_again_clears_the_library_switch()
    {
        using var storage = TestStorage.Create();
        var (movies, _) = await CreateAsync(storage);
        var id = await SeedTwoLibrariesAsync(storage, movies);
        await movies.ClearFileAsync(id, $"/4k/{id}.mkv", CancellationToken.None);

        await movies.UpdateMonitoredAsync([id], true, CancellationToken.None);

        Assert.Empty(await movies.ListNotWantedLibraryIdsAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task A_person_can_still_search_a_not_wanted_library_by_hand()
    {
        using var storage = TestStorage.Create();
        var (movies, shared) = await CreateAsync(storage);
        var id = await SeedTwoLibrariesAsync(storage, movies);
        await movies.ClearFileAsync(id, $"/4k/{id}.mkv", CancellationToken.None);

        var manual = await movies.ListEligibleWantedAsync("lib-4k", 10, Now, true, CancellationToken.None, requireMonitored: false);
        Assert.Equal(id, Assert.Single(manual).MovieId);
        var manualShared = await shared.ListEligibleWantedAsync(MediaKind.Movie, "lib-4k", 10, Now, true, CancellationToken.None, requireMonitored: false);
        Assert.Equal(id, Assert.Single(manualShared).Id);
    }

    [Fact]
    public async Task An_import_into_the_library_brings_it_back()
    {
        using var storage = TestStorage.Create();
        var (movies, _) = await CreateAsync(storage);
        var id = await SeedTwoLibrariesAsync(storage, movies);
        await movies.ClearFileAsync(id, $"/4k/{id}.mkv", CancellationToken.None);

        await movies.EnsureWantedStateAsync(id, "lib-4k", WantedStatuses.Covered, "x", true, "WEB 2160p", "WEB 2160p", true, CancellationToken.None);

        Assert.Empty(await movies.ListNotWantedLibraryIdsAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task The_migration_adds_the_flag_off_for_every_existing_row()
    {
        using var storage = TestStorage.Create();
        var (movies, _) = await CreateAsync(storage);
        var id = await SeedTwoLibrariesAsync(storage, movies);

        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Movies, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM movie_wanted_state WHERE not_wanted_here <> 0 OR not_wanted_since_utc IS NOT NULL;";
        Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        Assert.Empty(await movies.ListNotWantedLibraryIdsAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task The_catalogue_counts_the_switched_off_library_as_unmonitored_and_the_other_as_monitored()
    {
        using var storage = TestStorage.Create();
        var (movies, _) = await CreateAsync(storage);
        var id = await SeedTwoLibrariesAsync(storage, movies);
        await movies.ClearFileAsync(id, $"/4k/{id}.mkv", CancellationToken.None);

        var fourK = (await movies.ListPageAsync(new CatalogueQuery(LibraryId: "lib-4k", PageSize: 50), CancellationToken.None)).Facets!;
        Assert.Equal(1, fourK.Unmonitored);
        Assert.Equal(0, fourK.Monitored);
        var hd = (await movies.ListPageAsync(new CatalogueQuery(LibraryId: "lib-hd", PageSize: 50), CancellationToken.None)).Facets!;
        Assert.Equal(0, hd.Unmonitored);
        Assert.Equal(1, hd.Monitored);
        var unmonitoredFourK = await movies.ListPageAsync(new CatalogueQuery(LibraryId: "lib-4k", Monitored: false, PageSize: 50), CancellationToken.None);
        Assert.Single(unmonitoredFourK.Items);
    }

    private static async Task AssertOnlyHdIsStillWantedAsync(
        TestStorage storage,
        SqliteMovieCatalogRepository movies,
        IMediaStateRepository shared,
        string id)
    {
        // The title stays monitored; only the 4K row is switched off.
        Assert.True((await movies.GetByIdAsync(id, CancellationToken.None))!.Monitored);
        Assert.Equal(["lib-4k"], (await movies.ListNotWantedLibraryIdsAsync(id, CancellationToken.None)).ToArray());

        // The 4K row lost its file and is Missing, but is never picked for a search.
        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Movies, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT wanted_status, has_file FROM movie_wanted_state WHERE movie_id = @id AND library_id = 'lib-4k';";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = id;
        command.Parameters.Add(parameter);
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal("missing", reader.GetString(0));
            Assert.Equal(0L, reader.GetInt64(1));
        }

        Assert.Empty(await movies.ListEligibleWantedAsync("lib-4k", 10, Now, true, CancellationToken.None));
        Assert.Empty(await shared.ListEligibleWantedAsync(MediaKind.Movie, "lib-4k", 10, Now, true, CancellationToken.None));
        Assert.Equal(0, await movies.CountRetryDelayedWantedAsync("lib-4k", Now, CancellationToken.None));

        // The other library's copy is still searched and upgraded.
        var hd = await movies.ListEligibleWantedAsync("lib-hd", 10, Now, true, CancellationToken.None);
        Assert.Equal(id, Assert.Single(hd).MovieId);
        Assert.Equal("upgrade", hd[0].WantedStatus);
        Assert.Equal(id, Assert.Single(await shared.ListEligibleWantedAsync(MediaKind.Movie, "lib-hd", 10, Now, true, CancellationToken.None)).Id);

        // The wanted lists and counts skip the switched-off row and keep the other.
        var summary = await shared.GetWantedSummaryAsync(MediaKind.Movie, CancellationToken.None);
        Assert.Equal(1, summary.TotalWanted);
        Assert.Equal(0, summary.MissingCount);
        Assert.Equal(1, summary.UpgradeCount);
        var unmonitored = await shared.ListWantedPageAsync(MediaKind.Movie, "missing", false, 0, 10, CancellationToken.None);
        Assert.Equal(1, unmonitored.Total);
        var monitoredMissing = await shared.ListWantedPageAsync(MediaKind.Movie, "missing", true, 0, 10, CancellationToken.None);
        Assert.Equal(0, monitoredMissing.Total);
    }

    /// <summary>One film with a 1080p copy still upgradable and a 4K copy, each with its own file.</summary>
    private static async Task<string> SeedTwoLibrariesAsync(TestStorage storage, SqliteMovieCatalogRepository movies)
    {
        var added = await movies.AddAsync(new CreateMovieRequest("Heat", 1995, null), CancellationToken.None);
        await movies.EnsureWantedStateAsync(added.Id, "lib-hd", WantedStatuses.Upgrade, "x", true, "WEB 720p", "WEB 1080p", false, CancellationToken.None);
        await movies.EnsureWantedStateAsync(added.Id, "lib-4k", WantedStatuses.Covered, "x", true, "WEB 2160p", "WEB 2160p", true, CancellationToken.None);
        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Movies, CancellationToken.None);
        await using var paths = connection.CreateCommand();
        paths.CommandText = "UPDATE movie_wanted_state SET file_path = CASE library_id WHEN 'lib-hd' THEN '/hd/' ELSE '/4k/' END || movie_id || '.mkv' WHERE has_file = 1;";
        await paths.ExecuteNonQueryAsync();
        return added.Id;
    }

    private static async Task<(SqliteMovieCatalogRepository Movies, SqliteMediaStateRepository Shared)> CreateAsync(TestStorage storage)
    {
        var timeProvider = new FixedTimeProvider(Now);
        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var shared = new SqliteMediaStateRepository(storage.Factory, timeProvider);
        return (movies, shared);
    }

    private static async Task SetPathsAsync(TestStorage storage, string prefix)
    {
        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Movies, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE movie_wanted_state SET file_path = '{prefix}' || movie_id || '.mkv' WHERE has_file = 1;";
        await command.ExecuteNonQueryAsync();
    }
}
