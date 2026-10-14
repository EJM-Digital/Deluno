using System.Reflection;
using System.Text.RegularExpressions;
using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Persistence.Tests.Migrations.Upgrade;

/// <summary>
/// #1274. The register of data-changing migrations cannot fall behind the migrations. A new migration that updates,
/// deletes, copies, drops or runs code against existing rows must be listed in <see cref="DataChangingMigrations"/>,
/// and what it lists must name tests that exist.
/// </summary>
public sealed partial class EveryDataChangingMigrationHasItsTestTests
{
    [GeneratedRegex(@"--[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex SqlComments();

    [GeneratedRegex(@"CREATE\s+TRIGGER\b.*?\bEND\s*;", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TriggerDefinitions();

    [GeneratedRegex(@"\b(UPDATE\s+\S+\s+SET|DELETE\s+FROM|INSERT\s+(OR\s+\w+\s+)?INTO|REPLACE\s+INTO|DROP\s+(TABLE|COLUMN)|ALTER\s+TABLE\s+\S+\s+(RENAME|DROP))\b", RegexOptions.IgnoreCase)]
    private static partial Regex DataStatements();

    [Fact]
    public void Every_registered_migration_exists_once_and_names_at_least_one_test()
    {
        foreach (var change in DataChangingMigrations.All)
        {
            var database = MigrationUpgradeHarness.Database(change.Database);
            Assert.True(database.Migrations.Any(migration => migration.Version == change.Version), $"{change.Database} V{change.Version:0000} is registered but there is no such migration.");
            Assert.True(change.Tests.Length > 0, $"{change.Database} V{change.Version:0000} names no test.");
        }

        Assert.Equal(
            DataChangingMigrations.All.Count,
            DataChangingMigrations.All.Select(change => (change.Database, change.Version)).Distinct().Count());
    }

    [Fact]
    public void Every_test_a_registered_migration_names_exists_in_this_test_assembly()
    {
        var missing = new List<string>();
        foreach (var change in DataChangingMigrations.All)
        {
            foreach (var reference in change.Tests)
            {
                var parts = reference.Split('.', 2);
                var type = typeof(EveryDataChangingMigrationHasItsTestTests).Assembly.GetTypes().SingleOrDefault(candidate => candidate.Name == parts[0]);
                var tests = type?.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Where(method => method.GetCustomAttributes<FactAttribute>().Any())
                    .Where(method => parts.Length == 1 || method.Name.StartsWith(parts[1], StringComparison.Ordinal));
                if (tests is null || !tests.Any())
                {
                    missing.Add($"{change.Database} V{change.Version:0000}: {reference}");
                }
            }
        }

        Assert.True(missing.Count == 0, "Registered tests that do not exist: " + string.Join("; ", missing));
    }

    [Fact]
    public void Every_migration_that_runs_code_or_changes_rows_is_in_the_register()
    {
        var unregistered = new List<string>();
        foreach (var database in MigrationUpgradeHarness.Databases)
        {
            foreach (var migration in database.Migrations)
            {
                if (DataChangingMigrations.Find(database.Name, migration.Version) is not null)
                {
                    continue;
                }

                if (ChangesRows(migration))
                {
                    unregistered.Add($"{database.Name} V{migration.Version:0000} {migration.Name}");
                }
            }
        }

        Assert.True(unregistered.Count == 0, "Migrations that change rows but are not in DataChangingMigrations: " + string.Join("; ", unregistered));
    }

    [Fact]
    public void The_schema_only_migrations_are_everything_the_register_does_not_list_and_each_is_tried_with_rows_in_place()
    {
        var all = MigrationUpgradeHarness.Databases.Sum(database => database.Migrations.Count);
        var changing = DataChangingMigrations.All.Count;
        var generated = MigrationUpgradeHarness.EveryMigration().Count();

        Assert.Equal(all, generated);
        Assert.True(all - changing > 0);
    }

    private static bool ChangesRows(IDelunoDatabaseMigration migration)
    {
        if (migration is not SqliteSqlMigration)
        {
            return true;
        }

        var sql = (string)typeof(SqliteSqlMigration).GetProperty("Sql", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(migration)!;
        sql = SqlComments().Replace(sql, " ");
        sql = TriggerDefinitions().Replace(sql, " ");
        return DataStatements().IsMatch(sql);
    }
}
