using System.Diagnostics;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Libraries.Data;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Deluno.Persistence.Tests.Media;

/// <summary>
/// What the subtitle bar costs the catalogue page, measured rather than claimed.
///
/// James: "I don't want to burn memory or CPU cycles unnecessarily, and routes
/// and functions inside Deluno should not fight for processing power or
/// schedules." The two claims this feature makes are that the page stays a seek
/// and that a shelf nobody has asked for subtitles pays nothing at all. Both are
/// checkable, so they are checked here at the size the design notes keep
/// invoking.
///
/// Read the numbers with:
/// <c>dotnet test --filter SubtitleScaleBenchmark -l "console;verbosity=detailed"</c>
/// </summary>
public sealed class SubtitleScaleBenchmark(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-27T00:00:00Z");

    [Fact]
    public async Task Twenty_thousand_films_page_the_same_whether_or_not_subtitles_are_wanted()
    {
        const int total = 20_000;
        using var storage = TestStorage.Create();

        var timeProvider = new FixedTimeProvider(Now);
        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        await SeedAsync(storage, total);

        var off = new SqliteMovieCatalogRepository(
            storage.Factory, timeProvider, null, new StubPreferences([]));
        var on = new SqliteMovieCatalogRepository(
            storage.Factory, timeProvider, null, new StubPreferences(["en", "ja"]));

        // Warm the page path once so the first measurement is not paying for
        // SQLite opening the file and preparing statements.
        await off.ListPageAsync(new CatalogueQuery(PageSize: 100), CancellationToken.None);

        var (offMs, offWorst) = await WalkAsync(off, total);
        output.WriteLine($"no languages wanted   {offMs,6:N0} ms for {total / 100} pages ({offWorst} ms worst page)");

        var (onMs, onWorst) = await WalkAsync(on, total);
        output.WriteLine($"two languages wanted  {onMs,6:N0} ms for {total / 100} pages ({onWorst} ms worst page)");
        output.WriteLine($"subtitles cost        {onMs - offMs,6:N0} ms across the whole walk");

        var withBars = await on.ListPageAsync(new CatalogueQuery(PageSize: 100), CancellationToken.None);
        Assert.All(withBars.Items, item => Assert.Equal(2, item.SubtitleLanguagesWanted));
        // Half the seeded films hold English; none holds Japanese.
        Assert.Contains(withBars.Items, item => item.SubtitleLanguagesHeld == 1);

        var withoutBars = await off.ListPageAsync(new CatalogueQuery(PageSize: 100), CancellationToken.None);
        Assert.All(withoutBars.Items, item => Assert.Equal(0, item.SubtitleLanguagesWanted));

        // Deliberately loose, like the benchmark next door: a guard against an
        // order-of-magnitude regression, not a target, on hardware of unknown
        // speed.
        Assert.True(
            onWorst < 250,
            $"A page with subtitles wanted took {onWorst} ms at {total:N0} titles, which suggests the rollup has become a scan.");
    }

    /// <summary>
    /// The one cost a shelf pays even when nobody has asked it for subtitles:
    /// the page has to read what the libraries want before it can decide there
    /// is nothing to do. Small, and a per-page round trip, so it is measured
    /// rather than assumed to be free.
    /// </summary>
    [Fact]
    public async Task Asking_the_libraries_what_they_want_costs_a_fraction_of_a_page()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(Now);

        await new Deluno.Platform.Data.PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<Deluno.Platform.Data.PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var libraries = new SqliteLibrariesRepository(storage.Factory, timeProvider);
        await libraries.GetSubtitlePreferencesAsync(CancellationToken.None);

        const int reads = 200;
        var clock = Stopwatch.StartNew();
        for (var index = 0; index < reads; index++)
        {
            await libraries.GetSubtitlePreferencesAsync(CancellationToken.None);
        }

        clock.Stop();
        output.WriteLine($"library preferences   {clock.Elapsed.TotalMilliseconds / reads:F3} ms per page");

        Assert.True(
            clock.Elapsed.TotalMilliseconds / reads < 5,
            "Reading what the libraries want is on the catalogue page's path, so it has to stay a rounding error.");
    }

    private static async Task<(long TotalMs, long WorstPageMs)> WalkAsync(IMovieCatalogRepository movies, int total)
    {
        string? token = null;
        long worst = 0;
        var clock = Stopwatch.StartNew();

        do
        {
            var pageClock = Stopwatch.StartNew();
            var page = await movies.ListPageAsync(
                new CatalogueQuery(PageSize: 100, PageToken: token), CancellationToken.None);
            pageClock.Stop();
            worst = Math.Max(worst, pageClock.ElapsedMilliseconds);
            token = page.NextPageToken;
        }
        while (token is not null);

        clock.Stop();
        return (clock.ElapsedMilliseconds, worst);
    }

    private static async Task SeedAsync(TestStorage storage, int total)
    {
        await using var connection = await storage.Factory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies, CancellationToken.None);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);

        const string now = "2026-08-27T00:00:00.0000000+00:00";

        for (var index = 0; index < total; index++)
        {
            var id = $"movie-{index:D6}";

            using (var entry = connection.CreateCommand())
            {
                entry.Transaction = transaction;
                entry.CommandText =
                    "INSERT INTO movie_entries (id, title, release_year, monitored, created_utc, updated_utc) " +
                    "VALUES (@id, @title, @year, 1, @now, @now);";
                AddParameter(entry, "@id", id);
                AddParameter(entry, "@title", $"Title {index:D6}");
                AddParameter(entry, "@year", 1990 + (index % 30));
                AddParameter(entry, "@now", now);
                await entry.ExecuteNonQueryAsync(CancellationToken.None);
            }

            using (var wanted = connection.CreateCommand())
            {
                wanted.Transaction = transaction;
                wanted.CommandText =
                    "INSERT INTO movie_wanted_state " +
                    "(movie_id, library_id, wanted_status, wanted_reason, has_file, current_quality, target_quality, quality_cutoff_met, file_path, file_size_bytes, updated_utc) " +
                    "VALUES (@movieId, 'library-films', 'covered', 'seeded', 1, 'WEB 1080p', 'WEB 2160p', 1, @filePath, 1024, @now);";
                AddParameter(wanted, "@movieId", id);
                AddParameter(wanted, "@filePath", $@"D:\Media\Title {index:D6}\Title {index:D6}.mkv");
                AddParameter(wanted, "@now", now);
                await wanted.ExecuteNonQueryAsync(CancellationToken.None);
            }

            // Half the library holds an English track, which is roughly what a
            // real library looks like and keeps the rollup returning rows for
            // most of the pages it walks.
            if (index % 2 == 0)
            {
                using var subtitle = connection.CreateCommand();
                subtitle.Transaction = transaction;
                subtitle.CommandText =
                    "INSERT INTO movie_subtitle_state " +
                    "(movie_id, language, forced, hearing_impaired, source, stream_index, codec, created_utc, updated_utc) " +
                    "VALUES (@movieId, 'en', 0, 0, 'embedded', 2, 'subrip', @now, @now);";
                AddParameter(subtitle, "@movieId", id);
                AddParameter(subtitle, "@now", now);
                await subtitle.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }

        await transaction.CommitAsync(CancellationToken.None);
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private sealed class StubPreferences(string[] languages) : ILibrarySubtitlePreferences
    {
        public Task<IReadOnlyDictionary<string, LibrarySubtitlePreference>> GetSubtitlePreferencesAsync(
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyDictionary<string, LibrarySubtitlePreference>>(
                new Dictionary<string, LibrarySubtitlePreference>(StringComparer.OrdinalIgnoreCase)
                {
                    ["library-films"] = new("library-films", languages, SubtitleLanguageModes.All)
                });
    }
}
