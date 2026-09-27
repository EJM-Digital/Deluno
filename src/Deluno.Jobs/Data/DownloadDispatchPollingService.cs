using System.Data.Common;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Contracts;
using Microsoft.Data.Sqlite;

namespace Deluno.Jobs.Data;

public sealed class DownloadDispatchPollingService(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider,
    IDispatchRecoveryHandler recoveryHandler,
    IActivityFeedRepository activityFeedRepository,
    IDownloadDispatchesRepository downloadDispatchesRepository,
    IDispatchAlertRepository alertRepository,
    IRealtimeEventPublisher realtimeEventPublisher,
    IGrabAppearanceLookupService? appearanceLookup = null,
    IBlockedReleaseRepository? blockedReleaseRepository = null,
    IJobScheduler? jobScheduler = null)
    : IDownloadDispatchPollingService
{
    private static readonly TimeSpan GrabTimeout = TimeSpan.FromHours(2);
    private static readonly TimeSpan DetectionTimeout = TimeSpan.FromHours(4);
    private static readonly TimeSpan ImportTimeout = TimeSpan.FromHours(24);

    public async Task<DownloadDispatchPollingReport> PollAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var report = new DownloadDispatchPollingReport(
            UnresolvedDispatchesChecked: 0,
            GrabTimeoutsDetected: 0,
            DetectionTimeoutsDetected: 0,
            ImportTimeoutsDetected: 0,
            ImportFailuresDetected: 0,
            RecoveryCasesRecorded: 0,
            GrabAppearanceResolved: 0);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        var unresolvedDispatches = await QueryUnresolvedDispatchesAsync(connection, cancellationToken);
        report = report with { UnresolvedDispatchesChecked = unresolvedDispatches.Count };
        var recoveryActivities = new List<ActivityEventItem>();

        foreach (var dispatch in unresolvedDispatches)
        {
            if (dispatch.DetectedUtc is not null && dispatch.ImportStatus is null)
            {
                await realtimeEventPublisher.PublishDispatchDetectedAsync(
                    dispatch.Id,
                    dispatch.ReleaseName,
                    null,
                    null,
                    cancellationToken);
            }

            if (dispatch.ImportStatus == "imported" && dispatch.ImportDetectedUtc is not null)
            {
                await realtimeEventPublisher.PublishDispatchImportCompletedAsync(
                    dispatch.Id,
                    dispatch.ReleaseName,
                    true,
                    null,
                    null,
                    cancellationToken);
            }
            else if (dispatch.ImportStatus == "failed" && !string.IsNullOrWhiteSpace(dispatch.ImportFailureCode))
            {
                await realtimeEventPublisher.PublishDispatchImportCompletedAsync(
                    dispatch.Id,
                    dispatch.ReleaseName,
                    false,
                    null,
                    dispatch.ImportFailureMessage ?? dispatch.ImportFailureCode,
                    cancellationToken);
            }

            if (dispatch.GrabStatus == "succeeded" && dispatch.DetectedUtc is null)
            {
                var age = now - dispatch.GrabAttemptedUtc;
                if (age > GrabTimeout)
                {
                    // #911: the appearance timeout has expired. Looked into
                    // once, the first time it expires; a dispatch already
                    // ended by an earlier pass is not asked about again.
                    var alreadyRecorded = await alertRepository.HasUnresolvedAlertAsync(dispatch.Id, "grab-timeout", cancellationToken);
                    var lookup = alreadyRecorded ? null : await SafeLookUpAsync(dispatch, cancellationToken);

                    if (lookup is { Outcome: GrabAppearanceOutcome.FoundAsExpected or GrabAppearanceOutcome.FoundUnderAnotherCategory or GrabAppearanceOutcome.Duplicate })
                    {
                        await FollowExistingItemAsync(dispatch, lookup, recoveryActivities, cancellationToken);
                        report = report with { GrabAppearanceResolved = report.GrabAppearanceResolved + 1 };
                    }
                    else if (lookup is { Outcome: GrabAppearanceOutcome.Rejected })
                    {
                        await BlocklistAndSearchAgainAsync(dispatch, lookup, recoveryActivities, cancellationToken);
                        report = report with { GrabAppearanceResolved = report.GrabAppearanceResolved + 1 };
                    }
                    else
                    {
                        // Nothing found anywhere (or the client could not be
                        // reached, or nothing could look at all): whatever has
                        // been said about it already, the download itself ends
                        // here, so it stops reading "Sent" and a person can
                        // dismiss it. A late detection brings it back.
                        await downloadDispatchesRepository.MarkNeverDetectedAsync(
                            dispatch.Id,
                            $"{dispatch.DownloadClientName} accepted this download and then never showed it.",
                            cancellationToken);

                        if (!alreadyRecorded)
                        {
                            var unreachableDetail = lookup?.Outcome == GrabAppearanceOutcome.Unreachable
                                ? lookup.Detail ?? "unreachable"
                                : null;
                            await RecordGrabTimeoutRecoveryAsync(dispatch, recoveryActivities, cancellationToken, unreachableDetail);
                            report = report with { GrabTimeoutsDetected = report.GrabTimeoutsDetected + 1, RecoveryCasesRecorded = report.RecoveryCasesRecorded + 1 };
                        }
                    }
                }
            }
            else if (dispatch.DetectedUtc is not null && dispatch.ImportStatus is null)
            {
                // Measured from when the client finished, not from when it first
                // showed the download (#554): a download can be on screen and
                // progressing for many hours, and none of that is overdue.
                var age = dispatch.ClientCompletedUtc is { } completedUtc ? now - completedUtc : TimeSpan.Zero;
                if (age > DetectionTimeout && !await alertRepository.HasUnresolvedAlertAsync(dispatch.Id, "detection-timeout", cancellationToken))
                {
                    await RecordDetectionTimeoutRecoveryAsync(
                        dispatch,
                        recoveryHandler,
                        recoveryActivities,
                        alertRepository,
                        cancellationToken);
                    report = report with { DetectionTimeoutsDetected = report.DetectionTimeoutsDetected + 1, RecoveryCasesRecorded = report.RecoveryCasesRecorded + 1 };
                }
            }
            else if (dispatch.ImportStatus == "pending" && dispatch.ImportDetectedUtc is not null)
            {
                var age = now - dispatch.ImportDetectedUtc.Value;
                if (age > ImportTimeout && !await alertRepository.HasUnresolvedAlertAsync(dispatch.Id, "import-timeout", cancellationToken))
                {
                    await RecordImportTimeoutRecoveryAsync(
                        dispatch,
                        recoveryHandler,
                        recoveryActivities,
                        alertRepository,
                        cancellationToken);
                    report = report with { ImportTimeoutsDetected = report.ImportTimeoutsDetected + 1, RecoveryCasesRecorded = report.RecoveryCasesRecorded + 1 };
                }
            }
            else if (dispatch.ImportStatus == "failed"
                && !string.IsNullOrWhiteSpace(dispatch.ImportFailureCode)
                && !await alertRepository.HasUnresolvedAlertAsync(dispatch.Id, "import-failed", cancellationToken))
            {
                await RecordImportFailureRecoveryAsync(
                    dispatch,
                    recoveryHandler,
                    recoveryActivities,
                    alertRepository,
                    cancellationToken);
                report = report with { ImportFailuresDetected = report.ImportFailuresDetected + 1, RecoveryCasesRecorded = report.RecoveryCasesRecorded + 1 };
            }
        }

        if (recoveryActivities.Count > 0)
        {
            await activityFeedRepository.RecordActivityBatchAsync(recoveryActivities, cancellationToken);
        }

        await ArchiveSuccessfulDispatchesAsync(connection, cancellationToken);

        // After archiving, so a download archived on this pass closes its
        // alerts on this pass rather than an hour later.
        var resolved = await alertRepository.ResolveSettledAlertsAsync(cancellationToken);
        report = report with { AlertsResolved = resolved.Count };

        // The recovery case written beside each alert settles with it (#559),
        // or it goes on saying "never appeared" about a download the client
        // has since shown, and the screens go on counting it.
        foreach (var settled in resolved.GroupBy(alert => alert.DispatchId, StringComparer.Ordinal))
        {
            await recoveryHandler.HandleDispatchSettledAsync(settled.Key, settled.First().Reason, cancellationToken);
        }

        return report;
    }

    private static async Task<IReadOnlyList<UnresolvedDispatch>> QueryUnresolvedDispatchesAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        var items = new List<UnresolvedDispatch>();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, library_id, media_type, entity_type, entity_id, release_name,
                download_client_id, download_client_name, grab_status, grab_attempted_utc,
                detected_utc, import_status, import_detected_utc, import_failure_code, import_failure_message,
                client_completed_utc, indexer_name, torrent_hash_or_item_id
            FROM download_dispatches
            -- A download that was imported or archived is finished, whatever an
            -- earlier stage recorded. The import can complete through a
            -- processor callback or a manual import without the client-detection
            -- step ever stamping detected_utc, and this query used to read that
            -- as "grabbed but never seen": on the rig it raised a grab timeout
            -- about a film after importing it, three times, each with its own
            -- activity entry and a recovery case saying the download never
            -- reached the client.
            WHERE COALESCE(status, '') <> 'archived'
              AND COALESCE(import_status, '') <> 'imported'
              AND ((grab_status IS NULL OR grab_status = 'succeeded' AND detected_utc IS NULL)
                OR (detected_utc IS NOT NULL AND import_status IS NULL)
                OR (import_status IN ('pending', 'failed')))
            ORDER BY grab_attempted_utc DESC
            LIMIT 500;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new UnresolvedDispatch(
                Id: reader.GetString(0),
                LibraryId: reader.GetString(1),
                MediaType: reader.GetString(2),
                EntityType: reader.GetString(3),
                EntityId: reader.GetString(4),
                ReleaseName: reader.GetString(5),
                DownloadClientId: reader.GetString(6),
                DownloadClientName: reader.GetString(7),
                GrabStatus: reader.IsDBNull(8) ? null : reader.GetString(8),
                GrabAttemptedUtc: reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9)),
                DetectedUtc: reader.IsDBNull(10) ? null : DateTimeOffset.Parse(reader.GetString(10)),
                ImportStatus: reader.IsDBNull(11) ? null : reader.GetString(11),
                ImportDetectedUtc: reader.IsDBNull(12) ? null : DateTimeOffset.Parse(reader.GetString(12)),
                ImportFailureCode: reader.IsDBNull(13) ? null : reader.GetString(13),
                ImportFailureMessage: reader.IsDBNull(14) ? null : reader.GetString(14),
                ClientCompletedUtc: reader.IsDBNull(15) ? null : DateTimeOffset.Parse(reader.GetString(15)),
                IndexerName: reader.IsDBNull(16) ? "" : reader.GetString(16),
                TorrentHashOrItemId: reader.IsDBNull(17) ? null : reader.GetString(17)));
        }

        return items;
    }

    /// <summary>
    /// The #911 lookup itself, defended against a client adapter throwing
    /// something this poller did not expect — that is answered exactly like
    /// an unreachable client, not by breaking the whole polling pass over one
    /// download.
    /// </summary>
    private async Task<GrabAppearanceLookupResult?> SafeLookUpAsync(
        UnresolvedDispatch dispatch,
        CancellationToken cancellationToken)
    {
        if (appearanceLookup is null)
        {
            return null;
        }

        try
        {
            return await appearanceLookup.LookupAsync(
                dispatch.DownloadClientId,
                dispatch.MediaType,
                category: null,
                dispatch.TorrentHashOrItemId,
                dispatch.ReleaseName,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new GrabAppearanceLookupResult(GrabAppearanceOutcome.Unreachable, Detail: exception.Message);
        }
    }

    /// <summary>
    /// It was there all along — under the category Deluno expected, a
    /// category setting that is wrong, or as a duplicate the client's own
    /// check already declined. Either way, Deluno follows the copy the client
    /// holds rather than treating a live download as gone (#877, #886, #911).
    /// </summary>
    private async Task FollowExistingItemAsync(
        UnresolvedDispatch dispatch,
        GrabAppearanceLookupResult lookup,
        ICollection<ActivityEventItem> recoveryActivities,
        CancellationToken cancellationToken)
    {
        var followId = lookup.QueueItemId ?? dispatch.TorrentHashOrItemId;
        await downloadDispatchesRepository.RecordDetectionAsync(
            dispatch.Id, followId, downloadedBytes: null, completed: false, cancellationToken);

        var (category, message) = lookup.Outcome switch
        {
            GrabAppearanceOutcome.Duplicate => (
                "download.grab.duplicate",
                $"{dispatch.DownloadClientName} already held {dispatch.ReleaseName}. Deluno is following the copy it has."),
            GrabAppearanceOutcome.FoundUnderAnotherCategory => (
                "download.grab.foundElsewhere",
                $"{dispatch.ReleaseName} was under {lookup.FoundCategory} in {dispatch.DownloadClientName}, not {lookup.ExpectedCategory}. Deluno is following it there — the {(string.Equals(dispatch.MediaType, "tv", StringComparison.OrdinalIgnoreCase) ? "TV" : "movies")} category setting for {dispatch.DownloadClientName} may be wrong."),
            _ => (
                "download.grab.foundElsewhere",
                $"{dispatch.ReleaseName} was already in {dispatch.DownloadClientName}'s queue. Deluno is following it.")
        };

        recoveryActivities.Add(CreateRecoveryActivity(
            category,
            message,
            JsonSerializer.Serialize(new
            {
                DispatchId = dispatch.Id,
                dispatch.DownloadClientId,
                dispatch.DownloadClientName,
                dispatch.ReleaseName,
                foundCategory = lookup.FoundCategory,
                expectedCategory = lookup.ExpectedCategory
            }),
            "download",
            dispatch.Id));
    }

    /// <summary>
    /// The client itself said this release was no good. Blocklisted with the
    /// #874 key (hash first, name for every indexer, whichever the search
    /// finds a key for) and a fresh search started at once, the same way a
    /// processor's rejection already does (#911, mirroring
    /// <c>ProcessorFailureReportHandler</c>).
    /// </summary>
    private async Task BlocklistAndSearchAgainAsync(
        UnresolvedDispatch dispatch,
        GrabAppearanceLookupResult lookup,
        ICollection<ActivityEventItem> recoveryActivities,
        CancellationToken cancellationToken)
    {
        var reason = string.IsNullOrWhiteSpace(lookup.Detail)
            ? $"{dispatch.DownloadClientName} rejected this release."
            : $"{dispatch.DownloadClientName} rejected this release: {lookup.Detail}";

        if (blockedReleaseRepository is not null)
        {
            try
            {
                await blockedReleaseRepository.BlockAsync(
                    new BlockedRelease(
                        Guid.NewGuid().ToString("n"),
                        BlockedReleaseKeys.For(dispatch.ReleaseName, dispatch.IndexerName),
                        dispatch.ReleaseName,
                        dispatch.IndexerName,
                        dispatch.MediaType,
                        dispatch.EntityId,
                        dispatch.ReleaseName,
                        ImportFailurePolicy.ClientReportedFailure,
                        reason,
                        dispatch.TorrentHashOrItemId,
                        dispatch.DownloadClientId,
                        dispatch.DownloadClientName,
                        DateTimeOffset.MinValue),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Left unblocked; the next pass finds the same rejection and
                // tries again rather than this one going silent about it.
            }
        }

        await downloadDispatchesRepository.ArchiveDispatchAsync(dispatch.Id, reason, cancellationToken);

        if (jobScheduler is not null)
        {
            try
            {
                await jobScheduler.EnqueueAsync(
                    new EnqueueJobRequest(
                        LibrarySearchJobTypes.For(dispatch.MediaType),
                        "grab-rejected",
                        JsonSerializer.Serialize(new
                        {
                            libraryId = dispatch.LibraryId,
                            mediaType = dispatch.MediaType,
                            checkMissing = true,
                            checkUpgrades = true,
                            maxItems = 1,
                            retryDelayHours = 24,
                            triggeredBy = "grab-rejected",
                            targetEntityId = dispatch.EntityId
                        }),
                        "download_dispatch",
                        dispatch.Id,
                        IdempotencyKey: $"grab-rejected:{dispatch.Id}",
                        DedupeKey: $"grab-rejected:{dispatch.Id}"),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The blocklist entry alone still keeps this release from
                // being sent again; the title's own scheduled search picks it
                // back up regardless.
            }
        }

        recoveryActivities.Add(CreateRecoveryActivity(
            "download.grab.rejected",
            $"{dispatch.DownloadClientName} rejected {dispatch.ReleaseName}. Deluno blocklisted it and is searching for another copy.",
            JsonSerializer.Serialize(new
            {
                DispatchId = dispatch.Id,
                dispatch.DownloadClientId,
                dispatch.DownloadClientName,
                dispatch.ReleaseName,
                detail = lookup.Detail
            }),
            "download",
            dispatch.Id));
    }

    private async Task RecordGrabTimeoutRecoveryAsync(
        UnresolvedDispatch dispatch,
        ICollection<ActivityEventItem> recoveryActivities,
        CancellationToken cancellationToken,
        string? unreachableDetail)
    {
        var title = dispatch.ReleaseName;
        var detailsJson = JsonSerializer.Serialize(new
        {
            DispatchId = dispatch.Id,
            dispatch.DownloadClientId,
            dispatch.DownloadClientName,
            dispatch.ReleaseName,
            dispatch.MediaType,
            unreachable = unreachableDetail is not null
        });

        await recoveryHandler.HandleGrabTimeoutAsync(
            title,
            dispatch.MediaType,
            dispatch.DownloadClientId,
            dispatch.DownloadClientName,
            dispatch.ReleaseName,
            detailsJson,
            cancellationToken);

        var message = unreachableDetail is null
            ? $"{title} was not found anywhere in {dispatch.DownloadClientName}'s queue or history after {GrabTimeout.TotalHours:F0} hours. Deluno stopped waiting for it."
            : $"Deluno could not reach {dispatch.DownloadClientName} to check on {title} after {GrabTimeout.TotalHours:F0} hours. It will look again — this is a setup problem with {dispatch.DownloadClientName}, not the release.";

        recoveryActivities.Add(CreateRecoveryActivity(
            unreachableDetail is null ? "download.grab.timeout" : "download.grab.unreachable",
            message,
            detailsJson,
            "download",
            dispatch.Id));

        await alertRepository.CreateAlertAsync(
            dispatch.Id,
            title,
            unreachableDetail is null
                ? $"Download not detected in {dispatch.DownloadClientName}"
                : $"Deluno could not reach {dispatch.DownloadClientName}",
            "grab-timeout",
            "warning",
            new Dictionary<string, string> { { "client", dispatch.DownloadClientName } },
            cancellationToken);
    }

    private async Task RecordDetectionTimeoutRecoveryAsync(
        UnresolvedDispatch dispatch,
        IDispatchRecoveryHandler recoveryHandler,
        ICollection<ActivityEventItem> recoveryActivities,
        IDispatchAlertRepository alertRepository,
        CancellationToken cancellationToken)
    {
        var title = dispatch.ReleaseName;
        var detailsJson = JsonSerializer.Serialize(new
        {
            DispatchId = dispatch.Id,
            dispatch.DownloadClientId,
            dispatch.DownloadClientName,
            dispatch.ReleaseName,
            dispatch.MediaType,
            dispatch.EntityId
        });

        await recoveryHandler.HandleDetectionTimeoutAsync(
            title,
            dispatch.MediaType,
            dispatch.DownloadClientId,
            dispatch.DownloadClientName,
            dispatch.ReleaseName,
            dispatch.EntityId,
            detailsJson,
            cancellationToken);

        recoveryActivities.Add(CreateRecoveryActivity(
            "download.detection.timeout",
            $"{title} finished downloading but was not imported within {DetectionTimeout.TotalHours:F0} hours.",
            detailsJson,
            "download",
            dispatch.Id));

        await alertRepository.CreateAlertAsync(
            dispatch.Id,
            title,
            "Downloaded but not imported",
            "detection-timeout",
            "warning",
            new Dictionary<string, string> { { "mediaType", dispatch.MediaType } },
            cancellationToken);
    }

    private async Task RecordImportTimeoutRecoveryAsync(
        UnresolvedDispatch dispatch,
        IDispatchRecoveryHandler recoveryHandler,
        ICollection<ActivityEventItem> recoveryActivities,
        IDispatchAlertRepository alertRepository,
        CancellationToken cancellationToken)
    {
        var title = dispatch.ReleaseName;
        var detailsJson = JsonSerializer.Serialize(new
        {
            DispatchId = dispatch.Id,
            dispatch.DownloadClientId,
            dispatch.DownloadClientName,
            dispatch.ReleaseName,
            dispatch.MediaType,
            dispatch.EntityId
        });

        await recoveryHandler.HandleImportTimeoutAsync(
            title,
            dispatch.MediaType,
            dispatch.DownloadClientId,
            dispatch.DownloadClientName,
            dispatch.ReleaseName,
            dispatch.EntityId,
            detailsJson,
            cancellationToken);

        recoveryActivities.Add(CreateRecoveryActivity(
            "download.import.timeout",
            $"{title} import was detected but never completed after {ImportTimeout.TotalHours:F0} hours.",
            detailsJson,
            "download",
            dispatch.Id));

        await alertRepository.CreateAlertAsync(
            dispatch.Id,
            title,
            "Import process did not complete within timeout period",
            "import-timeout",
            "error",
            new Dictionary<string, string> { { "mediaType", dispatch.MediaType } },
            cancellationToken);
    }

    private async Task RecordImportFailureRecoveryAsync(
        UnresolvedDispatch dispatch,
        IDispatchRecoveryHandler recoveryHandler,
        ICollection<ActivityEventItem> recoveryActivities,
        IDispatchAlertRepository alertRepository,
        CancellationToken cancellationToken)
    {
        var title = dispatch.ReleaseName;
        var failureReason = !string.IsNullOrWhiteSpace(dispatch.ImportFailureMessage)
            ? dispatch.ImportFailureMessage
            : dispatch.ImportFailureCode ?? "unknown";
        var detailsJson = JsonSerializer.Serialize(new
        {
            DispatchId = dispatch.Id,
            dispatch.DownloadClientId,
            dispatch.DownloadClientName,
            dispatch.ReleaseName,
            dispatch.MediaType,
            dispatch.EntityId,
            dispatch.ImportFailureCode,
            dispatch.ImportFailureMessage
        });

        await recoveryHandler.HandleImportFailureAsync(
            title,
            dispatch.MediaType,
            dispatch.DownloadClientId,
            dispatch.DownloadClientName,
            dispatch.ReleaseName,
            dispatch.EntityId,
            dispatch.ImportFailureCode ?? "",
            dispatch.ImportFailureMessage ?? "",
            detailsJson,
            cancellationToken);

        recoveryActivities.Add(CreateRecoveryActivity(
            "download.import.failed",
            $"{title} import failed: {failureReason}",
            detailsJson,
            "download",
            dispatch.Id));

        await alertRepository.CreateAlertAsync(
            dispatch.Id,
            title,
            $"Import failed: {failureReason}",
            "import-failed",
            "error",
            new Dictionary<string, string>
            {
                { "failureCode", dispatch.ImportFailureCode ?? "" },
                { "failureMessage", dispatch.ImportFailureMessage ?? "" }
            },
            cancellationToken);
    }

    private ActivityEventItem CreateRecoveryActivity(
        string category,
        string message,
        string? detailsJson,
        string relatedEntityType,
        string relatedEntityId)
    {
        return new ActivityEventItem(
            Id: Guid.CreateVersion7().ToString("N"),
            Category: category,
            Message: message,
            DetailsJson: detailsJson,
            RelatedJobId: null,
            RelatedEntityType: relatedEntityType,
            RelatedEntityId: relatedEntityId,
            CreatedUtc: timeProvider.GetUtcNow());
    }

    private async Task ArchiveSuccessfulDispatchesAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id FROM download_dispatches
            WHERE import_status = 'imported' AND archived_utc IS NULL
            ORDER BY import_detected_utc DESC
            LIMIT 100;
            """;

        var dispatchIds = new List<string>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            dispatchIds.Add(reader.GetString(0));
        }

        foreach (var dispatchId in dispatchIds)
        {
            await downloadDispatchesRepository.ArchiveDispatchAsync(
                dispatchId,
                "import_completed",
                cancellationToken);
        }
    }

    private sealed record UnresolvedDispatch(
        string Id,
        string LibraryId,
        string MediaType,
        string EntityType,
        string EntityId,
        string ReleaseName,
        string DownloadClientId,
        string DownloadClientName,
        string? GrabStatus,
        DateTimeOffset? GrabAttemptedUtc,
        DateTimeOffset? DetectedUtc,
        string? ImportStatus,
        DateTimeOffset? ImportDetectedUtc,
        string? ImportFailureCode,
        string? ImportFailureMessage,
        DateTimeOffset? ClientCompletedUtc,
        string IndexerName = "",
        string? TorrentHashOrItemId = null);
}
