using System.Data.Common;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Contracts;

namespace Deluno.Jobs.Data;

public sealed class SqliteDownloadDispatchesRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider,
    IRealtimeEventPublisher? realtimeEventPublisher = null)
    : IDownloadDispatchesRepository
{
    /// <summary>
    /// Every column <see cref="ReadDispatch"/> reads, in the order it reads them. Eight queries used to spell this
    /// list out separately, so adding a column meant eight edits that had to agree.
    /// </summary>
    private const string DispatchColumns =
        "id, library_id, media_type, entity_type, entity_id, release_name, " +
        "indexer_name, download_client_id, download_client_name, status, notes_json, " +
        "created_utc, grab_status, grab_attempted_utc, grab_response_code, " +
        "grab_message, grab_failure_code, grab_response_json, grab_failure_json, detected_utc, " +
        "torrent_hash_or_item_id, downloaded_bytes, import_status, import_detected_utc, " +
        "import_completed_utc, imported_file_path, import_failure_code, " +
        "import_failure_message, import_failure_json, circuit_open_until_utc, next_retry_eligible_utc, attempt_count, " +
        "client_completed_utc, decision_quality, decision_custom_format_score, decision_language, indexer_id, started_by, started_by_detail, " +
        "imported_file_bytes";

    /// <summary>
    /// The largest notes / grab-response JSON a list keeps (#1081). Above it the
    /// column reads as null in a list, and <c>octet_length</c> answers from the
    /// record header, so a large value is never read off disk to be thrown away.
    /// </summary>
    public const int ListJsonLimitBytes = 65536;

    private static readonly string DispatchListColumns = DispatchColumns
        .Replace(
            "notes_json,",
            $"CASE WHEN octet_length(notes_json) <= {ListJsonLimitBytes} THEN notes_json END AS notes_json,",
            StringComparison.Ordinal)
        .Replace(
            "grab_response_json,",
            $"CASE WHEN octet_length(grab_response_json) <= {ListJsonLimitBytes} THEN grab_response_json END AS grab_response_json,",
            StringComparison.Ordinal);

    public async Task<DownloadDispatchItem?> GetDispatchIncludingArchivedAsync(
        string dispatchId,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT
                {DispatchColumns}
            FROM download_dispatches
            WHERE id = @dispatchId
            """;
        AddParameter(command, "@dispatchId", dispatchId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadDispatch(reader) : null;
    }

    public async Task<DownloadDispatchItem?> GetDispatchAsync(
        string dispatchId,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT
                {DispatchColumns}
            FROM download_dispatches
            WHERE id = @dispatchId AND status != 'archived'
            """;

        AddParameter(command, "@dispatchId", dispatchId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return ReadDispatch(reader);
        }

        return null;
    }

    public async Task<DownloadDispatchItem> RecordGrabAsync(
        string dispatchId,
        string grabStatus,
        int? grabResponseCode,
        string? grabMessage,
        string? grabFailureCode,
        string? grabResponseJson,
        CancellationToken cancellationToken,
        IntegrationFailure? failure = null,
        string? externalId = null)
    {
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE download_dispatches
                SET
                    grab_status = @grabStatus,
                    grab_attempted_utc = @grabAttemptedUtc,
                    grab_response_code = @grabResponseCode,
                    grab_message = @grabMessage,
                    grab_failure_code = @grabFailureCode,
                    grab_response_json = @grabResponseJson,
                    grab_failure_json = @grabFailureJson,
                    torrent_hash_or_item_id = COALESCE(@externalId, torrent_hash_or_item_id)
                WHERE id = @dispatchId
                """;

            AddParameter(command, "@dispatchId", dispatchId);
            AddParameter(command, "@grabStatus", grabStatus);
            AddParameter(command, "@grabAttemptedUtc", now.ToString("O"));
            AddParameter(command, "@grabResponseCode", grabResponseCode);
            AddParameter(command, "@grabMessage", grabMessage);
            AddParameter(command, "@grabFailureCode", grabFailureCode);
            AddParameter(command, "@grabResponseJson", grabResponseJson);
            AddParameter(command, "@grabFailureJson", failure is null ? null : JsonSerializer.Serialize(failure));
            AddParameter(command, "@externalId", string.IsNullOrWhiteSpace(externalId) ? null : externalId.Trim());

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await RecordTimelineEventInternalAsync(
            connection,
            transaction,
            dispatchId,
            grabStatus == "succeeded" ? "grab_succeeded" : "grab_failed",
            JsonSerializer.Serialize(new
            {
                grabStatus,
                grabResponseCode,
                grabMessage,
                grabFailureCode,
                externalId
            }),
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        await AnnounceStageAsync(dispatchId, grabStatus == "succeeded" ? "grab_succeeded" : "grab_failed", grabMessage, cancellationToken);

        var result = await GetDispatchAsync(dispatchId, cancellationToken);
        return result ?? throw new InvalidOperationException($"Dispatch {dispatchId} not found after record");
    }

    public async Task<DownloadDispatchItem> RecordDetectionAsync(
        string dispatchId,
        string? torrentHashOrItemId,
        long? downloadedBytes,
        bool completed,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Each fact is written once. Polling reports a download on every pass, and
        // a second sighting must neither move the times nor add to the timeline.
        bool alreadyDetected;
        bool alreadyCompleted;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
                """
                SELECT detected_utc IS NOT NULL, client_completed_utc IS NOT NULL
                FROM download_dispatches
                WHERE id = @dispatchId
                """;
            AddParameter(read, "@dispatchId", dispatchId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException($"Dispatch {dispatchId} not found for detection");
            }

            alreadyDetected = reader.GetBoolean(0);
            alreadyCompleted = reader.GetBoolean(1);
        }

        var firstSighting = !alreadyDetected;
        var firstCompletion = completed && !alreadyCompleted;
        if (firstSighting || firstCompletion)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    """
                    UPDATE download_dispatches
                    SET
                        detected_utc = COALESCE(detected_utc, @now),
                        client_completed_utc = CASE WHEN @completed = 1 THEN COALESCE(client_completed_utc, @now) ELSE client_completed_utc END,
                        torrent_hash_or_item_id = CASE WHEN torrent_hash_or_item_id IS NULL OR torrent_hash_or_item_id = '' THEN @torrentHashOrItemId ELSE torrent_hash_or_item_id END,
                        downloaded_bytes = COALESCE(@downloadedBytes, downloaded_bytes),
                        -- A client that was down for longer than the detection
                        -- limit shows the download late. That undoes the ending
                        -- MarkNeverDetectedAsync gave it; any other failure stands.
                        status = CASE WHEN status = 'failed' AND grab_failure_code = 'not_found' THEN 'sent' ELSE status END,
                        grab_message = CASE WHEN grab_failure_code = 'not_found' THEN NULL ELSE grab_message END,
                        grab_failure_code = CASE WHEN grab_failure_code = 'not_found' THEN NULL ELSE grab_failure_code END
                    WHERE id = @dispatchId
                    """;

                AddParameter(command, "@dispatchId", dispatchId);
                AddParameter(command, "@now", now.ToString("O"));
                AddParameter(command, "@completed", completed ? 1 : 0);
                AddParameter(command, "@torrentHashOrItemId", torrentHashOrItemId);
                AddParameter(command, "@downloadedBytes", downloadedBytes);

                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await RecordTimelineEventInternalAsync(
                connection,
                transaction,
                dispatchId,
                firstSighting ? "detection_succeeded" : "client_completed",
                JsonSerializer.Serialize(new
                {
                    torrentHashOrItemId,
                    downloadedBytes,
                    completed
                }),
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        if (firstSighting || firstCompletion)
        {
            await AnnounceStageAsync(dispatchId, firstSighting ? "detection_succeeded" : "client_completed", null, cancellationToken);
        }

        var result = await GetDispatchAsync(dispatchId, cancellationToken);
        return result ?? throw new InvalidOperationException($"Dispatch {dispatchId} not found after detection");
    }

    /// <summary>
    /// One word per outcome, enforced at the door.
    ///
    /// <c>completed</c> and <c>imported</c> both meant "the import finished"
    /// until V0016: every writer used <c>imported</c> and three readers asked
    /// for <c>completed</c>, so nothing was ever archived and the
    /// successful-import metric served zero. Normalising here is what stops a
    /// caller reintroducing the second word — the same guard the wanted-status
    /// repositories already use.
    /// </summary>
    private static string NormalizeImportStatus(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "imported" or "completed" => "imported",
            "failed" => "failed",
            _ => "pending"
        };

    public async Task<DownloadDispatchItem> RecordImportOutcomeAsync(
        string dispatchId,
        string importStatus,
        string? importedFilePath,
        string? importFailureCode,
        string? importFailureMessage,
        CancellationToken cancellationToken,
        IntegrationFailure? failure = null,
        long? importedFileBytes = null)
    {
        var now = timeProvider.GetUtcNow();
        importStatus = NormalizeImportStatus(importStatus);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var persistedFailure = importStatus == "failed"
            ? failure ?? IntegrationFailureFactory.FromLegacy(
                "deluno",
                dispatchId,
                "Deluno import",
                "import",
                "rejected",
                importFailureMessage ?? "The downloaded file could not be imported.",
                code: importFailureCode,
                externalId: dispatchId)
            : null;

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE download_dispatches
                SET
                    import_status = @importStatus,
                    import_detected_utc = CASE
                        WHEN import_detected_utc IS NULL THEN @importDetectedUtc
                        ELSE import_detected_utc
                    END,
                    import_completed_utc = CASE
                        WHEN @importStatus IN ('imported', 'failed') THEN @importCompletedUtc
                        ELSE import_completed_utc
                    END,
                    imported_file_path = @importedFilePath,
                    imported_file_bytes = @importedFileBytes,
                    import_failure_code = @importFailureCode,
                    import_failure_message = @importFailureMessage,
                    import_failure_json = @importFailureJson
                WHERE id = @dispatchId
                """;

            AddParameter(command, "@dispatchId", dispatchId);
            AddParameter(command, "@importStatus", importStatus);
            AddParameter(command, "@importDetectedUtc", now.ToString("O"));
            AddParameter(command, "@importCompletedUtc", now.ToString("O"));
            AddParameter(command, "@importedFilePath", importedFilePath);
            AddParameter(command, "@importedFileBytes", importedFileBytes);
            AddParameter(command, "@importFailureCode", importFailureCode);
            AddParameter(command, "@importFailureMessage", importFailureMessage);
            AddParameter(
                command,
                "@importFailureJson",
                persistedFailure is null ? null : JsonSerializer.Serialize(persistedFailure));

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var eventType = importStatus == "imported" ? "import_succeeded" : "import_failed";
        await RecordTimelineEventInternalAsync(
            connection,
            transaction,
            dispatchId,
            eventType,
            JsonSerializer.Serialize(new
            {
                importStatus,
                importedFilePath,
                importFailureCode,
                importFailureMessage,
                failure = persistedFailure
            }),
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        await AnnounceStageAsync(dispatchId, eventType, importFailureMessage, cancellationToken);

        var result = await GetDispatchIncludingArchivedAsync(dispatchId, cancellationToken);
        return result ?? throw new InvalidOperationException($"Dispatch {dispatchId} not found after import outcome");
    }

    public async Task<(IReadOnlyList<DownloadDispatchItem> Items, string? NextPageToken)> QueryDispatchesAsync(
        DispatchQueryFilter filter,
        DispatchPaginationOptions pagination,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        var whereConditions = filter.IncludeArchived
            ? new List<string>()
            : new List<string> { "status != 'archived'" };
        var parameters = new Dictionary<string, object?>();

        // The same three-way rule as DispatchHistoryEvents.For, in SQL.
        switch (DispatchHistoryEvents.Normalize(filter.HistoryEvent))
        {
            case DispatchHistoryEvents.Imported:
                whereConditions.Add("import_status = 'imported'");
                break;
            case DispatchHistoryEvents.Failed:
                whereConditions.Add("COALESCE(import_status, '') != 'imported' AND (grab_status = 'failed' OR import_status = 'failed')");
                break;
            case DispatchHistoryEvents.Grabbed:
                whereConditions.Add("COALESCE(import_status, '') NOT IN ('imported', 'failed') AND COALESCE(grab_status, '') != 'failed'");
                break;
        }

        if (!string.IsNullOrEmpty(filter.GrabStatus))
        {
            whereConditions.Add("grab_status = @grabStatus");
            parameters["grabStatus"] = filter.GrabStatus;
        }

        if (!string.IsNullOrEmpty(filter.ImportStatus))
        {
            whereConditions.Add("import_status = @importStatus");
            // Through the same door as a write, so a caller asking for the word
            // this column no longer stores still gets its rows.
            parameters["importStatus"] = NormalizeImportStatus(filter.ImportStatus);
        }

        if (!string.IsNullOrEmpty(filter.ClientId))
        {
            whereConditions.Add("download_client_id = @clientId");
            parameters["clientId"] = filter.ClientId;
        }

        if (!string.IsNullOrEmpty(filter.MediaType))
        {
            whereConditions.Add("media_type = @mediaType");
            parameters["mediaType"] = filter.MediaType;
        }

        if (!string.IsNullOrEmpty(filter.EntityType))
        {
            whereConditions.Add("entity_type = @entityType");
            parameters["entityType"] = filter.EntityType;
        }

        if (!string.IsNullOrEmpty(filter.EntityId))
        {
            whereConditions.Add("entity_id = @entityId");
            parameters["entityId"] = filter.EntityId;
        }

        if (!string.IsNullOrEmpty(filter.LibraryId))
        {
            whereConditions.Add("library_id = @libraryId");
            parameters["libraryId"] = filter.LibraryId;
        }

        if (filter.MinGrabTime.HasValue)
        {
            whereConditions.Add("grab_attempted_utc >= @minGrabTime");
            parameters["minGrabTime"] = filter.MinGrabTime.Value.ToString("O");
        }

        if (filter.MaxGrabTime.HasValue)
        {
            whereConditions.Add("grab_attempted_utc <= @maxGrabTime");
            parameters["maxGrabTime"] = filter.MaxGrabTime.Value.ToString("O");
        }

        if (filter.MinImportTime.HasValue)
        {
            whereConditions.Add("import_completed_utc >= @minImportTime");
            parameters["minImportTime"] = filter.MinImportTime.Value.ToString("O");
        }

        if (filter.MaxImportTime.HasValue)
        {
            whereConditions.Add("import_completed_utc <= @maxImportTime");
            parameters["maxImportTime"] = filter.MaxImportTime.Value.ToString("O");
        }

        var pageSize = new PageRequest(pagination.PageSize, pagination.PageToken).BoundedPageSize;
        var token = DelunoPageToken.Decode(pagination.PageToken, 2);

        // The page boundary is a condition like any other, and keeping it in
        // the list is what stops the clause ever being empty. A query that
        // filtered on nothing built "WHERE  AND (...)" - a syntax error, and a
        // 500 to the caller. Asking for archived rows without naming a media
        // type or an entity did exactly that, because dropping the
        // "not archived" condition left nothing behind it.
        // Only written when there is a boundary: SQLite does not see through a
        // "@sortUtc IS NULL OR" guard, and the sort could then not use
        // ix_download_dispatches_sort (#1081).
        if (token is not null)
        {
            whereConditions.Add(
                """
                (COALESCE(grab_attempted_utc, created_utc) < @sortUtc
                 OR (COALESCE(grab_attempted_utc, created_utc) = @sortUtc AND rowid < CAST(@id AS INTEGER)))
                """);
        }

        var whereClause = whereConditions.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", whereConditions);

        // Fetch one extra to determine if there's a next page
        var fetchCount = pageSize + 1;

        using var command = connection.CreateCommand();
        command.CommandText =
            $$"""
            SELECT
                {{(filter.OmitLargeJson ? DispatchListColumns : DispatchColumns)}}, rowid
            FROM download_dispatches
            {{whereClause}}
            ORDER BY COALESCE(grab_attempted_utc, created_utc) DESC, rowid DESC
            LIMIT @limit
            """;

        foreach (var param in parameters)
        {
            AddParameter(command, $"@{param.Key}", param.Value);
        }

        AddParameter(command, "@limit", fetchCount);
        if (token is not null)
        {
            AddParameter(command, "@sortUtc", token[0]);
            AddParameter(command, "@id", token[1]);
        }

        var items = new List<DownloadDispatchItem>();
        var rowids = new List<long>(fetchCount);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadDispatch(reader));
            rowids.Add(reader.GetInt64(reader.FieldCount - 1));
        }

        string? nextPageToken = null;
        if (items.Count > pageSize)
        {
            items.RemoveAt(pageSize);
            var last = items[^1];
            nextPageToken = DelunoPageToken.Encode(
                (last.GrabAttemptedUtc ?? last.CreatedUtc).ToString("O"),
                rowids[pageSize - 1].ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return (items, nextPageToken);
    }

    public async Task<IReadOnlyList<DownloadInFlight>> ListDownloadsInFlightAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        // Not FindUnresolvedDispatchesAsync, which this used to be: that asks
        // which grabs the client never showed, so a download the client had
        // shown and that was waiting for its import, or sitting with a
        // processor, did not count as happening, and its title went back on the
        // work list to be grabbed again. A download the client never showed
        // still counts: its client may only be unreachable, and counting it
        // costs a delay, while not counting it costs a second download.
        command.CommandText =
            """
            SELECT entity_type, entity_id, release_name,
                   CASE WHEN status = 'failed' AND grab_failure_code = 'not_found' THEN 1 ELSE 0 END,
                   download_client_id, download_client_name, torrent_hash_or_item_id
            FROM download_dispatches
            WHERE grab_status = 'succeeded'
              AND archived_utc IS NULL
              AND COALESCE(status, '') <> 'archived'
              AND COALESCE(import_status, '') NOT IN ('imported', 'failed')
              AND entity_id IS NOT NULL AND entity_id <> ''
            ORDER BY grab_attempted_utc DESC
            LIMIT @limit
            """;
        AddParameter(command, "@limit", limit);

        var downloads = new List<DownloadInFlight>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            downloads.Add(new DownloadInFlight(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) == 1,
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return downloads;
    }

    public async Task<IReadOnlyList<DispatchTimelineEvent>> GetDispatchTimelineAsync(
        string dispatchId,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, dispatch_id, event_type, timestamp, details_json, created_utc
            FROM download_dispatch_timeline
            WHERE dispatch_id = @dispatchId
              -- Bookkeeping rows (see IDispatchProblemLog) are not events a person reads.
              AND event_type NOT LIKE 'problem-%'
            ORDER BY timestamp DESC
            """;

        AddParameter(command, "@dispatchId", dispatchId);

        var events = new List<DispatchTimelineEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(ReadTimelineEvent(reader));
        }

        return events;
    }

    public async Task<DispatchTimelineEvent> RecordTimelineEventAsync(
        string dispatchId,
        string eventType,
        string? detailsJson,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var result = await RecordTimelineEventInternalAsync(
            connection,
            transaction,
            dispatchId,
            eventType,
            detailsJson,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        await AnnounceStageAsync(dispatchId, eventType, null, cancellationToken);
        return result;
    }

    public async Task<DownloadDispatchItem> SetCircuitBreakerAsync(
        string dispatchId,
        DateTimeOffset? openUntilUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE download_dispatches
            SET circuit_open_until_utc = @openUntilUtc
            WHERE id = @dispatchId
            """;

        AddParameter(command, "@dispatchId", dispatchId);
        AddParameter(command, "@openUntilUtc", openUntilUtc?.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);

        var result = await GetDispatchAsync(dispatchId, cancellationToken);
        return result ?? throw new InvalidOperationException($"Dispatch {dispatchId} not found");
    }

    public async Task ArchiveDispatchAsync(
        string dispatchId,
        string reason,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE download_dispatches
                SET status = 'archived',
                    archived_utc = @archivedUtc
                WHERE id = @dispatchId
                """;

            AddParameter(command, "@dispatchId", dispatchId);
            AddParameter(command, "@archivedUtc", timeProvider.GetUtcNow().ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await RecordTimelineEventInternalAsync(
            connection,
            transaction,
            dispatchId,
            "archived",
            JsonSerializer.Serialize(new { reason }),
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        await AnnounceStageAsync(dispatchId, "archived", reason, cancellationToken);
    }

    public async Task<int> CloseContainedDispatchesAsync(string clientId, string hash, string reason, CancellationToken cancellationToken)
    {
        var closed = 0;
        foreach (var open in await FindOpenDispatchesByHashAsync(clientId, hash, cancellationToken))
        {
            if (string.Equals(open.ImportStatus, "imported", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            await ArchiveDispatchAsync(open.Id, reason, cancellationToken);
            await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Jobs, cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE download_dispatches
                SET import_failure_code = @code, import_failure_message = @message
                WHERE id = @id
                """;
            AddParameter(command, "@id", open.Id);
            AddParameter(command, "@code", IDownloadDispatchesRepository.ContainedFailureCode);
            AddParameter(command, "@message", reason);
            await command.ExecuteNonQueryAsync(cancellationToken);
            closed++;
        }

        return closed;
    }

    /// <summary>Timeline entries that are bookkeeping, not a move on the journey.</summary>
    private static readonly HashSet<string> NotAStageChange = new(StringComparer.Ordinal)
    {
        "history",
        "file_provenance_recorded",
        "health-remediation-applied"
    };

    /// <summary>
    /// Says on the Live board, the moment it is written, that a download moved on its journey (#1097):
    /// grab sent or refused, seen in the client, finished there, import done or failed, never shown, archived.
    /// Sent after the commit so a screen never hears of something that did not stick.
    /// </summary>
    private async Task AnnounceStageAsync(string dispatchId, string eventType, string? detail, CancellationToken cancellationToken)
    {
        if (realtimeEventPublisher is null || NotAStageChange.Contains(eventType))
        {
            return;
        }

        try
        {
            var dispatch = await GetDispatchIncludingArchivedAsync(dispatchId, cancellationToken);
            await realtimeEventPublisher.PublishJourneyStageChangedAsync(
                new JourneyStageChangedEvent(
                    dispatchId,
                    dispatch?.ReleaseName,
                    dispatch?.MediaType,
                    dispatch?.EntityType,
                    dispatch?.EntityId,
                    dispatch?.LibraryId,
                    eventType,
                    detail,
                    timeProvider.GetUtcNow().ToString("O")),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // A push that cannot be made never undoes what was just saved.
        }
    }

    private async Task<DispatchTimelineEvent> RecordTimelineEventInternalAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string dispatchId,
        string eventType,
        string? detailsJson,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var eventId = Guid.CreateVersion7().ToString("N");

        using var command = connection.CreateCommand();
        if (transaction != null)
        {
            command.Transaction = transaction;
        }
        command.CommandText =
            """
            INSERT INTO download_dispatch_timeline
            (id, dispatch_id, event_type, timestamp, details_json, created_utc)
            VALUES (@id, @dispatchId, @eventType, @timestamp, @detailsJson, @createdUtc)
            """;

        AddParameter(command, "@id", eventId);
        AddParameter(command, "@dispatchId", dispatchId);
        AddParameter(command, "@eventType", eventType);
        AddParameter(command, "@timestamp", now.ToString("O"));
        AddParameter(command, "@detailsJson", detailsJson);
        AddParameter(command, "@createdUtc", now.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);

        return new DispatchTimelineEvent(
            Id: eventId,
            DispatchId: dispatchId,
            EventType: eventType,
            Timestamp: now,
            DetailsJson: detailsJson,
            CreatedUtc: now);
    }

    public async Task<IReadOnlyList<DownloadDispatchItem>> FindStaleFailedDispatchesAsync(
        TimeSpan minAge,
        int limit,
        CancellationToken cancellationToken)
    {
        var cutoff = timeProvider.GetUtcNow().Subtract(minAge);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs, cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT
                {DispatchColumns}
            FROM download_dispatches
            WHERE status = 'failed'
              AND grab_attempted_utc < @cutoff
              AND status != 'archived'
            ORDER BY grab_attempted_utc ASC
            LIMIT @limit
            """;

        AddParameter(command, "@cutoff", cutoff.ToString("O"));
        AddParameter(command, "@limit", limit);

        var results = new List<DownloadDispatchItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadDispatch(reader));
        }

        return results;
    }

    public async Task<DownloadDispatchItem?> FindDispatchByHashAsync(
        string clientId,
        string hash,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs, cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT
                {DispatchColumns}
            FROM download_dispatches
            WHERE download_client_id = @clientId
              AND torrent_hash_or_item_id = @hash COLLATE NOCASE
              AND status != 'archived'
            ORDER BY created_utc DESC
            LIMIT 1
            """;

        AddParameter(command, "@clientId", clientId);
        AddParameter(command, "@hash", hash);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadDispatch(reader) : null;
    }

    public async Task<IReadOnlyList<DownloadDispatchItem>> FindOpenDispatchesByHashAsync(
        string clientId,
        string hash,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs, cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT
                {DispatchColumns}
            FROM download_dispatches
            WHERE download_client_id = @clientId
              AND torrent_hash_or_item_id = @hash COLLATE NOCASE
              AND COALESCE(status, '') <> 'archived'
              AND archived_utc IS NULL
            ORDER BY created_utc DESC
            """;

        AddParameter(command, "@clientId", clientId);
        AddParameter(command, "@hash", hash);

        var results = new List<DownloadDispatchItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadDispatch(reader));
        }

        return results;
    }

    public async Task<DownloadDispatchItem?> FindAnyDispatchByHashAsync(
        string clientId,
        string hash,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs, cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT
                {DispatchColumns}
            FROM download_dispatches
            WHERE download_client_id = @clientId
              AND torrent_hash_or_item_id = @hash COLLATE NOCASE
            ORDER BY created_utc DESC
            LIMIT 1
            """;

        AddParameter(command, "@clientId", clientId);
        AddParameter(command, "@hash", hash);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadDispatch(reader) : null;
    }

    public async Task ReleaseDispatchesByHashAsync(
        string clientId,
        string hash,
        string message,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs, cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE download_dispatches
            SET import_failure_code = 'health-remediation-applied',
                import_failure_message = @message
            WHERE download_client_id = @clientId
              AND torrent_hash_or_item_id = @hash COLLATE NOCASE
            """;
        AddParameter(command, "@clientId", clientId);
        AddParameter(command, "@hash", hash);
        AddParameter(command, "@message", message);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> FindGrabbedReleaseNamesAsync(
        IReadOnlyCollection<string> candidateNames,
        CancellationToken cancellationToken)
    {
        var names = candidateNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (names.Length == 0)
        {
            return [];
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs, cancellationToken);

        var found = new List<string>();
        // SQLite caps bound parameters per statement, so ask in batches.
        foreach (var batch in names.Chunk(500))
        {
            using var command = connection.CreateCommand();
            var placeholders = string.Join(", ", batch.Select((_, index) => $"@n{index}"));
            command.CommandText =
                $"SELECT DISTINCT release_name FROM download_dispatches WHERE release_name COLLATE NOCASE IN ({placeholders})";
            for (var index = 0; index < batch.Length; index++)
            {
                AddParameter(command, $"@n{index}", batch[index]);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                found.Add(reader.GetString(0));
            }
        }

        return found;
    }

    public async Task<IReadOnlyList<DownloadDispatchItem>> FindDispatchesByClientItemIdsAsync(
        string clientId,
        IReadOnlyCollection<string> clientItemIds,
        CancellationToken cancellationToken)
    {
        var ids = clientItemIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        var found = new Dictionary<string, DownloadDispatchItem>(StringComparer.Ordinal);
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs, cancellationToken);

        // SQLite caps the number of bound variables, so ask in batches.
        foreach (var batch in ids.Chunk(400))
        {
            using var command = connection.CreateCommand();
            var names = new List<string>(batch.Length);
            for (var index = 0; index < batch.Length; index++)
            {
                names.Add($"@id{index}");
                AddParameter(command, $"@id{index}", batch[index]);
            }

            var list = string.Join(", ", names);
            // The ids a client reports are a dispatch's stored hash/item id
            // (index on client + id ignoring case) or, for a client that files
            // an item under Deluno's own dispatch id, the primary key, which is
            // always written lower-case, so it is compared exactly.
            command.CommandText =
                $"""
                SELECT {DispatchColumns}
                FROM download_dispatches
                WHERE download_client_id = @clientId
                  AND torrent_hash_or_item_id COLLATE NOCASE IN ({list})
                UNION
                SELECT {DispatchColumns}
                FROM download_dispatches
                WHERE download_client_id = @clientId
                  AND id IN ({list.Replace("@id", "@lower")})
                """;
            AddParameter(command, "@clientId", clientId);
            for (var index = 0; index < batch.Length; index++)
            {
                AddParameter(command, $"@lower{index}", batch[index].ToLowerInvariant());
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var dispatch = ReadDispatch(reader);
                found[dispatch.Id] = dispatch;
            }
        }

        return found.Values
            .OrderByDescending(item => item.GrabAttemptedUtc ?? item.CreatedUtc)
            .ToArray();
    }

    public async Task<string> ReserveGrabAsync(
        string clientId,
        string clientItemId,
        string releaseName,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var reservationId = Guid.CreateVersion7().ToString("N");
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs, cancellationToken);

        // A reservation only has to outlive the moment between the send and the
        // dispatch row being written; older ones are swept as new ones arrive.
        using (var sweep = connection.CreateCommand())
        {
            sweep.CommandText = "DELETE FROM download_grab_reservations WHERE created_utc < @cutoff";
            AddParameter(sweep, "@cutoff", now.AddMinutes(-15).ToString("O"));
            await sweep.ExecuteNonQueryAsync(cancellationToken);
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO download_grab_reservations (id, download_client_id, client_item_id, release_name, created_utc)
            VALUES (@id, @clientId, @itemId, @releaseName, @now)
            """;
        AddParameter(command, "@id", reservationId);
        AddParameter(command, "@clientId", clientId);
        AddParameter(command, "@itemId", clientItemId.Trim().ToLowerInvariant());
        AddParameter(command, "@releaseName", releaseName);
        AddParameter(command, "@now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return reservationId;
    }

    public async Task ReleaseGrabReservationAsync(string reservationId, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM download_grab_reservations WHERE id = @id";
        AddParameter(command, "@id", reservationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> HasOtherGrabReservationAsync(
        string clientId,
        string clientItemId,
        string? exceptReservationId,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT 1 FROM download_grab_reservations
            WHERE download_client_id = @clientId
              AND client_item_id = @itemId
              AND id != @except
            LIMIT 1
            """;
        AddParameter(command, "@clientId", clientId);
        AddParameter(command, "@itemId", clientItemId.Trim().ToLowerInvariant());
        AddParameter(command, "@except", exceptReservationId ?? string.Empty);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<bool> ReplaceClientItemIdAsync(
        string dispatchId,
        string oldId,
        string newId,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs, cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE download_dispatches
            SET torrent_hash_or_item_id = @newId
            WHERE id = @dispatchId
              AND torrent_hash_or_item_id = @oldId
            """;
        AddParameter(command, "@dispatchId", dispatchId);
        AddParameter(command, "@oldId", oldId);
        AddParameter(command, "@newId", newId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static DownloadDispatchItem ReadDispatch(DbDataReader reader)
    {
        var dispatch = new DownloadDispatchItem(
            Id: reader.GetString(0),
            LibraryId: reader.GetString(1),
            MediaType: reader.GetString(2),
            EntityType: reader.GetString(3),
            EntityId: reader.GetString(4),
            ReleaseName: reader.GetString(5),
            IndexerName: reader.GetString(6),
            DownloadClientId: reader.GetString(7),
            DownloadClientName: reader.GetString(8),
            Status: reader.GetString(9),
            NotesJson: reader.IsDBNull(10) ? null : reader.GetString(10),
            CreatedUtc: DateTimeOffset.Parse(reader.GetString(11)),
            GrabStatus: reader.IsDBNull(12) ? null : reader.GetString(12),
            GrabAttemptedUtc: reader.IsDBNull(13) ? null : DateTimeOffset.Parse(reader.GetString(13)),
            GrabResponseCode: reader.IsDBNull(14) ? null : reader.GetInt32(14),
            GrabMessage: reader.IsDBNull(15) ? null : reader.GetString(15),
            GrabFailureCode: reader.IsDBNull(16) ? null : reader.GetString(16),
            GrabResponseJson: reader.IsDBNull(17) ? null : reader.GetString(17),
            DetectedUtc: reader.IsDBNull(19) ? null : DateTimeOffset.Parse(reader.GetString(19)),
            TorrentHashOrItemId: reader.IsDBNull(20) ? null : reader.GetString(20),
            DownloadedBytes: reader.IsDBNull(21) ? null : reader.GetInt64(21),
            ImportStatus: reader.IsDBNull(22) ? null : reader.GetString(22),
            ImportDetectedUtc: reader.IsDBNull(23) ? null : DateTimeOffset.Parse(reader.GetString(23)),
            ImportCompletedUtc: reader.IsDBNull(24) ? null : DateTimeOffset.Parse(reader.GetString(24)),
            ImportedFilePath: reader.IsDBNull(25) ? null : reader.GetString(25),
            ImportFailureCode: reader.IsDBNull(26) ? null : reader.GetString(26),
            ImportFailureMessage: reader.IsDBNull(27) ? null : reader.GetString(27),
            CircuitOpenUntilUtc: reader.IsDBNull(29) ? null : DateTimeOffset.Parse(reader.GetString(29)),
            NextRetryEligibleUtc: reader.IsDBNull(30) ? null : DateTimeOffset.Parse(reader.GetString(30)),
            AttemptCount: reader.IsDBNull(31) ? null : reader.GetInt32(31),
            ClientCompletedUtc: reader.IsDBNull(32) ? null : DateTimeOffset.Parse(reader.GetString(32)),
            DecisionQuality: reader.IsDBNull(33) ? null : reader.GetString(33),
            DecisionCustomFormatScore: reader.IsDBNull(34) ? null : reader.GetInt32(34),
            DecisionLanguage: reader.IsDBNull(35) ? null : reader.GetString(35),
            IndexerId: reader.IsDBNull(36) ? null : reader.GetString(36),
            StartedBy: reader.IsDBNull(37) ? null : reader.GetString(37),
            StartedByDetail: reader.IsDBNull(38) ? null : reader.GetString(38),
            ImportedFileBytes: reader.IsDBNull(39) ? null : reader.GetInt64(39));

        return dispatch with
        {
            Failure = ReadFailure(
                dispatch,
                reader.IsDBNull(18) ? null : DeserializeFailure(reader.GetString(18)),
                reader.IsDBNull(28) ? null : DeserializeFailure(reader.GetString(28)))
        };
    }

    private static IntegrationFailure? ReadFailure(
        DownloadDispatchItem dispatch,
        IntegrationFailure? persistedGrabFailure,
        IntegrationFailure? persistedImportFailure)
    {
        // Import is the terminal outcome for a dispatch. A grab response may
        // legitimately contain an earlier failed attempt (or a provider
        // payload that happens to include a failure-shaped object), but once
        // the import stage has failed that is the failure the owner needs to
        // act on. Do not let stale grab JSON mask it.
        if (persistedImportFailure is not null)
        {
            return persistedImportFailure;
        }

        if (persistedGrabFailure is not null)
        {
            return persistedGrabFailure;
        }

        if (!string.IsNullOrWhiteSpace(dispatch.ImportFailureCode)
            || !string.IsNullOrWhiteSpace(dispatch.ImportFailureMessage)
            || string.Equals(dispatch.ImportStatus, "failed", StringComparison.OrdinalIgnoreCase))
        {
            return IntegrationFailureFactory.FromLegacy(
                "deluno",
                dispatch.LibraryId,
                "Deluno import",
                "import",
                "rejected",
                dispatch.ImportFailureMessage ?? "The downloaded file could not be imported.",
                code: dispatch.ImportFailureCode,
                externalId: dispatch.Id);
        }

        foreach (var json in new[] { dispatch.GrabResponseJson, dispatch.NotesJson })
        {
            if (TryReadTypedFailure(json) is { } typedFailure)
            {
                return typedFailure;
            }
        }

        // next_retry_eligible_utc is not a retry time. Nothing retries a failed
        // grab on a clock, and the only writer left stamped the moment somebody
        // pressed Retry, which read back as "Retry scheduled" at a time already
        // gone (#621). The title comes back on the library's own schedule.
        if (IsFailedGrab(dispatch))
        {
            return IntegrationFailureFactory.FromLegacy(
                "download-client",
                dispatch.DownloadClientId,
                dispatch.DownloadClientName,
                "grab",
                dispatch.GrabFailureCode ?? dispatch.GrabStatus,
                dispatch.GrabMessage ?? "The download client did not accept the release.",
                dispatch.GrabResponseCode,
                code: dispatch.GrabFailureCode);
        }

        return null;
    }

    private static bool IsFailedGrab(DownloadDispatchItem dispatch)
        => !string.IsNullOrWhiteSpace(dispatch.GrabFailureCode)
            || dispatch.GrabStatus?.Trim().ToLowerInvariant() is "failed" or "not_found" or "circuit_open" or "paused";

    private static IntegrationFailure? TryReadTypedFailure(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (LooksLikeFailure(root) && TryDeserializeFailure(root) is { } direct)
            {
                return direct;
            }

            if (TryGetPropertyIgnoreCase(root, "failure", out var failure))
            {
                var nested = TryDeserializeFailure(failure);
                if (nested is not null)
                {
                    return nested;
                }
            }

            if (TryGetPropertyIgnoreCase(root, "grabResult", out var grabResult)
                && TryGetPropertyIgnoreCase(grabResult, "failure", out var grabFailure))
            {
                var grab = TryDeserializeFailure(grabFailure);
                if (grab is not null)
                {
                    return grab;
                }
            }
        }
        catch (JsonException)
        {
            // Legacy response bodies are allowed to be arbitrary provider
            // payloads. The column fallback below still gives them a typed
            // product failure.
        }

        return null;
    }

    private static bool LooksLikeFailure(JsonElement element)
        => element.ValueKind == JsonValueKind.Object
            && TryGetPropertyIgnoreCase(element, "serviceType", out _)
            && TryGetPropertyIgnoreCase(element, "operation", out _)
            && TryGetPropertyIgnoreCase(element, "message", out _);

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static IntegrationFailure? TryDeserializeFailure(JsonElement element)
    {
        try
        {
            return element.ValueKind == JsonValueKind.Object
                ? JsonSerializer.Deserialize<IntegrationFailure>(element.GetRawText())
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IntegrationFailure? DeserializeFailure(string json)
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

    public async Task<bool> MarkNeverDetectedAsync(string dispatchId, string message, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            // grab_status stays 'succeeded': the client did accept it, and the
            // indexer scoreboard reads that column.
            command.CommandText =
                """
                UPDATE download_dispatches
                SET status = 'failed', grab_failure_code = 'not_found', grab_message = @message
                WHERE id = @id
                  AND detected_utc IS NULL
                  AND COALESCE(status, '') NOT IN ('failed', 'archived');
                """;
            AddParameter(command, "@id", dispatchId);
            AddParameter(command, "@message", message);
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return false;
            }
        }

        await RecordTimelineEventInternalAsync(
            connection,
            transaction,
            dispatchId,
            "never_detected",
            JsonSerializer.Serialize(new { message }),
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        await AnnounceStageAsync(dispatchId, "never_detected", message, cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<DownloadDispatchItem>> FindOldUnresolvedDispatchesAsync(
        TimeSpan minAge,
        int limit,
        CancellationToken cancellationToken)
    {
        var cutoff = timeProvider.GetUtcNow().Subtract(minAge);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT
                {DispatchColumns}
            FROM download_dispatches
            WHERE status != 'archived'
              AND created_utc < @cutoff
              AND (grab_status = 'succeeded' AND detected_utc IS NULL
                   OR detected_utc IS NOT NULL AND import_status IS NULL)
            ORDER BY created_utc ASC
            LIMIT @limit
            """;

        AddParameter(command, "@cutoff", cutoff.ToString("O"));
        AddParameter(command, "@limit", limit);

        var results = new List<DownloadDispatchItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadDispatch(reader));
        }

        return results;
    }

    private static DispatchTimelineEvent ReadTimelineEvent(DbDataReader reader)
    {
        return new DispatchTimelineEvent(
            Id: reader.GetString(0),
            DispatchId: reader.GetString(1),
            EventType: reader.GetString(2),
            Timestamp: DateTimeOffset.Parse(reader.GetString(3)),
            DetailsJson: reader.IsDBNull(4) ? null : reader.GetString(4),
            CreatedUtc: DateTimeOffset.Parse(reader.GetString(5)));
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
