using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Media;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Media;

/// <summary>
/// The subtitle rollup must not be what turns the catalogue page into a scan.
///
/// <c>CatalogueSearchStateOnPageTests</c> pins the page query itself for the
/// same reason, in its own words: nothing about a wrong plan looks wrong until
/// the twenty-thousandth title, and only on a machine nobody tests on. This is
/// the same guard, extended to the query DESIGN-002 added beside it.
///
/// The SQL is taken from the repository rather than written out again here, so
/// a change there is a change to what this asserts.
/// </summary>
public sealed class SubtitleRollupQueryPlanTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-27T00:00:00Z");

    [Fact]
    public async Task A_movie_page_reaches_its_subtitles_by_key()
    {
        using var storage = TestStorage.Create();
        await InitialiseMoviesAsync(storage);

        var plan = await ExplainAsync(storage, DelunoDatabaseNames.Movies, MediaKind.Movie);

        Assert.NotEmpty(plan);
        // One indexed range scan per title on the page, on the primary key's
        // leading column. Anything that reads "SCAN movie_subtitle_state" is
        // reading the whole catalogue's subtitles to answer for fifty films.
        Assert.All(plan, line => Assert.DoesNotContain("SCAN movie_subtitle_state", line));
        Assert.Contains(plan, line => line.StartsWith("SEARCH", StringComparison.Ordinal) && line.Contains("movie_subtitle_state"));
    }

    [Fact]
    public async Task A_show_page_reaches_its_episodes_subtitles_by_key()
    {
        using var storage = TestStorage.Create();
        await InitialiseSeriesAsync(storage);

        var plan = await ExplainAsync(storage, DelunoDatabaseNames.Series, MediaKind.Series);

        Assert.NotEmpty(plan);
        Assert.All(plan, line => Assert.DoesNotContain("SCAN episode_subtitle_state", line));
        // The episode rows are reached from the subtitle rows by primary key,
        // not the other way round: a scan of every episode in the catalogue to
        // answer for fifty shows is the overhead DESIGN-002 rule 2 refuses.
        Assert.All(plan, line => Assert.DoesNotContain("SCAN episode_entries", line));
        Assert.Contains(plan, line => line.StartsWith("SEARCH", StringComparison.Ordinal) && line.Contains("episode_subtitle_state"));
        Assert.Contains(plan, line => line.StartsWith("SEARCH", StringComparison.Ordinal) && line.Contains("episode_entries"));
    }

    private static async Task<IReadOnlyList<string>> ExplainAsync(TestStorage storage, string databaseName, MediaKind kind)
    {
        await using var connection = await storage.Factory.OpenConnectionAsync(databaseName);
        using var command = connection.CreateCommand();

        var map = MediaTableMap.For(kind);
        command.CommandText = "EXPLAIN QUERY PLAN " + CatalogueSubtitleRollup.Sql(map, idCount: 50, languageCount: 2);

        for (var index = 0; index < 50; index++)
        {
            SqliteRecordHelpers.AddParameter(command, $"@id{index}", $"id-{index}");
        }

        SqliteRecordHelpers.AddParameter(command, "@lang0", "en");
        SqliteRecordHelpers.AddParameter(command, "@lang1", "ja");
        if (map.SubtitleRollupJoin.Contains("@now", StringComparison.Ordinal))
        {
            SqliteRecordHelpers.AddParameter(command, "@now", Now.ToString("O"));
        }

        var lines = new List<string>();
        using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        while (await reader.ReadAsync(CancellationToken.None))
        {
            lines.Add(reader.GetString(3));
        }

        return lines;
    }

    private static Task InitialiseMoviesAsync(TestStorage storage)
        => new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, new FixedTimeProvider(Now)),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

    private static Task InitialiseSeriesAsync(TestStorage storage)
        => new SeriesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, new FixedTimeProvider(Now)),
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
}
