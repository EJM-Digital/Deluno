using System.Globalization;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;

namespace Deluno.Media;

/// <summary>A wanted title whose latest search found no release Deluno would take (#1098).</summary>
public sealed record NoAcceptableReleaseItem(
    string MediaType,
    string Id,
    string Title,
    int? Year,
    string LibraryId,
    DateTimeOffset LastSearchUtc,
    DateTimeOffset? MissingSinceUtc,
    string? Reason);

/// <summary>
/// The titles the bell lists under "no acceptable release": still wanted, still without a file, and whose
/// newest search ended with nothing Deluno would take (either nobody offered it, or everything offered was
/// turned down). It states what is stored and decides nothing; searching again or dismissing is the person's
/// call from the bell.
/// </summary>
public interface INoAcceptableReleaseReader
{
    Task<IReadOnlyList<NoAcceptableReleaseItem>> ListAsync(int take, CancellationToken cancellationToken);
}

public sealed class SqliteNoAcceptableReleaseReader(IDelunoDatabaseConnectionFactory databases) : INoAcceptableReleaseReader
{
    public async Task<IReadOnlyList<NoAcceptableReleaseItem>> ListAsync(int take, CancellationToken cancellationToken)
    {
        var items = new List<NoAcceptableReleaseItem>();
        foreach (var kind in new[] { MediaKind.Movie, MediaKind.Series })
        {
            items.AddRange(await ReadKindAsync(kind, Math.Clamp(take, 1, 500), cancellationToken));
        }

        return items.OrderByDescending(item => item.LastSearchUtc).Take(take).ToArray();
    }

    private async Task<IReadOnlyList<NoAcceptableReleaseItem>> ReadKindAsync(MediaKind kind, int take, CancellationToken cancellationToken)
    {
        var map = MediaTableMap.For(kind);
        await using var connection = await databases.OpenReadOnlyConnectionAsync(map.DatabaseName, cancellationToken);
        using var command = connection.CreateCommand();
        // The newest history row decides: a title searched since it last found something is not stuck on "nothing".
        command.CommandText = $"""
            SELECT {map.EntryAlias}.id, {map.EntryAlias}.title, {map.EntryAlias}.{map.YearColumn}, w.library_id,
                   w.last_search_utc, w.missing_since_utc, w.last_search_result
            FROM {map.WantedTable} w
            INNER JOIN {map.EntryTable} {map.EntryAlias} ON {map.EntryAlias}.id = w.{map.WantedMediaIdColumn}
            WHERE w.wanted_status = 'missing'
              AND w.last_search_utc IS NOT NULL
              AND {map.WantedHere()}
              AND (
                  SELECT h.outcome FROM {map.HistoryTable} h
                  WHERE h.{map.WantedMediaIdColumn} = {map.EntryAlias}.id
                  ORDER BY h.created_utc DESC LIMIT 1
              ) = 'checked'
            ORDER BY w.last_search_utc DESC
            LIMIT @take;
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@take";
        parameter.Value = take;
        command.Parameters.Add(parameter);

        var items = new List<NoAcceptableReleaseItem>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new NoAcceptableReleaseItem(
                kind == MediaKind.Movie ? "movie" : "series",
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return items;
    }
}
