using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Movies.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Quality;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Movies;

/// <summary>
/// #1047: a movie is on one stored rung, every filter chip counts that rung, and
/// the columns that describe it cannot disagree.
/// </summary>
public sealed class MovieOneRungPerMovieTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-29T00:00:00Z");
    private static readonly string[] Chips =
    [
        CatalogueStatusFilters.Missing,
        CatalogueStatusFilters.Upgrades,
        CatalogueStatusFilters.Covered,
        CatalogueStatusFilters.Upcoming,
        CatalogueStatusFilters.Downloading
    ];

    [Fact]
    public async Task Chip_counts_sum_to_all_in_one_library_and_across_two()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateMoviesAsync(storage);

        // One film in each state, in library A.
        await SeedAsync(movies, "Missing Film", "lib-a", WantedStatuses.Missing, hasFile: false, cutoffMet: false);
        await SeedAsync(movies, "Upgrade Film", "lib-a", WantedStatuses.Upgrade, hasFile: true, cutoffMet: false);
        await SeedAsync(movies, "Covered Film", "lib-a", WantedStatuses.Covered, hasFile: true, cutoffMet: true);
        await SeedAsync(movies, "Upcoming Film", "lib-a", WantedStatuses.Upcoming, hasFile: false, cutoffMet: false);
        await SeedAsync(movies, "Downloading Film", "lib-a", WantedStatuses.Downloading, hasFile: false, cutoffMet: false);

        // A profile that does not upgrade: the engine stores covered beside a
        // file below its cutoff, and that stays Quality met (#1065).
        await SeedAsync(movies, "Below Target Film", "lib-a", WantedStatuses.Covered, hasFile: true, cutoffMet: false);

        // Held in two libraries: covered in one, upgradable in the other.
        var twoLibraries = await SeedAsync(movies, "Two Library Film", "lib-a", WantedStatuses.Covered, hasFile: true, cutoffMet: true);
        await movies.EnsureWantedStateAsync(
            twoLibraries, "lib-b", WantedStatuses.Upgrade, "x", true, "WEB 1080p", "WEB 2160p", false, CancellationToken.None);

        // A film only in library B.
        await SeedAsync(movies, "B Only Film", "lib-b", WantedStatuses.Missing, hasFile: false, cutoffMet: false);

        foreach (var libraryId in new string?[] { null, "lib-a", "lib-b" })
        {
            var all = await movies.ListPageAsync(new CatalogueQuery(LibraryId: libraryId, PageSize: 100), CancellationToken.None);
            var facets = all.Facets!;
            Assert.Equal(facets.All, facets.Missing + facets.Upgrades + facets.Covered + facets.Upcoming + facets.Downloading);
            Assert.Equal(all.TotalCount, facets.All);

            // Each movie appears under exactly one chip, and the chip agrees
            // with the rung the movie's own row carries (the list mark).
            var seen = new Dictionary<string, string>();
            foreach (var chip in Chips)
            {
                var page = await movies.ListPageAsync(
                    new CatalogueQuery(Status: chip, LibraryId: libraryId, PageSize: 100), CancellationToken.None);
                foreach (var item in page.Items)
                {
                    Assert.True(seen.TryAdd(item.Id, chip), $"{item.Title} appeared under {seen.GetValueOrDefault(item.Id)} and {chip}");
                    var rung = chip == CatalogueStatusFilters.Upgrades ? WantedStatuses.Upgrade : chip;
                    Assert.Equal(rung, item.WantedStatus);
                }
            }

            Assert.Equal(all.Items.Count, seen.Count);
        }

        var everything = (await movies.ListPageAsync(new CatalogueQuery(PageSize: 100), CancellationToken.None)).Facets!;
        Assert.Equal(8, everything.All);
        Assert.Equal(2, everything.Missing); // Missing Film + B Only Film
        Assert.Equal(2, everything.Upgrades); // Upgrade Film + Two Library Film
        Assert.Equal(2, everything.Covered); // Covered Film + Below Target Film
        Assert.Equal(1, everything.Upcoming);
        Assert.Equal(1, everything.Downloading);

        // The two-library film follows the library asked about.
        var inA = (await movies.ListPageAsync(new CatalogueQuery(LibraryId: "lib-a", PageSize: 100), CancellationToken.None)).Facets!;
        var inB = (await movies.ListPageAsync(new CatalogueQuery(LibraryId: "lib-b", PageSize: 100), CancellationToken.None)).Facets!;
        Assert.Equal(3, inA.Covered); // Covered Film + Below Target Film + Two Library Film in A
        Assert.Equal(0, inB.Covered);
        Assert.Equal(1, inB.Upgrades);
    }

    [Fact]
    public async Task A_below_cutoff_file_the_profile_does_not_upgrade_stays_covered_and_a_file_less_title_is_never_covered()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateMoviesAsync(storage);
        await SeedAsync(movies, "Below Target Film", "lib-a", WantedStatuses.Covered, hasFile: true, cutoffMet: false);
        await SeedAsync(movies, "Phantom Cover Film", "lib-a", WantedStatuses.Covered, hasFile: false, cutoffMet: true);

        var page = await movies.ListPageAsync(new CatalogueQuery(PageSize: 10), CancellationToken.None);

        Assert.Equal(WantedStatuses.Covered, page.Items.Single(item => item.Title == "Below Target Film").WantedStatus);
        Assert.Equal(WantedStatuses.Missing, page.Items.Single(item => item.Title == "Phantom Cover Film").WantedStatus);
    }

    [Fact]
    public async Task Repair_migration_fixes_rows_that_disagree_and_leaves_the_rest()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateMoviesAsync(storage);
        var below = await SeedAsync(movies, "Below Target Film", "lib-a", WantedStatuses.Missing, hasFile: false, cutoffMet: false);
        var phantom = await SeedAsync(movies, "Phantom Cover Film", "lib-a", WantedStatuses.Missing, hasFile: false, cutoffMet: false);
        var future = await SeedAsync(movies, "Future Film", "lib-a", WantedStatuses.Missing, hasFile: false, cutoffMet: false, year: 2999);
        var fine = await SeedAsync(movies, "Fine Film", "lib-a", WantedStatuses.Covered, hasFile: true, cutoffMet: true);

        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Movies, CancellationToken.None);
        // Put the database back the way an install from before the fix has it.
        await ExecuteAsync(connection, "DROP TRIGGER trg_movie_wanted_rung_ai; DROP TRIGGER trg_movie_wanted_rung_au;");
        await ExecuteAsync(connection, $"""
            UPDATE movie_wanted_state SET wanted_status = 'covered', has_file = 1, quality_cutoff_met = 0 WHERE movie_id = '{below}';
            UPDATE movie_wanted_state SET wanted_status = 'covered', has_file = 0, quality_cutoff_met = 1 WHERE movie_id = '{phantom}';
            UPDATE movie_wanted_state SET wanted_status = 'upgrade', has_file = 0 WHERE movie_id = '{future}';
            """);

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await new V0045MovieOneRungPerMovie().UpAsync(connection, transaction, CancellationToken.None);
            await transaction.CommitAsync();
        }

        Assert.Equal("upgrade", await StatusAsync(connection, below));
        Assert.Equal("missing", await StatusAsync(connection, phantom));
        Assert.Equal("upcoming", await StatusAsync(connection, future));
        Assert.Equal("covered", await StatusAsync(connection, fine));
    }

    [Fact]
    public void The_policy_engine_stops_looking_only_when_the_profile_does_not_upgrade()
    {
        var engine = new VersionedMediaPolicyEngine();

        var quiet = engine.DecideWantedState(Input(false, false, "WEB 1080p"));
        Assert.Equal(WantedStatuses.Covered, quiet.WantedStatus);
        Assert.False(quiet.QualityCutoffMet);

        var quietUnknown = engine.DecideWantedState(Input(true, false, null));
        Assert.Equal(WantedStatuses.Covered, quietUnknown.WantedStatus);
        Assert.False(quietUnknown.QualityCutoffMet);

        Assert.Equal(WantedStatuses.Upgrade, engine.DecideWantedState(Input(true, false, "WEB 1080p")).WantedStatus);
        Assert.Equal(WantedStatuses.Upgrade, engine.DecideWantedState(Input(false, true, null)).WantedStatus);
    }

    [Fact]
    public async Task Repair_v0047_puts_quiet_movies_back_on_covered_and_leaves_upgrading_ones_and_searches_alone()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateMoviesAsync(storage);
        var engine = new VersionedMediaPolicyEngine();

        // The exact words the engine writes, so the repair's match cannot drift from them.
        var quietBelow = engine.DecideWantedState(Input(false, false, "WEB 1080p")).WantedReason;
        var quietUnknown = engine.DecideWantedState(Input(true, false, null)).WantedReason;
        var hunting = engine.DecideWantedState(Input(true, false, "WEB 1080p")).WantedReason;
        var huntingUnknown = engine.DecideWantedState(Input(false, true, null)).WantedReason;

        var a = await SeedAsync(movies, "Quiet Below", "lib-a", WantedStatuses.Covered, hasFile: true, cutoffMet: false);
        var b = await SeedAsync(movies, "Quiet Unknown", "lib-a", WantedStatuses.Covered, hasFile: true, cutoffMet: false);
        var c = await SeedAsync(movies, "Hunting", "lib-a", WantedStatuses.Upgrade, hasFile: true, cutoffMet: false);
        var d = await SeedAsync(movies, "Hunting Unknown", "lib-a", WantedStatuses.Upgrade, hasFile: true, cutoffMet: false);
        var e = await SeedAsync(movies, "Was Cleared", "lib-a", WantedStatuses.Missing, hasFile: false, cutoffMet: false);

        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Movies, CancellationToken.None);
        // An install V0045 already rewrote: status upgrade, the engine's reason untouched.
        await ExecuteAsync(connection, "DELETE FROM movie_rung_repairs;");
        await SetRewrittenAsync(connection, a, quietBelow);
        await SetRewrittenAsync(connection, b, quietUnknown);
        await SetRewrittenAsync(connection, c, hunting);
        await SetRewrittenAsync(connection, d, huntingUnknown);

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await new V0047MovieQuietRungRestored().UpAsync(connection, transaction, CancellationToken.None);
            await transaction.CommitAsync();
        }

        Assert.Equal("covered", await StatusAsync(connection, a));
        Assert.Equal("covered", await StatusAsync(connection, b));
        Assert.Equal("upgrade", await StatusAsync(connection, c));
        Assert.Equal("upgrade", await StatusAsync(connection, d));
        Assert.Equal("missing", await StatusAsync(connection, e));

        // Nothing was queued for search, and only the two repaired are recorded for Activity.
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM movie_wanted_state WHERE last_search_utc IS NOT NULL OR next_eligible_search_utc IS NOT NULL;";
        Assert.Equal(0L, Convert.ToInt64(await count.ExecuteScalarAsync()));
        count.CommandText = "SELECT COUNT(*) FROM movie_rung_repairs WHERE reported = 0;";
        Assert.Equal(2L, Convert.ToInt64(await count.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Deleting_a_movies_file_leaves_it_with_no_file_unmonitored_and_never_covered()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateMoviesAsync(storage);
        var released = await SeedAsync(movies, "Released Film", "lib-a", WantedStatuses.Covered, hasFile: true, cutoffMet: true);
        var future = await SeedAsync(movies, "Future Film", "lib-a", WantedStatuses.Covered, hasFile: true, cutoffMet: true, year: 2999);

        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Movies, CancellationToken.None);
        await ExecuteAsync(connection, "UPDATE movie_wanted_state SET file_path = '/movies/' || movie_id || '.mkv';");

        Assert.True(await movies.ClearFileAsync(released, $"/movies/{released}.mkv", CancellationToken.None));
        Assert.True(await movies.ClearFileAsync(future, $"/movies/{future}.mkv", CancellationToken.None));

        Assert.Equal("missing", await StatusAsync(connection, released));
        Assert.Equal("upcoming", await StatusAsync(connection, future));

        // The one rule for a deleted file: no file, unmonitored (so it sits under
        // the Unmonitored pill and no search picks it up), nothing queued.
        foreach (var id in new[] { released, future })
        {
            var after = await movies.GetByIdAsync(id, CancellationToken.None);
            Assert.False(after!.HasFile);
            Assert.False(after.Monitored, "a movie whose file was deleted must be unmonitored");
        }

        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM movie_wanted_state WHERE last_search_utc IS NOT NULL OR next_eligible_search_utc IS NOT NULL;";
        Assert.Equal(0L, Convert.ToInt64(await count.ExecuteScalarAsync()));
    }

    private static MediaWantedDecisionInput Input(bool upgradeUntilCutoff, bool upgradeUnknown, string? current)
        => new("movies", true, current, "WEB 2160p", upgradeUntilCutoff, upgradeUnknown);

    private static Task SetRewrittenAsync(System.Data.Common.DbConnection connection, string movieId, string reason)
        => ExecuteAsync(connection,
            $"UPDATE movie_wanted_state SET wanted_status = 'upgrade', wanted_reason = '{reason.Replace("'", "''")}' WHERE movie_id = '{movieId}';");

    private static async Task<string> SeedAsync(
        SqliteMovieCatalogRepository movies,
        string title,
        string libraryId,
        string status,
        bool hasFile,
        bool cutoffMet,
        int year = 2001)
    {
        var added = await movies.AddAsync(new CreateMovieRequest(title, year, null), CancellationToken.None);
        await movies.EnsureWantedStateAsync(
            added.Id, libraryId, status, "x", hasFile, hasFile ? "WEB 1080p" : null, "WEB 2160p", cutoffMet, CancellationToken.None);
        return added.Id;
    }

    private static async Task<string?> StatusAsync(System.Data.Common.DbConnection connection, string movieId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT wanted_status FROM movie_wanted_state WHERE movie_id = '{movieId}';";
        return (string?)await command.ExecuteScalarAsync();
    }

    private static async Task ExecuteAsync(System.Data.Common.DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<SqliteMovieCatalogRepository> CreateMoviesAsync(TestStorage storage)
    {
        var timeProvider = new FixedTimeProvider(Now);
        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        return new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
    }
}
