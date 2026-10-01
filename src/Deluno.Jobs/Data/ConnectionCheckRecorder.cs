using System.Globalization;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;

namespace Deluno.Jobs.Data;

/// <inheritdoc cref="IConnectionCheckRecorder" />
public sealed class ConnectionCheckRecorder(
    IDelunoDatabaseConnectionFactory databases,
    IActivityFeedRepository activityFeed,
    TimeProvider timeProvider,
    IRealtimeEventPublisher? realtimeEventPublisher = null) : IConnectionCheckRecorder
{
    public async Task<ConnectionCheckedEvent> RecordAsync(
        string kind,
        string id,
        string name,
        bool ok,
        int? latencyMs,
        string message,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var slow = ConnectionCheckRules.IsSlow(ok, latencyMs);
        var text = string.IsNullOrWhiteSpace(message) ? (ok ? "Answered." : "Did not answer.") : message.Trim();
        var result = new ConnectionCheckedEvent(kind, id, name, ok, slow, text, now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));

        bool? previousOk = null;
        var previousSlow = false;
        try
        {
            await using var connection = await databases.OpenConnectionAsync(DelunoDatabaseNames.Jobs, cancellationToken);
            using (var read = connection.CreateCommand())
            {
                read.CommandText = "SELECT ok, slow FROM connection_checks WHERE kind = @kind AND connection_id = @id;";
                Add(read, "@kind", kind);
                Add(read, "@id", id);
                using var reader = await read.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    previousOk = reader.GetInt32(0) != 0;
                    previousSlow = reader.GetInt32(1) != 0;
                }
            }

            using var write = connection.CreateCommand();
            write.CommandText =
                """
                INSERT INTO connection_checks (kind, connection_id, name, ok, slow, message, checked_utc)
                VALUES (@kind, @id, @name, @ok, @slow, @message, @checked)
                ON CONFLICT (kind, connection_id) DO UPDATE SET
                    name = excluded.name, ok = excluded.ok, slow = excluded.slow,
                    message = excluded.message, checked_utc = excluded.checked_utc;
                """;
            Add(write, "@kind", kind);
            Add(write, "@id", id);
            Add(write, "@name", name);
            Add(write, "@ok", ok ? 1 : 0);
            Add(write, "@slow", slow ? 1 : 0);
            Add(write, "@message", text);
            Add(write, "@checked", result.CheckedUtc);
            await write.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // The check itself happened; losing the memory of it must not fail the test button.
        }

        // A plain Activity line only when the result changes: never one for every quiet check.
        var changed = previousOk is null ? !ok : previousOk != ok || previousSlow != slow;
        if (changed)
        {
            try
            {
                await activityFeed.RecordActivityAsync(
                    "connection.checked",
                    SentenceFor(name, ok, slow, text, previousOk),
                    System.Text.Json.JsonSerializer.Serialize(new { kind, id, name, ok, slow }),
                    null,
                    "connection",
                    id,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // The Activity line is a courtesy; the check stands without it.
            }
        }

        await realtimeEventPublisher.PublishSafelyAsync(publisher => publisher.PublishConnectionCheckedAsync(result, cancellationToken));
        return result;
    }

    public async Task<IReadOnlyList<ConnectionCheckItem>> ListAsync(CancellationToken cancellationToken)
    {
        var items = new List<ConnectionCheckItem>();
        await using var connection = await databases.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Jobs, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind, connection_id, name, ok, slow, message, checked_utc FROM connection_checks ORDER BY name COLLATE NOCASE;";
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new ConnectionCheckItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3) != 0,
                reader.GetInt32(4) != 0,
                reader.GetString(5),
                DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }

        return items;
    }

    /// <summary>One plain sentence for the Activity feed.</summary>
    public static string SentenceFor(string name, bool ok, bool slow, string message, bool? previousOk)
    {
        if (!ok)
        {
            return $"{name} is not answering: {message}";
        }

        if (slow)
        {
            return $"{name} is answering slowly.";
        }

        return previousOk == false ? $"{name} is answering again." : $"{name} is answering normally again.";
    }

    private static void Add(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
