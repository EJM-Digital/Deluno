using System.Data.Common;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Contracts;

namespace Deluno.Jobs.Data;

public sealed class SqliteDownloadDispatchesRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider)
    : IDownloadDispatchesRepository, IDownloadDispatchRepository
{
    public async Task<DownloadDispatchItem?> GetDispatchAsync(
        string dispatchId,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, library_id, media_type, entity_type, entity_id, release_name,
                indexer_name, download_client_id, download_client_name, status, notes_json,
                created_utc, grab_status, grab_attempted_utc, grab_response_code,
                grab_message, grab_failure_code, grab_response_json, grab_failure_json, detected_utc,
                torrent_hash_or_item_id, downloaded_bytes, import_status, import_detected_utc,
                import_completed_utc, imported_file_path, import_failure_code,
                import_failure_message, import_failure_json, circuit_open_until_utc, next_retry_eligible_utc, attempt_count
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

        var result = await GetDispatchAsync(dispatchId, cancellationToken);
        return result ?? throw new InvalidOperationException($"Dispatch {dispatchId} not found after record");
    }

    public async Task<DownloadDispatchItem> RecordDetectionAsync(
        string dispatchId,
        string? torrentHashOrItemId,
        long? downloadedBytes,
        CancellationToken cancellationToken)
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
                    detected_utc = @detectedUtc,
                    torrent_hash_or_item_id = @torrentHashOrItemId,
                    downloaded_bytes = @downloadedBytes
                WHERE id = @dispatchId
                """;

            AddParameter(command, "@dispatchId", dispatchId);
            AddParameter(command, "@detectedUtc", now.ToString("O"));
            AddParameter(command, "@torrentHashOrItemId", torrentHashOrItemId);
            AddParameter(command, "@downloadedBytes", downloadedBytes);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await RecordTimelineEventInternalAsync(
            connection,
            transaction,
            dispatchId,
            "detection_succeeded",
            JsonSerializer.Serialize(new
            {
                torrentHashOrItemId,
                downloadedBytes
            }),
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

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
        IntegrationFailure? failure = null)
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

        var result = await GetDispatchAsync(dispatchId, cancellationToken);
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

        var whereConditions = new List<string> { "status != 'archived'" };
        var parameters = new Dictionary<string, object?>();

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

        var whereClause = string.Join(" AND ", whereConditions);

        // Fetch one extra to determine if there's a next page
        var fetchCount = pageSize + 1;

        using var command = connection.CreateCommand();
        command.CommandText =
            $$"""
            SELECT
                id, library_id, media_type, entity_type, entity_id, release_name,
                indexer_name, download_client_id, download_client_name, status, notes_json,
                created_utc, grab_status, grab_attempted_utc, grab_response_code,
                grab_message, grab_failure_code, grab_response_json, grab_failure_json, detected_utc,
                torrent_hash_or_item_id, downloaded_bytes, import_status, import_detected_utc,
                import_completed_utc, imported_file_path, import_failure_code,
                import_failure_message, import_failure_json, circuit_open_until_utc, next_retry_eligible_utc, attempt_count
            FROM download_dispatches
            WHERE {{whereClause}}
              AND (@sortUtc IS NULL
                   OR COALESCE(grab_attempted_utc, created_utc) < @sortUtc
                   OR (COALESCE(grab_attempted_utc, created_utc) = @sortUtc AND id < @id))
            ORDER BY COALESCE(grab_attempted_utc, created_utc) DESC, id DESC
            LIMIT @limit
            """;

        foreach (var param in parameters)
        {
            AddParameter(command, $"@{param.Key}", param.Value);
        }

        AddParameter(command, "@limit", fetchCount);
        AddParameter(command, "@sortUtc", token?[0]);
        AddParameter(command, "@id", token?[1]);

        var items = new List<DownloadDispatchItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadDispatch(reader));
        }

        string? nextPageToken = null;
        if (items.Count > pageSize)
        {
            items.RemoveAt(pageSize);
            var last = items[^1];
            nextPageToken = DelunoPageToken.Encode(
                (last.GrabAttemptedUtc ?? last.CreatedUtc).ToString("O"),
                last.Id);
        }

        return (items, nextPageToken);
    }

    public async Task<IReadOnlyList<DownloadDispatchItem>> FindUnresolvedDispatchesAsync(
        int minAgeMinutes,
        string? clientId,
        int limit,
        CancellationToken cancellationToken)
    {
        var cutoffTime = timeProvider.GetUtcNow().AddMinutes(-minAgeMinutes);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, library_id, media_type, entity_type, entity_id, release_name,
                indexer_name, download_client_id, download_client_name, status, notes_json,
                created_utc, grab_status, grab_attempted_utc, grab_response_code,
                grab_message, grab_failure_code, grab_response_json, grab_failure_json, detected_utc,
                torrent_hash_or_item_id, downloaded_bytes, import_status, import_detected_utc,
                import_completed_utc, imported_file_path, import_failure_code,
                import_failure_message, import_failure_json, circuit_open_until_utc, next_retry_eligible_utc, attempt_count
            FROM download_dispatches
            WHERE
                grab_status = 'succeeded'
                AND detected_utc IS NULL
                AND grab_attempted_utc <= @cutoffTime
            """;

        if (!string.IsNullOrEmpty(clientId))
        {
            command.CommandText += " AND download_client_id = @clientId";
            AddParameter(command, "@clientId", clientId);
        }

        command.CommandText += " ORDER BY grab_attempted_utc DESC LIMIT @limit";

        AddParameter(command, "@cutoffTime", cutoffTime.ToString("O"));
        AddParameter(command, "@limit", limit);

        var items = new List<DownloadDispatchItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadDispatch(reader));
        }

        return items;
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
            """
            SELECT
                id, library_id, media_type, entity_type, entity_id, release_name,
                indexer_name, download_client_id, download_client_name, status, notes_json,
                created_utc, grab_status, grab_attempted_utc, grab_response_code,
                grab_message, grab_failure_code, grab_response_json, grab_failure_json, detected_utc,
                torrent_hash_or_item_id, downloaded_bytes, import_status, import_detected_utc,
                import_completed_utc, imported_file_path, import_failure_code,
                import_failure_message, import_failure_json, circuit_open_until_utc, next_retry_eligible_utc, attempt_count
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
            """
            SELECT
                id, library_id, media_type, entity_type, entity_id, release_name,
                indexer_name, download_client_id, download_client_name, status, notes_json,
                created_utc, grab_status, grab_attempted_utc, grab_response_code,
                grab_message, grab_failure_code, grab_response_json, grab_failure_json, detected_utc,
                torrent_hash_or_item_id, downloaded_bytes, import_status, import_detected_utc,
                import_completed_utc, imported_file_path, import_failure_code,
                import_failure_message, import_failure_json, circuit_open_until_utc, next_retry_eligible_utc, attempt_count
            FROM download_dispatches
            WHERE download_client_id = @clientId
              AND torrent_hash_or_item_id = @hash
              AND status != 'archived'
            ORDER BY created_utc DESC
            LIMIT 1
            """;

        AddParameter(command, "@clientId", clientId);
        AddParameter(command, "@hash", hash);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadDispatch(reader) : null;
    }

    public async Task<DownloadDispatchItem?> FindDispatchByReleaseNameAsync(
        string clientId,
        string releaseName,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs, cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, library_id, media_type, entity_type, entity_id, release_name,
                indexer_name, download_client_id, download_client_name, status, notes_json,
                created_utc, grab_status, grab_attempted_utc, grab_response_code,
                grab_message, grab_failure_code, grab_response_json, grab_failure_json, detected_utc,
                torrent_hash_or_item_id, downloaded_bytes, import_status, import_detected_utc,
                import_completed_utc, imported_file_path, import_failure_code,
                import_failure_message, import_failure_json, circuit_open_until_utc, next_retry_eligible_utc, attempt_count
            FROM download_dispatches
            WHERE download_client_id = @clientId
              AND release_name = @releaseName
              AND detected_utc IS NULL
              AND status != 'archived'
            ORDER BY created_utc DESC
            LIMIT 1
            """;

        AddParameter(command, "@clientId", clientId);
        AddParameter(command, "@releaseName", releaseName);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadDispatch(reader) : null;
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
            AttemptCount: reader.IsDBNull(31) ? null : reader.GetInt32(31));

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
                retryAfterUtc: dispatch.NextRetryEligibleUtc,
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

    public async Task<int> IncrementAttemptCountAsync(string dispatchId, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs, cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE download_dispatches
            SET attempt_count = attempt_count + 1
            WHERE id = @id;
            SELECT attempt_count FROM download_dispatches WHERE id = @id;
            """;
        AddParameter(command, "@id", dispatchId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long count ? (int)count : 0;
    }

    public async Task MarkDispatchFailedAsync(string dispatchId, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs, cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE download_dispatches
            SET status = 'failed', grab_status = 'failed', grab_failure_code = 'max-retries-exceeded'
            WHERE id = @id;
            """;
        AddParameter(command, "@id", dispatchId);
        await command.ExecuteNonQueryAsync(cancellationToken);
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
            """
            SELECT
                id, library_id, media_type, entity_type, entity_id, release_name,
                indexer_name, download_client_id, download_client_name, status, notes_json,
                created_utc, grab_status, grab_attempted_utc, grab_response_code,
                grab_message, grab_failure_code, grab_response_json, grab_failure_json, detected_utc,
                torrent_hash_or_item_id, downloaded_bytes, import_status, import_detected_utc,
                import_completed_utc, imported_file_path, import_failure_code,
                import_failure_message, import_failure_json, circuit_open_until_utc, next_retry_eligible_utc, attempt_count
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

    public async Task<DownloadDispatchItem> UpdateFailureRetryWindowAsync(
        string dispatchId,
        DateTimeOffset nextRetryEligibleUtc,
        int retryCount,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE download_dispatches
            SET
                next_retry_eligible_utc = @nextRetryEligibleUtc,
                attempt_count = @attemptCount
            WHERE id = @dispatchId
            """;

        AddParameter(command, "@dispatchId", dispatchId);
        AddParameter(command, "@nextRetryEligibleUtc", nextRetryEligibleUtc.ToString("O"));
        AddParameter(command, "@attemptCount", retryCount);

        await command.ExecuteNonQueryAsync(cancellationToken);

        var result = await GetDispatchAsync(dispatchId, cancellationToken);
        return result ?? throw new InvalidOperationException($"Dispatch {dispatchId} not found after retry window update");
    }

    public async Task<IReadOnlyList<DownloadDispatchItem>> FindDispatchesEligibleForRetryAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, library_id, media_type, entity_type, entity_id, release_name,
                indexer_name, download_client_id, download_client_name, status, notes_json,
                created_utc, grab_status, grab_attempted_utc, grab_response_code,
                grab_message, grab_failure_code, grab_response_json, grab_failure_json, detected_utc,
                torrent_hash_or_item_id, downloaded_bytes, import_status, import_detected_utc,
                import_completed_utc, imported_file_path, import_failure_code,
                import_failure_message, import_failure_json, circuit_open_until_utc, next_retry_eligible_utc, attempt_count
            FROM download_dispatches
            WHERE status != 'archived'
              AND next_retry_eligible_utc IS NOT NULL
              AND next_retry_eligible_utc <= @now
            ORDER BY next_retry_eligible_utc ASC
            LIMIT @limit
            """;

        AddParameter(command, "@now", now.ToString("O"));
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
