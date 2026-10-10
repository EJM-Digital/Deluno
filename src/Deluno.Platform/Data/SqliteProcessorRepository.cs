using System.Security.Cryptography;
using System.Text;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Platform.Contracts;
using Deluno.Security;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;
using static Deluno.Contracts.DelunoValueNormalizers;

namespace Deluno.Platform.Data;

public sealed class SqliteProcessorRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider,
    ISecretProtector secretProtector,
    IRealtimeEventPublisher? realtimeEventPublisher = null,
    IConnectionUseReporter? connectionUses = null,
    IHealthCheckRepository? healthChecks = null,
    IEnumerable<IHandoffClosedListener>? handoffClosedListeners = null)
    : IProcessorRepository
{
    public async Task<IReadOnlyList<ProcessorConnectionItem>> ListProcessorConnectionsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, name, provider, submission_url, auth_header_name, secret_value, is_enabled, health_status, last_health_message, last_health_test_utc, created_utc, updated_utc, health_check_url FROM processor_connections ORDER BY name COLLATE NOCASE;";
        var items = new List<ProcessorConnectionItem>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadProcessorConnection(reader));
        }

        return items;
    }

    public async Task<ProcessorConnectionItem?> GetProcessorConnectionAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        return await GetProcessorConnectionAsync(connection, id, cancellationToken);
    }

    public async Task<ProcessorConnectionItem?> FindProcessorConnectionByNameAsync(string? name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, name, provider, submission_url, auth_header_name, secret_value, is_enabled, health_status, last_health_message, last_health_test_utc, created_utc, updated_utc, health_check_url FROM processor_connections WHERE name = @name COLLATE NOCASE LIMIT 1;";
        AddParameter(command, "@name", name.Trim());
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProcessorConnection(reader) : null;
    }

    public async Task<ProcessorConnectionItem?> FindProcessorConnectionAsync(string? id, string? nameFallback, CancellationToken cancellationToken)
    {
        // The id is the link (#1077). The name is only for a row that could not
        // be matched to a connection when it was written.
        if (!string.IsNullOrWhiteSpace(id))
        {
            var byId = await GetProcessorConnectionAsync(id, cancellationToken);
            if (byId is not null)
            {
                return byId;
            }
        }

        return await FindProcessorConnectionByNameAsync(nameFallback, cancellationToken);
    }

    /// <summary>
    /// #1076: "FileFlows on RIG". The machine name the processor publishes wins over the host
    /// of its address; either way this machine's own address is written as its hostname.
    /// </summary>
    private static async Task<string> ProcessorNameAsync(
        System.Data.Common.DbConnection connection,
        string id,
        string provider,
        string submissionUrl,
        string? machineName,
        string? productName,
        CancellationToken cancellationToken)
    {
        // #1144: the processor's own name when it (or the setup that added it) gave one.
        var app = !string.IsNullOrWhiteSpace(productName) ? productName.Trim() : provider == "fileflows-webhook" ? "FileFlows" : "Processor";
        var named = string.IsNullOrWhiteSpace(machineName)
            ? ConnectionNames.AppOnHost(app, submissionUrl)
            : $"{app} on {(ConnectionNames.IsLoopback(machineName.Trim()) ? DelunoHostName.Current : machineName.Trim())}";
        return ConnectionNames.Unique(
            named,
            ConnectionNames.PortOf(submissionUrl),
            id,
            await ConnectionNameRows.OtherNamesAsync(connection, "processor_connections", id, cancellationToken));
    }

    /// <summary>The product part of a name this repository wrote ("Weir on RIG" gives "Weir"); null for the generic "Processor on RIG" and "FileFlows on RIG", which the provider already gives, and for a name someone chose.</summary>
    private static string? ProductOf(string name)
    {
        var at = name.IndexOf(" on ", StringComparison.Ordinal);
        var product = at > 0 ? name[..at] : null;
        return product is null or "Processor" or "FileFlows" ? null : product;
    }

    public async Task<ProcessorConnectionItem> CreateProcessorConnectionAsync(
        CreateProcessorConnectionRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var item = new ProcessorConnectionItem(
            Guid.CreateVersion7().ToString("N"),
            string.Empty,
            NormalizeProcessorConnectionProvider(request.Provider),
            NormalizeProcessorConnectionUrl(request.SubmissionUrl) ?? string.Empty,
            NormalizeProcessorAuthHeaderName(request.AuthHeaderName),
            string.IsNullOrWhiteSpace(request.Secret) ? null : request.Secret.Trim(),
            request.IsEnabled,
            "unknown",
            null,
            null,
            now,
            now,
            NormalizeProcessorConnectionUrl(request.HealthCheckUrl));

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        item = item with
        {
            Name = await ProcessorNameAsync(connection, item.Id, item.Provider, item.SubmissionUrl, request.MachineName, request.ProductName, cancellationToken)
        };
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO processor_connections (
                id, name, provider, submission_url, auth_header_name, secret_value, is_enabled,
                health_status, last_health_message, last_health_test_utc, created_utc, updated_utc, health_check_url
            ) VALUES (
                @id, @name, @provider, @submissionUrl, @authHeaderName, @secretValue, @isEnabled,
                @healthStatus, NULL, NULL, @createdUtc, @updatedUtc, @healthCheckUrl
            );
            """;
        AddParameter(command, "@id", item.Id);
        AddParameter(command, "@name", item.Name);
        AddParameter(command, "@provider", item.Provider);
        AddParameter(command, "@submissionUrl", item.SubmissionUrl);
        AddParameter(command, "@authHeaderName", item.AuthHeaderName);
        AddParameter(command, "@secretValue", item.Secret is null ? null : secretProtector.Protect($"processor-connection:{item.Id}", item.Secret));
        AddParameter(command, "@isEnabled", item.IsEnabled ? 1 : 0);
        AddParameter(command, "@healthStatus", item.HealthStatus);
        AddParameter(command, "@createdUtc", item.CreatedUtc.ToString("O"));
        AddParameter(command, "@updatedUtc", item.UpdatedUtc.ToString("O"));
        AddParameter(command, "@healthCheckUrl", item.HealthCheckUrl);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return item;
    }

    public async Task<ProcessorConnectionItem?> UpdateProcessorConnectionAsync(
        string id,
        UpdateProcessorConnectionRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        var existing = await GetProcessorConnectionAsync(connection, id, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var provider = NormalizeProcessorConnectionProvider(request.Provider ?? existing.Provider);
        var submissionUrl = NormalizeProcessorConnectionUrl(request.SubmissionUrl) ?? existing.SubmissionUrl;
        // The name follows the address, and stays put when nothing about the address changed and no new machine name came.
        var name = request.MachineName is null && provider == existing.Provider && string.Equals(submissionUrl, existing.SubmissionUrl, StringComparison.OrdinalIgnoreCase)
            ? existing.Name
            : await ProcessorNameAsync(connection, id.Trim(), provider, submissionUrl, request.MachineName, request.ProductName ?? ProductOf(existing.Name), cancellationToken);
        var authHeaderName = NormalizeProcessorAuthHeaderName(request.AuthHeaderName ?? existing.AuthHeaderName);
        var secret = string.IsNullOrWhiteSpace(request.Secret) ? existing.Secret : request.Secret.Trim();
        // Not sent keeps the address; sent empty clears it.
        var healthCheckUrl = request.HealthCheckUrl is null ? existing.HealthCheckUrl : NormalizeProcessorConnectionUrl(request.HealthCheckUrl);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE processor_connections
            SET name = @name,
                provider = @provider,
                submission_url = @submissionUrl,
                auth_header_name = @authHeaderName,
                secret_value = @secretValue,
                is_enabled = @isEnabled,
                health_check_url = @healthCheckUrl,
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;
        AddParameter(command, "@id", id.Trim());
        AddParameter(command, "@healthCheckUrl", healthCheckUrl);
        AddParameter(command, "@name", name);
        AddParameter(command, "@provider", provider);
        AddParameter(command, "@submissionUrl", submissionUrl);
        AddParameter(command, "@authHeaderName", authHeaderName);
        AddParameter(command, "@secretValue", secret is null ? null : secretProtector.Protect($"processor-connection:{id.Trim()}", secret));
        AddParameter(command, "@isEnabled", request.IsEnabled ? 1 : 0);
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return await GetProcessorConnectionAsync(connection, id, cancellationToken);
    }

    public async Task<bool> DeleteProcessorConnectionAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM processor_connections WHERE id = @id;";
        AddParameter(command, "@id", id.Trim());
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<ProcessorConnectionItem?> RecordProcessorConnectionHealthAsync(
        string id,
        string status,
        string? message,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        var before = await GetProcessorConnectionAsync(connection, id, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE processor_connections
            SET health_status = @status,
                last_health_message = @message,
                last_health_test_utc = @testedUtc,
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;
        var now = timeProvider.GetUtcNow();
        AddParameter(command, "@id", id.Trim());
        AddParameter(command, "@status", NormalizeProcessorConnectionHealth(status));
        AddParameter(command, "@message", NormalizeName(message));
        AddParameter(command, "@testedUtc", now.ToString("O"));
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            return null;
        }

        var after = await GetProcessorConnectionAsync(connection, id, cancellationToken);
        // A processor that starts or stops answering is pushed like any other health change, so the Dashboard, System
        // and the tray icon move together instead of the tray waiting for the next regular check (#1202).
        if (realtimeEventPublisher is not null && after is not null
            && !string.Equals(before?.HealthStatus, after.HealthStatus, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await realtimeEventPublisher.PublishHealthChangedAsync(
                    $"processor:{after.Id}", after.HealthStatus, after.LastHealthMessage ?? string.Empty, cancellationToken);
            }
            catch (Exception)
            {
                // The health is saved; the next push or reconnect catches the screens up.
            }
        }

        await AskForProcessorChecksAfterAnswerAsync(before, after, cancellationToken);
        return after;
    }

    /// <summary>
    /// A processor that answers again has its capabilities re-read at once (#1217): the watched-folder rows that said
    /// "did not answer Deluno's capabilities check" were only re-run on the fifteen-minute pass, so they outlived the
    /// outage by many minutes, through a Test and a hand-off. Any healthy answer (a check, a Test, a hand-off) now asks
    /// for the processor checks when the health just came back or a row for this processor is still not fine.
    /// </summary>
    private async Task AskForProcessorChecksAfterAnswerAsync(ProcessorConnectionItem? before, ProcessorConnectionItem? after, CancellationToken cancellationToken)
    {
        if (healthChecks is null || after is null
            || !string.Equals(after.HealthStatus, "healthy", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var cameBack = !string.Equals(before?.HealthStatus, "healthy", StringComparison.OrdinalIgnoreCase);
            var prefix = $"processor:{after.Id}";
            var stillShowing = cameBack || (await healthChecks.ListAsync(cancellationToken)).Any(row =>
                row.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(row.Status, HealthCheckStatuses.Ok, StringComparison.OrdinalIgnoreCase)
                // A row a check wrote moments ago is the answer to this very reading; only an older one is stale.
                && timeProvider.GetUtcNow() - row.LastRunUtc > TimeSpan.FromSeconds(30));
            if (stillShowing)
            {
                await healthChecks.RequestImmediateRunAsync(HealthCheckCategories.Processor, cancellationToken);
            }
        }
        catch (Exception)
        {
            // The health is saved; the regular pass still re-reads the processor.
        }
    }

    public async Task<IReadOnlyList<ProcessorPathMappingItem>> ListProcessorPathMappingsAsync(
        string processorConnectionId,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var items = new List<ProcessorPathMappingItem>();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, processor_connection_id, deluno_path, processor_path, is_enabled, priority, created_utc, updated_utc
            FROM processor_path_mappings
            WHERE processor_connection_id = @processorConnectionId
            ORDER BY priority ASC, LENGTH(processor_path) DESC, processor_path ASC;
            """;
        AddParameter(command, "@processorConnectionId", processorConnectionId.Trim());

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new ProcessorPathMappingItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4) == 1,
                reader.GetInt32(5),
                ParseTimestamp(reader.GetString(6)),
                ParseTimestamp(reader.GetString(7))));
        }

        return items;
    }

    public async Task<ProcessorPathMappingItem?> CreateProcessorPathMappingAsync(
        string processorConnectionId,
        CreateProcessorPathMappingRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        var owner = await GetProcessorConnectionAsync(connection, processorConnectionId, cancellationToken);
        if (owner is null)
        {
            return null;
        }

        var delunoPath = NormalizePath(request.DelunoPath);
        var processorPath = NormalizePath(request.ProcessorPath);
        if (string.IsNullOrWhiteSpace(delunoPath) || string.IsNullOrWhiteSpace(processorPath))
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var item = new ProcessorPathMappingItem(
            Guid.CreateVersion7().ToString("N"),
            owner.Id,
            delunoPath,
            processorPath,
            request.IsEnabled,
            request.Priority is >= 1 ? request.Priority.Value : 10,
            now,
            now);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO processor_path_mappings (
                id, processor_connection_id, deluno_path, processor_path, is_enabled, priority, created_utc, updated_utc)
            VALUES (@id, @processorConnectionId, @delunoPath, @processorPath, @isEnabled, @priority, @createdUtc, @updatedUtc);
            """;
        AddParameter(command, "@id", item.Id);
        AddParameter(command, "@processorConnectionId", item.ProcessorConnectionId);
        AddParameter(command, "@delunoPath", item.DelunoPath);
        AddParameter(command, "@processorPath", item.ProcessorPath);
        AddParameter(command, "@isEnabled", item.IsEnabled ? 1 : 0);
        AddParameter(command, "@priority", item.Priority);
        AddParameter(command, "@createdUtc", item.CreatedUtc.ToString("O"));
        AddParameter(command, "@updatedUtc", item.UpdatedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return item;
    }

    public async Task<bool> DeleteProcessorPathMappingAsync(
        string processorConnectionId,
        string id,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM processor_path_mappings WHERE id = @id AND processor_connection_id = @processorConnectionId;";
        AddParameter(command, "@id", id.Trim());
        AddParameter(command, "@processorConnectionId", processorConnectionId.Trim());
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<ProcessorHandoffItem> EnsureProcessorHandoffAsync(
        CreateProcessorHandoffRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var sourcePath = request.SourcePath.Trim();
        var sourceKey = BuildProcessorSourceKey(sourcePath);

        var linked = await FindProcessorConnectionAsync(request.ProcessorConnectionId, request.ProcessorName, cancellationToken);
        var processorConnectionId = linked?.Id;
        var processorDisplayName = linked?.Name ?? NormalizeName(request.ProcessorName);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var created = false;
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText =
                """
                INSERT INTO processor_handoffs (
                    id, library_id, media_type, client_id, queue_item_id, release_name, source_path, source_key,
                    processor_name, processor_connection_id, status, output_path, import_job_id, failure_message, created_utc, updated_utc
                ) VALUES (
                    @id, @libraryId, @mediaType, @clientId, @queueItemId, @releaseName, @sourcePath, @sourceKey,
                    @processorName, @processorConnectionId, 'waiting', NULL, NULL, NULL, @createdUtc, @updatedUtc
                )
                ON CONFLICT(library_id, source_key) DO NOTHING;
                """;
            AddParameter(insert, "@id", Guid.CreateVersion7().ToString("N"));
            AddParameter(insert, "@libraryId", request.LibraryId.Trim());
            AddParameter(insert, "@mediaType", request.MediaType.Trim().ToLowerInvariant());
            AddParameter(insert, "@clientId", request.ClientId.Trim());
            AddParameter(insert, "@queueItemId", request.QueueItemId.Trim());
            AddParameter(insert, "@releaseName", request.ReleaseName.Trim());
            AddParameter(insert, "@sourcePath", sourcePath);
            AddParameter(insert, "@sourceKey", sourceKey);
            AddParameter(insert, "@processorName", processorDisplayName);
            AddParameter(insert, "@processorConnectionId", processorConnectionId);
            AddParameter(insert, "@createdUtc", now.ToString("O"));
            AddParameter(insert, "@updatedUtc", now.ToString("O"));
            created = await insert.ExecuteNonQueryAsync(cancellationToken) > 0;
        }

        // A hand-off that has finished describes a cycle that is over, so being
        // asked to ensure one again means a new cycle has started.
        //
        // The insert above is keyed on the source path and does nothing when a
        // row is already there, which is the right answer while that row is
        // still working — that is what stops one download being submitted to
        // the processor twice. It was the wrong answer once the row had
        // finished: downloading the same release to the same path a second time
        // returned the earlier row, still marked completed and still carrying
        // the previous output path and import job. WorkPlanner only acts on a
        // hand-off whose status is "waiting", so nothing was submitted and
        // nothing was imported, while the queue went on reporting that Deluno
        // was waiting for a cleaned output. It never came.
        //
        // Seen on the lab: a film removed and re-acquired downloaded, refined,
        // and then sat in the refined folder for ever.
        //
        // The new cycle also gets a new id. The id is what the processor keys
        // its record on, so resubmitting under the old one asked the processor
        // about a file it had already finished: Weir answered "completed"
        // from the earlier run within a minute, wrote nothing, and Deluno
        // waited for an output that was never coming (seen on the rig with a
        // re-grabbed film, #526). A person's Retry keeps the id on purpose;
        // that is the same cycle, tried again.
        // The id the row had before it may be replaced below. The processor holds that id and will report against it.
        string? previousId = null;
        string? previousStatus = null;
        string? previousRelease = null;
        using (var before = connection.CreateCommand())
        {
            before.CommandText = "SELECT id, status, release_name FROM processor_handoffs WHERE library_id = @libraryId AND source_key = @sourceKey;";
            AddParameter(before, "@libraryId", request.LibraryId.Trim());
            AddParameter(before, "@sourceKey", sourceKey);
            using var read = await before.ExecuteReaderAsync(cancellationToken);
            if (await read.ReadAsync(cancellationToken))
            {
                previousId = read.GetString(0);
                previousStatus = read.GetString(1);
                previousRelease = read.GetString(2);
            }
        }

        var restarted = false;
        using (var restart = connection.CreateCommand())
        {
            restart.CommandText =
                """
                UPDATE processor_handoffs
                SET id = @newId,
                    queue_item_id = @queueItemId,
                    media_type = @mediaType,
                    client_id = @clientId,
                    release_name = @releaseName,
                    processor_name = @processorName,
                    processor_connection_id = @processorConnectionId,
                    status = 'waiting',
                    output_path = NULL,
                    import_job_id = NULL,
                    failure_message = NULL,
                    created_utc = @createdUtc,
                    updated_utc = @updatedUtc,
                    processor_state = NULL,
                    processor_message = NULL,
                    processor_queue_position = NULL,
                    processor_scheduled_for_utc = NULL,
                    processor_last_changed_utc = NULL,
                    processor_checked_utc = NULL,
                    processor_unreachable_since_utc = NULL,
                    processor_answering_since_utc = NULL
                WHERE library_id = @libraryId
                  AND source_key = @sourceKey
                  -- Any other ending restarts only for a new download. The same
                  -- download still sitting in the client after the processor
                  -- gave up on it, or after its original was imported, is that
                  -- ending, not a new cycle: restarting it resubmitted the file
                  -- every pass once processors' final failure reports began to
                  -- land (#511). Retrying the same download is a person's
                  -- choice, from Queue.
                  --
                  -- A torrent's queue id is its content hash, so the same release
                  -- fetched again - a force re-download after the processor
                  -- rejected it, say - arrives with the old id. It is still a new
                  -- download when the client took it after the hand-off ended
                  -- (seen on the rig, #497: the re-fetched file sat in the client
                  -- and was never handed to Weir).
                  --
                  -- 'completed' used to restart whoever asked, on the grounds that
                  -- nobody asks about a finished download. A Usenet client's
                  -- history does: it lists the finished download as "completed" on
                  -- every pass, with nothing to say it was handed off already. On
                  -- the rig one film went to Weir once a minute, each round
                  -- processed again and failing to import over the copy already in
                  -- the library. A completed hand-off now restarts on the same
                  -- evidence as any other ending.
                  -- 'dismissed' is an ending too. Left out, a release whose failed
                  -- hand-off a person had dismissed could never be processed
                  -- again: fetched a second time, it found that row, was not
                  -- handed over, and sat in the client (seen on the rig).
                  AND (
                    ((status IN ('completed', 'failed', 'rejected', 'timed-out', 'stalled', 'lost', 'cancelled', 'released', 'dismissed', 'source-gone')
                    -- The processor said it finished this cycle, but nothing
                    -- closed it, because its output had already been imported
                    -- for an earlier download of the same release. That cycle
                    -- is over for a new download even though its status is not.
                    -- Left open, it claimed every later download into the
                    -- folder, and none reached the processor (#561).
                    OR processor_state IN (@finishedState, @passedThroughState))
                    AND (queue_item_id <> @queueItemId
                      OR (@downloadAddedUtc IS NOT NULL AND @downloadAddedUtc > updated_utc)))
                    -- A hand-off still open belongs to the acquisition that made
                    -- it (#1155). A download added after that hand-off was made
                    -- is a different acquisition -- the title was removed,
                    -- restored and added again, or the file replaced -- and the
                    -- old hand-off must not stand in for it: it sat as "waiting
                    -- for the processor" for hours and nothing was ever sent.
                    OR (status IN ('waiting', 'submitted', 'accepted', 'started')
                      AND @downloadAddedUtc IS NOT NULL AND @downloadAddedUtc > created_utc));
                """;
            AddParameter(restart, "@downloadAddedUtc", request.DownloadAddedUtc is { } added && added > DateTimeOffset.UnixEpoch ? added.ToUniversalTime().ToString("O") : null);
            AddParameter(restart, "@newId", Guid.CreateVersion7().ToString("N"));
            AddParameter(restart, "@finishedState", ProcessorHandoffStates.Completed);
            AddParameter(restart, "@passedThroughState", ProcessorHandoffStates.PassedThrough);
            AddParameter(restart, "@libraryId", request.LibraryId.Trim());
            AddParameter(restart, "@sourceKey", sourceKey);
            AddParameter(restart, "@queueItemId", request.QueueItemId.Trim());
            AddParameter(restart, "@mediaType", request.MediaType.Trim().ToLowerInvariant());
            AddParameter(restart, "@clientId", request.ClientId.Trim());
            AddParameter(restart, "@releaseName", request.ReleaseName.Trim());
            AddParameter(restart, "@processorName", processorDisplayName);
            AddParameter(restart, "@processorConnectionId", processorConnectionId);
            AddParameter(restart, "@createdUtc", now.ToString("O"));
            AddParameter(restart, "@updatedUtc", now.ToString("O"));
            restarted = await restart.ExecuteNonQueryAsync(cancellationToken) > 0;
            created |= restarted;
        }

        if (restarted && previousId is not null)
        {
            using var keep = connection.CreateCommand();
            keep.CommandText =
                """
                INSERT INTO processor_handoff_superseded (id, library_id, source_key, release_name, last_status, superseded_by_id, superseded_utc)
                SELECT @oldId, @libraryId, @sourceKey, @releaseName, @lastStatus, id, @nowUtc
                FROM processor_handoffs
                WHERE library_id = @libraryId AND source_key = @sourceKey AND id <> @oldId
                ON CONFLICT(id) DO NOTHING;
                """;
            AddParameter(keep, "@oldId", previousId);
            AddParameter(keep, "@libraryId", request.LibraryId.Trim());
            AddParameter(keep, "@sourceKey", sourceKey);
            AddParameter(keep, "@releaseName", previousRelease ?? request.ReleaseName.Trim());
            AddParameter(keep, "@lastStatus", previousStatus ?? string.Empty);
            AddParameter(keep, "@nowUtc", now.ToString("O"));
            await keep.ExecuteNonQueryAsync(cancellationToken);

            // An id replaced earlier now points at the newest one.
            using var repoint = connection.CreateCommand();
            repoint.CommandText =
                "UPDATE processor_handoff_superseded SET superseded_by_id = (SELECT id FROM processor_handoffs WHERE library_id = @libraryId AND source_key = @sourceKey) WHERE library_id = @libraryId AND source_key = @sourceKey;";
            AddParameter(repoint, "@libraryId", request.LibraryId.Trim());
            AddParameter(repoint, "@sourceKey", sourceKey);
            await repoint.ExecuteNonQueryAsync(cancellationToken);
        }

        var ensured = (await FindProcessorHandoffAsync(request.LibraryId, null, sourcePath, cancellationToken))
            ?? throw new InvalidOperationException("Processor hand-off could not be created or loaded.");
        if (created)
        {
            await AnnounceHandoffAsync(ensured, ensured.Status, null, cancellationToken);
        }

        return ensured;
    }

    public async Task<ProcessorHandoffItem?> FindProcessorHandoffAsync(
        string libraryId,
        string? handoffId,
        string? sourcePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(libraryId) || (string.IsNullOrWhiteSpace(handoffId) && string.IsNullOrWhiteSpace(sourcePath)))
        {
            return null;
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = !string.IsNullOrWhiteSpace(handoffId)
            ? HandoffSelect + " WHERE id = @id AND library_id = @libraryId LIMIT 1;"
            : HandoffSelect + " WHERE library_id = @libraryId AND source_key = @sourceKey LIMIT 1;";
        AddParameter(command, "@libraryId", libraryId.Trim());
        if (!string.IsNullOrWhiteSpace(handoffId)) AddParameter(command, "@id", handoffId.Trim());
        else AddParameter(command, "@sourceKey", BuildProcessorSourceKey(sourcePath!));
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProcessorHandoff(reader) : null;
    }

    public async Task<SupersededProcessorHandoff?> FindSupersededProcessorHandoffAsync(
        string libraryId,
        string handoffId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(libraryId) || string.IsNullOrWhiteSpace(handoffId))
        {
            return null;
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, library_id, release_name, last_status, superseded_by_id, superseded_utc, settled_utc FROM processor_handoff_superseded WHERE id = @id AND library_id = @libraryId LIMIT 1;";
        AddParameter(command, "@id", handoffId.Trim());
        AddParameter(command, "@libraryId", libraryId.Trim());
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new SupersededProcessorHandoff(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture),
                reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), System.Globalization.CultureInfo.InvariantCulture))
            : null;
    }

    public async Task<bool> SettleSupersededProcessorHandoffAsync(
        string handoffId,
        string reportStatus,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE processor_handoff_superseded SET settled_utc = @nowUtc, settled_report_status = @status WHERE id = @id AND settled_utc IS NULL;";
        AddParameter(command, "@id", handoffId.Trim());
        AddParameter(command, "@status", reportStatus);
        AddParameter(command, "@nowUtc", timeProvider.GetUtcNow().ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<ProcessorHandoffItem?> GetProcessorHandoffAsync(
        string id,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = HandoffSelect + " WHERE id = @id LIMIT 1;";
        AddParameter(command, "@id", id.Trim());
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProcessorHandoff(reader) : null;
    }

    public async Task<ProcessorHandoffItem?> FindProcessorHandoffForDownloadAsync(
        string clientId,
        string queueItemId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(queueItemId))
        {
            return null;
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = HandoffSelect
            + " WHERE client_id = @clientId AND queue_item_id = @queueItemId ORDER BY updated_utc DESC LIMIT 1;";
        AddParameter(command, "@clientId", clientId.Trim());
        AddParameter(command, "@queueItemId", queueItemId.Trim());
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProcessorHandoff(reader) : null;
    }

    public async Task<IReadOnlyList<ProcessorHandoffItem>> ListProcessorHandoffsForReleaseAsync(
        string releaseName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(releaseName))
        {
            return [];
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = HandoffSelect + " WHERE release_name = @releaseName COLLATE NOCASE ORDER BY updated_utc DESC;";
        AddParameter(command, "@releaseName", releaseName.Trim());
        var items = new List<ProcessorHandoffItem>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(ReadProcessorHandoff(reader));
        return items;
    }

    public async Task<bool> RepointWaitingProcessorHandoffAsync(
        string id,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var path = sourcePath.Trim();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var update = connection.CreateCommand();
        update.CommandText =
            """
            UPDATE processor_handoffs
            SET source_path = @sourcePath,
                source_key = @sourceKey,
                updated_utc = @updatedUtc
            WHERE id = @id
              AND status = 'waiting'
              AND NOT EXISTS (
                  SELECT 1 FROM processor_handoffs other
                  WHERE other.library_id = processor_handoffs.library_id
                    AND other.source_key = @sourceKey
                    AND other.id <> processor_handoffs.id);
            """;
        AddParameter(update, "@id", id.Trim());
        AddParameter(update, "@sourcePath", path);
        AddParameter(update, "@sourceKey", BuildProcessorSourceKey(path));
        AddParameter(update, "@updatedUtc", timeProvider.GetUtcNow().ToString("O"));
        return await update.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<ProcessorHandoffItem?> UpdateProcessorHandoffAsync(
        string id,
        string status,
        string? outputPath,
        string? importJobId,
        string? failureMessage,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using (var update = connection.CreateCommand())
        {
            update.CommandText =
                """
                UPDATE processor_handoffs
                SET status = @status,
                    output_path = COALESCE(@outputPath, output_path),
                    import_job_id = COALESCE(@importJobId, import_job_id),
                    failure_message = @failureMessage,
                    updated_utc = @updatedUtc,
                    -- Back to waiting starts a new conversation with the
                    -- processor; the last one's answers do not carry over.
                    -- A processor-reported failure keeps saying so after a dismissal (#1221): the status
                    -- goes, the state stays, and it is what tells Deluno no file was handed back.
                    processor_state = CASE
                        WHEN @status = 'waiting' THEN NULL
                        WHEN @status = 'dismissed' AND status IN ('failed', 'rejected')
                            AND COALESCE(processor_state, '') NOT IN ('failed', 'rejected') THEN status
                        ELSE processor_state END,
                    processor_message = CASE WHEN @status = 'waiting' THEN NULL ELSE processor_message END,
                    processor_queue_position = CASE WHEN @status = 'waiting' THEN NULL ELSE processor_queue_position END,
                    processor_scheduled_for_utc = CASE WHEN @status = 'waiting' THEN NULL ELSE processor_scheduled_for_utc END,
                    processor_last_changed_utc = CASE WHEN @status = 'waiting' THEN NULL ELSE processor_last_changed_utc END,
                    processor_checked_utc = CASE
                        WHEN @status = 'waiting' THEN NULL
                        -- The observation is only read back when it has a check time, so the state above needs one.
                        WHEN @status = 'dismissed' AND status IN ('failed', 'rejected') THEN COALESCE(processor_checked_utc, @updatedUtc)
                        ELSE processor_checked_utc END,
                    processor_unreachable_since_utc = CASE WHEN @status = 'waiting' THEN NULL ELSE processor_unreachable_since_utc END,
                    processor_answering_since_utc = CASE WHEN @status = 'waiting' THEN NULL ELSE processor_answering_since_utc END
                WHERE id = @id;
                """;
            AddParameter(update, "@id", id.Trim());
            AddParameter(update, "@status", NormalizeProcessorHandoffStatus(status));
            AddParameter(update, "@outputPath", NormalizePath(outputPath));
            AddParameter(update, "@importJobId", NormalizeName(importJobId));
            AddParameter(update, "@failureMessage", NormalizeName(failureMessage));
            AddParameter(update, "@updatedUtc", timeProvider.GetUtcNow().ToString("O"));
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 0) return null;
        }

        await using var select = connection.CreateCommand();
        select.CommandText = HandoffSelect + " WHERE id = @id LIMIT 1;";
        AddParameter(select, "@id", id.Trim());
        using var reader = await select.ExecuteReaderAsync(cancellationToken);
        var updated = await reader.ReadAsync(cancellationToken) ? ReadProcessorHandoff(reader) : null;
        if (updated is not null)
        {
            await AnnounceHandoffAsync(updated, updated.Status, updated.FailureMessage, cancellationToken);
            await TellHandoffClosedAsync(updated, cancellationToken);
        }

        return updated;
    }

    public async Task TellHandoffClosedAsync(string handoffId, CancellationToken cancellationToken)
    {
        if (await GetProcessorHandoffAsync(handoffId, cancellationToken) is { } handoff)
        {
            await TellHandoffClosedAsync(handoff, cancellationToken);
        }
    }

    /// <summary>
    /// A dismissed hand-off is closed for good, so the import issue and System row that mirror it close with it (#1228),
    /// whoever dismissed it. The listeners own those records; one failing never undoes the dismissal.
    /// </summary>
    private async Task TellHandoffClosedAsync(ProcessorHandoffItem handoff, CancellationToken cancellationToken)
    {
        if (handoffClosedListeners is null
            || !string.Equals(handoff.Status, ProcessorHandoffStatuses.Dismissed, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var listener in handoffClosedListeners)
        {
            try
            {
                await listener.HandoffClosedAsync(handoff.Id, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The hand-off is saved; the planner's settler closes any issue this missed.
            }
        }
    }

    public async Task<IReadOnlyList<ProcessorHandoffItem>> ListProcessorHandoffsAsync(
        string? libraryId,
        int take,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = string.IsNullOrWhiteSpace(libraryId)
            ? HandoffSelect + " ORDER BY updated_utc DESC LIMIT @take;"
            : HandoffSelect + " WHERE library_id = @libraryId ORDER BY updated_utc DESC LIMIT @take;";
        AddParameter(command, "@take", Math.Clamp(take, 1, 200));
        if (!string.IsNullOrWhiteSpace(libraryId)) AddParameter(command, "@libraryId", libraryId.Trim());
        var items = new List<ProcessorHandoffItem>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(ReadProcessorHandoff(reader));
        return items;
    }

    public Task<IReadOnlyList<ProcessorHandoffItem>> ListOpenProcessorHandoffsAsync(CancellationToken cancellationToken)
        => ListProcessorHandoffsWithStatusAsync(ProcessorHandoffStatuses.WithProcessor, cancellationToken);

    public async Task<IReadOnlyList<ProcessorHandoffItem>> ListProcessorHandoffsWithStatusAsync(
        IReadOnlyList<string> statuses,
        CancellationToken cancellationToken)
    {
        if (statuses.Count == 0) return [];

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        var names = new List<string>(statuses.Count);
        for (var index = 0; index < statuses.Count; index++)
        {
            var name = $"@status{index}";
            AddParameter(command, name, statuses[index]);
            names.Add(name);
        }

        command.CommandText = HandoffSelect + $" WHERE status IN ({string.Join(", ", names)}) ORDER BY updated_utc DESC;";
        var items = new List<ProcessorHandoffItem>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(ReadProcessorHandoff(reader));
        return items;
    }

    /// <summary>One column list for every hand-off read, so a new column cannot reach some reads and miss others.</summary>
    private const string HandoffSelect =
        "SELECT id, library_id, media_type, client_id, queue_item_id, release_name, source_path, COALESCE((SELECT pc.name FROM processor_connections pc WHERE pc.id = processor_handoffs.processor_connection_id), processor_name), status, output_path, import_job_id, failure_message, created_utc, updated_utc, "
        + "processor_state, processor_message, processor_queue_position, processor_scheduled_for_utc, processor_last_changed_utc, processor_checked_utc, processor_unreachable_since_utc, processor_answering_since_utc, processor_connection_id "
        + "FROM processor_handoffs";

    private static ProcessorHandoffItem ReadProcessorHandoff(System.Data.Common.DbDataReader reader)
        => new(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.GetString(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11), ParseTimestamp(reader.GetString(12)), ParseTimestamp(reader.GetString(13)),
            reader.IsDBNull(19)
                ? null
                : new ProcessorHandoffObservation(
                    reader.IsDBNull(14) ? null : reader.GetString(14),
                    reader.IsDBNull(15) ? null : reader.GetString(15),
                    reader.IsDBNull(16) ? null : reader.GetInt32(16),
                    ReadOptionalTimestamp(reader, 17),
                    ReadOptionalTimestamp(reader, 18),
                    ParseTimestamp(reader.GetString(19)),
                    ReadOptionalTimestamp(reader, 20),
                    ReadOptionalTimestamp(reader, 21)),
            ProcessorConnectionId: reader.IsDBNull(22) ? null : reader.GetString(22));

    private static DateTimeOffset? ReadOptionalTimestamp(System.Data.Common.DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : ParseTimestamp(reader.GetString(ordinal));

    public async Task RecordProcessorObservationAsync(
        string id,
        ProcessorHandoffObservation observation,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        string? previousState = null;
        using (var before = connection.CreateCommand())
        {
            before.CommandText = "SELECT processor_state FROM processor_handoffs WHERE id = @id;";
            AddParameter(before, "@id", id.Trim());
            var current = await before.ExecuteScalarAsync(cancellationToken);
            previousState = current is string text ? text : null;
        }

        using var update = connection.CreateCommand();
        update.CommandText =
            """
            UPDATE processor_handoffs
            SET processor_state = @state,
                processor_message = @message,
                processor_queue_position = @queuePosition,
                processor_scheduled_for_utc = @scheduledFor,
                processor_last_changed_utc = @lastChanged,
                processor_checked_utc = @checked,
                processor_unreachable_since_utc = @unreachableSince,
                processor_answering_since_utc = @answeringSince
            WHERE id = @id;
            """;
        AddParameter(update, "@id", id.Trim());
        AddParameter(update, "@state", observation.State);
        AddParameter(update, "@message", observation.Message);
        AddParameter(update, "@queuePosition", observation.QueuePosition);
        AddParameter(update, "@scheduledFor", observation.ScheduledForUtc?.ToString("O"));
        AddParameter(update, "@lastChanged", observation.LastChangedUtc?.ToString("O"));
        AddParameter(update, "@checked", observation.CheckedUtc.ToString("O"));
        AddParameter(update, "@unreachableSince", observation.UnreachableSinceUtc?.ToString("O"));
        AddParameter(update, "@answeringSince", observation.AnsweringSinceUtc?.ToString("O"));
        await update.ExecuteNonQueryAsync(cancellationToken);

        // Said only when the processor reports a new state: every check writes an observation.
        if (observation.State is { Length: > 0 } state && !string.Equals(state, previousState, StringComparison.Ordinal))
        {
            var handoff = await GetProcessorHandoffAsync(id, cancellationToken);
            if (handoff is not null)
            {
                await AnnounceHandoffAsync(handoff, state, observation.Message, cancellationToken);
            }
        }
    }

    /// <summary>Tells the Live board the moment a hand-off to the processor moves on (#1097).</summary>
    private async Task AnnounceHandoffAsync(ProcessorHandoffItem handoff, string state, string? message, CancellationToken cancellationToken)
    {
        // The processor's tile on Live: what it has done today and how many titles it has right now (#1098).
        // Reported on every hand-off change, so a title finishing clears its glow as surely as a new one lights it.
        if (connectionUses is not null && !string.IsNullOrWhiteSpace(handoff.ProcessorConnectionId))
        {
            await connectionUses.ReportAsync(
                ConnectionKinds.Processor,
                handoff.ProcessorConnectionId,
                string.IsNullOrWhiteSpace(handoff.ProcessorName) ? "Processor" : handoff.ProcessorName,
                cancellationToken);
        }

        if (realtimeEventPublisher is null)
        {
            return;
        }

        try
        {
            await realtimeEventPublisher.PublishProcessorHandoffChangedAsync(
                new ProcessorHandoffChangedEvent(
                    handoff.Id,
                    handoff.QueueItemId,
                    handoff.LibraryId,
                    handoff.ReleaseName,
                    state,
                    handoff.ProcessorName,
                    message,
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

    public async Task<ProcessorOutcomeRecord?> GetProcessorOutcomeAsync(string handoffId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(handoffId))
        {
            return null;
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = OutcomeSelect + " WHERE handoff_id = @handoffId LIMIT 1;";
        AddParameter(command, "@handoffId", handoffId.Trim());
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProcessorOutcome(reader) : null;
    }

    public async Task SaveProcessorOutcomeAsync(ProcessorOutcomeRecord record, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO processor_handoff_outcomes (
                handoff_id, processor_name, processor_connection_id, library_id, release_name, outcome, imported_path, reason, occurred_utc,
                delivery, attempts, last_attempt_utc, next_attempt_utc, released, processor_said, recorded_utc
            ) VALUES (
                @handoffId, @processorName, @processorConnectionId, @libraryId, @releaseName, @outcome, @importedPath, @reason, @occurredUtc,
                @delivery, @attempts, @lastAttemptUtc, @nextAttemptUtc, @released, @processorSaid, @recordedUtc
            )
            ON CONFLICT(handoff_id) DO UPDATE SET
                processor_name = excluded.processor_name,
                processor_connection_id = excluded.processor_connection_id,
                library_id = excluded.library_id,
                release_name = excluded.release_name,
                outcome = excluded.outcome,
                imported_path = excluded.imported_path,
                reason = excluded.reason,
                occurred_utc = excluded.occurred_utc,
                delivery = excluded.delivery,
                attempts = excluded.attempts,
                last_attempt_utc = excluded.last_attempt_utc,
                next_attempt_utc = excluded.next_attempt_utc,
                released = excluded.released,
                processor_said = excluded.processor_said,
                recorded_utc = excluded.recorded_utc;
            """;
        AddParameter(command, "@handoffId", record.HandoffId.Trim());
        AddParameter(command, "@processorName", record.ProcessorName);
        AddParameter(command, "@processorConnectionId", record.ProcessorConnectionId);
        AddParameter(command, "@libraryId", record.LibraryId);
        AddParameter(command, "@releaseName", record.ReleaseName);
        AddParameter(command, "@outcome", record.Outcome);
        AddParameter(command, "@importedPath", record.ImportedPath);
        AddParameter(command, "@reason", record.Reason);
        AddParameter(command, "@occurredUtc", record.OccurredUtc.ToString("O"));
        AddParameter(command, "@delivery", record.Delivery);
        AddParameter(command, "@attempts", record.Attempts);
        AddParameter(command, "@lastAttemptUtc", record.LastAttemptUtc?.ToString("O"));
        AddParameter(command, "@nextAttemptUtc", record.NextAttemptUtc?.ToString("O"));
        AddParameter(command, "@released", record.Released ? 1 : 0);
        AddParameter(command, "@processorSaid", record.ProcessorSaid);
        AddParameter(command, "@recordedUtc", record.RecordedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ProcessorOutcomeRecord>> ListDueProcessorOutcomesAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        // Timestamps are written as round-trip UTC strings, so they order as text.
        command.CommandText = OutcomeSelect
            + " WHERE delivery = @pending AND (next_attempt_utc IS NULL OR next_attempt_utc <= @now) ORDER BY next_attempt_utc;";
        AddParameter(command, "@pending", ProcessorOutcomeDeliveries.Pending);
        AddParameter(command, "@now", nowUtc.ToUniversalTime().ToString("O"));
        var items = new List<ProcessorOutcomeRecord>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(ReadProcessorOutcome(reader));
        return items;
    }

    public async Task<IReadOnlyList<ProcessorOutcomeRecord>> ListProcessorOutcomesAsync(
        IReadOnlyCollection<string> handoffIds,
        CancellationToken cancellationToken)
    {
        if (handoffIds.Count == 0) return [];

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        var names = new List<string>(handoffIds.Count);
        foreach (var id in handoffIds)
        {
            var name = $"@id{names.Count}";
            AddParameter(command, name, id.Trim());
            names.Add(name);
        }

        command.CommandText = OutcomeSelect + $" WHERE handoff_id IN ({string.Join(", ", names)});";
        var items = new List<ProcessorOutcomeRecord>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(ReadProcessorOutcome(reader));
        return items;
    }

    private const string OutcomeSelect =
        "SELECT handoff_id, COALESCE((SELECT pc.name FROM processor_connections pc WHERE pc.id = processor_handoff_outcomes.processor_connection_id), processor_name), library_id, release_name, outcome, imported_path, reason, occurred_utc, "
        + "delivery, attempts, last_attempt_utc, next_attempt_utc, released, processor_said, recorded_utc, processor_connection_id "
        + "FROM processor_handoff_outcomes";

    private static ProcessorOutcomeRecord ReadProcessorOutcome(System.Data.Common.DbDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            ParseTimestamp(reader.GetString(7)),
            reader.GetString(8),
            reader.GetInt32(9),
            ReadOptionalTimestamp(reader, 10),
            ReadOptionalTimestamp(reader, 11),
            reader.GetInt64(12) == 1,
            reader.IsDBNull(13) ? null : reader.GetString(13),
            ParseTimestamp(reader.GetString(14)),
            reader.IsDBNull(15) ? null : reader.GetString(15));

    private ProcessorConnectionItem ReadProcessorConnection(System.Data.Common.DbDataReader reader)
    {
        var id = reader.GetString(0);
        var secret = reader.IsDBNull(5) ? SecretReadResult.None : secretProtector.TryUnprotect($"processor-connection:{id}", reader.GetString(5));
        var problem = secret.Unreadable ? SecretUnreadable.Message("secret") : null;
        var enabled = reader.GetInt64(6) == 1;
        return new ProcessorConnectionItem(
            id,
            reader.GetString(1),
            NormalizeProcessorConnectionProvider(reader.GetString(2)),
            reader.GetString(3),
            NormalizeProcessorAuthHeaderName(reader.GetString(4)),
            secret.Value,
            enabled,
            problem is not null && enabled ? "degraded" : NormalizeProcessorConnectionHealth(reader.GetString(7)),
            problem ?? (reader.IsDBNull(8) ? null : reader.GetString(8)),
            reader.IsDBNull(9) ? null : ParseTimestamp(reader.GetString(9)),
            ParseTimestamp(reader.GetString(10)),
            ParseTimestamp(reader.GetString(11)),
            reader.IsDBNull(12) ? null : reader.GetString(12))
        {
            SecretUnreadable = problem is not null,
            SecretProblem = problem
        };
    }

    private async Task<ProcessorConnectionItem?> GetProcessorConnectionAsync(
        System.Data.Common.DbConnection connection,
        string id,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, name, provider, submission_url, auth_header_name, secret_value, is_enabled, health_status, last_health_message, last_health_test_utc, created_utc, updated_utc, health_check_url FROM processor_connections WHERE id = @id LIMIT 1;";
        AddParameter(command, "@id", id.Trim());
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProcessorConnection(reader) : null;
    }

    private static string BuildProcessorSourceKey(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim().ToLowerInvariant())));

    private static string NormalizeProcessorHandoffStatus(string? status)
        => status is not null && ProcessorHandoffStatuses.All.Contains(status.Trim())
            ? status.Trim().ToLowerInvariant()
            : ProcessorHandoffStatuses.Waiting;

    private static string NormalizeProcessorConnectionProvider(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "fileflows" or "fileflows-webhook" => "fileflows-webhook",
            _ => "generic-webhook"
        };

    private static string NormalizeProcessorAuthHeaderName(string? value)
        => string.IsNullOrWhiteSpace(value) ? "Authorization" : value.Trim();

    private static string? NormalizeProcessorConnectionUrl(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        return uri.ToString().TrimEnd('/');
    }

    private static string NormalizeProcessorConnectionHealth(string? status)
        => status?.Trim().ToLowerInvariant() switch
        {
            "healthy" or "degraded" or "unreachable" => status.Trim().ToLowerInvariant(),
            _ => "unknown"
        };
}
