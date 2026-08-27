using System.Data.Common;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Libraries.Data;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Media;

/// <summary>
/// The subtitle store, written once and used by both catalogues.
///
/// ADR-001 records that Movies and Series are parallel copies of one engine,
/// that fourteen repository methods already exist twice with the same shape,
/// and that the duplication is actively reproducing. Subtitles arrive after
/// that was written, so they start on the other side of it: every statement
/// here is one SQL body reading table names out of
/// <see cref="MediaTableMap"/>, and the identifiers are allow-listed there so
/// nothing interpolates caller input.
/// </summary>
public sealed class SqliteMediaSubtitleRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory)
    : IMediaSubtitleRepository
{
    /// <summary>
    /// Files whose subtitles have never been read, or were read when the file
    /// was a different file.
    ///
    /// Bounded and indexed rather than streamed and filtered: a library scan
    /// that reads twenty thousand rows to find the eleven it has not done yet
    /// is a scan somebody notices. Size is compared as well as path because a
    /// replaced upgrade keeps the name and changes everything else about the
    /// file, subtitle tracks included.
    /// </summary>
    public async Task<IReadOnlyList<MediaSubtitleScanCandidate>> ListPendingScansAsync(
        MediaKind kind,
        string libraryId,
        int limit,
        CancellationToken cancellationToken)
    {
        var map = MediaTableMap.For(kind);
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            map.DatabaseName,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {map.SubtitleFileIdColumn}, f.file_path, f.file_size_bytes
            FROM {map.SubtitleFileSource}
            {map.SubtitleFileLibraryJoin}
            LEFT JOIN {map.SubtitleScanTable} scan
                ON scan.{map.SubtitleMediaIdColumn} = {map.SubtitleFileIdColumn}
            WHERE {map.SubtitleFileLibraryFilter}
              AND f.has_file = 1
              AND f.file_path IS NOT NULL
              AND (
                    scan.{map.SubtitleMediaIdColumn} IS NULL
                 OR scan.file_path <> f.file_path
                 OR COALESCE(scan.file_size_bytes, -1) <> COALESCE(f.file_size_bytes, -1)
              )
            LIMIT @limit;
            """;
        AddParameter(command, "@libraryId", libraryId);
        AddParameter(command, "@limit", Math.Max(1, limit));

        var candidates = new List<MediaSubtitleScanCandidate>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add(new MediaSubtitleScanCandidate(
                MediaId: reader.GetString(0),
                FilePath: reader.GetString(1),
                FileSizeBytes: reader.IsDBNull(2) ? null : reader.GetInt64(2)));
        }

        return candidates;
    }

    /// <summary>
    /// What this file has, now — as one transaction, because a half-written
    /// inventory is a title that reports subtitles it does not have.
    ///
    /// Rows are upserted and then anything not seen is deleted, rather than
    /// deleted and re-inserted. That preserves <c>provider</c> and
    /// <c>created_utc</c> on a subtitle Deluno fetched itself: rescanning the
    /// folder finds it as an ordinary file beside the video, and replacing the
    /// row would turn Deluno's own work into something it knows nothing about
    /// — which is exactly what a blacklist and an upgrade will need later.
    /// </summary>
    public async Task RecordScanAsync(
        MediaKind kind,
        string mediaId,
        MediaSubtitleScan scan,
        IReadOnlyList<MediaSubtitleRow> subtitles,
        CancellationToken cancellationToken)
    {
        var map = MediaTableMap.For(kind);
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            map.DatabaseName,
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var now = scan.ScannedUtc.ToString("O");
        var kept = new List<string>();

        foreach (var subtitle in subtitles)
        {
            var language = SubtitleLanguages.Normalize(subtitle.Language) ?? SubtitleLanguages.Unknown;
            var forced = subtitle.Forced ? 1 : 0;
            var hearingImpaired = subtitle.HearingImpaired ? 1 : 0;

            using var upsert = connection.CreateCommand();
            upsert.Transaction = transaction;
            upsert.CommandText = $"""
                INSERT INTO {map.SubtitleTable} (
                    {map.SubtitleMediaIdColumn}, language, forced, hearing_impaired,
                    source, file_path, stream_index, codec, provider, created_utc, updated_utc
                )
                VALUES (
                    @mediaId, @language, @forced, @hearingImpaired,
                    @source, @filePath, @streamIndex, @codec, @provider, @now, @now
                )
                ON CONFLICT({map.SubtitleMediaIdColumn}, language, forced, hearing_impaired) DO UPDATE SET
                    source = excluded.source,
                    file_path = excluded.file_path,
                    stream_index = excluded.stream_index,
                    codec = excluded.codec,
                    provider = COALESCE(excluded.provider, {map.SubtitleTable}.provider),
                    updated_utc = excluded.updated_utc;
                """;
            AddParameter(upsert, "@mediaId", mediaId);
            AddParameter(upsert, "@language", language);
            AddParameter(upsert, "@forced", forced);
            AddParameter(upsert, "@hearingImpaired", hearingImpaired);
            AddParameter(upsert, "@source", SubtitleSources.IsKnown(subtitle.Source) ? subtitle.Source : SubtitleSources.External);
            AddParameter(upsert, "@filePath", subtitle.FilePath);
            AddParameter(upsert, "@streamIndex", subtitle.StreamIndex);
            AddParameter(upsert, "@codec", subtitle.Codec);
            AddParameter(upsert, "@provider", subtitle.Provider);
            AddParameter(upsert, "@now", now);
            await upsert.ExecuteNonQueryAsync(cancellationToken);

            kept.Add($"{language}|{forced}|{hearingImpaired}");
        }

        using (var prune = connection.CreateCommand())
        {
            prune.Transaction = transaction;
            var keptParameters = string.Join(", ", kept.Select((_, index) => $"@kept{index}"));
            var keptFilter = kept.Count == 0
                ? string.Empty
                : $" AND (language || '|' || forced || '|' || hearing_impaired) NOT IN ({keptParameters})";
            prune.CommandText = $"""
                DELETE FROM {map.SubtitleTable}
                WHERE {map.SubtitleMediaIdColumn} = @mediaId{keptFilter};
                """;
            AddParameter(prune, "@mediaId", mediaId);
            for (var index = 0; index < kept.Count; index++)
            {
                AddParameter(prune, $"@kept{index}", kept[index]);
            }

            await prune.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var marker = connection.CreateCommand())
        {
            marker.Transaction = transaction;
            marker.CommandText = $"""
                INSERT INTO {map.SubtitleScanTable} (
                    {map.SubtitleMediaIdColumn}, file_path, file_size_bytes, probe_status, subtitle_count, scanned_utc
                )
                VALUES (@mediaId, @filePath, @fileSizeBytes, @probeStatus, @subtitleCount, @now)
                ON CONFLICT({map.SubtitleMediaIdColumn}) DO UPDATE SET
                    file_path = excluded.file_path,
                    file_size_bytes = excluded.file_size_bytes,
                    probe_status = excluded.probe_status,
                    subtitle_count = excluded.subtitle_count,
                    scanned_utc = excluded.scanned_utc;
                """;
            AddParameter(marker, "@mediaId", mediaId);
            AddParameter(marker, "@filePath", scan.FilePath);
            AddParameter(marker, "@fileSizeBytes", scan.FileSizeBytes);
            AddParameter(marker, "@probeStatus", scan.ProbeStatus);
            AddParameter(marker, "@subtitleCount", scan.SubtitleCount);
            AddParameter(marker, "@now", now);
            await marker.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MediaSubtitleRow>> ListSubtitlesAsync(
        MediaKind kind,
        string mediaId,
        CancellationToken cancellationToken)
    {
        var map = MediaTableMap.For(kind);
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            map.DatabaseName,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT language, source, forced, hearing_impaired, file_path, stream_index, codec, provider
            FROM {map.SubtitleTable}
            WHERE {map.SubtitleMediaIdColumn} = @mediaId
            ORDER BY language, forced, hearing_impaired;
            """;
        AddParameter(command, "@mediaId", mediaId);

        var rows = new List<MediaSubtitleRow>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new MediaSubtitleRow(
                Language: reader.GetString(0),
                Source: reader.GetString(1),
                Forced: reader.GetInt64(2) == 1,
                HearingImpaired: reader.GetInt64(3) == 1,
                FilePath: reader.IsDBNull(4) ? null : reader.GetString(4),
                StreamIndex: reader.IsDBNull(5) ? null : reader.GetInt32(5),
                Codec: reader.IsDBNull(6) ? null : reader.GetString(6),
                Provider: reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return rows;
    }
}

/// <summary>
/// How much of what a library asked for each title on a catalogue page holds.
///
/// Run on the page's own connection, after the seek, over the page's own ids —
/// so it costs what the episode progress rollup beside it costs and no more.
///
/// <para>Two things keep it cheap. The wanted languages are bound into the
/// query rather than intersected afterwards, so a show with twelve embedded
/// tracks returns one row and not twelve. And a library that has asked for no
/// languages is never passed here at all: until somebody turns subtitles on,
/// the catalogue page runs exactly the queries it ran before this existed.</para>
///
/// <para>It is grouped by library rather than by page, because the wanted list
/// is a property of the library and a page can hold two of them — a movie
/// shelf that wants English and an anime shelf that wants English and Japanese
/// cannot share one answer. In the ordinary case, and always on a
/// library-filtered page, that is one query.</para>
/// </summary>
public static class CatalogueSubtitleRollup
{
    /// <summary>
    /// The two numbers a page of titles needs, for every title on it.
    ///
    /// This is the whole rule in one place, so a movie shelf and a TV shelf
    /// cannot answer the same question differently — which is what DESIGN-001
    /// spent a run undoing four times over.
    ///
    /// <c>Wanted</c> is per file, because that is what the bar multiplies by:
    /// one for a movie, and the episodes a show actually holds. <c>Held</c>
    /// reads whichever of the two counts the library's mode asked for.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, CatalogueSubtitleCounts>> ForPageAsync(
        DbConnection connection,
        MediaKind kind,
        IReadOnlyList<(string Id, string? LibraryId)> items,
        IReadOnlyDictionary<string, LibrarySubtitlePreference> preferences,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, CatalogueSubtitleCounts>(StringComparer.Ordinal);
        if (items.Count == 0 || preferences.Count == 0)
        {
            return counts;
        }

        var byLibrary = items
            .Where(item => !string.IsNullOrWhiteSpace(item.LibraryId))
            .GroupBy(item => item.LibraryId!, StringComparer.OrdinalIgnoreCase);

        foreach (var group in byLibrary)
        {
            if (!preferences.TryGetValue(group.Key, out var preference) || preference.Languages.Count == 0)
            {
                // Nobody asked this shelf for subtitles. No query, no bar.
                continue;
            }

            var ids = group.Select(item => item.Id).ToArray();
            var held = await ReadAsync(connection, kind, ids, preference.Languages, now, cancellationToken);
            var wantedPerFile = preference.WantedPerFile;
            var readFiles = SubtitleLanguageModes.Normalize(preference.Mode) == SubtitleLanguageModes.First;

            foreach (var id in ids)
            {
                var titleHeld = held.TryGetValue(id, out var value) ? value : new MediaSubtitleHeld(0, 0);
                counts[id] = new CatalogueSubtitleCounts(
                    wantedPerFile,
                    readFiles ? titleHeld.Files : titleHeld.Languages);
            }
        }

        return counts;
    }

    /// <summary>
    /// The rollup query itself, exposed so the query-plan guard can explain the
    /// real thing rather than a copy of it that could drift from the real thing.
    /// </summary>
    public static string Sql(MediaTableMap map, int idCount, int languageCount)
    {
        var idParameters = string.Join(", ", Enumerable.Range(0, idCount).Select(index => $"@id{index}"));
        var languageParameters = string.Join(", ", Enumerable.Range(0, languageCount).Select(index => $"@lang{index}"));

        return $"""
            SELECT
                {map.SubtitleRollupIdColumn},
                COUNT(DISTINCT sub.{map.SubtitleMediaIdColumn} || '/' || sub.language),
                COUNT(DISTINCT sub.{map.SubtitleMediaIdColumn})
            FROM {map.SubtitleTable} sub
            {map.SubtitleRollupJoin}
            WHERE {map.SubtitleRollupIdColumn} IN ({idParameters})
              AND sub.forced = 0
              AND sub.language IN ({languageParameters})
            GROUP BY {map.SubtitleRollupIdColumn};
            """;
    }

    public static async Task<IReadOnlyDictionary<string, MediaSubtitleHeld>> ReadAsync(
        DbConnection connection,
        MediaKind kind,
        IReadOnlyList<string> titleIds,
        IReadOnlyList<string> languages,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var held = new Dictionary<string, MediaSubtitleHeld>(StringComparer.Ordinal);
        if (titleIds.Count == 0 || languages.Count == 0)
        {
            return held;
        }

        var map = MediaTableMap.For(kind);

        using var command = connection.CreateCommand();
        command.CommandText = Sql(map, titleIds.Count, languages.Count);

        for (var index = 0; index < titleIds.Count; index++)
        {
            AddParameter(command, $"@id{index}", titleIds[index]);
        }

        for (var index = 0; index < languages.Count; index++)
        {
            AddParameter(command, $"@lang{index}", languages[index]);
        }

        // Only the series rollup dates its episodes; a movie is its own file.
        if (map.SubtitleRollupJoin.Contains("@now", StringComparison.Ordinal))
        {
            AddParameter(command, "@now", now.ToString("O"));
        }

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            held[reader.GetString(0)] = new MediaSubtitleHeld(reader.GetInt32(1), reader.GetInt32(2));
        }

        return held;
    }
}

/// <summary>What one title on a page asked for, per file, and what it holds.</summary>
public sealed record CatalogueSubtitleCounts(int WantedPerFile, int Held);
