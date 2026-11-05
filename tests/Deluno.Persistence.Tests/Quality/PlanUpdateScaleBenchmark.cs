using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Media;
using Deluno.Movies.Data;
using Deluno.Movies.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Quality.Contracts;
using Deluno.Quality.Guides;
using Deluno.Quality.ReleasePreferences;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Deluno.Persistence.Tests.Quality;

/// <summary>
/// #352 line 4: performance budgets pass at 20,000 titles.
///
/// <para>The catalogue benchmark measures reading a library of that size. This
/// measures the thing a plan change actually costs: compiling the new plan
/// once, and re-deciding every held title under it. Both are the operations
/// the issue names, and both are the ones that would quietly become a
/// per-row query.</para>
///
/// <para>Read the numbers with:
/// <c>dotnet test --filter PlanUpdateScaleBenchmark -l "console;verbosity=detailed"</c></para>
/// </summary>
public sealed class PlanUpdateScaleBenchmark(ITestOutputHelper output)
{
    /// <summary>
    /// How many database commits re-deciding the whole library may take.
    ///
    /// <para>This test used to compare the time re-deciding took with the time
    /// of a floor measurement taken minutes later, and failed CI on loaded
    /// runners (8.35x against a budget of 6x): the two operations do not slow
    /// alike under contention, so any ratio of times moves with the load.
    /// What it defends is a shape, and a shape can be counted. Re-deciding is
    /// written in a few transactions; the regression is a transaction per
    /// title (9 seconds against 1.6 for a 20,000-title plan change), which is
    /// a commit per title. Commits do not depend on how busy the machine is, so
    /// this cannot flake, and a per-row write fails it by three orders of
    /// magnitude.</para>
    /// </summary>
    private const int MaxCommitsToRedecideTheLibrary = 20;

    [Theory]
    [InlineData(5_000)]
    [InlineData(20_000)]
    public async Task Re_deciding_a_library_of_this_size_after_a_plan_update_stays_inside_budget(int total)
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-03T00:00:00Z"));
        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, clock);

        await SeedAsync(storage, total);
        output.WriteLine($"seeded {total:N0} held titles");

        // Compiling the plan is once per change, not once per title.
        var package = GuidePackageCatalog.Current;
        var profile = package.QualityProfiles[0];
        var compiled = GuidePlanCompiler.Compile(profile.Id, profile.MediaType, package);
        Assert.NotEmpty(compiled.Plan.OrderedFamilies);
        output.WriteLine($"compiled the new plan ({compiled.Plan.OrderedFamilies.Count} families)");

        // And the part that does scale: every held title re-decided under the
        // activated plan, which is what has to finish before automatic
        // upgrades resume. Counted in commits, not seconds.
        var commitsBefore = storage.Factory.GetWriteGeneration(DelunoDatabaseNames.Movies);
        var updated = await movies.ReevaluateLibraryWantedStateAsync(
            "library-films",
            cutoffQuality: "Bluray 2160p",
            upgradeUntilCutoff: true,
            upgradeUnknownItems: true,
            CancellationToken.None);
        var commits = storage.Factory.GetWriteGeneration(DelunoDatabaseNames.Movies) - commitsBefore;

        Assert.Equal(total, updated);
        output.WriteLine($"re-decided {updated:N0} titles in {commits} commit(s) (budget {MaxCommitsToRedecideTheLibrary})");

        Assert.True(
            commits <= MaxCommitsToRedecideTheLibrary,
            $"Re-deciding {total:N0} titles took {commits} commits. It should write in a few transactions; "
            + "a commit per title means it is writing a row at a time again.");

        // A second pass over an already-settled library, because a plan update
        // is not the only time this runs.
        var settledBefore = storage.Factory.GetWriteGeneration(DelunoDatabaseNames.Movies);
        await movies.ReevaluateLibraryWantedStateAsync(
            "library-films",
            cutoffQuality: "Bluray 2160p",
            upgradeUntilCutoff: true,
            upgradeUnknownItems: true,
            CancellationToken.None);
        var settledCommits = storage.Factory.GetWriteGeneration(DelunoDatabaseNames.Movies) - settledBefore;
        output.WriteLine($"settled second pass took {settledCommits} commit(s)");
        Assert.True(settledCommits <= MaxCommitsToRedecideTheLibrary, $"The settled second pass took {settledCommits} commits.");
    }

    private static async Task SeedAsync(TestStorage storage, int total)
    {
        await using var connection = await storage.Factory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies, CancellationToken.None);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);

        const string now = "2026-09-03T00:00:00.0000000+00:00";

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

            using var wanted = connection.CreateCommand();
            wanted.Transaction = transaction;
            wanted.CommandText =
                "INSERT INTO movie_wanted_state " +
                "(movie_id, library_id, wanted_status, wanted_reason, has_file, current_quality, target_quality, quality_cutoff_met, updated_utc) " +
                "VALUES (@movieId, 'library-films', @status, 'seeded', 1, @currentQuality, 'WEB 1080p', @cutoffMet, @now);";
            AddParameter(wanted, "@movieId", id);
            // Every row holds a file, because a plan update only has to
            // re-decide the titles it can decide anything about. A spread of
            // tiers so the answer is not the same for all of them.
            var rung = index % 3;
            AddParameter(wanted, "@status", rung == 2 ? WantedStatuses.Covered : WantedStatuses.Upgrade);
            AddParameter(wanted, "@currentQuality", rung switch
            {
                0 => "WEB 720p",
                1 => "WEB 1080p",
                _ => "Bluray 1080p"
            });
            AddParameter(wanted, "@cutoffMet", rung == 2 ? 1 : 0);
            AddParameter(wanted, "@now", now);
            await wanted.ExecuteNonQueryAsync(CancellationToken.None);
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
}
