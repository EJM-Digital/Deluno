using Deluno.Platform.Migrations;
using Microsoft.Data.Sqlite;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// #529: Weir is now Weir, and the login Deluno generated for it moves from
/// the tool id <c>weir</c> to <c>weir</c> so an existing install keeps it.
/// </summary>
public sealed class CompanionToolWeirMigrationTests
{
    [Fact]
    public async Task The_saved_Weir_login_becomes_the_Weir_login()
    {
        await using var connection = await OpenAsync();
        await InsertAsync(connection, "weir", "http://127.0.0.1:8788", "protected-admin-secret");
        await InsertAsync(connection, "sabnzbd", "http://127.0.0.1:8085", "protected-api-key");

        await MigrateAsync(connection);

        Assert.Equal(["sabnzbd:http://127.0.0.1:8085:protected-api-key", "weir:http://127.0.0.1:8788:protected-admin-secret"], await RowsAsync(connection));
    }

    /// <summary>A Weir login written after the rename is the newer one and wins.</summary>
    [Fact]
    public async Task A_Weir_login_that_already_exists_is_kept_and_the_old_one_goes()
    {
        await using var connection = await OpenAsync();
        await InsertAsync(connection, "weir", "http://127.0.0.1:8788", "old-secret");
        await InsertAsync(connection, "weir", "http://127.0.0.1:8789", "new-secret");

        await MigrateAsync(connection);

        Assert.Equal(["weir:http://127.0.0.1:8789:new-secret"], await RowsAsync(connection));
    }

    [Fact]
    public async Task Nothing_changes_where_Weir_was_never_set_up()
    {
        await using var connection = await OpenAsync();
        await InsertAsync(connection, "qbittorrent", "http://127.0.0.1:8080", "protected-password");

        await MigrateAsync(connection);

        Assert.Equal(["qbittorrent:http://127.0.0.1:8080:protected-password"], await RowsAsync(connection));
    }

    private static async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await new V0060CompanionToolLogins().UpAsync(connection, transaction, CancellationToken.None);
        await transaction.CommitAsync();
        return connection;
    }

    private static async Task MigrateAsync(SqliteConnection connection)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await new V0065CompanionToolWeir().UpAsync(connection, transaction, CancellationToken.None);
        await transaction.CommitAsync();
    }

    private static async Task InsertAsync(SqliteConnection connection, string toolId, string url, string secret)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO companion_tool_logins (tool_id, url, username, secret, secret_label, updated_utc) VALUES ($id, $url, 'deluno-admin', $secret, 'Password', '2026-09-18T00:00:00Z');";
        command.Parameters.AddWithValue("$id", toolId);
        command.Parameters.AddWithValue("$url", url);
        command.Parameters.AddWithValue("$secret", secret);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<string>> RowsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT tool_id, url, secret FROM companion_tool_logins ORDER BY tool_id;";
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add($"{reader.GetString(0)}:{reader.GetString(1)}:{reader.GetString(2)}");
        }

        return rows;
    }
}
