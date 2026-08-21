using System.Text.Json;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search;
using Deluno.Jobs.Data;
using Deluno.Jobs.Decisions;
using Deluno.Libraries.Data;
using Deluno.Platform.Data;
using Deluno.Quality.Data;

namespace Deluno.Media;

public sealed record MediaSearchResult(
    bool NotFound,
    string Outcome,
    string Summary,
    string Reason,
    string? ReleaseName,
    string? IndexerName,
    string? DispatchStatus,
    string? DispatchMessage,
    IReadOnlyList<MediaSearchCandidate> Candidates)
{
    public static MediaSearchResult NotFoundResult()
        => new(true, string.Empty, string.Empty, string.Empty, null, null, null, null, []);
}

/// <summary>
/// Shared workflow for manually searching one movie or series and optionally
/// dispatching the planner's selected result. Series-specific episode and
/// season searches remain in Deluno.Series.
/// </summary>
public static class MediaSearchHandler
{
    public static async Task<MediaSearchResult> ExecuteAsync(
        MediaKind kind,
        string id,
        string? mode,
        IMediaStateRepository mediaStateRepository,
        ILibrariesRepository librariesRepository,
        IQualityRepository qualityRepository,
        IJobQueueRepository jobQueueRepository,
        IAcquisitionDecisionPipeline acquisitionPipeline,
        IDownloadClientGrabService downloadClientGrabService,
        IActivityFeedRepository activityFeedRepository,
        TimeProvider timeProvider,
        RecordMediaSearchAttemptAsync recordSearchAttemptAsync,
        CancellationToken cancellationToken)
    {
        var item = await mediaStateRepository.GetByIdAsync(kind, id, cancellationToken);
        if (item is null)
        {
            return MediaSearchResult.NotFoundResult();
        }

        var entityType = kind == MediaKind.Movie ? "movie" : "series";
        var mediaType = kind == MediaKind.Movie ? "movies" : "tv";
        var wanted = await mediaStateRepository.GetWantedSummaryAsync(kind, cancellationToken);
        var wantedItem = wanted.RecentItems.FirstOrDefault(candidate => candidate.Id == id);
        if (wantedItem is null || string.IsNullOrWhiteSpace(wantedItem.LibraryId))
        {
            return BlockedResult(
                $"This {entityType} is not currently linked to a searchable library.",
                MediaSearchReasons.NotSearchable);
        }

        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
        var library = libraries.FirstOrDefault(candidate => candidate.Id == wantedItem.LibraryId);
        if (library is null)
        {
            return BlockedResult(
                $"Deluno could not find the linked library for this {entityType}.",
                MediaSearchReasons.LibraryMissing);
        }

        var routing = await librariesRepository.GetLibraryRoutingAsync(library.Id, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var customFormats = await ResolveCustomFormatsAsync(
            qualityRepository,
            library.QualityProfileId,
            cancellationToken);
        var decisionPlan = await acquisitionPipeline.PlanAsync(
            new AcquisitionDecisionRequest(
                item.Title,
                item.Year,
                mediaType,
                wantedItem.CurrentQuality,
                wantedItem.TargetQuality,
                routing?.Sources ?? [],
                routing?.DownloadClients ?? [],
                customFormats,
                PreviewOnly: string.Equals(mode, "preview", StringComparison.OrdinalIgnoreCase)),
            cancellationToken);
        var searchPlan = decisionPlan.SearchPlan;
        var bestCandidate = searchPlan.BestCandidate;
        var outcome = decisionPlan.Outcome;
        DownloadClientGrabResult? grabResult = null;

        if (decisionPlan.ShouldDispatch
            && decisionPlan.SelectedDownloadClient is not null
            && decisionPlan.DispatchRequest is not null)
        {
            var downloadClient = decisionPlan.SelectedDownloadClient;
            grabResult = bestCandidate!.DownloadUrl is null
                ? new DownloadClientGrabResult(
                    downloadClient.DownloadClientId,
                    bestCandidate.ReleaseName,
                    false,
                    "planned",
                    "No download URL was available.")
                : await downloadClientGrabService.GrabAsync(
                    downloadClient.DownloadClientId,
                    decisionPlan.DispatchRequest,
                    cancellationToken);
            await jobQueueRepository.RecordDownloadDispatchAsync(
                library.Id,
                mediaType,
                entityType,
                item.Id,
                bestCandidate.ReleaseName,
                bestCandidate.IndexerName,
                downloadClient.DownloadClientId,
                downloadClient.DownloadClientName,
                grabResult.Status,
                JsonSerializer.Serialize(new { searchPlan, grabResult }),
                grabResponseCode: grabResult.Succeeded ? 200 : 400,
                grabFailureCode: null,
                cancellationToken: cancellationToken);
        }

        await recordSearchAttemptAsync(
            item.Id,
            library.Id,
            "manual",
            outcome,
            now,
            now.AddHours(Math.Max(1, library.RetryDelayHours)),
            decisionPlan.SearchResult,
            bestCandidate?.ReleaseName,
            bestCandidate?.IndexerName,
            searchPlan.Candidates.Count == 0 ? null : JsonSerializer.Serialize(searchPlan),
            cancellationToken);

        await activityFeedRepository.RecordDecisionAsync(
            new DecisionExplanationPayload(
                Scope: $"{entityType}.search",
                Status: outcome,
                Reason: decisionPlan.SearchResult,
                Inputs: new Dictionary<string, string?>
                {
                    ["title"] = item.Title,
                    ["year"] = item.Year?.ToString(),
                    ["libraryId"] = library.Id,
                    ["sourceCount"] = decisionPlan.SourceCount.ToString(),
                    ["downloadClientCount"] = decisionPlan.DownloadClientCount.ToString(),
                    ["policyVersion"] = decisionPlan.PolicyVersion,
                    ["mode"] = string.Equals(mode, "preview", StringComparison.OrdinalIgnoreCase) ? "preview" : "manual"
                },
                Outcome: grabResult is null
                    ? searchPlan.Summary
                    : $"{grabResult.Status}: {grabResult.Message}",
                Alternatives: decisionPlan.Alternatives),
            null,
            entityType,
            item.Id,
            cancellationToken);

        await activityFeedRepository.RecordActivityAsync(
            $"{entityType}.search.manual",
            $"{item.Title} was searched manually from the Deluno workspace.",
            null,
            null,
            entityType,
            item.Id,
            cancellationToken);

        return new MediaSearchResult(
            NotFound: false,
            Outcome: outcome,
            Summary: searchPlan.Summary,
            Reason: searchPlan.Reason,
            ReleaseName: bestCandidate?.ReleaseName,
            IndexerName: bestCandidate?.IndexerName,
            DispatchStatus: grabResult?.Status,
            DispatchMessage: grabResult?.Message,
            Candidates: searchPlan.Candidates);
    }

    private static MediaSearchResult BlockedResult(string summary, string reason)
        => new(
            NotFound: false,
            Outcome: "blocked",
            Summary: summary,
            Reason: reason,
            ReleaseName: null,
            IndexerName: null,
            DispatchStatus: null,
            DispatchMessage: null,
            Candidates: []);

    private static async Task<IReadOnlyList<Deluno.Quality.Contracts.CustomFormatItem>> ResolveCustomFormatsAsync(
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
}
