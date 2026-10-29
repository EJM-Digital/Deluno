using Deluno.Platform.Migrations;
using Microsoft.Data.Sqlite;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// #1432: the regular connection check always runs. An install that had turned it off loses the stored answer
/// when it updates, and no other setting is touched.
/// </summary>
public sealed class ConnectionChecksAlwaysOnMigrationTests
{
    [Fact]
    public async Task An_install_that_had_the_check_off_has_the_old_answer_removed_and_keeps_every_other_setting()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE system_settings (setting_key TEXT PRIMARY KEY, setting_value TEXT, updated_utc TEXT);" +
                "INSERT INTO system_settings VALUES ('connections.checkRegularly','false','x'),('machine.keepAwake','true','x');";
            await create.ExecuteNonQueryAsync();
        }

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await new V0140ConnectionChecksAlwaysOn().UpAsync(connection, transaction, CancellationToken.None);
            await transaction.CommitAsync();
        }

        await using var read = connection.CreateCommand();
        read.CommandText = "SELECT setting_key FROM system_settings";
        var keys = new List<string>();
        await using var reader = await read.ExecuteReaderAsync();
        while (await reader.ReadAsync()) keys.Add(reader.GetString(0));
        Assert.Equal(["machine.keepAwake"], keys);
    }
}
