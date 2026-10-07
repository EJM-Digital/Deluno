using System.Globalization;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;

namespace Deluno.Media;

/// <summary>
/// A release a search found and held for a person's decision (#1159): the best candidate was usable but not safe to
/// send on its own. Shown under Transfers' Needs action with the candidate and Approve / Find another / Dismiss.
/// </summary>
public sealed record HeldReleaseItem(
    string HistoryId,
    string MediaType,
    string Id,
    string? EpisodeId,
    string Title,
    int? Year,
    string? EpisodeLabel,
    string LibraryId,
    string ReleaseName,
    string? IndexerName,
    string? IndexerId,
    string? DownloadUrl,
    string? Quality,
    long? SizeBytes,
    int? Seeders,
    string? Reason,
    DateTimeOffset HeldUtc);

/// <summary>
/// The held results the search history keeps: a title's (or an episode's) newest search ended "held", the title is
/// still wanted, and the held candidate carries a download link. States what is stored and decides nothing.
/// </summary>
public interface IHeldReleaseReader
{
    Task<IReadOnlyList<HeldReleaseItem>> ListAsync(int take, CancellationToken cancellationToken);
}

public sealed class SqliteHeldReleaseReader(IDelunoDatabaseConnectionFactory databases) : IHeldReleaseReader
{
    public async Task<IReadOnlyList<HeldReleaseItem>> ListAsync(int take, CancellationToken cancellationToken)
    {
        take = Math.Clamp(take, 1, 500);
        var items = new List<HeldReleaseItem>();
        items.AddRange(await ReadMoviesAsync(take, cancellationToken));
        items.AddRange(await ReadSeriesAsync(take, cancellationToken));
        return items.OrderByDescending(item => item.HeldUtc).Take(take).ToArray();
    }

    private async Task<IReadOnlyList<HeldReleaseItem>> ReadMoviesAsync(int take, CancellationToken cancellationToken)
    {
        await using var connection = await databases.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Movies, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT h.id, m.id, m.title, m.release_year, h.library_id, h.details_json, h.created_utc, w.last_search_result
            FROM movie_search_history h
            INNER JOIN movie_entries m ON m.id = h.movie_id
            INNER JOIN movie_wanted_state w ON w.movie_id = h.movie_id AND w.library_id = h.library_id
            WHERE h.outcome = 'held'
              AND w.wanted_status IN ('missing', 'upgrade')
              AND h.id = (
                  SELECT h2.id FROM movie_search_history h2
                  WHERE h2.movie_id = h.movie_id AND h2.library_id = h.library_id
                  ORDER BY h2.created_utc DESC LIMIT 1)
            ORDER BY h.created_utc DESC
            LIMIT @take;
            """;
        Add(command, "@take", take);
        var items = new List<HeldReleaseItem>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (Candidate.From(reader.IsDBNull(5) ? null : reader.GetString(5)) is not { } candidate)
            {
                continue;
            }

            items.Add(new HeldReleaseItem(
                reader.GetString(0), "movie", reader.GetString(1), null, reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3), null, reader.GetString(4),
                candidate.ReleaseName, candidate.IndexerName, candidate.IndexerId, candidate.DownloadUrl, candidate.Quality,
                candidate.SizeBytes, candidate.Seeders, candidate.Summary ?? (reader.IsDBNull(7) ? null : reader.GetString(7)),
                Parse(reader.GetString(6))));
        }

        return items;
    }

    private async Task<IReadOnlyList<HeldReleaseItem>> ReadSeriesAsync(int take, CancellationToken cancellationToken)
    {
        await using var connection = await databases.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Series, cancellationToken);
        using var command = connection.CreateCommand();
        // Newest search per episode (or per show for a season-wide search). An episode that now has a file is not wanted any more.
        command.CommandText = """
            SELECT h.id, s.id, s.title, s.start_year, h.library_id, h.details_json, h.created_utc, ews.last_search_result,
                   h.episode_id, e.season_number, e.episode_number
            FROM series_search_history h
            INNER JOIN series_entries s ON s.id = h.series_id
            LEFT JOIN episode_entries e ON e.id = h.episode_id
            LEFT JOIN episode_wanted_state ews ON ews.episode_id = h.episode_id
            WHERE h.outcome = 'held'
              AND (h.episode_id IS NULL OR ews.wanted_status IN ('missing', 'upgrade'))
              AND h.id = (
                  SELECT h2.id FROM series_search_history h2
                  WHERE h2.series_id = h.series_id AND h2.library_id = h.library_id
                    AND COALESCE(h2.episode_id, '') = COALESCE(h.episode_id, '')
                  ORDER BY h2.created_utc DESC LIMIT 1)
            ORDER BY h.created_utc DESC
            LIMIT @take;
            """;
        Add(command, "@take", take);
        var items = new List<HeldReleaseItem>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (Candidate.From(reader.IsDBNull(5) ? null : reader.GetString(5)) is not { } candidate)
            {
                continue;
            }

            var label = reader.IsDBNull(9) || reader.IsDBNull(10)
                ? null
                : $"S{reader.GetInt32(9):D2}E{reader.GetInt32(10):D2}";
            items.Add(new HeldReleaseItem(
                reader.GetString(0), "series", reader.GetString(1), reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3), label, reader.GetString(4),
                candidate.ReleaseName, candidate.IndexerName, candidate.IndexerId, candidate.DownloadUrl, candidate.Quality,
                candidate.SizeBytes, candidate.Seeders, candidate.Summary ?? (reader.IsDBNull(7) ? null : reader.GetString(7)),
                Parse(reader.GetString(6))));
        }

        return items;
    }

    private static DateTimeOffset Parse(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static void Add(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <summary>The held best candidate out of a stored search plan, or null when it is not usable (no name, no link, or refused).</summary>
    internal sealed record Candidate(string ReleaseName, string? IndexerName, string? IndexerId, string? DownloadUrl, string? Quality, long? SizeBytes, int? Seeders, string? Summary)
    {
        public static Candidate? From(string? detailsJson)
        {
            if (string.IsNullOrWhiteSpace(detailsJson))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(detailsJson);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                // A grabbed plan nests the plan under searchPlan; a held one is the plan itself.
                if (Find(root, "searchPlan") is { ValueKind: JsonValueKind.Object } nested)
                {
                    root = nested;
                }

                if (Find(root, "bestCandidate") is not { ValueKind: JsonValueKind.Object } best)
                {
                    return null;
                }

                var name = Text(best, "releaseName");
                var url = Text(best, "downloadUrl");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)
                    || string.Equals(Text(best, "decisionStatus"), "rejected", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return new Candidate(name, Text(best, "indexerName"), Text(best, "indexerId"), url, Text(best, "quality"), Long(best, "sizeBytes"), (int?)Long(best, "seeders"), Text(best, "summary"));
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static JsonElement? Find(JsonElement element, string name)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value;
                }
            }

            return null;
        }

        private static string? Text(JsonElement element, string name)
            => Find(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

        private static long? Long(JsonElement element, string name)
            => Find(element, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var number) ? number : null;
    }
}
