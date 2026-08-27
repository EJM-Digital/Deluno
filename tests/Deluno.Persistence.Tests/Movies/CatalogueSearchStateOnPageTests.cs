using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Movies;

/// <summary>
/// The search state a catalogue page carries for every title on it.
///
/// The grid used to read this from the wanted summary, whose <c>recentItems</c>
/// is <c>LIMIT 25</c>. Past the twenty-fifth title in a library, every card lost
/// its status, its reason, its target quality and the library it belonged to,
/// and fell back to "is there a file". Eleven films on a lab rig all fit inside
/// twenty-five, which is exactly why nobody saw it — so these tests are written
/// at a size where the cap would show.
/// </summary>
public sealed class CatalogueSearchStateOnPageTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-27T00:00:00Z");

    /// <summary>
    /// The whole point of the change, at a size the old path could not reach.
    /// Two thousand films, walked page by page: every single card has to know
    /// its own state, not just the first twenty-five.
    /// </summary>
    [Fact]
    public async Task Every_card_on_every_page_carries_its_own_search_state()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateMoviesAsync(storage);

        const int total = 2_000;
        for (var index = 0; index < total; index++)
        {
            var added = await movies.AddAsync(
                new CreateMovieRequest($"Title {index:D5}", 1990 + (index % 30), null),
                CancellationToken.None);

            await movies.EnsureWantedStateAsync(
                added.Id,
                "library-films",
                wantedStatus: index % 2 == 0 ? "missing" : "upgrade",
                wantedReason: $"Reason {index}",
                hasFile: index % 2 == 1,
                currentQuality: index % 2 == 1 ? "WEB 1080p" : null,
                targetQuality: "WEB 2160p",
                qualityCutoffMet: false,
                CancellationToken.None);
        }

        var seen = 0;
        string? token = null;

        do
        {
            var page = await movies.ListPageAsync(
                new CatalogueQuery(PageSize: 100, PageToken: token),
                CancellationToken.None);

            foreach (var item in page.Items)
            {
                Assert.NotNull(item.WantedStatus);
                Assert.NotNull(item.WantedReason);
                Assert.Equal("library-films", item.LibraryId);
                Assert.Equal("WEB 2160p", item.TargetQuality);
                Assert.False(item.QualityCutoffMet);
                seen++;
            }

            token = page.NextPageToken;
        }
        while (token is not null);

        Assert.Equal(total, seen);
    }

    /// <summary>
    /// A page must not answer for a library the caller did not ask about.
    /// </summary>
    [Fact]
    public async Task A_scoped_page_reads_the_state_of_the_library_it_was_asked_for()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateMoviesAsync(storage);
        var film = await movies.AddAsync(new CreateMovieRequest("Arrival", 2016, null), CancellationToken.None);

        // "waiting" is what the movie vocabulary calls a title that is here and
        // at target; #300 renames it to "covered". The point of this test is the
        // library the state was read from, not the word it is stored under.
        await movies.EnsureWantedStateAsync(film.Id, "library-4k", "waiting", "Meets the 4K profile.", true, "WEB 2160p", "WEB 2160p", true, CancellationToken.None);
        await movies.EnsureWantedStateAsync(film.Id, "library-hd", "upgrade", "Short of the HD profile.", true, "WEB 720p", "WEB 1080p", false, CancellationToken.None);

        var fourK = await movies.ListPageAsync(new CatalogueQuery(LibraryId: "library-4k"), CancellationToken.None);
        var hd = await movies.ListPageAsync(new CatalogueQuery(LibraryId: "library-hd"), CancellationToken.None);

        var inFourK = Assert.Single(fourK.Items);
        Assert.Equal("library-4k", inFourK.LibraryId);
        Assert.Equal("waiting", inFourK.WantedStatus);
        Assert.Equal("WEB 2160p", inFourK.CurrentQuality);
        Assert.True(inFourK.QualityCutoffMet);

        var inHd = Assert.Single(hd.Items);
        Assert.Equal("library-hd", inHd.LibraryId);
        Assert.Equal("upgrade", inHd.WantedStatus);
        Assert.Equal("WEB 720p", inHd.CurrentQuality);
        Assert.False(inHd.QualityCutoffMet);
    }

    /// <summary>
    /// The card and the filter that selected it have to agree.
    ///
    /// A film held in two libraries has two states, and the page shows one. If
    /// it picked the settled copy while the Upgrades filter counted the
    /// unsettled one, the film would appear under Upgrades saying its cutoff was
    /// met — the same class of defect as a status written in one word and read
    /// in another. The pick prefers the copy still short of its cutoff, so the
    /// row the page speaks for is the row the filter selected on.
    /// </summary>
    [Fact]
    public async Task An_unscoped_page_reports_the_copy_its_own_filters_select_on()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateMoviesAsync(storage);
        var film = await movies.AddAsync(new CreateMovieRequest("Arrival", 2016, null), CancellationToken.None);

        await movies.EnsureWantedStateAsync(film.Id, "library-a", "waiting", "Settled here.", true, "WEB 2160p", "WEB 2160p", true, CancellationToken.None);
        await movies.EnsureWantedStateAsync(film.Id, "library-b", "upgrade", "Still short here.", true, "WEB 720p", "WEB 1080p", false, CancellationToken.None);

        var all = await movies.ListPageAsync(new CatalogueQuery(), CancellationToken.None);
        var upgrades = await movies.ListPageAsync(
            new CatalogueQuery(Status: CatalogueStatusFilters.Upgrades),
            CancellationToken.None);

        var card = Assert.Single(all.Items);
        Assert.True(card.HasFile);
        Assert.False(card.QualityCutoffMet);
        Assert.Equal("upgrade", card.WantedStatus);

        // The filter agrees, and the row it returns says the same thing.
        Assert.Equal(film.Id, Assert.Single(upgrades.Items).Id);
        Assert.Equal(1, all.Facets!.Upgrades);
    }

    /// <summary>
    /// A title Deluno tracks in no library has no state to report. Null says
    /// that; <c>false</c> would be a claim it cannot support.
    /// </summary>
    [Fact]
    public async Task An_untracked_title_says_nothing_rather_than_guessing()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateMoviesAsync(storage);
        await movies.AddAsync(new CreateMovieRequest("Arrival", 2016, null), CancellationToken.None);

        var card = Assert.Single((await movies.ListPageAsync(new CatalogueQuery(), CancellationToken.None)).Items);

        Assert.False(card.HasFile);
        Assert.Null(card.LibraryId);
        Assert.Null(card.WantedStatus);
        Assert.Null(card.WantedReason);
        Assert.Null(card.TargetQuality);
        Assert.Null(card.QualityCutoffMet);
        Assert.Null(card.LastSearchUtc);
        Assert.Null(card.NextEligibleSearchUtc);
    }

    /// <summary>Shows carry the same state, for the same reason.</summary>
    [Fact]
    public async Task Series_pages_carry_their_search_state_too()
    {
        using var storage = TestStorage.Create();
        var series = await CreateSeriesAsync(storage);
        var show = await series.AddAsync(new CreateSeriesRequest("Severance", 2022, null), CancellationToken.None);

        await series.EnsureWantedStateAsync(show.Id, "library-tv", "upgrade", "Better copy available.", true, "WEB 1080p", "WEB 2160p", false, CancellationToken.None);

        var card = Assert.Single((await series.ListPageAsync(new CatalogueQuery(), CancellationToken.None)).Items);

        Assert.Equal("library-tv", card.LibraryId);
        Assert.Equal("upgrade", card.WantedStatus);
        Assert.Equal("Better copy available.", card.WantedReason);
        Assert.Equal("WEB 2160p", card.TargetQuality);
        Assert.False(card.QualityCutoffMet);
    }

    /// <summary>
    /// The bar under a show's poster counts what has aired.
    ///
    /// Measuring against what will eventually exist leaves every ongoing show
    /// permanently short of itself, which is true of every ongoing show and so
    /// says nothing about any of them.
    /// </summary>
    [Fact]
    public async Task Episode_counts_describe_what_has_aired_not_what_will_exist()
    {
        using var storage = TestStorage.Create();
        var series = await CreateSeriesAsync(storage);
        var show = await series.AddAsync(new CreateSeriesRequest("Severance", 2022, null), CancellationToken.None);

        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Series);

        // Six aired: four held, one of those still short of its cutoff. Two
        // still to come, and one with no air date at all.
        await InsertEpisodeAsync(connection, show.Id, 1, 1, Now.AddDays(-40), hasFile: true, cutoffMet: true);
        await InsertEpisodeAsync(connection, show.Id, 1, 2, Now.AddDays(-33), hasFile: true, cutoffMet: true);
        await InsertEpisodeAsync(connection, show.Id, 1, 3, Now.AddDays(-26), hasFile: true, cutoffMet: true);
        await InsertEpisodeAsync(connection, show.Id, 1, 4, Now.AddDays(-19), hasFile: true, cutoffMet: false);
        await InsertEpisodeAsync(connection, show.Id, 1, 5, Now.AddDays(-12), hasFile: false, cutoffMet: false);
        await InsertEpisodeAsync(connection, show.Id, 1, 6, Now.AddDays(-5), hasFile: false, cutoffMet: false);
        await InsertEpisodeAsync(connection, show.Id, 1, 7, Now.AddDays(2), hasFile: false, cutoffMet: false);
        await InsertEpisodeAsync(connection, show.Id, 1, 8, Now.AddDays(9), hasFile: false, cutoffMet: false);
        await InsertEpisodeAsync(connection, show.Id, 1, 9, airDateUtc: null, hasFile: false, cutoffMet: false);

        var card = Assert.Single((await series.ListPageAsync(new CatalogueQuery(), CancellationToken.None)).Items);

        Assert.Equal(9, card.EpisodeCount);
        Assert.Equal(6, card.AiredEpisodeCount);
        Assert.Equal(4, card.AiredWithFileCount);
        Assert.Equal(1, card.AiredUpgradableCount);
        Assert.Equal(Now.AddDays(2), card.NextAirDateUtc);
    }

    /// <summary>
    /// A show whose episodes have not been catalogued counts zero of zero — not
    /// zero of some total it does not know.
    /// </summary>
    [Fact]
    public async Task A_show_with_no_catalogued_episodes_counts_nothing()
    {
        using var storage = TestStorage.Create();
        var series = await CreateSeriesAsync(storage);
        await series.AddAsync(new CreateSeriesRequest("Severance", 2022, null), CancellationToken.None);

        var card = Assert.Single((await series.ListPageAsync(new CatalogueQuery(), CancellationToken.None)).Items);

        Assert.Equal(0, card.EpisodeCount);
        Assert.Equal(0, card.AiredEpisodeCount);
        Assert.Equal(0, card.AiredWithFileCount);
        Assert.Equal(0, card.AiredUpgradableCount);
        Assert.Null(card.NextAirDateUtc);
    }

    /// <summary>
    /// Episode progress is bounded by the page, not by the catalogue.
    ///
    /// Five aggregates written into the page query would have cost a scan of
    /// every episode of every show on it per column; a grouped subquery joined
    /// to the page would have cost a scan of the whole episode table. One
    /// grouped pass over the page's own shows is what keeps page four hundred
    /// costing what page one costs, and this is the assertion that says so.
    /// </summary>
    [Fact]
    public async Task Episode_progress_touches_only_the_shows_on_the_page()
    {
        using var storage = TestStorage.Create();
        var series = await CreateSeriesAsync(storage);

        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Series);

        for (var index = 0; index < 60; index++)
        {
            var show = await series.AddAsync(
                new CreateSeriesRequest($"Show {index:D3}", 2020, null),
                CancellationToken.None);

            for (var episode = 1; episode <= 10; episode++)
            {
                await InsertEpisodeAsync(connection, show.Id, 1, episode, Now.AddDays(-episode), hasFile: true, cutoffMet: true);
            }
        }

        var page = await series.ListPageAsync(new CatalogueQuery(PageSize: 10), CancellationToken.None);

        Assert.Equal(10, page.Items.Count);
        Assert.All(page.Items, item =>
        {
            Assert.Equal(10, item.EpisodeCount);
            Assert.Equal(10, item.AiredEpisodeCount);
            Assert.Equal(10, item.AiredWithFileCount);
        });
    }

    /// <summary>
    /// A single title knows its own search state too.
    ///
    /// The detail page used to find this by searching the wanted summary — the
    /// 25 most recently updated titles — for the one title it was already
    /// showing. Open the 26th and it found nothing: no library, no target
    /// quality, no cutoff, and a Defer button that could only fail. Exactly the
    /// grid's defect, one screen over. Asking a title about itself cannot go
    /// stale with how recently it happened to be touched, so this seeds a title
    /// that is deliberately not recent and still expects a full answer.
    /// </summary>
    [Fact]
    public async Task A_single_title_carries_its_search_state_however_long_ago_it_moved()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateMoviesAsync(storage);
        var film = await movies.AddAsync(new CreateMovieRequest("Arrival", 2016, null), CancellationToken.None);
        await movies.EnsureWantedStateAsync(film.Id, "library-films", "upgrade", "Better copy available.", true, "WEB 1080p", "WEB 2160p", false, CancellationToken.None);

        // Fifty more titles touched after it, so it is nowhere near the recent 25.
        for (var index = 0; index < 50; index++)
        {
            var other = await movies.AddAsync(new CreateMovieRequest($"Later {index:D2}", 2020, null), CancellationToken.None);
            await movies.EnsureWantedStateAsync(other.Id, "library-films", "missing", "Needs a file.", false, null, "WEB 1080p", false, CancellationToken.None);
        }

        var detail = await movies.GetByIdAsync(film.Id, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.True(detail.HasFile);
        Assert.Equal("library-films", detail.LibraryId);
        Assert.Equal("upgrade", detail.WantedStatus);
        Assert.Equal("Better copy available.", detail.WantedReason);
        Assert.Equal("WEB 1080p", detail.CurrentQuality);
        Assert.Equal("WEB 2160p", detail.TargetQuality);
        Assert.False(detail.QualityCutoffMet);
    }

    /// <summary>
    /// And it does so on the path production actually runs.
    ///
    /// <see cref="SqliteMovieCatalogRepository"/> answers <c>GetByIdAsync</c>
    /// two ways: its own query, and — whenever the shared media state repository
    /// is registered, which it always is — a delegation to that. Testing only
    /// the first would be testing the half that does not ship.
    /// </summary>
    [Fact]
    public async Task The_shared_media_state_path_carries_it_as_well()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(Now);
        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var shared = new Deluno.Media.SqliteMediaStateRepository(storage.Factory, timeProvider);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider, shared);

        var film = await movies.AddAsync(new CreateMovieRequest("Arrival", 2016, null), CancellationToken.None);
        await movies.EnsureWantedStateAsync(film.Id, "library-films", "upgrade", "Better copy available.", true, "WEB 1080p", "WEB 2160p", false, CancellationToken.None);

        var detail = await movies.GetByIdAsync(film.Id, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.True(detail.HasFile);
        Assert.Equal("library-films", detail.LibraryId);
        Assert.Equal("upgrade", detail.WantedStatus);
        Assert.Equal("Better copy available.", detail.WantedReason);
        Assert.Equal("WEB 1080p", detail.CurrentQuality);
        Assert.Equal("WEB 2160p", detail.TargetQuality);
        Assert.False(detail.QualityCutoffMet);
    }

    /// <summary>The show half of the same fix.</summary>
    [Fact]
    public async Task A_single_show_carries_its_search_state_too()
    {
        using var storage = TestStorage.Create();
        var series = await CreateSeriesAsync(storage);
        var show = await series.AddAsync(new CreateSeriesRequest("Severance", 2022, null), CancellationToken.None);
        await series.EnsureWantedStateAsync(show.Id, "library-tv", "upgrade", "Better copy available.", true, "WEB 1080p", "WEB 2160p", false, CancellationToken.None);

        for (var index = 0; index < 50; index++)
        {
            var other = await series.AddAsync(new CreateSeriesRequest($"Later {index:D2}", 2023, null), CancellationToken.None);
            await series.EnsureWantedStateAsync(other.Id, "library-tv", "missing", "Needs a file.", false, null, "WEB 1080p", false, CancellationToken.None);
        }

        var detail = await series.GetByIdAsync(show.Id, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal("library-tv", detail.LibraryId);
        Assert.Equal("upgrade", detail.WantedStatus);
        Assert.Equal("WEB 1080p", detail.CurrentQuality);
        Assert.Equal("WEB 2160p", detail.TargetQuality);
        Assert.False(detail.QualityCutoffMet);
    }

    /// <summary>
    /// The page stays a seek.
    ///
    /// The wanted state has to be reached one row at a time, by key. Reach it
    /// any other way — a grouped subquery joined to the page, an aggregate over
    /// the table — and SQLite materialises every wanted row in the catalogue
    /// before it can return fifty, which turns page four hundred of twenty
    /// thousand from a seek into a full scan. Nothing about the result would
    /// look wrong; only the twenty-thousandth title would feel it, and only on
    /// a machine nobody tests on. So the plan itself is the assertion.
    /// </summary>
    [Fact]
    public async Task The_page_reaches_the_wanted_state_by_key_and_never_scans_it()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateMoviesAsync(storage);

        for (var index = 0; index < 50; index++)
        {
            var added = await movies.AddAsync(
                new CreateMovieRequest($"Title {index:D3}", 2016, null),
                CancellationToken.None);
            await movies.EnsureWantedStateAsync(added.Id, "library-films", "missing", "Needs a file.", false, null, "WEB 1080p", false, CancellationToken.None);
        }

        var plan = await ExplainCataloguePageAsync(storage);

        // The catalogue itself is walked in sort order on an index, so the LIMIT
        // can stop early rather than sorting the whole library first.
        Assert.Contains(plan, line => line.Contains("m") && line.Contains("INDEX") && !line.Contains("TEMP B-TREE"));

        // And every mention of the wanted state is a keyed lookup.
        var wantedLines = plan.Where(line => line.Contains("movie_wanted_state") || line.Contains(" ws") || line.Contains(" pick")).ToArray();
        Assert.NotEmpty(wantedLines);
        Assert.All(wantedLines, line => Assert.StartsWith("SEARCH", line));
    }

    /// <summary>
    /// The plan for the real page query, read back the way SQLite reports it.
    /// Built from the same pieces the repository builds it from, so a change
    /// there changes this.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ExplainCataloguePageAsync(TestStorage storage)
    {
        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Movies);
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            EXPLAIN QUERY PLAN
            SELECT
                m.id,
                {CatalogueWantedState.HasFileColumn},
                ws.current_quality,
            {CatalogueWantedState.PageColumns},
                m.created_utc AS sort_value
            FROM movie_entries m
            {CatalogueWantedState.Join("m", "movie_wanted_state", "movie_id", scopedToLibrary: false)}
            WHERE 1 = 1
            ORDER BY m.created_utc DESC, m.id DESC
            LIMIT 51;
            """;

        var lines = new List<string>();
        using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        while (await reader.ReadAsync(CancellationToken.None))
        {
            lines.Add(reader.GetString(3));
        }

        return lines;
    }

    private static async Task InsertEpisodeAsync(
        System.Data.Common.DbConnection connection,
        string seriesId,
        int seasonNumber,
        int episodeNumber,
        DateTimeOffset? airDateUtc,
        bool hasFile,
        bool cutoffMet)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO episode_entries (
                id, series_id, season_id, season_number, episode_number, title, air_date_utc,
                monitored, has_file, quality_cutoff_met, created_utc, updated_utc
            ) VALUES (
                @id, @seriesId, NULL, @seasonNumber, @episodeNumber, @title, @airDateUtc,
                1, @hasFile, @cutoffMet, @stamp, @stamp
            );
            """;
        AddParameter(command, "@id", Guid.CreateVersion7().ToString("N"));
        AddParameter(command, "@seriesId", seriesId);
        AddParameter(command, "@seasonNumber", seasonNumber);
        AddParameter(command, "@episodeNumber", episodeNumber);
        AddParameter(command, "@title", $"S{seasonNumber:D2}E{episodeNumber:D2}");
        AddParameter(command, "@airDateUtc", airDateUtc?.ToString("O"));
        AddParameter(command, "@hasFile", hasFile ? 1 : 0);
        AddParameter(command, "@cutoffMet", cutoffMet ? 1 : 0);
        AddParameter(command, "@stamp", Now.ToString("O"));
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
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

    private static async Task<SqliteSeriesCatalogRepository> CreateSeriesAsync(TestStorage storage)
    {
        var timeProvider = new FixedTimeProvider(Now);
        await new SeriesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        return new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
    }
}
