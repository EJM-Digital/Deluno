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
/// What a held release is now: the item with, when it no longer qualifies, the plain reason why (#1159).
/// </summary>
public sealed record HeldReleaseCheck(HeldReleaseItem Item, string? Refusal)
{
    public bool IsOffered => Refusal is null;
}

/// <summary>
/// The held results the search history keeps: a title's (or an episode's) newest search ended "held", the title or
/// episode still wants something, the held candidate carries a download link, and the current title rules still
/// accept that release for that title or episode. A record that stops qualifying drops off by itself, with no
/// dismiss needed. This judges with the same title rules a search uses and sends nothing.
/// </summary>
public interface IHeldReleaseReader
{
    Task<IReadOnlyList<HeldReleaseItem>> ListAsync(int take, CancellationToken cancellationToken);

    /// <summary>One held record judged again now. Null when no such record exists.</summary>
    Task<HeldReleaseCheck?> CheckAsync(string historyId, CancellationToken cancellationToken);
}

public sealed class SqliteHeldReleaseReader(IDelunoDatabaseConnectionFactory databases) : IHeldReleaseReader
{
    private const int ScanLimit = 2000;
    private const string NothingWantedWords = "That title or episode no longer needs a release: the file you wanted is already there.";
    private const string NewerSearchWords = "A newer search has run since, so this held release is out of date.";

    public async Task<IReadOnlyList<HeldReleaseItem>> ListAsync(int take, CancellationToken cancellationToken)
    {
        take = Math.Clamp(take, 1, 500);
        var items = new List<HeldReleaseItem>();
        items.AddRange((await ReadMoviesAsync(null, cancellationToken)).Where(check => check.IsOffered).Select(check => check.Item));
        items.AddRange((await ReadSeriesAsync(null, cancellationToken)).Where(check => check.IsOffered).Select(check => check.Item));
        return items.OrderByDescending(item => item.HeldUtc).Take(take).ToArray();
    }

    public async Task<HeldReleaseCheck?> CheckAsync(string historyId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(historyId))
        {
            return null;
        }

        var movie = (await ReadMoviesAsync(historyId, cancellationToken)).FirstOrDefault();
        return movie ?? (await ReadSeriesAsync(historyId, cancellationToken)).FirstOrDefault();
    }

    private async Task<IReadOnlyList<HeldReleaseCheck>> ReadMoviesAsync(string? historyId, CancellationToken cancellationToken)
    {
        await using var connection = await databases.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Movies, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT h.id, m.id, m.title, m.release_year, h.library_id, h.details_json, h.created_utc, w.last_search_result,
                   w.wanted_status, m.metadata_json,
                   CASE WHEN h.id = (
                       SELECT h2.id FROM movie_search_history h2
                       WHERE h2.movie_id = h.movie_id AND h2.library_id = h.library_id
                       ORDER BY h2.created_utc DESC LIMIT 1) THEN 1 ELSE 0 END
            FROM movie_search_history h
            INNER JOIN movie_entries m ON m.id = h.movie_id
            LEFT JOIN movie_wanted_state w ON w.movie_id = h.movie_id AND w.library_id = h.library_id
            WHERE h.outcome = 'held' AND (@id IS NULL OR h.id = @id)
            ORDER BY h.created_utc DESC
            LIMIT @take;
            """;
        Add(command, "@take", ScanLimit);
        Add(command, "@id", (object?)historyId ?? DBNull.Value);
        var items = new List<HeldReleaseCheck>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (Candidate.From(reader.IsDBNull(5) ? null : reader.GetString(5)) is not { } candidate)
            {
                continue;
            }

            var item = new HeldReleaseItem(
                reader.GetString(0), "movie", reader.GetString(1), null, reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3), null, reader.GetString(4),
                candidate.ReleaseName, candidate.IndexerName, candidate.IndexerId, candidate.DownloadUrl, candidate.Quality,
                candidate.SizeBytes, candidate.Seeders, candidate.Summary ?? (reader.IsDBNull(7) ? null : reader.GetString(7)),
                Parse(reader.GetString(6)));

            string? refusal;
            if (!IsWanted(reader.IsDBNull(8) ? null : reader.GetString(8)))
            {
                refusal = NothingWantedWords;
            }
            else if (reader.GetInt32(10) == 0)
            {
                refusal = NewerSearchWords;
            }
            else
            {
                refusal = ReleaseTitleMatch.Mismatch(item.ReleaseName, new ReleaseTitleExpectation(
                    item.Title, item.Year, IsTv: false,
                    OtherTitles: CatalogueMetadata.ReadAlternateTitles(reader.IsDBNull(9) ? null : reader.GetString(9))));
            }

            items.Add(new HeldReleaseCheck(item, refusal));
        }

        return items;
    }

    private async Task<IReadOnlyList<HeldReleaseCheck>> ReadSeriesAsync(string? historyId, CancellationToken cancellationToken)
    {
        await using var connection = await databases.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Series, cancellationToken);
        var rows = new List<SeriesRow>();
        using (var command = connection.CreateCommand())
        {
            // Newest search per episode (or per show for a season-wide search).
            command.CommandText = """
                SELECT h.id, s.id, s.title, s.start_year, h.library_id, h.details_json, h.created_utc, ews.last_search_result,
                       h.episode_id, e.season_number, e.episode_number, ews.wanted_status, s.metadata_json,
                       CASE WHEN h.id = (
                           SELECT h2.id FROM series_search_history h2
                           WHERE h2.series_id = h.series_id AND h2.library_id = h.library_id
                             AND COALESCE(h2.episode_id, '') = COALESCE(h.episode_id, '')
                           ORDER BY h2.created_utc DESC LIMIT 1) THEN 1 ELSE 0 END
                FROM series_search_history h
                INNER JOIN series_entries s ON s.id = h.series_id
                LEFT JOIN episode_entries e ON e.id = h.episode_id
                LEFT JOIN episode_wanted_state ews ON ews.episode_id = h.episode_id
                WHERE h.outcome = 'held' AND (@id IS NULL OR h.id = @id)
                ORDER BY h.created_utc DESC
                LIMIT @take;
                """;
            Add(command, "@take", ScanLimit);
            Add(command, "@id", (object?)historyId ?? DBNull.Value);
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
                var item = new HeldReleaseItem(
                    reader.GetString(0), "series", reader.GetString(1), reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetInt32(3), label, reader.GetString(4),
                    candidate.ReleaseName, candidate.IndexerName, candidate.IndexerId, candidate.DownloadUrl, candidate.Quality,
                    candidate.SizeBytes, candidate.Seeders, candidate.Summary ?? (reader.IsDBNull(7) ? null : reader.GetString(7)),
                    Parse(reader.GetString(6)));
                rows.Add(new SeriesRow(
                    item,
                    reader.IsDBNull(9) ? null : reader.GetInt32(9),
                    reader.IsDBNull(10) ? null : reader.GetInt32(10),
                    reader.IsDBNull(11) ? null : reader.GetString(11),
                    reader.IsDBNull(12) ? null : reader.GetString(12),
                    reader.GetInt32(13) == 1));
            }
        }

        var items = new List<HeldReleaseCheck>();
        var episodesBySeries = new Dictionary<string, IReadOnlyList<EpisodeState>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var item = row.Item;
            if (!episodesBySeries.TryGetValue(item.Id, out var episodes))
            {
                episodes = await ReadEpisodesAsync(connection, item.Id, cancellationToken);
                episodesBySeries[item.Id] = episodes;
            }

            var episodeBound = item.EpisodeId is not null;
            string? refusal = null;
            if (episodeBound ? !IsWanted(row.WantedStatus) : !episodes.Any(episode => IsWanted(episode.WantedStatus)))
            {
                refusal = NothingWantedWords;
            }
            else if (!row.IsLatest)
            {
                refusal = NewerSearchWords;
            }
            else
            {
                // A season-wide record whose release names one episode that already has its file is not offered either.
                if (!episodeBound && NamedEpisode(item.ReleaseName) is { } named
                    && episodes.FirstOrDefault(episode => episode.Season == named.Season && episode.Episode == named.Episode) is { } target
                    && !IsWanted(target.WantedStatus))
                {
                    refusal = NothingWantedWords;
                }

                refusal ??= ReleaseTitleMatch.Mismatch(item.ReleaseName, new ReleaseTitleExpectation(
                    item.Title, item.Year, IsTv: true, row.Season, row.Episode,
                    OtherTitles: CatalogueMetadata.ReadAlternateTitles(row.MetadataJson),
                    EpisodeTitles: row.Season is { } season ? TitlesFor(episodes, season) : null,
                    SeasonEpisodeTitles: row.Season is null
                        ? episodes.Where(episode => episode.Season > 0).Select(episode => episode.Season).Distinct()
                            .ToDictionary(number => number, number => TitlesFor(episodes, number))
                        : null));
            }

            items.Add(new HeldReleaseCheck(item, refusal));
        }

        return items;
    }

    private static bool IsWanted(string? status)
        => string.Equals(status, "missing", StringComparison.OrdinalIgnoreCase) || string.Equals(status, "upgrade", StringComparison.OrdinalIgnoreCase);

    /// <summary>The one episode a release name says it is, or null for a pack or a name with no or several markers.</summary>
    private static (int Season, int Episode)? NamedEpisode(string releaseName)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(
            releaseName, @"(?<![A-Za-z0-9])S(\d{1,3})[ ._-]?E(\d{1,4})(?!\d)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return matches.Count == 1
            ? (int.Parse(matches[0].Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(matches[0].Groups[2].Value, CultureInfo.InvariantCulture))
            : null;
    }

    private static IReadOnlyDictionary<int, string> TitlesFor(IReadOnlyList<EpisodeState> episodes, int season)
        => episodes
            .Where(episode => episode.Season == season && !string.IsNullOrWhiteSpace(episode.Title))
            .GroupBy(episode => episode.Episode)
            .ToDictionary(group => group.Key, group => group.First().Title!.Trim());

    private static async Task<IReadOnlyList<EpisodeState>> ReadEpisodesAsync(System.Data.Common.DbConnection connection, string seriesId, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.season_number, e.episode_number, e.title, w.wanted_status
            FROM episode_entries e
            LEFT JOIN episode_wanted_state w ON w.episode_id = e.id
            WHERE e.series_id = @series;
            """;
        Add(command, "@series", seriesId);
        var episodes = new List<EpisodeState>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            episodes.Add(new EpisodeState(reader.GetInt32(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return episodes;
    }

    private sealed record EpisodeState(int Season, int Episode, string? Title, string? WantedStatus);

    private sealed record SeriesRow(HeldReleaseItem Item, int? Season, int? Episode, string? WantedStatus, string? MetadataJson, bool IsLatest);

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
