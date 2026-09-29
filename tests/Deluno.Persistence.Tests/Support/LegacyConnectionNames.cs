using Deluno.Infrastructure.Storage;

namespace Deluno.Persistence.Tests.Support;

/// <summary>
/// #1076: a connection's name is worked out from its address now. A test about
/// something else that still looks a connection up by a name an older install
/// typed sets that name straight in the table, as such an install would have it.
/// </summary>
internal static class LegacyConnectionNames
{
    public static async Task SetAsync(IDelunoDatabaseConnectionFactory factory, string table, string id, string name)
    {
        await using var connection = await factory.OpenConnectionAsync(DelunoDatabaseNames.Platform, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE {table} SET name = @name WHERE id = @id;";
        foreach (var (key, value) in new[] { ("@name", name), ("@id", id) })
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = key;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync();
    }
}
