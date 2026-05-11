using System.Data.Common;
using System.Text.Json;
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
                grab_message, grab_failure_code, grab_response_json, detected_utc,
                torrent_hash_or_item_id, downloaded_bytes, import_status, import_detected_utc,
                import_completed_utc, imported_file_path, import_failure_code,
                import_failure_message, circuit_open_until_utc, next_retry_eligible_utc, attempt_count
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
                    grab_status = @grabStatus,
                    grab_attempted_utc = @grabAttemptedUtc,
                    grab_response_code = @grabResponseCode,
                    grab_message = @grabMessage,
                    grab_failure_code = @grabFailureCode,
                    grab_response_json = @grabResponseJson
                WHERE id = @dispatchId
                """;

            AddParameter(command, "@dispatchId", dispatchId);
            AddParameter(command, "@grabStatus", grabStatus);
            AddParameter(command, "@grabAttemptedUtc", now.ToString("O"));
            AddParameter(command, "@grabResponseCode", grabResponseCode);
            AddParameter(command, "@grabMessage", grabMessage);
            AddParameter(command, "@grabFailureCode", grabFailureCode);
            AddParameter(command, "@grabResponseJson", grabResponseJson);

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
                grabFailureCode
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

    public async Task<DownloadDispatchItem> RecordImportOutcomeAsync(
        string dispatchId,
        string importStatus,
        string? importedFilePath,
        string? importFailureCode,
        string? importFailureMessage,
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
                    import_failure_message = @importFailureMessage
                WHERE id = @dispatchId
                """;

            AddParameter(command, "@dispatchId", dispatchId);
            AddParameter(command, "@importStatus", importStatus);
            AddParameter(command, "@importDetectedUtc", now.ToString("O"));
            AddParameter(command, "@importCompletedUtc", now.ToString("O"));
            AddParameter(command, "@importedFilePath", importedFilePath);
            AddParameter(command, "@importFailureCode", importFailureCode);
            AddParameter(command, "@importFailureMessage", importFailureMessage);

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
                importFailureMessage
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
            parameters["importStatus"] = filter.ImportStatus;
        }

        if (!string.IsNullOrEmpty(filter.ClientId))
        {
            whereConditions.Add("download_client_id = @clientId");
            parameters["clientId"] = filter.ClientId;
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

        var pageSize = Math.Max(10, Math.Min(pagination.PageSize, 100));
        var offset = 0;

        // Simple cursor implementation: decode offset from page token
        if (!string.IsNullOrEmpty(pagination.PageToken) &&
            int.TryParse(pagination.PageToken, out var decodedOffset))
        {
            offset = decodedOffset;
        }

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
                grab_message, grab_failure_code, grab_response_json, detected_utc,
                torrent_hash_or_item_id, downloaded_bytes, import_status, import_detected_utc,
                import_completed_utc, imported_file_path, import_failure_code,
                import_failure_message, circuit_open_until_utc, next_retry_eligible_utc, attempt_count
            FROM download_dispatches
            WHERE {{whereClause}}
            ORDER BY grab_attempted_utc DESC, created_utc DESC
            LIMIT @limit
            OFFSET @offset
            """;

        foreach (var param in parameters)
        {
            AddParameter(command, $"@{param.Key}", param.Value);
        }

        AddParameter(command, "@limit", fetchCount);
        AddParameter(command, "@offset", offset);

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
            nextPageToken = (offset + pageSize).ToString();
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
                grab_message, grab_failure_code, grab_response_json, detected_utc,
                torrent_hash_or_item_id, downloaded_bytes, import_status, import_detected_utc,
                import_completed_utc, imported_file_path, import_failure_code,
                import_failure_message, circuit_open_until_utc, next_retry_eligible_utc, attempt_count
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
                SET status = 'archived'
                WHERE id = @dispatchId
                """;

            AddParameter(command, "@dispatchId", dispatchId);
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
                grab_message, grab_failure_code, grab_response_json, detected_utc,
                torrent_hash_or_item_id, downloaded_bytes, import_status, import_detected_utc,
                import_completed_utc, imported_file_path, import_failure_code,
                import_failure_message, circuit_open_until_utc, next_retry_eligible_utc, attempt_count
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
                grab_message, grab_failure_code, grab_response_json, detected_utc,
                torrent_hash_or_item_id, downloaded_bytes, import_status, import_detected_utc,
                import_completed_utc, imported_file_path, import_failure_code,
                import_failure_message, circuit_open_until_utc, next_retry_eligible_utc, attempt_count
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
                grab_message, grab_failure_code, grab_response_json, detected_utc,
                torrent_hash_or_item_id, downloaded_bytes, import_status, import_detected_utc,
                import_completed_utc, imported_file_path, import_failure_code,
                import_failure_message, circuit_open_until_utc, next_retry_eligible_utc, attempt_count
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
        return new DownloadDispatchItem(
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
            DetectedUtc: reader.IsDBNull(18) ? null : DateTimeOffset.Parse(reader.GetString(18)),
            TorrentHashOrItemId: reader.IsDBNull(19) ? null : reader.GetString(19),
            DownloadedBytes: reader.IsDBNull(20) ? null : reader.GetInt64(20),
            ImportStatus: reader.IsDBNull(21) ? null : reader.GetString(21),
            ImportDetectedUtc: reader.IsDBNull(22) ? null : DateTimeOffset.Parse(reader.GetString(22)),
            ImportCompletedUtc: reader.IsDBNull(23) ? null : DateTimeOffset.Parse(reader.GetString(23)),
            ImportedFilePath: reader.IsDBNull(24) ? null : reader.GetString(24),
            ImportFailureCode: reader.IsDBNull(25) ? null : reader.GetString(25),
            ImportFailureMessage: reader.IsDBNull(26) ? null : reader.GetString(26),
            CircuitOpenUntilUtc: reader.IsDBNull(27) ? null : DateTimeOffset.Parse(reader.GetString(27)),
            NextRetryEligibleUtc: reader.IsDBNull(28) ? null : DateTimeOffset.Parse(reader.GetString(28)),
            AttemptCount: reader.IsDBNull(29) ? null : reader.GetInt32(29));
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
                grab_message, grab_failure_code, grab_response_json, detected_utc,
                torrent_hash_or_item_id, downloaded_bytes, import_status, import_detected_utc,
                import_completed_utc, imported_file_path, import_failure_code,
                import_failure_message, circuit_open_until_utc, next_retry_eligible_utc, attempt_count
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
                grab_message, grab_failure_code, grab_response_json, detected_utc,
                torrent_hash_or_item_id, downloaded_bytes, import_status, import_detected_utc,
                import_completed_utc, imported_file_path, import_failure_code,
                import_failure_message, circuit_open_until_utc, next_retry_eligible_utc, attempt_count
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
