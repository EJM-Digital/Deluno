using System.Diagnostics;
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
    /// How many times the floor re-deciding is allowed to cost.
    ///
    /// <para>This was 4 and failed CI on a change to a stylesheet: 837 µs per
    /// title against a 175 µs floor, a ratio of 4.77, in a run where seeding
    /// 20,000 rows took 100 seconds. Relative-to-a-floor was already the right
    /// idea — an absolute millisecond budget here is a coin toss — but it only
    /// cancels the machine out if load is steady across both measurements, and
    /// they are taken minutes apart on a runner shared with three other
    /// jobs. The two operations do not degrade alike under contention, so the
    /// ratio itself moves with the load.</para>
    ///
    /// <para>6 rather than 4, because of what this is for. The regression it
    /// exists to catch is a change of shape — a query per row, or a transaction
    /// per row — and that is an order of magnitude, not a factor of five. A
    /// threshold tight enough to fail on runner noise fails often enough that
    /// people learn to re-run it, which is the same as not having it.</para>
    /// </summary>
    private const double MaxCostOverFloor = 6;

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

        var seeding = Stopwatch.StartNew();
        await SeedAsync(storage, total);
        seeding.Stop();
        output.WriteLine($"seeded {total:N0} held titles in {seeding.ElapsedMilliseconds:N0} ms");

        // Compiling the plan is once per change, not once per title. A plan is
        // cached by its immutable hash, so this is the cost of the change
        // itself and it must not grow with the library.
        var package = GuidePackageCatalog.Current;
        var profile = package.QualityProfiles[0];
        var compile = Stopwatch.StartNew();
        var compiled = GuidePlanCompiler.Compile(profile.Id, profile.MediaType, package);
        compile.Stop();
        Assert.NotEmpty(compiled.Plan.OrderedFamilies);
        output.WriteLine($"compiled the new plan  {compile.ElapsedMilliseconds,6:N0} ms  ({compiled.Plan.OrderedFamilies.Count} families)");

        // And the part that does scale: every held title re-decided under the
        // activated plan, which is what has to finish before automatic
        // upgrades resume.
        var reevaluate = Stopwatch.StartNew();
        var updated = await movies.ReevaluateLibraryWantedStateAsync(
            "library-films",
            cutoffQuality: "Bluray 2160p",
            upgradeUntilCutoff: true,
            upgradeUnknownItems: true,
            CancellationToken.None);
        reevaluate.Stop();

        Assert.Equal(total, updated);
        var perTitleMicroseconds = reevaluate.Elapsed.TotalMilliseconds * 1000 / total;
        output.WriteLine(
            $"re-decided {updated:N0} titles {reevaluate.ElapsedMilliseconds,6:N0} ms  ({perTitleMicroseconds:F1} µs per title)");

        // A second pass over an already-settled library, because a plan update
        // is not the only time this runs.
        var second = Stopwatch.StartNew();
        await movies.ReevaluateLibraryWantedStateAsync(
            "library-films",
            cutoffQuality: "Bluray 2160p",
            upgradeUntilCutoff: true,
            upgradeUnknownItems: true,
            CancellationToken.None);
        second.Stop();
        output.WriteLine($"settled second pass    {second.ElapsedMilliseconds,6:N0} ms");

        // The budget is relative to a floor measured in the same run, not to a
        // number of milliseconds.
        //
        // An absolute budget on a timing test is a coin toss: the whole suite
        // runs in parallel, so the same code costs 88 µs per title alone and
        // several times that under load. The floor below is the cheapest
        // possible shape of this work — one indexed UPDATE per row inside one
        // transaction — measured on the same disk, at the same size, at the
        // same moment. Comparing against it cancels the machine out, and it
        // measures the thing that actually regressed: whether re-deciding a
        // library costs about one write per title, or several.
        var floorMicroseconds = await MeasureSingleTransactionWriteFloorAsync(storage, total);
        var ratio = perTitleMicroseconds / floorMicroseconds;
        output.WriteLine($"floor: one indexed UPDATE per row in one transaction — {floorMicroseconds:F1} µs per row");

        // Printed on every run, pass or fail. The number this test defends is a
        // shape, and a shape drifts long before it breaks: a ratio creeping
        // from 2 to 5 is visible here while it is still cheap to look into,
        // where a bare pass tells nobody anything.
        output.WriteLine($"re-deciding costs {ratio:F2}x the floor (budget {MaxCostOverFloor}x)");

        Assert.True(
            ratio < MaxCostOverFloor,
            $"Re-deciding cost {perTitleMicroseconds:F1} µs per title against a {floorMicroseconds:F1} µs floor "
            + $"at {total:N0} titles — {ratio:F2}x. It should be within a small multiple of one write per row; "
            + "a large multiple means it is writing a row at a time again, or asking a question per row.");
        Assert.True(
            compile.ElapsedMilliseconds < 2_000,
            $"Compiling one plan took {compile.ElapsedMilliseconds:N0} ms, which should not depend on the library at all.");
    }

    /// <summary>
    /// The cheapest shape this work can have on this machine right now: one
    /// indexed UPDATE per row, in one transaction, over the same table.
    /// </summary>
    private static async Task<double> MeasureSingleTransactionWriteFloorAsync(TestStorage storage, int total)
    {
        await using var connection = await storage.Factory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies, CancellationToken.None);
        var clock = Stopwatch.StartNew();
        await using (var transaction = await connection.BeginTransactionAsync(CancellationToken.None))
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                "UPDATE movie_wanted_state SET wanted_reason = @reason WHERE movie_id = @movieId AND library_id = 'library-films';";
            AddParameter(update, "@reason", "floor measurement");
            AddParameter(update, "@movieId", "movie-000000");
            for (var index = 0; index < total; index++)
            {
                update.Parameters["@movieId"].Value = $"movie-{index:D6}";
                await update.ExecuteNonQueryAsync(CancellationToken.None);
            }

            await transaction.CommitAsync(CancellationToken.None);
        }

        clock.Stop();
        return clock.Elapsed.TotalMilliseconds * 1000 / total;
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
