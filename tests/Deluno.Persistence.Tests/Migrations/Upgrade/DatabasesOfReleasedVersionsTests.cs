using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Migrations.Upgrade;

/// <summary>
/// #1274. The newest Deluno opening a database that a released version made. Each row of <see cref="Releases"/> is a set of
/// release tags that shipped the same last migration in each database (read from the tags' migration folders in git, which
/// do not change, so nothing is fetched). The database is built with the migrations up to that point, every table is given a
/// row, then every later migration is applied in order and each step must keep every row it is not registered to change.
/// </summary>
public sealed class DatabasesOfReleasedVersionsTests
{
    /// <summary>Tags, then the last migration shipped: platform, jobs, movies, series, cache.</summary>
    public static readonly (string Tags, int Platform, int Jobs, int Movies, int Series, int Cache)[] Releases =
    [
        ("v0.1.0", 8, 8, 5, 5, 1),
        ("v0.1.1 to v0.1.4", 9, 9, 5, 5, 1),
        ("v0.1.5, v1.0.0-rc1, v1.0.0-rc2, v1.0.0 to v1.0.3, v1.1.0 to v1.1.2", 10, 9, 5, 5, 1),
        ("v1.0.0-rc.1, v1.0.0-rc.2", 50, 25, 35, 38, 1),
        ("v1.0.0-rc.3", 56, 30, 36, 39, 1),
        ("v1.0.0-rc.4 to rc.6", 56, 30, 37, 40, 1),
        ("v1.0.0-rc.7", 56, 31, 37, 40, 1),
        ("v1.0.0-rc.8 to rc.10", 64, 31, 38, 41, 1),
        ("v1.0.0-rc.11 to rc.16", 65, 31, 38, 41, 1),
        ("v1.0.0-rc.17 to rc.20", 66, 32, 38, 41, 1),
        ("v1.0.0-rc.21", 67, 32, 39, 42, 1),
        ("v1.0.0-rc.22", 69, 33, 39, 42, 1),
        ("v1.0.0-rc.23", 135, 38, 49, 52, 1),
        ("v1.0.0-rc.24", 135, 41, 51, 54, 1),
        ("v1.0.0-rc.25, rc.26", 136, 50, 51, 54, 1),
        ("v1.0.0-rc.27 to rc.30", 136, 52, 51, 54, 1),
        ("v1.0.0-rc.30.14, rc.30.15", 137, 53, 51, 54, 1),
        ("v1.0.0-rc.30.16 to rc.30.20", 138, 53, 51, 54, 1)
    ];

    public static IEnumerable<object[]> EveryReleaseAndDatabase() =>
        Releases.SelectMany((release, index) => new[]
        {
            new object[] { index, DelunoDatabaseNames.Platform },
            [index, DelunoDatabaseNames.Jobs],
            [index, DelunoDatabaseNames.Movies],
            [index, DelunoDatabaseNames.Series],
            [index, DelunoDatabaseNames.Cache]
        });

    private static int LastMigration((string Tags, int Platform, int Jobs, int Movies, int Series, int Cache) release, string database) => database switch
    {
        DelunoDatabaseNames.Platform => release.Platform,
        DelunoDatabaseNames.Jobs => release.Jobs,
        DelunoDatabaseNames.Movies => release.Movies,
        DelunoDatabaseNames.Series => release.Series,
        _ => release.Cache
    };

    [Theory]
    [MemberData(nameof(EveryReleaseAndDatabase))]
    public async Task A_database_a_released_version_made_opens_in_the_newest_Deluno_with_every_row_kept(int releaseIndex, string databaseName)
    {
        var release = Releases[releaseIndex];
        var database = MigrationUpgradeHarness.Database(databaseName);
        var shipped = LastMigration(release, databaseName);
        Assert.Contains(database.Migrations, migration => migration.Version == shipped);

        using var storage = TestStorage.Create();
        await MigrationUpgradeHarness.ApplyThroughAsync(storage, database, shipped);
        await using var connection = await MigrationUpgradeHarness.OpenAsync(storage, databaseName);
        await MigrationUpgradeHarness.SeedEveryTableAsync(connection);

        var problems = new List<string>();
        foreach (var migration in database.Migrations.Where(migration => migration.Version > shipped).OrderBy(migration => migration.Version))
        {
            var before = await MigrationUpgradeHarness.TakeSnapshotAsync(connection);
            await MigrationUpgradeHarness.ApplyOnlyAsync(storage, database, migration.Version);
            var after = await MigrationUpgradeHarness.TakeSnapshotAsync(connection);

            var allowed = new HashSet<string>(DataChangingMigrations.Find(databaseName, migration.Version)?.Allowed ?? [], StringComparer.Ordinal);
            problems.AddRange(MigrationUpgradeHarness.LostOrChanged(before, after, allowed).Select(problem => $"V{migration.Version:0000}: {problem}"));
        }

        Assert.True(problems.Count == 0, $"{release.Tags} ({databaseName} at V{shipped:0000}): {string.Join("; ", problems)}");
        Assert.Equal(database.Migrations.Count, await MigrationUpgradeHarness.ScalarAsync(connection, "SELECT COUNT(*) FROM schema_migrations;") is long applied ? (int)applied : -1);
    }
}
