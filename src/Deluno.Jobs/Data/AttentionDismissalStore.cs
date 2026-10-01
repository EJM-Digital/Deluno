using System.Globalization;
using Deluno.Infrastructure.Storage;

namespace Deluno.Jobs.Data;

/// <summary>
/// What the person has dismissed from the bell (#1098). A dismissal hides one item; it changes nothing about
/// the title or the problem, and it is forgotten as soon as the item stops being a problem, so the same
/// problem coming back later is a new row.
/// </summary>
public sealed class AttentionDismissalStore(IDelunoDatabaseConnectionFactory databases, TimeProvider timeProvider)
{
    public static string NoReleaseKey(string mediaType, string id) => $"no-release:{mediaType}:{id}";

    public async Task DismissAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = await databases.OpenConnectionAsync(DelunoDatabaseNames.Jobs, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO attention_dismissals (dismissal_key, dismissed_utc) VALUES (@key, @utc) ON CONFLICT (dismissal_key) DO NOTHING;";
        Add(command, "@key", key);
        Add(command, "@utc", timeProvider.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlySet<string>> ListAsync(CancellationToken cancellationToken)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var connection = await databases.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Jobs, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT dismissal_key FROM attention_dismissals;";
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            keys.Add(reader.GetString(0));
        }

        return keys;
    }

    /// <summary>Forgets every dismissal whose key is not in <paramref name="stillProblems"/>.</summary>
    public async Task ForgetExceptAsync(IReadOnlySet<string> stillProblems, CancellationToken cancellationToken)
    {
        var existing = await ListAsync(cancellationToken);
        var gone = existing.Where(key => !stillProblems.Contains(key)).ToArray();
        if (gone.Length == 0)
        {
            return;
        }

        await using var connection = await databases.OpenConnectionAsync(DelunoDatabaseNames.Jobs, cancellationToken);
        foreach (var key in gone)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM attention_dismissals WHERE dismissal_key = @key;";
            Add(command, "@key", key);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static void Add(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
