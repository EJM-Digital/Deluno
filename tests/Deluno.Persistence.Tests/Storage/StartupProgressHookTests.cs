using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Deluno.Persistence.Tests.Storage;

/// <summary>
/// #1256: the shared start-up code reports the step it is about to take, so the tray (or the Windows service) can
/// tell a slow start from a hung one. A host that registers nothing is unaffected.
/// </summary>
public sealed class StartupProgressHookTests
{
    private sealed class Recorder : IStartupProgress
    {
        public List<(StartupPhase Phase, string? Detail)> Steps { get; } = [];
        public void Enter(StartupPhase phase, string? detail = null) => Steps.Add((phase, detail));
    }

    private sealed class ProbeMigration(int version, string name) : SqliteSqlMigration
    {
        public override int Version => version;
        public override string Name => name;
        protected override string Sql => $"CREATE TABLE probe_{version} (id INTEGER PRIMARY KEY);";
    }

    [Fact]
    public async Task The_migrator_reports_each_migration_before_it_runs()
    {
        using var storage = TestStorage.Create();
        var progress = new Recorder();
        var migrator = new SqliteDatabaseMigrator(storage.Factory, TimeProvider.System, null, null, progress);

        await migrator.ApplyAsync(
            DelunoDatabaseNames.Platform,
            [new ProbeMigration(1, "first"), new ProbeMigration(2, "second")],
            CancellationToken.None);

        Assert.Equal(
            [
                (StartupPhase.Migrations, "platform: checking the changes to apply"),
                (StartupPhase.Migrations, "platform: V0001 first"),
                (StartupPhase.Migrations, "platform: V0002 second")
            ],
            progress.Steps);
    }

    [Fact]
    public async Task A_migration_that_was_already_applied_is_not_reported_again()
    {
        using var storage = TestStorage.Create();
        var progress = new Recorder();
        var migrator = new SqliteDatabaseMigrator(storage.Factory, TimeProvider.System, null, null, progress);
        await migrator.ApplyAsync(DelunoDatabaseNames.Platform, [new ProbeMigration(1, "first")], CancellationToken.None);
        progress.Steps.Clear();

        await migrator.ApplyAsync(DelunoDatabaseNames.Platform, [new ProbeMigration(1, "first")], CancellationToken.None);

        Assert.DoesNotContain(progress.Steps, step => step.Detail!.Contains("V0001"));
    }

    [Fact]
    public async Task Opening_the_databases_reports_each_database_before_it_is_opened()
    {
        using var storage = TestStorage.Create();
        var progress = new Recorder();
        var service = new DelunoStorageBootstrapService(
            storage.Factory,
            Options.Create(new StoragePathOptions { DataRoot = storage.DataRoot }),
            NullLogger<DelunoStorageBootstrapService>.Instance,
            null,
            progress);

        await service.StartAsync(CancellationToken.None);

        var expected = DelunoStorageLayout.Databases.Select(d => (StartupPhase.OpeningDatabases, (string?)d.Key)).ToList();
        Assert.Equal(expected, progress.Steps);
    }

    [Fact]
    public void When_every_database_is_ready_the_start_moves_on_to_the_web_host()
    {
        var progress = new Recorder();
        var gate = new DelunoStartupGate(progress);

        foreach (var database in DelunoStorageLayout.Databases.Take(DelunoStorageLayout.Databases.Count - 1))
        {
            gate.MarkReady(database.Key);
        }

        Assert.Empty(progress.Steps);
        gate.MarkReady(DelunoStorageLayout.Databases.Last().Key);

        Assert.Equal([(StartupPhase.WebHost, (string?)"the databases are ready")], progress.Steps);
        gate.MarkReady(DelunoStorageLayout.Databases.Last().Key);
        Assert.Single(progress.Steps);
    }

    [Fact]
    public async Task Hosts_that_register_no_progress_behave_as_before()
    {
        using var storage = TestStorage.Create();
        var migrator = new SqliteDatabaseMigrator(storage.Factory, TimeProvider.System);
        await migrator.ApplyAsync(DelunoDatabaseNames.Platform, [new ProbeMigration(1, "first")], CancellationToken.None);

        var gate = new DelunoStartupGate();
        foreach (var database in DelunoStorageLayout.Databases)
        {
            gate.MarkReady(database.Key);
        }

        Assert.True(gate.IsReady);
    }
}
