using System.Globalization;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;

namespace Deluno.Jobs.Data;

/// <inheritdoc cref="IConnectionVersionStore" />
public sealed class ConnectionVersionStore(
    IDelunoDatabaseConnectionFactory databases,
    TimeProvider timeProvider) : IConnectionVersionStore
{
    public async Task RecordAsync(string kind, string id, string? project, string? version, CancellationToken cancellationToken)
    {
        var text = version?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 64)
        {
            return;
        }

        try
        {
            await using var connection = await databases.OpenConnectionAsync(DelunoDatabaseNames.Jobs, cancellationToken);
            using var write = connection.CreateCommand();
            write.CommandText =
                """
                INSERT INTO connection_versions (kind, connection_id, project, version, read_utc)
                VALUES (@kind, @id, @project, @version, @read)
                ON CONFLICT (kind, connection_id) DO UPDATE SET
                    project = COALESCE(excluded.project, connection_versions.project),
                    version = excluded.version,
                    read_utc = excluded.read_utc;
                """;
            Add(write, "@kind", kind);
            Add(write, "@id", id);
            Add(write, "@project", project);
            Add(write, "@version", text);
            Add(write, "@read", timeProvider.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            await write.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Remembering a version must never fail the check that read it.
        }
    }

    public async Task<IReadOnlyList<ConnectionVersionRow>> ListAsync(CancellationToken cancellationToken)
    {
        var items = new List<ConnectionVersionRow>();
        await using var connection = await databases.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Jobs, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind, connection_id, project, version, read_utc FROM connection_versions;";
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new ConnectionVersionRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }

        return items;
    }

    private static void Add(System.Data.Common.DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
