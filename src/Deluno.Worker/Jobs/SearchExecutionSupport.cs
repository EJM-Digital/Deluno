using System.Text.Json;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search;
using Deluno.Quality.Contracts;
using Deluno.Quality.Data;

namespace Deluno.Worker.Jobs;

/// <summary>
/// Shared behaviour between <see cref="LibrarySearchJobHandler"/> and
/// <see cref="EpisodeSearchJobHandler"/> — grabbing a matched candidate,
/// serializing a search plan for storage, and resolving the custom formats
/// configured on a quality profile.
/// </summary>
internal static class SearchExecutionSupport
{
    internal static async Task<DownloadClientGrabResult> GrabBestCandidateAsync(
        IDownloadClientGrabService downloadClientGrabService,
        string downloadClientId,
        MediaSearchCandidate candidate,
        DownloadClientGrabRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(candidate.DownloadUrl))
        {
            return new DownloadClientGrabResult(
                downloadClientId,
                candidate.ReleaseName,
                false,
                "planned",
                "No download URL was available.");
        }

        return await downloadClientGrabService.GrabAsync(
            downloadClientId,
            request,
            cancellationToken);
    }

    internal static string? SerializeSearchPlan(MediaSearchPlan plan, DownloadClientGrabResult? grabResult = null)
    {
        if (plan.Candidates.Count == 0)
        {
            return null;
        }

        return grabResult is null
            ? JsonSerializer.Serialize(plan, JobPayloads.Options)
            : JsonSerializer.Serialize(new { searchPlan = plan, grabResult }, JobPayloads.Options);
    }

    internal static string SerializeCycleNotes(
        int configuredSources,
        int configuredClients,
        int checkedCount,
        int matchedCount,
        int blockedCount,
        int heldCount,
        int retryDelayedCount,
        int maxItems,
        int apiCallCount,
        long queuedReleaseBytes)
    {
        return JsonSerializer.Serialize(new
        {
            configuredSources,
            configuredClients,
            checkedCount,
            matchedCount,
            blockedCount,
            heldCount,
            retryDelayedCount,
            maxItems,
            apiCallCount,
            queuedReleaseBytes
        }, JobPayloads.Options);
    }

    internal static string NormalizeActionKind(string? wantedStatus)
        => string.Equals(wantedStatus, "upgrade", StringComparison.OrdinalIgnoreCase)
            ? "upgrade"
            : "missing";

    internal static async Task<IReadOnlyList<CustomFormatItem>> ResolveCustomFormatsAsync(
        IQualityRepository repository,
        string? qualityProfileId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(qualityProfileId))
        {
            return [];
        }

        var profiles = await repository.ListQualityProfilesAsync(cancellationToken);
        var profile = profiles.FirstOrDefault(item => item.Id == qualityProfileId);
        if (profile is null || string.IsNullOrWhiteSpace(profile.CustomFormatIds))
        {
            return [];
        }

        var ids = profile.CustomFormatIds
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (ids.Length == 0)
        {
            return [];
        }

        var formats = await repository.ListCustomFormatsAsync(cancellationToken);
        return formats.Where(item => ids.Contains(item.Id, StringComparer.OrdinalIgnoreCase)).ToArray();
    }

    internal static string FormatExecutionMessage(
        string libraryName,
        int candidateCount,
        int sourceCount,
        int clientCount,
        string mediaLabel)
    {
        if (candidateCount == 0)
        {
            return $"Deluno checked {libraryName} and found nothing else to look for right now.";
        }

        if (sourceCount == 0)
        {
            return $"Deluno found {candidateCount} {mediaLabel}{(candidateCount == 1 ? "" : "s")} to search in {libraryName}, but this library does not have any indexers linked yet.";
        }

        if (clientCount == 0)
        {
            return $"Deluno found {candidateCount} {mediaLabel}{(candidateCount == 1 ? "" : "s")} to search in {libraryName}, but it still needs a download client for this library.";
        }

        return $"Deluno checked {candidateCount} {mediaLabel}{(candidateCount == 1 ? "" : "s")} in {libraryName} using {sourceCount} source{(sourceCount == 1 ? "" : "s")}.";
    }

    internal static string FormatCompletionMessage(
        string libraryName,
        int candidateCount,
        int sourceCount,
        int clientCount,
        string mediaLabel)
    {
        if (candidateCount == 0)
        {
            return $"Finished checking {libraryName}. Nothing else needs attention right now.";
        }

        if (sourceCount == 0)
        {
            return $"Finished checking {libraryName}. Deluno found {candidateCount} {mediaLabel}{(candidateCount == 1 ? "" : "s")} but this library still needs indexers.";
        }

        if (clientCount == 0)
        {
            return $"Finished checking {libraryName}. Deluno found {candidateCount} {mediaLabel}{(candidateCount == 1 ? "" : "s")} but this library still needs a download client.";
        }

        return $"Finished checking {libraryName}. Deluno reviewed {candidateCount} {mediaLabel}{(candidateCount == 1 ? "" : "s")} for new or better releases.";
    }
}
