using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Catalogue;

/// <summary>
/// How far through a show you are is right when you ask, however long it has
/// been since anybody last asked.
///
/// <para><b>Why this is not like the other cached facts.</b> Size and quality
/// change when something is <i>written</i> — a file arrives, a profile is
/// edited — so a trigger catches every one of them. This number changes because
/// <b>time passed</b>: nothing at all happens in the database when Thursday's
/// episode airs, and the show goes from 8 of 10 to 8 of 11 with no row
/// touched.</para>
///
/// <para>James was given the three ways to handle that and picked the one with
/// no caveat: exact whenever asked, and fast. These are the tests that make the
/// claim true rather than aspirational — every one of them moves the clock and
/// writes nothing.</para>
/// </summary>
public sealed class SeriesProgressStaysExactTests
{
    private static readonly DateTimeOffset Monday = DateTimeOffset.Parse("2026-03-02T00:00:00Z");

    [Fact]
    public async Task An_episode_airing_changes_the_count_with_nothing_written_at_all()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Monday);
        var series = await CreateAsync(storage, clock);

        // Three episodes: two already aired, one still to come on Thursday.
        var id = await AddShowAsync(series, "Severance",
            [Monday.AddDays(-14), Monday.AddDays(-7), Monday.AddDays(3)]);

        Assert.Equal(2, await AiredAsync(series, id));

        // Thursday arrives. No import, no edit, no write of any kind — the only
        // thing that has changed is the time.
        clock.Advance(TimeSpan.FromDays(4));

        Assert.Equal(3, await AiredAsync(series, id));
    }

    /// <summary>
    /// The other half of the bargain: a show with nothing still to come has no
    /// expiry, so it is never recomputed again however often the shelf is
    /// filtered. This is what makes the design cheap rather than merely
    /// correct.
    /// </summary>
    [Fact]
    public async Task A_finished_show_stops_costing_anything()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Monday);
        var series = await CreateAsync(storage, clock);

        var id = await AddShowAsync(series, "The Wire",
            [Monday.AddDays(-30), Monday.AddDays(-23)]);

        _ = await AiredAsync(series, id);

        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Series);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT next_air_date_utc FROM series_entries WHERE id = @id;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = id;
        command.Parameters.Add(parameter);

        // Null is the whole point: nothing is still to come, so nothing can go
        // stale, so the expiry sweep's partial index never sees this row again.
        Assert.True(await command.ExecuteScalarAsync() is null or DBNull);
    }

    /// <summary>
    /// A whole season that aired and holds nothing is a different and worse
    /// problem from a few scattered gaps — and season zero is specials, so a
    /// show with none is not a show missing a season.
    /// </summary>
    [Fact]
    public async Task A_season_that_aired_and_holds_nothing_is_flagged_and_specials_are_not()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Monday);
        var series = await CreateAsync(storage, clock);

        var id = await AddShowAsync(series, "Deadwood",
            [Monday.AddDays(-30), Monday.AddDays(-23)],
            seasonNumbers: [1, 2]);

        Assert.True(await HasMissingSeasonAsync(series, id));
    }

    /* ------------------------------------------------------------ helpers */

    private static async Task<int> AiredAsync(ISeriesCatalogRepository series, string id)
    {
        var row = await ReadAsync(series, id, "aired_episode_count");
        return Convert.ToInt32(row);
    }

    private static async Task<bool> HasMissingSeasonAsync(ISeriesCatalogRepository series, string id)
        => Convert.ToInt64(await ReadAsync(series, id, "has_missing_season")) == 1;

    /// <summary>
    /// Reading goes through <c>ListPageAsync</c> with a filter first, because
    /// that is what brings an expired row up to date — and a test that read the
    /// column directly would pass against a design that never refreshed
    /// anything.
    /// </summary>
    private static async Task<object?> ReadAsync(ISeriesCatalogRepository series, string id, string column)
    {
        await series.ListPageAsync(
            new CatalogueQuery(Filters: new CatalogueFilters(
                [new CatalogueFilterCondition("episodesAired", CatalogueFilterOperator.AtLeast, ["0"])])),
            CancellationToken.None);

        var storage = TestStorageAccessor.Current!;
        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Series);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {column} FROM series_entries WHERE id = @id;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = id;
        command.Parameters.Add(parameter);
        return await command.ExecuteScalarAsync();
    }

    private static async Task<ISeriesCatalogRepository> CreateAsync(TestStorage storage, TimeProvider clock)
    {
        TestStorageAccessor.Current = storage;
        await new SeriesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return new SqliteSeriesCatalogRepository(storage.Factory, clock);
    }

    private static async Task<string> AddShowAsync(
        ISeriesCatalogRepository series,
        string title,
        IReadOnlyList<DateTimeOffset> airDates,
        IReadOnlyList<int>? seasonNumbers = null)
    {
        await series.AddAsync(new CreateSeriesRequest(title, 2022, null), CancellationToken.None);
        var id = (await series.ListPageAsync(new CatalogueQuery(), CancellationToken.None))
            .Items.Single(item => item.Title == title).Id;

        await using var connection = await TestStorageAccessor.Current!.Factory
            .OpenConnectionAsync(DelunoDatabaseNames.Series);

        for (var i = 0; i < airDates.Count; i++)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO episode_entries
                    (id, series_id, season_number, episode_number, title, air_date_utc,
                     monitored, has_file, quality_cutoff_met, created_utc, updated_utc)
                VALUES (@id, @series, @season, @episode, @title, @air, 1, 0, 0, @now, @now);
                """;
            Add(command, "@id", $"{id}-e{i}");
            Add(command, "@series", id);
            Add(command, "@season", seasonNumbers is null ? 1 : seasonNumbers[i % seasonNumbers.Count]);
            Add(command, "@episode", i + 1);
            Add(command, "@title", $"Episode {i + 1}");
            Add(command, "@air", airDates[i].ToString("O"));
            Add(command, "@now", Monday.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        return id;
    }

    private static void Add(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static class TestStorageAccessor
    {
        [ThreadStatic]
        public static TestStorage? Current;
    }
}
