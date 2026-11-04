using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

public sealed class PlatformStartupAndKeptDownloadsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-05T09:00:00Z");

    [Fact]
    public async Task The_platform_database_is_brought_up_to_date_and_the_gate_is_told_it_is_ready()
    {
        using var storage = TestStorage.Create();
        var gate = new RecordingGate();
        var initializer = new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, new FixedTimeProvider(Now)),
            NullLogger<PlatformSchemaInitializer>.Instance,
            gate);

        await initializer.StartAsync(CancellationToken.None);
        await initializer.StopAsync(CancellationToken.None);

        Assert.Equal([DelunoDatabaseNames.Platform], gate.Ready);
        Assert.Empty(gate.Failed);
    }

    [Fact]
    public async Task Without_a_gate_the_database_is_still_brought_up_to_date()
    {
        using var storage = TestStorage.Create();

        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, new FixedTimeProvider(Now)),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        Assert.Empty(await new SqliteKeptDownloadsRepository(storage.Factory, new FixedTimeProvider(Now)).ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_migration_that_fails_is_recorded_on_the_gate_and_the_failure_is_not_hidden()
    {
        using var storage = TestStorage.Create();
        var gate = new RecordingGate();
        var initializer = new PlatformSchemaInitializer(
            storage.Factory,
            new FailingMigrator(),
            NullLogger<PlatformSchemaInitializer>.Instance,
            gate);

        await Assert.ThrowsAsync<InvalidOperationException>(() => initializer.StartAsync(CancellationToken.None));

        Assert.Empty(gate.Ready);
        Assert.Equal("no room", gate.Failed.Single().Reason.Message);
        Assert.Equal(DelunoDatabaseNames.Platform, gate.Failed.Single().Database);
    }

    [Fact]
    public async Task A_failure_without_a_gate_is_still_not_hidden()
    {
        using var storage = TestStorage.Create();
        var initializer = new PlatformSchemaInitializer(storage.Factory, new FailingMigrator(), NullLogger<PlatformSchemaInitializer>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => initializer.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_download_kept_by_choice_is_listed_once_whatever_its_letter_case_and_can_be_forgotten()
    {
        using var storage = TestStorage.Create();
        var repository = await NewRepositoryAsync(storage);

        Assert.Empty(await repository.ListAsync(CancellationToken.None));

        await repository.KeepAsync("/downloads/Film", CancellationToken.None);
        await repository.KeepAsync("/DOWNLOADS/film", CancellationToken.None);
        await repository.KeepAsync("/downloads/Other", CancellationToken.None);

        Assert.Equal(["/downloads/Film", "/downloads/Other"], await repository.ListAsync(CancellationToken.None));

        await repository.ForgetAsync("/DOWNLOADS/FILM", CancellationToken.None);
        await repository.ForgetAsync("/downloads/never-kept", CancellationToken.None);

        Assert.Equal(["/downloads/Other"], await repository.ListAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("null")]
    public async Task A_saved_list_that_cannot_be_read_is_an_empty_list(string saved)
    {
        using var storage = TestStorage.Create();
        var repository = await NewRepositoryAsync(storage);
        await using (var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform, CancellationToken.None))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO system_settings (setting_key, setting_value, updated_utc) VALUES ('downloads.orphans.kept.v1', @value, '2026-01-01T00:00:00Z');";
            SqliteRecordHelpers.AddParameter(command, "@value", saved);
            await command.ExecuteNonQueryAsync();
        }

        Assert.Empty(await repository.ListAsync(CancellationToken.None));
    }

    private static async Task<SqliteKeptDownloadsRepository> NewRepositoryAsync(TestStorage storage)
    {
        var time = new FixedTimeProvider(Now);
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, time),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return new SqliteKeptDownloadsRepository(storage.Factory, time);
    }

    private sealed class FailingMigrator : IDelunoDatabaseMigrator
    {
        public Task ApplyAsync(string databaseName, IReadOnlyList<IDelunoDatabaseMigration> migrations, CancellationToken cancellationToken)
            => throw new InvalidOperationException("no room");
    }

    private sealed class RecordingGate : IDelunoStartupGate
    {
        public List<string> Ready { get; } = [];

        public List<(string Database, Exception Reason)> Failed { get; } = [];

        public bool IsReady => false;

        public IReadOnlyList<string> PendingDatabases => [];

        public IReadOnlyDictionary<string, string> FailedDatabases => new Dictionary<string, string>();

        public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => Task.CompletedTask;

        public void MarkReady(string databaseName) => Ready.Add(databaseName);

        public void MarkFailed(string databaseName, Exception exception) => Failed.Add((databaseName, exception));
    }
}
