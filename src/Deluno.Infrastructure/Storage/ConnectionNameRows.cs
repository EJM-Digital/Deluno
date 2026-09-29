namespace Deluno.Infrastructure.Storage;

/// <summary>#1076: the names every other row of a connection table already has, so a new name can be made unique.</summary>
public static class ConnectionNameRows
{
    public static async Task<List<string>> OtherNamesAsync(
        System.Data.Common.DbConnection connection,
        string table,
        string? exceptId,
        CancellationToken cancellationToken)
    {
        var names = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM {table} WHERE id <> @id;";
        SqliteRecordHelpers.AddParameter(command, "@id", exceptId ?? string.Empty);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
