using Deluno.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Deluno.Persistence.Tests.Support;

/// <summary>
/// Clearing only this storage's pools must still release its files, or test
/// folders pile up in %TEMP% again, and must not close another storage's
/// connection - which ClearAllPools() did, mid-query, to tests running in parallel.
/// </summary>
public sealed class TestStorageTests
{
    [Fact]
    public async Task Disposing_releases_its_own_files_so_the_folder_is_deleted()
    {
        string dataRoot;
        using (var storage = TestStorage.Create())
        {
            dataRoot = storage.DataRoot;
            await using (var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE IF NOT EXISTS probe (id INTEGER); INSERT INTO probe VALUES (1);";
                await command.ExecuteNonQueryAsync();
            }

            await using (var reader = await storage.Factory.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Platform))
            {
                await using var command = reader.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM probe;";
                Assert.Equal(1L, await command.ExecuteScalarAsync());
            }
        }

        Assert.False(Directory.Exists(dataRoot), "A released pool lets the folder be deleted.");
    }

    [Fact]
    public async Task Clearing_one_storage_does_not_close_another_storage_connection()
    {
        using var mine = TestStorage.Create();
        using var theirs = TestStorage.Create();

        await using var open = (SqliteConnection)await theirs.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform);
        await using (var command = open.CreateCommand())
        {
            command.CommandText = "CREATE TABLE IF NOT EXISTS probe (id INTEGER);";
            await command.ExecuteNonQueryAsync();
        }

        mine.Factory.ClearPools();

        // The other storage's open connection is untouched and still usable.
        await using var after = open.CreateCommand();
        after.CommandText = "INSERT INTO probe VALUES (2); SELECT COUNT(*) FROM probe;";
        Assert.Equal(1L, await after.ExecuteScalarAsync());
    }
}
