using System.Globalization;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;

namespace Deluno.Jobs.Data;

/// <summary>What one connection has done since the person's midnight, and (for a processor) how many titles it has right now.</summary>
public sealed record ConnectionDay(int Count, int Busy);

/// <summary>
/// The day's counts for every connection, read from stored rows: processor hand-offs, Seerr requests received,
/// media server refreshes and subtitle fetches (<c>connection_uses</c>), and the lists each provider family has.
/// One grouped, indexed read per source. The Live screen and the pushed <c>ConnectionUsed</c> event both read
/// it here, so a number and its update can never disagree.
/// </summary>
public sealed class ConnectionDayStore(IDelunoDatabaseConnectionFactory databases)
{
    private static readonly string[] OpenHandoffStatuses = ["waiting", "submitted", "accepted", "started"];

    public static string Key(string kind, string id) => $"{kind}:{id}";

    public async Task<IReadOnlyDictionary<string, ConnectionDay>> ReadAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var busy = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var since = sinceUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

        await using (var jobs = await databases.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Jobs, cancellationToken))
        {
            using var command = jobs.CreateCommand();
            command.CommandText = "SELECT kind, connection_id, COUNT(*) FROM connection_uses WHERE used_utc >= @since GROUP BY kind, connection_id;";
            Add(command, "@since", since);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                counts[Key(reader.GetString(0), reader.GetString(1))] = reader.GetInt32(2);
            }
        }

        await using var platform = await databases.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);

        using (var command = platform.CreateCommand())
        {
            command.CommandText =
                "SELECT processor_connection_id, COUNT(*) FROM processor_handoffs WHERE created_utc >= @since AND processor_connection_id IS NOT NULL GROUP BY processor_connection_id;";
            Add(command, "@since", since);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                counts[Key(ConnectionKinds.Processor, reader.GetString(0))] = reader.GetInt32(1);
            }
        }

        using (var command = platform.CreateCommand())
        {
            var placeholders = string.Join(", ", OpenHandoffStatuses.Select((_, index) => $"@s{index}"));
            command.CommandText =
                $"SELECT processor_connection_id, COUNT(*) FROM processor_handoffs WHERE status IN ({placeholders}) AND processor_connection_id IS NOT NULL GROUP BY processor_connection_id;";
            for (var index = 0; index < OpenHandoffStatuses.Length; index++)
            {
                Add(command, $"@s{index}", OpenHandoffStatuses[index]);
            }

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                busy[Key(ConnectionKinds.Processor, reader.GetString(0))] = reader.GetInt32(1);
            }
        }

        using (var command = platform.CreateCommand())
        {
            command.CommandText = "SELECT connection_id, COUNT(*) FROM seerr_acquisitions WHERE created_utc >= @since GROUP BY connection_id;";
            Add(command, "@since", since);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                counts[Key(ConnectionKinds.Requests, reader.GetString(0))] = reader.GetInt32(1);
            }
        }

        // Lists are not a "today" figure: how many each provider family is watching now.
        using (var command = platform.CreateCommand())
        {
            command.CommandText = "SELECT provider, COUNT(*) FROM intake_sources WHERE is_enabled = 1 GROUP BY provider;";
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var key = Key(ConnectionKinds.Lists, ListProviderFamilies.KeyFor(reader.GetString(0)));
                counts[key] = counts.GetValueOrDefault(key) + reader.GetInt32(1);
            }
        }

        var all = new Dictionary<string, ConnectionDay>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in counts.Keys.Concat(busy.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            all[key] = new ConnectionDay(counts.GetValueOrDefault(key), busy.GetValueOrDefault(key));
        }

        return all;
    }

    private static void Add(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

/// <inheritdoc cref="IConnectionUseReporter" />
public sealed class ConnectionUseReporter(
    IDelunoDatabaseConnectionFactory databases,
    ConnectionDayStore dayStore,
    TimeProvider timeProvider,
    IRealtimeEventPublisher? realtimeEventPublisher = null) : IConnectionUseReporter
{
    /// <summary>How long a recorded use is kept; the screen only ever asks for today.</summary>
    private static readonly TimeSpan Retention = TimeSpan.FromDays(14);

    public async Task ReportAsync(string kind, string id, string name, CancellationToken cancellationToken)
    {
        try
        {
            var now = timeProvider.GetUtcNow();
            if (kind is ConnectionKinds.MediaServer or ConnectionKinds.Subtitles)
            {
                // These two left no stored trace of their own; the use is the record.
                await using var connection = await databases.OpenConnectionAsync(DelunoDatabaseNames.Jobs, cancellationToken);
                using (var insert = connection.CreateCommand())
                {
                    insert.CommandText = "INSERT INTO connection_uses (id, kind, connection_id, used_utc) VALUES (@id, @kind, @connection, @used);";
                    Add(insert, "@id", Guid.NewGuid().ToString("N"));
                    Add(insert, "@kind", kind);
                    Add(insert, "@connection", id);
                    Add(insert, "@used", now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                }

                using var prune = connection.CreateCommand();
                prune.CommandText = "DELETE FROM connection_uses WHERE used_utc < @cutoff;";
                Add(prune, "@cutoff", now.Subtract(Retention).UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
                await prune.ExecuteNonQueryAsync(cancellationToken);
            }

            if (realtimeEventPublisher is null)
            {
                return;
            }

            var local = TimeZoneInfo.ConvertTime(now, timeProvider.LocalTimeZone);
            var midnight = new DateTimeOffset(local.Date, local.Offset).ToUniversalTime();
            var days = await dayStore.ReadAsync(midnight, cancellationToken);
            var day = days.TryGetValue(ConnectionDayStore.Key(kind, id), out var found) ? found : new ConnectionDay(0, 0);
            await realtimeEventPublisher.PublishConnectionUsedAsync(
                new ConnectionUsedEvent(kind, id, name, day.Count, day.Busy, now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Never undoes or fails the thing it reports; the next use or a reload catches the screen up.
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
