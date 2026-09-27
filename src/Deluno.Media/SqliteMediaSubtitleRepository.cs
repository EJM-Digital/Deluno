using System.Data.Common;
using System.Text.Json;
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
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider)
    : IMediaSubtitleRepository
{
    /// <summary>
    /// The next files short of a language the library asked for.
    ///
    /// <para><b>Held means here exactly what it means on the bar.</b> The
    /// predicate is <c>forced = 0</c> and the language matching, which is what
    /// <see cref="CatalogueSubtitleRollup.Sql"/> counts — a file the shelf paints
    /// green while the fetcher keeps searching for it would be two answers to one
    /// question, and that shape is what DESIGN-001 spent a run undoing.</para>
    ///
    /// <para>A forced track is stored and does not count, because a file whose
    /// only English is forced has English for four lines of Elvish. Hearing
    /// impaired does count, because it is watchable.</para>
    ///
    /// <para>Bounded, like the scan, and for the same reason: this runs inside a
    /// job slice and a library of twenty thousand episodes must not arrive in one
    /// lease.</para>
    /// </summary>
    public async Task<IReadOnlyList<MediaSubtitleWantedItem>> ListWantedAsync(
        MediaKind kind,
        string libraryId,
        IReadOnlyList<string> languages,
        int limit,
        bool embeddedCounts,
        CancellationToken cancellationToken)
    {
        if (languages.Count == 0)
        {
            // Nobody asked this shelf for subtitles, so there is no gap and no
            // query. The same rule the rollup follows, and the reason a library
            // that has not asked pays nothing.
            return [];
        }

        var map = MediaTableMap.For(kind);
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            map.DatabaseName,
            cancellationToken);

        var languageParameters = string.Join(", ", Enumerable.Range(0, languages.Count).Select(index => $"@lang{index}"));

        // What counts as held — the same predicate `CatalogueSubtitleRollup`
        // gives the bar.
        var heldPredicate = CatalogueSubtitleRollup.HeldPredicate(embeddedCounts);

        // ...and what counts as *settled*, which is a stricter question and the
        // one this query asks.
        //
        // <b>These two deliberately part company now, and it is worth saying why,
        // because the comment here used to insist they must not.</b> It said a
        // shelf would otherwise "paint a title green while the fetcher kept
        // searching for it" — and that is now exactly the intended behaviour.
        // DESIGN-001 gave the bar three colours: red for a language you do not
        // have, green for one you have that could still get better, gold for one
        // at the cutoff. Green *is* "held, and still being looked at".
        //
        // So held answers "can I watch it tonight" and paints the bar; settled
        // answers "is Deluno finished" and drives this query. A subtitle nobody
        // can prove was cut for your release is the first and not the second —
        // which is the whole of the owner's *"no point spreading lies about subs that
        // may be out of sync."*
        //
        // <b>An embedded track is the one exception (#894).</b> It has no release
        // to check, so it can never climb to the cutoff on its own merits — and
        // asking a provider for a language the container already carries is not
        // improving anything, it is spending a daily allowance on a language that
        // was never missing. When <paramref name="embeddedCounts"/> is false
        // <c>heldPredicate</c> has already thrown embedded rows out, so this
        // never reopens what that setting closed.
        var settledPredicate = $"{heldPredicate} AND (sub.source = '{SubtitleSources.Embedded}' OR sub.match_rung >= @cutoff)";

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                {map.SubtitleFileIdColumn},
                f.file_path,
                {map.SubtitleSearchColumns},
                (
                    SELECT GROUP_CONCAT(sub.language)
                    FROM {map.SubtitleTable} sub
                    WHERE sub.{map.SubtitleMediaIdColumn} = {map.SubtitleFileIdColumn}
                      AND {settledPredicate}
                      AND sub.language IN ({languageParameters})
                ),
                (
                    SELECT GROUP_CONCAT(att.language)
                    FROM {map.SubtitleAttemptTable} att
                    WHERE att.{map.SubtitleMediaIdColumn} = {map.SubtitleFileIdColumn}
                      AND att.language IN ({languageParameters})
                      AND att.next_eligible_search_utc > @now
                ),
                (
                    SELECT att.failure_json
                    FROM {map.SubtitleAttemptTable} att
                    WHERE att.{map.SubtitleMediaIdColumn} = {map.SubtitleFileIdColumn}
                      AND att.failure_json IS NOT NULL
                    ORDER BY att.last_search_utc DESC
                    LIMIT 1
                )
            FROM {map.SubtitleFileSource}
            {map.SubtitleSearchJoin}
            {map.SubtitleFileLibraryJoin}
            WHERE {map.SubtitleFileLibraryFilter}
              AND f.has_file = 1
              AND f.file_path IS NOT NULL
              -- Held *or* waiting on its backoff. Both have to be counted here
              -- rather than subtracted afterwards: the LIMIT is applied by
              -- SQLite, so a slice filtered in C# would come back full of titles
              -- that are not due and do nothing at all.
              AND (
                    SELECT COUNT(*) FROM (
                        SELECT sub.language AS language
                        FROM {map.SubtitleTable} sub
                        WHERE sub.{map.SubtitleMediaIdColumn} = {map.SubtitleFileIdColumn}
                          AND {settledPredicate}
                          AND sub.language IN ({languageParameters})
                        UNION
                        SELECT att.language
                        FROM {map.SubtitleAttemptTable} att
                        WHERE att.{map.SubtitleMediaIdColumn} = {map.SubtitleFileIdColumn}
                          AND att.language IN ({languageParameters})
                          AND att.next_eligible_search_utc > @now
                    )
              ) < @languageCount
            -- Longest-waiting first, and never-tried before either. Without an
            -- order the slice is whatever SQLite hands back, which is the same
            -- ten titles every cycle while the rest of the library is never
            -- asked — and nothing about that looks wrong.
            ORDER BY COALESCE((
                SELECT MIN(att.next_eligible_search_utc)
                FROM {map.SubtitleAttemptTable} att
                WHERE att.{map.SubtitleMediaIdColumn} = {map.SubtitleFileIdColumn}
            ), '') ASC
            LIMIT @limit;
            """;

        AddParameter(command, "@libraryId", libraryId);
        AddParameter(command, "@languageCount", languages.Count);
        AddParameter(command, "@cutoff", (int)SubtitleCutoff.Rung);
        AddParameter(command, "@now", timeProvider.GetUtcNow().ToString("O"));
        AddParameter(command, "@limit", Math.Max(1, limit));
        for (var index = 0; index < languages.Count; index++)
        {
            AddParameter(command, $"@lang{index}", languages[index]);
        }

        var items = new List<MediaSubtitleWantedItem>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            // Ordinals 8, 9 and 10: the id and the path are 0 and 1, and the map's
            // search columns are the six after them. Reading one short of that
            // silently returns the release name as the held-language list, which
            // never matches, and every file looks short of everything for ever.
            var held = Split(reader, 8);
            var waiting = Split(reader, 9);
            var lastFailure = reader.IsDBNull(10) ? null : ReadFailure(reader.GetString(10));

            var missing = languages
                .Where(language =>
                    !held.Contains(language, StringComparer.OrdinalIgnoreCase) &&
                    !waiting.Contains(language, StringComparer.OrdinalIgnoreCase))
                .ToArray();

            if (missing.Length == 0)
            {
                continue;
            }

            items.Add(new MediaSubtitleWantedItem(
                MediaId: reader.GetString(0),
                FilePath: reader.GetString(1),
                Title: reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                Year: reader.IsDBNull(3) ? null : reader.GetInt32(3),
                SeasonNumber: reader.IsDBNull(4) ? null : reader.GetInt32(4),
                EpisodeNumber: reader.IsDBNull(5) ? null : reader.GetInt32(5),
                EpisodeTitle: reader.IsDBNull(6) ? null : reader.GetString(6),
                ReleaseName: reader.IsDBNull(7) ? null : reader.GetString(7),
                LanguagesToFetch: missing,
                LastFailure: lastFailure));
        }

        return items;
    }

    /// <summary>
    /// Remembers that Deluno looked and did not find it, and when to look again.
    ///
    /// <para>The delay doubles from the library's own <c>RetryDelayHours</c> —
    /// the same number the release search uses, because DESIGN-002 asked for
    /// backoff that reads the same way rather than a second vocabulary — and
    /// stops doubling at a fortnight.</para>
    ///
    /// <para><b>It never stops entirely,</b> which is where this parts company
    /// with Weir's Subber. A permanent skip is work that has silently left
    /// the system: nobody finds out the day somebody finally uploads the
    /// subtitle. A hopeless title costs one request a fortnight for ever, which
    /// is nothing, and still succeeds when it becomes possible.</para>
    /// </summary>
    public async Task RecordAttemptAsync(
        MediaKind kind,
        string mediaId,
        string language,
        string? result,
        TimeSpan baseDelay,
        CancellationToken cancellationToken,
        IntegrationFailure? failure = null)
    {
        var map = MediaTableMap.For(kind);
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            map.DatabaseName,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {map.SubtitleAttemptTable} (
                {map.SubtitleMediaIdColumn}, language, attempts, last_search_utc, next_eligible_search_utc, last_result, failure_json
            )
            VALUES (@mediaId, @language, 1, @now, @firstRetry, @result, @failureJson)
            ON CONFLICT ({map.SubtitleMediaIdColumn}, language) DO UPDATE SET
                attempts = attempts + 1,
                last_search_utc = @now,
                -- Doubling is done here rather than in C# so the count and the
                -- delay cannot drift apart across two round trips.
                next_eligible_search_utc = MIN(
                    datetime(@now, '+' || (@baseMinutes * (1 << MIN(attempts, @maxDoublings))) || ' minutes'),
                    @cap),
                last_result = @result,
                failure_json = @failureJson;
            """;

        var baseMinutes = Math.Max(1, (int)baseDelay.TotalMinutes);
        AddParameter(command, "@mediaId", mediaId);
        AddParameter(command, "@language", language);
        AddParameter(command, "@now", now.ToString("O"));
        AddParameter(command, "@firstRetry", now.Add(baseDelay).ToString("O"));
        AddParameter(command, "@baseMinutes", baseMinutes);
        AddParameter(command, "@maxDoublings", MaxDoublings);
        AddParameter(command, "@cap", now.Add(MaxBackoff).ToString("O"));
        AddParameter(command, "@result", result);
        AddParameter(command, "@failureJson", failure is null ? null : JsonSerializer.Serialize(failure));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Forgets an outstanding attempt, because the subtitle arrived.
    ///
    /// <para>Deleting rather than zeroing keeps this table holding only work
    /// that is still outstanding, which is what makes the ordering above cheap.</para>
    /// </summary>
    public async Task ClearAttemptAsync(
        MediaKind kind,
        string mediaId,
        string language,
        CancellationToken cancellationToken)
    {
        var map = MediaTableMap.For(kind);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            map.DatabaseName,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            $"DELETE FROM {map.SubtitleAttemptTable} WHERE {map.SubtitleMediaIdColumn} = @mediaId AND language = @language;";

        AddParameter(command, "@mediaId", mediaId);
        AddParameter(command, "@language", language);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>How many times the delay may double before it stops growing.</summary>
    private const int MaxDoublings = 6;

    /// <summary>
    /// The longest Deluno will wait before asking again. A fortnight, chosen so
    /// that a title nobody has subtitled costs one request every two weeks
    /// rather than disappearing.
    /// </summary>
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromDays(14);

    private static string[] Split(System.Data.Common.DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal)
            ? []
            : reader.GetString(ordinal).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IntegrationFailure? ReadFailure(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<IntegrationFailure>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task RecordFetchedAsync(
        MediaKind kind,
        string mediaId,
        MediaSubtitleRow subtitle,
        CancellationToken cancellationToken)
    {
        var map = MediaTableMap.For(kind);
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            map.DatabaseName,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {map.SubtitleTable} (
                {map.SubtitleMediaIdColumn}, language, forced, hearing_impaired,
                source, file_path, stream_index, codec, provider, match_rung, created_utc, updated_utc
            )
            VALUES (
                @mediaId, @language, @forced, @hearingImpaired,
                @source, @filePath, NULL, @codec, @provider, @matchRung, @now, @now
            )
            ON CONFLICT ({map.SubtitleMediaIdColumn}, language, forced, hearing_impaired) DO UPDATE SET
                source = excluded.source,
                file_path = excluded.file_path,
                codec = excluded.codec,
                provider = excluded.provider,
                match_rung = excluded.match_rung,
                updated_utc = excluded.updated_utc;
            """;

        AddParameter(command, "@mediaId", mediaId);
        AddParameter(command, "@language", subtitle.Language);
        AddParameter(command, "@forced", subtitle.Forced ? 1 : 0);
        AddParameter(command, "@hearingImpaired", subtitle.HearingImpaired ? 1 : 0);
        AddParameter(command, "@source", subtitle.Source);
        AddParameter(command, "@filePath", subtitle.FilePath);
        AddParameter(command, "@codec", subtitle.Codec);
        AddParameter(command, "@provider", subtitle.Provider);
        AddParameter(command, "@matchRung", subtitle.MatchRung);
        AddParameter(command, "@now", now.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Files whose subtitles have never been read, were read when the file was
    /// a different file, or were only half read.
    ///
    /// Bounded and indexed rather than streamed and filtered: a library scan
    /// that reads twenty thousand rows to find the eleven it has not done yet
    /// is a scan somebody notices. Size is compared as well as path because a
    /// replaced upgrade keeps the name and changes everything else about the
    /// file, subtitle tracks included.
    ///
    /// <para><b>A file read without ffprobe is read again once ffprobe is
    /// there.</b> Only the subtitles beside it could be seen the first time, so
    /// the tracks inside it are still unknown — and an install can gain ffprobe
    /// at any point, which is exactly what the lab rig did. <c>unavailable</c>
    /// and <c>failed</c> are treated differently on purpose, for the reason
    /// <see cref="Deluno.Filesystem.FfprobeMediaProbeService"/> already gives:
    /// a missing binary is an environment state that changes, and a file
    /// ffprobe could not parse is a fact about the file. Retrying the second
    /// one every cycle would read a corrupt file forever.</para>
    ///
    /// <para><b>And a file is read again on a cadence even when nothing about
    /// the video changed</b>, because the thing that changed may be beside it.
    /// Deleting a <c>.srt</c> alters the video's path, size and probe status not
    /// at all — it is a different file — so before this the row saying English
    /// was held stood for ever, the shelf went on reporting that every file had
    /// what you asked for, and nothing ever fetched it again. The same blind
    /// spot swallowed a subtitle dropped in by hand, which is the commoner half
    /// of it.</para>
    ///
    /// <para>The cadence costs a directory listing per file and not an ffprobe:
    /// <see cref="MediaSubtitleScanCandidate.VideoChanged"/> carries which half
    /// is needed, and the tracks inside a container cannot move while the
    /// container does not. Oldest read first, so a library that has fallen
    /// behind catches up in order rather than by whatever the index
    /// offers.</para>
    /// </summary>
    /// <summary>
    /// Forget that these files were ever probed, so the next subtitle pass
    /// reads them again.
    ///
    /// <para><b>No new job and no new lane.</b> A title becomes a scan
    /// candidate when it has no scan row — see <see cref="ListPendingScansAsync"/>,
    /// where <c>scan.… IS NULL</c> is the first thing that makes one pending.
    /// So "re-read the subtitles for these forty titles" is a delete, and the
    /// library's existing subtitle pass does the work on its own schedule. A
    /// per-title subtitle job type would have been a second way to do the same
    /// thing, racing the first (DESIGN-002 rule 3).</para>
    ///
    /// <para>The state rows are left alone deliberately: the subtitles you hold
    /// are still held, and the shelf goes on saying so until the re-probe
    /// replaces them. Clearing both would blank the subtitle bar for every
    /// selected title until the pass got to it, which looks like Deluno losing
    /// them.</para>
    /// </summary>
    /// <returns>How many had been probed and now have not.</returns>
    public async Task<int> ClearScansAsync(
        MediaKind kind,
        IReadOnlyList<string> mediaIds,
        CancellationToken cancellationToken)
    {
        if (mediaIds.Count == 0)
        {
            return 0;
        }

        var map = MediaTableMap.For(kind);
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            map.DatabaseName,
            cancellationToken);

        var parameters = string.Join(", ", mediaIds.Select((_, index) => $"@id{index}"));

        using var command = connection.CreateCommand();
        command.CommandText =
            $"DELETE FROM {map.SubtitleScanTable} WHERE {map.SubtitleMediaIdColumn} IN ({parameters});";

        for (var index = 0; index < mediaIds.Count; index++)
        {
            AddParameter(command, $"@id{index}", mediaIds[index]);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MediaSubtitleScanCandidate>> ListPendingScansAsync(
        MediaKind kind,
        string libraryId,
        int limit,
        DateTimeOffset staleBefore,
        CancellationToken cancellationToken)
    {
        var map = MediaTableMap.For(kind);
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            map.DatabaseName,
            cancellationToken);

        // Named once because the SELECT reports it and the WHERE selects on it,
        // and a WHERE that admitted a row the SELECT then called unchanged
        // would skip the probe on a file that had never had one.
        var videoChanged = $"""
            scan.{map.SubtitleMediaIdColumn} IS NULL
                 OR scan.file_path <> f.file_path
                 OR COALESCE(scan.file_size_bytes, -1) <> COALESCE(f.file_size_bytes, -1)
                 OR scan.probe_status = 'unavailable'
            """;

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                {map.SubtitleFileIdColumn},
                f.file_path,
                f.file_size_bytes,
                CASE WHEN {videoChanged} THEN 1 ELSE 0 END
            FROM {map.SubtitleFileSource}
            {map.SubtitleFileLibraryJoin}
            LEFT JOIN {map.SubtitleScanTable} scan
                ON scan.{map.SubtitleMediaIdColumn} = {map.SubtitleFileIdColumn}
            WHERE {map.SubtitleFileLibraryFilter}
              AND f.has_file = 1
              AND f.file_path IS NOT NULL
              AND (
                    {videoChanged}
                 OR scan.scanned_utc IS NULL
                 OR scan.scanned_utc < @staleBefore
              )
            ORDER BY scan.scanned_utc IS NOT NULL, scan.scanned_utc
            LIMIT @limit;
            """;
        AddParameter(command, "@libraryId", libraryId);
        // Formatted exactly as the column is written, a few lines down in
        // RecordScanAsync: a DateTimeOffset renders the offset as +00:00 and a
        // DateTime renders it as Z, and these are compared as text.
        AddParameter(command, "@staleBefore", staleBefore.ToUniversalTime().ToString("O"));
        AddParameter(command, "@limit", Math.Max(1, limit));

        var candidates = new List<MediaSubtitleScanCandidate>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add(new MediaSubtitleScanCandidate(
                MediaId: reader.GetString(0),
                FilePath: reader.GetString(1),
                FileSizeBytes: reader.IsDBNull(2) ? null : reader.GetInt64(2),
                VideoChanged: reader.GetInt64(3) == 1));
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
                    -- A subtitle Deluno fetched is still Deluno's own work when
                    -- a later pass finds it sitting there as an ordinary file.
                    -- The summary on `SubtitleSources.Fetched` already promised
                    -- this and only `provider` was actually kept; `source`
                    -- flipped to `external` the first time a rescan touched it.
                    -- Rare enough to go unnoticed while a rescan needed the
                    -- video to change, and routine the moment one runs on a
                    -- cadence.
                    source = CASE
                        WHEN {map.SubtitleTable}.source = '{SubtitleSources.Fetched}'
                         AND excluded.source = '{SubtitleSources.External}'
                        THEN {map.SubtitleTable}.source
                        ELSE excluded.source
                    END,
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
                    readFiles ? titleHeld.Files : titleHeld.Languages,
                    // In "first language wins" mode the bar counts files rather
                    // than languages, and a file is settled when the one language
                    // it found is. Capped at the held count so the gold segment
                    // can never be longer than the green one it sits inside.
                    Math.Min(readFiles ? titleHeld.Files : titleHeld.Languages, titleHeld.Settled));
            }
        }

        return counts;
    }

    /// <summary>
    /// The rollup query itself, exposed so the query-plan guard can explain the
    /// real thing rather than a copy of it that could drift from the real thing.
    /// </summary>
    /// <summary>
    /// What counts as <b>held</b>, as one SQL fragment.
    ///
    /// <para>Read by the bar's rollup and by the fetcher's "what is still
    /// missing" query, so the two cannot answer differently about the same
    /// title — which is DESIGN-001's defect one subsystem out.</para>
    ///
    /// <para><b>Forced never counts.</b> A file whose only English is forced has
    /// English for four lines of Elvish.</para>
    ///
    /// <para><b>Embedded counts unless the library says otherwise.</b> Deluno has
    /// always counted a track inside the container; some people want a sidecar
    /// regardless, because a player handles the two differently and an embedded
    /// track cannot be swapped or corrected (#321).</para>
    /// </summary>
    public static string HeldPredicate(bool embeddedCounts)
        => embeddedCounts
            ? "sub.forced = 0"
            : $"sub.forced = 0 AND sub.source <> '{SubtitleSources.Embedded}'";

    public static string Sql(MediaTableMap map, int idCount, int languageCount, bool embeddedCounts = true)
    {
        var idParameters = string.Join(", ", Enumerable.Range(0, idCount).Select(index => $"@id{index}"));
        var languageParameters = string.Join(", ", Enumerable.Range(0, languageCount).Select(index => $"@lang{index}"));

        return $"""
            SELECT
                {map.SubtitleRollupIdColumn},
                COUNT(DISTINCT sub.{map.SubtitleMediaIdColumn} || '/' || sub.language),
                COUNT(DISTINCT sub.{map.SubtitleMediaIdColumn}),
                -- The third number, and the one that makes gold possible.
                --
                -- <b>Deliberately narrower than the first, and never instead of
                -- it.</b> Held is "can I watch this tonight" and must stay blind
                -- to the cutoff; settled is "has Deluno finished". Folding the
                -- rung into the held count would strip the green off every title
                -- Deluno is still improving and make the shelf read as though
                -- nothing had been fetched at all.
                COUNT(DISTINCT CASE
                    WHEN sub.match_rung >= {(int)SubtitleCutoff.Rung}
                    THEN sub.{map.SubtitleMediaIdColumn} || '/' || sub.language
                END)
            FROM {map.SubtitleTable} sub
            {map.SubtitleRollupJoin}
            WHERE {map.SubtitleRollupIdColumn} IN ({idParameters})
              AND {HeldPredicate(embeddedCounts)}
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
            held[reader.GetString(0)] = new MediaSubtitleHeld(reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
        }

        return held;
    }
}

/// <summary>What one title on a page asked for, per file, and what it holds.</summary>
public sealed record CatalogueSubtitleCounts(int WantedPerFile, int Held, int Settled = 0);
