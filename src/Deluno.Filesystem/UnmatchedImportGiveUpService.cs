using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Movies.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Recovery.Services;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging;

namespace Deluno.Filesystem;

public interface IUnmatchedImportGiveUpService
{
    /// <summary>
    /// #888: when the "give up after N days" policy is chosen, finds every
    /// unmatched import recovery case nobody has acted on past that limit and
    /// gives up on it - removes the download, deletes the file, blocklists
    /// the release, and searches again. A no-op under any other policy, so a
    /// setup gap or an outage that merely looks unmatched is never touched
    /// unless the user chose this specifically.
    /// </summary>
    Task<int> RunAsync(CancellationToken cancellationToken);
}

public sealed class UnmatchedImportGiveUpService(
    IPlatformSettingsRepository platformRepository,
    IMovieCatalogRepository movieCatalogRepository,
    ISeriesCatalogRepository seriesCatalogRepository,
    IDownloadDispatchesRepository? downloadDispatchesRepository,
    IBlockedReleaseRepository? blockedReleases,
    IDownloadClientActionGateway? downloadClientActions,
    IActivityFeedRepository activityFeedRepository,
    TimeProvider timeProvider,
    ILogger<UnmatchedImportGiveUpService> logger)
    : IUnmatchedImportGiveUpService
{
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var settings = await platformRepository.GetAsync(cancellationToken);
        if (settings.UnmatchedImportPolicy != UnmatchedImportPolicies.GiveUpAfterDays)
        {
            return 0;
        }

        var cutoff = timeProvider.GetUtcNow() - TimeSpan.FromDays(Math.Max(1, settings.UnmatchedImportGiveUpDays));
        var handled = 0;

        var movies = await movieCatalogRepository.GetImportRecoverySummaryAsync(cancellationToken);
        foreach (var recoveryCase in movies.RecentCases.Where(item => IsOverdueUnmatchedCase(item.FailureKind, item.DetectedUtc, cutoff)))
        {
            if (await GiveUpAsync(recoveryCase.Id, recoveryCase.Title, recoveryCase.DetailsJson, "movie", cancellationToken))
            {
                await movieCatalogRepository.ResolveImportRecoveryCaseAsync(recoveryCase.Id, "resolved", cancellationToken);
                handled++;
            }
        }

        var series = await seriesCatalogRepository.GetImportRecoverySummaryAsync(cancellationToken);
        foreach (var recoveryCase in series.RecentCases.Where(item => IsOverdueUnmatchedCase(item.FailureKind, item.DetectedUtc, cutoff)))
        {
            if (await GiveUpAsync(recoveryCase.Id, recoveryCase.Title, recoveryCase.DetailsJson, "series", cancellationToken))
            {
                await seriesCatalogRepository.ResolveImportRecoveryCaseAsync(recoveryCase.Id, "resolved", cancellationToken);
                handled++;
            }
        }

        return handled;
    }

    private static bool IsOverdueUnmatchedCase(string failureKind, DateTimeOffset detectedUtc, DateTimeOffset cutoff)
        => failureKind == ImportFailurePolicy.Unmatched && detectedUtc <= cutoff;

    private async Task<bool> GiveUpAsync(string caseId, string title, string? detailsJson, string entityKind, CancellationToken cancellationToken)
    {
        var (dispatchId, sourcePath) = ParseDetails(detailsJson);
        var deletedFile = false;
        if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            try
            {
                if (File.Exists(sourcePath))
                {
                    File.Delete(sourcePath);
                    deletedFile = true;
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not delete the unmatched file for {Title} while giving up on it.", title);
            }
        }

        var removedFromClient = false;
        var blocklisted = false;
        if (!string.IsNullOrWhiteSpace(dispatchId) && downloadDispatchesRepository is not null)
        {
            var dispatch = await downloadDispatchesRepository.GetDispatchAsync(dispatchId, cancellationToken);
            if (dispatch is not null)
            {
                if (downloadClientActions is not null &&
                    !string.IsNullOrWhiteSpace(dispatch.DownloadClientId) &&
                    !string.IsNullOrWhiteSpace(dispatch.TorrentHashOrItemId))
                {
                    var removal = await downloadClientActions.RemoveWithDataAsync(
                        dispatch.DownloadClientId,
                        dispatch.TorrentHashOrItemId,
                        cancellationToken);
                    removedFromClient = removal.Succeeded;
                }

                if (blockedReleases is not null && !string.IsNullOrWhiteSpace(dispatch.ReleaseName))
                {
                    await blockedReleases.BlockAsync(
                        new BlockedRelease(
                            Guid.NewGuid().ToString("n"),
                            BlockedReleaseKeys.For(dispatch.ReleaseName, dispatch.IndexerName),
                            dispatch.ReleaseName,
                            dispatch.IndexerName,
                            dispatch.MediaType,
                            dispatch.EntityId,
                            title,
                            ImportFailurePolicy.Unmatched,
                            $"Gave up waiting for you to match {title} by hand.",
                            dispatch.TorrentHashOrItemId,
                            dispatch.DownloadClientId,
                            dispatch.DownloadClientName,
                            DateTimeOffset.MinValue,
                            BlockedReleaseStates.Refused),
                        cancellationToken);
                    blocklisted = true;
                }

                // A fresh search only makes sense once Deluno knows which
                // title wanted this download in the first place - an
                // unmatched release never resolved that, so there is nothing
                // here to point a new search at. The title's own missing/
                // wanted search cycle already covers it, unaffected by this.
            }
        }

        var summary = string.Join(", ", new[]
        {
            deletedFile ? "deleted the file" : null,
            removedFromClient ? "removed it from the download client" : null,
            blocklisted ? "blocklisted the release" : null
        }.Where(part => part is not null));

        await activityFeedRepository.RecordActivityAsync(
            "filesystem.import.unmatched-given-up",
            summary.Length > 0
                ? $"Gave up waiting to match {title} by hand: {summary}. A new search will run on its usual schedule."
                : $"Gave up waiting to match {title} by hand, but nothing was left to clean up.",
            JsonSerializer.Serialize(new { CaseId = caseId, Title = title, DeletedFile = deletedFile, RemovedFromClient = removedFromClient, Blocklisted = blocklisted }),
            null,
            entityKind,
            null,
            cancellationToken);

        return true;
    }

    private static (string? DispatchId, string? SourcePath) ParseDetails(string? detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(detailsJson);
            var dispatchId = document.RootElement.TryGetProperty("DispatchId", out var dispatchElement) && dispatchElement.ValueKind == JsonValueKind.String
                ? dispatchElement.GetString()
                : null;
            var sourcePath = document.RootElement.TryGetProperty("SourcePath", out var sourceElement) && sourceElement.ValueKind == JsonValueKind.String
                ? sourceElement.GetString()
                : null;
            return (dispatchId, sourcePath);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
