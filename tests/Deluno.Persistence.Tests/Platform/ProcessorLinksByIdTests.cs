using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Platform.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// #1077: a library, a hand-off and a recorded outcome link to their processor
/// by the connection's id. Renaming the connection changes nothing.
/// </summary>
public sealed class ProcessorLinksByIdTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-30T02:00:00Z");

    [Fact]
    public async Task Renaming_a_processor_keeps_its_library_its_hand_offs_and_its_outcomes()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Now);
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var processors = new SqliteProcessorRepository(storage.Factory, clock, TestSecretProtection.Create(storage));
        var libraries = new SqliteLibrariesRepository(storage.Factory, clock);

        var weir = await processors.CreateProcessorConnectionAsync(
            new CreateProcessorConnectionRequest("Weir", "generic-webhook", "https://weir.example.test/hook", "X-Api-Key", "s3cret", IsEnabled: true),
            CancellationToken.None);

        // Older callers still send the name; it is turned into the id.
        var library = await libraries.CreateLibraryAsync(
            new CreateLibraryRequest(
                "Movies", "movies", "General", "/media/movies", "/downloads/movies", null,
                "refine-before-import", weir.Name, "/processed/movies", 360, "block",
                true, true, true, 360, 24, 25),
            CancellationToken.None);
        Assert.Equal(weir.Id, library.ProcessorConnectionId);
        Assert.Equal(weir.Name, library.ProcessorName);

        var handoff = await processors.EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(
                library.Id, "movies", "client-1", "queue-1", "Sintel.2010.1080p", "/downloads/movies/Sintel",
                library.ProcessorName, Now, library.ProcessorConnectionId),
            CancellationToken.None);
        Assert.Equal(weir.Id, handoff.ProcessorConnectionId);

        await processors.SaveProcessorOutcomeAsync(
            new ProcessorOutcomeRecord(
                handoff.Id, "Weir", library.Id, handoff.ReleaseName, ProcessorOutcomeRecord.Imported, "/media/movies/Sintel.mkv", null,
                Now, ProcessorOutcomeDeliveries.Pending, 0, null, Now, false, null, Now, weir.Id),
            CancellationToken.None);

        // A name is worked out from the address now, so the rename is done straight in the table, as an older install's typed name would be.
        await using (var db = await storage.Factory.OpenConnectionAsync(Deluno.Infrastructure.Storage.DelunoDatabaseNames.Platform, CancellationToken.None))
        {
            await using var rename = db.CreateCommand();
            rename.CommandText = "UPDATE processor_connections SET name = 'Media cleaner' WHERE id = @id;";
            var parameter = rename.CreateParameter();
            parameter.ParameterName = "@id";
            parameter.Value = weir.Id;
            rename.Parameters.Add(parameter);
            await rename.ExecuteNonQueryAsync();
        }

        // Nothing looks the old name up any more, and the old name finds nothing.
        Assert.Null(await processors.FindProcessorConnectionByNameAsync(weir.Name, CancellationToken.None));

        var reloaded = Assert.Single(await libraries.ListLibrariesAsync(CancellationToken.None));
        Assert.Equal(weir.Id, reloaded.ProcessorConnectionId);
        Assert.Equal("Media cleaner", reloaded.ProcessorName);
        Assert.Equal(weir.Id, (await processors.FindProcessorConnectionAsync(reloaded.ProcessorConnectionId, "Weir", CancellationToken.None))!.Id);

        var reloadedHandoff = (await processors.GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!;
        Assert.Equal(weir.Id, reloadedHandoff.ProcessorConnectionId);
        Assert.Equal("Media cleaner", reloadedHandoff.ProcessorName);
        Assert.Equal(weir.Id, (await processors.FindProcessorConnectionAsync(reloadedHandoff.ProcessorConnectionId, "Weir", CancellationToken.None))!.Id);

        var outcome = (await processors.GetProcessorOutcomeAsync(handoff.Id, CancellationToken.None))!;
        Assert.Equal(weir.Id, outcome.ProcessorConnectionId);
        Assert.Equal("Media cleaner", outcome.ProcessorName);

        // A new download of the same release after the rename is still handed to the same processor.
        var again = await processors.EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(
                library.Id, "movies", "client-1", "queue-2", "Sintel.2010.1080p", "/downloads/movies/Sintel2",
                reloaded.ProcessorName, Now, reloaded.ProcessorConnectionId),
            CancellationToken.None);
        Assert.Equal(weir.Id, again.ProcessorConnectionId);
    }

    [Fact]
    public async Task Saving_a_library_workflow_by_id_wins_over_a_stale_name()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Now);
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var processors = new SqliteProcessorRepository(storage.Factory, clock, TestSecretProtection.Create(storage));
        var libraries = new SqliteLibrariesRepository(storage.Factory, clock);
        var first = await processors.CreateProcessorConnectionAsync(
            new CreateProcessorConnectionRequest("First", "generic-webhook", "https://a.example.test/hook", "X-Api-Key", null, IsEnabled: true), CancellationToken.None);
        var second = await processors.CreateProcessorConnectionAsync(
            new CreateProcessorConnectionRequest("Second", "generic-webhook", "https://b.example.test/hook", "X-Api-Key", null, IsEnabled: true), CancellationToken.None);
        var library = await libraries.CreateLibraryAsync(
            new CreateLibraryRequest("Movies", "movies", "General", "/media/movies", null, null, "refine-before-import", null, "/processed", 360, "block", true, true, true, 360, 24, 25,
                ProcessorConnectionId: first.Id),
            CancellationToken.None);
        Assert.Equal(first.Name, library.ProcessorName);

        var updated = await libraries.UpdateLibraryWorkflowAsync(
            library.Id,
            new UpdateLibraryWorkflowRequest("refine-before-import", "First", "/processed", 360, "block", ProcessorConnectionId: second.Id),
            CancellationToken.None);

        Assert.Equal(second.Id, updated!.ProcessorConnectionId);
        Assert.Equal(second.Name, updated.ProcessorName);
    }

    [Fact]
    public async Task The_upgrade_maps_each_stored_name_to_its_connection_id_and_leaves_a_name_that_matches_nothing()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE processor_connections (id TEXT PRIMARY KEY, name TEXT NOT NULL, created_utc TEXT NOT NULL);" +
                "CREATE TABLE libraries (id TEXT PRIMARY KEY, processor_name TEXT NULL);" +
                "CREATE TABLE processor_handoffs (id TEXT PRIMARY KEY, processor_name TEXT NULL);" +
                "CREATE TABLE processor_handoff_outcomes (handoff_id TEXT PRIMARY KEY, processor_name TEXT NOT NULL);" +
                "INSERT INTO processor_connections VALUES ('c1', 'Weir', '2026-01-01T00:00:00Z');" +
                "INSERT INTO libraries VALUES ('l1', 'weir'), ('l2', 'Gone'), ('l3', NULL);" +
                "INSERT INTO processor_handoffs VALUES ('h1', 'WEIR'), ('h2', 'Gone');" +
                "INSERT INTO processor_handoff_outcomes VALUES ('h1', 'Weir');";
            await create.ExecuteNonQueryAsync();
        }

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await new V0132ProcessorLinksById().UpAsync(connection, transaction, CancellationToken.None);
            await transaction.CommitAsync();
        }

        Assert.Equal("c1", await ScalarAsync(connection, "SELECT processor_connection_id FROM libraries WHERE id = 'l1'"));
        Assert.Null(await ScalarAsync(connection, "SELECT processor_connection_id FROM libraries WHERE id = 'l2'"));
        Assert.Equal("Gone", await ScalarAsync(connection, "SELECT processor_name FROM libraries WHERE id = 'l2'"));
        Assert.Null(await ScalarAsync(connection, "SELECT processor_connection_id FROM libraries WHERE id = 'l3'"));
        Assert.Equal("c1", await ScalarAsync(connection, "SELECT processor_connection_id FROM processor_handoffs WHERE id = 'h1'"));
        Assert.Null(await ScalarAsync(connection, "SELECT processor_connection_id FROM processor_handoffs WHERE id = 'h2'"));
        Assert.Equal("c1", await ScalarAsync(connection, "SELECT processor_connection_id FROM processor_handoff_outcomes WHERE handoff_id = 'h1'"));
    }

    private static async Task<string?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : (string)value;
    }
}
